using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Stateless;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Plugins;

namespace TelnetNegotiationCore.Protocols;

/// <summary>
/// Terminal Speed protocol plugin - RFC 1079
/// Allows exchange of terminal speed information (transmit and receive speeds in bps)
/// https://datatracker.ietf.org/doc/html/rfc1079
/// </summary>
public class TerminalSpeedProtocol : TelnetProtocolPluginBase
{
    private static readonly byte[] s_willTspeed = new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.TSPEED };
    private static readonly byte[] s_doTspeed = new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.TSPEED };

    private int _transmitSpeed = 38400;  // Default speeds
    private int _receiveSpeed = 38400;
    private Func<int, int, ValueTask>? _onTerminalSpeed;

    /// <summary>
    /// Gets the current transmit speed in bits per second
    /// </summary>
    public int TransmitSpeed => _transmitSpeed;

    /// <summary>
    /// Gets the current receive speed in bits per second
    /// </summary>
    public int ReceiveSpeed => _receiveSpeed;

    /// <inheritdoc />
    public override Type ProtocolType => typeof(TerminalSpeedProtocol);

    /// <inheritdoc />
    public override string ProtocolName => "Terminal Speed (RFC 1079)";

    /// <inheritdoc />
    public override IReadOnlyCollection<Type> Dependencies => Array.Empty<Type>();

    /// <summary>
    /// Sets the callback that is invoked when terminal speed information is received.
    /// </summary>
    /// <param name="callback">The callback to handle terminal speed (transmitSpeed, receiveSpeed in bps)</param>
    /// <returns>This instance for fluent chaining</returns>
    public TerminalSpeedProtocol OnTerminalSpeed(Func<int, int, ValueTask>? callback)
    {
        _onTerminalSpeed = callback;
        return this;
    }

    /// <summary>
    /// Sets the terminal speeds to send when requested by server (client mode).
    /// </summary>
    /// <param name="transmitSpeed">The transmit speed in bits per second</param>
    /// <param name="receiveSpeed">The receive speed in bits per second</param>
    /// <returns>This instance for fluent chaining</returns>
    public TerminalSpeedProtocol WithClientTerminalSpeed(int transmitSpeed, int receiveSpeed)
    {
        if (transmitSpeed <= 0)
            throw new ArgumentOutOfRangeException(nameof(transmitSpeed), "Transmit speed must be positive");
        if (receiveSpeed <= 0)
            throw new ArgumentOutOfRangeException(nameof(receiveSpeed), "Receive speed must be positive");

        _transmitSpeed = transmitSpeed;
        _receiveSpeed = receiveSpeed;
        return this;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Negotiation acceptance and the subnegotiation are wired to the generated machine (see
    /// <see cref="OnPeerNegotiatedAsync"/> and <see cref="CompleteTerminalSpeedFromBytesAsync"/>);
    /// this hook survives only to register the server's initial offer, a cross-cutting mechanism
    /// independent of which machine drives byte processing.
    /// </remarks>
    public override void ConfigureStateMachine(StateMachine<State, Trigger> stateMachine, IProtocolContext context)
    {
        if (context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Server)
        {
            context.RegisterInitialNegotiation(async () => await SendDoTerminalSpeedAsync(context));
        }
    }

    private async ValueTask OnDontTerminalSpeedAsClientAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Connection: {ConnectionState}", "Server telling us not to send Terminal Speed");
        await OnNegotiatedAsync(false);
    }

    private async ValueTask OnWontTerminalSpeedAsServerAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Connection: {ConnectionState}", "Client won't send Terminal Speed");
        await OnNegotiatedAsync(false);
    }

    /// <summary>What arriving at WILL/WONT (client) or DO/DONT (server) for TSPEED does.</summary>
    internal async ValueTask OnPeerNegotiatedAsync(byte verb, IProtocolContext context)
    {
        var client = context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Client;
        switch (verb)
        {
            case (byte)Trigger.DO when client:
                await WillTerminalSpeedAsync(context);
                break;
            case (byte)Trigger.DONT when client:
                await OnDontTerminalSpeedAsClientAsync(context);
                break;
            case (byte)Trigger.WILL when !client:
                await RequestTerminalSpeedAsync(context);
                break;
            case (byte)Trigger.WONT when !client:
                await OnWontTerminalSpeedAsServerAsync(context);
                break;
        }
    }

    /// <summary>What SEND means, from either side: the client reports its terminal speed.</summary>
    internal ValueTask OnRequestedAsync(IProtocolContext context) => SendTerminalSpeedAsync(context);

    /// <inheritdoc />
    protected override ValueTask OnInitializeAsync()
    {
        Context.Logger.LogInformation("Terminal Speed Protocol initialized");
        return default(ValueTask);
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolEnabledAsync()
    {
        Context.Logger.LogInformation("Terminal Speed Protocol enabled");
        return default(ValueTask);
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolDisabledAsync()
    {
        Context.Logger.LogInformation("Terminal Speed Protocol disabled");
        return default(ValueTask);
    }

    /// <inheritdoc />
    protected override ValueTask OnDisposeAsync()
    {
        _onTerminalSpeed = null;
        return default(ValueTask);
    }

    #region State Machine Handlers

    internal async ValueTask CompleteTerminalSpeedFromBytesAsync(byte[] bytes, IProtocolContext context)
    {
        if (bytes.Length == 0)
        {
            context.Logger.LogWarning("No speed data received");
            return;
        }

        var speedString = Encoding.ASCII.GetString(bytes);
        context.Logger.LogDebug("Connection: {ConnectionState}: {SpeedString}",
            "Received Terminal Speed", speedString);

        // Parse the speed string (format: "transmit,receive")
        var parts = speedString.Split(',');
        if (parts.Length == 2 &&
            int.TryParse(parts[0], out var transmit) &&
            int.TryParse(parts[1], out var receive))
        {
            _transmitSpeed = transmit;
            _receiveSpeed = receive;

            context.Logger.LogInformation("Terminal Speed set to {Transmit} bps transmit, {Receive} bps receive",
                transmit, receive);

            if (_onTerminalSpeed != null)
                await _onTerminalSpeed(transmit, receive);
        }
        else
        {
            context.Logger.LogWarning("Invalid terminal speed format: {SpeedString}. Expected format: transmit,receive",
                speedString);
        }
    }

    private async ValueTask WillTerminalSpeedAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Connection: {ConnectionState}", "Telling the server, Willing to send Terminal Speed.");
        await OnNegotiatedAsync(true);
        await context.SendNegotiationAsync(s_willTspeed);
    }

    private async ValueTask SendDoTerminalSpeedAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Connection: {ConnectionState}", "Telling the client to send Terminal Speed.");
        await context.SendNegotiationAsync(s_doTspeed);
    }

    private async ValueTask RequestTerminalSpeedAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Connection: {ConnectionState}", "Requesting Terminal Speed from client.");
        await OnNegotiatedAsync(true);
        await context.SendNegotiationAsync(new byte[]
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TSPEED, (byte)Trigger.SEND, (byte)Trigger.IAC,
            (byte)Trigger.SE
        });
    }

    private async ValueTask SendTerminalSpeedAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Connection: {ConnectionState}", "Sending Terminal Speed to server.");
        
        // Format: "transmit,receive" (e.g., "38400,38400")
        var speedString = $"{_transmitSpeed},{_receiveSpeed}";
        
        byte[] terminalSpeed =
        [
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TSPEED, (byte)Trigger.IS,
            .. Encoding.ASCII.GetBytes(speedString),
            (byte)Trigger.IAC, (byte)Trigger.SE
        ];

        await context.SendNegotiationAsync(terminalSpeed);
    }

    #endregion
}
