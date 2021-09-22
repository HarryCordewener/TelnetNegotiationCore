using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Serilog;
using Serilog.Context;
using Stateless;

namespace TelnetNegotiationCore
{
	public partial class TelnetInterpretor
	{
		/// <summary>
		/// A list of functions to call at the start.
		/// </summary>
		private List<Func<Task>> _InitialWilling;

		/// <summary>
		/// The current Encoding used for interpretting incoming non-negotiation text, and what we should send on outbound.
		/// </summary>
		private Encoding _CurrentEncoding = Encoding.GetEncoding("ISO-8859-1");

		/// <summary>
		/// Telnet state machine
		/// </summary>
		private readonly StateMachine<State, Trigger> _TelnetStateMachine;

		/// <summary>
		/// Input Stream
		/// </summary>
		private StreamReader _InputStream;

		/// <summary>
		/// Output Stream
		/// </summary>
		private StreamWriter _OutputStream;

		private Func<string, Task> _WriteBack;

		/// <summary>
		/// Identifier for the connection.
		/// </summary>
		private string _Identifier;

		/// <summary>
		/// Local buffer. We only take up to 5mb in buffer space. 
		/// </summary>
		private byte[] buffer = new byte[5242880];

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
			SetupStandardProtocol(_TelnetStateMachine);
			SetupNAWS(_TelnetStateMachine);
			SetupCharsetNegotiation(_TelnetStateMachine);
			SetupTelnetTerminalType(_TelnetStateMachine);
			// SetupIgnored(_TelnetStateMachine);
			SetupSafeNegotiation(_TelnetStateMachine);

			if (_Logger.IsEnabled(Serilog.Events.LogEventLevel.Verbose))
			{
				_TelnetStateMachine.OnTransitioned((transition) => _Logger.Verbose("{source} --[{trigger}]--> {destination}",
					transition.Source, transition.Trigger, transition.Destination));
			}
		}

		/// <summary>
		/// Setup standard processes.
		/// </summary>
		/// <param name="tsm">The state machine.</param>
		private void SetupStandardProtocol(StateMachine<State, Trigger> tsm)
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
				.OnEntry(x => _Logger.Debug("{connectionState}", "Starting Negotiation"));

			tsm.Configure(State.Willing);

			tsm.Configure(State.Refusing);

			tsm.Configure(State.ReadingCharacters)
				.OnEntryFrom(Trigger.IAC, x => _Logger.Debug("{connectionState}", "Canceling negotation"));

			tsm.Configure(State.SubNegotiation)
				.OnEntryFrom(Trigger.IAC, x => _Logger.Debug("{connectionState}", "Subnegotiation request"));

			tsm.Configure(State.EndSubNegotiation)
				.Permit(Trigger.SE, State.Accepting);
		}

		/// <summary>
		/// Register the Buffered Stream to read and write to for Telnet negotiation.
		/// </summary>
		/// <param name="input">A StreamReader wrapped around a Network Stream</param>
		/// <param name="output">A StreamWriter wrapped around a Network Stream</param>
		/// <param name="identifier">An identifier for the client stream. Doesn't need to be unique, but is used for logging.</param>
		public void RegisterStream(StreamReader input, StreamWriter output, string identifier = null)
		{
			_Identifier = identifier ?? Guid.NewGuid().ToString();
			LogContext.PushProperty("identifier", _Identifier);

			_InputStream = input;
			_OutputStream = output;
		}

		public void RegisterWriteBack(Func<string, Task> wb)
		{
			_WriteBack = wb;
		}

		/// <summary>
		/// Write the character into a buffer.
		/// </summary>
		/// <param name="b"></param>
		private void WriteToBufferAndAdvance(byte b)
		{
			buffer[bufferposition] = b;
			bufferposition++;
		}

		/// <summary>
		/// Write it to output - this should become an Event.
		/// </summary>
		/// <remarks>
		/// TODO: 
		/// We use ASCII Encoding here until we negotiate for UTF-8.
		/// How do we want to split out ASCII vs UNICODE if we get mid-session negotiation?
		/// </remarks>
		private void WriteToOutput() => _WriteBack?.Invoke(_CurrentEncoding.GetString(buffer, 0, bufferposition));

		/// <summary>
		/// Validates the object is ready to process.
		/// </summary>
		private void Validate()
		{
			if (_WriteBack == null) throw new ApplicationException("Writeback Function is null or has not been registered.");
			if (_InputStream == null) throw new ApplicationException("Input Stream is null or has not been registered.");
			if (_OutputStream == null) throw new ApplicationException("Output Stream is null or has not been registered.");
		}

		private void RegisterInitialWilling(Func<Task> fun)
		{
			_InitialWilling.Add(fun);
		}

		/// <summary>
		/// Start processing the inbound stream.
		/// </summary>
		public async Task ProcessAsync()
		{
			_Logger.Information("{serverStatus}", "Connected");

			Validate();

			foreach (var t in _InitialWilling)
			{
				await t();
			}

			int currentByte;

			while ((currentByte = _InputStream.BaseStream.ReadByte()) != -1)
			{
				if (Enum.IsDefined(typeof(Trigger), currentByte))
				{
					await _TelnetStateMachine.FireAsync(BTrigger((Trigger)currentByte), (byte)currentByte);
					continue;
				}
				await _TelnetStateMachine.FireAsync(BTrigger(Trigger.ReadNextCharacter), (byte)currentByte);
			}

			_Logger.Information("{serverStatus}", "Connection Closed");
		}
	}
}
