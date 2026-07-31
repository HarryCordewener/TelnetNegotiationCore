using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Stateless;
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
/// are reused wholesale; only the framing and the field split differ. Reports arrive through
/// <see cref="MSSPProtocol"/>'s existing <c>OnMSSP</c> callback, tagged
/// <see cref="MSSPSource.Plaintext"/>.
/// </para>
/// <para>
/// <b>Adding this plugin is the opt-in.</b> There is no second switch, and nothing here happens on a
/// connection that does not register it. What adding it means differs by mode:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>Server:</b> automatic. An incoming <c>MSSP-REQUEST</c> line is answered and <em>consumed</em>,
/// which means that word is no longer usable as a character name on this server. That is the whole
/// consequence, and registering the plugin is the consent to it.
/// </description></item>
/// <item><description>
/// <b>Client:</b> nothing is sent until <see cref="RequestReportAsync"/> is called. Unlike
/// <c>IAC DO 70</c>, which a server that does not implement MSSP ignores, this puts real text at a
/// stranger's login prompt, where a server without the plaintext form will treat it as a character
/// name. When that is worth doing is a decision about the connection, not about the protocol, so the
/// consumer makes it.
/// </description></item>
/// </list>
/// <para>
/// <b>There is deliberately no timer.</b> No version of the MSSP specification gives timing for this
/// exchange -- the only timing concept MSSP has is <c>CRAWL DELAY</c>, which is hours between crawls,
/// not when to speak within a connection. Grapevine's crawler asks 10 seconds after connecting and
/// gives up at 20, but that is one crawler's published policy, and a library that baked it in would
/// be deciding for every consumer when to put text on a stranger's login prompt -- by which time an
/// interactive client may already have sent a character name. A crawler can rebuild that policy on
/// top of <see cref="RequestReportAsync"/> in three lines; nobody can recover the precision back out
/// of a baked-in timer.
/// </para>
/// <para>
/// <b>Specification status, stated plainly.</b> This form is not described on the current
/// specification page at <see href="https://tintin.mudhalla.net/protocols/mssp/">mudhalla.net</see>,
/// nor on the mudstandards mirror, but that page's own changelog records "Mar 20, 2009 - Plaintext
/// version of MSSP finalized and added to specification", and it is implemented across the SMAUG
/// family and read by Grapevine's crawler. It is more widely deployed than it is currently
/// documented, and no version of it ever gave timing guidance.
/// </para>
/// </remarks>
public class MSSPPlaintextProtocol : TelnetProtocolPluginBase
{
	/// <summary>The line a client sends to ask for a report.</summary>
	private const string RequestLine = "MSSP-REQUEST";

	/// <summary>The line that opens a reply.</summary>
	private const string ReplyStart = "MSSP-REPLY-START";

	/// <summary>The line that closes a reply.</summary>
	private const string ReplyEnd = "MSSP-REPLY-END";

	/// <summary>
	/// How long <see cref="RequestReportAsync"/> waits for <c>MSSP-REPLY-END</c> before giving up:
	/// 10 seconds.
	/// </summary>
	/// <remarks>
	/// This is a ceiling on an untrusted peer, not protocol timing. The specification says nothing
	/// about how quickly a server must answer; what it cannot be allowed to do is never answer at all
	/// while the caller waits forever.
	/// </remarks>
	public static readonly TimeSpan DefaultReplyTimeout = TimeSpan.FromSeconds(10);

	private TimeSpan _replyTimeout = DefaultReplyTimeout;

	/// <summary>The exchange currently in flight, or null when none is.</summary>
	private Exchange? _exchange;

	/// <summary>
	/// Guards <see cref="_exchange"/>, which the byte-processing loop completes and the calling
	/// thread starts.
	/// </summary>
	private readonly object _gate = new();

	/// <inheritdoc />
	public override Type ProtocolType => typeof(MSSPPlaintextProtocol);

	/// <inheritdoc />
	public override string ProtocolName => "MSSP Plaintext (MSSP-REQUEST)";

