using Microsoft.Extensions.Logging;
using OneOf;
using Stateless;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using TelnetNegotiationCore.Models;

namespace TelnetNegotiationCore.Interpreters;

/// <summary>
/// Implements RFC 1091 and MTTS
/// https://datatracker.ietf.org/doc/html/rfc1091
/// https://tintin.mudhalla.net/protocols/mtts/
/// 
/// TODO: Allow the end-user to set TerminalTypes in Client Mode.
/// TODO: Optimize byte array allocations that get commonly used.
/// </summary>
public partial class TelnetInterpreter
{
    /// <summary>
    /// A list of terminal types for this connection.
    /// </summary>
    public ImmutableList<string> TerminalTypes { get; private set; } = [];

    /// <summary>
    /// The current selected Terminal Type. Use RequestTerminalTypeAsync if you want the client to switch to the next mode.
    /// </summary>
    public string CurrentTerminalType => _CurrentTerminalType == -1
        ? "unknown"
        : TerminalTypes[Math.Min(_CurrentTerminalType, TerminalTypes.Count - 1)];

    /// <summary>
    /// Currently selected Terminal Type index.
    /// </summary>
    private int _CurrentTerminalType = -1;

#pragma warning disable CS0414 // Field is assigned but never used in this partial - used in TerminalTypeProtocol
    /// <summary>
    /// Internal Terminal Type Byte State
    /// </summary>
    private byte[] _ttypeByteState = [];

    /// <summary>
    /// Internal Terminal Type Byte Index
    /// </summary>
    private int _ttypeIndex = 0;
#pragma warning restore CS0414

    /// <summary>
    /// A dictionary for MTTS support.
    /// </summary>
    private readonly Dictionary<int, string> _MTTS = new()
    {
        { 1, "ANSI" },
        { 2, "VT100" },
        { 4, "UTF8" },
        { 8, "256 COLORS" },
        { 16, "MOUSE_TRACKING" },
        { 32, "OSC_COLOR_PALETTE" },
        { 64, "SCREEN_READER" },
        { 128, "PROXY" },
        { 256, "TRUECOLOR" },
        { 512, "MNES" },
        { 1024, "MSLP" }
    };

    /// <summary>
    /// Request Terminal Type from Client. This flips to the next one.
    /// </summary>
    public async ValueTask RequestTerminalTypeAsync()
    {
        _logger.LogDebug("Connection: {ConnectionState}", "Telling the client, to send the next Terminal Type.");
        await CallbackNegotiationAsync([
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TTYPE, (byte)Trigger.SEND, (byte)Trigger.IAC,
            (byte)Trigger.SE
        ]);
    }
}