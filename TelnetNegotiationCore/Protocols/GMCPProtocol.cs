using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OneOf;
using Stateless;
using TelnetNegotiationCore.Attributes;
using TelnetNegotiationCore.Helpers;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Plugins;

namespace TelnetNegotiationCore.Protocols;

/// <summary>
/// GMCP (Generic MUD Communication Protocol) plugin implementation.
/// This demonstrates the plugin architecture pattern.
/// </summary>
/// <remarks>
/// This protocol optionally accepts configuration. Call <see cref="OnGMCPMessage"/> to set up
/// the callback that will handle GMCP messages if you need to be notified of client messages.
/// </remarks>
[RequiredMethod("OnGMCPMessage", Description = "Configure the callback to handle GMCP messages (optional but recommended)")]
public class GMCPProtocol : TelnetProtocolPluginBase
{
    private static readonly byte[] s_willGmcp = new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.GMCP };
    private static readonly byte[] s_doGmcp = new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.GMCP };

    private readonly SubnegotiationBuffer _gmcpBytes = new();

    private Func<(string Package, string Info), ValueTask>? _onGMCPReceived;

    private Func<(string Package, long ReceivedBytes, int MaxMessageSize), ValueTask>? _onGMCPMessageTooLarge;

    /// <summary>
    /// Sets the callback that is invoked when a GMCP message is received.
    /// </summary>
    /// <param name="callback">The callback to handle GMCP messages</param>
    /// <returns>This instance for fluent chaining</returns>
    public GMCPProtocol OnGMCPMessage(Func<(string Package, string Info), ValueTask>? callback)
    {
        _onGMCPReceived = callback;
        return this;
    }

    /// <summary>
    /// The largest GMCP message this connection will accept, in bytes, counted over the whole
    /// subnegotiation payload (package name, separator and data together). Defaults to 1 MiB.
    /// </summary>
    /// <remarks>
    /// The GMCP specification defines no maximum message size, so this is a defence against a
    /// hostile or broken peer rather than a protocol limit. A message larger than this is
    /// <em>dropped</em>, never truncated: it is logged at Error level and reported to the
    /// <see cref="OnGMCPMessageTooLarge"/> callback, because half a JSON document is indistinguishable
    /// from a malformed one.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
    public int MaxMessageSize
    {
        get => _gmcpBytes.MaxMessageSize;
        set => _gmcpBytes.MaxMessageSize = value;
    }

    /// <summary>
    /// Sets the maximum GMCP message size in a fluent manner.
    /// </summary>
    /// <param name="maxMessageSize">The maximum message size in bytes</param>
    /// <returns>This instance for fluent chaining</returns>
    public GMCPProtocol WithMaxMessageSize(int maxMessageSize)
    {
        MaxMessageSize = maxMessageSize;
        return this;
    }

    /// <summary>
    /// Sets the callback that is invoked when a GMCP message is dropped for exceeding
    /// <see cref="MaxMessageSize"/>. This is the observable counterpart of the Error level log:
    /// it lets a consumer distinguish "the server sent nothing" from "the server sent too much".
    /// </summary>
    /// <param name="callback">The callback to handle oversized GMCP messages. Receives the package
    /// name (empty if the message had no separator), the number of bytes the peer sent, and the
    /// configured limit.</param>
    /// <returns>This instance for fluent chaining</returns>
    public GMCPProtocol OnGMCPMessageTooLarge(Func<(string Package, long ReceivedBytes, int MaxMessageSize), ValueTask>? callback)
    {
        _onGMCPMessageTooLarge = callback;
        return this;
    }



    /// <inheritdoc />
    public override Type ProtocolType => typeof(GMCPProtocol);

    /// <inheritdoc />
    public override string ProtocolName => "GMCP (Generic MUD Communication Protocol)";

    /// <inheritdoc />
    public override IReadOnlyCollection<Type> Dependencies => Array.Empty<Type>();
    // Note: In the original code, GMCP had a dependency on MSDP for message forwarding
    // This can be expressed as: new[] { typeof(MSDPProtocol) }

    /// <inheritdoc />
    public override void ConfigureStateMachine(StateMachine<State, Trigger> stateMachine, IProtocolContext context)
    {
        context.Logger.LogInformation("Configuring GMCP state machine");
        
        // Register GMCP protocol handlers with the context
        context.SetSharedState("GMCP_Protocol", this);
        
        if (context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Server)
        {
            stateMachine.Configure(State.Do)
                .Permit(Trigger.GMCP, State.DoGMCP);

            stateMachine.Configure(State.Dont)
                .Permit(Trigger.GMCP, State.DontGMCP);

            stateMachine.Configure(State.DoGMCP)
                .SubstateOf(State.Accepting)
                .OnEntry(() => context.Logger.LogDebug("Connection: {ConnectionState}", "Client will do GMCP"));

            stateMachine.Configure(State.DontGMCP)
                .SubstateOf(State.Accepting)
                .OnEntry(() => context.Logger.LogDebug("Connection: {ConnectionState}", "Client will not GMCP"));

            context.RegisterInitialNegotiation(async () => await WillGMCPAsync(context));
        }
        else if (context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Client)
        {
            stateMachine.Configure(State.Willing)
                .Permit(Trigger.GMCP, State.WillGMCP);

            stateMachine.Configure(State.Refusing)
                .Permit(Trigger.GMCP, State.WontGMCP);

            stateMachine.Configure(State.WillGMCP)
                .SubstateOf(State.Accepting)
                .OnEntryAsync(async x => await DoGMCPAsync(x, context));

            stateMachine.Configure(State.WontGMCP)
                .SubstateOf(State.Accepting)
                .OnEntry(() => context.Logger.LogDebug("Connection: {ConnectionState}", "Client will GMCP"));
        }

        stateMachine.Configure(State.SubNegotiation)
            .Permit(Trigger.GMCP, State.AlmostNegotiatingGMCP);

        stateMachine.Configure(State.AlmostNegotiatingGMCP)
            .Permit(Trigger.IAC, State.EscapingGMCPValue)
            .OnEntry(() => _gmcpBytes.Reset());

        TriggerHelper.ForAllTriggersButIAC(t => stateMachine
                .Configure(State.EvaluatingGMCPValue)
                .PermitReentry(t)
                .OnEntryFrom(context.Interpreter.ParameterizedTrigger(t), RegisterGMCPValue));

        TriggerHelper.ForAllTriggersButIAC(t => stateMachine
                .Configure(State.AlmostNegotiatingGMCP)
                .Permit(t, State.EvaluatingGMCPValue));

        stateMachine.Configure(State.EvaluatingGMCPValue)
            .Permit(Trigger.IAC, State.EscapingGMCPValue);

        stateMachine.Configure(State.EscapingGMCPValue)
            .Permit(Trigger.IAC, State.EvaluatingGMCPValue)
            .Permit(Trigger.SE, State.CompletingGMCPValue);

        stateMachine.Configure(State.CompletingGMCPValue)
            .SubstateOf(State.Accepting)
            .OnEntryAsync(async x => await CompleteGMCPNegotiation(x, context));
    }

    /// <inheritdoc />
    protected override ValueTask OnInitializeAsync()
    {
        Context.Logger.LogInformation("GMCP Protocol initialized");
        return default(ValueTask);
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolEnabledAsync()
    {
        Context.Logger.LogInformation("GMCP Protocol enabled");
        return default(ValueTask);
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolDisabledAsync()
    {
        Context.Logger.LogInformation("GMCP Protocol disabled");
        _gmcpBytes.Reset();
        return default(ValueTask);
    }

    /// <summary>
    /// Called by the interpreter when a GMCP message is received.
    /// Internal method that invokes the callback.
    /// </summary>
    internal async ValueTask OnGMCPMessageAsync((string Package, string Info) message)
    {
        if (!IsEnabled)
            return;

        Context.Logger.LogDebug("Received GMCP message: Package={Package}", message.Package);
        
        if (_onGMCPReceived != null)
            await _onGMCPReceived(message).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override ValueTask OnDisposeAsync()
    {
        _gmcpBytes.Reset();
        return default(ValueTask);
    }

    #region State Machine Handlers

    private void RegisterGMCPValue(OneOf<byte, Trigger> b) => _gmcpBytes.Add(b.AsT0);

    private async ValueTask CompleteGMCPNegotiation(StateMachine<State, Trigger>.Transition _, IProtocolContext context)
    {
        var gmcpBytes = _gmcpBytes.Bytes;

        if (_gmcpBytes.Overflowed)
        {
            // The message is incomplete, so it is dropped rather than handed over as if it were
            // whole. Report it loudly: a silently shortened payload is invalid JSON that the
            // consumer cannot tell apart from a malformed server.
            var receivedBytes = _gmcpBytes.ReceivedBytes;
            var maxMessageSize = _gmcpBytes.MaxMessageSize;
            var oversizedPackage = PackageNameOf(gmcpBytes, context.CurrentEncoding);
            _gmcpBytes.Reset();

            context.Logger.LogError(
                "GMCP message for package {Package} exceeded the maximum message size of {MaxMessageSize} bytes ({ReceivedBytes} bytes received) and was dropped. Raise GMCPProtocol.MaxMessageSize if this is legitimate traffic.",
                oversizedPackage, maxMessageSize, receivedBytes);

            if (_onGMCPMessageTooLarge != null)
            {
                await _onGMCPMessageTooLarge((Package: oversizedPackage, ReceivedBytes: receivedBytes, MaxMessageSize: maxMessageSize));
            }

            return;
        }

        if (gmcpBytes.Count == 0)
        {
            context.Logger.LogWarning("Empty GMCP message received");
            _gmcpBytes.Reset();
            return;
        }

        var (packageLength, dataStart) = SplitPackageAndData(gmcpBytes);

        if (packageLength == 0)
        {
            // Nothing in front of the data that could be a package name, so there is no message to
            // deliver - but say what was thrown away, or this is indistinguishable from a server
            // that sent nothing at all.
            context.Logger.LogWarning(
                "Discarded a GMCP message with no package name: {Payload}",
                Preview(gmcpBytes, context.CurrentEncoding));
            _gmcpBytes.Reset();
            return;
        }

        // A message with no data section at all is not an error: the specification says the space
        // "should be omitted" when there is no data, so Core.Ping is exactly the prescribed form.
        var separatorMissing = dataStart == packageLength && dataStart < gmcpBytes.Count;

#if NET5_0_OR_GREATER
        // Use CollectionsMarshal.AsSpan with slicing for zero-copy access
        var gmcpSpan = CollectionsMarshal.AsSpan(gmcpBytes);
        var package = context.CurrentEncoding.GetString(gmcpSpan[..packageLength]);
        var isMsdp = package == "MSDP";
        // MSDPScan requires byte[] - only allocate for the destination that will actually be called.
        var packageBytes = isMsdp ? gmcpSpan[..packageLength].ToArray() : [];
        var info = isMsdp || _onGMCPReceived is null
            ? string.Empty
            : context.CurrentEncoding.GetString(gmcpSpan[dataStart..]);
#else
        var packageBytes = gmcpBytes.Take(packageLength).ToArray();
        var package = context.CurrentEncoding.GetString(packageBytes);
        var isMsdp = package == "MSDP";
        var info = isMsdp || _onGMCPReceived is null
            ? string.Empty
            : context.CurrentEncoding.GetString(gmcpBytes.Skip(dataStart).ToArray());
#endif

        if (separatorMissing)
        {
            // Malformed - but a package name cannot contain '{', so what the sender meant is not in
            // doubt. Accepted, and reported so the sending side can be fixed.
            context.Logger.LogWarning(
                "GMCP message for package {Package} ran its data section into the package name with no space separator. Accepting it, but the sender does not match the GMCP specification.",
                package);
        }

        // Release the payload before handing control to consumer code: a large message should not
        // keep its buffer alive for the duration of the callback.
        _gmcpBytes.Reset();

        if (isMsdp)
        {
            // Call MSDP plugin if available
            var msdpPlugin = context.GetPlugin<MSDPProtocol>();
            if (msdpPlugin != null && msdpPlugin.IsEnabled)
            {
                await msdpPlugin.OnMSDPMessageAsync(context.Interpreter, JsonSerializer.Serialize(Functional.MSDPLibrary.MSDPScan(packageBytes, context.CurrentEncoding)));
            }
        }
        else if (_onGMCPReceived != null)
        {
            // Call GMCP plugin callback. A bodyless message delivers an empty Info rather than a
            // fabricated "{}": the tuple reports what was on the wire, and the consumer decides.
            await _onGMCPReceived((Package: package, Info: info));
        }
    }

    /// <summary>
    /// Best-effort package name for diagnostics, for a message that will not be delivered.
    /// </summary>
    private static string PackageNameOf(List<byte> bytes, System.Text.Encoding encoding)
    {
        var (packageLength, _) = SplitPackageAndData(bytes);

        if (packageLength <= 0)
        {
            return string.Empty;
        }

#if NET5_0_OR_GREATER
        return encoding.GetString(CollectionsMarshal.AsSpan(bytes)[..packageLength]);
#else
        return encoding.GetString(bytes.Take(packageLength).ToArray());
#endif
    }

    /// <summary>
    /// Splits a GMCP payload into its package field and its optional data section.
    /// </summary>
    /// <remarks>
    /// The specification states: "The &lt;data&gt; field is optional and should be separated from
    /// the package field with a space. When sending a command without a data section the space
    /// should be omitted." So the absence of a space is a bodyless message, not an error - unless
    /// the sender ran the data straight into the package name, which is detectable because a
    /// package name is limited to letters, digits, '.', '-' and '_'.
    /// </remarks>
    /// <returns>
    /// The length of the package field, and the index at which the data section starts. The two are
    /// equal when there is no separator; the data start equals the payload length when there is no
    /// data at all.
    /// </returns>
    private static (int PackageLength, int DataStart) SplitPackageAndData(List<byte> payload)
    {
        const byte space = (byte)' ';  // Literal instead of GetBytes(" ").First()
        var firstSpace = payload.FindIndex(x => x == space);

        if (firstSpace >= 0)
        {
            return (firstSpace, firstSpace + 1);
        }

        var firstNonPackageByte = payload.FindIndex(IsNotPackageNameByte);

        return firstNonPackageByte < 0
            ? (payload.Count, payload.Count)                    // bodyless: all package, no data
            : (firstNonPackageByte, firstNonPackageByte);       // data, missing its separator
    }

    /// <summary>
    /// True for any byte that cannot appear in a GMCP package name, and therefore marks the start
    /// of a data section that was sent without its separator.
    /// </summary>
    private static bool IsNotPackageNameByte(byte b) =>
        !((b >= (byte)'a' && b <= (byte)'z')
          || (b >= (byte)'A' && b <= (byte)'Z')
          || (b >= (byte)'0' && b <= (byte)'9')
          || b == (byte)'.'
          || b == (byte)'-'
          || b == (byte)'_');

    /// <summary>
    /// A bounded, loggable rendering of a payload that is being discarded.
    /// </summary>
    private static string Preview(List<byte> payload, System.Text.Encoding encoding)
    {
        const int maxPreviewLength = 64;
        var length = Math.Min(payload.Count, maxPreviewLength);

#if NET5_0_OR_GREATER
        var text = encoding.GetString(CollectionsMarshal.AsSpan(payload)[..length]);
#else
        var text = encoding.GetString(payload.Take(length).ToArray());
#endif

        return payload.Count > maxPreviewLength ? text + "..." : text;
    }

    private async ValueTask WillGMCPAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Connection: {ConnectionState}", "Announcing the server will GMCP");

        await context.SendNegotiationAsync(s_willGmcp);
    }

    private async ValueTask DoGMCPAsync(StateMachine<State, Trigger>.Transition _, IProtocolContext context)
    {
        context.Logger.LogDebug("Connection: {ConnectionState}", "Announcing the client can do GMCP");

        await context.SendNegotiationAsync(s_doGmcp);
    }

    #endregion
}

/// <summary>
/// MSDP (MUD Server Data Protocol) plugin implementation.
/// This demonstrates the plugin architecture pattern.
/// </summary>
/// <remarks>
/// This protocol optionally accepts configuration. Call <see cref="OnMSDPMessage"/> to set up
/// the callback that will handle MSDP messages if you need to be notified of client messages.
/// </remarks>
[RequiredMethod("OnMSDPMessage", Description = "Configure the callback to handle MSDP messages (optional but recommended)")]
public class MSDPProtocol : TelnetProtocolPluginBase
{
    private static readonly byte[] s_willMsdp = new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.MSDP };
    private static readonly byte[] s_doMsdp = new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MSDP };

    private readonly SubnegotiationBuffer _msdpBytes = new();

    private Func<Interpreters.TelnetInterpreter, string, ValueTask>? _onMSDPReceived;

    private Func<(long ReceivedBytes, int MaxMessageSize), ValueTask>? _onMSDPMessageTooLarge;

    /// <summary>
    /// Sets the callback that is invoked when an MSDP message is received.
    /// </summary>
    /// <param name="callback">The callback to handle MSDP messages</param>
    /// <returns>This instance for fluent chaining</returns>
    public MSDPProtocol OnMSDPMessage(Func<Interpreters.TelnetInterpreter, string, ValueTask>? callback)
    {
        _onMSDPReceived = callback;
        return this;
    }

    /// <summary>
    /// The largest MSDP message this connection will accept, in bytes. Defaults to 1 MiB.
    /// </summary>
    /// <remarks>
    /// The MSDP specification defines no maximum message size, so this is a defence against a
    /// hostile or broken peer rather than a protocol limit. A message larger than this is
    /// <em>dropped</em>, never truncated: a half-read MSDP table does not parse into the data the
    /// peer sent, it parses into different data.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
    public int MaxMessageSize
    {
        get => _msdpBytes.MaxMessageSize;
        set => _msdpBytes.MaxMessageSize = value;
    }

    /// <summary>
    /// Sets the maximum MSDP message size in a fluent manner.
    /// </summary>
    /// <param name="maxMessageSize">The maximum message size in bytes</param>
    /// <returns>This instance for fluent chaining</returns>
    public MSDPProtocol WithMaxMessageSize(int maxMessageSize)
    {
        MaxMessageSize = maxMessageSize;
        return this;
    }

    /// <summary>
    /// Sets the callback that is invoked when an MSDP message is dropped for exceeding
    /// <see cref="MaxMessageSize"/>.
    /// </summary>
    /// <param name="callback">The callback to handle oversized MSDP messages. Receives the number
    /// of bytes the peer sent and the configured limit.</param>
    /// <returns>This instance for fluent chaining</returns>
    public MSDPProtocol OnMSDPMessageTooLarge(Func<(long ReceivedBytes, int MaxMessageSize), ValueTask>? callback)
    {
        _onMSDPMessageTooLarge = callback;
        return this;
    }



    /// <inheritdoc />
    public override Type ProtocolType => typeof(MSDPProtocol);

    /// <inheritdoc />
    public override string ProtocolName => "MSDP (MUD Server Data Protocol)";

    /// <inheritdoc />
    public override IReadOnlyCollection<Type> Dependencies => Array.Empty<Type>();

    /// <inheritdoc />
    public override void ConfigureStateMachine(StateMachine<State, Trigger> stateMachine, IProtocolContext context)
    {
        context.Logger.LogInformation("Configuring MSDP state machine");
        
        // Register MSDP protocol handlers with the context
        context.SetSharedState("MSDP_Protocol", this);
        
        if (context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Server)
        {
            // Server-side MSDP negotiation
            stateMachine.Configure(State.Do)
                .Permit(Trigger.MSDP, State.DoMSDP);

            stateMachine.Configure(State.Dont)
                .Permit(Trigger.MSDP, State.DontMSDP);

            stateMachine.Configure(State.DoMSDP)
                .SubstateOf(State.Accepting)
                .OnEntry(() => context.Logger.LogDebug("Connection: {ConnectionState}", "Client will do MSDP"));

            stateMachine.Configure(State.DontMSDP)
                .SubstateOf(State.Accepting)
                .OnEntry(() => context.Logger.LogDebug("Connection: {ConnectionState}", "Client will not MSDP"));

            context.RegisterInitialNegotiation(async () => await WillMSDPAsync(context));
        }
        else if (context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Client)
        {
            // Client-side MSDP negotiation
            stateMachine.Configure(State.Willing)
                .Permit(Trigger.MSDP, State.WillMSDP);

            stateMachine.Configure(State.Refusing)
                .Permit(Trigger.MSDP, State.WontMSDP);

            stateMachine.Configure(State.WillMSDP)
                .SubstateOf(State.Accepting)
                .OnEntryAsync(async x => await DoMSDPAsync(x, context));

            stateMachine.Configure(State.WontMSDP)
                .SubstateOf(State.Accepting)
                .OnEntry(() => context.Logger.LogDebug("Connection: {ConnectionState}", "Server will not MSDP"));
        }

        // Sub-negotiation states (common to both server and client)
        stateMachine.Configure(State.SubNegotiation)
            .Permit(Trigger.MSDP, State.AlmostNegotiatingMSDP);

        stateMachine.Configure(State.AlmostNegotiatingMSDP)
            .Permit(Trigger.IAC, State.EscapingMSDP)
            .OnEntry(() => _msdpBytes.Reset());

        // Configure transitions for all non-IAC triggers to capture MSDP values
        TriggerHelper.ForAllTriggersButIAC(t => stateMachine
            .Configure(State.EvaluatingMSDP)
            .PermitReentry(t)
            .OnEntryFrom(context.Interpreter.ParameterizedTrigger(t), CaptureMSDPByte));

        TriggerHelper.ForAllTriggersButIAC(t => stateMachine
            .Configure(State.AlmostNegotiatingMSDP)
            .Permit(t, State.EvaluatingMSDP));

        stateMachine.Configure(State.EvaluatingMSDP)
            .Permit(Trigger.IAC, State.EscapingMSDP);

        stateMachine.Configure(State.EscapingMSDP)
            .Permit(Trigger.IAC, State.EvaluatingMSDP)
            .Permit(Trigger.SE, State.CompletingMSDP);

        stateMachine.Configure(State.CompletingMSDP)
            .SubstateOf(State.Accepting)
            .OnEntryAsync(async x => await CompleteMSDPNegotiation(x, context));
    }

    /// <inheritdoc />
    protected override ValueTask OnInitializeAsync()
    {
        Context.Logger.LogInformation("MSDP Protocol initialized");
        return default(ValueTask);
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolEnabledAsync()
    {
        Context.Logger.LogInformation("MSDP Protocol enabled");
        return default(ValueTask);
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolDisabledAsync()
    {
        Context.Logger.LogInformation("MSDP Protocol disabled");
        _msdpBytes.Reset();
        return default(ValueTask);
    }

    /// <summary>
    /// Called by the interpreter when an MSDP message is received.
    /// Internal method that invokes the callback.
    /// </summary>
    internal async ValueTask OnMSDPMessageAsync(Interpreters.TelnetInterpreter interpreter, string message)
    {
        if (!IsEnabled)
            return;

        Context.Logger.LogDebug("Received MSDP message");
        
        if (_onMSDPReceived != null)
            await _onMSDPReceived(interpreter, message).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override ValueTask OnDisposeAsync()
    {
        _msdpBytes.Reset();
        return default(ValueTask);
    }

    #region State Machine Handlers

    private void CaptureMSDPByte(OneOf<byte, Trigger> b)
    {
        if (!IsEnabled)
            return;

        _msdpBytes.Add(b.AsT0);
    }

    private async ValueTask CompleteMSDPNegotiation(StateMachine<State, Trigger>.Transition _, IProtocolContext context)
    {
        if (_msdpBytes.Overflowed)
        {
            // Incomplete: dropped rather than parsed, since a truncated MSDP body decodes into
            // different data than the peer sent, not less of it.
            var receivedBytes = _msdpBytes.ReceivedBytes;
            var maxMessageSize = _msdpBytes.MaxMessageSize;
            _msdpBytes.Reset();

            context.Logger.LogError(
                "MSDP message exceeded the maximum message size of {MaxMessageSize} bytes ({ReceivedBytes} bytes received) and was dropped. Raise MSDPProtocol.MaxMessageSize if this is legitimate traffic.",
                maxMessageSize, receivedBytes);

            if (_onMSDPMessageTooLarge != null)
            {
                await _onMSDPMessageTooLarge((ReceivedBytes: receivedBytes, MaxMessageSize: maxMessageSize));
            }

            return;
        }

        if (_msdpBytes.Count == 0)
        {
            context.Logger.LogWarning("Empty MSDP message received");
            return;
        }

        try
        {
            context.Logger.LogDebug("Processing MSDP message with {ByteCount} bytes", _msdpBytes.Count);

            // Parse MSDP bytes using the F# library
            var parsedData = Functional.MSDPLibrary.MSDPScan(_msdpBytes.Bytes, context.CurrentEncoding);
            var jsonString = JsonSerializer.Serialize(parsedData);

            // Invoke the callback if registered
            if (_onMSDPReceived != null)
            {
                await _onMSDPReceived(context.Interpreter, jsonString);
            }
        }
        catch (Exception ex)
        {
            context.Logger.LogError(ex, "Error processing MSDP message");
        }
        finally
        {
            _msdpBytes.Reset();
        }
    }

    private async ValueTask WillMSDPAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Connection: {ConnectionState}", "Announcing the server will MSDP");
        await context.SendNegotiationAsync(s_willMsdp);
    }

    private async ValueTask DoMSDPAsync(StateMachine<State, Trigger>.Transition _, IProtocolContext context)
    {
        context.Logger.LogDebug("Connection: {ConnectionState}", "Announcing the client can do MSDP");
        await context.SendNegotiationAsync(s_doMsdp);
    }

    #endregion
}