	/// <summary>
	/// <see cref="MSSPProtocol"/>. This transport carries that protocol's report, through its
	/// <c>OnMSSP</c> callback, bounded by its <c>MaxMessageSize</c> and, on a server, built from its
	/// <c>WithMSSPConfig</c> provider. Declaring the dependency means a consumer who adds this plugin
	/// alone is told so at <c>BuildAsync</c>, rather than finding out from silence on the wire.
	/// </summary>
	public override IReadOnlyCollection<Type> Dependencies => [typeof(MSSPProtocol)];

	/// <summary>
	/// How long <see cref="RequestReportAsync"/> waits for the end of a reply before returning
	/// <see langword="null"/>. Defaults to <see cref="DefaultReplyTimeout"/>.
	/// </summary>
	/// <remarks>
	/// A caller passing its own <see cref="CancellationToken"/> can end the wait sooner; this is the
	/// ceiling that still applies to a caller passing <see cref="CancellationToken.None"/>, so an
	/// unanswered request cannot wait forever.
	/// </remarks>
	/// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
	public TimeSpan ReplyTimeout
	{
		get => _replyTimeout;
		set
		{
			if (value <= TimeSpan.Zero)
			{
				throw new ArgumentOutOfRangeException(nameof(value), value, "Reply timeout must be positive.");
			}

			_replyTimeout = value;
		}
	}

	/// <summary>
	/// Sets <see cref="ReplyTimeout"/> in a fluent manner.
	/// </summary>
	/// <param name="timeout">How long to wait for the end of a reply</param>
	/// <returns>This instance for fluent chaining</returns>
	public MSSPPlaintextProtocol WithReplyTimeout(TimeSpan timeout)
	{
		ReplyTimeout = timeout;
		return this;
	}

	/// <inheritdoc />
	/// <remarks>
	/// This transport is text, not negotiation, so it adds no states and no triggers. It hooks the
	/// assembled-line path instead, which is what lets it consume its own lines rather than handing
	/// them to the host application as if a user had typed them.
	/// </remarks>
	public override void ConfigureStateMachine(StateMachine<State, Trigger> stateMachine, IProtocolContext context)
	{
		context.Logger.LogInformation("Configuring plaintext MSSP");
		context.Interpreter.RegisterInputLineObserver((line, encoding) => OnInputLineAsync(line, encoding, context));
	}

	/// <inheritdoc />
	protected override ValueTask OnInitializeAsync()
	{
		Context.Logger.LogInformation("MSSP Plaintext Protocol initialized");
		return default(ValueTask);
	}

	/// <inheritdoc />
	protected override ValueTask OnProtocolDisabledAsync()
	{
		AbandonExchange();
		return default(ValueTask);
	}

	/// <inheritdoc />
	protected override ValueTask OnDisposeAsync()
	{
		AbandonExchange();
		return default(ValueTask);
	}

