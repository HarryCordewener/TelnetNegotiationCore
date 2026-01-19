using Microsoft.Extensions.Logging;
using TUnit.Core;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Protocols;

namespace TelnetNegotiationCore.UnitTests;


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
        await Assert.That(interpreter).IsNotNull();
        await Assert.That(interpreter.PluginManager).IsNotNull();
        
        // Verify all default protocols are registered
        var gmcpPlugin = interpreter.PluginManager.GetPlugin<GMCPProtocol>();
        var nawsPlugin = interpreter.PluginManager.GetPlugin<NAWSProtocol>();
        var msspPlugin = interpreter.PluginManager.GetPlugin<MSSPProtocol>();
        var ttypePlugin = interpreter.PluginManager.GetPlugin<TerminalTypeProtocol>();
        var charsetPlugin = interpreter.PluginManager.GetPlugin<CharsetProtocol>();
        var eorPlugin = interpreter.PluginManager.GetPlugin<EORProtocol>();
        var sgPlugin = interpreter.PluginManager.GetPlugin<SuppressGoAheadProtocol>();

        await Assert.That(gmcpPlugin).IsNotNull();
        await Assert.That(nawsPlugin).IsNotNull();
        await Assert.That(msspPlugin).IsNotNull();
        await Assert.That(ttypePlugin).IsNotNull();
        await Assert.That(charsetPlugin).IsNotNull();
        await Assert.That(eorPlugin).IsNotNull();
        await Assert.That(sgPlugin).IsNotNull();

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
        await Assert.That(gmcpPlugin).IsNotNull();

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
        await Assert.That(nawsPlugin).IsNotNull();

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
        await Assert.That(msspPlugin).IsNotNull();

        await interpreter.DisposeAsync();
    }

    [Test]
    public async Task BuilderRequiresMode()
    {
        // Arrange & Act & Assert
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await new TelnetInterpreterBuilder()
                .UseLogger(logger)
                .OnSubmit(WriteBackToOutput)
                .OnNegotiation(WriteBackToNegotiate)
                .BuildAsync();
        });

        await Assert.That(ex!.Message).Contains("mode");
    }

    [Test]
    public async Task BuilderRequiresLogger()
    {
        // Arrange & Act & Assert
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await new TelnetInterpreterBuilder()
                .UseMode(TelnetInterpreter.TelnetMode.Server)
                .OnSubmit(WriteBackToOutput)
                .OnNegotiation(WriteBackToNegotiate)
                .BuildAsync();
        });

        await Assert.That(ex!.Message).Contains("Logger");
    }

    [Test]
    public async Task BuilderRequiresSubmitCallback()
    {
        // Arrange & Act & Assert
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await new TelnetInterpreterBuilder()
                .UseMode(TelnetInterpreter.TelnetMode.Server)
                .UseLogger(logger)
                .OnNegotiation(WriteBackToNegotiate)
                .BuildAsync();
        });

        await Assert.That(ex!.Message).Contains("Submit callback");
    }

    [Test]
    public async Task BuilderRequiresNegotiationCallback()
    {
        // Arrange & Act & Assert
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await new TelnetInterpreterBuilder()
                .UseMode(TelnetInterpreter.TelnetMode.Server)
                .UseLogger(logger)
                .OnSubmit(WriteBackToOutput)
                .BuildAsync();
        });

        await Assert.That(ex!.Message).Contains("Negotiation callback");
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
        await Assert.That(interpreter).IsNotNull();
        
        // Retrieve plugins from manager to verify they were created
        var nawsPlugin = interpreter.PluginManager.GetPlugin<NAWSProtocol>();
        var gmcpPlugin = interpreter.PluginManager.GetPlugin<GMCPProtocol>();
        var msspPlugin = interpreter.PluginManager.GetPlugin<MSSPProtocol>();
        
        await Assert.That(nawsPlugin).IsNotNull();
        await Assert.That(gmcpPlugin).IsNotNull();
        await Assert.That(msspPlugin).IsNotNull();

        // Note: The callbacks are stored internally and will be triggered when protocol events occur
    }
}
