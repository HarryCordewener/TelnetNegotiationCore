using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Plugins;

namespace TelnetNegotiationCore.Protocols;

/// <summary>
/// The plaintext MSSP transport: the same report as telnet option 70, carried as ordinary text.
/// </summary>
/// <remarks>
/// <para>
/// A client sends the literal line <c>MSSP-REQUEST</c> at the connect screen and the server answers
/// with a leading CRLF, a start marker, tab-separated <c>name&lt;TAB&gt;value</c> lines, and an end
/// marker:
/// </para>
/// <code>
/// \r\nMSSP-REPLY-START\r\n
/// NAME&lt;TAB&gt;Some MUD\r\n
/// PLAYERS&lt;TAB&gt;4\r\n
/// MSSP-REPLY-END\r\n
/// </code>
/// <para>
/// The vocabulary is identical to the subnegotiation form -- multi-word official names included --
/// so <see cref="MSSPConfig"/>, <see cref="MSSPVariables"/> and <see cref="MSSPVariableCollection"/>
/// are reused wholesale; only the framing and the field split differ.
/// </para>
/// <para>
/// <b>Specification status, stated plainly.</b> This form is not described on the current
/// specification page at <see href="https://tintin.mudhalla.net/protocols/mssp/">mudhalla.net</see>,
/// but that page's own changelog records "Mar 20, 2009 - Plaintext version of MSSP finalized and
/// added to specification", and it is implemented across the SMAUG family and read by Grapevine's
/// crawler. It is more widely deployed than it is currently documented.
/// </para>
/// <para>
/// <b>Opt-in, and off by default.</b> Unlike <c>IAC DO 70</c>, which a server that does not implement
/// MSSP ignores, this puts real text on the wire: a server without the plaintext form treats
/// <c>MSSP-REQUEST</c> as input at its login prompt. A crawler wants that trade; an interactive
/// client must not make it by accident.
/// </para>
/// </remarks>
public partial class MSSPProtocol
{
	/// <summary>The line a client sends to ask for a report.</summary>
	private const string RequestLine = "MSSP-REQUEST";

	/// <summary>The line that opens a reply.</summary>
	private const string ReplyStart = "MSSP-REPLY-START";

	/// <summary>The line that closes a reply.</summary>
	private const string ReplyEnd = "MSSP-REPLY-END";

	/// <summary>
	/// How long after the connection is built a client waits before sending <c>MSSP-REQUEST</c>:
	/// 10 seconds, matching Grapevine's crawler.
	/// </summary>
	/// <remarks>
	/// The wait is what gives the telnet option its chance to answer first, and it also lets the
	/// server finish printing whatever banner it prints before the word arrives at its prompt.
	/// </remarks>
	public static readonly TimeSpan DefaultPlaintextRequestDelay = TimeSpan.FromSeconds(10);

	/// <summary>
	/// How long a client waits for <c>MSSP-REPLY-END</c> after sending the request before giving up:
	/// 10 seconds. Grapevine stops the whole attempt 20 seconds after connecting, having sent the
	/// request at 10.
	/// </summary>
	public static readonly TimeSpan DefaultPlaintextReplyTimeout = TimeSpan.FromSeconds(10);

	private bool _plaintextFallback;
	private TimeSpan _plaintextRequestDelay = DefaultPlaintextRequestDelay;
	private TimeSpan _plaintextReplyTimeout = DefaultPlaintextReplyTimeout;
	private Func<ValueTask>? _onPlaintextTimeout;

	/// <summary>Variables collected from the reply currently being read.</summary>
	private readonly MSSPVariableCollection _plaintextReceived = new();

	/// <summary>True between <c>MSSP-REPLY-START</c> and the end of the attempt.</summary>
	private bool _plaintextCollecting;

	/// <summary>
	/// The bytes of the field lines collected so far, counted whether or not they were kept. This is
	/// the whole ceiling mechanism: past <see cref="MaxMessageSize"/> nothing more is parsed and what
	/// was parsed is released, so an unterminated reply cannot grow the process.
	/// </summary>
	private long _plaintextBytes;

	private bool _plaintextOverflowed;

	/// <summary>
	/// True once the attempt has ended, one way or another. It is made once per connection.
	/// </summary>
	/// <remarks>
	/// Written from the byte-processing loop (a reply arrived, or the option answered) and from the
	/// timer task (it gave up), hence <see langword="volatile"/> for the unlocked reads.
	/// </remarks>
	private volatile bool _plaintextFinished;

