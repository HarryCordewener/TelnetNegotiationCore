using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extension methods for <see cref="IServiceCollection"/> to register telnet interpreter services.
/// These methods integrate TelnetNegotiationCore with the standard .NET dependency injection
/// container, making it easy to use with <c>WebApplication.CreateBuilder()</c>,
/// <c>Host.CreateApplicationBuilder()</c>, or any <see cref="IServiceCollection"/>-based host.
/// </summary>
public static class TelnetServiceCollectionExtensions
{
    /// <summary>
    /// Registers an <see cref="ITelnetInterpreterFactory"/> configured for server mode.
    /// The factory automatically resolves an <see cref="ILogger"/> from the DI container.
    /// <para>
    /// Use the optional <paramref name="configure"/> action to add shared protocol plugins
    /// that apply to every connection. Per-connection callbacks (e.g. <c>OnSubmit</c>) should
    /// be set on the builder returned by <see cref="ITelnetInterpreterFactory.CreateBuilder"/>.
    /// </para>
    /// <example>
    /// <code>
    /// // In Program.cs or Startup
    /// builder.Services.AddTelnetServer(telnet =&gt;
    /// {
    ///     telnet.AddDefaultMUDProtocols();
    /// });
    ///
    /// // In a ConnectionHandler
    /// var (interpreter, readTask) = await _factory.CreateBuilder()
    ///     .OnSubmit(WriteBackAsync)
    ///     .BuildAndStartAsync(connection.Transport);
    /// </code>
    /// </example>
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="configure">Optional action to configure shared settings on each new builder</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddTelnetServer(
        this IServiceCollection services,
        Action<TelnetInterpreterBuilder>? configure = null)
    {
        if (services == null) throw new ArgumentNullException(nameof(services));

        services.TryAddSingleton<ITelnetInterpreterFactory>(sp =>
        {
            var loggerFactory = sp.GetService<ILoggerFactory>();
            return new TelnetInterpreterFactory(
                TelnetInterpreter.TelnetMode.Server,
                loggerFactory,
                configure);
        });

        return services;
    }

    /// <summary>
    /// Registers an <see cref="ITelnetInterpreterFactory"/> configured for client mode.
    /// The factory automatically resolves an <see cref="ILogger"/> from the DI container.
    /// <para>
    /// Use the optional <paramref name="configure"/> action to add shared protocol plugins
    /// that apply to every connection. Per-connection callbacks (e.g. <c>OnSubmit</c>) should
    /// be set on the builder returned by <see cref="ITelnetInterpreterFactory.CreateBuilder"/>.
    /// </para>
    /// <example>
    /// <code>
    /// // In Program.cs
    /// builder.Services.AddTelnetClient(telnet =&gt;
    /// {
    ///     telnet.AddDefaultMUDProtocols();
    /// });
    ///
    /// // In a service
    /// var (interpreter, readTask) = await _factory.CreateBuilder()
    ///     .OnSubmit(WriteBackAsync)
    ///     .BuildAndStartAsync(tcpClient);
    /// </code>
    /// </example>
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="configure">Optional action to configure shared settings on each new builder</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddTelnetClient(
        this IServiceCollection services,
        Action<TelnetInterpreterBuilder>? configure = null)
    {
        if (services == null) throw new ArgumentNullException(nameof(services));

        services.TryAddSingleton<ITelnetInterpreterFactory>(sp =>
        {
            var loggerFactory = sp.GetService<ILoggerFactory>();
            return new TelnetInterpreterFactory(
                TelnetInterpreter.TelnetMode.Client,
                loggerFactory,
                configure);
        });

        return services;
    }
}
