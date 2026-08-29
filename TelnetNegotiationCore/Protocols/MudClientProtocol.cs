using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Stateless;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Plugins;

namespace TelnetNegotiationCore.Protocols;

/// <summary>
/// MCP, the MUD Client Protocol: the out-of-band session layer LambdaMOO and its descendants carry
/// over ordinary telnet text, on lines beginning <c>#$#</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is the session layer only -- framing, quoting, the version handshake and the authentication
/// key. Package negotiation is <see cref="McpNegotiateProtocol"/>, which is a separate plugin
/// because <c>mcp-negotiate</c> is itself a package carried over this layer rather than a part of
/// it.
/// </para>
/// <para>
/// The handshake is asymmetric and the server opens it, because nothing else can: a client has no
/// way to know MCP is on offer until the server says so. The server's offer is the only message in
/// the protocol that carries no authentication key.
/// </para>
/// </remarks>
public class MudClientProtocol : TelnetProtocolPluginBase
{
	/// <summary>The prefix that marks a line as MCP rather than as output.</summary>
	public const string Prefix = "#$#";

	/// <summary>The prefix a server puts on a line of real output that would look like MCP.</summary>
	public const string QuotePrefix = "#$\"";

	private static readonly byte[] PrefixBytes = [0x23, 0x24, 0x23];

	private static readonly byte[] QuotePrefixBytes = [0x23, 0x24, 0x22];

	/// <summary>The only version of MCP this implements.</summary>
	public static readonly McpVersion Version = new(2, 1);

	/// <summary>
	/// How many multiline messages may be open at once, and how many lines any one of them may
	/// accumulate.
	/// </summary>
	/// <remarks>
	/// A multiline message is held in memory until its terminator arrives, and the peer decides
	/// whether one ever does. Without a ceiling, a server that opens messages and never closes them
	/// grows this client's memory for as long as the connection lasts.
	/// </remarks>
	private const int MaxOpenMultilineMessages = 8;

	/// <summary>How many continuation lines one multiline message may accumulate.</summary>
	private const int MaxContinuationLines = 4096;

	private readonly Dictionary<string, Func<McpMessage, ValueTask>> _handlers =
		new(StringComparer.OrdinalIgnoreCase);

	/// <summary>Called once, when the handshake has produced a session.</summary>
	private readonly List<Func<ValueTask>> _established = [];

	/// <summary>Multiline messages opened but not yet closed, keyed by their data tag.</summary>
	private readonly Dictionary<string, McpMessage> _open = new(StringComparer.Ordinal);

	private string? _authenticationKey;

	/// <summary>The number the next outbound data tag is built from.</summary>
	private long _nextDataTag;

	/// <summary>
	/// The key that authenticates every MCP message in this session after the handshake, or
	/// <see langword="null"/> until the handshake has produced one.
	/// </summary>
	/// <remarks>
	/// Chosen by the client, in both directions: after the handshake the server quotes back the key
	/// the client picked. The point of it is that other people on a MUD can type <c>#$#</c> at the
	/// start of a line, and without a key those keystrokes would reach this client as protocol.
	/// </remarks>
	public string? AuthenticationKey => _authenticationKey;

	/// <summary>
	/// Whether this client answers a server's offer of MCP. True by default.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Set false to take MCP out of the stream without ever speaking it. The offer is still consumed
	/// -- it is protocol, and does not belong in a connect screen shown to a reader -- but nothing is
	/// sent in reply, so no session is established and every later <c>#$#</c> line is treated as it is
	/// outside a session.
	/// </para>
	/// <para>
	/// This is for a consumer that reads screens from strangers and has no use for a session: a
	/// crawler. Answering would put text on a stranger's login prompt for a session it will never
	/// use, which is the objection <see cref="MSSPPlaintextProtocol"/> makes to sending
	/// <c>MSSP-REQUEST</c> unbidden. Unlike that case the server did ask first, so answering is not
	/// unsolicited -- it is merely pointless, and not free.
	/// </para>
	/// <para>
	/// Ignored in server mode, which answers nothing: a server makes the offer.
	/// </para>
	/// </remarks>
	public bool AnswersOffers { get; set; } = true;

	/// <summary>
	/// Sets <see cref="AnswersOffers"/> to false in a fluent manner.
	/// </summary>
	/// <returns>This instance for fluent chaining</returns>
	public MudClientProtocol WithoutAnsweringOffers()
	{
		AnswersOffers = false;
		return this;
	}

