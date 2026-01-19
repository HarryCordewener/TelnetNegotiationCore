using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Stateless;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Plugins;

namespace TelnetNegotiationCore.Protocols;

/// <summary>
/// NAWS (Negotiate About Window Size) protocol plugin - RFC 1073
/// Implements http://www.faqs.org/rfcs/rfc1073.html
/// </summary>
public class NAWSProtocol : TelnetProtocolPluginBase
{
    private byte[] _nawsByteState = [];
    private int _nawsIndex = 0;

    private Func<int, int, ValueTask>? _onNAWSNegotiated;

    /// <summary>
    /// Sets the callback that is invoked when NAWS negotiation is complete.
    /// </summary>
    /// <param name="callback">The callback to handle window size changes</param>
    /// <returns>This instance for fluent chaining</returns>
    public NAWSProtocol OnNAWS(Func<int, int, ValueTask>? callback)
    {
        _onNAWSNegotiated = callback;
        return this;
    }



    /// <summary>
    /// Currently known Client Height (defaults to 24)
    /// </summary>
    public int ClientHeight { get; private set; } = 24;

    /// <summary>
    /// Currently known Client Width (defaults to 78)
    /// </summary>
    public int ClientWidth { get; private set; } = 78;

    /// <inheritdoc />
    public override Type ProtocolType => typeof(NAWSProtocol);

    /// <inheritdoc />
    public override string ProtocolName => "NAWS (Negotiate About Window Size)";

    /// <inheritdoc />
    public override IReadOnlyCollection<Type> Dependencies => Array.Empty<Type>();

    /// <inheritdoc />
    public override void ConfigureStateMachine(StateMachine<State, Trigger> stateMachine, IProtocolContext context)
    {
        context.Logger.LogInformation("Configuring NAWS state machine");
        
        // State machine configuration would go here
        // This integrates with the main telnet state machine
    }

    /// <inheritdoc />
    protected override ValueTask OnInitializeAsync()
    {
        Context.Logger.LogInformation("NAWS Protocol initialized");
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolEnabledAsync()
    {
        Context.Logger.LogInformation("NAWS Protocol enabled - requesting window size");
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolDisabledAsync()
    {
        Context.Logger.LogInformation("NAWS Protocol disabled");
        _nawsByteState = [];
        _nawsIndex = 0;
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Processes a NAWS byte from the client
    /// </summary>
    public void ProcessNAWSByte(byte value)
    {
        if (!IsEnabled)
            return;

        if (_nawsIndex >= _nawsByteState.Length)
        {
            Array.Resize(ref _nawsByteState, _nawsIndex + 1);
        }

        _nawsByteState[_nawsIndex++] = value;
    }

    /// <summary>
    /// Completes NAWS negotiation and updates width/height
    /// </summary>
    public async ValueTask CompleteNAWSNegotiationAsync()
    {
        if (!IsEnabled || _nawsByteState.Length < 4)
            return;

        try
        {
            ClientWidth = (_nawsByteState[0] << 8) + _nawsByteState[1];
            ClientHeight = (_nawsByteState[2] << 8) + _nawsByteState[3];

            Context.Logger.LogInformation("NAWS negotiation complete: Width={Width}, Height={Height}", 
                ClientWidth, ClientHeight);

            // Trigger callback if registered
            if (Context.TryGetSharedState<Func<int, int, ValueTask>>("NAWS_Callback", out var callback) && callback != null)
            {
                await callback(ClientWidth, ClientHeight);
            }
        }
        finally
        {
            _nawsByteState = [];
            _nawsIndex = 0;
        }
    }

    /// <inheritdoc />
    protected override ValueTask OnDisposeAsync()
    {
        _nawsByteState = [];
        _nawsIndex = 0;
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Called by the interpreter when NAWS negotiation is complete.
    /// Internal method that invokes the callback.
    /// </summary>
    internal async ValueTask OnNAWSNegotiatedAsync(int height, int width)
    {
        if (!IsEnabled)
            return;

        ClientWidth = width;
        ClientHeight = height;

        Context.Logger.LogInformation("NAWS negotiation complete: Width={Width}, Height={Height}", width, height);
        
        if (_onNAWSNegotiated != null)
            await _onNAWSNegotiated(height, width).ConfigureAwait(false);
    }
}