	/// <summary>
	/// Guards the collection state, which the byte-processing loop and the timer task both end.
	/// Without it a timeout landing mid-line could clear the variable collection while a field was
	/// being added to it.
	/// </summary>
	private readonly object _plaintextLock = new();

	private CancellationTokenSource? _plaintextCts;
	private Task? _plaintextSchedule;

	/// <summary>
	/// Whether this connection also speaks MSSP as plaintext. <see langword="false"/> by default.
	/// </summary>
	/// <remarks>
	/// In client mode this sends <c>MSSP-REQUEST</c> once, <see cref="PlaintextRequestDelay"/> after
	/// the connection is built and only if the telnet option has not already answered, and reads the
	/// reply into <c>OnMSSP</c> with <see cref="MSSPConfig.Source"/> set to
	/// <see cref="MSSPSource.Plaintext"/>. In server mode it answers an incoming <c>MSSP-REQUEST</c>
	/// line (case-insensitively, as SMAUG's <c>str_cmp</c> does) and consumes that line rather than
	/// passing it to the host application as input.
	/// <para>
	/// It is off by default because it is observable: the request is text at a login prompt, and a
	/// server that does not implement the form will answer it the way it answers any other unknown
	/// word. Answering it, on a server, likewise means <c>MSSP-REQUEST</c> stops being usable as a
	/// login name.
	/// </para>
	/// </remarks>
	public bool PlaintextFallback
	{
		get => _plaintextFallback;
		set => _plaintextFallback = value;
	}

	/// <summary>
	/// Enables (or disables) the plaintext transport in a fluent manner. See
	/// <see cref="PlaintextFallback"/>.
	/// </summary>
	/// <param name="enabled">Whether to speak plaintext MSSP</param>
	/// <returns>This instance for fluent chaining</returns>
	public MSSPProtocol WithPlaintextFallback(bool enabled = true)
	{
		PlaintextFallback = enabled;
		return this;
	}

	/// <summary>
	/// How long a client waits after the connection is built before sending <c>MSSP-REQUEST</c>.
	/// Defaults to <see cref="DefaultPlaintextRequestDelay"/>.
	/// </summary>
	/// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
	public TimeSpan PlaintextRequestDelay
	{
		get => _plaintextRequestDelay;
		set => _plaintextRequestDelay = Positive(value, nameof(value));
	}

	/// <summary>
	/// Sets <see cref="PlaintextRequestDelay"/> in a fluent manner.
	/// </summary>
	/// <param name="delay">How long to wait before sending the request</param>
	/// <returns>This instance for fluent chaining</returns>
	public MSSPProtocol WithPlaintextRequestDelay(TimeSpan delay)
	{
		PlaintextRequestDelay = delay;
		return this;
	}

	/// <summary>
	/// How long a client waits for <c>MSSP-REPLY-END</c> after sending the request before giving up.
	/// Defaults to <see cref="DefaultPlaintextReplyTimeout"/>.
	/// </summary>
	/// <remarks>
	/// A size cap alone is not enough here: a server that ignores the request never terminates the
	/// read, and one that starts a reply is not obliged to finish it. When this elapses the collected
	/// bytes are released, <see cref="OnPlaintextMSSPTimeout"/> fires, and the attempt is over -- a
	/// half-collected report is never delivered.
	/// </remarks>
	/// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
	public TimeSpan PlaintextReplyTimeout
	{
		get => _plaintextReplyTimeout;
		set => _plaintextReplyTimeout = Positive(value, nameof(value));
	}

	/// <summary>
	/// Sets <see cref="PlaintextReplyTimeout"/> in a fluent manner.
	/// </summary>
	/// <param name="timeout">How long to wait for the end of a reply</param>
	/// <returns>This instance for fluent chaining</returns>
	public MSSPProtocol WithPlaintextReplyTimeout(TimeSpan timeout)
	{
		PlaintextReplyTimeout = timeout;
		return this;
	}

	/// <summary>
	/// Sets the callback invoked when a plaintext request goes unanswered for
	/// <see cref="PlaintextReplyTimeout"/>. This is how a caller tells "the server has no plaintext
	/// MSSP" apart from "the report has not arrived yet".
	/// </summary>
	/// <param name="callback">The callback to invoke when the attempt is given up on</param>
	/// <returns>This instance for fluent chaining</returns>
	public MSSPProtocol OnPlaintextMSSPTimeout(Func<ValueTask>? callback)
	{
		_onPlaintextTimeout = callback;
		return this;
	}

	private static TimeSpan Positive(TimeSpan value, string parameterName)
	{
		if (value <= TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(parameterName, value, "Duration must be positive.");
		}

		return value;
	}

