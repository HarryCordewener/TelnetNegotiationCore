using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Core;
using System;
using System.Text;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Protocols;

namespace TelnetNegotiationCore.UnitTests;

public class TelnetServiceCollectionTests : BaseTest
{
    private ValueTask WriteBackToOutput(byte[] arg1, Encoding arg2, TelnetInterpreter t) => ValueTask.CompletedTask;

    private ValueTask WriteBackToNegotiate(ReadOnlyMemory<byte> arg1) => ValueTask.CompletedTask;

    [Test]
    public async Task AddTelnetServer_RegistersFactory()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddTelnetServer();
        var provider = services.BuildServiceProvider();
        var factory = provider.GetService<ITelnetInterpreterFactory>();

        // Assert
        await Assert.That(factory).IsNotNull();
    }

    [Test]
    public async Task AddTelnetClient_RegistersFactory()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddTelnetClient();
        var provider = services.BuildServiceProvider();
        var factory = provider.GetService<ITelnetInterpreterFactory>();

        // Assert
        await Assert.That(factory).IsNotNull();
    }

    [Test]
    public async Task AddTelnetServer_CreateBuilder_SetsServerMode()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTelnetServer();
        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<ITelnetInterpreterFactory>();

        // Act - Build an interpreter using the factory
        var interpreter = await factory.CreateBuilder()
            .OnSubmit(WriteBackToOutput)
            .OnNegotiation(WriteBackToNegotiate)
            .BuildAsync();

        // Assert
        await Assert.That(interpreter).IsNotNull();
        await Assert.That(interpreter.Mode).IsEqualTo(TelnetInterpreter.TelnetMode.Server);

        await interpreter.DisposeAsync();
    }

    [Test]
    public async Task AddTelnetClient_CreateBuilder_SetsClientMode()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTelnetClient();
        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<ITelnetInterpreterFactory>();

        // Act
        var interpreter = await factory.CreateBuilder()
            .OnSubmit(WriteBackToOutput)
            .OnNegotiation(WriteBackToNegotiate)
            .BuildAsync();

        // Assert
        await Assert.That(interpreter).IsNotNull();
        await Assert.That(interpreter.Mode).IsEqualTo(TelnetInterpreter.TelnetMode.Client);

        await interpreter.DisposeAsync();
    }

    [Test]
    public async Task AddTelnetServer_WithConfigure_AppliesPlugins()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTelnetServer(builder =>
        {
            builder.AddPlugin<NAWSProtocol>();
            builder.AddPlugin<GMCPProtocol>();
        });
        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<ITelnetInterpreterFactory>();

        // Act
        var interpreter = await factory.CreateBuilder()
            .OnSubmit(WriteBackToOutput)
            .OnNegotiation(WriteBackToNegotiate)
            .BuildAsync();

        // Assert
        await Assert.That(interpreter.PluginManager).IsNotNull();
        var nawsPlugin = interpreter.PluginManager!.GetPlugin<NAWSProtocol>();
        var gmcpPlugin = interpreter.PluginManager!.GetPlugin<GMCPProtocol>();
        await Assert.That(nawsPlugin).IsNotNull();
        await Assert.That(gmcpPlugin).IsNotNull();

        await interpreter.DisposeAsync();
    }

    [Test]
    public async Task AddTelnetServer_CreateBuilder_ReturnsFreshBuilderEachTime()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTelnetServer();
        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<ITelnetInterpreterFactory>();

        // Act - Create two interpreters from the same factory
        var interpreter1 = await factory.CreateBuilder()
            .OnSubmit(WriteBackToOutput)
            .OnNegotiation(WriteBackToNegotiate)
            .BuildAsync();

        var interpreter2 = await factory.CreateBuilder()
            .OnSubmit(WriteBackToOutput)
            .OnNegotiation(WriteBackToNegotiate)
            .BuildAsync();

        // Assert - They should be different instances
        await Assert.That(interpreter1).IsNotNull();
        await Assert.That(interpreter2).IsNotNull();
        await Assert.That(interpreter1).IsNotEqualTo(interpreter2);

        await interpreter1.DisposeAsync();
        await interpreter2.DisposeAsync();
    }

    [Test]
    public async Task AddTelnetServer_WithDefaultMUDProtocols_AppliesAllProtocols()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTelnetServer(builder =>
        {
            builder.AddDefaultMUDProtocols();
        });
        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<ITelnetInterpreterFactory>();

        // Act
        var interpreter = await factory.CreateBuilder()
            .OnSubmit(WriteBackToOutput)
            .OnNegotiation(WriteBackToNegotiate)
            .BuildAsync();

        // Assert - All default MUD protocols should be registered
        await Assert.That(interpreter.PluginManager).IsNotNull();
        await Assert.That(interpreter.PluginManager!.GetPlugin<NAWSProtocol>()).IsNotNull();
        await Assert.That(interpreter.PluginManager!.GetPlugin<GMCPProtocol>()).IsNotNull();
        await Assert.That(interpreter.PluginManager!.GetPlugin<MSSPProtocol>()).IsNotNull();
        await Assert.That(interpreter.PluginManager!.GetPlugin<TerminalTypeProtocol>()).IsNotNull();
        await Assert.That(interpreter.PluginManager!.GetPlugin<CharsetProtocol>()).IsNotNull();
        await Assert.That(interpreter.PluginManager!.GetPlugin<EORProtocol>()).IsNotNull();
        await Assert.That(interpreter.PluginManager!.GetPlugin<SuppressGoAheadProtocol>()).IsNotNull();

        await interpreter.DisposeAsync();
    }

    [Test]
    public async Task AddTelnetServer_FactoryResolvesLoggerFromDI()
    {
        // Arrange - Register with a real logger factory
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Debug));
        services.AddTelnetServer();
        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<ITelnetInterpreterFactory>();

        // Act - Should not throw because logger is resolved from DI
        var interpreter = await factory.CreateBuilder()
            .OnSubmit(WriteBackToOutput)
            .OnNegotiation(WriteBackToNegotiate)
            .BuildAsync();

        // Assert
        await Assert.That(interpreter).IsNotNull();

        await interpreter.DisposeAsync();
    }
}
