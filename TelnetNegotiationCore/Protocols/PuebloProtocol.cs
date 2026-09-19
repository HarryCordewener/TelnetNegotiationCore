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
/// <b>Adding this plugin is the opt-in</b>, as with <see cref="MSSPPlaintextProtocol"/>. The hello is
/// unsolicited text on every connection, which a client without Pueblo shows on its first screen, and
/// a line beginning <c>PUEBLOCLIENT </c> is consumed wherever it arrives in the session rather than
/// reaching the application. Registering the plugin is the consent to both. Server mode only; in
/// client mode it does nothing.
/// </para>
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

	/// <summary>The longest <c>md5</c> value kept, as PennMUSH's <c>PUEBLO_CHECKSUM_LEN</c>.</summary>
	private const int MaxChecksumLength = 32;

	private Func<PuebloClient, ValueTask>? _onPuebloEnabled;
	private volatile PuebloClient? _client;

	/// <inheritdoc />
	public override Type ProtocolType => typeof(PuebloProtocol);

	/// <inheritdoc />
	public override string ProtocolName => "Pueblo";

	/// <inheritdoc />
	public override IReadOnlyCollection<Type> Dependencies => [];

	/// <summary>The client, once it has sent <c>PUEBLOCLIENT</c>; <see langword="null"/> until then.</summary>
	public PuebloClient? Client => _client;

	/// <summary>Whether the client has answered the handshake and been switched to HTML mode.</summary>
	public bool IsPuebloActive => _client is not null;

	/// <summary>
	/// Called once, after the first <c>PUEBLOCLIENT</c> line has been answered with <see cref="Start"/>.
	/// PennMUSH shows its connect screen again at this point, now in HTML; that is the application's
	/// to do here.
	/// </summary>
	/// <param name="callback">Receives what the client said about itself.</param>
	/// <returns>This instance for fluent chaining</returns>
	public PuebloProtocol OnPuebloEnabled(Func<PuebloClient, ValueTask>? callback)
	{
		_onPuebloEnabled = callback;
		return this;
	}

	/// <inheritdoc />
	/// <remarks>
	/// No states or triggers: the hello goes out with the initial negotiation, and the answer is read
	/// from the assembled-line path, which is what lets this consume it.
	/// </remarks>
	public override void ConfigureStateMachine(IProtocolContext context)
	{
		if (context.Mode != Interpreters.TelnetInterpreter.TelnetMode.Server)
		{
			return;
		}

		context.RegisterInitialNegotiation(async () =>
		{
			if (!IsEnabled) return;
			await context.SendNegotiationAsync(Encoding.ASCII.GetBytes(Hello));
		});

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
		await OnNegotiatedAsync(false);
	}

	/// <inheritdoc />
	protected override ValueTask OnDisposeAsync()
	{
		_client = null;
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

		if (_onPuebloEnabled is { } callback)
		{
			await callback(client).ConfigureAwait(false);
		}

		return true;
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
