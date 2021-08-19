using System;
using System.IO;
using System.Text;
using Stateless;

namespace TelnetNegotiationCore
{
	public partial class TelnetInterpretor
	{
		/// <summary>
		/// Character set Negotiation will set the Character Set and Character Page Server & Client have agreed to.
		/// </summary>
		/// <param name="tsm"></param>
		private void SetupCharsetNegotiation(StateMachine<State, Trigger> tsm)
		{
			tsm.Configure(State.Accepting)
				.PermitReentry(Trigger.CHARSET);

			tsm.Configure(State.ReadingCharacters)
				.OnEntryFrom(BTrigger(Trigger.CHARSET), WriteToBufferAndAdvance);

			tsm.Configure(State.Willing)
				.Permit(Trigger.CHARSET, State.WillDoCharset);

			tsm.Configure(State.Refusing)
				.Permit(Trigger.CHARSET, State.WontDoCharset);

			tsm.Configure(State.WillDoCharset)
				.SubstateOf(State.Accepting)
				.OnEntry(WillDoCharset);

			tsm.Configure(State.WontDoCharset)
				.SubstateOf(State.Accepting)
				.OnEntry(() => Console.WriteLine("Won't do Character Set - do nothing"));

		}

		/// <summary>
		/// Announce we do charset negotiation to the client.
		/// </summary>
		public void WillDoCharset(StateMachine<State, Trigger>.Transition _)
		{
			Console.WriteLine("Announcing we do charset negotiation to Client");
			_InputStream.Write(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.CHARSET });
		}

		/// <summary>
		/// Announce we do charset negotiation to the client.
		/// </summary>
		public void RequestCharset(StateMachine<State, Trigger>.Transition _)
		{
			Console.WriteLine("Request charset negotiation from Client");
			_InputStream.Write(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.CHARSET });
		}
	}
}