	/// <summary>
	/// Hooks the assembled-line path, and in client mode arms the request timer. Called from
	/// <c>ConfigureStateMachine</c>; does nothing at all unless <see cref="PlaintextFallback"/> is set,
	/// so a consumer that has not opted in carries none of this.
	/// </summary>
	private void ConfigurePlaintextTransport(IProtocolContext context)
	{
		if (!_plaintextFallback) return;

		context.Interpreter.RegisterInputLineObserver((line, encoding) => OnInputLineAsync(line, encoding, context));

		if (context.Mode != Interpreters.TelnetInterpreter.TelnetMode.Client) return;

		context.RegisterInitialNegotiation(() =>
		{
			StartPlaintextSchedule(context);
			return default;
		});
	}

	#region Reading a reply

	/// <summary>
	/// One assembled line of ordinary input. Returns true when the line belongs to MSSP and must not
	/// reach the host application.
	/// </summary>
	/// <remarks>
	/// The markers are matched as whole lines rather than as substrings of a receive buffer, which is
	/// how Grapevine detects a reply (<c>string =~ "MSSP-REPLY-START"</c>). On a MUD, where people
	/// type things on purpose, a substring match is trippable by saying the words out loud.
	/// </remarks>
	private async ValueTask<bool> OnInputLineAsync(byte[] line, Encoding encoding, IProtocolContext context)
	{
		if (!_plaintextFallback || !IsEnabled) return false;

		var text = encoding.GetString(line);

		if (context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Server)
		{
			if (!text.Trim().Equals(RequestLine, StringComparison.OrdinalIgnoreCase)) return false;

			await SendPlaintextReportAsync(context);
			return true;
		}

		if (_plaintextFinished) return false;

		if (!_plaintextCollecting)
		{
			if (!text.Trim().Equals(ReplyStart, StringComparison.OrdinalIgnoreCase)) return false;

			lock (_plaintextLock)
			{
				if (_plaintextFinished) return false;

				_plaintextCollecting = true;
			}

			context.Logger.LogDebug("Plaintext MSSP reply started");
			return true;
		}

		if (text.Trim().Equals(ReplyEnd, StringComparison.OrdinalIgnoreCase))
		{
			await CompletePlaintextReplyAsync(context);
			return true;
		}

		CollectPlaintextField(text, line.Length, context);
		return true;
	}

	/// <summary>
	/// Records one <c>name&lt;TAB&gt;value</c> line, and keeps the running byte count that bounds the
	/// reply.
	/// </summary>
	/// <remarks>
	/// The split is on the first tab and nothing else. Official variable names contain spaces --
	/// <c>MINIMUM AGE</c>, <c>PAY TO PLAY</c>, <c>XTERM 256 COLORS</c> -- and values contain them too,
	/// so any whitespace-based split would tear both apart. A line with no tab is not a field: SMAUG
	/// never writes one, and a decorative separator must not become a variable.
	/// </remarks>
	private void CollectPlaintextField(string text, int byteCount, IProtocolContext context)
	{
		var separator = text.IndexOf('\t');
		var name = separator < 0 ? null : text.Substring(0, separator);

		lock (_plaintextLock)
		{
			if (_plaintextFinished) return;

			_plaintextBytes += byteCount;

			if (_plaintextBytes > MaxMessageSize)
			{
				if (!_plaintextOverflowed)
				{
					// Dropped whole rather than truncated, and released now rather than at the end:
					// what has been parsed so far is of no use once the report is going to be
					// discarded, and holding it is the thing the ceiling exists to prevent.
					_plaintextOverflowed = true;
					_plaintextReceived.Clear();
				}

				return;
			}

			if (name is { Length: > 0 })
			{
				// An empty value is legitimate -- "The value can be an empty string" -- so a line that
				// ends at the tab is a reported variable, not a malformed one.
				_plaintextReceived.Add(name, text.Substring(separator + 1));
				return;
			}
		}

		context.Logger.LogDebug(
			"Ignoring a line inside a plaintext MSSP reply that is not a field: {Line}", text);
	}

