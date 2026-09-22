using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TelnetNegotiationCore.Attributes;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Plugins;

namespace TelnetNegotiationCore.Protocols;

/// <summary>
/// MXP (MUD eXtension Protocol) plugin implementation.
/// MXP uses telnet option 91 (0x5B) and enables rich content tags in MUD output.
/// </summary>
/// <remarks>
/// <para>
/// MXP takes two steps, not one. <c>WILL</c>/<c>DO</c> settles the telnet option, and then the
/// server sends <c>IAC SB MXP IAC SE</c> — the marker that means "everything after this is MXP".
/// Only at that marker does a client start parsing tags and decoding entities; before it, a client
/// is in plain telnet and shows the server's <c>&lt;send&gt;</c> tags and <c>&amp;quot;</c>
/// entities to the player verbatim. Both halves live here: the server sends the marker when the
/// client says <c>DO</c>, and the client recognises it when a server sends it.
/// </para>
/// <para>
/// What the marker does <i>not</i> do is change how the byte stream is framed — unlike MCCP's
/// identically shaped one, which turns everything after it into a zlib stream. Ordinary text keeps
/// flowing through the same state machine.
/// </para>
/// <para>
/// Line modes (<c>ESC[0z</c> open, <c>ESC[1z</c> secure, <c>ESC[2z</c> locked, <c>ESC[6z</c> lock
/// secure, …) are in-band output, not negotiation, so they are the host application's to write and
/// deliberately not sent from here: which lines a game is willing to let carry live tags is its
/// policy, not this library's.
/// </para>
/// <para>
/// Call <see cref="OnMXPEnabled"/> to be told when MXP output actually begins — after the marker
/// on both sides, so a host that switches renderers in that callback cannot emit a tag ahead of it.
/// </para>
/// <seealso href="https://www.zuggsoft.com/zmud/mxp.htm">MXP specification</seealso>
/// <seealso href="https://www.gammon.com.au/mushclient/addingservermxp.htm">Adding MXP support to a MUD server</seealso>
/// </remarks>
[RequiredMethod("OnMXPEnabled", Description = "Configure the callback to handle MXP activation (optional but recommended)")]
public class MXPProtocol : TelnetProtocolPluginBase
{
    private static readonly byte[] s_willMxp = [(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.MXP];
    private static readonly byte[] s_doMxp = [(byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MXP];

    /// <summary><c>IAC SB MXP IAC SE</c> — the marker that starts MXP mode.</summary>
    private static readonly byte[] s_sbMxp =
        [(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.MXP, (byte)Trigger.IAC, (byte)Trigger.SE];

    /// <summary>The mode a tag has to be on to be read: <c>ESC[1z</c>, secure.</summary>
    private const string SecureLine = "\u001b[1z";

    private bool? _mxpEnabled = null;

    private Func<MxpSupport, ValueTask>? _onSupports;
    private Func<MxpVersion, ValueTask>? _onVersion;
    private Func<IReadOnlyList<string>, ValueTask>? _onSupportRequested;
    private Func<ValueTask>? _onVersionRequested;
    private string[]? _queryOnStart;
    private volatile bool _disposed;
    private MxpSupport _support = MxpSupport.None;
    private volatile MxpVersion? _peerVersion;

    // Written on the byte-processing loop, read by whatever thread asks IsMxpModeStarted -- the same
    // treatment IsNegotiated gets in the base class, and for the same reason.
    private volatile bool _mxpModeStarted;
    private Func<ValueTask>? _onMXPEnabled;

    /// <summary>
    /// Sets the callback that is invoked when MXP output begins — once the start marker has been
    /// sent (server) or received (client), not when the option is merely negotiated.
    /// </summary>
    /// <param name="callback">The callback to handle MXP activation</param>
    /// <returns>This instance for fluent chaining</returns>
    public MXPProtocol OnMXPEnabled(Func<ValueTask>? callback)
    {
        _onMXPEnabled = callback;
        return this;
    }

    /// <summary>
    /// Indicates whether the MXP telnet option has been negotiated. This is not yet a licence to
    /// write tags — see <see cref="IsMxpModeStarted"/>.
    /// </summary>
    public bool IsMXPActive => _mxpEnabled == true;

    /// <summary>
    /// Indicates whether MXP mode has actually started: this side has sent <c>IAC SB MXP IAC SE</c>
    /// (server) or seen it (client). Until then a peer treats tags and entities as literal text.
    /// </summary>
    public bool IsMxpModeStarted => _mxpModeStarted;

    /// <inheritdoc />
    public override Type ProtocolType => typeof(MXPProtocol);

    /// <inheritdoc />
    public override string ProtocolName => "MXP (MUD eXtension Protocol)";

    /// <inheritdoc />
    public override IReadOnlyCollection<Type> Dependencies => Array.Empty<Type>();

    /// <inheritdoc />
    /// <remarks>
    /// Negotiation acceptance and the start marker are wired to the generated machine (see
    /// <see cref="OnPeerNegotiatedAsync"/> and <c>MxpStartedAsync</c> in
    /// <c>TelnetGeneratedMachineInterpreter</c>); this hook survives only to register the server's
    /// initial offer, a cross-cutting mechanism independent of which machine drives byte processing.
    /// </remarks>
    /// <summary>
    /// What the peer said it can render, from every <c>&lt;SUPPORTS&gt;</c> reply so far. Empty until one
    /// arrives: a reply answers what was asked, and nothing is assumed about what was not.
    /// </summary>
    public MxpSupport Support => _support;

    /// <summary>What the peer said about itself in a <c>&lt;VERSION&gt;</c> reply, or null if it has not.</summary>
    public MxpVersion? PeerVersion => _peerVersion;

    /// <summary>Sets the callback run when a <c>&lt;SUPPORTS&gt;</c> reply arrives, carrying that reply alone.</summary>
    /// <returns>This instance for fluent chaining</returns>
    public MXPProtocol OnMxpSupports(Func<MxpSupport, ValueTask>? callback)
    {
        _onSupports = callback;
        return this;
    }

    /// <summary>Sets the callback run when a <c>&lt;VERSION&gt;</c> reply arrives.</summary>
    /// <returns>This instance for fluent chaining</returns>
    public MXPProtocol OnMxpVersion(Func<MxpVersion, ValueTask>? callback)
    {
        _onVersion = callback;
        return this;
    }

    /// <summary>
    /// Sets the callback run when the peer asks what this side supports, carrying what it asked about —
    /// empty for a bare <c>&lt;SUPPORT&gt;</c>, which asks for everything. Answer with
    /// <see cref="SendSupportsAsync"/>; nothing is sent for you, because what a client admits to is the
    /// host's to decide.
    /// </summary>
    /// <returns>This instance for fluent chaining</returns>
    public MXPProtocol OnMxpSupportRequested(Func<IReadOnlyList<string>, ValueTask>? callback)
    {
        _onSupportRequested = callback;
        return this;
    }

    /// <summary>Sets the callback run when the peer asks for a version. Answer with <see cref="SendVersionAsync"/>.</summary>
    /// <returns>This instance for fluent chaining</returns>
    public MXPProtocol OnMxpVersionRequested(Func<ValueTask>? callback)
    {
        _onVersionRequested = callback;
        return this;
    }

    /// <summary>
    /// Asks the peer what it supports as soon as MXP mode starts, instead of waiting for a
    /// <see cref="RequestSupportAsync"/> of your own. <paramref name="queries"/> are the entries to ask
    /// about; none asks for everything.
    /// </summary>
    /// <returns>This instance for fluent chaining</returns>
    public MXPProtocol QuerySupportOnStart(params string[] queries)
    {
        _queryOnStart = queries ?? [];
        return this;
    }

    /// <summary>
    /// Sends <c>&lt;SUPPORT&gt;</c>, asking the peer which tags and arguments it can render. With no
    /// <paramref name="queries"/> it asks for everything; otherwise each is a tag (<c>image</c>), one of
    /// a tag's arguments (<c>send.expire</c>), or a pattern the specification allows (<c>"color.*"</c>).
    /// The answer arrives as a <c>&lt;SUPPORTS&gt;</c> reply — <see cref="OnMxpSupports"/>, and
    /// <see cref="Support"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">MXP mode has not started on this connection.</exception>
    public ValueTask RequestSupportAsync(params string[] queries) =>
        SendTagAsync(queries is { Length: > 0 } asked ? "<SUPPORT " + string.Join(" ", asked) + ">" : "<SUPPORT>");

    /// <summary>
    /// Sends <c>&lt;VERSION&gt;</c>, asking the peer to name itself. The answer arrives as a
    /// <c>&lt;VERSION …&gt;</c> reply — <see cref="OnMxpVersion"/>, and <see cref="PeerVersion"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">MXP mode has not started on this connection.</exception>
    public ValueTask RequestVersionAsync() => SendTagAsync("<VERSION>");

    /// <summary>
    /// Answers a <c>&lt;SUPPORT&gt;</c> with what this side renders. Each entry goes out as <c>+entry</c>
    /// or <c>-entry</c>, in the shape the specification gives: a tag, or one of a tag's arguments.
    /// </summary>
    /// <exception cref="InvalidOperationException">MXP mode has not started on this connection.</exception>
    public ValueTask SendSupportsAsync(IEnumerable<string> supported, IEnumerable<string>? unsupported = null) =>
        SendTagAsync(new MxpSupport(supported ?? [], unsupported ?? []).ToString());

    /// <summary>Answers a <c>&lt;VERSION&gt;</c> with what this side is.</summary>
    /// <exception cref="InvalidOperationException">MXP mode has not started on this connection.</exception>
    public ValueTask SendVersionAsync(MxpVersion version) =>
        SendTagAsync((version ?? throw new ArgumentNullException(nameof(version))).ToString());

    /// <inheritdoc />
    public override void ConfigureStateMachine(IProtocolContext context)
    {
        if (context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Server)
        {
            context.RegisterInitialNegotiation(async () => await WillingMXPAsync(context));
        }

        // <SUPPORTS> and <VERSION> come back as ordinary lines, the way a Pueblo handshake does, so they
        // are read off the assembled-line path and consumed: they are the peer answering a question this
        // plugin asked, not something the application typed or printed.
        context.Interpreter.RegisterInputLineObserver(async (line, encoding) =>
            await OnInputLineAsync(line, encoding, context) ? null : line);
    }

    /// <summary>
    /// One assembled line. True when it belongs to this exchange and must not reach the application.
    /// </summary>
    private async ValueTask<bool> OnInputLineAsync(byte[] line, Encoding encoding, IProtocolContext context)
    {
        if (!IsEnabled || _disposed || !_mxpModeStarted) return false;

        var text = StripLineMode(encoding.GetString(line).Trim());
        if (text.Length < 2 || text[0] != '<' || text[text.Length - 1] != '>') return false;

        var inside = text.Substring(1, text.Length - 2).Trim();

        if (StartsWithWord(inside, "SUPPORTS"))
        {
            var report = ParseSupports(inside);
            _support = _support.With(report);
            context.Logger.LogDebug("MXP peer supports: {Supports}", report);
            await InvokeAsync(_onSupports is { } supports ? () => supports(report) : null, context);
            return true;
        }

        if (StartsWithWord(inside, "VERSION") && context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Server)
        {
            var version = ParseVersion(inside);
            _peerVersion = version;
            context.Logger.LogDebug("MXP peer version: {Version}", version);
            await InvokeAsync(_onVersion is { } onVersion ? () => onVersion(version) : null, context);
            return true;
        }

        // The other half of each exchange: a client reads the questions, and answers only if its host
        // decides to.
        if (context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Server) return false;

        if (StartsWithWord(inside, "SUPPORT"))
        {
            var asked = Arguments(inside, "SUPPORT");
            await InvokeAsync(_onSupportRequested is { } requested ? () => requested(asked) : null, context);
            return true;
        }

        if (!StartsWithWord(inside, "VERSION")) return false;

        await InvokeAsync(_onVersionRequested is { } versionRequested ? () => versionRequested() : null, context);
        return true;
    }

    /// <inheritdoc />
    protected override ValueTask OnInitializeAsync()
    {
        Context.Logger.LogInformation("MXP Protocol initialized");
        return default;
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolEnabledAsync()
    {
        // Deliberately does not touch _mxpEnabled. This hook means "the plugin is attached and
        // processing", which is what IsEnabled reports; IsMXPActive answers the different question of
        // whether the peer agreed to option 91, and only a real DO/WILL from the peer may set it.
        // Conflating the two is the confusion IsNegotiated was added in 2.9.0 to end.
        Context.Logger.LogInformation("MXP Protocol enabled");
        return default;
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolDisabledAsync()
    {
        Context.Logger.LogInformation("MXP Protocol disabled");
        _mxpEnabled = false;
        _mxpModeStarted = false;
        return default;
    }

    /// <inheritdoc />
    protected override ValueTask OnDisposeAsync()
    {
        _disposed = true;
        _mxpEnabled = null;
        _mxpModeStarted = false;
        return default;
    }

    #region State Machine Handlers

    /// <summary>
    /// A server normally expects DO/DONT and a client normally expects WILL/WONT for this option.
    /// A wrong-direction request is still owed the RFC 854 refusal paired with that verb; several
    /// deployed servers probe with DO MXP before making the conventional WILL MXP offer.
    /// </summary>
    internal async ValueTask OnPeerNegotiatedAsync(byte verb, IProtocolContext context)
    {
        if (context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Server)
        {
            switch (verb)
            {
                case (byte)Trigger.DO:
                    await OnDoMXPAsync(context);
                    break;
                case (byte)Trigger.DONT:
                    await OnDontMXPAsync(context);
                    break;
            }
        }
        else
        {
            switch (verb)
            {
                case (byte)Trigger.DO:
                    await Helpers.OptionNegotiation.AnswerAsync(false, verb, (byte)Trigger.MXP, context);
                    break;
                case (byte)Trigger.WILL:
                    await OnWillMXPAsync(context);
                    break;
                case (byte)Trigger.WONT:
                    await WontMXPAsync(context);
                    break;
            }
        }
    }

    private async ValueTask WillingMXPAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Announcing willingness to MXP!");
        await context.SendNegotiationAsync(s_willMxp);
    }

    private async ValueTask OnDoMXPAsync(IProtocolContext context)
    {
        // A peer may re-affirm DO mid-session. The marker below means "MXP output starts here", and
        // restating it in a session whose tags are already flowing is not something to obey; nor
        // should the host's activation callback — which is typically what swaps its renderer over —
        // run a second time for a state that did not move.
        if (_mxpEnabled == true)
        {
            context.Logger.LogDebug("MXP is already active; ignoring a repeated DO MXP.");
            return;
        }

        context.Logger.LogDebug("Client supports MXP.");
        _mxpEnabled = true;
        await OnNegotiatedAsync(true);

        // MXP does not begin at DO. Until this marker arrives the client is in plain telnet and
        // renders every tag and entity literally, so it goes out before the callback that lets the
        // host start writing them.
        await context.SendNegotiationAsync(s_sbMxp);
        await StartMxpModeAsync(context);
    }

    private async ValueTask OnDontMXPAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Client won't do MXP - do nothing");
        _mxpEnabled = false;
        _mxpModeStarted = false;
        await OnNegotiatedAsync(false);
    }

    private async ValueTask WontMXPAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Server won't do MXP - do nothing");
        _mxpEnabled = false;
        _mxpModeStarted = false;
        await OnNegotiatedAsync(false);
    }

    private async ValueTask OnWillMXPAsync(IProtocolContext context)
    {
        if (_mxpEnabled == true)
        {
            context.Logger.LogDebug("MXP is already active; ignoring a repeated WILL MXP.");
            return;
        }

        context.Logger.LogDebug("Server supports MXP.");
        _mxpEnabled = true;
        await OnNegotiatedAsync(true);
        await context.SendNegotiationAsync(s_doMxp);

        // No callback here: the server has agreed to speak MXP but has not started. That is
        // IAC SB MXP IAC SE, handled in ConfigureStartMarker.
    }

    /// <summary>
    /// Marks MXP mode started and tells the host, once.
    /// </summary>
    internal async ValueTask StartMxpModeAsync(IProtocolContext context)
    {
        // The marker says when a negotiated option begins, and cannot stand in for negotiating it.
        // A peer that sends IAC SB MXP IAC SE without a WILL/DO exchange behind it would otherwise
        // switch this side into MXP mode -- running the host's activation callback, and leaving
        // IsMxpModeStarted true while IsMXPActive said the option was never agreed to.
        if (_mxpEnabled != true)
        {
            context.Logger.LogWarning("Ignoring an MXP start marker: option 91 was never negotiated.");
            return;
        }

        if (_mxpModeStarted)
        {
            context.Logger.LogDebug("MXP mode already started; ignoring a repeated start marker.");
            return;
        }

        _mxpModeStarted = true;
        context.Logger.LogInformation("MXP mode has started; tags and entities are live from here on.");

        if (_onMXPEnabled != null)
            await _onMXPEnabled().ConfigureAwait(false);

        if (_queryOnStart is { } queries)
        {
            await RequestSupportAsync(queries);
        }
    }

    #endregion

    #region The capability exchange

    /// <summary>Writes one MXP tag on a secure line, which is the only mode tags are read in.</summary>
    private async ValueTask SendTagAsync(string tag)
    {
        if (!IsInitialized || _disposed)
        {
            throw new InvalidOperationException($"{nameof(MXPProtocol)} is not taking part in this connection.");
        }

        if (!_mxpModeStarted)
        {
            throw new InvalidOperationException(
                "MXP mode has not started on this connection, so a tag sent now would reach the peer as text.");
        }

        await Context.SendNegotiationAsync(Context.CurrentEncoding.GetBytes(SecureLine + tag + "\r\n"));
    }

    /// <summary>Drops a leading line-mode sequence (<c>ESC[1z</c>), which a peer may put before its reply.</summary>
    private static string StripLineMode(string text)
    {
        while (text.Length > 3 && text[0] == '\u001b' && text[1] == '[')
        {
            var z = text.IndexOf('z');
            if (z < 2 || z > 4) break;
            text = text.Substring(z + 1).TrimStart();
        }

        return text;
    }

    /// <summary>Whether <paramref name="inside"/> begins with <paramref name="word"/> as a whole word.</summary>
    private static bool StartsWithWord(string inside, string word) =>
        inside.StartsWith(word, StringComparison.OrdinalIgnoreCase)
        && (inside.Length == word.Length || char.IsWhiteSpace(inside[word.Length]));

    /// <summary>What a tag was given, whitespace-separated, with the tag name itself removed.</summary>
    private static string[] Arguments(string inside, string word) =>
        inside.Length == word.Length
            ? []
            : inside.Substring(word.Length).Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Reads a <c>&lt;SUPPORTS +b -image +color.fore&gt;</c> reply. An entry without a sign is ignored:
    /// the specification writes every one with <c>+</c> or <c>-</c>, and a bare word says nothing about
    /// which it meant.
    /// </summary>
    internal static MxpSupport ParseSupports(string inside)
    {
        var supported = new List<string>();
        var unsupported = new List<string>();

        foreach (var entry in Arguments(inside, "SUPPORTS"))
        {
            var name = entry.Substring(1).Trim('"');
            if (name.Length == 0) continue;

            if (entry[0] == '+') supported.Add(name);
            else if (entry[0] == '-') unsupported.Add(name);
        }

        return new MxpSupport(supported, unsupported);
    }

    /// <summary>
    /// Reads a <c>&lt;VERSION MXP=0.4 CLIENT=zmud VERSION=6.07 REGISTERED=yes&gt;</c> reply. Values may be
    /// quoted; an attribute that is not one of the five is ignored.
    /// </summary>
    internal static MxpVersion ParseVersion(string inside)
    {
        string? mxp = null, style = null, client = null, version = null;
        bool? registered = null;

        foreach (var field in Arguments(inside, "VERSION"))
        {
            var equals = field.IndexOf('=');
            if (equals <= 0) continue;

            var name = field.Substring(0, equals);
            var value = field.Substring(equals + 1).Trim('"');

            if (name.Equals("MXP", StringComparison.OrdinalIgnoreCase)) mxp = value;
            else if (name.Equals("STYLE", StringComparison.OrdinalIgnoreCase)) style = value;
            else if (name.Equals("CLIENT", StringComparison.OrdinalIgnoreCase)) client = value;
            else if (name.Equals("VERSION", StringComparison.OrdinalIgnoreCase)) version = value;
            else if (name.Equals("REGISTERED", StringComparison.OrdinalIgnoreCase))
                registered = value.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }

        return new MxpVersion(mxp, style, client, version, registered);
    }

    /// <summary>
    /// Runs a host callback where a throw would otherwise reach byte processing. Contained and logged
    /// here, as CharsetProtocol contains its change callback: the exchange stands either way, and only
    /// the notification was lost.
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
            context.Logger.LogError(ex, "An MXP callback threw. The exchange stands; only the notification was lost.");
        }
    }

    #endregion
}
