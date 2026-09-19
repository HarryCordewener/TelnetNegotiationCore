using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TelnetNegotiationCore.Attributes;
using TelnetNegotiationCore.Plugins;

namespace TelnetNegotiationCore.Protocols;

/// <summary>What a Pueblo client said about itself in its <c>PUEBLOCLIENT</c> line.</summary>
/// <param name="Version">The version it announced, e.g. <c>2.50</c>; empty when it gave none.</param>
/// <param name="Checksum">The <c>md5="…"</c> value, or <see langword="null"/> when it sent none.</param>
public sealed record PuebloClient(string Version, string? Checksum);

/// <summary>
/// The Pueblo handshake: the server announces itself with a line of text, a Pueblo client answers
/// with a <c>PUEBLOCLIENT</c> line, and the server switches it to HTML mode.
/// </summary>
/// <remarks>
/// <para>
/// Pueblo has no telnet option. The whole exchange is ordinary text, framed as PennMUSH frames it
/// (<c>hdrs/conf.h</c>, <c>src/bsd.c</c>):
/// </para>
/// <code>
/// server: This world is Pueblo 1.10 Enhanced.\r\n
/// client: PUEBLOCLIENT 2.50 md5="…"
/// server: &lt;/xch_mudtext&gt;&lt;img xch_mode=purehtml&gt;&lt;xch_page clear=text&gt;\n
/// </code>
/// <para>
/// The last line is not a courtesy. It is what moves the client out of text mode; a client that
/// never receives it shows every tag the server sends as text. A repeated <c>PUEBLOCLIENT</c> — a
/// client that thinks it is still showing raw HTML — is answered again without the clear-screen.
/// </para>
/// <para>
/// <b>Adding this plugin is the opt-in</b>, as with <see cref="MSSPPlaintextProtocol"/>, and what it
/// means differs by mode:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>Server:</b> automatic. The hello goes out on every connection, where a client without Pueblo
/// shows it on its first screen, and a line beginning <c>PUEBLOCLIENT </c> is consumed wherever it
/// arrives in the session rather than reaching the application. Registering the plugin is the consent
/// to both.
/// </description></item>
/// <item><description>
/// <b>Client:</b> the server's hello and its start sequence are recognised and consumed, and
/// <see cref="OnPuebloOffered"/> reports the offer — but nothing is sent until
/// <see cref="AnnounceAsync"/> is called. Unlike a telnet option, which a server that does not
/// implement it ignores, <c>PUEBLOCLIENT</c> is real text at a login prompt, where a server without
/// Pueblo reads it as a character name. When that is worth doing is a decision about the connection,
/// so the consumer makes it.
/// </description></item>
/// </list>
/// <para>
/// Only the handshake is here. Which markup to send once a client is in Pueblo mode, and what to do
/// if it has also negotiated MXP, are the application's decisions — see
/// <see cref="OnPuebloEnabled"/>.
/// </para>
/// </remarks>
[RequiredMethod("OnPuebloEnabled", Description = "Configure the callback to handle a client switching to Pueblo mode (optional but recommended)")]
public class PuebloProtocol : TelnetProtocolPluginBase
{
	/// <summary>The line a server announces Pueblo support with.</summary>
	public const string Hello = "This world is Pueblo 1.10 Enhanced.\r\n";

	/// <summary>The command a client answers with, trailing space included, matched exactly.</summary>
	public const string ClientCommand = "PUEBLOCLIENT ";

	/// <summary>What switches a client into HTML mode and clears its text window.</summary>
	public const string Start = "</xch_mudtext><img xch_mode=purehtml><xch_page clear=text>\n";

	/// <summary>What a client already in Pueblo mode is sent again, without the clear.</summary>
	public const string Restart = "</xch_mudtext><img xch_mode=purehtml>\n";

	/// <summary>What <see cref="Start"/> and <see cref="Restart"/> share, which is how a client recognises either.</summary>
	private const string StartPrefix = "</xch_mudtext><img xch_mode=purehtml";

	/// <summary>The longest <c>md5</c> value kept, as PennMUSH's <c>PUEBLO_CHECKSUM_LEN</c>.</summary>
	private const int MaxChecksumLength = 32;

	/// <summary>What may not appear in a version or a checksum: the line is whitespace-delimited and the checksum quoted.</summary>
	private static readonly char[] s_lineBreakers = [' ', '\t', '\r', '\n', '"'];

	private volatile PuebloClient? _announced;

	private Func<PuebloClient, ValueTask>? _onPuebloEnabled;
	private Func<ValueTask>? _onPuebloOffered;
	private volatile PuebloClient? _client;
	private volatile bool _serverOffered;

	/// <inheritdoc />
	public override Type ProtocolType => typeof(PuebloProtocol);

	/// <inheritdoc />
	public override string ProtocolName => "Pueblo";

