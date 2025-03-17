using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OneOf;
using Stateless;
using TelnetNegotiationCore.Models;

namespace TelnetNegotiationCore.Interpreters;

/// <summary>
/// Implements RFC 2066: 
///
/// RFC 2066: http://www.faqs.org/rfcs/rfc2066.html
/// </summary>
public partial class TelnetInterpreter
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

	private bool _charsetOffered = false;

	private Func<IEnumerable<EncodingInfo>> AllowedEncodings { get; set; } = Encoding.GetEncodings;

	private readonly Func<IEnumerable<EncodingInfo>, IOrderedEnumerable<Encoding>> _charsetOrder = x 
		=> x.Select(y => y.GetEncoding()).OrderBy(z => z.EncodingName);

	private Func<Encoding, ValueTask> SignalCharsetChangeAsync { get; set; }

	public Lazy<byte[]> SupportedCharacterSets { get; }

	/// <summary>
	/// Sets the CharacterSet Order
	/// </summary>
	/// <exception cref="ArgumentOutOfRangeException">codepage is less than zero or greater than 65535.</exception>
	/// <exception cref="ArgumentException">codepage is not supported by the underlying platform.</exception>
	/// <exception cref="NotSupportedException">codepage is not supported by the underlying platform.</exception>
	public IEnumerable<Encoding> CharsetOrder
	{
		init
		{
			var ordered = value.Reverse().ToList();
			_charsetOrder = x => x.Select(y => y.GetEncoding()).OrderByDescending(z => ordered.IndexOf(z));
		}
	}

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
			.OnEntryAsync(async x => await OnWillingCharsetAsync(x));

		tsm.Configure(State.WontDoCharset)
			.SubstateOf(State.Accepting)
			.OnEntry(() => _logger.LogDebug("Connection: {ConnectionState}", "Won't do Character Set - do nothing"));

		tsm.Configure(State.DoCharset)
			.SubstateOf(State.Accepting)
			.OnEntryAsync(async x => await OnDoCharsetAsync(x));

		tsm.Configure(State.DontCharset)
			.SubstateOf(State.Accepting)
			.OnEntry(() => _logger.LogDebug("Connection: {ConnectionState}", "Client won't do Character Set - do nothing"));

		tsm.Configure(State.SubNegotiation)
			.Permit(Trigger.CHARSET, State.AlmostNegotiatingCharset);

		tsm.Configure(State.AlmostNegotiatingCharset)
			.Permit(Trigger.REQUEST, State.NegotiatingCharset)
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

		TriggerHelper.ForAllTriggers(t => tsm.Configure(State.EvaluatingCharset).OnEntryFrom(ParameterizedTrigger(t), CaptureCharset));
		TriggerHelper.ForAllTriggers(t => tsm.Configure(State.EvaluatingAcceptedCharsetValue).OnEntryFrom(ParameterizedTrigger(t), CaptureAcceptedCharset));

		TriggerHelper.ForAllTriggersButIAC(t => tsm.Configure(State.EvaluatingCharset).PermitReentry(t));
		TriggerHelper.ForAllTriggersButIAC(t => tsm.Configure(State.EvaluatingAcceptedCharsetValue).PermitReentry(t));

		tsm.Configure(State.CompletingAcceptedCharset)
			.OnEntryAsync(async x => await CompleteAcceptedCharsetAsync(x))
			.SubstateOf(State.Accepting);

		tsm.Configure(State.CompletingCharset)
			.OnEntryAsync(async x => await CompleteCharsetAsync(x))
			.SubstateOf(State.Accepting);

		RegisterInitialWilling(async () => await WillingCharsetAsync());

		return tsm;
	}

	/// <summary>
	/// Initialize internal state values for Charset.
	/// </summary>
	/// <param name="_">Ignored</param>
	private void GetCharset(StateMachine<State, Trigger>.Transition _)
	{
		_charsetByteState = new byte[_buffer.Length];
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
	/// <param name="b">Byte</param>
	private void CaptureCharset(OneOf<byte, Trigger> b)
	{
		if (_charsetByteIndex > _charsetByteState.Length) return;
		_charsetByteState[_charsetByteIndex] = b.AsT0;
		_charsetByteIndex++;
	}

	/// <summary>
	/// Read the Charset state values and finalize it and prepare to respond.
	/// </summary>
	/// <param name="b"></param>
	private void CaptureAcceptedCharset(OneOf<byte, Trigger> b)
	{
		_acceptedCharsetByteState[_acceptedCharsetByteIndex] = b.AsT0;
		_acceptedCharsetByteIndex++;
	}

	/// <summary>
	/// Finalize internal state values for Charset.
	/// </summary>
	/// <param name="_">Ignored</param>
	private async ValueTask CompleteCharsetAsync(StateMachine<State, Trigger>.Transition _)
	{
		if (_charsetOffered && Mode == TelnetMode.Server)
		{
			await CallbackNegotiationAsync([(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.CHARSET, (byte)Trigger.REJECTED, (byte)Trigger.IAC, (byte)Trigger.SE]);
			return;
		}

		var sep = ascii.GetString(_charsetByteState, 0, 1)?[0];
		var charsetsOffered = ascii.GetString(_charsetByteState, 1, _charsetByteIndex - 1).Split(sep ?? ' ');

		_logger.LogDebug("Charsets offered to us: {@charsetResultDebug}", charsetsOffered);

		var encodingDict = AllowedEncodings().ToDictionary(x => x.GetEncoding().WebName);
		var offeredEncodingInfo = charsetsOffered.Select(x => { try { return encodingDict[Encoding.GetEncoding(x).WebName]; } catch { return null; } }).Where(x => x != null);
		var preferredEncoding = _charsetOrder(offeredEncodingInfo);
		var chosenEncoding = preferredEncoding.FirstOrDefault();

		if (chosenEncoding == null)
		{
			await CallbackNegotiationAsync([(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.CHARSET, (byte)Trigger.REJECTED, (byte)Trigger.IAC, (byte)Trigger.SE]);
			return;
		}

		_logger.LogDebug("Charsets chosen by us: {@charsetWebName} (CP: {@cp})", chosenEncoding.WebName, chosenEncoding.CodePage);

		byte[] preamble = [(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.CHARSET, (byte)Trigger.ACCEPTED];
		byte[] charsetAscii = ascii.GetBytes(chosenEncoding.WebName);
		byte[] postAmble = [ (byte)Trigger.IAC, (byte)Trigger.SE ];

		CurrentEncoding = chosenEncoding;
		await (SignalCharsetChangeAsync?.Invoke(CurrentEncoding) ?? ValueTask.CompletedTask);

		// TODO: The implementing Server or Client needs to be warned when CurrentEncoding is set!
		// This would allow, for instance, the Console to ensure it displays Unicode correctly.

		await CallbackNegotiationAsync([.. preamble, .. charsetAscii, .. postAmble]);
	}

	/// <summary>
	/// Finalize internal state values for Accepted Charset.
	/// </summary>
	/// <param name="_">Ignored</param>
	private async ValueTask CompleteAcceptedCharsetAsync(StateMachine<State, Trigger>.Transition _)
	{
		try
		{
			CurrentEncoding = Encoding.GetEncoding(ascii.GetString(_acceptedCharsetByteState, 0, _acceptedCharsetByteIndex).Trim());
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Unexpected error during Accepting Charset Negotiation. Could not find charset: {charset}", ascii.GetString(_acceptedCharsetByteState, 0, _acceptedCharsetByteIndex));
			await CallbackNegotiationAsync([(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.CHARSET, (byte)Trigger.REJECTED, (byte)Trigger.IAC, (byte)Trigger.SE]);
		}
		_logger.LogInformation("Connection: Accepted Charset Negotiation for: {charset}", CurrentEncoding.WebName);
		_charsetOffered = false;
	}

	/// <summary>
	/// Announce we do charset negotiation to the client after getting a Willing.
	/// </summary>
	private async ValueTask OnWillingCharsetAsync(StateMachine<State, Trigger>.Transition _)
	{
		_logger.LogDebug("Connection: {ConnectionState}", "Request charset negotiation from Client");
		await CallbackNegotiationAsync([(byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.CHARSET]);
		_charsetOffered = false;
	}

	/// <summary>
	/// Announce we do charset negotiation to the client.
	/// </summary>
	private async ValueTask WillingCharsetAsync()
	{
		_logger.LogDebug("Connection: {ConnectionState}", "Announcing willingness to Charset!");
		await CallbackNegotiationAsync([(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.CHARSET]);
	}

	/// <summary>
	/// Announce the charsets we support to the client after getting a Do.
	/// </summary>
	private async ValueTask OnDoCharsetAsync(StateMachine<State, Trigger>.Transition _)
	{
		_logger.LogDebug("Charsets String: {CharsetList}", ";" + string.Join(";", _charsetOrder(AllowedEncodings()).Select(x => x.WebName)));
		await CallbackNegotiationAsync(SupportedCharacterSets.Value);
		_charsetOffered = true;
	}

	/// <summary>
	/// Form the Character Set output, based on the system Encodings at the time of connection startup.
	/// </summary>
	/// <returns>A byte array representing the charset offering.</returns>
	private byte[] CharacterSets()
	{
		return [(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.CHARSET, (byte)Trigger.REQUEST,
						.. ascii.GetBytes($";{string.Join(";", _charsetOrder(AllowedEncodings()).Select(x => x.WebName))}"),
						(byte)Trigger.IAC, (byte)Trigger.SE];
	}
}
