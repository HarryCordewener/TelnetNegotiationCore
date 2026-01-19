using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Protocols;

namespace TelnetNegotiationCore.Builders;

/// <summary>
/// Extension methods for TelnetInterpreterBuilder to simplify protocol setup.
/// </summary>
public static class TelnetInterpreterBuilderExtensions
{
    /// <summary>
    /// Adds all default MUD protocols to the interpreter with optional configuration.
    /// This includes: NAWS, GMCP, MSSP, Terminal Type, Charset, EOR, and Suppress Go-Ahead.
    /// </summary>
    /// <param name="builder">The builder instance</param>
    /// <param name="onNAWS">Optional callback for NAWS (window size) events</param>
    /// <param name="onGMCPMessage">Optional callback for GMCP message events</param>
    /// <param name="onMSSP">Optional callback for MSSP request events</param>
    /// <param name="msspConfig">Optional MSSP configuration provider</param>
    /// <param name="onMSDPMessage">Optional callback for MSDP message events</param>
    /// <param name="onPrompt">Optional callback for prompt events (EOR/SuppressGoAhead)</param>
    /// <param name="charsetOrder">Optional ordered list of preferred character encodings</param>
    /// <returns>The builder for method chaining</returns>
    public static TelnetInterpreterBuilder AddDefaultMUDProtocols(
        this TelnetInterpreterBuilder builder,
        Func<int, int, ValueTask>? onNAWS = null,
        Func<(string Package, string Info), ValueTask>? onGMCPMessage = null,
        Func<MSSPConfig, ValueTask>? onMSSP = null,
        Func<MSSPConfig>? msspConfig = null,
        Func<Interpreters.TelnetInterpreter, string, ValueTask>? onMSDPMessage = null,
        Func<ValueTask>? onPrompt = null,
        Encoding[]? charsetOrder = null)
    {
        if (builder == null)
            throw new ArgumentNullException(nameof(builder));

        // Add NAWS protocol
        var nawsContext = builder.AddPlugin<NAWSProtocol>();
        if (onNAWS != null)
            nawsContext = nawsContext.OnNAWS(onNAWS);

        // Add GMCP protocol
        var gmcpContext = nawsContext.AddPlugin<GMCPProtocol>();
        if (onGMCPMessage != null)
            gmcpContext = gmcpContext.OnGMCPMessage(onGMCPMessage);

        // Add MSDP protocol
        var msdpContext = gmcpContext.AddPlugin<MSDPProtocol>();
        if (onMSDPMessage != null)
            msdpContext = msdpContext.OnMSDPMessage(onMSDPMessage);

        // Add MSSP protocol
        var msspContext = msdpContext.AddPlugin<MSSPProtocol>();
        if (onMSSP != null)
            msspContext = msspContext.OnMSSP(onMSSP);
        if (msspConfig != null)
            msspContext = msspContext.WithMSSPConfig(msspConfig);

        // Add Terminal Type protocol
        var ttypeContext = msspContext.AddPlugin<TerminalTypeProtocol>();

        // Add Charset protocol
        var charsetContext = ttypeContext.AddPlugin<CharsetProtocol>();
        if (charsetOrder != null && charsetOrder.Length > 0)
            charsetContext = charsetContext.WithCharsetOrder(charsetOrder);

        // Add EOR protocol
        var eorContext = charsetContext.AddPlugin<EORProtocol>();
        if (onPrompt != null)
            eorContext = eorContext.OnPrompt(onPrompt);

        // Add Suppress Go-Ahead protocol (uses same prompt callback)
        var sgaContext = eorContext.AddPlugin<SuppressGoAheadProtocol>();
        if (onPrompt != null)
            sgaContext = sgaContext.OnPrompt(onPrompt);

        return sgaContext;
    }

    /// <summary>
    /// Adds all default MUD protocols to the interpreter without any configuration.
    /// Use the overload with parameters to configure protocols inline, or configure them
    /// after building by getting the plugin from PluginManager.
    /// </summary>
    /// <param name="builder">The builder instance</param>
    /// <returns>The builder for method chaining</returns>
    public static TelnetInterpreterBuilder AddDefaultMUDProtocols(this TelnetInterpreterBuilder builder)
    {
        return AddDefaultMUDProtocols(builder, null, null, null, null, null, null, null);
    }
}
