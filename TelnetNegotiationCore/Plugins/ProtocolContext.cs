using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Stateless;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;

namespace TelnetNegotiationCore.Plugins;

/// <summary>
/// Implementation of IProtocolContext that bridges plugins to the telnet interpreter.
/// </summary>
internal class ProtocolContext : IProtocolContext
{
    private readonly TelnetInterpreter _interpreter;
    private readonly ProtocolPluginManager _pluginManager;
    private readonly Dictionary<string, object?> _sharedState = new();

    public ProtocolContext(
        TelnetInterpreter interpreter,
        ProtocolPluginManager pluginManager,
        ILogger logger)
    {
        _interpreter = interpreter ?? throw new ArgumentNullException(nameof(interpreter));
        _pluginManager = pluginManager ?? throw new ArgumentNullException(nameof(pluginManager));
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public ILogger Logger { get; }

    /// <inheritdoc />
    public Encoding CurrentEncoding => _interpreter.CurrentEncoding;

    /// <inheritdoc />
    public void SetEncoding(Encoding encoding)
    {
        if (encoding == null)
            throw new ArgumentNullException(nameof(encoding));
        
        _interpreter.CurrentEncoding = encoding;
    }

    /// <inheritdoc />
    public TelnetInterpreter.TelnetMode Mode => _interpreter.Mode;

    /// <inheritdoc />
    public StateMachine<State, Trigger> StateMachine => _interpreter.TelnetStateMachine;

    /// <inheritdoc />
    public async ValueTask SendNegotiationAsync(ReadOnlyMemory<byte> bytes)
    {
        if (_interpreter.CallbackNegotiationAsync != null)
        {
            await _interpreter.CallbackNegotiationAsync(bytes.ToArray());
        }
    }

    /// <inheritdoc />
    public ValueTask WriteToBufferAsync(ReadOnlyMemory<byte> data)
    {
        // This would integrate with the interpreter's internal buffer
        // For now, we'll log it as this requires deeper integration
        Logger.LogDebug("WriteToBufferAsync called with {ByteCount} bytes", data.Length);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public T? GetPlugin<T>() where T : class, ITelnetProtocolPlugin
    {
        return _pluginManager.GetPlugin<T>();
    }

    /// <inheritdoc />
    public ITelnetProtocolPlugin? GetPlugin(Type pluginType)
    {
        return _pluginManager.GetPlugin(pluginType);
    }

    /// <inheritdoc />
    public bool IsPluginEnabled<T>() where T : class, ITelnetProtocolPlugin
    {
        return _pluginManager.IsPluginEnabled<T>();
    }

    /// <inheritdoc />
    public bool IsPluginEnabled(Type pluginType)
    {
        return _pluginManager.IsPluginEnabled(pluginType);
    }

    /// <inheritdoc />
    public IReadOnlyCollection<ITelnetProtocolPlugin> GetAllPlugins()
    {
        return _pluginManager.GetAllPlugins();
    }

    /// <inheritdoc />
    public void SetSharedState(string key, object? value)
    {
        _sharedState[key] = value;
    }

    /// <inheritdoc />
    public object? GetSharedState(string key)
    {
        return _sharedState.TryGetValue(key, out var value) ? value : null;
    }

    /// <inheritdoc />
    public bool TryGetSharedState<T>(string key, out T? value)
    {
        if (_sharedState.TryGetValue(key, out var obj) && obj is T typedValue)
        {
            value = typedValue;
            return true;
        }

        value = default;
        return false;
    }

    /// <inheritdoc />
    public void RegisterInitialNegotiation(Func<ValueTask> negotiationFunc)
    {
        if (negotiationFunc == null)
            throw new ArgumentNullException(nameof(negotiationFunc));
        
        // Access the interpreter's internal registration method
        // This requires making the method accessible or using reflection
        _interpreter.RegisterInitialWilling(negotiationFunc);
    }

    /// <inheritdoc />
    public TelnetInterpreter Interpreter => _interpreter;
}