	/// <inheritdoc />
	public override IReadOnlyCollection<Type> Dependencies => [];

	/// <summary>The client, once it has sent <c>PUEBLOCLIENT</c>; <see langword="null"/> until then.</summary>
	public PuebloClient? Client => _client;

	/// <summary>
	/// Whether this connection is in Pueblo mode: on a server, the client has answered the handshake and
	/// been sent the start sequence; on a client, the server has sent it.
	/// </summary>
	public bool IsPuebloActive => _client is not null;

	/// <summary>Client mode: whether the server has announced Pueblo with its hello.</summary>
	public bool ServerOffered => _serverOffered;

	/// <summary>
	/// Called once, when this connection enters Pueblo mode: on a server, after the first
	/// <c>PUEBLOCLIENT</c> line has been answered with <see cref="Start"/>; on a client, when the server
	/// sends that sequence in answer to <see cref="AnnounceAsync"/>. PennMUSH shows its connect screen
	/// again at this point, now in HTML; that is the application's to do here.
	/// </summary>
	/// <param name="callback">
	/// Receives the identity in play: what the client said about itself on a server, and what this
	/// client announced on a client.
	/// </param>
	/// <returns>This instance for fluent chaining</returns>
	public PuebloProtocol OnPuebloEnabled(Func<PuebloClient, ValueTask>? callback)
	{
		_onPuebloEnabled = callback;
		return this;
	}

	/// <summary>
	/// Client mode: called when the server announces Pueblo with its hello. Nothing is sent in reply
	/// until <see cref="AnnounceAsync"/> is called, which is the decision this callback exists to
	/// inform.
	/// </summary>
	/// <param name="callback">Called once, when the hello arrives.</param>
	/// <returns>This instance for fluent chaining</returns>
	public PuebloProtocol OnPuebloOffered(Func<ValueTask>? callback)
	{
		_onPuebloOffered = callback;
		return this;
	}

	/// <summary>
	/// Client mode: announces this client's Pueblo support with a <c>PUEBLOCLIENT</c> line. A server that
	/// implements Pueblo answers with <see cref="Start"/>, at which point <see cref="IsPuebloActive"/> is
	/// true and <see cref="OnPuebloEnabled"/> has run.
	/// </summary>
	/// <param name="version">The version to announce; PennMUSH reads the word after the command.</param>
	/// <param name="checksum">An optional <c>md5</c> value, at most 32 characters.</param>
	/// <exception cref="InvalidOperationException">
	/// This interpreter is in server mode — a server answers this line rather than sending one — or the
	/// plugin is disabled on this connection.
	/// </exception>
	/// <exception cref="ArgumentException"><paramref name="version"/> is empty, or either argument holds whitespace.</exception>
	public async ValueTask AnnounceAsync(string version = "2.50", string? checksum = null)
	{
		if (Context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Server)
		{
			throw new InvalidOperationException(
				"A server answers PUEBLOCLIENT rather than sending one. AnnounceAsync is the client half of the handshake.");
		}

		if (!IsEnabled)
		{
			throw new InvalidOperationException(
				$"{nameof(PuebloProtocol)} is disabled on this connection, so it will not send {ClientCommand.Trim()}.");
		}

		// The line is whitespace-delimited and the checksum is quoted, so neither may carry either: a
		// space in the version would put the rest of it where the md5 goes, and a quote in the checksum
		// would end the value early.
		if (string.IsNullOrWhiteSpace(version) || version.IndexOfAny(s_lineBreakers) >= 0)
		{
			throw new ArgumentException("A version is one word, with no whitespace or quote in it.", nameof(version));
		}

		if (checksum is not null && (checksum.IndexOfAny(s_lineBreakers) >= 0 || checksum.Length > MaxChecksumLength))
		{
			throw new ArgumentException(
				$"A checksum is at most {MaxChecksumLength} characters, with no whitespace or quote in it.", nameof(checksum));
		}

		var line = checksum is null
			? $"{ClientCommand}{version}\r\n"
			: $"{ClientCommand}{version} md5=\"{checksum}\"\r\n";

		_announced = new PuebloClient(version, checksum);
		Context.Logger.LogDebug("Announcing Pueblo support (version {Version})", version);
		await Context.SendNegotiationAsync(Context.CurrentEncoding.GetBytes(line));
	}

	/// <inheritdoc />
	/// <remarks>
	/// No states or triggers: the hello goes out with the initial negotiation, and the answer is read
	/// from the assembled-line path, which is what lets this consume it.
	/// </remarks>
	public override void ConfigureStateMachine(IProtocolContext context)
	{
		if (context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Server)
		{
			context.RegisterInitialNegotiation(async () =>
			{
				if (!IsEnabled) return;
				await context.SendNegotiationAsync(Encoding.ASCII.GetBytes(Hello));
			});
		}

		context.Interpreter.RegisterInputLineObserver(async (line, encoding) =>
			await OnInputLineAsync(line, encoding, context) ? null : line);
	}