	/// <summary>
	/// <c>MSSP-REPLY-END</c>: deliver the report, or say why it is not being delivered.
	/// </summary>
	private async ValueTask CompletePlaintextReplyAsync(IProtocolContext context)
	{
		bool overflowed;
		long receivedBytes;
		MSSPConfig? config;

		lock (_plaintextLock)
		{
			// The give-up timer may have landed on this line's heels; if it did, the report is already
			// gone and delivering half of it now is exactly what the timeout exists to prevent.
			if (_plaintextFinished) return;

			overflowed = _plaintextOverflowed;
			receivedBytes = _plaintextBytes;
			config = overflowed ? null : ProjectConfig(_plaintextReceived, MSSPSource.Plaintext, context.Logger);

			FinishPlaintext();
		}

		// Outside the lock: cancelling a token can run the timer task's continuation inline.
		CancelPlaintextSchedule();

		if (overflowed)
		{
			await ReportPlaintextOversizedAsync(receivedBytes, context);
			return;
		}

		context.Logger.LogDebug("Plaintext MSSP reply complete: {VariableCount} variables", config!.Variables.Count);

		if (_onMSSPRequest != null)
		{
			await _onMSSPRequest(config);
		}
	}

	private async ValueTask ReportPlaintextOversizedAsync(long receivedBytes, IProtocolContext context)
	{
		context.Logger.LogError(
			"Plaintext MSSP reply exceeded the maximum message size of {MaxMessageSize} bytes ({ReceivedBytes} bytes received) and was dropped. Raise MSSPProtocol.MaxMessageSize if this is legitimate traffic.",
			MaxMessageSize, receivedBytes);

		if (_onMSSPMessageTooLarge != null)
		{
			await _onMSSPMessageTooLarge((ReceivedBytes: receivedBytes, MaxMessageSize: MaxMessageSize));
		}
	}

	/// <summary>
	/// Ends the attempt: the collected bytes go, and nothing further is read as a reply. The exchange
	/// is one request and one answer, so a second start marker later in the session is ordinary text.
	/// Callers hold <see cref="_plaintextLock"/>.
	/// </summary>
	private void FinishPlaintext()
	{
		_plaintextCollecting = false;
		_plaintextFinished = true;
		_plaintextBytes = 0;
		_plaintextOverflowed = false;
		_plaintextReceived.Clear();
	}

	#endregion

	#region Answering a request

	/// <summary>
	/// Writes the report the way <c>send_mssp_data</c> writes it: a leading CRLF, the start marker,
	/// one <c>name&lt;TAB&gt;value</c> line per value, then the end marker.
	/// </summary>
	private async ValueTask SendPlaintextReportAsync(IProtocolContext context)
	{
		context.Logger.LogDebug("Client asked for MSSP as plaintext. Sending...");

		var encoding = context.CurrentEncoding;
		var bytes = new List<byte>();

		AppendPlaintextLine(bytes, string.Empty, encoding);
		AppendPlaintextLine(bytes, ReplyStart, encoding);

		foreach (var (name, value) in ReportFields(_msspConfig()))
		{
			foreach (var text in PlaintextValues(value))
			{
				AppendPlaintextLine(bytes, Sanitize(name, context) + "\t" + Sanitize(text, context), encoding);
			}
		}

		AppendPlaintextLine(bytes, ReplyEnd, encoding);

		await context.SendNegotiationAsync(bytes.ToArray());
	}

	/// <summary>
	/// Every value a variable carries, as the strings this transport puts on a line.
	/// </summary>
	private static IEnumerable<string> PlaintextValues(object value)
	{
		switch (value)
		{
			case string s:
				yield return s;
				break;
			case int i:
				// Invariant for the same reason the subnegotiation path is: a culture whose
				// NegativeSign is not '-' would put a U+2212 on the wire for CRAWL DELAY's -1.
				yield return i.ToString(CultureInfo.InvariantCulture);
				break;
			case bool b:
				yield return b ? "1" : "0";
				break;
			case IEnumerable enumerable:
				foreach (var item in enumerable)
				{
					yield return item?.ToString() ?? string.Empty;
				}

				break;
		}
	}

	/// <summary>
	/// Removes the two characters that would corrupt the framing.
	/// </summary>
	/// <remarks>
	/// A tab in a name or a value would move the field boundary; a CR or LF would end the line early
	/// and turn one field into two. This transport has no escape for either -- the framing is the line
	/// itself -- so they become spaces, which is the same choice as doubling <c>IAC</c> on the
	/// subnegotiation path: never put a frame on the wire that the peer cannot parse back.
	/// </remarks>
	private static string Sanitize(string text, IProtocolContext context)
	{
		if (text.IndexOf('\t') < 0 && text.IndexOf('\r') < 0 && text.IndexOf('\n') < 0)
		{
			return text;
		}

		context.Logger.LogDebug("Replacing tab or line-ending characters in a plaintext MSSP field: {Field}", text);

		var sanitized = new StringBuilder(text.Length);
		foreach (var c in text)
		{
			sanitized.Append(c is '\t' or '\r' or '\n' ? ' ' : c);
		}

		return sanitized.ToString();
	}

