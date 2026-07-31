using System;
using System.Collections.Generic;
using System.Linq;

namespace TelnetNegotiationCore.Models;

/// <summary>
/// The MUD New-Environ Standard's variable vocabulary.
/// https://tintin.mudhalla.net/protocols/mnes/
/// </summary>
/// <remarks>
/// <para>
/// MNES is a profile of RFC 1572 NEW-ENVIRON, not a separate option: the same option 39, the same
/// IS/SEND/INFO and VAR/VAL codes, with an agreed set of variable names a MUD client and server
/// actually have use for. Nothing here changes the wire format.
/// </para>
/// <para>
/// The names matter because RFC 1572's own well-known variables mean something else. <c>USER</c>
/// there is the account to log in as — a telnet analogue of a login prompt — and has nothing to do
/// with the operating-system account of whoever happens to be running a MUD client. MNES answers the
/// question RFC 1572 does not: what does a *MUD* client have to say about itself?
/// </para>
/// </remarks>
public static class MnesVariables
{
    /// <summary>The encoding the client is using, e.g. <c>UTF-8</c>.</summary>
    public const string Charset = "CHARSET";

    /// <summary>The name of the client. The application's name, never this library's.</summary>
    public const string ClientName = "CLIENT_NAME";

    /// <summary>The client's version.</summary>
    public const string ClientVersion = "CLIENT_VERSION";

    /// <summary>The client's IP address, where the client knows it and chooses to say.</summary>
    public const string IpAddress = "IPADDRESS";

    /// <summary>The MTTS bitvector reflecting the client's current state.</summary>
    public const string Mtts = "MTTS";

    /// <summary>The terminal type name.</summary>
    public const string TerminalType = "TERMINAL_TYPE";

    /// <summary>Every variable the standard defines for a client to report.</summary>
    public static readonly IReadOnlyList<string> All =
        [Charset, ClientName, ClientVersion, IpAddress, Mtts, TerminalType];

    /// <summary>
    /// Whether a name is a legal MNES variable name: upper-case letters and underscores only.
    /// </summary>
    /// <remarks>
    /// Checked rather than assumed, because a name is written into a subnegotiation unescaped. This
    /// does not restrict a caller to <see cref="All"/> — a client and server that agree on a variable
    /// the standard does not list are still speaking MNES.
    /// </remarks>
    public static bool IsLegalName(string? name) =>
        !string.IsNullOrEmpty(name) && name.All(c => c is '_' or (>= 'A' and <= 'Z'));

    /// <summary>
    /// Whether a value can be sent without corrupting the subnegotiation.
    /// </summary>
    /// <remarks>
    /// MNES: "Values cannot contain VAR, VAL, ESC, USERVAR, or IAC bytes." Those are 0, 1, 2, 3 and
    /// 255 — the first four collide with the framing codes and the last needs doubling. A value
    /// carrying one of them does not produce a wrong variable, it produces a subnegotiation the other
    /// side cannot parse, so it is refused at the door rather than escaped into something the
    /// standard says is not a value.
    /// </remarks>
    public static bool IsLegalValue(string? value) =>
        value is not null && !value.Any(IsFramingByte);

    /// <summary>VAR (0), VAL (1), ESC (2) and USERVAR (3) from RFC 1572, plus IAC (255).</summary>
    private static bool IsFramingByte(char c) => c is (char)0 or (char)1 or (char)2 or (char)3 or (char)255;
}
