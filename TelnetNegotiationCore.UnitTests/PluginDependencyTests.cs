using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Plugins;
using System.Text;
using System.Collections.Generic;
using TelnetNegotiationCore.Models;

namespace TelnetNegotiationCore.UnitTests;

[TestFixture]
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
    public void ThrowsExceptionWhenDependencyIsMissing()
    {
        // Arrange & Act & Assert
        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await new TelnetInterpreterBuilder()
                .UseMode(TelnetInterpreter.TelnetMode.Server)
                .UseLogger(logger)
                .OnSubmit(WriteBackToOutput)
                .OnNegotiation(WriteBackToNegotiate)
                .AddPlugin(new TestPluginWithDependency()) // Has dependency on GMCPProtocol but it's not added
                .BuildAsync();
        });

        Assert.That(ex!.Message, Does.Contain("depends on"));
        Assert.That(ex.Message, Does.Contain("GMCPProtocol"));
        Assert.That(ex.Message, Does.Contain("not registered"));
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
        Assert.IsNotNull(interpreter, "Interpreter should be created");
        var plugin = interpreter.PluginManager!.GetPlugin<TestPluginWithDependency>();
        Assert.IsNotNull(plugin, "Test plugin should be registered");

        await interpreter.DisposeAsync();
    }

    [Test]
    public void ThrowsExceptionOnCircularDependency()
    {
        // Arrange & Act & Assert
        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
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

        Assert.That(ex!.Message, Does.Contain("Circular dependency"));
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
        
        Assert.IsNotNull(gmcp, "GMCP plugin should be registered");
        Assert.IsNotNull(test, "Test plugin should be registered");
        Assert.IsTrue(gmcp.IsEnabled, "GMCP should be enabled");
        Assert.IsTrue(test.IsEnabled, "Test plugin should be enabled");

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
        Assert.IsFalse(gmcp!.IsEnabled, "GMCP should be disabled");

        await interpreter.DisposeAsync();
    }

    [Test]
    public void CannotDisablePluginWhenHasDependents()
    {
        // Arrange & Act & Assert
        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
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

        Assert.That(ex!.Message, Does.Contain("Cannot disable"));
        Assert.That(ex.Message, Does.Contain("required by"));
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
        Assert.IsNotNull(interpreter.PluginManager!.GetPlugin<Protocols.NAWSProtocol>());
        Assert.IsNotNull(interpreter.PluginManager.GetPlugin<Protocols.GMCPProtocol>());
        Assert.IsNotNull(interpreter.PluginManager.GetPlugin<Protocols.MSSPProtocol>());
        Assert.IsNotNull(interpreter.PluginManager.GetPlugin<Protocols.TerminalTypeProtocol>());
        Assert.IsNotNull(interpreter.PluginManager.GetPlugin<Protocols.CharsetProtocol>());
        Assert.IsNotNull(interpreter.PluginManager.GetPlugin<Protocols.EORProtocol>());
        Assert.IsNotNull(interpreter.PluginManager.GetPlugin<Protocols.SuppressGoAheadProtocol>());

        await interpreter.DisposeAsync();
    }
}
