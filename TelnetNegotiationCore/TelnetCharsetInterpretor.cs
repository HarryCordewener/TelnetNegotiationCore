using System;
using System.Text;
using Stateless;

namespace TelnetNegotiationCore
{
	public partial class TelnetInterpretor
	{
		/// <summary>
		/// Internal Charset Byte State
		/// </summary>
		private byte[] _charsetByteState;

		/// <summary>
		/// Internal Charset Byte Index Value
		/// </summary>
		private int _charsetByteIndex = 0;

		/// <summary>
		/// Character set Negotiation will set the Character Set and Character Page Server & Client have agreed to.
		/// </summary>
		/// <param name="tsm"></param>
		private void SetupCharsetNegotiation(StateMachine<State, Trigger> tsm)
		{
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

			tsm.Configure(State.SubNegotiation)
				.Permit(Trigger.CHARSET, State.NegotiatingCharset);

			TriggerHelper.ForAllTriggersButIAC(t => tsm.Configure(State.NegotiatingCharset).Permit(t, State.EvaluatingCharset));

			tsm.Configure(State.EscapingCharsetValue)
				.Permit(Trigger.IAC, State.EvaluatingCharset)
				.Permit(Trigger.SE, State.CompletingCharset);

			tsm.Configure(State.NegotiatingCharset)
				.Permit(Trigger.IAC, State.EscapingCharsetValue)
				.OnEntry(GetCharset);

			tsm.Configure(State.EvaluatingCharset)
				.Permit(Trigger.IAC, State.EscapingCharsetValue);

			TriggerHelper.ForAllTriggers(t => tsm.Configure(State.EvaluatingCharset).OnEntryFrom(BTrigger(t), CaptureCharset));

			tsm.Configure(State.CompletingCharset)
				.OnEntry(CompleteCharset)
				.SubstateOf(State.Accepting);
		}

		/// <summary>
		/// Initialize internal state values for Charset.
		/// </summary>
		/// <param name="_">Ignored</param>
		private void GetCharset(StateMachine<State, Trigger>.Transition _)
		{
			_charsetByteState = new byte[500]; // TODO: Make this configurable
			_charsetByteIndex = 0;
		}

		/// <summary>
		/// Read the Charset state values and finalize it and prepare to respond.
		/// </summary>
		/// <param name="_">Ignored</param>
		private void CaptureCharset(byte b)
		{
			Console.WriteLine(new ASCIIEncoding().GetString(_charsetByteState, 0, _charsetByteIndex));
		}

		/// <summary>
		/// Initialize internal state values for Charset.
		/// </summary>
		/// <param name="_">Ignored</param>
		private void CompleteCharset(StateMachine<State, Trigger>.Transition _)
		{
			_charsetByteState = new byte[500]; // TODO: Make this configurable
			_charsetByteIndex = 0;
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
