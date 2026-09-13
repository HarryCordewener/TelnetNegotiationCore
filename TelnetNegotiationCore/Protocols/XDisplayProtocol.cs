using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Plugins;

namespace TelnetNegotiationCore.Protocols;

/// <summary>
/// X-Display Location protocol plugin - RFC 1096
/// Allows exchange of X Window System display location information
/// https://datatracker.ietf.org/doc/html/rfc1096
/// </summary>
public class XDisplayProtocol : TelnetProtocolPluginBase
{
    private static readonly byte[] s_willXdisploc = new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.XDISPLOC };
    private static readonly byte[] s_doXdisploc = new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.XDISPLOC };

    private string _displayLocation = string.Empty;
    private Func<string, ValueTask>? _onDisplayLocation;

    /// <summary>
    /// Gets the current X display location
    /// </summary>
    public string DisplayLocation => _displayLocation;

    /// <inheritdoc />
    public override Type ProtocolType => typeof(XDisplayProtocol);

    /// <inheritdoc />
    public override string ProtocolName => "X-Display Location (RFC 1096)";

    /// <inheritdoc />
    public override IReadOnlyCollection<Type> Dependencies => Array.Empty<Type>();

    /// <summary>
    /// Sets the callback that is invoked when X display location information is received.
    /// </summary>
    /// <param name="callback">The callback to handle X display location. Pass null to clear any existing callback.</param>
    /// <returns>This instance for fluent chaining</returns>
    public XDisplayProtocol OnDisplayLocation(Func<string, ValueTask>? callback)
    {
        _onDisplayLocation = callback;
        return this;
    }

    /// <summary>
    /// Sets the X display location to send when requested by server (client mode).
    /// </summary>
    /// <param name="displayLocation">The X display location (e.g., "localhost:0.0", "host.example.com:0")</param>
    /// <returns>This instance for fluent chaining</returns>
    /// <exception cref="ArgumentNullException">Thrown when displayLocation is null</exception>
    /// <exception cref="ArgumentException">Thrown when displayLocation is empty</exception>
    public XDisplayProtocol WithClientDisplayLocation(string displayLocation)
    {
        if (displayLocation == null)
            throw new ArgumentNullException(nameof(displayLocation), "Display location cannot be null");
        if (string.IsNullOrEmpty(displayLocation))
            throw new ArgumentException("Display location cannot be empty", nameof(displayLocation));

        _displayLocation = displayLocation;
        return this;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Negotiation acceptance and the subnegotiation are wired to the generated machine (see
    /// <see cref="OnPeerNegotiatedAsync"/> and <see cref="CompleteXDisplayLocationFromBytesAsync"/>);
    /// this hook survives only to register the server's initial offer, a cross-cutting mechanism
    /// independent of which machine drives byte processing.
    /// </remarks>
    public override void ConfigureStateMachine(IProtocolContext context)
    {
        if (context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Server)
        {
            context.RegisterInitialNegotiation(async () => await SendDoXDisplayLocationAsync(context));
        }
    }

    private async ValueTask OnDontXDisplayAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Connection: {ConnectionState}", "Told not to send X Display Location");
        await OnNegotiatedAsync(false);
    }

    private async ValueTask OnWontXDisplayAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Connection: {ConnectionState}", "Peer won't do X Display Location");
        await OnNegotiatedAsync(false);
    }

    /// <summary>
    /// What arriving at each of WILL/WONT/DO/DONT for XDISPLOC does. Unlike most of this library's
    /// options, both directions are answered identically in either mode, so this needs no mode
    /// branch at all.
    /// </summary>
    internal async ValueTask OnPeerNegotiatedAsync(byte verb, IProtocolContext context)
    {
        switch (verb)
        {
            case (byte)Trigger.DO:
                await WillXDisplayAsync(context);
                break;
            case (byte)Trigger.DONT:
                await OnDontXDisplayAsync(context);
                break;
            case (byte)Trigger.WILL:
                await RequestXDisplayLocationAsync(context);
                break;
            case (byte)Trigger.WONT:
                await OnWontXDisplayAsync(context);
                break;
        }
    }

    /// <inheritdoc />
    protected override ValueTask OnInitializeAsync()
    {
        Context.Logger.LogInformation("X-Display Location Protocol initialized");
        return default(ValueTask);
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolEnabledAsync()
    {
        Context.Logger.LogInformation("X-Display Location Protocol enabled");
        return default(ValueTask);
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolDisabledAsync()
    {
        Context.Logger.LogInformation("X-Display Location Protocol disabled");
        return default(ValueTask);
    }

    /// <inheritdoc />
    protected override ValueTask OnDisposeAsync()
    {
        _onDisplayLocation = null;
        return default(ValueTask);
    }

    #region State Machine Handlers

    /// <summary>What an IS report's raw bytes mean, once read.</summary>
    internal async ValueTask CompleteXDisplayLocationFromBytesAsync(byte[] bytes, IProtocolContext context)
    {
        if (bytes.Length == 0)
        {
            context.Logger.LogWarning("No X display location data received");
            return;
        }

        var displayString = Encoding.ASCII.GetString(bytes);
        context.Logger.LogDebug("Connection: {ConnectionState}: {DisplayLocation}",
            "Received X Display Location", displayString);

        _displayLocation = displayString;

        context.Logger.LogInformation("X Display Location set to {DisplayLocation}", displayString);

        if (_onDisplayLocation != null)
            await _onDisplayLocation(displayString);
    }

    /// <summary>What SEND means, from either side -- independent of which machine asked.</summary>
    internal ValueTask OnRequestedAsync(IProtocolContext context) => SendXDisplayLocationAsync(context);

    private async ValueTask WillXDisplayAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Connection: {ConnectionState}", "Telling the server, Willing to send X Display Location.");
        await OnNegotiatedAsync(true);
        await context.SendNegotiationAsync(s_willXdisploc);
    }

    private async ValueTask SendDoXDisplayLocationAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Connection: {ConnectionState}", "Telling the client to send X Display Location.");
        await context.SendNegotiationAsync(s_doXdisploc);
    }

    private async ValueTask RequestXDisplayLocationAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Connection: {ConnectionState}", "Requesting X Display Location from client.");
        await OnNegotiatedAsync(true);
        await context.SendNegotiationAsync(new byte[]
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.XDISPLOC, (byte)Trigger.SEND, (byte)Trigger.IAC,
            (byte)Trigger.SE
        });
    }

    private async ValueTask SendXDisplayLocationAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Connection: {ConnectionState}", "Sending X Display Location to server.");
        
        // Use configured display location or empty string if not configured
        var displayString = _displayLocation;
        
        byte[] xDisplayLocation =
        [
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.XDISPLOC, (byte)Trigger.IS,
            .. Encoding.ASCII.GetBytes(displayString),
            (byte)Trigger.IAC, (byte)Trigger.SE
        ];

        await context.SendNegotiationAsync(xDisplayLocation);
    }

    #endregion
}
