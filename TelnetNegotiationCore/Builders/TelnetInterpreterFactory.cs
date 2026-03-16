using System;
using Microsoft.Extensions.Logging;
using TelnetNegotiationCore.Interpreters;

namespace TelnetNegotiationCore.Builders;

/// <summary>
/// Default implementation of <see cref="ITelnetInterpreterFactory"/>.
/// Creates <see cref="TelnetInterpreterBuilder"/> instances pre-configured with
/// a telnet mode, logger from DI, and optional shared plugin configuration.
/// </summary>
internal sealed class TelnetInterpreterFactory : ITelnetInterpreterFactory
{
    private readonly TelnetInterpreter.TelnetMode _mode;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly Action<TelnetInterpreterBuilder>? _configure;

    /// <summary>
    /// Initializes a new instance of the <see cref="TelnetInterpreterFactory"/> class.
    /// </summary>
    /// <param name="mode">The telnet mode (Server or Client)</param>
    /// <param name="loggerFactory">The logger factory from DI (may be null)</param>
    /// <param name="configure">Optional action to apply shared configuration (plugins, etc.)</param>
    public TelnetInterpreterFactory(
        TelnetInterpreter.TelnetMode mode,
        ILoggerFactory? loggerFactory,
        Action<TelnetInterpreterBuilder>? configure)
    {
        _mode = mode;
        _loggerFactory = loggerFactory;
        _configure = configure;
    }

    /// <inheritdoc />
    public TelnetInterpreterBuilder CreateBuilder()
    {
        var builder = new TelnetInterpreterBuilder()
            .UseMode(_mode);

        if (_loggerFactory != null)
        {
            builder.UseLogger(_loggerFactory.CreateLogger<TelnetInterpreter>());
        }

        _configure?.Invoke(builder);

        return builder;
    }
}
