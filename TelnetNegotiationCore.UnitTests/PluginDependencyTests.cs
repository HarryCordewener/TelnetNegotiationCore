using Microsoft.Extensions.Logging;
using TUnit.Core;
using System;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Plugins;
using System.Text;
using System.Collections.Generic;
using TelnetNegotiationCore.Models;

namespace TelnetNegotiationCore.UnitTests;


public class PluginDependencyTests : BaseTest
{
    private ValueTask WriteBackToOutput(byte[] arg1, Encoding arg2, TelnetInterpreter t) => ValueTask.CompletedTask;
    private ValueTask WriteBackToNegotiate(byte[] arg1) => ValueTask.CompletedTask;

    /// <summary>
    /// Test plugin with dependencies for testing
    /// </summary>
    private class TestPluginWithDependency : TelnetProtocolPluginBase
    {
        public override Type ProtocolType => typeof(TestPluginWithDependency);
        public override string ProtocolName => "Test Plugin With Dependency";
        public override IReadOnlyCollection<Type> Dependencies => new[] { typeof(Protocols.GMCPProtocol) };

        public override void ConfigureStateMachine(Stateless.StateMachine<State, Trigger> stateMachine, IProtocolContext context)
        {
            // No-op for test
        }

        protected override ValueTask OnInitializeAsync() => ValueTask.CompletedTask;
        protected override ValueTask OnProtocolEnabledAsync() => ValueTask.CompletedTask;
        protected override ValueTask OnProtocolDisabledAsync() => ValueTask.CompletedTask;
        protected override ValueTask OnDisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// Test plugin that creates a circular dependency
    /// </summary>
    private class TestPluginCircularA : TelnetProtocolPluginBase
    {
        public override Type ProtocolType => typeof(TestPluginCircularA);
        public override string ProtocolName => "Test Plugin Circular A";
        public override IReadOnlyCollection<Type> Dependencies => new[] { typeof(TestPluginCircularB) };

        public override void ConfigureStateMachine(Stateless.StateMachine<State, Trigger> stateMachine, IProtocolContext context)
        {
            // No-op for test
        }

        protected override ValueTask OnInitializeAsync() => ValueTask.CompletedTask;
        protected override ValueTask OnProtocolEnabledAsync() => ValueTask.CompletedTask;
        protected override ValueTask OnProtocolDisabledAsync() => ValueTask.CompletedTask;
        protected override ValueTask OnDisposeAsync() => ValueTask.CompletedTask;
    }

    private class TestPluginCircularB : TelnetProtocolPluginBase
    {
        public override Type ProtocolType => typeof(TestPluginCircularB);
        public override string ProtocolName => "Test Plugin Circular B";
        public override IReadOnlyCollection<Type> Dependencies => new[] { typeof(TestPluginCircularA) };

        public override void ConfigureStateMachine(Stateless.StateMachine<State, Trigger> stateMachine, IProtocolContext context)
        {
            // No-op for test
        }

        protected override ValueTask OnInitializeAsync() => ValueTask.CompletedTask;
        protected override ValueTask OnProtocolEnabledAsync() => ValueTask.CompletedTask;
        protected override ValueTask OnProtocolDisabledAsync() => ValueTask.CompletedTask;
        protected override ValueTask OnDisposeAsync() => ValueTask.CompletedTask;
    }

    [Test]
    public async Task ThrowsExceptionWhenDependencyIsMissing()
    {
        // Arrange & Act & Assert
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await new TelnetInterpreterBuilder()
                .UseMode(TelnetInterpreter.TelnetMode.Server)
                .UseLogger(logger)
                .OnSubmit(WriteBackToOutput)
                .OnNegotiation(WriteBackToNegotiate)
                .AddPlugin(new TestPluginWithDependency()) // Has dependency on GMCPProtocol but it's not added
                .BuildAsync();
        });

