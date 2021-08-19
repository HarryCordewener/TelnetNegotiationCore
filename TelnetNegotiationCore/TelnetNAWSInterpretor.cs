using System;
using System.IO;
using System.Text;
using Stateless;

namespace TelnetNegotiationCore
{
	public partial class TelnetInterpretor
	{
		/// <summary>
		/// If the server you are connected to makes use of the client window in ways that are linked to its width and height, 
		/// then is useful for it to be able to find out how big it is, and also to get notified when it is resized.
		/// 
		/// NAWS can be initiated from the Client or the Server.
		/// </summary>
		/// <param name="tsm">The state machine.</param>
		private void SetupNAWS(StateMachine<State, Trigger> tsm)
		{

			// NAWS triggers, which are fine in the Awaiting state and should just be interpretted as a character in this state.
			tsm.Configure(State.Accepting)
				.PermitReentry(Trigger.NAWS);

			// Write this character.
			tsm.Configure(State.ReadingCharacters)
				.OnEntryFrom(BTrigger(Trigger.NAWS), WriteToBufferAndAdvance);

			tsm.Configure(State.Willing)
				.Permit(Trigger.NAWS, State.WillDoNAWS);

			tsm.Configure(State.Refusing)
				.Permit(Trigger.NAWS, State.WontDoNAWS);

			tsm.Configure(State.WillDoNAWS)
				.SubstateOf(State.Accepting)
				.OnEntry(RequestNAWS);

			tsm.Configure(State.WontDoNAWS)
				.SubstateOf(State.Accepting)
				.OnEntry(x => Console.WriteLine("Refusing to NAWS"));

			tsm.Configure(State.SubNegotiation)
				.Permit(Trigger.NAWS, State.NegotiatingNAWS);

			tsm.Configure(State.NegotiatingNAWS)
				.Permit(Trigger.IAC, State.EndSubNegotiation)
				.OnEntry(GetNAWS);
		}

		/// <summary>
		/// Request NAWS from a client.
		/// </summary>
		public void RequestNAWS(StateMachine<State, Trigger>.Transition _)
		{
			Console.WriteLine("Requesting NAWS details from Client");
			_InputStream.Write(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.NAWS });
		}

		private void GetNAWS(StateMachine<State, Trigger>.Transition _)
		{
			// TODO: We should check if there is a 255 in here, if so, keep reading.
			
			byte[] width = new byte[2];
			byte[] height = new byte[2];
			_InputStream.Read(width, 0, 2);
			_InputStream.Read(height, 0, 2);

			if (BitConverter.IsLittleEndian)
			{
				Array.Reverse(width);
				Array.Reverse(height);
			}

			Console.WriteLine($"Negotiated for: {BitConverter.ToInt16(width)} width and {BitConverter.ToInt16(height)} height");
		}
	}
}
