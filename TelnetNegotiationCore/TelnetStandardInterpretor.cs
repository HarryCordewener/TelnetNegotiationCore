using System;
using System.IO;
using System.Text;
using Stateless;

namespace TelnetNegotiationCore
{
	public partial class TelnetInterpretor
	{
		private readonly StateMachine<State, Trigger> _TelnetStateMachine;
		private BufferedStream _InputStream;
		private byte[] buffer = new byte[5242880];
		private int bufferposition = 0;

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
		}

		/// <summary>
		/// Setup standard processes.
		/// </summary>
		/// <param name="tsm">The state machine.</param>
		private void SetupStandardProtocol(StateMachine<State, Trigger> tsm)
		{
			// If we are in Accepting mode, these should be interpreted as regular characters.
			tsm.Configure(State.Accepting)
				.Permit(Trigger.DO, State.ReadingCharacters)
				.Permit(Trigger.DONT, State.ReadingCharacters)
				.Permit(Trigger.IS, State.ReadingCharacters)
				.Permit(Trigger.NOP, State.ReadingCharacters)
				.Permit(Trigger.SB, State.ReadingCharacters)
				.Permit(Trigger.SE, State.ReadingCharacters)
				.Permit(Trigger.SEND, State.ReadingCharacters)
				.Permit(Trigger.WILL, State.ReadingCharacters)
				.Permit(Trigger.WONT, State.ReadingCharacters)
				.Permit(Trigger.ReadNextCharacter, State.ReadingCharacters);

			// Standard triggers, which are fine in the Awaiting state and should just be interpretted as a character in this state.
			tsm.Configure(State.ReadingCharacters)
				.SubstateOf(State.Accepting)
				.Permit(Trigger.NewLine, State.Act)
				.OnEntryFrom(BTrigger(Trigger.DO), WriteToBufferAndAdvance)
				.OnEntryFrom(BTrigger(Trigger.DONT), WriteToBufferAndAdvance)
				.OnEntryFrom(BTrigger(Trigger.IS), WriteToBufferAndAdvance)
				.OnEntryFrom(BTrigger(Trigger.NOP), WriteToBufferAndAdvance)
				.OnEntryFrom(BTrigger(Trigger.SB), WriteToBufferAndAdvance)
				.OnEntryFrom(BTrigger(Trigger.SE), WriteToBufferAndAdvance)
				.OnEntryFrom(BTrigger(Trigger.SEND), WriteToBufferAndAdvance)
				.OnEntryFrom(BTrigger(Trigger.WILL), WriteToBufferAndAdvance)
				.OnEntryFrom(BTrigger(Trigger.WONT), WriteToBufferAndAdvance)
				.OnEntryFrom(BTrigger(Trigger.ReadNextCharacter), WriteToBufferAndAdvance);

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
		public void RegisterStream(BufferedStream input)
		{
			_InputStream = input;
		}


		private void WriteToBufferAndAdvance(byte b)
		{
			buffer[bufferposition] = b;
			bufferposition++;
		}

		private void WriteToOutput()
		{
			Console.WriteLine(new ASCIIEncoding().GetString(buffer, 0, bufferposition));
			buffer = new byte[5242880];
			bufferposition = 0;
		}

		public void Process()
		{
			int currentByte;
			while ((currentByte = _InputStream.ReadByte()) != -1)
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
