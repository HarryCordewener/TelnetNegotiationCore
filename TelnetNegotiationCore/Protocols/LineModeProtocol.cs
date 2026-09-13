using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Plugins;

namespace TelnetNegotiationCore.Protocols;

/// <summary>
/// Line Mode protocol plugin (RFC 1184)
/// Allows negotiation of line editing and signal trapping modes
/// </summary>
/// <remarks>
/// This protocol implements RFC 1184 - Telnet Linemode Option.
/// It allows the client and server to negotiate whether line editing
/// should be done locally (on the client) or remotely (on the server).
/// 
/// The protocol supports:
/// - EDIT mode: Client performs line editing locally
/// - TRAPSIG mode: Client traps signals (interrupt, quit, etc.)
/// - MODE_ACK: Mode acknowledgment bit
/// - SOFT_TAB: Soft tab processing
/// - LIT_ECHO: Literal echo of non-printable characters
/// 
/// SLC (Set Local Characters) and FORWARDMASK subnegotiations are not
/// currently implemented but may be added in future versions.
/// </remarks>
public class LineModeProtocol : TelnetProtocolPluginBase
{
    // Mode bit constants per RFC 1184
    private const byte MODE_EDIT = 0x01;
    private const byte MODE_TRAPSIG = 0x02;
    private const byte MODE_ACK = 0x04;
    private const byte MODE_SOFT_TAB = 0x08;
    private const byte MODE_LIT_ECHO = 0x10;
    
    // Subnegotiation type constants
    private const int SUBNEG_TYPE_MODE = 1;
    private const int SUBNEG_TYPE_FORWARDMASK = 2;
    private const int SUBNEG_TYPE_SLC = 3;
    
    private byte _currentMode = 0;
    private bool _lineModeEnabled = false;
    
    private Func<byte, ValueTask>? _onModeChanged;

    /// <summary>
    /// Gets whether line mode is currently enabled
    /// </summary>
    public bool IsLineModeEnabled => _lineModeEnabled;

    /// <summary>
    /// Gets the current line mode settings
    /// </summary>
    public byte CurrentMode => _currentMode;

    /// <summary>
    /// Gets whether EDIT mode is enabled (client does local editing)
    /// </summary>
    public bool IsEditModeEnabled => (_currentMode & MODE_EDIT) != 0;

    /// <summary>
    /// Gets whether TRAPSIG mode is enabled (client traps signals)
    /// </summary>
    public bool IsTrapSigModeEnabled => (_currentMode & MODE_TRAPSIG) != 0;

    /// <summary>
    /// Gets whether SOFT_TAB mode is enabled
    /// </summary>
    public bool IsSoftTabEnabled => (_currentMode & MODE_SOFT_TAB) != 0;

    /// <summary>
    /// Gets whether LIT_ECHO mode is enabled
    /// </summary>
    public bool IsLitEchoEnabled => (_currentMode & MODE_LIT_ECHO) != 0;

    /// <inheritdoc />
    public override Type ProtocolType => typeof(LineModeProtocol);

    /// <inheritdoc />
    public override string ProtocolName => "Line Mode (RFC 1184)";

    /// <inheritdoc />
    public override IReadOnlyCollection<Type> Dependencies => Array.Empty<Type>();

