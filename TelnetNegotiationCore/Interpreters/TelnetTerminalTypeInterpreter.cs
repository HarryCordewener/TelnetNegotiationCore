using Microsoft.Extensions.Logging;
using System;
using System.Collections.Immutable;
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
    public ImmutableList<string> TerminalTypes { get; internal set; } = [];

    /// <summary>
    /// The current selected Terminal Type. Use RequestTerminalTypeAsync if you want the client to switch to the next mode.
    /// </summary>
    public string CurrentTerminalType => CurrentTerminalTypeIndex == -1
        ? "unknown"
        : TerminalTypes[Math.Min(CurrentTerminalTypeIndex, TerminalTypes.Count - 1)];

    /// <summary>
    /// Index into <see cref="TerminalTypes"/> of the selected Terminal Type, or -1 when none has been
    /// selected yet. The Terminal Type protocol plugin negotiates it and publishes it here.
    /// </summary>
    internal int CurrentTerminalTypeIndex { get; set; } = -1;

    // Cached negotiation byte array to avoid repeated allocations
    private static readonly byte[] s_requestTerminalType = [
        (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TTYPE, (byte)Trigger.SEND, (byte)Trigger.IAC,
        (byte)Trigger.SE
    ];

    /// <summary>
    /// Request Terminal Type from Client. This flips to the next one.
    /// </summary>
    public async ValueTask RequestTerminalTypeAsync()
    {
        _logger.LogDebug("Connection: {ConnectionState}", "Telling the client, to send the next Terminal Type.");
        await WriteToNetworkAsync(s_requestTerminalType);
    }
}