using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Serilog;
using Stateless;
using TelnetNegotiationCore.Models;
using MoreLinq;
using OneOf;

namespace TelnetNegotiationCore.Interpreters
{
	/// <summary>
	/// TODO: Telnet Interpreter should take in a simple Interface object that can Read & Write from / to a Stream!
	/// Read Byte, Write Byte, and a Buffer Size. That way we can test it.
	/// </summary>
	public partial class TelnetInterpreter
	{
		/// <summary>
		/// A list of functions to call at the start.
		/// </summary>
		private readonly List<Func<Task>> _InitialCall;

		/// <summary>
		/// The current Encoding used for interpretting incoming non-negotiation text, and what we should send on outbound.
		/// </summary>
		public Encoding CurrentEncoding { get; private set; } = Encoding.GetEncoding("ISO-8859-1");

		/// <summary>
		/// Telnet state machine
		/// </summary>
		public StateMachine<State, Trigger> TelnetStateMachine { get; private set; }

		/// <summary>
		/// A cache of parameterized triggers.
		/// </summary>
		private readonly ParameterizedTriggers _parameterizedTriggers;

		/// <summary>
		/// Local buffer. We only take up to 5mb in buffer space. 
		/// </summary>
		private readonly byte[] buffer = new byte[5242880];

		/// <summary>
		/// Buffer position where we are writing.
		/// </summary>
		private int bufferposition = 0;

		/// <summary>
		/// Helper function for Byte parameterized triggers.
		/// </summary>
		/// <param name="t">The Trigger</param>
		/// <returns>A Parameterized trigger</returns>
		private StateMachine<State, Trigger>.TriggerWithParameters<OneOf<byte, Trigger>> ParametarizedTrigger(Trigger t)
			=> _parameterizedTriggers.ParametarizedTrigger(TelnetStateMachine, t);

		/// <summary>
		/// The Serilog style Logger
		/// </summary>
		private readonly ILogger _Logger;

		public enum TelnetMode
		{
			Error = 0,
			Client = 1,
			Server = 2
		};

		public readonly TelnetMode Mode;

		/// <summary>
		/// Callback to run on a submission (linefeed)
		/// </summary>
		public Func<byte[], Encoding, Task> CallbackOnSubmit { get; init; }

		/// <summary>
		/// Callback to the output stream directly for negotiation.
		/// </summary>
		public Func<byte[], Task> CallbackNegotiation { get; init; }

		/// <summary>
		/// Callback per byte.
		/// </summary>
		public Func<byte, Encoding, Task> CallbackOnByte { get; init; }

		/// <summary>
		/// Constructor, sets up for standard Telnet protocol with NAWS and Character Set support.
		/// </summary>
		/// <remarks>
		/// After calling this constructor, one should subscribe to the Triggers, register a Stream, and then run Process()
		/// </remarks>
		/// <param name="logger">A Serilog Logger. If null, we will use the default one with a Context of the Telnet Interpreter.</param>
		public TelnetInterpreter(TelnetMode mode, ILogger logger = null)
		{
			Mode = mode;
			_Logger = logger ?? Log.Logger.ForContext<TelnetInterpreter>().ForContext("TelnetMode", mode);
			_InitialCall = new List<Func<Task>>();
			TelnetStateMachine = new StateMachine<State, Trigger>(State.Accepting);
			_parameterizedTriggers = new ParameterizedTriggers();

			SupportedCharacterSets = new Lazy<byte[]>(CharacterSets, true);

			var li = new List<Func<StateMachine<State, Trigger>, StateMachine<State, Trigger>>> {
				SetupSafeNegotiation, SetupEORNegotiation, SetupMSSPNegotiation, SetupGMCPNegotiation, SetupTelnetTerminalType, SetupCharsetNegotiation, SetupNAWS, SetupStandardProtocol
			}.AggregateRight(TelnetStateMachine, (func, statemachine) => func(statemachine));

			if (_Logger.IsEnabled(Serilog.Events.LogEventLevel.Verbose))
			{
				TelnetStateMachine.OnTransitioned((transition) => _Logger.Verbose("Telnet Statemachine: {source} --[{trigger}({triggerbyte})]--> {destination}",
					transition.Source, transition.Trigger, transition.Parameters[0], transition.Destination));
			}
		}

