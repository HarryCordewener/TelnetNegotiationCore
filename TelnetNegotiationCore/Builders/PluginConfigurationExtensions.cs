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
        if (charsetOrder != null && charsetOrder.Length > 0)
        {
            context.Plugin.CharsetOrder = charsetOrder;
        }
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

    /// <summary>
    /// Sets the ENVIRON callback in a fluent manner (RFC 1408).
    /// </summary>
    /// <param name="context">The plugin configuration context</param>
    /// <param name="callback">The callback to handle environment variables</param>
    /// <returns>The configuration context for continued chaining</returns>
    public static PluginConfigurationContext<EnvironProtocol> OnEnvironmentVariables(
        this PluginConfigurationContext<EnvironProtocol> context,
        Func<System.Collections.Generic.Dictionary<string, string>, ValueTask>? callback)
    {
        context.Plugin.OnEnvironmentVariables(callback);
        return context;
    }

    /// <summary>
    /// Sets the environment variables to send when requested by server (RFC 1408, client mode).
    /// </summary>
    /// <param name="context">The plugin configuration context</param>
    /// <param name="environmentVariables">The environment variables to send</param>
    /// <returns>The configuration context for continued chaining</returns>
    public static PluginConfigurationContext<EnvironProtocol> WithClientEnvironmentVariables(
        this PluginConfigurationContext<EnvironProtocol> context,
        System.Collections.Generic.Dictionary<string, string> environmentVariables)
    {
        context.Plugin.WithClientEnvironmentVariables(environmentVariables);
        return context;
    }

    /// <summary>
    /// Sets the NEW-ENVIRON callback in a fluent manner.
    /// </summary>
    /// <param name="context">The plugin configuration context</param>
    /// <param name="callback">The callback to handle environment variables (regular, user)</param>
    /// <returns>The configuration context for continued chaining</returns>
    public static PluginConfigurationContext<NewEnvironProtocol> OnEnvironmentVariables(
        this PluginConfigurationContext<NewEnvironProtocol> context,
        Func<System.Collections.Generic.Dictionary<string, string>, System.Collections.Generic.Dictionary<string, string>, ValueTask>? callback)
    {
        context.Plugin.OnEnvironmentVariables(callback);
        return context;
    }

    /// <summary>
    /// Sets the MCCP compression state callback in a fluent manner.
    /// </summary>
    /// <param name="context">The plugin configuration context</param>
    /// <param name="callback">The callback to handle compression state changes (version: 2 or 3, enabled: true/false)</param>
    /// <returns>The configuration context for continued chaining</returns>
    public static PluginConfigurationContext<MCCPProtocol> OnCompressionEnabled(
        this PluginConfigurationContext<MCCPProtocol> context,
        Func<int, bool, ValueTask>? callback)
    {
        context.Plugin.OnCompressionEnabled(callback);
        return context;
    }

    /// <summary>
    /// Sets the Echo state change callback in a fluent manner.
    /// </summary>
    /// <param name="context">The plugin configuration context</param>
    /// <param name="callback">The callback to handle echo state changes (receives true if echoing is enabled, false otherwise)</param>
    /// <returns>The configuration context for continued chaining</returns>
    public static PluginConfigurationContext<EchoProtocol> OnEchoStateChanged(
        this PluginConfigurationContext<EchoProtocol> context,
        Func<bool, ValueTask>? callback)
    {
        context.Plugin.OnEchoStateChanged(callback);
        return context;
    }

    /// <summary>
    /// Enables the default echo handler which automatically echoes received bytes back to the client when echo is enabled.
    /// </summary>
    /// <param name="context">The plugin configuration context</param>
    /// <returns>The configuration context for continued chaining</returns>
    public static PluginConfigurationContext<EchoProtocol> UseDefaultEchoHandler(
        this PluginConfigurationContext<EchoProtocol> context)
    {
        context.Plugin.UseDefaultEchoHandler();
        return context;
    }

    /// <summary>
    /// Sets a custom echo handler for processing bytes when echo is enabled.
    /// </summary>
    /// <param name="context">The plugin configuration context</param>
    /// <param name="handler">Custom handler that receives byte and encoding</param>
    /// <returns>The configuration context for continued chaining</returns>
    public static PluginConfigurationContext<EchoProtocol> WithEchoHandler(
        this PluginConfigurationContext<EchoProtocol> context,
        Func<byte, System.Text.Encoding, ValueTask>? handler)
    {
        context.Plugin.WithEchoHandler(handler);
        return context;
    }
}
