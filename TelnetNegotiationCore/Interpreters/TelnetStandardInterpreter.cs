﻿using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Stateless;
using TelnetNegotiationCore.Models;
using OneOf;
using Microsoft.Extensions.Logging;
using LocalMoreLinq;

namespace TelnetNegotiationCore.Interpreters;

/// <summary>
/// TODO: Telnet Interpreter should take in a simple Interface object that can Read & Write from / to a Stream!
/// Read Byte, Write Byte, and a Buffer Size. That way we can test it.
/// </summary>
public partial class TelnetInterpreter
{
    private readonly Dictionary<byte, Trigger> _isDefinedDictionary = new();

    /// <summary>
    /// A list of functions to call at the start.
    /// </summary>
    private readonly List<Func<ValueTask>> _initialCall;

    /// <summary>
    /// The current Encoding used for interpreting incoming non-negotiation text, and what we should send on outbound.
    /// </summary>
    public Encoding CurrentEncoding { get; private set; } = Encoding.ASCII;

    /// <summary>
    /// Telnet state machine
    /// </summary>
    public StateMachine<State, Trigger> TelnetStateMachine { get; }

    /// <summary>
    /// A cache of parameterized triggers.
    /// </summary>
    private readonly ParameterizedTriggers _parameterizedTriggers;

    /// <summary>
    /// Local buffer. We only take up to 5mb in buffer space. 
    /// </summary>
    private readonly byte[] _buffer = new byte[5242880];

    /// <summary>
    /// Buffer position where we are writing.
    /// </summary>
    private int _bufferPosition;

    /// <summary>
    /// Helper function for Byte parameterized triggers.
    /// </summary>
    /// <param name="t">The Trigger</param>
    /// <returns>A Parameterized trigger</returns>
    private StateMachine<State, Trigger>.TriggerWithParameters<OneOf<byte, Trigger>> ParameterizedTrigger(Trigger t)
        => _parameterizedTriggers.ParameterizedTrigger(TelnetStateMachine, t);

    /// <summary>
    /// The Logger
    /// </summary>
    private readonly ILogger _logger;

    public enum TelnetMode
    {
        Error = 0,
        Client = 1,
        Server = 2
    };

    public TelnetMode Mode { get; }

    /// <summary>
    /// Callback to run on a submission (linefeed)
    /// </summary>
    public required Func<byte[], Encoding, TelnetInterpreter, ValueTask>? CallbackOnSubmitAsync { get; init; }

    /// <summary>
    /// Callback to the output stream directly for negotiation.
    /// </summary>
    public required Func<byte[], ValueTask> CallbackNegotiationAsync { get; init; }

    /// <summary>
    /// Callback per byte.
    /// </summary>
    public Func<byte, Encoding, ValueTask>? CallbackOnByteAsync { get; init; }

    /// <summary>
    /// Constructor, sets up for standard Telnet protocol with NAWS and Character Set support.
    /// </summary>
    /// <remarks>
    /// After calling this constructor, one should subscribe to the Triggers, register a Stream, and then run Process()
    /// </remarks>
    /// <param name="mode">Server or Client mode</param>
    /// <param name="logger">A Serilog Logger. If null, we will use the default one with a Context of the Telnet Interpreter.</param>
    public TelnetInterpreter(TelnetMode mode, ILogger logger)
    {
        Mode = mode;
        _logger = logger;
        logger.BeginScope(new Dictionary<string, object> { { "TelnetMode", mode } });

        _initialCall = [];
        TelnetStateMachine = new StateMachine<State, Trigger>(State.Accepting);
        _parameterizedTriggers = new ParameterizedTriggers();

        SupportedCharacterSets = new Lazy<byte[]>(CharacterSets, true);

        new List<Func<StateMachine<State, Trigger>, StateMachine<State, Trigger>>>
        {
            SetupSafeNegotiation,
            SetupEORNegotiation,
            SetupSuppressGANegotiation,
            SetupMSSPNegotiation,
            SetupMSDPNegotiation,
            SetupGMCPNegotiation,
            SetupTelnetTerminalType,
            SetupCharsetNegotiation,
            SetupNAWS,
            SetupStandardProtocol
        }.AggregateRight(TelnetStateMachine, (func, stateMachine) => func(stateMachine));

        if (logger.IsEnabled(LogLevel.Trace))
        {
            TelnetStateMachine.OnTransitioned(transition => _logger.LogTrace(
                "Telnet StateMachine: {Source} --[{Trigger}({TriggerByte})]--> {Destination}",
                transition.Source, transition.Trigger, transition.Parameters[0], transition.Destination));
        }
    }

    /// <summary>
    /// Validates the configuration, then sets up the initial calls for negotiation.
    /// </summary>
    /// <returns>The Telnet Interpreter</returns>
    public async ValueTask<TelnetInterpreter> BuildAsync()
    {
        var validatedInterpreter = Validate();

        foreach (var t in _initialCall)
        {
            await t();
        }

        return validatedInterpreter;
    }

