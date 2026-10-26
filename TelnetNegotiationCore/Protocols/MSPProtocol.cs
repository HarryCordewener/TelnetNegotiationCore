using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TelnetNegotiationCore.Attributes;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Plugins;

namespace TelnetNegotiationCore.Protocols;

/// <summary>
/// MSP (MUD Sound Protocol) plugin: telnet option 90.
/// </summary>
/// <remarks>
/// <para>
/// MSP has no subnegotiation. The server offers <c>IAC WILL MSP</c>, a client that plays sound answers
/// <c>IAC DO MSP</c>, and from then on the server writes its sound and music requests as ordinary lines of
/// output — <c>!!SOUND(name V=… L=… P=… T=… U=…)</c> and <c>!!MUSIC(name V=… L=… C=… T=… U=…)</c>. Those
/// lines are in-band text and are the host application's to recognise, play and take out of the output,
/// the same division the library keeps for MXP's tags.
/// </para>
/// <para>
/// What this plugin settles is the one fact a host needs before it does any of that: whether the option is
/// agreed. A client must not act on a <c>!!SOUND</c> line from a server it has not agreed MSP with, because
/// on such a connection the line is just text — a player can type one into a channel.
/// </para>
/// <para>
/// Registering the plugin is the client's statement that it can play sound. A client that cannot — or whose
/// user has turned sound off — should leave it unregistered, and the server's offer is then refused like any
/// other unclaimed option.
/// </para>
/// <seealso href="https://www.zuggsoft.com/zmud/msp.htm">MSP specification</seealso>
/// </remarks>
[RequiredMethod("OnMSPEnabled", Description = "Configure the callback to handle MSP activation (optional but recommended)")]
public class MSPProtocol : TelnetProtocolPluginBase
{
    private static readonly byte[] s_willMsp = [(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.MSP];
    private static readonly byte[] s_doMsp = [(byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MSP];
    private static readonly byte[] s_wontMsp = [(byte)Trigger.IAC, (byte)Trigger.WONT, (byte)Trigger.MSP];
    private static readonly byte[] s_dontMsp = [(byte)Trigger.IAC, (byte)Trigger.DONT, (byte)Trigger.MSP];

    // Written on the byte-processing loop, read by whatever thread asks IsMSPActive -- typically the host's
    // line handler deciding whether a !!SOUND line is a request or text.
    private volatile bool _mspActive;

    private Func<ValueTask>? _onMSPEnabled;
    private Func<ValueTask>? _onMSPDisabled;

    /// <summary>
    /// Sets the callback invoked when MSP is agreed: on the client when it answers the server's <c>WILL</c>,
    /// on the server when the client's <c>DO</c> arrives. Runs once per agreement.
    /// </summary>
    /// <param name="callback">The callback to handle MSP activation</param>
    /// <returns>This instance for fluent chaining</returns>
    public MSPProtocol OnMSPEnabled(Func<ValueTask>? callback)
    {
        _onMSPEnabled = callback;
        return this;
    }

    /// <summary>
    /// Sets the callback invoked when an agreed MSP is withdrawn (<c>WONT</c> or <c>DONT</c>), after which a
    /// <c>!!SOUND</c> line is text again.
    /// </summary>
    /// <param name="callback">The callback to handle MSP being turned off</param>
    /// <returns>This instance for fluent chaining</returns>
    public MSPProtocol OnMSPDisabled(Func<ValueTask>? callback)
    {
        _onMSPDisabled = callback;
        return this;
    }

    /// <summary>Whether MSP is agreed on this connection, so that sound triggers in the output are live.</summary>
    public bool IsMSPActive => _mspActive;

    /// <inheritdoc />
    public override Type ProtocolType => typeof(MSPProtocol);

    /// <inheritdoc />
    public override string ProtocolName => "MSP (MUD Sound Protocol)";

    /// <inheritdoc />
    public override IReadOnlyCollection<Type> Dependencies => Array.Empty<Type>();

    /// <inheritdoc />
    /// <remarks>
    /// Negotiation acceptance is wired to the generated machine (see <see cref="OnPeerNegotiatedAsync"/>);
    /// this hook only registers the server's initial offer.
    /// </remarks>
    public override void ConfigureStateMachine(IProtocolContext context)
    {
        if (context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Server)
        {
            context.RegisterInitialNegotiation(async () => await WillingMSPAsync(context));
        }
    }

    /// <inheritdoc />
    protected override ValueTask OnInitializeAsync()
    {
        Context.Logger.LogInformation("MSP Protocol initialized");
        return default;
    }

    /// <inheritdoc />
    protected override ValueTask OnDisposeAsync()
    {
        _mspActive = false;
        return default;
    }

    /// <summary>
    /// A peer's negotiation verb for option 90. MSP is server-offered: a server takes <c>DO</c>/<c>DONT</c>
    /// and refuses a client's <c>WILL</c>; a client takes <c>WILL</c>/<c>WONT</c> and refuses a server's
    /// <c>DO</c>, since a client has no sound of its own to send.
    /// </summary>
    internal async ValueTask OnPeerNegotiatedAsync(byte verb, IProtocolContext context)
    {
        if (context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Server)
        {
            switch (verb)
            {
                case (byte)Trigger.DO:
                    await AgreeAsync(context, "Client supports MSP.");
                    break;
                case (byte)Trigger.DONT:
                    await WithdrawAsync(context, "Client won't do MSP.", s_wontMsp);
                    break;
                case (byte)Trigger.WILL:
                    await Helpers.OptionNegotiation.AnswerAsync(false, verb, (byte)Trigger.MSP, context);
                    break;
            }
        }
        else
        {
            switch (verb)
            {
                case (byte)Trigger.WILL:
                    if (!_mspActive)
                    {
                        // Answer before telling the host, so a server that starts sending triggers on the
                        // DO cannot reach a host that has not yet been told they are live.
                        await context.SendNegotiationAsync(s_doMsp);
                    }

                    await AgreeAsync(context, "Server supports MSP.");
                    break;
                case (byte)Trigger.WONT:
                    await WithdrawAsync(context, "Server won't do MSP.", s_dontMsp);
                    break;
                case (byte)Trigger.DO:
                    await Helpers.OptionNegotiation.AnswerAsync(false, verb, (byte)Trigger.MSP, context);
                    break;
            }
        }
    }

    private async ValueTask WillingMSPAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Announcing willingness to MSP!");
        await context.SendNegotiationAsync(s_willMsp);
    }

    private async ValueTask AgreeAsync(IProtocolContext context, string message)
    {
        // A repeated WILL or DO restates an agreement already in force; RFC 1143 has it ignored rather than
        // answered, and the host's callback is not run a second time for a state that did not move.
        if (_mspActive)
        {
            context.Logger.LogDebug("MSP is already active; ignoring a repeated agreement.");
            return;
        }

        context.Logger.LogDebug("{Message}", message);
        _mspActive = true;
        await OnNegotiatedAsync(true);
        if (_onMSPEnabled != null)
            await _onMSPEnabled().ConfigureAwait(false);
    }

    /// <summary>
    /// The peer turned MSP off. RFC 1143: an option this side had agreed to is acknowledged
    /// (<paramref name="acknowledgement"/>); one it never agreed to needs no answer, and answering it would
    /// start a loop.
    /// </summary>
    private async ValueTask WithdrawAsync(IProtocolContext context, string message, byte[] acknowledgement)
    {
        context.Logger.LogDebug("{Message}", message);
        var wasActive = _mspActive;
        _mspActive = false;
        if (wasActive)
        {
            await context.SendNegotiationAsync(acknowledgement);
        }

        await OnNegotiatedAsync(false);
        if (wasActive && _onMSPDisabled != null)
            await _onMSPDisabled().ConfigureAwait(false);
    }
}
