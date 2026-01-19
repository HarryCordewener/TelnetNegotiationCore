using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Stateless;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Plugins;

namespace TelnetNegotiationCore.Protocols;

/// <summary>
/// Terminal Type protocol plugin - RFC 1091 and MTTS
/// https://datatracker.ietf.org/doc/html/rfc1091
/// https://tintin.mudhalla.net/protocols/mtts/
/// </summary>
public class TerminalTypeProtocol : TelnetProtocolPluginBase
{
    private ImmutableList<string> _terminalTypes = [];
    private int _currentTerminalType = -1;
    private byte[] _ttypeByteState = [];
    private int _ttypeIndex = 0;

    /// <summary>
    /// A list of terminal types for this connection
    /// </summary>
    public ImmutableList<string> TerminalTypes => _terminalTypes;

    /// <summary>
    /// The current selected Terminal Type
    /// </summary>
    public string CurrentTerminalType => _currentTerminalType == -1
        ? "unknown"
        : _terminalTypes[Math.Min(_currentTerminalType, _terminalTypes.Count - 1)];

    /// <inheritdoc />
    public override Type ProtocolType => typeof(TerminalTypeProtocol);

    /// <inheritdoc />
    public override string ProtocolName => "Terminal Type (RFC 1091 + MTTS)";

    /// <inheritdoc />
    public override IReadOnlyCollection<Type> Dependencies => Array.Empty<Type>();

    /// <inheritdoc />
    public override void ConfigureStateMachine(StateMachine<State, Trigger> stateMachine, IProtocolContext context)
    {
        context.Logger.LogInformation("Configuring Terminal Type state machine");
    }

    /// <inheritdoc />
    protected override ValueTask OnInitializeAsync()
    {
        Context.Logger.LogInformation("Terminal Type Protocol initialized");
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolEnabledAsync()
    {
        Context.Logger.LogInformation("Terminal Type Protocol enabled");
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolDisabledAsync()
    {
        Context.Logger.LogInformation("Terminal Type Protocol disabled");
        _terminalTypes = [];
        _currentTerminalType = -1;
        _ttypeByteState = [];
        _ttypeIndex = 0;
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Processes a terminal type byte
    /// </summary>
    public void ProcessTerminalTypeByte(byte value)
    {
        if (!IsEnabled)
            return;

        if (_ttypeIndex >= _ttypeByteState.Length)
        {
            Array.Resize(ref _ttypeByteState, _ttypeIndex + 1);
        }

        _ttypeByteState[_ttypeIndex++] = value;
    }

    /// <summary>
    /// Completes terminal type negotiation
    /// </summary>
    public ValueTask CompleteTerminalTypeNegotiationAsync()
    {
        if (!IsEnabled || _ttypeByteState.Length == 0)
            return ValueTask.CompletedTask;

        try
        {
            var terminalType = System.Text.Encoding.ASCII.GetString(_ttypeByteState, 0, _ttypeIndex);

            // Add if not already in list
            if (!_terminalTypes.Contains(terminalType, StringComparer.OrdinalIgnoreCase))
            {
                _terminalTypes = _terminalTypes.Add(terminalType);
                _currentTerminalType = _terminalTypes.Count - 1;

                Context.Logger.LogInformation("Terminal Type received: {TerminalType}", terminalType);
            }
            else
            {
                // Cycled back to first terminal type - negotiation complete
                Context.Logger.LogInformation("Terminal Type negotiation complete. List: {Types}", 
                    string.Join(", ", _terminalTypes.Select(t => $"\"{t}\"")));
            }
        }
        finally
        {
            _ttypeByteState = [];
            _ttypeIndex = 0;
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Requests the next terminal type from the client
    /// </summary>
    public ValueTask RequestNextTerminalTypeAsync()
    {
        if (!IsEnabled)
            return ValueTask.CompletedTask;

        Context.Logger.LogDebug("Requesting next terminal type from client");
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    protected override ValueTask OnDisposeAsync()
    {
        _terminalTypes = [];
        _ttypeByteState = [];
        _ttypeIndex = 0;
        return ValueTask.CompletedTask;
    }
}
