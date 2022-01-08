using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Serilog;
using Serilog.Context;
using Stateless;
using TelnetNegotiationCore.Models;
using MoreLinq;

namespace TelnetNegotiationCore.Interpretors
{
	/// <summary>
	/// TODO: Telnet Interpretor should take in a simple Interface object that can Read & Write from / to a Stream!
	/// Read Byte, Write Byte, and a Buffer Size. That way we can test it.
	/// </summary>
	public partial class TelnetInterpretor
	{
		/// <summary>
		/// A list of functions to call at the start.
		/// </summary>
		private readonly List<Func<Task>> _InitialWilling;

		/// <summary>
		/// The current Encoding used for interpretting incoming non-negotiation text, and what we should send on outbound.
		/// </summary>
		private Encoding _CurrentEncoding = Encoding.GetEncoding("ISO-8859-1");

		/// <summary>
		/// Telnet state machine
		/// </summary>
		private readonly StateMachine<State, Trigger> _TelnetStateMachine;

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
		private StateMachine<State, Trigger>.TriggerWithParameters<byte> BTrigger(Trigger t)
			=> ParameterizedTriggers.ByteTrigger(_TelnetStateMachine, t);

		/// <summary>
		/// The Serilog style Logger
		/// </summary>
		private readonly ILogger _Logger;

		public enum TelnetMode
		{
			[Obsolete("Not yet supported")]
			Client = 0,
			Server = 1
		};

		public TelnetMode Mode { get; init; }

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
		/// <param name="logger">A Serilog Logger. If null, we will use the default one with a Context of the Telnet Interpretor.</param>
		public TelnetInterpretor(ILogger logger = null)
		{
			_Logger = logger ?? Log.Logger.ForContext<TelnetInterpretor>();
			_InitialWilling = new List<Func<Task>>();
			_TelnetStateMachine = new StateMachine<State, Trigger>(State.Accepting);

			SupportedCharacterSets = new Lazy<byte[]>(CharacterSets, true);

			var li = new List<Func<StateMachine<State, Trigger>, StateMachine<State, Trigger>>> {
				SetupSafeNegotiation, SetupEORNegotiation, SetupMSSPNegotiation, SetupTelnetTerminalType, SetupCharsetNegotiation, SetupNAWS, SetupStandardProtocol
			}.AggregateRight(_TelnetStateMachine, (x, y) => x(y));

			if (_Logger.IsEnabled(Serilog.Events.LogEventLevel.Verbose))
			{
				_TelnetStateMachine.OnTransitioned((transition) => _Logger.Verbose("Telnet Statemachine: {source} --[{trigger}({triggerbyte})]--> {destination}",
					transition.Source, transition.Trigger, transition.Parameters[0], transition.Destination));
			}
		}

		public async Task<TelnetInterpretor> Build()
		{
			foreach (var t in _InitialWilling)
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

			TriggerHelper.ForAllTriggers(t => tsm.Configure(State.ReadingCharacters).OnEntryFrom(BTrigger(t), WriteToBufferAndAdvance));

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

			tsm.Configure(State.Willing);

			tsm.Configure(State.Refusing);

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
		private void WriteToBufferAndAdvance(byte b)
		{
			if (b == (byte)Trigger.CARRIAGERETURN) return;
			_Logger.Verbose("Debug: Writing into buffer: {byte}", b);
			buffer[bufferposition] = b;
			bufferposition++;
			CallbackOnByte?.Invoke(b, _CurrentEncoding);
		}

		/// <summary>
		/// Write it to output - this should become an Event.
		/// </summary>
		private void WriteToOutput()
		{
			byte[] cp = new byte[bufferposition];
			Array.Copy(buffer, cp, bufferposition);
			bufferposition = 0;
			CallbackOnSubmit.Invoke(cp, _CurrentEncoding);
		}

		/// <summary>
		/// Validates the object is ready to process.
		/// </summary>
		public TelnetInterpretor Validate()
		{
			if (CallbackOnSubmit == null && CallbackOnByte == null) 
				throw new ApplicationException($"Writeback Functions ({CallbackOnSubmit}, {CallbackOnByte}) are null or have not been registered.");
			if (CallbackNegotiation == null) throw new ApplicationException($"{CallbackNegotiation} is null and has not been registered.");

			return this;
		}

		private void RegisterInitialWilling(Func<Task> fun)
		{
			_InitialWilling.Add(fun);
		}

		/// <summary>
		/// Interprets the next byte in an asynchronous way.
		/// </summary>
		/// <param name="bt">An integer representation of a byte.</param>
		/// <returns>Awaitable Task</returns>
		public async Task Interpret(byte bt)
		{
			if (Enum.IsDefined(typeof(Trigger), (short)bt))
			{
				await _TelnetStateMachine.FireAsync(BTrigger((Trigger)bt), bt);
			}
			else
			{
				await _TelnetStateMachine.FireAsync(BTrigger(Trigger.ReadNextCharacter), bt);
			}
		}
	}
}