		public async Task<TelnetInterpreter> Build()
		{
			foreach (var t in _InitialCall)
			{
				await t();
			}
			return this;
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

			// Standard triggers, which are fine in the Awaiting state and should just be interpretted as a character in this state.
			tsm.Configure(State.ReadingCharacters)
				.SubstateOf(State.Accepting)
				.Permit(Trigger.NEWLINE, State.Act);

			TriggerHelper.ForAllTriggers(t => tsm.Configure(State.ReadingCharacters).OnEntryFrom(ParametarizedTrigger(t), WriteToBufferAndAdvance));

			// We've gotten a newline. We interpret this as time to act and send a signal back.
			tsm.Configure(State.Act)
				.SubstateOf(State.Accepting)
				.OnEntry(WriteToOutput);

			// Subnegotiation
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
				.OnEntry(x => _Logger.Debug("Connection: {connectionState}", "Starting Negotiation"));

			tsm.Configure(State.StartNegotiation)
				.Permit(Trigger.NOP, State.DoNothing);

			tsm.Configure(State.DoNothing)
				.SubstateOf(State.Accepting)
				.OnEntry(() => _Logger.Debug("Connection: {connectionState}", "NOP call. Do nothing."));

			// As a general documentation, negotiation means a Do followed by a Will, or a Will followed by a Do.
			// Do followed by Refusing or Will followed by Don't indicate negative negotiation.
			tsm.Configure(State.Willing);
			tsm.Configure(State.Refusing);
			tsm.Configure(State.Do);
			tsm.Configure(State.Dont);

			tsm.Configure(State.ReadingCharacters)
				.OnEntryFrom(Trigger.IAC, x => _Logger.Debug("Connection: {connectionState}", "Canceling negotation"));

			tsm.Configure(State.SubNegotiation)
				.OnEntryFrom(Trigger.IAC, x => _Logger.Debug("{Connection: connectionState}", "Subnegotiation request"));

			tsm.Configure(State.EndSubNegotiation)
				.Permit(Trigger.SE, State.Accepting);

			return tsm;
		}

		/// <summary>
		/// Write the character into a buffer.
		/// </summary>
		/// <param name="b">A useful byte for the Client/Server</param>
		private void WriteToBufferAndAdvance(OneOf<byte, Trigger> b)
		{
			if (b.AsT0 == (byte)Trigger.CARRIAGERETURN) return;
			_Logger.Verbose("Debug: Writing into buffer: {byte}", b.AsT0);
			buffer[bufferposition] = b.AsT0;
			bufferposition++;
			CallbackOnByte?.Invoke(b.AsT0, CurrentEncoding);
		}

		/// <summary>
		/// Write it to output - this should become an Event.
		/// </summary>
		private void WriteToOutput()
		{
			byte[] cp = new byte[bufferposition];
			Array.Copy(buffer, cp, bufferposition);
			bufferposition = 0;
			CallbackOnSubmit.Invoke(cp, CurrentEncoding);
		}

		/// <summary>
		/// Validates the object is ready to process.
		/// </summary>
		public TelnetInterpreter Validate()
		{
			if (CallbackOnSubmit == null && CallbackOnByte == null)
			{
				throw new ApplicationException($"Writeback Functions ({CallbackOnSubmit}, {CallbackOnByte}) are null or have not been registered.");
			}
			if (CallbackNegotiation == null)
			{
				throw new ApplicationException($"{CallbackNegotiation} is null and has not been registered.");
			}
			if (CallbackOnGMCP == null)
			{
				throw new ApplicationException($"{CallbackOnGMCP} is null and has not been registered.");
			}

			return this;
		}

		private void RegisterInitialWilling(Func<Task> fun)
		{
			_InitialCall.Add(fun);
		}

		/// <summary>
		/// Interprets the next byte in an asynchronous way.
		/// TODO: Cache the value of IsDefined, or get a way to compile this down to a faster call that doesn't require reflection each time.
		/// </summary>
		/// <param name="bt">An integer representation of a byte.</param>
		/// <returns>Awaitable Task</returns>
		public async Task InterpretAsync(byte bt)
		{
			if (Enum.IsDefined(typeof(Trigger), (short)bt))
			{
				await TelnetStateMachine.FireAsync(ParametarizedTrigger((Trigger)bt), bt);
			}
			else
			{
				await TelnetStateMachine.FireAsync(ParametarizedTrigger(Trigger.ReadNextCharacter), bt);
			}
		}
	}
}
