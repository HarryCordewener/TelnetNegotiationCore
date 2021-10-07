using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
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
		/// Internal Accepted Charset Byte State
		/// </summary>
		private byte[] _acceptedCharsetByteState;

		/// <summary>
		/// Internal Accepted Charset Byte Index Value
		/// </summary>
		private int _acceptedCharsetByteIndex = 0;

		private bool charsetoffered = false;

		private static readonly Lazy<byte[]> SupportedCharacterSets = new Lazy<byte[]>(CharacterSets, true);

		private static Func<IEnumerable<EncodingInfo>, IOrderedEnumerable<Encoding>> _CharsetOrder = (x) => x.Select(x => x.GetEncoding()).OrderBy(z => z);

		/// <summary>
		/// Character set Negotiation will set the Character Set and Character Page Server & Client have agreed to.
		/// </summary>
		/// <param name="tsm">The state machine.</param>
		/// <returns>Itself</returns>
		private StateMachine<State, Trigger> SetupCharsetNegotiation(StateMachine<State, Trigger> tsm)
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
				.OnEntryAsync(OnWillingCharsetAsync);

			tsm.Configure(State.WontDoCharset)
				.SubstateOf(State.Accepting)
				.OnEntry(() => _Logger.Debug("Connection: {connectionStatus}", "Won't do Character Set - do nothing"));

			tsm.Configure(State.DoCharset)
				.SubstateOf(State.Accepting)
				.OnEntryAsync(OnDoCharsetAsync);

			tsm.Configure(State.DontCharset)
				.SubstateOf(State.Accepting)
				.OnEntry(() => _Logger.Debug("Connection: {connectionStatus}", "Client won't do Character Set - do nothing"));

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
			TriggerHelper.ForAllTriggers(t => tsm.Configure(State.EvaluatingAcceptedCharsetValue).OnEntryFrom(BTrigger(t), CaptureAcceptedCharset));

			TriggerHelper.ForAllTriggersButIAC(t => tsm.Configure(State.EvaluatingCharset).PermitReentry(t));
			TriggerHelper.ForAllTriggersButIAC(t => tsm.Configure(State.EvaluatingAcceptedCharsetValue).PermitReentry(t));

			tsm.Configure(State.CompletingAcceptedCharset)
				.OnEntryAsync(CompleteAcceptedCharsetAsync)
				.SubstateOf(State.Accepting);

			tsm.Configure(State.CompletingCharset)
				.OnEntryAsync(CompleteCharsetAsync)
				.SubstateOf(State.Accepting);

			RegisterInitialWilling(WillingCharsetAsync);

			return tsm;
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
		/// Read the Charset state values and finalize it and prepare to respond.
		/// </summary>
		/// <param name="_">Ignored</param>
		private void CaptureAcceptedCharset(byte b)
		{
			_acceptedCharsetByteState[_acceptedCharsetByteIndex] = b;
			_acceptedCharsetByteIndex++;
		}

		/// <summary>
		/// Finalize internal state values for Charset.
		/// </summary>
		/// <param name="_">Ignored</param>
		private async Task CompleteCharsetAsync(StateMachine<State, Trigger>.Transition _)
		{
			if (charsetoffered)
			{
				await _OutputStream.BaseStream.WriteAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.CHARSET, (byte)Trigger.REJECTED, (byte)Trigger.IAC, (byte)Trigger.SE });
				return;
			}

			char sep = ascii.GetString(_charsetByteState, 0, 1)[0];
			string[] charsetsOffered = ascii.GetString(_charsetByteState, 1, _charsetByteIndex).Split(sep);

			var result = ascii.GetString(_charsetByteState, 0, _charsetByteIndex);
			_Logger.Debug("Charsets offered to us: {@charsetResultDebug}", charsetsOffered);
		}

		/// <summary>
		/// Finalize internal state values for Accepted Charset.
		/// </summary>
		/// <param name="_">Ignored</param>
		private async Task CompleteAcceptedCharsetAsync(StateMachine<State, Trigger>.Transition _)
		{
			try
			{
				_CurrentEncoding = Encoding.GetEncoding(ascii.GetString(_acceptedCharsetByteState, 1, _acceptedCharsetByteIndex-1).Trim());
			}
			catch(Exception ex1)
			{
				_Logger.Warning(ex1, "Potentially expected error during Accepting Charset Negotiation. Seperator not passed back. Trying without seperator.");
				try
				{
					_CurrentEncoding = Encoding.GetEncoding(ascii.GetString(_acceptedCharsetByteState, 0, _acceptedCharsetByteIndex).Trim());
				}
				catch(Exception ex2)
				{
					_Logger.Warning(ex2, "Unexpected error during Accepting Charset Negotiation. Could not find charset: {charset}", ascii.GetString(_acceptedCharsetByteState, 0, _acceptedCharsetByteIndex));
					await _OutputStream.BaseStream.WriteAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.CHARSET, (byte)Trigger.REJECTED, (byte)Trigger.IAC, (byte)Trigger.SE });
				}
			}
			_Logger.Information("Connection: Accepted Charset Negotiation for: {charset}", _CurrentEncoding);
			charsetoffered = false;
		}

		/// <summary>
		/// Announce we do charset negotiation to the client after getting a Willing.
		/// </summary>
		private async Task OnWillingCharsetAsync(StateMachine<State, Trigger>.Transition _)
		{
			_Logger.Debug("Connection: {connectionStatus}", "Request charset negotiation from Client");
			await _OutputStream.BaseStream.WriteAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.CHARSET });
			charsetoffered = false;
		}

		/// <summary>
		/// Announce we do charset negotiation to the client.
		/// </summary>
		private async Task WillingCharsetAsync()
		{
			_Logger.Debug("Connection: {connectionStatus}", "Announcing willingness to Charset!");
			await _OutputStream.BaseStream.WriteAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.CHARSET });
		}

		/// <summary>
		/// Announce the charsets we support to the client after getting a Do.
		/// </summary>
		private async Task OnDoCharsetAsync(StateMachine<State, Trigger>.Transition _)
		{
			_Logger.Debug("Charsets String: {charsetlist}", ";" + string.Join(";", _CharsetOrder(Encoding.GetEncodings()).Select( x=> x.WebName)));
			await _OutputStream.BaseStream.WriteAsync(SupportedCharacterSets.Value);
			charsetoffered = true;
		}

		/// <summary>
		/// Form the Character Set output, based on the system Encodings at the time of connection startup.
		/// </summary>
		/// <returns>A byte array representing the charset offering.</returns>
		private static byte[] CharacterSets()
		{
			byte[] pre = new byte[] { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.CHARSET, (byte)Trigger.SEND };
			byte[] post = new byte[] { (byte)Trigger.IAC, (byte)Trigger.SE };
			byte[] defaultcharsets = Encoding.ASCII.GetBytes(";" + string.Join(";", _CharsetOrder(Encoding.GetEncodings()).Select(x => x.WebName)));
			return pre.Concat(defaultcharsets).Concat(post).ToArray();
		}


		/// <summary>
		/// Register the preferred order of character sets. 
		/// </summary>
		public TelnetInterpretor RegisterCharsetOrder(IEnumerable<Encoding> order)
		{
			var reversed = order.Reverse().ToList();
			_CharsetOrder = (x) => x.Select(x => x.GetEncoding()).OrderByDescending(z => reversed.IndexOf(z));
			return this;
		}
	}
}
