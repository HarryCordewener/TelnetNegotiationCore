using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Protocols;

namespace TelnetNegotiationCore.UnitTests;

[TestFixture]
public class PluginBuilderTests : BaseTest
{
    private byte[] _negotiationOutput = Array.Empty<byte>();
    private (string Package, string Info)? _receivedGMCP = default;
    private MSSPConfig _receivedMSSP = default;

    private ValueTask WriteBackToOutput(byte[] arg1, Encoding arg2, TelnetInterpreter t) => ValueTask.CompletedTask;

    private ValueTask WriteBackToNegotiate(byte[] arg1)
    {
        _negotiationOutput = arg1;
        return ValueTask.CompletedTask;
    }

    [Test]
    public async Task CanBuildInterpreterWithDefaultMUDProtocols()
    {
        // Arrange & Act
        var interpreter = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(WriteBackToOutput)
            .OnNegotiation(WriteBackToNegotiate)
            .AddDefaultMUDProtocols()
            .BuildAsync();

        // Assert
        Assert.IsNotNull(interpreter, "Interpreter should be created");
        Assert.IsNotNull(interpreter.PluginManager, "Plugin manager should be set");
        
        // Verify all default protocols are registered
        var gmcpPlugin = interpreter.PluginManager.GetPlugin<GMCPProtocol>();
        var nawsPlugin = interpreter.PluginManager.GetPlugin<NAWSProtocol>();
        var msspPlugin = interpreter.PluginManager.GetPlugin<MSSPProtocol>();
        var ttypePlugin = interpreter.PluginManager.GetPlugin<TerminalTypeProtocol>();
        var charsetPlugin = interpreter.PluginManager.GetPlugin<CharsetProtocol>();
        var eorPlugin = interpreter.PluginManager.GetPlugin<EORProtocol>();
        var sgPlugin = interpreter.PluginManager.GetPlugin<SuppressGoAheadProtocol>();

        Assert.IsNotNull(gmcpPlugin, "GMCP protocol should be registered");
        Assert.IsNotNull(nawsPlugin, "NAWS protocol should be registered");
        Assert.IsNotNull(msspPlugin, "MSSP protocol should be registered");
        Assert.IsNotNull(ttypePlugin, "Terminal Type protocol should be registered");
        Assert.IsNotNull(charsetPlugin, "Charset protocol should be registered");
        Assert.IsNotNull(eorPlugin, "EOR protocol should be registered");
        Assert.IsNotNull(sgPlugin, "Suppress Go-Ahead protocol should be registered");

        await interpreter.DisposeAsync();
    }

    [Test]
    public async Task CanSubscribeToGMCPEvents()
    {
        // Arrange & Act - Set GMCP callback using fluent API
        var interpreter = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(WriteBackToOutput)
            .OnNegotiation(WriteBackToNegotiate)
            .AddPlugin<GMCPProtocol>()
                .OnGMCPMessage((message) =>
                {
                    _receivedGMCP = message;
                    return ValueTask.CompletedTask;
                })
            .BuildAsync();

        var gmcpPlugin = interpreter.PluginManager!.GetPlugin<GMCPProtocol>();
        Assert.IsNotNull(gmcpPlugin, "GMCP plugin should be registered");

        await interpreter.DisposeAsync();
    }

    [Test]
    public async Task CanSubscribeToNAWSEvents()
    {
        // Arrange & Act - Set NAWS callback using fluent API
        var interpreter = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(WriteBackToOutput)
            .OnNegotiation(WriteBackToNegotiate)
            .AddPlugin<NAWSProtocol>()
                .OnNAWS((width, height) =>
                {
                    return ValueTask.CompletedTask;
                })
            .BuildAsync();

        var nawsPlugin = interpreter.PluginManager!.GetPlugin<NAWSProtocol>();
        Assert.IsNotNull(nawsPlugin, "NAWS plugin should be registered");

        await interpreter.DisposeAsync();
    }

