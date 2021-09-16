using System;
using System.IO;
using System.Text;
using Stateless;
using Stateless.Graph;

namespace TelnetNegotiationCore
{
	public partial class TelnetInterpretor
	{

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
		/// Constructor, sets up for standard Telnet protocol with NAWS and Character Set support.
		/// </summary>
		/// <remarks>
		/// After calling this constructor, one should subscribe to the Triggers, register a Stream, and then run Process()
		/// </remarks>
		public TelnetInterpretor()
		{
			_TelnetStateMachine = new StateMachine<State, Trigger>(State.Accepting);
			SetupStandardProtocol(_TelnetStateMachine);
			SetupNAWS(_TelnetStateMachine);
			SetupCharsetNegotiation(_TelnetStateMachine);

			_TelnetStateMachine.OnTransitioned((transition) => { Console.WriteLine($"{transition.Source} --[{transition.Trigger}]--> {transition.Destination}"); });
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
				.Permit(Trigger.NewLine, State.Act);

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
				.OnEntry(x => Console.WriteLine("Starting Negotiation"));

			tsm.Configure(State.Willing)
				.OnEntry(x => Console.WriteLine("Willing"));

			tsm.Configure(State.Refusing)
				.OnEntry(x => Console.WriteLine("Refusing"));

			tsm.Configure(State.ReadingCharacters)
				.OnEntryFrom(Trigger.IAC, x => Console.WriteLine("Canceling negotation"));

			tsm.Configure(State.SubNegotiation)
				.OnEntryFrom(Trigger.IAC, x => Console.WriteLine("Subnegotiation request"));
			
			tsm.Configure(State.EndSubNegotiation)
				.Permit(Trigger.SE, State.Accepting);
		}

		/// <summary>
		/// Register the Buffered Stream to read and write to for Telnet negotiation.
		/// </summary>
		/// <param name="input">A Buffered Stream wrapped around a Network Stream</param>
		public void RegisterStream(StreamReader input, StreamWriter output)
		{
			_InputStream = input;
			_OutputStream = output;
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
		private void WriteToOutput()
		{
			// We use ASCII Encoding here until we negotiate for UTF-8.
			// How do we want to split out ASCII vs UNICODE if we get mid-session negotiation?
			Console.WriteLine(ascii.GetString(buffer, 0, bufferposition));
			bufferposition = 0;
		}

		/// <summary>
		/// Start processing the inbound stream.
		/// </summary>
		public void Process()
		{
			int currentByte;
			WillingCharset();
			WillingNAWS();

			while ((currentByte = _InputStream.BaseStream.ReadByte() ) != -1)
			{
				if (Enum.IsDefined(typeof(Trigger), currentByte))
				{
					_TelnetStateMachine.Fire(BTrigger((Trigger)currentByte), (byte)currentByte);
					continue;
				}
				_TelnetStateMachine.Fire(BTrigger(Trigger.ReadNextCharacter), (byte)currentByte);
			}

			Console.WriteLine("Connection Closed");
		}
	}
}