    /// <summary>
    /// Setup standard processes.
    /// </summary>
    /// <param name="tsm">The state machine.</param>
    /// <returns>Itself</returns>
    private StateMachine<State, Trigger> SetupStandardProtocol(StateMachine<State, Trigger> tsm)
    {
        // If we are in Accepting mode, these should be interpreted as regular characters.
        TriggerHelper.ForAllTriggersButIAC(t => tsm.Configure(State.Accepting).Permit(t, State.ReadingCharacters));

        // Standard triggers, which are fine in the Awaiting state and should just be interpreted as a character in this state.
        tsm.Configure(State.ReadingCharacters)
            .SubstateOf(State.Accepting)
            .Permit(Trigger.NEWLINE, State.Act);

        TriggerHelper.ForAllTriggers(t => tsm.Configure(State.ReadingCharacters)
            .OnEntryFromAsync(ParameterizedTrigger(t), async x => await WriteToBufferAndAdvanceAsync(x)));

        // We've gotten a newline. We interpret this as time to act and send a signal back.
        tsm.Configure(State.Act)
            .SubstateOf(State.Accepting)
            .OnEntryAsync(async () => await WriteToOutput());

        // SubNegotiation
        tsm.Configure(State.Accepting)
            .Permit(Trigger.IAC, State.StartNegotiation);

        // Escaped IAC, interpret as actual IAC
        tsm.Configure(State.StartNegotiation)
            .Permit(Trigger.IAC, State.ReadingCharacters)
            .Permit(Trigger.WILL, State.Willing)
            .Permit(Trigger.WONT, State.Refusing)
            .Permit(Trigger.DO, State.Do)
            .Permit(Trigger.DONT, State.Dont)
            .Permit(Trigger.SB, State.SubNegotiation)
            .OnEntry(_ => _logger.LogTrace("Connection: {ConnectionState}", "Starting Negotiation"));

        tsm.Configure(State.StartNegotiation)
            .Permit(Trigger.NOP, State.DoNothing);

        tsm.Configure(State.DoNothing)
            .SubstateOf(State.Accepting)
            .OnEntry(() => _logger.LogTrace("Connection: {ConnectionState}", "NOP call. Do nothing."));

        // As a general documentation, negotiation means a Do followed by a Will, or a Will followed by a Do.
        // Do is followed by Refusing or Will followed by Don't indicate negative negotiation.
        tsm.Configure(State.Willing);
        tsm.Configure(State.Refusing);
        tsm.Configure(State.Do);
        tsm.Configure(State.Dont);

        tsm.Configure(State.ReadingCharacters)
            .OnEntryFrom(Trigger.IAC, _ => _logger.LogDebug("Connection: {ConnectionState}", "Canceling negotiation"));

        tsm.Configure(State.SubNegotiation)
            .OnEntryFrom(Trigger.IAC, _ => _logger.LogDebug("Connection: {ConnectionState}", "SubNegotiation request"));

        tsm.Configure(State.EndSubNegotiation)
            .Permit(Trigger.SE, State.Accepting);

        return tsm;
    }

    /// <summary>
    /// Write the character into a buffer.
    /// </summary>
    /// <param name="b">A useful byte for the Client/Server</param>
    private async ValueTask WriteToBufferAndAdvanceAsync(OneOf<byte, Trigger> b)
    {
        if (b.AsT0 == (byte)Trigger.CARRIAGERETURN) return;
        _logger.LogTrace("Debug: Writing into buffer: {Byte}", b.AsT0);
        _buffer[_bufferPosition] = b.AsT0;
        _bufferPosition++;
        await (CallbackOnByteAsync?.Invoke(b.AsT0, CurrentEncoding) ?? ValueTask.CompletedTask);
    }

    /// <summary>
    /// Write it to output - this should become an Event.
    /// </summary>
    private async ValueTask WriteToOutput()
    {
        var cp = new byte[_bufferPosition];
        _buffer.AsSpan()[.._bufferPosition].CopyTo(cp);
        _bufferPosition = 0;

        if (CallbackOnSubmitAsync is not null)
        {
            await CallbackOnSubmitAsync(cp, CurrentEncoding, this);
        }
    }

    /// <summary>
    /// Validates the object is ready to process.
    /// </summary>
    private TelnetInterpreter Validate()
    {
        if (CallbackOnSubmitAsync == null && CallbackOnByteAsync == null)
        {
            throw new ApplicationException(
                $"Writeback Functions ({CallbackOnSubmitAsync}, {CallbackOnByteAsync}) are null or have not been registered.");
        }

        if (CallbackNegotiationAsync == null)
        {
            throw new ApplicationException($"{CallbackNegotiationAsync} is null and has not been registered.");
        }

        if (SignalOnGMCPAsync == null)
        {
            throw new ApplicationException($"{SignalOnGMCPAsync} is null and has not been registered.");
        }

        return this;
    }

    private void RegisterInitialWilling(Func<ValueTask> fun)
    {
        _initialCall.Add(fun);
    }

    /// <summary>
    /// Interprets the next byte in an asynchronous way.
    /// </summary>
    /// <param name="bt">An integer representation of a byte.</param>
    /// <returns>ValueTask</returns>
    public async ValueTask InterpretAsync(byte bt)
    {
        if (!_isDefinedDictionary.TryGetValue(bt, out var triggerOrByte))
        {
            triggerOrByte = Enum.IsDefined(typeof(Trigger), (short)bt)
                ? (Trigger)bt
                : Trigger.ReadNextCharacter;
            _isDefinedDictionary.Add(bt, triggerOrByte);
        }
        
        await TelnetStateMachine.FireAsync(ParameterizedTrigger(triggerOrByte), bt);
    }

    /// <summary>
    /// Interprets the next byte in an asynchronous way.
    /// </summary>
    /// <param name="byteArray">An integer representation of a byte.</param>
    /// <returns>ValueTask</returns>
    public async ValueTask InterpretByteArrayAsync(ReadOnlyMemory<byte> byteArray)
    {
        foreach (var b in byteArray.ToArray())
        {
            await InterpretAsync(b);
        }
    }
}