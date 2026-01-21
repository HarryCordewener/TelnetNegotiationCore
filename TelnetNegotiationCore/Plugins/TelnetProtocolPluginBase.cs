using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Stateless;
using TelnetNegotiationCore.Models;

namespace TelnetNegotiationCore.Plugins;

/// <summary>
/// Base class for telnet protocol plugins providing common functionality.
/// </summary>
public abstract class TelnetProtocolPluginBase : ITelnetProtocolPlugin, IAsyncDisposable
{
    private bool _isEnabled;
    private IProtocolContext? _context;

    /// <summary>
    /// Gets the protocol context. Null until InitializeAsync is called.
    /// </summary>
    protected IProtocolContext Context => _context ?? throw new InvalidOperationException("Plugin not initialized");

    /// <inheritdoc />
    public abstract Type ProtocolType { get; }

    /// <inheritdoc />
    public abstract string ProtocolName { get; }

    /// <inheritdoc />
    public virtual IReadOnlyCollection<Type> Dependencies => Array.Empty<Type>();

    /// <inheritdoc />
    public bool IsEnabled => _isEnabled;

    /// <inheritdoc />
    public virtual async ValueTask InitializeAsync(IProtocolContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _isEnabled = true;
        await OnInitializeAsync();
    }

    /// <inheritdoc />
    public abstract void ConfigureStateMachine(StateMachine<State, Trigger> stateMachine, IProtocolContext context);

    /// <inheritdoc />
    public virtual async ValueTask OnEnabledAsync()
    {
        _isEnabled = true;
        await OnProtocolEnabledAsync();
    }

    /// <inheritdoc />
    public virtual async ValueTask OnDisabledAsync()
    {
        _isEnabled = false;
        await OnProtocolDisabledAsync();
    }

    /// <inheritdoc />
    public virtual async ValueTask DisposeAsync()
    {
        await OnDisposeAsync();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Called when the plugin is initialized. Override to provide custom initialization logic.
    /// </summary>
    protected virtual ValueTask OnInitializeAsync() => default(ValueTask);

    /// <summary>
    /// Called when the protocol is enabled. Override to provide custom enable logic.
    /// </summary>
    protected virtual ValueTask OnProtocolEnabledAsync() => default(ValueTask);

    /// <summary>
    /// Called when the protocol is disabled. Override to provide custom disable logic.
    /// </summary>
    protected virtual ValueTask OnProtocolDisabledAsync() => default(ValueTask);

    /// <summary>
    /// Called when the plugin is disposed. Override to provide custom cleanup logic.
    /// </summary>
    protected virtual ValueTask OnDisposeAsync() => default(ValueTask);
}