	/// <summary>
	/// Registers the handler for one message name. Packages call this from their own
	/// <c>InitializeAsync</c>, which is before any wire traffic can arrive.
	/// </summary>
	/// <param name="name">The message name, matched case-insensitively as MCP matches it</param>
	/// <param name="handler">Called once per complete message -- for a multiline message, once its
	/// terminator has arrived, not once per continuation line</param>
	/// <returns>This instance for fluent chaining</returns>
	public MudClientProtocol OnMessage(string name, Func<McpMessage, ValueTask> handler)
	{
		if (string.IsNullOrEmpty(name)) throw new ArgumentException("A message name is required.", nameof(name));
		if (handler is null) throw new ArgumentNullException(nameof(handler));

		lock (_handlers) _handlers[name] = handler;
		return this;
	}

	/// <summary>
	/// Registers a handler to run once the handshake has produced a session -- which is when there is
	/// an authentication key, and so the first moment anything can be sent.
	/// </summary>
	/// <remarks>
	/// This is the hook a package plugin opens on. It runs after <c>InitializeAsync</c> for every
	/// plugin, on the byte-processing loop, which is what makes it safe for <c>mcp-negotiate</c> to
	/// send its whole package list from here: every package that was going to register has done so.
	/// </remarks>
	/// <param name="handler">Called once, when the session is established</param>
	/// <returns>This instance for fluent chaining</returns>
	public MudClientProtocol OnEstablished(Func<ValueTask> handler)
	{
		if (handler is null) throw new ArgumentNullException(nameof(handler));

		lock (_established) _established.Add(handler);
		return this;
	}

	/// <summary>
	/// Sends one MCP message, with this session's authentication key and the values quoted.
	/// </summary>
	/// <param name="name">The message name</param>
	/// <param name="values">The keyword arguments, in the order they should be written</param>
	/// <exception cref="InvalidOperationException">There is no MCP session on this connection.</exception>
	public async ValueTask SendAsync(string name, params (string Key, string Value)[] values)
	{
		var line = new StringBuilder(Prefix).Append(name).Append(' ').Append(RequireKey());

		foreach (var (key, value) in values)
		{
			line.Append(' ').Append(key).Append(": ").Append(Quote(value));
		}

		line.Append("\r\n");

		await Context.SendNegotiationAsync(Context.CurrentEncoding.GetBytes(line.ToString()));
	}

	/// <summary>
	/// Sends one MCP message carrying content that does not fit on the line: the opening message, one
	/// continuation line per line of content, and the line that closes the tag.
	/// </summary>
	/// <remarks>
	/// <para>
	/// This is the direction <c>dns-org-mud-moo-simpleedit</c> needs -- a server handing a client a
	/// buffer to edit. The opening message names each continuation key with a trailing <c>*</c> and
	/// carries a <c>_data-tag</c>; every continuation line quotes that tag, and its text runs verbatim
	/// to the end of the line, with no quoting of its own.
	/// </para>
	/// <para>
	/// <b>The whole message goes out under one lock</b>, because a second message interleaved into the
	/// middle of this one is not merely out of order: the peer reassembles by data tag, so a foreign
	/// line landing between the opening message and its terminator is read as belonging to whatever
	/// tag it names, and this message's own lines arrive with a hole in them.
	/// </para>
	/// </remarks>
	/// <param name="name">The message name</param>
	/// <param name="values">The ordinary keyword arguments, in the order they should be written</param>
	/// <param name="multiline">The continuation keys and their content</param>
	/// <exception cref="InvalidOperationException">There is no MCP session on this connection.</exception>
	public async ValueTask SendMultilineAsync(
		string name,
		IReadOnlyCollection<(string Key, string Value)> values,
		params (string Key, IReadOnlyCollection<string> Lines)[] multiline)
	{
		if (values is null) throw new ArgumentNullException(nameof(values));
		if (multiline is null) throw new ArgumentNullException(nameof(multiline));

		if (multiline.Length == 0)
		{
			throw new ArgumentException(
				"A multiline message needs at least one continuation key; use SendAsync for a message without one.",
				nameof(multiline));
		}

		var key = RequireKey();

		// Unique per direction, which is all the peer needs: it reassembles what this side sent, and
		// this side's own inbound table is keyed by the tags the peer chose. Nothing has to agree.
		var tag = Interlocked.Increment(ref _nextDataTag).ToString(CultureInfo.InvariantCulture);

		var message = new StringBuilder(Prefix).Append(name).Append(' ').Append(key);

		foreach (var (argument, value) in values)
		{
			message.Append(' ').Append(argument).Append(": ").Append(Quote(value));
		}

		foreach (var (argument, _) in multiline)
		{
			// The empty string is what goes in the place of a value that arrives elsewhere. It is
			// convention rather than data, and the peer does not read it.
			message.Append(' ').Append(argument).Append("*: \"\"");
		}

		message.Append(" _data-tag: ").Append(Quote(tag)).Append("\r\n");

		foreach (var (argument, lines) in multiline)
		{
			foreach (var line in lines)
			{
				// Split rather than refused: a caller handing over a whole document as one string is
				// ordinary use, and an embedded newline written straight to the wire would end the
				// continuation line early and put the rest of the content out as ordinary output.
				foreach (var single in SplitLines(line))
				{
					message.Append(Prefix).Append("* ").Append(tag).Append(' ')
						.Append(argument).Append(": ").Append(single).Append("\r\n");
				}
			}
		}

		message.Append(Prefix).Append(": ").Append(tag).Append("\r\n");

		await Context.SendNegotiationAsync(Context.CurrentEncoding.GetBytes(message.ToString()));
	}