	/// <summary>
	/// Appends <paramref name="text"/> and a CRLF, doubling any <c>IAC</c> among the encoded bytes.
	/// </summary>
	/// <remarks>
	/// This is text, but it is text on a telnet connection: RFC 854's "the IAC need be doubled to be
	/// sent as data" applies to it exactly as it applies to a subnegotiation value, and under
	/// ISO-8859-1 a single <c>ÿ</c> encodes to 0xFF. CRLF rather than a bare LF for the same reason --
	/// RFC 854 makes CR LF the line terminator of the network virtual terminal.
	/// </remarks>
	private static void AppendPlaintextLine(List<byte> destination, string text, Encoding encoding)
	{
		AppendEscaped(destination, text, encoding);
		destination.Add((byte)Trigger.CARRIAGERETURN);
		destination.Add((byte)Trigger.NEWLINE);
	}

	#endregion

	#region Timers

	/// <summary>
	/// Records that the telnet option has answered, so the plaintext request is not sent -- or, if it
	/// already went out, is no longer waited for.
	/// </summary>
	/// <remarks>
	/// Grapevine's rule, and the right one: a server that has just told us it speaks option 70 has
	/// nothing to gain from a word typed at its login prompt.
	/// </remarks>
	private void TelnetOptionAnswered()
	{
		if (!_plaintextFallback) return;

		lock (_plaintextLock)
		{
			if (_plaintextFinished) return;

			FinishPlaintext();
		}

		CancelPlaintextSchedule();
	}

	private void StartPlaintextSchedule(IProtocolContext context)
	{
		if (_plaintextSchedule is not null) return;

		// Linked to the interpreter's own token, so that disposing the connection ends the timers even
		// though nothing disposes the plugin.
		_plaintextCts = CancellationTokenSource.CreateLinkedTokenSource(context.Interpreter.ProcessingToken);
		var token = _plaintextCts.Token;
		_plaintextSchedule = Task.Run(() => PlaintextScheduleAsync(context, token), CancellationToken.None);
	}

	/// <summary>
	/// Ask after one delay, give up after another. Grapevine's shape: <c>{:text_mssp_request}</c> ten
	/// seconds after connecting, a hard stop ten seconds after that.
	/// </summary>
	private async Task PlaintextScheduleAsync(IProtocolContext context, CancellationToken cancellationToken)
	{
		try
		{
			await Task.Delay(_plaintextRequestDelay, cancellationToken);

			if (_plaintextFinished || !IsEnabled) return;

			context.Logger.LogDebug("Requesting MSSP as plaintext");
			await context.SendNegotiationAsync(PlaintextRequest(context.CurrentEncoding));

			await Task.Delay(_plaintextReplyTimeout, cancellationToken);

			lock (_plaintextLock)
			{
				if (_plaintextFinished) return;

				FinishPlaintext();
			}

			context.Logger.LogWarning(
				"No plaintext MSSP reply within {PlaintextReplyTimeout}; giving up on this connection.",
				_plaintextReplyTimeout);

			if (_onPlaintextTimeout != null)
			{
				await _onPlaintextTimeout();
			}
		}
		catch (OperationCanceledException)
		{
			// Expected: the option answered, the reply arrived, or the connection is going away.
		}
		catch (Exception ex)
		{
			// This runs on a background task. Rethrowing would take down the host application over an
			// optional, best-effort transport, and there is nothing to retry: the attempt is one-shot.
			context.Logger.LogWarning(ex, "The plaintext MSSP request failed. No further attempt is made.");
		}
	}

	private static byte[] PlaintextRequest(Encoding encoding)
	{
		var bytes = new List<byte>();
		AppendPlaintextLine(bytes, RequestLine, encoding);
		return bytes.ToArray();
	}

	private void CancelPlaintextSchedule()
	{
		try
		{
			_plaintextCts?.Cancel();
		}
		catch (ObjectDisposedException)
		{
			// Disposed by StopPlaintextAsync while a reply was being completed. Nothing to cancel.
		}
	}

	/// <summary>
	/// Waits for the schedule to observe cancellation, then releases it. Called from the plugin's
	/// disable and dispose paths, so no timer can outlive the connection.
	/// </summary>
	private async ValueTask StopPlaintextAsync()
	{
		CancelPlaintextSchedule();

		if (_plaintextSchedule is { } schedule)
		{
			_plaintextSchedule = null;

			try
			{
				await schedule;
			}
			catch (OperationCanceledException)
			{
				// Expected.
			}
		}

		_plaintextCts?.Dispose();
		_plaintextCts = null;
	}

	#endregion
}
