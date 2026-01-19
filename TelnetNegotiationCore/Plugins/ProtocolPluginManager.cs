using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace TelnetNegotiationCore.Plugins;

/// <summary>
/// Manages telnet protocol plugins including registration, dependency resolution, and lifecycle.
/// </summary>
public class ProtocolPluginManager
{
    private readonly Dictionary<Type, ITelnetProtocolPlugin> _plugins = new();
    private readonly List<Type> _initializationOrder = new();
    private readonly ILogger _logger;
    private bool _isInitialized;

    public ProtocolPluginManager(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Registers a protocol plugin.
    /// </summary>
    /// <typeparam name="T">The plugin type</typeparam>
    /// <param name="plugin">The plugin instance</param>
    public void RegisterPlugin<T>(T plugin) where T : class, ITelnetProtocolPlugin
    {
        if (_isInitialized)
            throw new InvalidOperationException("Cannot register plugins after initialization");

        var type = plugin.ProtocolType;
        if (_plugins.ContainsKey(type))
        {
            _logger.LogWarning("Plugin {PluginType} already registered, replacing with new instance", type.Name);
            _plugins[type] = plugin;
        }
        else
        {
            _plugins[type] = plugin;
            _logger.LogInformation("Registered plugin: {PluginName} ({PluginType})", plugin.ProtocolName, type.Name);
        }
    }

    /// <summary>
    /// Gets a plugin by its type.
    /// </summary>
    /// <typeparam name="T">The plugin type</typeparam>
    /// <returns>The plugin instance or null if not registered</returns>
    public T? GetPlugin<T>() where T : class, ITelnetProtocolPlugin
    {
        return _plugins.TryGetValue(typeof(T), out var plugin) ? plugin as T : null;
    }

    /// <summary>
    /// Gets a plugin by its runtime type.
    /// </summary>
    /// <param name="pluginType">The plugin type</param>
    /// <returns>The plugin instance or null if not registered</returns>
    public ITelnetProtocolPlugin? GetPlugin(Type pluginType)
    {
        return _plugins.TryGetValue(pluginType, out var plugin) ? plugin : null;
    }

    /// <summary>
    /// Checks if a plugin is registered and enabled.
    /// </summary>
    /// <typeparam name="T">The plugin type</typeparam>
    /// <returns>True if registered and enabled</returns>
    public bool IsPluginEnabled<T>() where T : class, ITelnetProtocolPlugin
    {
        return _plugins.TryGetValue(typeof(T), out var plugin) && plugin.IsEnabled;
    }

    /// <summary>
    /// Checks if a plugin is registered and enabled.
    /// </summary>
    /// <param name="pluginType">The plugin type</param>
    /// <returns>True if registered and enabled</returns>
    public bool IsPluginEnabled(Type pluginType)
    {
        return _plugins.TryGetValue(pluginType, out var plugin) && plugin.IsEnabled;
    }

    /// <summary>
    /// Gets all registered plugins.
    /// </summary>
    public IReadOnlyCollection<ITelnetProtocolPlugin> GetAllPlugins()
    {
        return _plugins.Values.ToList().AsReadOnly();
    }

    /// <summary>
    /// Initializes all plugins in dependency order.
    /// </summary>
    /// <param name="context">The protocol context</param>
    public async ValueTask InitializePluginsAsync(IProtocolContext context)
    {
        if (_isInitialized)
            throw new InvalidOperationException("Plugins already initialized");

        _logger.LogInformation("Initializing {PluginCount} plugins with dependency resolution", _plugins.Count);

        // Perform topological sort to resolve dependencies
        _initializationOrder.Clear();
        var resolved = new HashSet<Type>();
        var visiting = new HashSet<Type>();

        foreach (var pluginType in _plugins.Keys)
        {
            ResolveDependencies(pluginType, resolved, visiting);
        }

        // Initialize plugins in dependency order
        foreach (var pluginType in _initializationOrder)
        {
            var plugin = _plugins[pluginType];
            _logger.LogDebug("Initializing plugin: {PluginName}", plugin.ProtocolName);
            await plugin.InitializeAsync(context);
        }

        _isInitialized = true;
        _logger.LogInformation("All plugins initialized successfully");
    }

    /// <summary>
    /// Configures all plugin state machines in dependency order.
    /// </summary>
    /// <param name="stateMachine">The state machine to configure</param>
    /// <param name="context">The protocol context</param>
    public void ConfigureStateMachines(Stateless.StateMachine<Models.State, Models.Trigger> stateMachine, IProtocolContext context)
    {
        _logger.LogInformation("Configuring state machines for {PluginCount} plugins", _plugins.Count);

        // If initialization order is already determined, use it for consistency
        // Otherwise, configure plugins in registration order
        var pluginsToConfig = _initializationOrder.Count > 0
            ? _initializationOrder.Select(t => _plugins[t])
            : _plugins.Values;

        foreach (var plugin in pluginsToConfig)
        {
            _logger.LogDebug("Configuring state machine for: {PluginName}", plugin.ProtocolName);
            plugin.ConfigureStateMachine(stateMachine, context);
        }
    }

    /// <summary>
    /// Enables a plugin at runtime.
    /// </summary>
    /// <typeparam name="T">The plugin type</typeparam>
    public async ValueTask EnablePluginAsync<T>() where T : class, ITelnetProtocolPlugin
    {
        if (!_plugins.TryGetValue(typeof(T), out var plugin))
            throw new InvalidOperationException($"Plugin {typeof(T).Name} not registered");

        if (plugin.IsEnabled)
        {
            _logger.LogDebug("Plugin {PluginName} already enabled", plugin.ProtocolName);
            return;
        }

        _logger.LogInformation("Enabling plugin: {PluginName}", plugin.ProtocolName);
        await plugin.OnEnabledAsync();
    }

    /// <summary>
    /// Disables a plugin at runtime.
    /// </summary>
    /// <typeparam name="T">The plugin type</typeparam>
    public async ValueTask DisablePluginAsync<T>() where T : class, ITelnetProtocolPlugin
    {
        if (!_plugins.TryGetValue(typeof(T), out var plugin))
            throw new InvalidOperationException($"Plugin {typeof(T).Name} not registered");

        if (!plugin.IsEnabled)
        {
            _logger.LogDebug("Plugin {PluginName} already disabled", plugin.ProtocolName);
            return;
        }

        // Check if any enabled plugins depend on this one
        var dependents = _plugins.Values
            .Where(p => p.IsEnabled && p.Dependencies.Contains(typeof(T)))
            .ToList();

        if (dependents.Any())
        {
            var dependentNames = string.Join(", ", dependents.Select(p => p.ProtocolName));
            throw new InvalidOperationException(
                $"Cannot disable plugin {plugin.ProtocolName} because it is required by: {dependentNames}");
        }

        _logger.LogInformation("Disabling plugin: {PluginName}", plugin.ProtocolName);
        await plugin.OnDisabledAsync();
    }

    /// <summary>
    /// Disposes all plugins.
    /// </summary>
    public async ValueTask DisposeAllAsync()
    {
        _logger.LogInformation("Disposing all plugins");

        // Dispose in reverse order
        for (int i = _initializationOrder.Count - 1; i >= 0; i--)
        {
            var pluginType = _initializationOrder[i];
            var plugin = _plugins[pluginType];
            await plugin.DisposeAsync();
        }

        _plugins.Clear();
        _initializationOrder.Clear();
        _isInitialized = false;
    }

    private void ResolveDependencies(Type pluginType, HashSet<Type> resolved, HashSet<Type> visiting)
    {
        if (resolved.Contains(pluginType))
            return;

        if (visiting.Contains(pluginType))
            throw new InvalidOperationException($"Circular dependency detected involving plugin {pluginType.Name}");

        visiting.Add(pluginType);

        var plugin = _plugins[pluginType];
        foreach (var dependencyType in plugin.Dependencies)
        {
            if (!_plugins.ContainsKey(dependencyType))
            {
                throw new InvalidOperationException(
                    $"Plugin {plugin.ProtocolName} depends on {dependencyType.Name}, but it is not registered");
            }

            ResolveDependencies(dependencyType, resolved, visiting);
        }

        visiting.Remove(pluginType);
        resolved.Add(pluginType);
        _initializationOrder.Add(pluginType);
    }
}
