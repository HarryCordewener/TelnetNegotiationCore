using System;
using System.Collections.Generic;

namespace TelnetNegotiationCore.Models;

/// <summary>
/// The MTTS bitvector a client reports as its third TTYPE response, and as the MNES
/// <c>MTTS</c> variable.
/// See https://tintin.mudhalla.net/protocols/mtts/.
/// </summary>
/// <remarks>
/// Most of these are claims about a terminal that only the application can make — this library
/// renders nothing and cannot know whether the host draws truecolour or is being read by a screen
/// reader. Two of them are facts about the negotiation stack itself, and those the library works
/// out for the application: see <see cref="Protocols.TerminalTypeProtocol.ObservedCapabilities"/>.
/// </remarks>
[Flags]
public enum MttsCapabilities
{
    /// <summary>No capabilities claimed.</summary>
    None = 0,

    /// <summary>Client supports ANSI colour codes.</summary>
    Ansi = 1,

    /// <summary>Client supports VT100 codes.</summary>
    Vt100 = 2,

    /// <summary>Client is using UTF-8 character encoding.</summary>
    Utf8 = 4,

    /// <summary>Client supports 256 colours.</summary>
    Colors256 = 8,

    /// <summary>Client supports mouse tracking.</summary>
    MouseTracking = 16,

    /// <summary>Client supports the OSC colour palette.</summary>
    OscColorPalette = 32,

    /// <summary>Client is being read by a screen reader.</summary>
    ScreenReader = 64,

    /// <summary>Client is a proxy, and the connection is not the end user's.</summary>
    Proxy = 128,

    /// <summary>Client supports truecolour codes.</summary>
    Truecolor = 256,

    /// <summary>Client supports MNES (Mud New Environment Standard).</summary>
    Mnes = 512,

    /// <summary>Client supports MSLP (Mud Server Link Protocol).</summary>
    Mslp = 1024
}

/// <summary>
/// The names MTTS gives its capability bits, in bit order. A server expands a client's bitvector
/// into these names; see <see cref="Protocols.TerminalTypeProtocol"/>.
/// </summary>
public static class MttsCapabilityNames
{
    private static readonly (MttsCapabilities Flag, string Name)[] s_names =
    [
        (MttsCapabilities.Ansi, "ANSI"),
        (MttsCapabilities.Vt100, "VT100"),
        (MttsCapabilities.Utf8, "UTF8"),
        (MttsCapabilities.Colors256, "256 COLORS"),
        (MttsCapabilities.MouseTracking, "MOUSE_TRACKING"),
        (MttsCapabilities.OscColorPalette, "OSC_COLOR_PALETTE"),
        (MttsCapabilities.ScreenReader, "SCREEN_READER"),
        (MttsCapabilities.Proxy, "PROXY"),
        (MttsCapabilities.Truecolor, "TRUECOLOR"),
        (MttsCapabilities.Mnes, "MNES"),
        (MttsCapabilities.Mslp, "MSLP")
    ];

    /// <summary>
    /// The MTTS capability names carried by a bitvector, in bit order. Bits MTTS has not defined
    /// are ignored rather than named.
    /// </summary>
    /// <param name="capabilities">The bitvector to expand</param>
    /// <returns>The names of the capabilities that are set</returns>
    public static IEnumerable<string> Expand(MttsCapabilities capabilities)
    {
        foreach (var (flag, name) in s_names)
        {
            if ((capabilities & flag) != 0)
            {
                yield return name;
            }
        }
    }
}
