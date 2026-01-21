using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OneOf;
using Stateless;
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
    private byte[] _charsetByteState = [];
    private int _charsetByteIndex = 0;
    private byte[] _acceptedCharsetByteState = [];
    private int _acceptedCharsetByteIndex = 0;
    private bool _charsetOffered = false;
    private Func<IEnumerable<EncodingInfo>, IOrderedEnumerable<Encoding>> _charsetOrder = x 
        => x.Select(y => y.GetEncoding()).OrderBy(z => z.EncodingName);
    private Func<Encoding, ValueTask>? _signalCharsetChangeAsync;
    private Lazy<byte[]>? _supportedCharacterSets;
    private static System.Reflection.PropertyInfo? _cachedEncodingProperty;
    
    // TTABLE support fields
    private byte[] _ttableByteState = [];
    private int _ttableByteIndex = 0;
    private bool _ttableSupportEnabled = false;
    private Dictionary<int, int>? _currentTranslationTable;
    private Func<byte[], ValueTask<bool>>? _onTTableReceived;
    private Func<ValueTask<byte[]?>>? _onTTableRequested;
    
    // TTABLE constants
    private const int TTABLE_BUFFER_SIZE = 8192;
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
    public override void ConfigureStateMachine(StateMachine<State, Trigger> stateMachine, IProtocolContext context)
    {
        context.Logger.LogInformation("Configuring Charset state machine");
        
        // Register Charset protocol handlers with the context
        context.SetSharedState("Charset_Protocol", this);
        
        // Initialize lazy-loaded supported character sets
        _supportedCharacterSets = new Lazy<byte[]>(CharacterSets);
        
        // Configure state machine transitions for Charset protocol
        stateMachine.Configure(State.Willing)
            .Permit(Trigger.CHARSET, State.WillDoCharset);

        stateMachine.Configure(State.Refusing)
            .Permit(Trigger.CHARSET, State.WontDoCharset);

        stateMachine.Configure(State.Do)
            .Permit(Trigger.CHARSET, State.DoCharset);

        stateMachine.Configure(State.Dont)
            .Permit(Trigger.CHARSET, State.DontCharset);

        stateMachine.Configure(State.WillDoCharset)
            .SubstateOf(State.Accepting)
            .OnEntryAsync(async x => await OnWillingCharsetAsync(x, context));

        stateMachine.Configure(State.WontDoCharset)
            .SubstateOf(State.Accepting)
            .OnEntry(() => context.Logger.LogDebug("Connection: {ConnectionState}", "Won't do Character Set - do nothing"));

        stateMachine.Configure(State.DoCharset)
            .SubstateOf(State.Accepting)
            .OnEntryAsync(async x => await OnDoCharsetAsync(x, context));

        stateMachine.Configure(State.DontCharset)
            .SubstateOf(State.Accepting)
            .OnEntry(() => context.Logger.LogDebug("Connection: {ConnectionState}", "Client won't do Character Set - do nothing"));

        stateMachine.Configure(State.SubNegotiation)
            .Permit(Trigger.CHARSET, State.AlmostNegotiatingCharset);

        stateMachine.Configure(State.AlmostNegotiatingCharset)
            .Permit(Trigger.REQUEST, State.NegotiatingCharset)
            .Permit(Trigger.REJECTED, State.EndingCharsetSubnegotiation)
            .Permit(Trigger.ACCEPTED, State.NegotiatingAcceptedCharset)
            .Permit(Trigger.TTABLE_IS, State.NegotiatingTTABLE)
            .Permit(Trigger.TTABLE_REJECTED, State.EndingTTABLESubnegotiation)
            .Permit(Trigger.TTABLE_ACK, State.EndingTTABLESubnegotiation)
            .Permit(Trigger.TTABLE_NAK, State.EndingTTABLESubnegotiation);

        stateMachine.Configure(State.EndingCharsetSubnegotiation)
            .Permit(Trigger.IAC, State.EndSubNegotiation);

        stateMachine.Configure(State.EndingTTABLESubnegotiation)
            .Permit(Trigger.IAC, State.EndSubNegotiation);

        // TTABLE state machine configuration
        TriggerHelper.ForAllTriggersButIAC(t => stateMachine.Configure(State.NegotiatingTTABLE).Permit(t, State.EvaluatingTTABLE));

        stateMachine.Configure(State.EscapingTTABLEValue)
            .Permit(Trigger.IAC, State.EvaluatingTTABLE)
            .Permit(Trigger.SE, State.CompletingTTABLE);

        stateMachine.Configure(State.NegotiatingTTABLE)
            .Permit(Trigger.IAC, State.EscapingTTABLEValue)
            .OnEntry(GetTTable);

        stateMachine.Configure(State.EvaluatingTTABLE)
            .Permit(Trigger.IAC, State.EscapingTTABLEValue);

        TriggerHelper.ForAllTriggers(t => stateMachine.Configure(State.EvaluatingTTABLE).OnEntryFrom(context.Interpreter.ParameterizedTrigger(t), CaptureTTable));
        TriggerHelper.ForAllTriggersButIAC(t => stateMachine.Configure(State.EvaluatingTTABLE).PermitReentry(t));

        stateMachine.Configure(State.CompletingTTABLE)
            .OnEntryAsync(async x => await CompleteTTableAsync(x, context))
            .SubstateOf(State.Accepting);

        TriggerHelper.ForAllTriggersButIAC(t => stateMachine.Configure(State.NegotiatingCharset).Permit(t, State.EvaluatingCharset));
        TriggerHelper.ForAllTriggersButIAC(t => stateMachine.Configure(State.NegotiatingAcceptedCharset).Permit(t, State.EvaluatingAcceptedCharsetValue));

        stateMachine.Configure(State.EscapingCharsetValue)
            .Permit(Trigger.IAC, State.EvaluatingCharset)
            .Permit(Trigger.SE, State.CompletingCharset);

        stateMachine.Configure(State.EscapingAcceptedCharsetValue)
            .Permit(Trigger.IAC, State.EvaluatingAcceptedCharsetValue)
            .Permit(Trigger.SE, State.CompletingAcceptedCharset);

        stateMachine.Configure(State.NegotiatingCharset)
            .Permit(Trigger.IAC, State.EscapingCharsetValue)
            .OnEntry(GetCharset);

        stateMachine.Configure(State.NegotiatingAcceptedCharset)
            .Permit(Trigger.IAC, State.EscapingAcceptedCharsetValue)
            .OnEntry(GetAcceptedCharset);

        stateMachine.Configure(State.EvaluatingCharset)
            .Permit(Trigger.IAC, State.EscapingCharsetValue);

        stateMachine.Configure(State.EvaluatingAcceptedCharsetValue)
            .Permit(Trigger.IAC, State.EscapingAcceptedCharsetValue);

        TriggerHelper.ForAllTriggers(t => stateMachine.Configure(State.EvaluatingCharset).OnEntryFrom(context.Interpreter.ParameterizedTrigger(t), CaptureCharset));
        TriggerHelper.ForAllTriggers(t => stateMachine.Configure(State.EvaluatingAcceptedCharsetValue).OnEntryFrom(context.Interpreter.ParameterizedTrigger(t), CaptureAcceptedCharset));

        TriggerHelper.ForAllTriggersButIAC(t => stateMachine.Configure(State.EvaluatingCharset).PermitReentry(t));
        TriggerHelper.ForAllTriggersButIAC(t => stateMachine.Configure(State.EvaluatingAcceptedCharsetValue).PermitReentry(t));

        stateMachine.Configure(State.CompletingAcceptedCharset)
            .OnEntryAsync(async x => await CompleteAcceptedCharsetAsync(x, context))
            .SubstateOf(State.Accepting);

        stateMachine.Configure(State.CompletingCharset)
            .OnEntryAsync(async x => await CompleteCharsetAsync(x, context))
            .SubstateOf(State.Accepting);

        context.RegisterInitialNegotiation(async () => await WillingCharsetAsync(context));
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
        _charsetByteState = [];
        _charsetByteIndex = 0;
        _acceptedCharsetByteState = [];
        _acceptedCharsetByteIndex = 0;
        _charsetOffered = false;
        _ttableByteState = [];
        _ttableByteIndex = 0;
        _currentTranslationTable = null;
        return default(ValueTask);
    }

    /// <inheritdoc />
    protected override ValueTask OnDisposeAsync()
    {
        _charsetByteState = [];
        _acceptedCharsetByteState = [];
        _ttableByteState = [];
        _currentTranslationTable = null;
        return default(ValueTask);
    }

    #region State Machine Handlers

    private void GetCharset(StateMachine<State, Trigger>.Transition _)
    {
        _charsetByteState = new byte[1024];
        _charsetByteIndex = 0;
    }

    private void GetAcceptedCharset(StateMachine<State, Trigger>.Transition _)
    {
        _acceptedCharsetByteState = new byte[42];
        _acceptedCharsetByteIndex = 0;
    }

    private void CaptureCharset(OneOf<byte, Trigger> b)
    {
        if (_charsetByteIndex >= _charsetByteState.Length) return;
        _charsetByteState[_charsetByteIndex] = b.AsT0;
        _charsetByteIndex++;
    }

    private void CaptureAcceptedCharset(OneOf<byte, Trigger> b)
    {
        if (_acceptedCharsetByteIndex >= _acceptedCharsetByteState.Length) return;
        _acceptedCharsetByteState![_acceptedCharsetByteIndex] = b.AsT0;
        _acceptedCharsetByteIndex++;
    }

    private IOrderedEnumerable<Encoding> GetCharsetOrder(IEnumerable<EncodingInfo> encodings)
    {
        return _charsetOrder(encodings);
    }

    private async ValueTask CompleteCharsetAsync(StateMachine<State, Trigger>.Transition _, IProtocolContext context)
    {
        var ascii = Encoding.ASCII;
        
        if (_charsetOffered && context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Server)
        {
            await context.SendNegotiationAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.CHARSET, (byte)Trigger.REJECTED, (byte)Trigger.IAC, (byte)Trigger.SE });
            return;
        }

        var sep = ascii.GetString(_charsetByteState, 0, 1)?[0];
        var charsetsOffered = ascii.GetString(_charsetByteState, 1, _charsetByteIndex - 1).Split(sep ?? ' ');

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
            await context.SendNegotiationAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.CHARSET, (byte)Trigger.REJECTED, (byte)Trigger.IAC, (byte)Trigger.SE });
            return;
        }

        context.Logger.LogDebug("Charsets chosen by us: {@charsetWebName} (CP: {@cp})", chosenEncoding.WebName, chosenEncoding.CodePage);

        byte[] preamble = [(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.CHARSET, (byte)Trigger.ACCEPTED];
        byte[] charsetAscii = ascii.GetBytes(chosenEncoding.WebName);
        byte[] postAmble = [ (byte)Trigger.IAC, (byte)Trigger.SE ];

        CurrentEncoding = chosenEncoding;
        await (_signalCharsetChangeAsync?.Invoke(CurrentEncoding) ?? default(ValueTask));
        
        // Update interpreter properties for backward compatibility
        UpdateInterpreterEncoding(context);

        byte[] response = [.. preamble, .. charsetAscii, .. postAmble];
        await context.SendNegotiationAsync(response);
    }

    private async ValueTask CompleteAcceptedCharsetAsync(StateMachine<State, Trigger>.Transition _, IProtocolContext context)
    {
        var ascii = Encoding.ASCII;
        
        try
        {
            CurrentEncoding = Encoding.GetEncoding(ascii.GetString(_acceptedCharsetByteState!, 0, _acceptedCharsetByteIndex).Trim());
            
            // Update interpreter properties for backward compatibility
            UpdateInterpreterEncoding(context);
        }
        catch (Exception ex)
        {
            context.Logger.LogError(ex, "Unexpected error during Accepting Charset Negotiation. Could not find charset: {charset}", ascii.GetString(_acceptedCharsetByteState!, 0, _acceptedCharsetByteIndex));
            await context.SendNegotiationAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.CHARSET, (byte)Trigger.REJECTED, (byte)Trigger.IAC, (byte)Trigger.SE });
        }
        context.Logger.LogInformation("Connection: Accepted Charset Negotiation for: {charset}", CurrentEncoding.WebName);
        _charsetOffered = false;
    }

    private async ValueTask OnWillingCharsetAsync(StateMachine<State, Trigger>.Transition _, IProtocolContext context)
    {
        context.Logger.LogDebug("Connection: {ConnectionState}", "Request charset negotiation from Client");
        await context.SendNegotiationAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.CHARSET });
        _charsetOffered = false;
    }

    private async ValueTask WillingCharsetAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Connection: {ConnectionState}", "Announcing willingness to Charset!");
        await context.SendNegotiationAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.CHARSET });
    }

    private async ValueTask OnDoCharsetAsync(StateMachine<State, Trigger>.Transition _, IProtocolContext context)
    {
        context.Logger.LogDebug("Charsets String: {CharsetList}", ";" + string.Join(";", GetCharsetOrder(AllowedEncodings()).Select(x => x.WebName)));
        await context.SendNegotiationAsync(_supportedCharacterSets!.Value);
        _charsetOffered = true;
    }

    private byte[] CharacterSets()
    {
        return [(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.CHARSET, (byte)Trigger.REQUEST,
                        .. Encoding.ASCII.GetBytes($";{string.Join(";", GetCharsetOrder(AllowedEncodings()).Select(x => x.WebName))}"),
                        (byte)Trigger.IAC, (byte)Trigger.SE];
    }
    
    private void UpdateInterpreterEncoding(IProtocolContext context)
    {
        var interpreter = context.Interpreter;
        if (_cachedEncodingProperty == null)
        {
            _cachedEncodingProperty = interpreter.GetType().GetProperty("CurrentEncoding");
        }
        
        if (_cachedEncodingProperty != null && _cachedEncodingProperty.CanWrite)
            _cachedEncodingProperty.SetValue(interpreter, CurrentEncoding);
    }

    // TTABLE state machine handlers
    private void GetTTable(StateMachine<State, Trigger>.Transition _)
    {
        _ttableByteState = new byte[TTABLE_BUFFER_SIZE];
        _ttableByteIndex = 0;
    }

    private void CaptureTTable(OneOf<byte, Trigger> b)
    {
        if (_ttableByteIndex >= _ttableByteState.Length)
        {
            Context.Logger.LogWarning("TTABLE buffer overflow - data truncated at {MaxSize} bytes", TTABLE_BUFFER_SIZE);
            return;
        }
        _ttableByteState[_ttableByteIndex] = b.AsT0;
        _ttableByteIndex++;
    }

    private async ValueTask CompleteTTableAsync(StateMachine<State, Trigger>.Transition _, IProtocolContext context)
    {
        context.Logger.LogDebug("Processing TTABLE-IS message with {Bytes} bytes", _ttableByteIndex);
        
        try
        {
            // Parse TTABLE-IS message according to RFC 2066
            // Format: <version> <sep> <charset1> <sep> <size1> <count1> <charset2> <sep> <size2> <count2> <map1> <map2>
            
            if (_ttableByteIndex < TTABLE_MIN_LENGTH)
            {
                context.Logger.LogWarning("TTABLE-IS message too short");
                await context.SendNegotiationAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.CHARSET, (byte)Trigger.TTABLE_REJECTED, (byte)Trigger.IAC, (byte)Trigger.SE });
                return;
            }

            var version = _ttableByteState[0];
            if (version != TTABLE_VERSION_1)
            {
                context.Logger.LogWarning("Unsupported TTABLE version: {Version}", version);
                await context.SendNegotiationAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.CHARSET, (byte)Trigger.TTABLE_REJECTED, (byte)Trigger.IAC, (byte)Trigger.SE });
                return;
            }

            // Invoke callback if registered
            if (_onTTableReceived != null)
            {
                var ttableData = new byte[_ttableByteIndex];
                Array.Copy(_ttableByteState, 0, ttableData, 0, _ttableByteIndex);
                
                var shouldAccept = await _onTTableReceived.Invoke(ttableData);
                
                if (shouldAccept)
                {
                    // Parse and store the translation table
                    ParseTTableVersion1(ttableData, context);
                    
                    // Send TTABLE-ACK
                    context.Logger.LogInformation("TTABLE accepted and acknowledged");
                    await context.SendNegotiationAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.CHARSET, (byte)Trigger.TTABLE_ACK, (byte)Trigger.IAC, (byte)Trigger.SE });
                }
                else
                {
                    // Send TTABLE-NAK to request retransmission
                    context.Logger.LogInformation("TTABLE rejected by callback, sending NAK");
                    await context.SendNegotiationAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.CHARSET, (byte)Trigger.TTABLE_NAK, (byte)Trigger.IAC, (byte)Trigger.SE });
                }
            }
            else
            {
                // No callback registered, reject TTABLE
                context.Logger.LogDebug("No TTABLE callback registered, rejecting");
                await context.SendNegotiationAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.CHARSET, (byte)Trigger.TTABLE_REJECTED, (byte)Trigger.IAC, (byte)Trigger.SE });
            }
        }
        catch (Exception ex)
        {
            context.Logger.LogError(ex, "Error processing TTABLE-IS message");
            await context.SendNegotiationAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.CHARSET, (byte)Trigger.TTABLE_REJECTED, (byte)Trigger.IAC, (byte)Trigger.SE });
        }
    }

    private void ParseTTableVersion1(byte[] ttableData, IProtocolContext context)
    {
        try
        {
            // Parse TTABLE version 1 data structure
            // Format: <version> <sep> <charset1> <sep> <size1> <count1> <charset2> <sep> <size2> <count2> <map1> <map2>
            
            var version = ttableData[0];
            if (version != TTABLE_VERSION_1 || ttableData.Length < TTABLE_MIN_LENGTH)
            {
                context.Logger.LogWarning("Invalid TTABLE format");
                return;
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
                return;
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
                return;
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
                return;
            }
            
            // Build translation table from map1 (charset1 -> charset2)
            _currentTranslationTable = new Dictionary<int, int>();
            for (int i = 0; i < count1 && pos + i < ttableData.Length; i++)
            {
                _currentTranslationTable[i] = ttableData[pos + i];
            }
            
            context.Logger.LogInformation("TTABLE parsed successfully: {Charset1} -> {Charset2} with {Entries} mappings",
                charset1, charset2, _currentTranslationTable.Count);
        }
        catch (Exception ex)
        {
            context.Logger.LogError(ex, "Error parsing TTABLE version 1 data");
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

        var preamble = new byte[] { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.CHARSET, (byte)Trigger.TTABLE_IS };
        var postamble = new byte[] { (byte)Trigger.IAC, (byte)Trigger.SE };
        
        var message = new byte[preamble.Length + ttableData.Length + postamble.Length];
        Array.Copy(preamble, 0, message, 0, preamble.Length);
        Array.Copy(ttableData, 0, message, preamble.Length, ttableData.Length);
        Array.Copy(postamble, 0, message, preamble.Length + ttableData.Length, postamble.Length);
        
        await Context.SendNegotiationAsync(message);
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

        await Context.SendNegotiationAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.CHARSET, (byte)Trigger.TTABLE_REJECTED, (byte)Trigger.IAC, (byte)Trigger.SE });
        Context.Logger.LogInformation("Sent TTABLE-REJECTED message");
    }

    #endregion
}