	/// <inheritdoc />
	protected override ValueTask OnInitializeAsync()
	{
		Context.Logger.LogInformation("Pueblo Protocol initialized");
		return default;
	}

	/// <inheritdoc />
	protected override async ValueTask OnProtocolDisabledAsync()
	{
		_client = null;
		_announced = null;
		_serverOffered = false;
		await OnNegotiatedAsync(false);
	}

	/// <inheritdoc />
	protected override ValueTask OnDisposeAsync()
	{
		_client = null;
		_announced = null;
		_serverOffered = false;
		return default;
	}

	/// <summary>
	/// One assembled line of input. True when it is the handshake, which must not reach the
	/// application — it would otherwise be parsed as a command.
	/// </summary>
	private async ValueTask<bool> OnInputLineAsync(byte[] line, Encoding encoding, IProtocolContext context)
	{
		if (!IsEnabled) return false;

		var text = encoding.GetString(line).TrimEnd('\r', '\n');

		if (context.Mode != Interpreters.TelnetInterpreter.TelnetMode.Server)
		{
			return await OnServerLineAsync(text, context);
		}

		if (!text.StartsWith(ClientCommand, StringComparison.Ordinal)) return false;

		if (_client is not null)
		{
			context.Logger.LogDebug("Repeated PUEBLOCLIENT; resending the Pueblo start without the clear");
			await context.SendNegotiationAsync(Encoding.ASCII.GetBytes(Restart));
			return true;
		}

		var client = Parse(text);
		await context.SendNegotiationAsync(Encoding.ASCII.GetBytes(Start));
		_client = client;
		context.Logger.LogDebug("Client switched to Pueblo mode (version {Version})", client.Version);
		await OnNegotiatedAsync(true);

		await InvokeAsync(_onPuebloEnabled is { } callback ? () => callback(client) : null, context);

		return true;
	}

	/// <summary>
	/// Client mode: the server's hello, and the start sequence that answers our own announcement. Both
	/// are protocol rather than content, so both are consumed.
	/// </summary>
	private async ValueTask<bool> OnServerLineAsync(string text, IProtocolContext context)
	{
		if (text.StartsWith(Hello.TrimEnd('\r', '\n'), StringComparison.Ordinal))
		{
			_serverOffered = true;
			context.Logger.LogDebug("Server announced Pueblo");
			await InvokeAsync(_onPuebloOffered is { } offered ? () => offered() : null, context);
			return true;
		}

		// PennMUSH sends the long form on the first PUEBLOCLIENT and the short one on a repeat; both
		// begin the same way, and both mean the same thing here.
		if (!text.StartsWith(StartPrefix, StringComparison.Ordinal)) return false;

		context.Logger.LogDebug("Server switched this connection to Pueblo mode");
		if (_client is not null) return true;

		var announced = _announced ?? new PuebloClient(string.Empty, null);
		_client = announced;
		await OnNegotiatedAsync(true);
		await InvokeAsync(_onPuebloEnabled is { } enabled ? () => enabled(announced) : null, context);
		return true;
	}

	/// <summary>
	/// Runs a host callback where a throw would otherwise reach byte processing. Contained and logged
	/// here, as <c>CharsetProtocol</c> contains its change callback: the connection is in Pueblo mode
	/// either way, and only the notification was lost.
	/// </summary>
	private static async ValueTask InvokeAsync(Func<ValueTask>? callback, IProtocolContext context)
	{
		if (callback is null) return;

		try
		{
			await callback().ConfigureAwait(false);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			context.Logger.LogError(ex,
				"A Pueblo callback threw. The connection is in Pueblo mode; only the notification was lost.");
		}
	}

	/// <summary>
	/// PennMUSH's <c>parse_puebloclient</c>: the word after the command is the version, and a
	/// <c>md5="…"</c> of at most 32 characters is the checksum.
	/// </summary>
	internal static PuebloClient Parse(string line)
	{
		var rest = line.AsSpan(ClientCommand.Length).Trim();
		var space = rest.IndexOfAny(' ', '\t');
		var version = (space < 0 ? rest : rest[..space]).ToString();
		if (version.StartsWith("md5=", StringComparison.OrdinalIgnoreCase)) version = string.Empty;

		string? checksum = null;
		var md5 = line.IndexOf("md5=\"", StringComparison.OrdinalIgnoreCase);
		if (md5 >= 0)
		{
			var value = line.AsSpan(md5 + "md5=\"".Length);
			var end = value.IndexOf('"');
			if (end is > 0 and <= MaxChecksumLength) checksum = value[..end].ToString();
		}

		return new PuebloClient(version, checksum);
	}
}