	/// <summary>One string as the lines it holds, whatever line endings it was written with.</summary>
	private static IEnumerable<string> SplitLines(string text)
	{
		if (text is null) throw new ArgumentNullException(nameof(text));

		var at = 0;

		while (true)
		{
			var end = text.IndexOfAny(['\r', '\n'], at);

			if (end < 0)
			{
				yield return text.Substring(at);
				yield break;
			}

			yield return text.Substring(at, end - at);

			at = end + 1;

			if (text[end] == '\r' && at < text.Length && text[at] == '\n') at++;

			if (at == text.Length) yield break;
		}
	}

	/// <summary>
	/// This session's authentication key, or the reason there is not one.
	/// </summary>
	private string RequireKey() =>
		IsNegotiated && _authenticationKey is not null
			? _authenticationKey
			: throw new InvalidOperationException(
				"There is no MCP session on this connection, so there is no authentication key to send a message with.");

	/// <summary>
	/// Sends ordinary output with MCP's quoting applied, which is a server's half of the framing rule.
	/// </summary>
	/// <remarks>
	/// <para>
	/// While a session is up, a line of real output that begins <c>#$#</c> would be read by the peer
	/// as a message, so it goes out prefixed with <c>#$"</c> for the peer to strip. A line that
	/// already begins <c>#$"</c> is quoted for the same reason in reverse: unquoted, it arrives at a
	/// peer that strips the prefix, and the text loses three characters on the way.
	/// </para>
	/// <para>
	/// Outside a session nothing is quoted, because nothing is unquoting it: a peer with no MCP
	/// session would show the prefix to the player as text.
	/// </para>
	/// <para>
	/// This is a separate call rather than a hook on the interpreter's own send path because quoting
	/// is a line-level decision and that path is a byte stream: a caller that writes half a line, or
	/// several at once, is ordinary use of it, and there is no honest place in the middle of that to
	/// decide what a line begins with. A server sends its output through here instead.
	/// </para>
	/// </remarks>
	/// <param name="text">The output, with its line endings, exactly as it would otherwise be sent</param>
	public async ValueTask SendOutputAsync(string text)
	{
		if (text is null) throw new ArgumentNullException(nameof(text));

		await Context.Interpreter.SendAsync(Context.CurrentEncoding.GetBytes(IsNegotiated ? QuoteOutput(text) : text));
	}

	/// <summary>
	/// The same quoting <see cref="SendOutputAsync"/> applies, for a caller that writes to the wire
	/// some other way.
	/// </summary>
	/// <param name="text">The output, with its line endings</param>
	/// <returns>The output with every line that would look like protocol quoted.</returns>
	public static string QuoteOutput(string text)
	{
		if (text is null) throw new ArgumentNullException(nameof(text));

		// Lines are found rather than split out, so whatever endings the text already has -- CRLF, LF,
		// or none at all on the last line -- come back exactly as they went in.
		var quoted = new StringBuilder(text.Length);
		var at = 0;

		while (at < text.Length)
		{
			var end = text.IndexOf('\n', at);
			var next = end < 0 ? text.Length : end + 1;

			if (Needs(text, at)) quoted.Append(QuotePrefix);

			quoted.Append(text, at, next - at);
			at = next;
		}

		return quoted.ToString();
	}

