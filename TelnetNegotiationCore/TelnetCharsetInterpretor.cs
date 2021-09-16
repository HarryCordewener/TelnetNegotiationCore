using System;
using System.Linq;
using System.Text;
using Stateless;

namespace TelnetNegotiationCore
{
	public partial class TelnetInterpretor
	{
		private Encoding CurrentEncoding = Encoding.GetEncoding("ISO-8859-1");

		/// <summary>
		/// Internal Charset Byte State
		/// </summary>
		private byte[] _charsetByteState;

		/// <summary>
		/// Internal Charset Byte Index Value
		/// </summary>
		private int _charsetByteIndex = 0;

		/// <summary>
		/// Internal Accepted Charset Byte State
		/// </summary>
		private byte[] _acceptedCharsetByteState;

		/// <summary>
		/// Internal Accepted Charset Byte Index Value
		/// </summary>
		private int _acceptedCharsetByteIndex = 0;

		private bool charsetoffered = false;

		private static readonly Lazy<byte[]> SupportedCharacterSets = new Lazy<byte[]>(CharacterSets, true);

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

			tsm.Configure(State.Do)
				.Permit(Trigger.CHARSET, State.DoCharset);

			tsm.Configure(State.Dont)
				.Permit(Trigger.CHARSET, State.DontCharset);

			tsm.Configure(State.WillDoCharset)
				.SubstateOf(State.Accepting)
				.OnEntry(OnWillingCharset);

			tsm.Configure(State.WontDoCharset)
				.SubstateOf(State.Accepting)
				.OnEntry(() => Console.WriteLine("Won't do Character Set - do nothing"));

			tsm.Configure(State.DoCharset)
				.SubstateOf(State.Accepting)
				.OnEntry(OnDoCharset);

			tsm.Configure(State.DontCharset)
				.SubstateOf(State.Accepting)
				.OnEntry(() => Console.WriteLine("Client won't do Character Set - do nothing"));

			tsm.Configure(State.SubNegotiation)
				.Permit(Trigger.CHARSET, State.AlmostNegotiatingCharset);

			tsm.Configure(State.AlmostNegotiatingCharset)
				.Permit(Trigger.SEND, State.NegotiatingCharset)
				.Permit(Trigger.REJECTED, State.EndingCharsetSubnegotiation)
				.Permit(Trigger.ACCEPTED, State.NegotiatingAcceptedCharset);

			tsm.Configure(State.EndingCharsetSubnegotiation)
				.Permit(Trigger.IAC, State.EndSubNegotiation);

			TriggerHelper.ForAllTriggersButIAC(t => tsm.Configure(State.NegotiatingCharset).Permit(t, State.EvaluatingCharset));
			TriggerHelper.ForAllTriggersButIAC(t => tsm.Configure(State.NegotiatingAcceptedCharset).Permit(t, State.EvaluatingAcceptedCharsetValue));

			tsm.Configure(State.EscapingCharsetValue)
				.Permit(Trigger.IAC, State.EvaluatingCharset)
				.Permit(Trigger.SE, State.CompletingCharset);

			tsm.Configure(State.EscapingAcceptedCharsetValue)
				.Permit(Trigger.IAC, State.EvaluatingAcceptedCharsetValue)
				.Permit(Trigger.SE, State.CompletingAcceptedCharset);

			tsm.Configure(State.NegotiatingCharset)
				.Permit(Trigger.IAC, State.EscapingCharsetValue)
				.OnEntry(GetCharset);

			tsm.Configure(State.NegotiatingAcceptedCharset)
				.Permit(Trigger.IAC, State.EscapingAcceptedCharsetValue)
				.OnEntry(GetAcceptedCharset);

			tsm.Configure(State.EvaluatingCharset)
				.Permit(Trigger.IAC, State.EscapingCharsetValue);

			tsm.Configure(State.EvaluatingAcceptedCharsetValue)
				.Permit(Trigger.IAC, State.EscapingAcceptedCharsetValue);

			TriggerHelper.ForAllTriggers(t => tsm.Configure(State.EvaluatingCharset).OnEntryFrom(BTrigger(t), CaptureCharset));

