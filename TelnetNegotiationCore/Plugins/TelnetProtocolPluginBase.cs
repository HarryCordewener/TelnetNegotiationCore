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
    private volatile bool _isNegotiated;
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
    public bool IsNegotiated => _isNegotiated;

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

    /// <inheritdoc />
    /// <remarks>
    /// This is the one place <see cref="IsNegotiated"/> changes. A protocol calls it from its own
    /// <see cref="ConfigureStateMachine"/> handlers, at the state entered when a WILL/DO exchange for
    /// its option genuinely resolves -- not at <see cref="InitializeAsync"/>, which runs before any
    /// negotiation has happened at all.
    /// <para>
    /// <b>Transition-only, not level-triggered.</b> A protocol's own state machine can re-enter the
    /// same accepted (or refused) state more than once for reasons that are its own business -- a
    /// re-affirmed WILL, a retried handshake -- and calling this again with the value it already had
    /// must not fire <see cref="OnNegotiationChangedAsync"/> a second time for a change that did not
    /// happen. A consumer reacting to "negotiation just flipped" would otherwise see spurious repeats
    /// for a state that never moved.
    /// </para>
    /// </remarks>
    public virtual async ValueTask OnNegotiatedAsync(bool isNegotiated)
    {
        if (_isNegotiated == isNegotiated)
        {
            return;
        }

        _isNegotiated = isNegotiated;
        await OnNegotiationChangedAsync(isNegotiated);
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
    /// Called whenever <see cref="IsNegotiated"/> changes -- the real-negotiation counterpart of
    /// <see cref="OnProtocolEnabledAsync"/> / <see cref="OnProtocolDisabledAsync"/>. Override to react
    /// to the peer genuinely agreeing to, refusing, or withdrawing this option.
    /// </summary>
    /// <param name="isNegotiated">True if the peer just agreed, false if it just refused or withdrew.</param>
    protected virtual ValueTask OnNegotiationChangedAsync(bool isNegotiated) => default(ValueTask);

    /// <summary>
    /// Called when the plugin is disposed. Override to provide custom cleanup logic.
    /// </summary>
    protected virtual ValueTask OnDisposeAsync() => default(ValueTask);
}