    [Test]
    public async Task CanSubscribeToMSSPEvents()
    {
        // Arrange & Act - Set MSSP callback using fluent API
        var interpreter = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(WriteBackToOutput)
            .OnNegotiation(WriteBackToNegotiate)
            .AddPlugin<MSSPProtocol>()
                .OnMSSP((config) =>
                {
                    _receivedMSSP = config;
                    return ValueTask.CompletedTask;
                })
            .BuildAsync();

        var msspPlugin = interpreter.PluginManager!.GetPlugin<MSSPProtocol>();
        Assert.IsNotNull(msspPlugin, "MSSP plugin should be registered");

        await interpreter.DisposeAsync();
    }

    [Test]
    public void BuilderRequiresMode()
    {
        // Arrange & Act & Assert
        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await new TelnetInterpreterBuilder()
                .UseLogger(logger)
                .OnSubmit(WriteBackToOutput)
                .OnNegotiation(WriteBackToNegotiate)
                .BuildAsync();
        });

        Assert.That(ex!.Message, Does.Contain("mode"));
    }

    [Test]
    public void BuilderRequiresLogger()
    {
        // Arrange & Act & Assert
        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await new TelnetInterpreterBuilder()
                .UseMode(TelnetInterpreter.TelnetMode.Server)
                .OnSubmit(WriteBackToOutput)
                .OnNegotiation(WriteBackToNegotiate)
                .BuildAsync();
        });

        Assert.That(ex!.Message, Does.Contain("Logger"));
    }

    [Test]
    public void BuilderRequiresSubmitCallback()
    {
        // Arrange & Act & Assert
        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await new TelnetInterpreterBuilder()
                .UseMode(TelnetInterpreter.TelnetMode.Server)
                .UseLogger(logger)
                .OnNegotiation(WriteBackToNegotiate)
                .BuildAsync();
        });

        Assert.That(ex!.Message, Does.Contain("Submit callback"));
    }

    [Test]
    public void BuilderRequiresNegotiationCallback()
    {
        // Arrange & Act & Assert
        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await new TelnetInterpreterBuilder()
                .UseMode(TelnetInterpreter.TelnetMode.Server)
                .UseLogger(logger)
                .OnSubmit(WriteBackToOutput)
                .BuildAsync();
        });

        Assert.That(ex!.Message, Does.Contain("Negotiation callback"));
    }

    [Test]
    public async Task CanUseFluentPluginConfiguration()
    {
        // Act - Using the fluent API to chain plugin configuration
        var interpreter = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(WriteBackToOutput)
            .OnNegotiation(WriteBackToNegotiate)
            .AddPlugin<NAWSProtocol>()
                .OnNAWS((height, width) =>
                {
                    return ValueTask.CompletedTask;
                })
            .AddPlugin<GMCPProtocol>()
                .OnGMCPMessage((message) =>
                {
                    return ValueTask.CompletedTask;
                })
            .AddPlugin<MSSPProtocol>()
                .OnMSSP((config) =>
                {
                    return ValueTask.CompletedTask;
                })
            .BuildAsync();

        // Assert
        Assert.IsNotNull(interpreter, "Interpreter should be created");
        
        // Retrieve plugins from manager to verify they were created
        var nawsPlugin = interpreter.PluginManager.GetPlugin<NAWSProtocol>();
        var gmcpPlugin = interpreter.PluginManager.GetPlugin<GMCPProtocol>();
        var msspPlugin = interpreter.PluginManager.GetPlugin<MSSPProtocol>();
        
        Assert.IsNotNull(nawsPlugin, "NAWS plugin should be created");
        Assert.IsNotNull(gmcpPlugin, "GMCP plugin should be created");
        Assert.IsNotNull(msspPlugin, "MSSP plugin should be created");

        // Note: The callbacks are stored internally and will be triggered when protocol events occur
    }
}