	/// <summary>Whether the line starting at <paramref name="at"/> would be read as protocol.</summary>
	private static bool Needs(string text, int at) =>
		string.CompareOrdinal(text, at, Prefix, 0, Prefix.Length) == 0
		|| string.CompareOrdinal(text, at, QuotePrefix, 0, QuotePrefix.Length) == 0;

	/// <summary>
	/// A value as MCP writes it: always quoted, because an unquoted value cannot hold a space and
	/// deciding case by case only moves the bug to the first value that grows one.
	/// </summary>
	private static string Quote(string value)
	{
		var quoted = new StringBuilder(value.Length + 2).Append('"');

		foreach (var c in value)
		{
			if (c == '"' || c == '\\') quoted.Append('\\');

			quoted.Append(c);
		}

		return quoted.Append('"').ToString();
	}

	/// <inheritdoc />
	public override Type ProtocolType => typeof(MudClientProtocol);

	/// <inheritdoc />
	public override string ProtocolName => "MCP (MUD Client Protocol)";

	/// <inheritdoc />
	/// <remarks>
	/// MCP is text, not negotiation, so it adds no states and no triggers. It hooks the assembled-line
	/// path instead, which is what lets it take its own lines out of the stream rather than handing
	/// them to the host application as if a user had typed them.
	/// </remarks>
	public override void ConfigureStateMachine(StateMachine<State, Trigger> stateMachine, IProtocolContext context)
	{
		context.Logger.LogInformation("Configuring MCP");

		context.Interpreter.RegisterInputLineObserver((line, encoding) => OnInputLineAsync(line, encoding, context));

		if (context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Server)
		{
			context.RegisterInitialNegotiation(() => OfferAsync(context));
		}
	}

	/// <inheritdoc />
	/// <remarks>
	/// The session ends with the plugin. Leaving the key behind would leave a value that goes on
	/// authenticating messages nothing is listening for, and leaving the half-built multiline messages
	/// behind would hold their memory for the life of the connection.
	/// </remarks>
	protected override async ValueTask OnProtocolDisabledAsync()
	{
		_authenticationKey = null;

		lock (_open) _open.Clear();

		await OnNegotiatedAsync(false);
	}

	/// <inheritdoc />
	protected override ValueTask OnDisposeAsync()
	{
		lock (_open) _open.Clear();

		return default(ValueTask);
	}

	/// <summary>
	/// Sends the one message in MCP that carries no authentication key: the server's offer.
	/// </summary>
	private static async ValueTask OfferAsync(IProtocolContext context)
	{
		context.Logger.LogDebug("Offering MCP {Version}", Version);

		var line = $"{Prefix}{McpMessage.Handshake} version: \"{Version}\" to: \"{Version}\"\r\n";
		await context.SendNegotiationAsync(context.CurrentEncoding.GetBytes(line));
	}

	/// <summary>
	/// One assembled line of ordinary input. Returns the line to carry on with -- unquoted, if the
	/// peer quoted it -- or <see langword="null"/> when the line belongs to MCP and must not reach
	/// the host application.
	/// </summary>
	/// <remarks>
	/// The prefixes are matched against the line's <em>bytes</em> rather than its decoded text, and
	/// unquoting hands back a slice of those same bytes. MCP itself is ASCII, but the output it is
	/// carried alongside is whatever the connection negotiated, and a line rebuilt by re-encoding a
	/// decoded string comes back changed on every connection where those two differ.
	/// </remarks>
	private async ValueTask<byte[]?> OnInputLineAsync(byte[] line, Encoding encoding, IProtocolContext context)
	{
		if (!IsEnabled) return line;

		// Unquoting is a fact about a running session rather than about the characters: a server only
		// quotes once MCP is up, so before that a line beginning #$" is just what it looks like.
		if (IsNegotiated && StartsWith(line, QuotePrefixBytes))
		{
			var unquoted = new byte[line.Length - QuotePrefixBytes.Length];
			Array.Copy(line, QuotePrefixBytes.Length, unquoted, 0, unquoted.Length);
			return unquoted;
		}

		if (!StartsWith(line, PrefixBytes)) return line;

		var body = encoding.GetString(line, PrefixBytes.Length, line.Length - PrefixBytes.Length).TrimEnd('\r');

		// The two shapes that are not messages: a continuation line and the line that closes one.
		// Neither carries the authentication key -- the data tag is what authenticates them, and it is
		// only ever known to a peer this side already opened a message with.
		if (body.Length > 0 && (body[0] == '*' || body[0] == ':'))
		{
			return await OnContinuationAsync(body, context, line);
		}

		if (!McpMessage.TryParse(body, out var message))
		{
			return Reject(context, "it is not a well-formed message", line);
		}

		if (message!.Name == McpMessage.Handshake)
		{
			await OnHandshakeAsync(message, context);
			return null;
		}

		if (!IsNegotiated)
		{
			return Reject(context, "there is no MCP session on this connection", line);
		}

		if (!string.Equals(message.AuthenticationKey, _authenticationKey, StringComparison.Ordinal))
		{
			return Reject(context, "it carries the wrong authentication key", line);
		}

		if (message.IsMultiline)
		{
			return Open(message, context);
		}

		await DispatchAsync(message, context);
		return null;
	}

