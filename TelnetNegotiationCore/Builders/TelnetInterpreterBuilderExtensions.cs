using System;
using TelnetNegotiationCore.Protocols;

namespace TelnetNegotiationCore.Builders;

/// <summary>
/// Extension methods for TelnetInterpreterBuilder to simplify protocol setup.
/// </summary>
public static class TelnetInterpreterBuilderExtensions
{
    /// <summary>
    /// Adds all default MUD protocols to the interpreter.
    /// This includes: NAWS, GMCP, MSSP, Terminal Type, Charset, EOR, and Suppress Go-Ahead.
    /// </summary>
    /// <param name="builder">The builder instance</param>
    /// <returns>The builder for method chaining</returns>
    public static TelnetInterpreterBuilder AddDefaultMUDProtocols(this TelnetInterpreterBuilder builder)
    {
        if (builder == null)
            throw new ArgumentNullException(nameof(builder));

        return builder
            .AddPlugin<NAWSProtocol>()
            .AddPlugin<GMCPProtocol>()
            .AddPlugin<MSSPProtocol>()
            .AddPlugin<TerminalTypeProtocol>()
            .AddPlugin<CharsetProtocol>()
            .AddPlugin<EORProtocol>()
            .AddPlugin<SuppressGoAheadProtocol>();
    }
}
