using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TelnetNegotiationCore.Helpers;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Plugins;

namespace TelnetNegotiationCore.Protocols;

/// <summary>
/// Charset protocol plugin - RFC 2066
/// http://www.faqs.org/rfcs/rfc2066.html
/// </summary>
/// <remarks>
/// This protocol supports optional configuration. Set <see cref="CharsetOrder"/> property to define
/// the priority of character sets for negotiation, and <see cref="AllowedEncodings"/> to control
/// which character sets are allowed.
/// </remarks>
public class CharsetProtocol : TelnetProtocolPluginBase
{
    private static readonly byte[] s_charsetRejected = new byte[] { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.CHARSET, (byte)Trigger.REJECTED, (byte)Trigger.IAC, (byte)Trigger.SE };
    private static readonly byte[] s_doCharset = new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.CHARSET };
    private static readonly byte[] s_willCharset = new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.CHARSET };
    private static readonly byte[] s_ttableRejected = new byte[] { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.CHARSET, (byte)Trigger.TTABLE_REJECTED, (byte)Trigger.IAC, (byte)Trigger.SE };
    private static readonly byte[] s_ttableAck = new byte[] { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.CHARSET, (byte)Trigger.TTABLE_ACK, (byte)Trigger.IAC, (byte)Trigger.SE };
    private static readonly byte[] s_ttableNak = new byte[] { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.CHARSET, (byte)Trigger.TTABLE_NAK, (byte)Trigger.IAC, (byte)Trigger.SE };

    private bool _charsetOffered = false;
    private Func<IEnumerable<EncodingInfo>, IOrderedEnumerable<Encoding>> _charsetOrder = x 
        => x.Select(y => y.GetEncoding()).OrderBy(z => z.EncodingName);
    private Func<Encoding, ValueTask>? _signalCharsetChangeAsync;
    private Lazy<byte[]>? _supportedCharacterSets;

    // TTABLE support fields
    private readonly SubnegotiationBuffer _ttableBytes = new();
    private bool _ttableSupportEnabled = false;
    private Dictionary<int, int>? _currentTranslationTable;
    private Func<byte[], ValueTask<bool>>? _onTTableReceived;
    private Func<ValueTask<byte[]?>>? _onTTableRequested;
    
    // TTABLE constants
    private const byte TTABLE_VERSION_1 = 1;
    private const int TTABLE_MIN_LENGTH = 2;

    /// <summary>
    /// Sets the CharacterSet Order for negotiation priority
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">codepage is less than zero or greater than 65535.</exception>
    /// <exception cref="ArgumentException">codepage is not supported by the underlying platform.</exception>
    /// <exception cref="NotSupportedException">codepage is not supported by the underlying platform.</exception>
    public IEnumerable<Encoding>? CharsetOrder 
    { 
        get => null;
        set
        {
            if (value != null)
            {
                var ordered = value.Reverse().ToList();
                _charsetOrder = x => x.Select(y => y.GetEncoding()).OrderByDescending(z => ordered.IndexOf(z));
            }
        }
    }

    /// <summary>
    /// Function to get allowed encodings (defaults to all system encodings)
    /// </summary>
    public Func<IEnumerable<EncodingInfo>> AllowedEncodings { get; set; } = Encoding.GetEncodings;

    /// <summary>
    /// Currently selected encoding (defaults to UTF8)
    /// </summary>
    public Encoding CurrentEncoding { get; private set; } = Encoding.UTF8;

    /// <summary>
    /// Sets the callback that is invoked when Charset negotiation changes encoding.
    /// </summary>
    public CharsetProtocol OnCharsetChange(Func<Encoding, ValueTask>? callback)
    {
        _signalCharsetChangeAsync = callback;
        return this;
    }

    /// <summary>
    /// Enables TTABLE (Translation Table) support for character set negotiation.
    /// When enabled, the protocol can send and receive custom character set translation tables.
    /// </summary>
    public bool EnableTTableSupport
    {
        get => _ttableSupportEnabled;
        set => _ttableSupportEnabled = value;
    }

    /// <summary>
    /// The largest TTABLE-IS message this connection will accept, in bytes. Defaults to 1 MiB.
    /// </summary>
    /// <remarks>
    /// RFC 2066 places no limit on the size of a translation table - a table covering a 16-bit
    /// character set is legitimately large. A table beyond this size is rejected with
    /// TTABLE-REJECTED rather than parsed from a truncated buffer.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
    public int MaxTTableSize
    {
        get => _ttableBytes.MaxMessageSize;
        set => _ttableBytes.MaxMessageSize = value;
    }

    /// <summary>
    /// Sets the maximum TTABLE-IS message size in a fluent manner.
    /// </summary>
    /// <param name="maxTTableSize">The maximum TTABLE size in bytes</param>
    /// <returns>This instance for fluent chaining</returns>
    public CharsetProtocol WithMaxTTableSize(int maxTTableSize)
    {
        MaxTTableSize = maxTTableSize;
        return this;
    }

    /// <summary>
    /// Sets the callback that is invoked when a TTABLE is received from the remote party.
    /// The callback receives the raw TTABLE data and should return true to ACK or false to NAK.
    /// </summary>
    public CharsetProtocol OnTTableReceived(Func<byte[], ValueTask<bool>>? callback)
    {
        _onTTableReceived = callback;
        return this;
    }

    /// <summary>
    /// Sets the callback that is invoked when the remote party requests a TTABLE.
    /// The callback should return the TTABLE data to send, or null to reject.
    /// </summary>
    public CharsetProtocol OnTTableRequested(Func<ValueTask<byte[]?>>? callback)
    {
        _onTTableRequested = callback;
        return this;
    }

    /// <summary>
    /// Gets the current translation table if one has been negotiated.
    /// </summary>
    public IReadOnlyDictionary<int, int>? CurrentTranslationTable => _currentTranslationTable;

    /// <inheritdoc />
    public override Type ProtocolType => typeof(CharsetProtocol);

    /// <inheritdoc />
    public override string ProtocolName => "Charset (RFC 2066)";

    /// <inheritdoc />
    public override IReadOnlyCollection<Type> Dependencies => Array.Empty<Type>();

    /// <inheritdoc />
    /// <remarks>
    /// Negotiation acceptance and every subnegotiation are wired to the generated machine (see
    /// <see cref="OnPeerNegotiatedAsync"/>, <see cref="CompleteCharsetRequestFromBytesAsync"/>,
    /// <see cref="CompleteAcceptedCharsetFromBytesAsync"/> and <see cref="CompleteTTableFromBufferAsync"/>);
    /// this hook survives for two things that are not Stateless configuration themselves: the lazy
    /// supported-character-set list <see cref="OnDoCharsetAsync"/> sends, resolved once regardless of
    /// which machine drives byte processing, and the server's initial offer.
    /// </remarks>
    public override void ConfigureStateMachine(IProtocolContext context)
    {
        _supportedCharacterSets = new Lazy<byte[]>(CharacterSets);

        // RFC 2066: the server initiates CHARSET by offering WILL CHARSET; the client responds
        // (DO CHARSET, then ACCEPTED). If the client also proactively offered WILL CHARSET, two
        // peers would collide (WILL/WILL) and the negotiation would never resolve — a stuck CHARSET
        // state that can make a server discard the client's first line (observed against SharpMUSH:
        // the login line was dropped and the login screen redrawn). Only the server initiates.
        if (context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Server)
        {
            context.RegisterInitialNegotiation(async () => await WillingCharsetAsync(context));
        }
    }

    /// <inheritdoc />
    protected override ValueTask OnInitializeAsync()
    {
        Context.Logger.LogInformation("Charset Protocol initialized");
        return default(ValueTask);
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolEnabledAsync()
    {
        Context.Logger.LogInformation("Charset Protocol enabled");
        return default(ValueTask);
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolDisabledAsync()
    {
        Context.Logger.LogInformation("Charset Protocol disabled");
        _charsetOffered = false;
        _ttableBytes.Reset();
        _currentTranslationTable = null;
        return default(ValueTask);
    }

    /// <inheritdoc />
    protected override ValueTask OnDisposeAsync()
    {
        _ttableBytes.Reset();
        _currentTranslationTable = null;
        return default(ValueTask);
    }

    #region State Machine Handlers

    private IOrderedEnumerable<Encoding> GetCharsetOrder(IEnumerable<EncodingInfo> encodings)
    {
        return _charsetOrder(encodings);
    }

    /// <summary>
    /// The two spellings of RFC 2066's translation-table prefix. The RFC's format line writes
    /// <c>"[TTABLE ]"</c> and its prose writes <c>[TTABLE]</c>; both are accepted, because being
    /// strict about which would refuse real peers over an ambiguity in the specification itself.
    /// </summary>
    private static readonly string[] s_ttablePrefixes = ["[TTABLE]", "[TTABLE ]"];

    /// <summary>
    /// Takes RFC 2066's optional <c>{ "[TTABLE ]" &lt;Version&gt; }</c> prefix off the front of a
    /// <c>REQUEST</c> payload, leaving the <c>&lt;sep&gt;&lt;charset&gt;…</c> list.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The prefix sits before the separator octet, so it has to come off before the first remaining
    /// byte can be read as one. Left in place, the <c>[</c> is taken for the separator and the whole
    /// charset list collapses into a single unrecognised name, which is why a peer offering a
    /// translation table alongside charsets this library supports used to be rejected outright.
    /// </para>
    /// <para>
    /// The prefix means the peer is willing to accept a mapping between any charset it listed and
    /// any the receiver wants. Nothing here acts on that beyond noting it: choosing to send a
    /// <c>TTABLE-IS</c> is the receiver's option, and this library does not.
    /// </para>
    /// </remarks>
    /// <param name="bytes">The <c>REQUEST</c> payload.</param>
    /// <param name="context">For logging.</param>
    /// <param name="offered">The payload with any prefix removed.</param>
    /// <returns>
    /// False when the message is malformed — a prefix with no version octet after it — in which case
    /// the caller rejects. True otherwise, prefix or no prefix.
    /// </returns>
    private static bool TryStripTTablePrefix(byte[] bytes, IProtocolContext context, out byte[] offered)
    {
        offered = bytes;

        foreach (var prefix in s_ttablePrefixes)
        {
            if (!StartsWithAscii(bytes, prefix))
            {
                continue;
            }

            if (bytes.Length <= prefix.Length)
            {
                // RFC 2066 requires a version octet after the prefix. Without one there is neither a
                // version nor a charset list, so there is nothing to answer.
                context.Logger.LogWarning(
                    "CHARSET REQUEST carries {Prefix} with no version octet after it, rejecting", prefix);
                return false;
            }

            var version = bytes[prefix.Length];
            if (version == 0)
            {
                // "This field must not be zero." The sender is broken, but its charset list may be
                // perfectly good, so the offer of a table is ignored rather than the message refused.
                context.Logger.LogWarning(
                    "CHARSET REQUEST carries {Prefix} with a zero version, which RFC 2066 forbids. "
                    + "Ignoring the translation-table offer and reading the charset list", prefix);
            }
            else
            {
                context.Logger.LogDebug(
                    "CHARSET REQUEST offers a translation table, version {Version}", version);
            }

            // Array.Copy rather than a range slice: this assembly also targets netstandard2.0, which
            // has no RuntimeHelpers.GetSubArray for the compiler to lower `bytes[n..]` onto.
            var start = prefix.Length + 1;
            var rest = new byte[bytes.Length - start];
            Array.Copy(bytes, start, rest, 0, rest.Length);

            offered = rest;
            return true;
        }

        return true;
    }

    /// <summary>Whether <paramref name="bytes"/> begins with <paramref name="prefix"/>, case-insensitively.</summary>
    private static bool StartsWithAscii(byte[] bytes, string prefix)
    {
        if (bytes.Length < prefix.Length)
        {
            return false;
        }

        for (var i = 0; i < prefix.Length; i++)
        {
            if (char.ToUpperInvariant((char)bytes[i]) != char.ToUpperInvariant(prefix[i]))
            {
                return false;
            }
        }

        return true;
    }

    internal async ValueTask CompleteCharsetRequestFromBytesAsync(byte[] bytes, IProtocolContext context)
    {
        var ascii = Encoding.ASCII;

        if (_charsetOffered && context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Server)
        {
            await context.SendNegotiationAsync(s_charsetRejected);
            return;
        }

        // RFC 2066's optional translation-table prefix comes before the separator, so it has to be
        // taken off before the first byte can be read as one.
        if (!TryStripTTablePrefix(bytes, context, out var offered))
        {
            await context.SendNegotiationAsync(s_charsetRejected);
            return;
        }

        // A REQUEST with no bytes at all -- IAC SB CHARSET REQUEST IAC SE -- names no separator and no
        // charset, so it offers nothing to choose from. Encoding.GetString(offered, 0, 1) below assumes
        // at least one byte for the separator; reject rather than let an empty array index out of range.
        if (offered.Length == 0)
        {
            context.Logger.LogDebug("Empty CHARSET REQUEST - nothing offered, rejecting");
            await context.SendNegotiationAsync(s_charsetRejected);
            return;
        }

        var sep = ascii.GetString(offered, 0, 1)?[0];
        var charsetsOffered = ascii.GetString(offered, 1, offered.Length - 1).Split(sep ?? ' ');

        context.Logger.LogDebug("Charsets offered to us: {@charsetResultDebug}", [..charsetsOffered]);

        var encodingDict = AllowedEncodings().ToDictionary(x => x.GetEncoding().WebName);
        var offeredEncodingInfo = charsetsOffered
            .Select(x => { try { return encodingDict[Encoding.GetEncoding(x).WebName]; } catch { return null; } })
            .Where(x => x != null)
            .Select(x => x!);
        var preferredEncoding = GetCharsetOrder(offeredEncodingInfo);
        var chosenEncoding = preferredEncoding.FirstOrDefault();

        if (chosenEncoding == null)
        {
            await context.SendNegotiationAsync(s_charsetRejected);
            return;
        }

        context.Logger.LogDebug("Charsets chosen by us: {@charsetWebName} (CP: {@cp})", chosenEncoding.WebName, chosenEncoding.CodePage);

        // RFC 2066 is explicit that this is not conditional on what the payload can hold: "All
        // octets of value 255 (other than IAC) MUST be quoted to conform with TELNET requirements."
        // A charset name is IANA-registered and so ASCII, which cannot produce a 255 -- but TTABLE-IS
        // on this same option needed the escaping badly enough to be a bug (see the CHANGELOG), and
        // the difference between the two was the payload, not the rule.
        byte[] response = Helpers.SubnegotiationFrame.Build(
            (byte)Trigger.CHARSET, (byte)Trigger.ACCEPTED, ascii.GetBytes(chosenEncoding.WebName));

        CurrentEncoding = chosenEncoding;
        UpdateInterpreterEncoding(context);

        // The peer is told before the consumer is. Its CHARSET ACCEPTED is what terminates the
        // subnegotiation, and the consumer's notification is its cue to send whatever it queued while
        // CHARSET was in flight - so notifying first would invite it to send text the peer would still
        // be reading in the old charset.
        await context.SendNegotiationAsync(response);

        await NotifyCharsetChangeAsync(context);
    }

    internal async ValueTask CompleteAcceptedCharsetFromBytesAsync(byte[] bytes, IProtocolContext context)
    {
        var acceptedCharset = Encoding.ASCII.GetString(bytes, 0, bytes.Length).Trim();

        Encoding negotiated;
        try
        {
            negotiated = Encoding.GetEncoding(acceptedCharset);
        }
        catch (Exception ex)
        {
            // The charset stays where it was, so there is no change to announce.
            context.Logger.LogError(ex, "Unexpected error during Accepting Charset Negotiation. Could not find charset: {charset}", acceptedCharset);
            await context.SendNegotiationAsync(s_charsetRejected);
            _charsetOffered = false;
            return;
        }

        CurrentEncoding = negotiated;
        UpdateInterpreterEncoding(context);
        _charsetOffered = false;

        context.Logger.LogInformation("Connection: Accepted Charset Negotiation for: {charset}", CurrentEncoding.WebName);

        // The peer accepting one of our offered charsets moves the encoding exactly as our own pick
        // does in CompleteCharsetAsync, so it is announced the same way. A consumer holding text back
        // until CHARSET settles - RFC 2066 page 9, "While a CHARSET subnegotiation is in progress,
        // data SHOULD be queued" - has no other signal that it may now send. Announced last, so that
        // everything it reports is already true by the time it is told.
        await NotifyCharsetChangeAsync(context);
    }

    /// <summary>
    /// Tells the consumer the encoding moved, without letting its failure become the connection's.
    /// </summary>
    /// <remarks>
    /// Every state change this reports is committed before it runs, and on the server path the peer
    /// has already had its reply, so a consumer throwing out of here costs nothing but its own
    /// notification. It used to cost more: the interpreter's copy of the encoding went unassigned,
    /// leaving the plugin and the interpreter disagreeing about what every later line is labelled
    /// with, and on the server path the CHARSET ACCEPTED that RFC 2066 needs to terminate the
    /// subnegotiation was never sent, so the peer waited on a reply that would never come.
    /// </remarks>
    private async ValueTask NotifyCharsetChangeAsync(IProtocolContext context)
    {
        if (_signalCharsetChangeAsync is null) return;

        try
        {
            await _signalCharsetChangeAsync(CurrentEncoding);
        }
        catch (Exception ex)
        {
            context.Logger.LogError(ex,
                "A charset-change callback threw for {charset}. The encoding change stands; only the notification was lost.",
                CurrentEncoding.WebName);
        }
    }

    private async ValueTask OnWillingCharsetAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Connection: {ConnectionState}", "Request charset negotiation from Client");
        await OnNegotiatedAsync(true);
        await context.SendNegotiationAsync(s_doCharset);
        _charsetOffered = false;
    }

    private async ValueTask WillingCharsetAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Connection: {ConnectionState}", "Announcing willingness to Charset!");
        await context.SendNegotiationAsync(s_willCharset);
    }

    private ValueTask OnWontCharsetAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Connection: {ConnectionState}", "Won't do Character Set - do nothing");
        return OnNegotiatedAsync(false);
    }

    private ValueTask OnDontCharsetAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Connection: {ConnectionState}", "Client won't do Character Set - do nothing");
        return OnNegotiatedAsync(false);
    }

    /// <summary>
    /// RFC 2066's WILL/WONT/DO/DONT acceptance is symmetric: whichever side receives WILL answers
    /// DO, and whichever side receives DO answers with the charset list, regardless of which of them
    /// is the server. Only the initial offer (the server-only <c>RegisterInitialNegotiation</c> in
    /// <see cref="ConfigureStateMachine"/>) is mode-specific.
    /// </summary>
    internal async ValueTask OnPeerNegotiatedAsync(byte verb, IProtocolContext context)
    {
        switch (verb)
        {
            case (byte)Trigger.WILL:
                await OnWillingCharsetAsync(context);
                break;
            case (byte)Trigger.WONT:
                await OnWontCharsetAsync(context);
                break;
            case (byte)Trigger.DO:
                await OnDoCharsetAsync(context);
                break;
            case (byte)Trigger.DONT:
                await OnDontCharsetAsync(context);
                break;
        }
    }

    private async ValueTask OnDoCharsetAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Charsets String: {CharsetList}", ";" + string.Join(";", GetCharsetOrder(AllowedEncodings()).Select(x => x.WebName)));
        await OnNegotiatedAsync(true);
        await context.SendNegotiationAsync(_supportedCharacterSets!.Value);
        _charsetOffered = true;
    }

    private byte[] CharacterSets()
    {
        return Helpers.SubnegotiationFrame.Build(
            (byte)Trigger.CHARSET, (byte)Trigger.REQUEST,
            Encoding.ASCII.GetBytes($";{string.Join(";", GetCharsetOrder(AllowedEncodings()).Select(x => x.WebName))}"));
    }
    
    /// <summary>
    /// The interpreter carries the negotiated encoding for consumers reading
    /// <see cref="Interpreters.TelnetInterpreter.CurrentEncoding"/>.
    /// </summary>
    private void UpdateInterpreterEncoding(IProtocolContext context)
        => context.Interpreter.CurrentEncoding = CurrentEncoding;

    /// <summary>Resets the TTABLE buffer for the generated machine's TTABLE_IS start event --
    /// streaming into it rather than an unbounded per-message list is what lets
    /// <see cref="MaxTTableSize"/> reject an oversized table mid-stream instead of after it has
    /// already been read into memory.</summary>
    internal void StartTTableMessage() => _ttableBytes.Reset();

    internal void AppendTTableBytes(ReadOnlyMemory<byte> data)
    {
        foreach (var b in data.Span) _ttableBytes.Add(b);
    }

    internal ValueTask CompleteTTableFromBufferAsync(IProtocolContext context) =>
        CompleteTTableFromBytesAsync(_ttableBytes.Bytes.ToArray(), context, _ttableBytes.Overflowed);

    private async ValueTask CompleteTTableFromBytesAsync(byte[] ttableData, IProtocolContext context, bool overflowed)
    {
        context.Logger.LogDebug("Processing TTABLE-IS message with {Bytes} bytes", ttableData.Length);

        try
        {
            if (overflowed)
            {
                // A truncated translation table is a wrong translation table. RFC 2066 gives us a
                // way to say so, so say it instead of parsing the fragment.
                context.Logger.LogError(
                    "TTABLE-IS exceeded the maximum size of {MaxSize} bytes and was rejected. Raise CharsetProtocol.MaxTTableSize if this is legitimate traffic.",
                    MaxTTableSize);
                await context.SendNegotiationAsync(s_ttableRejected);
                return;
            }

            // Parse TTABLE-IS message according to RFC 2066
            // Format: <version> <sep> <charset1> <sep> <size1> <count1> <charset2> <sep> <size2> <count2> <map1> <map2>

            if (ttableData.Length < TTABLE_MIN_LENGTH)
            {
                context.Logger.LogWarning("TTABLE-IS message too short");
                await context.SendNegotiationAsync(s_ttableRejected);
                return;
            }

            var version = ttableData[0];
            if (version != TTABLE_VERSION_1)
            {
                context.Logger.LogWarning("Unsupported TTABLE version: {Version}", version);
                await context.SendNegotiationAsync(s_ttableRejected);
                return;
            }

            // Invoke callback if registered
            if (_onTTableReceived != null)
            {
                var shouldAccept = await _onTTableReceived.Invoke(ttableData);
                
                if (shouldAccept && ParseTTableVersion1(ttableData, context))
                {
                    // Send TTABLE-ACK
                    context.Logger.LogInformation("TTABLE accepted and acknowledged");
                    await context.SendNegotiationAsync(s_ttableAck);
                }
                else if (shouldAccept)
                {
                    // Callback accepted, but the payload didn't parse -- do not ACK a table we never stored
                    context.Logger.LogWarning("TTABLE-IS accepted by callback but failed to parse; rejecting");
                    await context.SendNegotiationAsync(s_ttableRejected);
                }
                else
                {
                    // Send TTABLE-NAK to request retransmission
                    context.Logger.LogInformation("TTABLE rejected by callback, sending NAK");
                    await context.SendNegotiationAsync(s_ttableNak);
                }
            }
            else
            {
                // No callback registered, reject TTABLE
                context.Logger.LogDebug("No TTABLE callback registered, rejecting");
                await context.SendNegotiationAsync(s_ttableRejected);
            }
        }
        catch (Exception ex)
        {
            context.Logger.LogError(ex, "Error processing TTABLE-IS message");
            await context.SendNegotiationAsync(s_ttableRejected);
        }
    }

    private bool ParseTTableVersion1(byte[] ttableData, IProtocolContext context)
    {
        try
        {
            // Parse TTABLE version 1 data structure
            // Format: <version> <sep> <charset1> <sep> <size1> <count1> <charset2> <sep> <size2> <count2> <map1> <map2>

            var version = ttableData[0];
            if (version != TTABLE_VERSION_1 || ttableData.Length < TTABLE_MIN_LENGTH)
            {
                context.Logger.LogWarning("Invalid TTABLE format");
                return false;
            }

            var sep = (char)ttableData[1];
            var ascii = Encoding.ASCII;
            int pos = 2;
            
            // Parse charset1 name
            int charset1Start = pos;
            while (pos < ttableData.Length && ttableData[pos] != sep) pos++;
            var charset1 = ascii.GetString(ttableData, charset1Start, pos - charset1Start);
            pos++; // Skip separator
            
            if (pos + 4 > ttableData.Length)
            {
                context.Logger.LogWarning("TTABLE too short for size1 and count1");
                return false;
            }
            
            var size1 = ttableData[pos++];
            var count1 = (ttableData[pos] << 16) | (ttableData[pos + 1] << 8) | ttableData[pos + 2];
            pos += 3;
            
            // Parse charset2 name
            int charset2Start = pos;
            while (pos < ttableData.Length && ttableData[pos] != sep) pos++;
            var charset2 = ascii.GetString(ttableData, charset2Start, pos - charset2Start);
            pos++; // Skip separator
            
            if (pos + 4 > ttableData.Length)
            {
                context.Logger.LogWarning("TTABLE too short for size2 and count2");
                return false;
            }
            
            var size2 = ttableData[pos++];
            var count2 = (ttableData[pos] << 16) | (ttableData[pos + 1] << 8) | ttableData[pos + 2];
            pos += 3;
            
            context.Logger.LogDebug("TTABLE: {Charset1} ({Size1}bit, {Count1} chars) <-> {Charset2} ({Size2}bit, {Count2} chars)",
                charset1, size1, count1, charset2, size2, count2);
            
            // Parse translation maps
            if (pos + count1 + count2 > ttableData.Length)
            {
                context.Logger.LogWarning("TTABLE data incomplete for specified counts");
                return false;
            }

            // Build translation table from map1 (charset1 -> charset2)
            _currentTranslationTable = new Dictionary<int, int>();
            for (int i = 0; i < count1 && pos + i < ttableData.Length; i++)
            {
                _currentTranslationTable[i] = ttableData[pos + i];
            }

            context.Logger.LogInformation("TTABLE parsed successfully: {Charset1} -> {Charset2} with {Entries} mappings",
                charset1, charset2, _currentTranslationTable.Count);
            return true;
        }
        catch (Exception ex)
        {
            context.Logger.LogError(ex, "Error parsing TTABLE version 1 data");
            return false;
        }
    }

    /// <summary>
    /// Sends a TTABLE-IS message to the remote party with the specified translation table data.
    /// </summary>
    /// <param name="ttableData">The translation table data in RFC 2066 version 1 format</param>
    public async ValueTask SendTTableAsync(byte[] ttableData)
    {
        if (Context == null)
        {
            throw new InvalidOperationException("Protocol not initialized");
        }

        // RFC 2066: "All octets of value 255 (other than IAC) MUST be quoted to conform with TELNET
        // requirements." A translation table maps between character sets, so an entry for any 8-bit
        // charset's 0xFF -- ISO-8859-1 'y with diaeresis', for one -- puts that byte in the payload.
        //
        // Sent raw it is worse than a desync: the receive side in CharsetModule already collapses
        // IAC IAC back to one literal byte, so an unescaped 0xFF was read as the start of an escape
        // and the table came back with bytes missing. This library mis-parsed its own output, the
        // same one-directional asymmetry that was fixed for ENCRYPT and AUTHENTICATION.
        //
        // CHARSET's other payloads cannot reach this: ACCEPTED and REQUEST build their charset lists
        // with Encoding.ASCII, which maps anything outside 0x00-0x7F to '?'.
        var message = new List<byte>(ttableData.Length + 6)
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.CHARSET, (byte)Trigger.TTABLE_IS,
        };

        Helpers.SubnegotiationEscaping.AppendEscaped(message, ttableData);

        message.Add((byte)Trigger.IAC);
        message.Add((byte)Trigger.SE);

        await Context.SendNegotiationAsync(message.ToArray());
        Context.Logger.LogInformation("Sent TTABLE-IS message with {Bytes} bytes", ttableData.Length);
    }

    /// <summary>
    /// Sends a TTABLE-REJECTED message to the remote party.
    /// </summary>
    public async ValueTask SendTTableRejectedAsync()
    {
        if (Context == null)
        {
            throw new InvalidOperationException("Protocol not initialized");
        }

        await Context.SendNegotiationAsync(s_ttableRejected);
        Context.Logger.LogInformation("Sent TTABLE-REJECTED message");
    }

    #endregion
}