	/// <summary>
	/// Holds a multiline message open until its terminator arrives.
	/// </summary>
	private byte[]? Open(McpMessage message, IProtocolContext context)
	{
		var tag = message.DataTag!;

		lock (_open)
		{
			if (_open.Count >= MaxOpenMultilineMessages)
			{
				context.Logger.LogWarning(
					"Refusing to open a {Count}th multiline MCP message; the peer has left the others unterminated",
					MaxOpenMultilineMessages + 1);
				return null;
			}

			// A repeated tag would silently replace a message already in flight, which is the same
			// substitution the authentication key exists to prevent, one layer down.
			if (_open.ContainsKey(tag))
			{
				context.Logger.LogDebug("Ignoring an MCP message that reuses the data tag of one already open");
				return null;
			}

			_open[tag] = message;
		}

		return null;
	}

	/// <summary>
	/// A continuation line (<c>#$#* &lt;tag&gt; &lt;key&gt;: &lt;text&gt;</c>) or the line that closes
	/// one (<c>#$#: &lt;tag&gt;</c>).
	/// </summary>
	private async ValueTask<byte[]?> OnContinuationAsync(string body, IProtocolContext context, byte[] line)
	{
		if (!IsNegotiated) return Reject(context, "there is no MCP session on this connection", line);

		var terminates = body[0] == ':';
		var rest = body.Substring(1).TrimStart(' ');
		var space = rest.IndexOf(' ');
		var tag = space < 0 ? rest : rest.Substring(0, space);

		if (tag.Length == 0) return Reject(context, "it names no data tag", line);

		McpMessage? open;
		lock (_open) _open.TryGetValue(tag, out open);

		if (open is null) return Reject(context, "no multiline message is open under that data tag", line);

		if (terminates)
		{
			lock (_open) _open.Remove(tag);

			await DispatchAsync(open, context);
			return null;
		}

		if (space < 0) return Reject(context, "it carries no continuation key", line);

		// The text runs verbatim to the end of the line: no quoting, and a colon or a quote in it is
		// part of the data rather than syntax.
		var argument = rest.Substring(space + 1);
		var colon = argument.IndexOf(':');

		if (colon <= 0) return Reject(context, "it carries no continuation key", line);

		var key = argument.Substring(0, colon);
		var text = argument.Substring(colon + 1);

		if (text.StartsWith(" ", StringComparison.Ordinal)) text = text.Substring(1);

		if (open.LineCount >= MaxContinuationLines)
		{
			context.Logger.LogWarning(
				"Dropping an MCP multiline message that exceeded {MaxContinuationLines} continuation lines",
				MaxContinuationLines);

			lock (_open) _open.Remove(tag);
			return null;
		}

		if (!open.AppendLine(key, text))
		{
			context.Logger.LogDebug("Ignoring a continuation line for a key the message did not open: {Key}", key);
		}

		return null;
	}

	private async ValueTask DispatchAsync(McpMessage message, IProtocolContext context)
	{
		Func<McpMessage, ValueTask>? handler;
		lock (_handlers) _handlers.TryGetValue(message.Name, out handler);

		if (handler is null)
		{
			// Consumed all the same: an unhandled MCP message is still MCP, and showing it to a player
			// is showing them the protocol.
			context.Logger.LogDebug("No handler for MCP message {Message}", message.Name);
			return;
		}

		await handler(message);
	}