	/// <summary>
	/// Sends the plaintext <c>MSSP-REQUEST</c> and waits for the reply, returning the report the peer
	/// sent, or <see langword="null"/> if it never sent one.
	/// </summary>
	/// <remarks>
	/// <para>
	/// The report is also delivered to <c>OnMSSP</c>, so a consumer already wired for telnet option 70
	/// needs no new plumbing; it is returned here as well because a crawler wants the value at the
	/// call site, where it knows which host it just asked.
	/// </para>
	/// <para>
	/// <see langword="null"/> means "no report", for every reason that can happen: the peer never
	/// answered within <see cref="ReplyTimeout"/>, it began a reply and never ended it, it sent more
	/// than <c>MSSPProtocol.MaxMessageSize</c> (in which case the reply is dropped whole and
	/// <c>OnMSSPMessageTooLarge</c> says so), or the connection went away. It is not an error: a
	/// server that does not implement this form answering "Illegal name, try another." is the ordinary
	/// case.
	/// </para>
	/// <para>
	/// Rebuilding Grapevine's crawler policy on top of this is three lines, and every part of it stays
	/// the caller's:
	/// </para>
	/// <code>
	/// await Task.Delay(TimeSpan.FromSeconds(10), token);
	/// if (report is null)
	///     report = await plaintext.RequestReportAsync(token);
	/// </code>
	/// </remarks>
	/// <param name="cancellationToken">Ends the wait early. Cancelling it throws
	/// <see cref="OperationCanceledException"/>, as cancellation normally does; the internal ceiling
	/// and a dead connection return <see langword="null"/> instead, because those are answers about
	/// the peer rather than about the caller.</param>
	/// <returns>The peer's report, or <see langword="null"/> if it did not send one.</returns>
	/// <exception cref="InvalidOperationException">
	/// This interpreter is in server mode -- a server answers this exchange, it does not start it --
	/// or a request is already in flight on this connection.
	/// </exception>
	public async ValueTask<MSSPConfig?> RequestReportAsync(CancellationToken cancellationToken = default)
	{
		if (Context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Server)
		{
			throw new InvalidOperationException(
				"A server answers MSSP-REQUEST rather than sending one. RequestReportAsync is the client half of the exchange.");
		}

		// A disabled plugin does not put text on a stranger's login prompt. Checked here rather than
		// left to fail quietly: the line observer already returns early when disabled, so without this
		// the request went out, nothing collected the reply, and the caller paid the side effect in
		// exchange for a guaranteed timeout.
		if (!IsEnabled)
		{
			throw new InvalidOperationException(
				$"{nameof(MSSPPlaintextProtocol)} is disabled on this connection, so it will not send MSSP-REQUEST.");
		}

		if (!Mssp.IsEnabled)
		{
			throw new InvalidOperationException(
				$"{nameof(MSSPProtocol)} is disabled on this connection, so {nameof(MSSPPlaintextProtocol)} will not ask for a report on its behalf.");
		}

		var exchange = new Exchange(ReplyTimeout, cancellationToken, Context.Interpreter.ProcessingToken);

		lock (_gate)
		{
			if (_exchange is not null)
			{
				exchange.Dispose();
				throw new InvalidOperationException(
					"An MSSP-REQUEST is already in flight on this connection. Await the one in progress before starting another.");
			}

			_exchange = exchange;
		}

		try
		{
			Context.Logger.LogDebug("Requesting MSSP as plaintext");
			await Context.SendNegotiationAsync(RequestBytes(Context.CurrentEncoding));

			var config = await exchange.Completion;

			if (config is not null)
			{
				await Mssp.DeliverReportAsync(config);
			}
			else if (exchange.OversizedBytes is { } oversized)
			{
				Context.Logger.LogError(
					"Plaintext MSSP reply exceeded the maximum message size of {MaxMessageSize} bytes ({ReceivedBytes} bytes received) and was dropped. Raise MSSPProtocol.MaxMessageSize if this is legitimate traffic.",
					Mssp.MaxMessageSize, oversized);

				await Mssp.ReportTooLargeAsync(oversized);
			}
			else
			{
				Context.Logger.LogDebug("No plaintext MSSP reply within {ReplyTimeout}", ReplyTimeout);
			}

			return config;
		}
		finally
		{
			lock (_gate)
			{
				if (ReferenceEquals(_exchange, exchange))
				{
					_exchange = null;
				}
			}

			exchange.Dispose();
		}
	}

	/// <summary>
	/// The <see cref="MSSPProtocol"/> whose report this transport carries. Present by construction:
	/// <see cref="Dependencies"/> makes registering without it a <c>BuildAsync</c> failure.
	/// </summary>
	private MSSPProtocol Mssp =>
		Context.GetPlugin<MSSPProtocol>()
		?? throw new InvalidOperationException($"{nameof(MSSPPlaintextProtocol)} requires {nameof(MSSPProtocol)}.");

