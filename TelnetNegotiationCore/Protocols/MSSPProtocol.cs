using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TelnetNegotiationCore.Attributes;
using TelnetNegotiationCore.Helpers;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Plugins;
using TelnetNegotiationCore.Generated;

namespace TelnetNegotiationCore.Protocols;

/// <summary>
/// MSSP (Mud Server Status Protocol) plugin
/// Provides server information to clients
/// </summary>
/// <remarks>
/// This protocol requires configuration before use. Call <see cref="OnMSSP"/> to set up
/// the callback that will handle MSSP requests and provide server information. Without
/// this configuration, the protocol will not be able to respond to client MSSP queries.
/// </remarks>
/// <remarks>
/// This covers telnet option 70. The plaintext <c>MSSP-REQUEST</c> exchange is a separate transport
/// for the same report, in the separate <see cref="MSSPPlaintextProtocol"/> plugin; adding that
/// plugin is what opts into it, and it borrows this one's callback, configuration and ceiling.
/// </remarks>
[RequiredMethod("OnMSSP", Description = "Configure the callback to handle MSSP requests and provide server information")]
public class MSSPProtocol : TelnetProtocolPluginBase
{
    private static readonly byte[] s_willMssp = new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.MSSP };
    private static readonly byte[] s_doMssp = new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MSSP };

    private Func<MSSPConfig, ValueTask>? _onMSSPRequest;

    private Func<(long ReceivedBytes, int MaxMessageSize), ValueTask>? _onMSSPMessageTooLarge;

    /// <summary>
    /// Sets the callback that is invoked when an MSSP request is received.
    /// </summary>
    /// <param name="callback">The callback to handle MSSP requests</param>
    /// <returns>This instance for fluent chaining</returns>
    public MSSPProtocol OnMSSP(Func<MSSPConfig, ValueTask>? callback)
    {
        _onMSSPRequest = callback;
        return this;
    }

    /// <summary>
    /// The largest MSSP report this connection will accept, in bytes, counted over the whole
    /// subnegotiation payload -- every variable name and every value together, markers excluded.
    /// Defaults to 1 MiB.
    /// </summary>
    /// <remarks>
    /// The MSSP specification defines no maximum report size, so this is a defence against a hostile
    /// or broken peer rather than a protocol limit. It matters most for a crawler, which connects to
    /// servers it does not trust by definition and would otherwise let each one decide how much the
    /// client allocates. A report larger than this is <em>dropped</em>, never truncated: it is logged
    /// at Error level and reported to the <see cref="OnMSSPMessageTooLarge"/> callback, because a
    /// report missing an unknown number of its variables is not a smaller report, it is a wrong one.
    /// <para>
    /// One ceiling covers both transports: <see cref="MSSPPlaintextProtocol"/> bounds a plaintext
    /// reply against this same value, because they carry the same report.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
    public int MaxMessageSize
    {
        get => _msspBytes.MaxMessageSize;
        set => _msspBytes.MaxMessageSize = value;
    }

    /// <summary>
    /// Sets the maximum MSSP report size in a fluent manner.
    /// </summary>
    /// <param name="maxMessageSize">The maximum payload size in bytes</param>
    /// <returns>This instance for fluent chaining</returns>
    public MSSPProtocol WithMaxMessageSize(int maxMessageSize)
    {
        MaxMessageSize = maxMessageSize;
        return this;
    }

    /// <summary>
    /// Sets the callback that is invoked when an MSSP report is dropped for exceeding
    /// <see cref="MaxMessageSize"/>. This is the observable counterpart of the Error level log: it
    /// lets a consumer distinguish "the server reported nothing" from "the server reported too much".
    /// </summary>
    /// <param name="callback">The callback to handle oversized MSSP reports. Receives the number of
    /// payload bytes the peer sent and the configured limit.</param>
    /// <returns>This instance for fluent chaining</returns>
    public MSSPProtocol OnMSSPMessageTooLarge(Func<(long ReceivedBytes, int MaxMessageSize), ValueTask>? callback)
    {
        _onMSSPMessageTooLarge = callback;
        return this;
    }

    private Func<MSSPConfig> _msspConfig = () => new MSSPConfig();

    /// <summary>
    /// The whole subnegotiation payload, accumulated field by field and bounded.
    /// </summary>
    /// <remarks>
    /// One buffer, not two, and a flag rather than a pair of parallel lists. MSSP delimits fields
    /// with <c>MSSP_VAR</c> and <c>MSSP_VAL</c> markers, and a variable may carry several values;
    /// parallel name/value lists cannot express that, and the implementation before that silently ran
    /// consecutive values together into one buffer. The field currently being accumulated is the tail
    /// of this buffer, from <see cref="_fieldStart"/> onwards.
    /// <para>
    /// It is the <em>payload</em> that is bounded rather than the field, because a report of a
    /// hundred thousand tiny variables costs the same memory as one enormous value.
    /// </para>
    /// </remarks>
    private readonly SubnegotiationBuffer _msspBytes = new();

    /// <summary>
    /// Where the field currently being accumulated starts within <see cref="_msspBytes"/>.
    /// </summary>
    private int _fieldStart;

    private bool _currentFieldIsValue;
    private string? _currentVariable;
    private readonly MSSPVariableCollection _received = new();

    // No longer needs reflection - uses generated MSSPConfigAccessor instead

    /// <inheritdoc />
    public override Type ProtocolType => typeof(MSSPProtocol);

    /// <inheritdoc />
    public override string ProtocolName => "MSSP (Mud Server Status Protocol)";

    /// <inheritdoc />
    public override IReadOnlyCollection<Type> Dependencies => Array.Empty<Type>();

    /// <summary>
    /// Sets the MSSP configuration provider
    /// </summary>
    public void SetMSSPConfig(Func<MSSPConfig> config)
    {
        _msspConfig = config ?? (() => new MSSPConfig());
    }

    /// <summary>
    /// Gets the current MSSP configuration
    /// </summary>
    public MSSPConfig GetMSSPConfig() => _msspConfig();

    private async ValueTask OnDontMsspAsServerAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Client won't do MSSP - do nothing");
        await OnNegotiatedAsync(false);
    }

    private async ValueTask OnWontMsspAsClientAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Server won't do MSSP - do nothing");
        await OnNegotiatedAsync(false);
    }

    /// <summary>
    /// What arriving at DO/DONT or WILL/WONT for MSSP does, in either role.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <c>DO</c> means two different things depending on whether this end already offered. A server
    /// announced <c>WILL MSSP</c> on initialisation (see <see cref="ConfigureStateMachine"/>), so the
    /// <c>DO</c> that follows is the peer agreeing, and the specification says precisely what to do
    /// with it: "if the server receives IAC DO MSSP it should respond with: IAC SB MSSP MSSP_VAR
    /// "variable" MSSP_VAL "value"...IAC SE." The report is the response; no <c>WILL</c> is owed,
    /// because one was already sent.
    /// </para>
    /// <para>
    /// A client made no such offer, so the same <c>DO</c> is an unsolicited request, and RFC 1143
    /// requires an answer: "a TELNET implementation MUST refuse (DONT/WONT) a request to enable an
    /// option for which it does not comply with the appropriate protocol specification". A client
    /// used to answer it by serving a report, which is wrong twice over -- a subnegotiation is not an
    /// answer to a <c>DO</c> under RFC 854, and there was no report to serve, so what went out was an
    /// empty <c>IAC SB MSSP IAC SE</c>.
    /// </para>
    /// <para>
    /// The answer is <c>WONT</c>. MSSP reports a server's own status and the specification provides
    /// no client-side report at all -- every variable it defines flows server to client -- so a
    /// client cannot comply with a request to enable it, which is exactly the case RFC 1143 has
    /// refused rather than left unanswered.
    /// </para>
    /// <para>
    /// The <c>WILL</c> case stays symmetric-looking but is not the mirror of this: a peer offering to
    /// send a report is offering something this library can <em>receive</em> in either role, since
    /// the parsing side of MSSP reads a report without reference to the interpreter's mode. Accepting
    /// it with <c>DO</c> is lenient about an out-of-spec offer, not a claim that clients report.
    /// </para>
    /// </remarks>
    internal async ValueTask OnPeerNegotiatedAsync(byte verb, IProtocolContext context)
    {
        var weAlreadyOffered = context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Server;

        switch (verb)
        {
            case (byte)Trigger.DO when weAlreadyOffered:
                await OnDoMSSPAsync(context);
                break;
            case (byte)Trigger.DO:
                await OnAskedToReportMSSPAsync(context);
                break;
            case (byte)Trigger.DONT:
                await OnDontMsspAsServerAsync(context);
                break;
            case (byte)Trigger.WILL:
                await OnWillMSSPAsync(context);
                break;
            case (byte)Trigger.WONT:
                await OnWontMsspAsClientAsync(context);
                break;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Negotiation acceptance and the subnegotiation are wired to the generated machine (see
    /// <see cref="OnPeerNegotiatedAsync"/> and <see cref="CompleteMsspAsync"/>); this hook survives
    /// only to register the server's initial offer, a cross-cutting mechanism independent of which
    /// machine drives byte processing.
    /// </remarks>
    public override void ConfigureStateMachine(IProtocolContext context)
    {
        if (context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Server)
        {
            context.RegisterInitialNegotiation(async () => await WillingMSSPAsync(context));
        }
    }

    /// <inheritdoc />
    protected override ValueTask OnInitializeAsync()
    {
        Context.Logger.LogInformation("MSSP Protocol initialized");
        return default(ValueTask);
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolEnabledAsync()
    {
        Context.Logger.LogInformation("MSSP Protocol enabled");
        return default(ValueTask);
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolDisabledAsync()
    {
        Context.Logger.LogInformation("MSSP Protocol disabled");
        ClearMSSPState();
        return default(ValueTask);
    }

    /// <summary>
    /// Adds a variable byte to the current MSSP variable
    /// </summary>
    public void AddMSSPVariableByte(byte value)
    {
        if (!IsEnabled)
            return;

        if (_currentFieldIsValue) FlushField(Context.CurrentEncoding);
        _currentFieldIsValue = false;
        _msspBytes.Add(value);
    }

    /// <summary>
    /// Adds a value byte to the current MSSP value
    /// </summary>
    public void AddMSSPValueByte(byte value)
    {
        if (!IsEnabled)
            return;

        if (!_currentFieldIsValue) FlushField(Context.CurrentEncoding);
        _currentFieldIsValue = true;
        _msspBytes.Add(value);
    }

    /// <summary>
    /// Completes the current MSSP variable
    /// </summary>
    public void CompleteMSSPVariable()
    {
        if (!IsEnabled)
            return;

        _currentFieldIsValue = false;
        FlushField(Context.CurrentEncoding);
    }

    /// <summary>
    /// Completes the current MSSP value
    /// </summary>
    public void CompleteMSSPValue()
    {
        if (!IsEnabled)
            return;

        _currentFieldIsValue = true;
        FlushField(Context.CurrentEncoding);
    }

    private void ClearMSSPState()
    {
        _msspBytes.Reset();
        _fieldStart = 0;
        _currentFieldIsValue = false;
        _currentVariable = null;
        _received.Clear();
    }

    /// <summary>
    /// Drops an over-large report and says so, returning true when there is nothing left to deliver.
    /// </summary>
    /// <remarks>
    /// Dropped rather than truncated, for the same reason GMCP and MSDP drop theirs: a report that
    /// has lost an unknown number of its variables cannot be told apart from a server that never sent
    /// them, and MSSP exists to be believed.
    /// </remarks>
    private async ValueTask<bool> ReportOversizedAsync(IProtocolContext context)
    {
        if (!_msspBytes.Overflowed)
        {
            return false;
        }

        var receivedBytes = _msspBytes.ReceivedBytes;
        var maxMessageSize = _msspBytes.MaxMessageSize;
        ClearMSSPState();

        context.Logger.LogError(
            "MSSP report exceeded the maximum message size of {MaxMessageSize} bytes ({ReceivedBytes} bytes received) and was dropped. Raise MSSPProtocol.MaxMessageSize if this is legitimate traffic.",
            maxMessageSize, receivedBytes);

        if (_onMSSPMessageTooLarge != null)
        {
            await _onMSSPMessageTooLarge((ReceivedBytes: receivedBytes, MaxMessageSize: maxMessageSize));
        }

        return true;
    }

    /// <summary>
    /// Closes the field currently being accumulated, attributing it to the variable it belongs to.
    /// </summary>
    /// <remarks>
    /// A value is recorded against the variable most recently named, so repeated <c>MSSP_VAL</c>
    /// markers append rather than run together. An empty value is legitimate -- the specification
    /// says "The value can be an empty string" -- while an empty name is not, so only the latter is
    /// dropped.
    /// <para>
    /// The bytes are decoded with the connection's encoding, not a fixed one. MSSP mandates no
    /// character set: its only byte-level rule is that "variables and values cannot contain the
    /// MSSP_VAL, MSSP_VAR, IAC, or NUL byte", and its own CHARSET variable is documented as reporting
    /// "ASCII, BIG5, CP437, CP949, CP1251, EUC-KR, GB18030, ISO-8859-1, ISO-8859-2, KOI8-R, UTF-8" --
    /// so a protocol whose vocabulary includes saying "I am GB18030" cannot be read as ASCII.
    /// </para>
    /// </remarks>
    /// <param name="encoding">The connection's current encoding.</param>
    private void FlushField(Encoding encoding)
    {
        // Nothing left to attribute once the payload has blown its ceiling: the report is going to be
        // dropped whole, and the bytes after the ceiling were never stored.
        if (_msspBytes.Overflowed)
        {
            return;
        }

        var fieldLength = _msspBytes.Count - _fieldStart;

        if (fieldLength == 0 && !_currentFieldIsValue)
        {
            return;
        }

#if NET5_0_OR_GREATER
        var field = CollectionsMarshal.AsSpan(_msspBytes.Bytes).Slice(_fieldStart, fieldLength);
        var text = encoding.GetString(field);
#else
        var field = _msspBytes.Bytes.Skip(_fieldStart).Take(fieldLength).ToArray();
        var text = encoding.GetString(field);
#endif
        _fieldStart = _msspBytes.Count;

        if (_currentFieldIsValue)
        {
            // A value with no variable ahead of it is malformed; there is nothing to attach it to.
            if (_currentVariable != null)
            {
                // Copied, not referenced: the span above is a window onto the accumulating parse
                // buffer, which keeps growing and is cleared between reports, so a view of it would
                // be reading someone else's field by the time a consumer looked. The report outlives
                // the buffer and has to own what it carries.
                _received.Add(_currentVariable, text, field.ToArray());
            }

            return;
        }

        if (text.Length == 0)
        {
            return;
        }

        // Declared before any value arrives, so a variable a peer sends with no MSSP_VAL at all is
        // still reported as present-but-empty rather than vanishing.
        _currentVariable = text;
        _received.Declare(text);
    }

    /// <summary>
    /// Projects everything received into an <see cref="MSSPConfig"/>: the lossless variable map, the
    /// strongly typed properties it can fill, and <see cref="MSSPConfig.Extended"/> for the rest.
    /// </summary>
    private MSSPConfig BuildReceivedConfig(IProtocolContext context)
    {
        FlushField(context.CurrentEncoding);

        return ProjectConfig(_received, MSSPSource.TelnetOption, context.Logger);
    }

    /// <summary>
    /// Turns a collection of received variables into an <see cref="MSSPConfig"/>, tagged with the
    /// transport that delivered it. Shared by both transports: the vocabulary and the projection are
    /// identical, only the framing differs.
    /// </summary>
    internal static MSSPConfig ProjectConfig(MSSPVariableCollection received, MSSPSource source, ILogger logger)
    {
        var config = new MSSPConfig { Source = source };

        foreach (var entry in received)
        {
            var variableName = entry.Key;
            var values = entry.Value;

            // The bytes travel with the text through the projection, or the report handed to a
            // consumer would carry only what the decode could make of a field and none of what
            // arrived -- which is the whole point of keeping them. Indices match by construction:
            // every write to one list writes to the other.
            var raw = received.Raw(variableName);

            for (var index = 0; index < values.Count; index++)
            {
                config.Variables.Add(variableName, values[index], raw[index]);
            }

            if (values.Count == 0)
            {
                config.Variables.Declare(variableName);
                continue;
            }

            if (MSSPConfigAccessor.TrySetValues(config, variableName, values))
            {
                logger.LogDebug("MSSP variable set: {Variable} = {Value}", variableName, values);
                continue;
            }

            // Not a variable this library models -- an unofficial extra, or a name a codebase
            // invented. Kept rather than dropped: describing itself is what MSSP is for.
            config.Extended[variableName] = values;
            logger.LogDebug("MSSP variable kept as extended: {Variable} = {Value}", variableName, values);
        }

        return config;
    }

    /// <inheritdoc />
    protected override ValueTask OnDisposeAsync()
    {
        ClearMSSPState();
        return default(ValueTask);
    }

    /// <summary>
    /// Called by the interpreter when an MSSP request is received.
    /// Internal method that invokes the callback.
    /// </summary>
    internal async ValueTask OnMSSPRequestAsync(MSSPConfig config)
    {
        if (!IsEnabled)
            return;

        Context.Logger.LogDebug("Received MSSP request");

        if (_onMSSPRequest != null)
            await _onMSSPRequest(config).ConfigureAwait(false);
    }

    /// <summary>
    /// Hands a received report to the consumer's <see cref="OnMSSP"/> callback, whichever transport
    /// carried it.
    /// </summary>
    /// <remarks>
    /// MSSP is one protocol with two transports, so it has one callback. <see cref="MSSPPlaintextProtocol"/>
    /// delivers through this rather than owning a second callback a consumer would have to wire
    /// separately; <see cref="MSSPConfig.Source"/> is what tells the two apart.
    /// <para>
    /// Gated on <see cref="TelnetProtocolPluginBase.IsEnabled"/> for the same reason
    /// <see cref="OnMSSPRequestAsync"/> is: <c>ProtocolPluginManager.DisablePluginAsync&lt;MSSPProtocol&gt;()</c>
    /// is public, and a consumer who turns MSSP off should not keep receiving reports through the
    /// other transport.
    /// </para>
    /// </remarks>
    internal ValueTask DeliverReportAsync(MSSPConfig config)
        => IsEnabled ? _onMSSPRequest?.Invoke(config) ?? default(ValueTask) : default(ValueTask);

    /// <summary>
    /// Reports a reply dropped for exceeding <see cref="MaxMessageSize"/> to the consumer's
    /// <see cref="OnMSSPMessageTooLarge"/> callback. Shared with <see cref="MSSPPlaintextProtocol"/>
    /// for the same reason the callback is: one ceiling, one notification, either transport.
    /// </summary>
    internal ValueTask ReportTooLargeAsync(long receivedBytes)
        => IsEnabled
            ? _onMSSPMessageTooLarge?.Invoke((ReceivedBytes: receivedBytes, MaxMessageSize: MaxMessageSize))
              ?? default(ValueTask)
            : default(ValueTask);

    #region State Machine Handlers

    private async ValueTask WillingMSSPAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Announcing willingness to MSSP!");
        await context.SendNegotiationAsync(s_willMssp);
    }

    private async ValueTask OnDoMSSPAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Client wants MSSP data. Sending...");
        await OnNegotiatedAsync(true);

        var config = _msspConfig();
        await SendMSSPDataAsync(config, context);
    }

    /// <summary>
    /// An unsolicited <c>DO MSSP</c>: the peer is asking this end for a status report it has no way
    /// to produce. See <see cref="OnPeerNegotiatedAsync"/> for why the refusal is owed.
    /// </summary>
    private async ValueTask OnAskedToReportMSSPAsync(IProtocolContext context)
    {
        context.Logger.LogDebug(
            "Peer asked this end for an MSSP report. Refusing: MSSP reports a server's own status and defines no client-side report.");

        await Helpers.OptionNegotiation.AnswerAsync(
            honour: false, (byte)Trigger.DO, (byte)Trigger.MSSP, context);
    }

    private async ValueTask OnWillMSSPAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Server will send MSSP data");
        await OnNegotiatedAsync(true);
        await context.SendNegotiationAsync(s_doMssp);
    }

    private async ValueTask SendMSSPDataAsync(MSSPConfig config, IProtocolContext context)
    {
        var bytes = new List<byte>
        {
            (byte)Trigger.IAC,
            (byte)Trigger.SB,
            (byte)Trigger.MSSP
        };

        var encoding = context.CurrentEncoding;

        foreach (var (name, value) in ReportFields(config))
        {
            bytes.AddRange(ConvertToMSSP(name, value, encoding));
        }

        bytes.Add((byte)Trigger.IAC);
        bytes.Add((byte)Trigger.SE);

        await context.SendNegotiationAsync(bytes.ToArray());
    }

    /// <summary>
    /// Every variable a configuration has to report, once each, in the order the transports send them.
    /// </summary>
    /// <remarks>
    /// A configuration that carries a verbatim variable map -- one received from a peer -- is written
    /// from it, so a report round-trips with its arrays and its unknown variables intact. A
    /// configuration built by hand has an empty map, and only the typed properties and
    /// <see cref="MSSPConfig.Extended"/> contribute. Shared by both transports so that a server
    /// answering <c>MSSP-REQUEST</c> reports exactly what it reports over option 70.
    /// </remarks>
    internal static IEnumerable<(string Name, object Value)> ReportFields(MSSPConfig config)
    {
        var written = new HashSet<string>(StringComparer.Ordinal);

        foreach (var variable in config.Variables)
        {
            written.Add(variable.Key);
            yield return (variable.Key, variable.Value);
        }

        // Serialize MSSP configuration using reflection
        var knownFields = typeof(MSSPConfig).GetProperties()
            .Where(field => Attribute.IsDefined(field, typeof(NameAttribute)));

        foreach (var field in knownFields)
        {
            var value = field.GetValue(config);
            if (value == null) continue;

            if (Attribute.GetCustomAttribute(field, typeof(NameAttribute)) is not NameAttribute attr) continue;

            if (!written.Add(MSSPVariables.Canonicalize(attr.Name))) continue;

            yield return (attr.Name, value);
        }

        foreach (var item in config.Extended ?? new Dictionary<string, object>())
        {
            if (item.Value == null) continue;
            if (!written.Add(MSSPVariables.Canonicalize(item.Key))) continue;

            yield return (item.Key, item.Value);
        }
    }

    /// <summary>
    /// Writes one variable and its value(s) as <c>MSSP_VAR name MSSP_VAL value ...</c>.
    /// </summary>
    /// <remarks>
    /// Encoded with the connection's encoding for the same reason a received report is decoded with
    /// it: MSSP fixes no character set. Writing ASCII while reading anything else would also mean a
    /// report received from a peer and sent back on could not round-trip, which
    /// <see cref="SendMSSPDataAsync"/> otherwise guarantees.
    /// </remarks>
    private static byte[] ConvertToMSSP(string name, object val, Encoding encoding)
    {
        var bt = new List<byte> { (byte)Trigger.MSSP_VAR };
        AppendEscaped(bt, name, encoding);

        switch (val)
        {
            case string s:
                bt.Add((byte)Trigger.MSSP_VAL);
                AppendEscaped(bt, s, encoding);
                break;
            case int i:
                bt.Add((byte)Trigger.MSSP_VAL);
                // Invariant, not current-culture: a culture whose NegativeSign is not '-' would put a
                // U+2212 on the wire for CRAWL DELAY's -1, which no peer parses as a number.
                AppendEscaped(bt, i.ToString(CultureInfo.InvariantCulture), encoding);
                break;
            case bool b:
                bt.Add((byte)Trigger.MSSP_VAL);
                AppendEscaped(bt, b ? "1" : "0", encoding);
                break;
            case System.Collections.IEnumerable enumerable:
                foreach (var item in enumerable)
                {
                    bt.Add((byte)Trigger.MSSP_VAL);
                    AppendEscaped(bt, item?.ToString() ?? string.Empty, encoding);
                }
                break;
        }

        return bt.ToArray();
    }

    /// <summary>
    /// Appends the encoded bytes of <paramref name="text"/>, doubling any <c>IAC</c> among them.
    /// </summary>
    /// <remarks>
    /// Kept as a named method here because <see cref="MSSPPlaintextProtocol"/> writes the same report
    /// over the plaintext transport and escapes it the same way; the rule itself lives in
    /// <see cref="Helpers.SubnegotiationEscaping"/>. MSSP says a variable or value "cannot contain
    /// the MSSP_VAL, MSSP_VAR, IAC, or NUL byte", which is not something this library can rely on:
    /// with a non-ASCII encoding a single character encodes to 0xFF (ISO-8859-1 'ÿ').
    /// </remarks>
    internal static void AppendEscaped(List<byte> destination, string text, Encoding encoding)
        => Helpers.SubnegotiationEscaping.AppendEscaped(destination, text, encoding);

    /// <summary>
    /// An <c>MSSP_VAR</c> marker: whatever was being accumulated is finished, and a variable name
    /// starts. Fires on re-entry too, so a repeated variable is a new entry rather than a no-op.
    /// </summary>
    private void OnMSSPVariableMarker(IProtocolContext context)
    {
        FlushField(context.CurrentEncoding);
        _currentFieldIsValue = false;
    }

    /// <summary>
    /// An <c>MSSP_VAL</c> marker: whatever was being accumulated is finished, and a value starts.
    /// Fires on re-entry, which is what keeps consecutive values of one variable separate.
    /// </summary>
    private void OnMSSPValueMarker(IProtocolContext context)
    {
        FlushField(context.CurrentEncoding);
        _currentFieldIsValue = true;
    }

    /// <summary>
    /// A stretch of the field being accumulated, whether it is a variable name or a value: the two
    /// differ only in what <see cref="FlushField"/> does with them at the next marker.
    /// </summary>
    internal void AppendMsspBytes(ReadOnlyMemory<byte> data)
    {
        foreach (var b in data.Span)
        {
            _msspBytes.Add(b);
        }
    }

    /// <summary>The generated machine's equivalent of AlmostNegotiatingMSSP's OnEntry.</summary>
    internal void StartMsspMessage() => ClearMSSPState();

    /// <summary>The generated machine's equivalent of <see cref="OnMSSPVariableMarker"/>.</summary>
    internal void OnMsspVariableMarker(IProtocolContext context) => OnMSSPVariableMarker(context);

    /// <summary>The generated machine's equivalent of <see cref="OnMSSPValueMarker"/>.</summary>
    internal void OnMsspValueMarker(IProtocolContext context) => OnMSSPValueMarker(context);

    /// <summary>The generated machine's equivalent of <see cref="ReadMSSPValues"/>.</summary>
    internal ValueTask CompleteMsspAsync(IProtocolContext context) => ReadMSSPValues(context);

    private async ValueTask ReadMSSPValues(IProtocolContext context)
    {
        try
        {
            if (await ReportOversizedAsync(context))
            {
                return;
            }

            var config = BuildReceivedConfig(context);

            // Call user callback
            if (_onMSSPRequest != null)
            {
                await _onMSSPRequest(config);
            }
        }
        finally
        {
            ClearMSSPState();
        }
    }

    #endregion
}