        await Assert.That(ex!.Message).Contains("depends on");
        await Assert.That(ex.Message).Contains("GMCPProtocol");
        await Assert.That(ex.Message).Contains("not registered");
    }

    [Test]
    public async Task SucceedsWhenDependencyIsPresent()
    {
        // Arrange & Act
        var interpreter = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(WriteBackToOutput)
            .OnNegotiation(WriteBackToNegotiate)
            .AddPlugin<Protocols.GMCPProtocol>() // Add the dependency first
            .AddPlugin(new TestPluginWithDependency()) // Now this should work
            .BuildAsync();

        // Assert
        await Assert.That(interpreter).IsNotNull();
        var plugin = interpreter.PluginManager!.GetPlugin<TestPluginWithDependency>();
        await Assert.That(plugin).IsNotNull();

        await interpreter.DisposeAsync();
    }

    [Test]
    public async Task ThrowsExceptionOnCircularDependency()
    {
        // Arrange & Act & Assert
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await new TelnetInterpreterBuilder()
                .UseMode(TelnetInterpreter.TelnetMode.Server)
                .UseLogger(logger)
                .OnSubmit(WriteBackToOutput)
                .OnNegotiation(WriteBackToNegotiate)
                .AddPlugin(new TestPluginCircularA())
                .AddPlugin(new TestPluginCircularB())
                .BuildAsync();
        });

        await Assert.That(ex!.Message).Contains("Circular dependency");
    }

    [Test]
    public async Task PluginsInitializedInDependencyOrder()
    {
        // Arrange
        var initOrder = new System.Collections.Generic.List<string>();
        
        // Create a plugin that tracks initialization
        var gmcpPlugin = new Protocols.GMCPProtocol();
        var testPlugin = new TestPluginWithDependency();

        // Act
        var interpreter = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(WriteBackToOutput)
            .OnNegotiation(WriteBackToNegotiate)
            .AddPlugin(testPlugin) // Add dependent plugin first
            .AddPlugin(gmcpPlugin) // Add dependency second
            .BuildAsync();

        // Assert - both plugins should be registered and initialized
        var gmcp = interpreter.PluginManager!.GetPlugin<Protocols.GMCPProtocol>();
        var test = interpreter.PluginManager!.GetPlugin<TestPluginWithDependency>();
        
        await Assert.That(gmcp).IsNotNull();
        await Assert.That(test).IsNotNull();
        await Assert.That(gmcp.IsEnabled).IsTrue();
        await Assert.That(test.IsEnabled).IsTrue();

        await interpreter.DisposeAsync();
    }

    [Test]
    public async Task CanDisablePluginWhenNoDependents()
    {
        // Arrange
        var interpreter = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(WriteBackToOutput)
            .OnNegotiation(WriteBackToNegotiate)
            .AddPlugin<Protocols.GMCPProtocol>()
            .BuildAsync();

        // Act
        await interpreter.PluginManager!.DisablePluginAsync<Protocols.GMCPProtocol>();

        // Assert
        var gmcp = interpreter.PluginManager.GetPlugin<Protocols.GMCPProtocol>();
        await Assert.That(gmcp!.IsEnabled).IsFalse();

        await interpreter.DisposeAsync();
    }

    [Test]
    public async Task CannotDisablePluginWhenHasDependents()
    {
        // Arrange & Act & Assert
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            var interpreter = await new TelnetInterpreterBuilder()
                .UseMode(TelnetInterpreter.TelnetMode.Server)
                .UseLogger(logger)
                .OnSubmit(WriteBackToOutput)
                .OnNegotiation(WriteBackToNegotiate)
                .AddPlugin<Protocols.GMCPProtocol>()
                .AddPlugin(new TestPluginWithDependency()) // Depends on GMCP
                .BuildAsync();

            // Try to disable GMCP while TestPluginWithDependency depends on it
            await interpreter.PluginManager!.DisablePluginAsync<Protocols.GMCPProtocol>();
        });

        await Assert.That(ex!.Message).Contains("Cannot disable");
        await Assert.That(ex.Message).Contains("required by");
    }

    [Test]
    public async Task AllDefaultMUDProtocolsHaveNoDependencyIssues()
    {
        // Arrange & Act - This should succeed without any dependency errors
        var interpreter = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(WriteBackToOutput)
            .OnNegotiation(WriteBackToNegotiate)
            .AddDefaultMUDProtocols()
            .BuildAsync();

        // Assert - Verify all default protocols are registered
        await Assert.That(interpreter.PluginManager!.GetPlugin<Protocols.NAWSProtocol>()).IsNotNull();
        await Assert.That(interpreter.PluginManager.GetPlugin<Protocols.GMCPProtocol>()).IsNotNull();
        await Assert.That(interpreter.PluginManager.GetPlugin<Protocols.MSSPProtocol>()).IsNotNull();
        await Assert.That(interpreter.PluginManager.GetPlugin<Protocols.TerminalTypeProtocol>()).IsNotNull();
        await Assert.That(interpreter.PluginManager.GetPlugin<Protocols.CharsetProtocol>()).IsNotNull();
        await Assert.That(interpreter.PluginManager.GetPlugin<Protocols.EORProtocol>()).IsNotNull();
        await Assert.That(interpreter.PluginManager.GetPlugin<Protocols.SuppressGoAheadProtocol>()).IsNotNull();

        await interpreter.DisposeAsync();
    }
}