	private void AbandonExchange()
	{
		Exchange? exchange;
		lock (_gate)
		{
			exchange = _exchange;
			_exchange = null;
		}

		exchange?.Abandon();
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
		if (!IsEnabled) return false;

		var text = encoding.GetString(line);

		if (context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Server)
		{
			if (!text.Trim().Equals(RequestLine, StringComparison.OrdinalIgnoreCase)) return false;

			// Not on behalf of a protocol that has been turned off -- the report would be built from
			// the configuration of a plugin the consumer disabled. ProtocolPluginManager refuses to
			// disable MSSPProtocol while this plugin is enabled (Dependencies is what makes it refuse),
			// so this is belt and braces against the one route that skips that check: calling
			// ITelnetProtocolPlugin.OnDisabledAsync directly, which is public.
			if (!Mssp.IsEnabled)
			{
				context.Logger.LogDebug(
					"Ignoring MSSP-REQUEST: {Protocol} is disabled on this connection", nameof(MSSPProtocol));
				return false;
			}

			await SendReportAsync(context);
			return true;
		}

		// No request outstanding: this is somebody else's text, markers or not.
		Exchange? exchange;
		lock (_gate) exchange = _exchange;

		if (exchange is null) return false;

		if (!exchange.Collecting)
		{
			if (!text.Trim().Equals(ReplyStart, StringComparison.OrdinalIgnoreCase)) return false;

			context.Logger.LogDebug("Plaintext MSSP reply started");
			exchange.Begin();
			return true;
		}

		if (text.Trim().Equals(ReplyEnd, StringComparison.OrdinalIgnoreCase))
		{
			exchange.Complete(context.Logger);
			return true;
		}

		if (!exchange.Collect(text, line.Length, Mssp.MaxMessageSize))
		{
			context.Logger.LogDebug(
				"Ignoring a line inside a plaintext MSSP reply that is not a field: {Line}", text);
		}

		return true;
	}

	#endregion

	#region Answering a request

