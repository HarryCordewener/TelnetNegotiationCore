using System;

namespace TelnetNegotiationCore.Models;

/// <summary>
/// How an application introduces itself to the servers it connects to. Set once on the builder with
/// <see cref="Builders.TelnetInterpreterBuilder.WithClientIdentity(ClientIdentity)"/>; every
/// protocol that has to name the client reads it from here.
/// </summary>
/// <remarks>
/// <para>
/// There are two channels through which a client says who it is, and this is the one fact behind
/// both of them:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>TTYPE / MTTS</b> — the first terminal type a client reports is defined as the client name,
/// the second as the terminal type, the third as the MTTS bitvector.
/// </description></item>
/// <item><description>
/// <b>NEW-ENVIRON / MNES</b> — <c>CLIENT_NAME</c>, <c>CLIENT_VERSION</c>, <c>TERMINAL_TYPE</c> and
/// <c>MTTS</c>, sent when a server asks.
/// </description></item>
/// </list>
/// <para>
/// Without an identity the library names nobody: TTYPE answers <c>UNKNOWN</c> (RFC 1091's own word
/// for a terminal that will not name itself) and NEW-ENVIRON sends no variables at all. It will not
/// introduce an application under the library's name, and it will not invent one.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// .WithClientIdentity(new ClientIdentity("MUINDEX-CRAWLER")
/// {
///     Version = "1.2.0",
///     TerminalType = "XTERM",
///     Mtts = MttsCapabilities.Ansi | MttsCapabilities.Truecolor
/// })
/// </code>
/// </example>
public sealed record ClientIdentity
{
    /// <summary>
    /// The key this identity is published under in <see cref="Plugins.IProtocolContext"/> shared
    /// state, for protocols that need to read it.
    /// </summary>
    public const string SharedStateKey = "ClientIdentity";

    /// <summary>
    /// Creates an identity for an application.
    /// </summary>
    /// <param name="name">
    /// The name of the application — not of the library it is built on. This is what an
    /// administrator reading a server log sees, and what they will search for if they want to
    /// contact whoever is connecting.
    /// </param>
    /// <exception cref="ArgumentException">The name is null, empty or whitespace.</exception>
    public ClientIdentity(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException(
                "A client identity needs a name: this is what identifies your application to every "
                + "server it connects to.", nameof(name));
        }

        Name = name.Trim();
    }

    /// <summary>
    /// The name of the application. Reported as the first TTYPE response and as MNES
    /// <c>CLIENT_NAME</c>.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// The version of the application, if it wants to report one. Reported as MNES
    /// <c>CLIENT_VERSION</c>. Not sent when null.
    /// </summary>
    public string? Version { get; init; }

    /// <summary>
    /// The terminal type the application presents, such as <c>XTERM</c>. Reported as the second
    /// TTYPE response and as MNES <c>TERMINAL_TYPE</c>. Not sent when null: an application that
    /// renders nothing has no terminal type, and the library will not invent one for it.
    /// </summary>
    public string? TerminalType { get; init; }

    /// <summary>
    /// The MTTS capabilities the application claims — the ones only it can know, such as colour
    /// support and screen readers. These are combined with the capabilities the library can observe
    /// about its own negotiation stack
    /// (<see cref="Protocols.TerminalTypeProtocol.ObservedCapabilities"/>), so an application only
    /// has to state what the library cannot work out for itself.
    /// </summary>
    public MttsCapabilities? Mtts { get; init; }
}
