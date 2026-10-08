using System;
using TelnetNegotiationCore.Gmcp;
using TelnetNegotiationCore.Protocols;

namespace TelnetNegotiationCore.Builders;

/// <summary>
/// Wires a <c>TelnetNegotiationCore.Gmcp</c> session to the GMCP plugin.
/// </summary>
/// <remarks>
/// The session gets every GMCP message before the <see cref="GMCPProtocol.OnGMCPMessage"/>
/// callback does, and acts on agreement before the <see cref="GMCPProtocol.OnGMCPNegotiated"/>
/// callback does. Both callbacks still run, whichever order they were set in. A second session
/// on the same plugin replaces the first.
/// </remarks>
public static class GmcpSessionExtensions
{
    /// <summary>
    /// Creates a client session on this connection. When the server agrees to GMCP it sends
    /// <c>Core.Hello</c> with the name and version from
    /// <see cref="TelnetInterpreterBuilder.WithClientIdentity(Models.ClientIdentity)"/>, then
    /// <c>Core.Supports.Set</c> with <paramref name="modules"/>, and again after each
    /// renegotiation.
    /// </summary>
    /// <remarks>
    /// With no client identity there is no name to send, so <c>Core.Hello</c> is skipped;
    /// <c>Core.Supports.Set</c> is still sent. With no modules, <c>Core.Supports.Set</c> is skipped.
    /// </remarks>
    /// <example>
    /// <code>
    /// var telnet = await new TelnetInterpreterBuilder()
    ///     .UseMode(TelnetInterpreter.TelnetMode.Client)
    ///     .WithClientIdentity("MyClient", "1.0")
    ///     .AddPlugin&lt;GMCPProtocol&gt;()
    ///         .UseGmcpClientSession(out var gmcp, new("Char", 1), new("Room", 1))
    ///     .BuildAsync();
    /// </code>
    /// </example>
    /// <param name="context">The GMCP plugin configuration context</param>
    /// <param name="session">The new session, which sends through this connection</param>
    /// <param name="modules">The modules to list in <c>Core.Supports.Set</c></param>
    /// <returns>The configuration context for continued chaining</returns>
    public static PluginConfigurationContext<GMCPProtocol> UseGmcpClientSession(
        this PluginConfigurationContext<GMCPProtocol> context,
        out GmcpClientSession session,
        params GmcpModule[] modules)
    {
        var plugin = context.Plugin;
        session = new GmcpClientSession(plugin.SendAsync);
        return context.UseGmcpClientSession(session, modules);
    }

    /// <summary>
    /// Attaches an existing client session, which keeps its own way of sending. Otherwise as
    /// <see cref="UseGmcpClientSession(PluginConfigurationContext{GMCPProtocol}, out GmcpClientSession, GmcpModule[])"/>.
    /// </summary>
    /// <param name="context">The GMCP plugin configuration context</param>
    /// <param name="session">The session</param>
    /// <param name="modules">The modules to list in <c>Core.Supports.Set</c></param>
    /// <returns>The configuration context for continued chaining</returns>
    public static PluginConfigurationContext<GMCPProtocol> UseGmcpClientSession(
        this PluginConfigurationContext<GMCPProtocol> context,
        GmcpClientSession session,
        params GmcpModule[] modules)
    {
        if (session == null)
            throw new ArgumentNullException(nameof(session));

        var plugin = context.Plugin;
        var supports = (GmcpModule[])(modules ?? Array.Empty<GmcpModule>()).Clone();

        plugin.AttachSession(
            session.HandleAsync,
            async agreed =>
            {
                if (!agreed)
                    return;

                if (plugin.ClientIdentity is { } identity)
                {
                    await session.HelloAsync(identity.Name, identity.Version).ConfigureAwait(false);
                }

                if (supports.Length > 0)
                {
                    await session.SetSupportsAsync(supports).ConfigureAwait(false);
                }
            });

        return context;
    }

    /// <summary>
    /// Creates a server session on this connection, which handles <c>Core</c> messages from the
    /// client.
    /// </summary>
    /// <param name="context">The GMCP plugin configuration context</param>
    /// <param name="session">The new session, which sends through this connection</param>
    /// <returns>The configuration context for continued chaining</returns>
    public static PluginConfigurationContext<GMCPProtocol> UseGmcpServerSession(
        this PluginConfigurationContext<GMCPProtocol> context,
        out GmcpServerSession session)
    {
        session = new GmcpServerSession(context.Plugin.SendAsync);
        return context.UseGmcpServerSession(session);
    }

    /// <summary>
    /// Attaches an existing server session, which keeps its own way of sending.
    /// </summary>
    /// <param name="context">The GMCP plugin configuration context</param>
    /// <param name="session">The session</param>
    /// <returns>The configuration context for continued chaining</returns>
    public static PluginConfigurationContext<GMCPProtocol> UseGmcpServerSession(
        this PluginConfigurationContext<GMCPProtocol> context,
        GmcpServerSession session)
    {
        if (session == null)
            throw new ArgumentNullException(nameof(session));

        context.Plugin.AttachSession(session.HandleAsync, null);
        return context;
    }
}
