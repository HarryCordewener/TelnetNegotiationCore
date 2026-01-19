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
            .Permit(Trigger.ACCEPTED, State.NegotiatingAcceptedCharset);

        stateMachine.Configure(State.EndingCharsetSubnegotiation)
            .Permit(Trigger.IAC, State.EndSubNegotiation);

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
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolEnabledAsync()
    {
        Context.Logger.LogInformation("Charset Protocol enabled");
        return ValueTask.CompletedTask;
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
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    protected override ValueTask OnDisposeAsync()
    {
        _charsetByteState = [];
        _acceptedCharsetByteState = [];
        return ValueTask.CompletedTask;
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
        if (_charsetByteIndex > _charsetByteState.Length) return;
        _charsetByteState[_charsetByteIndex] = b.AsT0;
        _charsetByteIndex++;
    }

    private void CaptureAcceptedCharset(OneOf<byte, Trigger> b)
    {
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
        await (_signalCharsetChangeAsync?.Invoke(CurrentEncoding) ?? ValueTask.CompletedTask);
        
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
        var encodingProp = interpreter.GetType().GetProperty("CurrentEncoding");
        if (encodingProp != null && encodingProp.CanWrite)
            encodingProp.SetValue(interpreter, CurrentEncoding);
    }

    #endregion
}