			tsm.Configure(State.CompletingAcceptedCharset)
				.OnEntry(CompleteAcceptedCharset)
				.SubstateOf(State.Accepting);

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
			_charsetByteState = new byte[buffer.Length];
			_charsetByteIndex = 0;
		}

		/// <summary>
		/// Initialize internal state values for Charset.
		/// </summary>
		/// <param name="_">Ignored</param>
		private void GetAcceptedCharset(StateMachine<State, Trigger>.Transition _)
		{
			_acceptedCharsetByteState = new byte[42];
			_acceptedCharsetByteIndex = 0;
		}

		/// <summary>
		/// Read the Charset state values and finalize it and prepare to respond.
		/// </summary>
		/// <param name="_">Ignored</param>
		private void CaptureCharset(byte b)
		{
			_charsetByteState[_charsetByteIndex] = b;
			_charsetByteIndex++;
		}

		/// <summary>
		/// Finalize  internal state values for Charset.
		/// </summary>
		/// <param name="_">Ignored</param>
		private void CompleteCharset(StateMachine<State, Trigger>.Transition _)
		{
			if(charsetoffered)
			{
				_OutputStream.BaseStream.Write(new byte[] { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.CHARSET, (byte)Trigger.REJECTED, (byte)Trigger.IAC, (byte)Trigger.SE });
			}

			// Accept one that we recognize. We pretend [TTABLE] doesn't exist for now.
			char sep = ascii.GetString(_charsetByteState, 0, 1)[0];
			string[] charsetsOffered = ascii.GetString(_charsetByteState, 1, _charsetByteIndex).Split(sep);

			Console.WriteLine(ascii.GetString(_charsetByteState, 0, _charsetByteIndex));
		}

		/// <summary>
		/// Finalize internal state values for Accepted Charset.
		/// </summary>
		/// <param name="_">Ignored</param>
		private void CompleteAcceptedCharset(StateMachine<State, Trigger>.Transition _)
		{
			try
			{
				CurrentEncoding = Encoding.GetEncoding(ascii.GetString(_acceptedCharsetByteState, 0, _acceptedCharsetByteIndex));
				charsetoffered = false;
			}
			catch
			{
				_OutputStream.BaseStream.Write(new byte[] { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.CHARSET, (byte)Trigger.REJECTED, (byte)Trigger.IAC, (byte)Trigger.SE });
			}
		}

		/// <summary>
		/// Announce we do charset negotiation to the client after getting a Willing.
		/// </summary>
		public void OnWillingCharset(StateMachine<State, Trigger>.Transition _)
		{
			_OutputStream.BaseStream.Write(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.CHARSET });
			charsetoffered = false;
		}

		/// <summary>
		/// Announce we do charset negotiation to the client after getting a Willing.
		/// </summary>
		public void WillingCharset()
		{
			Console.WriteLine("Announcing willingness to Charset!");
			_OutputStream.BaseStream.Write(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.CHARSET });
		}

		/// <summary>
		/// Announce we are willing to have charset negotiation to the client after getting a Do.
		/// </summary>
		public void OnDoCharset(StateMachine<State, Trigger>.Transition _)
		{
			_OutputStream.BaseStream.Write(SupportedCharacterSets.Value);
			charsetoffered = true;
		}


		private static byte[] CharacterSets()
		{
			byte[] pre = new byte[] { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.CHARSET, (byte)Trigger.SEND };
			byte[] post = new byte[] { (byte)Trigger.IAC, (byte)Trigger.SE };
			byte[] defaultcharsets = Encoding.ASCII.GetBytes(Encoding.GetEncodings().Select(x => $";{x.Name}").ToString());
			return pre.Concat(defaultcharsets).Concat(post).ToArray();
		}

		/// <summary>
		/// Announce we do charset negotiation to the client.
		/// </summary>
		public void RequestCharset(StateMachine<State, Trigger>.Transition _)
		{
			Console.WriteLine("Request charset negotiation from Client");
			_OutputStream.BaseStream.Write(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.CHARSET });
		}
	}
}