	/// <summary>
	/// Writes the report the way <c>send_mssp_data</c> writes it: a leading CRLF, the start marker,
	/// one <c>name&lt;TAB&gt;value</c> line per value, then the end marker.
	/// </summary>
	private async ValueTask SendReportAsync(IProtocolContext context)
	{
		context.Logger.LogDebug("Client asked for MSSP as plaintext. Sending...");

		var encoding = context.CurrentEncoding;
		var bytes = new List<byte>();

		AppendLine(bytes, string.Empty, encoding);
		AppendLine(bytes, ReplyStart, encoding);

		foreach (var (name, value) in MSSPProtocol.ReportFields(Mssp.GetMSSPConfig()))
		{
			foreach (var text in PlaintextValues(value))
			{
				AppendLine(bytes, Sanitize(name, context) + "\t" + Sanitize(text, context), encoding);
			}
		}

		AppendLine(bytes, ReplyEnd, encoding);

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

	private static byte[] RequestBytes(Encoding encoding)
	{
		var bytes = new List<byte>();
		AppendLine(bytes, RequestLine, encoding);
		return bytes.ToArray();
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
	private static void AppendLine(List<byte> destination, string text, Encoding encoding)
	{
		MSSPProtocol.AppendEscaped(destination, text, encoding);
		destination.Add((byte)Trigger.CARRIAGERETURN);
		destination.Add((byte)Trigger.NEWLINE);
	}

	#endregion

	/// <summary>
	/// One <c>MSSP-REQUEST</c> and the reply it is waiting for: the variables collected so far, the
	/// running byte count that bounds them, and the completion the caller is awaiting.
	/// </summary>
	/// <remarks>
	/// Bundled into one object so that starting a request, completing it and abandoning it are each a
	/// single reference swap under <see cref="_gate"/>. The byte-processing loop fills it; the calling
	/// thread awaits it.
	/// </remarks>
	private sealed class Exchange : IDisposable
	{
		private readonly TaskCompletionSource<MSSPConfig?> _completion =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		private readonly CancellationTokenSource _expiry;
		private readonly CancellationTokenRegistration _onExpired;
		private readonly MSSPVariableCollection _received = new();
		private readonly object _lock = new();

		private long _bytes;
		private bool _overflowed;
		private bool _finished;

		public Exchange(TimeSpan replyTimeout, CancellationToken callerToken, CancellationToken connectionToken)
		{
			// The caller's token is kept apart from the two internal ones: cancelling it is the
			// caller's decision and gets an exception, while the ceiling expiring and the connection
			// going away are both facts about the peer and get null.
			_expiry = CancellationTokenSource.CreateLinkedTokenSource(connectionToken);
			_expiry.CancelAfter(replyTimeout);
			_onExpired = _expiry.Token.Register(static state => ((Exchange)state!).Expire(), this);

			Completion = WaitAsync(callerToken);
		}

		/// <summary>The report, or null when the peer never sent a usable one.</summary>
		public Task<MSSPConfig?> Completion { get; }

		/// <summary>Set when the reply overran the ceiling, for the caller to report afterwards.</summary>
		public long? OversizedBytes { get; private set; }

		/// <summary>True between the start marker and the end of the exchange.</summary>
		public bool Collecting { get; private set; }

		public void Begin() => Collecting = true;

		/// <summary>
		/// Records one <c>name&lt;TAB&gt;value</c> line, and keeps the running byte count that bounds
		/// the reply. Returns false when the line is not a field.
		/// </summary>
		/// <remarks>
		/// The split is on the first tab and nothing else. Official variable names contain spaces --
		/// <c>MINIMUM AGE</c>, <c>PAY TO PLAY</c>, <c>XTERM 256 COLORS</c> -- and values contain them
		/// too, so any whitespace-based split would tear both apart. A line with no tab is not a
		/// field: SMAUG never writes one, and a decorative separator must not become a variable.
		/// </remarks>
		public bool Collect(string text, int byteCount, int maxMessageSize)
		{
			var separator = text.IndexOf('\t');
			var name = separator < 0 ? null : text.Substring(0, separator);

			lock (_lock)
			{
				if (_finished) return true;

				_bytes += byteCount;

				if (_bytes > maxMessageSize)
				{
					if (!_overflowed)
					{
						// Dropped whole rather than truncated, and released now rather than at the end:
						// what has been parsed is of no use once the report is going to be discarded,
						// and holding it is the thing the ceiling exists to prevent.
						_overflowed = true;
						_received.Clear();
					}

					return true;
				}

				if (name is not { Length: > 0 }) return false;

				// An empty value is legitimate -- "The value can be an empty string" -- so a line that
				// ends at the tab is a reported variable, not a malformed one.
				_received.Add(name, text.Substring(separator + 1));
				return true;
			}
		}

		/// <summary><c>MSSP-REPLY-END</c>: hand the caller the report, or say why there isn't one.</summary>
		public void Complete(ILogger logger)
		{
			MSSPConfig? config;

			lock (_lock)
			{
				if (_finished) return;

				_finished = true;
				Collecting = false;

				if (_overflowed)
				{
					OversizedBytes = _bytes;
					config = null;
				}
				else
				{
					config = MSSPProtocol.ProjectConfig(_received, MSSPSource.Plaintext, logger);
					logger.LogDebug("Plaintext MSSP reply complete: {VariableCount} variables", config.Variables.Count);
				}

				_received.Clear();
			}

			_completion.TrySetResult(config);
		}

		/// <summary>The ceiling elapsed, or the connection went away. Either way: no report.</summary>
		/// <remarks>
		/// The overflow is carried across, because the two ceilings can both apply to one reply: a peer
		/// that overruns the byte cap and then never sends an end marker ends the wait here rather than
		/// in <see cref="Complete"/>, and "the peer sent too much" is what happened -- reporting it as
		/// "the peer did not answer" would lose the only fact the caller can act on.
		/// </remarks>
		private void Expire()
		{
			lock (_lock)
			{
				// Complete already decided the outcome; it wins, and has already published it.
				if (_finished) return;

				_finished = true;
				Collecting = false;

				if (_overflowed)
				{
					OversizedBytes = _bytes;
				}

				_received.Clear();
			}

			_completion.TrySetResult(null);
		}

		/// <summary>The plugin is being disabled or disposed underneath a waiting caller.</summary>
		public void Abandon() => Expire();

		private async Task<MSSPConfig?> WaitAsync(CancellationToken callerToken)
		{
			if (!callerToken.CanBeCanceled)
			{
				return await _completion.Task.ConfigureAwait(false);
			}

			var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			using (callerToken.Register(static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true), cancelled))
			{
				var finished = await Task.WhenAny(_completion.Task, cancelled.Task).ConfigureAwait(false);

				if (finished == _completion.Task)
				{
					return await _completion.Task.ConfigureAwait(false);
				}
			}

			callerToken.ThrowIfCancellationRequested();
			return await _completion.Task.ConfigureAwait(false);
		}

		public void Dispose()
		{
			_onExpired.Dispose();
			_expiry.Dispose();
		}
	}
}
