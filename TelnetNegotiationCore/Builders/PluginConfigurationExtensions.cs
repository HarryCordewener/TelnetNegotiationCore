using System;
using System.Threading.Tasks;
using TelnetNegotiationCore.Protocols;

namespace TelnetNegotiationCore.Builders;

/// <summary>
/// Extension methods for fluent plugin configuration.
/// </summary>
public static class PluginConfigurationExtensions
{
    /// <summary>
    /// Sets the NAWS (window size) callback in a fluent manner.
    /// </summary>
    /// <param name="context">The plugin configuration context</param>
    /// <param name="callback">The callback to handle window size changes</param>
    /// <returns>The configuration context for continued chaining</returns>
    public static PluginConfigurationContext<NAWSProtocol> OnNAWS(
        this PluginConfigurationContext<NAWSProtocol> context,
        Func<int, int, ValueTask>? callback)
    {
        context.Plugin.OnNAWS(callback);
        return context;
    }

    /// <summary>
    /// Sets the GMCP message callback in a fluent manner.
    /// </summary>
    /// <param name="context">The plugin configuration context</param>
    /// <param name="callback">The callback to handle GMCP messages</param>
    /// <returns>The configuration context for continued chaining</returns>
    public static PluginConfigurationContext<GMCPProtocol> OnGMCPMessage(
        this PluginConfigurationContext<GMCPProtocol> context,
        Func<(string Package, string Info), ValueTask>? callback)
    {
        context.Plugin.OnGMCPMessage(callback);
        return context;
    }

    /// <summary>
    /// Sets the MSDP message callback in a fluent manner.
    /// </summary>
    /// <param name="context">The plugin configuration context</param>
    /// <param name="callback">The callback to handle MSDP messages</param>
    /// <returns>The configuration context for continued chaining</returns>
    public static PluginConfigurationContext<MSDPProtocol> OnMSDPMessage(
        this PluginConfigurationContext<MSDPProtocol> context,
        Func<Interpreters.TelnetInterpreter, string, ValueTask>? callback)
    {
        context.Plugin.OnMSDPMessage(callback);
        return context;
    }

    /// <summary>
    /// Sets the MSSP request callback in a fluent manner.
    /// </summary>
    /// <param name="context">The plugin configuration context</param>
    /// <param name="callback">The callback to handle MSSP requests</param>
    /// <returns>The configuration context for continued chaining</returns>
    public static PluginConfigurationContext<MSSPProtocol> OnMSSP(
        this PluginConfigurationContext<MSSPProtocol> context,
        Func<Models.MSSPConfig, ValueTask>? callback)
    {
        context.Plugin.OnMSSP(callback);
        return context;
    }

    /// <summary>
    /// Sets the EOR prompt callback in a fluent manner.
    /// </summary>
    /// <param name="context">The plugin configuration context</param>
    /// <param name="callback">The callback to handle prompts</param>
    /// <returns>The configuration context for continued chaining</returns>
    public static PluginConfigurationContext<EORProtocol> OnPrompt(
        this PluginConfigurationContext<EORProtocol> context,
        Func<ValueTask>? callback)
    {
        context.Plugin.OnPrompt(callback);
        return context;
    }

    /// <summary>
    /// Sets the Suppress Go-Ahead prompt callback in a fluent manner.
    /// </summary>
    /// <param name="context">The plugin configuration context</param>
    /// <param name="callback">The callback to handle prompts</param>
    /// <returns>The configuration context for continued chaining</returns>
    public static PluginConfigurationContext<SuppressGoAheadProtocol> OnPrompt(
        this PluginConfigurationContext<SuppressGoAheadProtocol> context,
        Func<ValueTask>? callback)
    {
        context.Plugin.OnPrompt(callback);
        return context;
    }

    /// <summary>
    /// Sets the character set order for Charset negotiation in a fluent manner.
    /// </summary>
    /// <param name="context">The plugin configuration context</param>
    /// <param name="charsetOrder">The ordered list of preferred encodings</param>
    /// <returns>The configuration context for continued chaining</returns>
    public static PluginConfigurationContext<CharsetProtocol> WithCharsetOrder(
        this PluginConfigurationContext<CharsetProtocol> context,
        params System.Text.Encoding[] charsetOrder)
    {
        context.Plugin.CharsetOrder = charsetOrder;
        return context;
    }

    /// <summary>
    /// Sets the MSSP configuration in a fluent manner.
    /// </summary>
    /// <param name="context">The plugin configuration context</param>
    /// <param name="configProvider">The function that provides the MSSP configuration</param>
    /// <returns>The configuration context for continued chaining</returns>
    public static PluginConfigurationContext<MSSPProtocol> WithMSSPConfig(
        this PluginConfigurationContext<MSSPProtocol> context,
        Func<Models.MSSPConfig> configProvider)
    {
        context.Plugin.SetMSSPConfig(configProvider);
        return context;
    }
}
