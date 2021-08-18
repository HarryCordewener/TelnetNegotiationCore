using System;
using System.IO;
using System.Text;
using Stateless;

namespace TelnetNegotiationCore
{
	public class TelnetInterpretor
	{
		private readonly StateMachine<State, Trigger> _TelnetStateMachine;
		private BufferedStream _InputStream;
		private byte[] buffer = new byte[50000];
		private int bufferposition = 0;
		private int currentByte;

		#region Setup
		public TelnetInterpretor()
		{
			_TelnetStateMachine = new StateMachine<State, Trigger>(State.Accepting);
			SetupStandardProtocol(_TelnetStateMachine);
			SetupNAWS(_TelnetStateMachine);
		}

		private void SetupStandardProtocol(StateMachine<State, Trigger> tsm)
		{
			tsm.Configure(State.Accepting)
				.Permit(Trigger.CHARSET, State.ReadingCharacters)
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
				.Permit(Trigger.CR, State.Act)
				.OnEntry(WriteToBufferAndAdvance);

			tsm.Configure(State.Act)
				.SubstateOf(State.Accepting)
				.OnEntry(WriteToOutput);
		}

		public void RegisterStream(BufferedStream input)
		{
			_InputStream = input;
		}

		/// <summary>
		/// If the server you are connected to makes use of the client window in ways that are linked to its width and height, 
		/// then is useful for it to be able to find out how big it is, and also to get notified when it is resized.
		/// 
		/// NAWS can be initiated from the Client or the Server.
		/// </summary>
		/// <param name="tsm"></param>
		private void SetupNAWS(StateMachine<State, Trigger> tsm)
		{
			tsm.Configure(State.Accepting)
				.Permit(Trigger.IAC, State.StartNegotiation);

			// NAWS triggers, which are fine in the Awaiting state and should just be interpretted as a character in this state.
			tsm.Configure(State.Accepting)
				.PermitReentry(Trigger.CHARSET)
				.PermitReentry(Trigger.NAWS);

			// Escaped IAC, interpret as actual IAC
			tsm.Configure(State.StartNegotiation)
				.Permit(Trigger.IAC, State.ReadingCharacters)
				.Permit(Trigger.WILL, State.Willing)
				.Permit(Trigger.WONT, State.Refusing)
				.Permit(Trigger.SB, State.SubNegotiation)
				.OnEntry(x => Console.WriteLine("Starting Negotiation"));

			tsm.Configure(State.Willing)
				.Permit(Trigger.NAWS, State.WillDoNAWS)
				.OnEntry(x => Console.WriteLine("Willing"));

			tsm.Configure(State.Refusing)
				.Permit(Trigger.NAWS, State.WontDoNAWS)
				.OnEntry(x => Console.WriteLine("Refusing"));

			tsm.Configure(State.WillDoNAWS)
				.SubstateOf(State.Accepting)
				.OnEntry(x => RequestNAWS());

			tsm.Configure(State.WontDoNAWS)
				.SubstateOf(State.Accepting)
				.OnEntry(x => Console.WriteLine("Refusing to NAWS"));

			tsm.Configure(State.ReadingCharacters)
				.OnEntryFrom(Trigger.IAC, x => Console.WriteLine("Canceling negotation"));

			tsm.Configure(State.SubNegotiation)
				.Permit(Trigger.NAWS, State.NegotiatingNAWS)
				.OnEntryFrom(Trigger.IAC, x => Console.WriteLine("Subnegotiation request"));

			tsm.Configure(State.NegotiatingNAWS)
				.Permit(Trigger.IAC, State.EndSubNegotiation)
				.OnEntry( x => GetNAWS());

			tsm.Configure(State.EndSubNegotiation)
				.Permit(Trigger.SE, State.Accepting);
		}
		#endregion Setup

		private void WriteToBufferAndAdvance()
		{
			buffer[bufferposition] = (byte)currentByte;
			bufferposition++;
		}

		private void WriteToOutput()
		{
			Console.WriteLine(new ASCIIEncoding().GetString(buffer,0,bufferposition));
			buffer = new byte[50000];
			bufferposition = 0;
		}

		public void RequestNAWS()
		{
			_InputStream.Write(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.NAWS });
		}

		private void GetNAWS()
		{
			byte[] width = new byte[2];
			byte[] height = new byte[2];
			_InputStream.Read(width, 0, 2);
			_InputStream.Read(height, 0, 2);

			if(BitConverter.IsLittleEndian)
			{ 
				Array.Reverse(width);
				Array.Reverse(height);
			}

			Console.WriteLine($"Negotiated for: {BitConverter.ToInt16(width)} width and {BitConverter.ToInt16(height)} height");
		}

		public void Process()
		{
			while ((currentByte = _InputStream.ReadByte()) != -1)
			{
				if(Enum.IsDefined(typeof(Trigger), currentByte))
				{
					_TelnetStateMachine.Fire((Trigger)currentByte);
					continue;
				}
				_TelnetStateMachine.Fire(Trigger.ReadNextCharacter);
			}

			Console.WriteLine(buffer);
		}
	}
}