	/// <summary>
	/// Refuses a line that arrived looking like MCP: dropped inside a session, passed through outside
	/// one.
	/// </summary>
	/// <remarks>
	/// Anyone on a MUD can type <c>#$#</c> at the start of a line, and a server in an MCP session is
	/// obliged to quote real output that would look like that -- so an unquoted one that fails to
	/// parse or fails the key is either an injection attempt or a broken server, and displaying it is
	/// what would make the attempt worth making. Outside a session none of that holds: nothing is
	/// quoting anything yet, so the line is ordinary output and is delivered as such.
	/// </remarks>
	private byte[]? Reject(IProtocolContext context, string reason, byte[] line)
	{
		if (!IsNegotiated) return line;

		context.Logger.LogDebug("Dropping a line that looks like MCP because {Reason}", reason);
		return null;
	}

	private static bool StartsWith(byte[] line, byte[] prefix)
	{
		if (line.Length < prefix.Length) return false;

		for (var i = 0; i < prefix.Length; i++)
		{
			if (line[i] != prefix[i]) return false;
		}

		return true;
	}

	/// <summary>
	/// Both halves of the handshake, which is the one exchange in MCP that is not symmetric: a client
	/// answers the server's offer with the key it has chosen, and a server adopts the key the client
	/// sent it.
	/// </summary>
	private async ValueTask OnHandshakeAsync(McpMessage message, IProtocolContext context)
	{
		if (IsNegotiated)
		{
			// The handshake happens once. A second one would replace the key mid-session, which is
			// exactly the substitution the key exists to prevent.
			context.Logger.LogDebug("Ignoring a second MCP handshake on a session that already has one");
			return;
		}

		if (!message.TryGetVersion("version", out var lowest) || !message.TryGetVersion("to", out var highest))
		{
			context.Logger.LogDebug("Ignoring an MCP handshake that does not state a version range");
			return;
		}

		if (lowest > highest || lowest > Version || highest < Version)
		{
			context.Logger.LogInformation(
				"Declining MCP: the peer offers {Lowest} to {Highest} and this speaks {Version}",
				lowest, highest, Version);
			return;
		}

		if (context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Server)
		{
			var key = message.Value("authentication-key");

			if (string.IsNullOrEmpty(key))
			{
				// A server that answered this would be answering its own offer reflected back at it.
				context.Logger.LogDebug("Ignoring an MCP handshake sent to a server without a key");
				return;
			}

			_authenticationKey = key;
			context.Logger.LogDebug("MCP {Version} established", Version);
			await OnNegotiatedAsync(true);
			await AnnounceEstablishedAsync(context);
			return;
		}

		if (!AnswersOffers)
		{
			// The offer is consumed by the caller either way. This only declines the session.
			context.Logger.LogDebug("Not answering the MCP offer: this client does not open MCP sessions");
			return;
		}

		_authenticationKey = NewAuthenticationKey();

		var line = $"{Prefix}{McpMessage.Handshake} authentication-key: \"{_authenticationKey}\" "
			+ $"version: \"{Version}\" to: \"{Version}\"\r\n";
		await context.SendNegotiationAsync(context.CurrentEncoding.GetBytes(line));

		context.Logger.LogDebug("MCP {Version} established", Version);
		await OnNegotiatedAsync(true);
		await AnnounceEstablishedAsync(context);
	}

	private async ValueTask AnnounceEstablishedAsync(IProtocolContext context)
	{
		Func<ValueTask>[] handlers;
		lock (_established) handlers = _established.ToArray();

		foreach (var handler in handlers)
		{
			// One package failing to open must not stop the others, and must not take down the session
			// layer that is carrying them.
			try
			{
				await handler();
			}
			catch (Exception exception)
			{
				context.Logger.LogError(exception, "An MCP package failed to open");
			}
		}
	}

	/// <summary>
	/// A key no other player on the MUD can guess, which is the whole of what it is for.
	/// </summary>
	private static string NewAuthenticationKey()
	{
		var bytes = new byte[8];

		using (var random = RandomNumberGenerator.Create())
		{
			random.GetBytes(bytes);
		}

		var key = new StringBuilder(bytes.Length * 2);

		foreach (var b in bytes)
		{
			key.Append(b.ToString("x2", CultureInfo.InvariantCulture));
		}

		return key.ToString();
	}
}
