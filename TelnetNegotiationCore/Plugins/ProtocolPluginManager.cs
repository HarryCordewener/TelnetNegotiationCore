using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace TelnetNegotiationCore.Plugins;

/// <summary>
/// Manages telnet protocol plugins including registration, dependency resolution, and lifecycle.
/// </summary>
public class ProtocolPluginManager : IAsyncDisposable
{
    private readonly Dictionary<Type, ITelnetProtocolPlugin> _plugins = new();
    private readonly List<Type> _initializationOrder = new();
    private readonly ILogger _logger;
    private readonly object _disposeGate = new();
    private Task? _disposeTask;
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
        lock (_disposeGate)
        {
            if (_disposeTask is not null)
                throw new ObjectDisposedException(nameof(ProtocolPluginManager));

            if (_isInitialized)
                throw new InvalidOperationException("Cannot register plugins after initialization");

            var type = plugin.ProtocolType;
            if (_plugins.ContainsKey(type))
                throw new InvalidOperationException($"Plugin {type.Name} is already registered");

            _plugins[type] = plugin;
            _logger.LogInformation("Registered plugin: {PluginName} ({PluginType})", plugin.ProtocolName, type.Name);

            // Registration is still allowed at this point (only initialization closes it off), so a
            // dependency order computed by an earlier ConfigureStateMachines/InitializePluginsAsync call
            // would otherwise go stale and silently omit this plugin from both.
            _initializationOrder.Clear();
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

        EnsureInitializationOrder();

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
    /// Runs each plugin's <see cref="TelnetProtocolPluginBase.ConfigureStateMachine"/> hook, in
    /// dependency order, before any plugin is initialized.
    /// </summary>
    /// <param name="context">The protocol context</param>
    public void ConfigureStateMachines(IProtocolContext context)
    {
        _logger.LogInformation("Configuring state machines for {PluginCount} plugins", _plugins.Count);

        // Computed here rather than left to whichever of this method or InitializePluginsAsync runs
        // first: both need the same dependency order, and the builder calls this one first, so relying
        // on InitializePluginsAsync to have computed it already would silently fall back to
        // registration order every time, contradicting this method's own contract.
        EnsureInitializationOrder();

        foreach (var pluginType in _initializationOrder)
        {
            var plugin = _plugins[pluginType];
            _logger.LogDebug("Configuring state machine for: {PluginName}", plugin.ProtocolName);

            // Every real plugin extends TelnetProtocolPluginBase, which is where this hook lives.
            (plugin as TelnetProtocolPluginBase)?.ConfigureStateMachine(context);
        }
    }

    /// <summary>
    /// Topologically sorts the registered plugins by <see cref="ITelnetProtocolPlugin.Dependencies"/>,
    /// populating <see cref="_initializationOrder"/>. A call while the order is already populated is a
    /// no-op, so either <see cref="ConfigureStateMachines"/> or <see cref="InitializePluginsAsync"/> can
    /// run first and the other reuses the same order rather than recomputing it -- unless
    /// <see cref="RegisterPlugin{T}"/> ran since, which clears the cache so the next call here rebuilds it.
    /// </summary>
    private void EnsureInitializationOrder()
    {
        if (_initializationOrder.Count > 0)
        {
            return;
        }

        var resolved = new HashSet<Type>();
        var visiting = new HashSet<Type>();

        foreach (var pluginType in _plugins.Keys)
        {
            ResolveDependencies(pluginType, resolved, visiting);
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
    /// <remarks>
    /// Every plugin is given an opportunity to dispose, even when an earlier plugin fails. A single
    /// failure is rethrown unchanged; multiple failures are reported together in an
    /// <see cref="AggregateException"/> after all plugins have been visited.
    /// </remarks>
    public ValueTask DisposeAllAsync()
    {
        lock (_disposeGate)
        {
            if (_disposeTask is not null)
            {
                return new ValueTask(_disposeTask);
            }

            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposeTask = completion.Task;
            _ = DisposeAndSignalAsync(completion);
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeAndSignalAsync(TaskCompletionSource<bool> completion)
    {
        try
        {
            await DisposeAllCoreAsync();
            completion.SetResult(true);
        }
        catch (Exception ex)
        {
            completion.SetException(ex);
        }
    }

    private async Task DisposeAllCoreAsync()
    {
        _logger.LogInformation("Disposing all plugins");

        List<Exception>? failures = null;
        var disposalOrder = _initializationOrder.ToList();
        var ordered = new HashSet<Type>(disposalOrder);
        disposalOrder.AddRange(_plugins.Keys.Where(ordered.Add));

        // Dispose in reverse order
        for (int i = disposalOrder.Count - 1; i >= 0; i--)
        {
            var pluginType = disposalOrder[i];
            var plugin = _plugins[pluginType];
            try
            {
                await plugin.DisposeAsync();
            }
            catch (Exception ex)
            {
                (failures ??= []).Add(ex);
            }
        }

        _plugins.Clear();
        _initializationOrder.Clear();
        _isInitialized = false;

        if (failures is [var failure])
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        if (failures is { Count: > 1 })
        {
            throw new AggregateException("Multiple plugins failed during disposal.", failures);
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => DisposeAllAsync();

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