    /// <summary>
    /// Sets the callback that is invoked when line mode settings change.
    /// </summary>
    /// <param name="callback">The callback to handle mode changes (receives the new mode byte)</param>
    /// <returns>This instance for fluent chaining</returns>
    public LineModeProtocol OnModeChanged(Func<byte, ValueTask>? callback)
    {
        _onModeChanged = callback;
        return this;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Negotiation acceptance and the subnegotiation are wired to the generated machine (see
    /// <see cref="OnPeerNegotiatedAsync"/> and <see cref="CompleteLineModeFromBytesAsync"/>); this
    /// hook survives only to register the server's initial offer, a cross-cutting mechanism
    /// independent of which machine drives byte processing.
    /// </remarks>
    public override void ConfigureStateMachine(IProtocolContext context)
    {
        if (context.Mode != Interpreters.TelnetInterpreter.TelnetMode.Client)
        {
            context.RegisterInitialNegotiation(async () => await SendDoLineModeAsync(context));
        }
    }

    private int _subnegotiationType = -1;
    private readonly List<byte> _buffer = new();

    /// <inheritdoc />
    protected override ValueTask OnInitializeAsync()
    {
        Context.Logger.LogInformation("Line Mode Protocol initialized");
        return default(ValueTask);
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolEnabledAsync()
    {
        Context.Logger.LogInformation("Line Mode Protocol enabled");
        return default(ValueTask);
    }

    /// <inheritdoc />
    protected override async ValueTask OnProtocolDisabledAsync()
    {
        Context.Logger.LogInformation("Line Mode Protocol disabled");
        await SetLineModeStateAsync(false);
        _currentMode = 0;
    }

    /// <inheritdoc />
    protected override ValueTask OnDisposeAsync()
    {
        _lineModeEnabled = false;
        _currentMode = 0;
        _onModeChanged = null;
        _buffer.Clear();
        return default(ValueTask);
    }

    #region Public API Methods

    /// <summary>
    /// Sends a MODE command to set line mode settings (server mode only)
    /// </summary>
    /// <param name="mode">Mode byte with flags (EDIT, TRAPSIG, MODE_ACK, etc.)</param>
    public async ValueTask SetModeAsync(byte mode)
    {
        if (!IsEnabled || Context.Mode != Interpreters.TelnetInterpreter.TelnetMode.Server)
            return;

        Context.Logger.LogDebug("Sending MODE command with mode byte: {Mode:X2}", mode);
        await Context.SendNegotiationAsync(ModeFrame(mode));
    }

    /// <summary>
    /// An <c>IAC SB LINEMODE MODE &lt;mode&gt; IAC SE</c> frame, with the mode byte escaped.
    /// </summary>
    /// <remarks>
    /// The mode byte is data, and RFC 1184 adds no exemption from RFC 854's rule that a literal 255
    /// in data is doubled. 255 is reachable: the bits RFC 1184 defines are EDIT, TRAPSIG, MODE_ACK,
    /// SOFT_TAB and LIT_ECHO, bits 32, 64 and 128 are undefined, and a peer's undefined bits are
    /// passed through rather than masked -- so a peer proposing <c>0xFB</c> is acknowledged with
    /// <c>0xFB | MODE_ACK</c>, which is 255. <see cref="SetModeAsync"/> is public and takes any byte,
    /// so an application reaches it without a peer being involved at all.
    /// <para>
    /// Sent unescaped, the peer reads that byte as the <c>IAC</c> that begins the end of the
    /// subnegotiation and then reads the real <c>IAC SE</c> as two more bytes of payload, so the
    /// frame never closes -- this library's own parser reports nothing at all for such a frame.
    /// </para>
    /// </remarks>
    private static byte[] ModeFrame(byte mode) => Helpers.SubnegotiationFrame.Build(
        (byte)Trigger.LINEMODE, (byte)Trigger.LINEMODE_MODE, mode);

    /// <summary>
    /// Sends a MODE command to enable EDIT mode (client does local line editing)
    /// </summary>
    public async ValueTask EnableEditModeAsync()
    {
        if (!IsEnabled || Context.Mode != Interpreters.TelnetInterpreter.TelnetMode.Server)
            return;

        var newMode = (byte)(_currentMode | MODE_EDIT);
        await SetModeAsync(newMode);
    }

    /// <summary>
    /// Sends a MODE command to disable EDIT mode (server does line editing)
    /// </summary>
    public async ValueTask DisableEditModeAsync()
    {
        if (!IsEnabled || Context.Mode != Interpreters.TelnetInterpreter.TelnetMode.Server)
            return;

        var newMode = (byte)(_currentMode & ~MODE_EDIT);
        await SetModeAsync(newMode);
    }

    /// <summary>
    /// Sends a MODE command to enable TRAPSIG mode (client traps signals)
    /// </summary>
    public async ValueTask EnableTrapSigModeAsync()
    {
        if (!IsEnabled || Context.Mode != Interpreters.TelnetInterpreter.TelnetMode.Server)
            return;

        var newMode = (byte)(_currentMode | MODE_TRAPSIG);
        await SetModeAsync(newMode);
    }

    /// <summary>
    /// Sends a MODE command to disable TRAPSIG mode (server handles signals)
    /// </summary>
    public async ValueTask DisableTrapSigModeAsync()
    {
        if (!IsEnabled || Context.Mode != Interpreters.TelnetInterpreter.TelnetMode.Server)
            return;

        var newMode = (byte)(_currentMode & ~MODE_TRAPSIG);
        await SetModeAsync(newMode);
    }

    #endregion

    #region State Machine Handlers

    private ValueTask SetLineModeStateAsync(bool enabled)
    {
        if (_lineModeEnabled == enabled)
            return default(ValueTask);

        _lineModeEnabled = enabled;
        Context.Logger.LogInformation("Line mode {State}", enabled ? "enabled" : "disabled");
        return default(ValueTask);
    }

    private async ValueTask OnWillLineModeAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Connection: {ConnectionState}",
            context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Server
                ? "Client is willing to use line mode"
                : "Server is willing to use line mode");
        await SetLineModeStateAsync(true);
        await OnNegotiatedAsync(true);
    }

    private async ValueTask OnWontLineModeAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Connection: {ConnectionState}",
            context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Server
                ? "Client won't use line mode"
                : "Server won't use line mode");
        await SetLineModeStateAsync(false);
        await OnNegotiatedAsync(false);
    }

    /// <summary>
    /// Every verb is answered identically regardless of which side receives it -- WILL and WONT even
    /// share their handler body verbatim but for log text, and DO/DONT are wired to the exact same
    /// methods for both server and client.
    /// </summary>
    internal async ValueTask OnPeerNegotiatedAsync(byte verb, IProtocolContext context)
    {
        switch (verb)
        {
            case (byte)Trigger.WILL:
                await OnWillLineModeAsync(context);
                break;
            case (byte)Trigger.WONT:
                await OnWontLineModeAsync(context);
                break;
            case (byte)Trigger.DO:
                await WillLineModeAsync(context);
                break;
            case (byte)Trigger.DONT:
                await OnDontLineModeAsync(context);
                break;
        }
    }

    internal ValueTask CompleteLineModeFromBytesAsync(byte kind, byte[] data, IProtocolContext context)
    {
        _subnegotiationType = kind;
        _buffer.Clear();
        _buffer.AddRange(data);
        return CompleteLineModeAsync(context);
    }

    private async ValueTask WillLineModeAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Client willing to use line mode - sending WILL");
        await context.SendNegotiationAsync(new byte[]
        {
            (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.LINEMODE
        });
        await SetLineModeStateAsync(true);
        await OnNegotiatedAsync(true);
    }

    private async ValueTask OnDontLineModeAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Server doesn't want line mode - do nothing");
        await SetLineModeStateAsync(false);
        await OnNegotiatedAsync(false);
    }

    private async ValueTask SendDoLineModeAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Server requesting line mode support - sending DO");
        await context.SendNegotiationAsync(new byte[]
        {
            (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.LINEMODE
        });
    }

    private async ValueTask CompleteLineModeAsync(IProtocolContext context)
    {
        if (_subnegotiationType == SUBNEG_TYPE_MODE)
        {
            if (_buffer.Count > 0)
            {
                var mode = _buffer[0];
                context.Logger.LogDebug("Received MODE subnegotiation: {Mode:X2}", mode);
                
                // Check if this is an acknowledgment
                var isAck = (mode & MODE_ACK) != 0;
                
                if (isAck)
                {
                    // This is an acknowledgment of a mode we sent
                    context.Logger.LogDebug("Received MODE acknowledgment");
                    // Don't send another acknowledgment back - that would create a loop
                }
                else if (Context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Server)
                {
                    // Client is proposing a mode (without ACK bit) - we should acknowledge it
                    context.Logger.LogDebug("Client proposing mode, sending acknowledgment");
                    var ackMode = (byte)(mode | MODE_ACK);
                    await context.SendNegotiationAsync(ModeFrame(ackMode));
                }
                // Client mode: If we receive a mode without ACK bit, it means the server
                // is commanding us to use this mode. We should acknowledge it.
                else if (Context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Client && !isAck)
                {
                    context.Logger.LogDebug("Server commanding mode, sending acknowledgment");
                    var ackMode = (byte)(mode | MODE_ACK);
                    await context.SendNegotiationAsync(ModeFrame(ackMode));
                }
                
                // Update current mode (remove ACK bit for storage)
                _currentMode = (byte)(mode & ~MODE_ACK);
                
                // Invoke callback if registered
                if (_onModeChanged != null)
                {
                    await _onModeChanged(_currentMode);
                }
            }
        }
        else if (_subnegotiationType == SUBNEG_TYPE_FORWARDMASK)
        {
            context.Logger.LogDebug("Received FORWARDMASK subnegotiation (not implemented)");
        }
        else if (_subnegotiationType == SUBNEG_TYPE_SLC)
        {
            context.Logger.LogDebug("Received SLC subnegotiation (not implemented)");
        }
        
        _buffer.Clear();
        _subnegotiationType = -1;
    }

    #endregion
}
