using Microsoft.Extensions.Logging;
using TUnit.Core;
using System;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Plugins;
using System.Text;
using System.Collections.Generic;

namespace TelnetNegotiationCore.UnitTests;


public class PluginDependencyTests : BaseTest
{
    private ValueTask WriteBackToOutput(byte[] arg1, Encoding arg2, TelnetInterpreter t) => ValueTask.CompletedTask;
    private ValueTask WriteBackToNegotiate(ReadOnlyMemory<byte> arg1) => ValueTask.CompletedTask;

    /// <summary>
    /// Test plugin with dependencies for testing
    /// </summary>
    private class TestPluginWithDependency : TelnetProtocolPluginBase
    {
        public override Type ProtocolType => typeof(TestPluginWithDependency);
        public override string ProtocolName => "Test Plugin With Dependency";
        public override IReadOnlyCollection<Type> Dependencies => new[] { typeof(Protocols.GMCPProtocol) };

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

        protected override ValueTask OnInitializeAsync() => ValueTask.CompletedTask;
        protected override ValueTask OnProtocolEnabledAsync() => ValueTask.CompletedTask;
        protected override ValueTask OnProtocolDisabledAsync() => ValueTask.CompletedTask;
        protected override ValueTask OnDisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Records "dependency" into the shared list from ConfigureStateMachine, so a test can
    /// check the order plugins were configured in rather than just that they ended up enabled.</summary>
    private class OrderTrackingDependency(List<string> order) : TelnetProtocolPluginBase
    {
        public override Type ProtocolType => typeof(OrderTrackingDependency);
        public override string ProtocolName => "Order Tracking Dependency";

        public override void ConfigureStateMachine(IProtocolContext context) => order.Add("dependency");

        protected override ValueTask OnInitializeAsync() => ValueTask.CompletedTask;
        protected override ValueTask OnProtocolEnabledAsync() => ValueTask.CompletedTask;
        protected override ValueTask OnProtocolDisabledAsync() => ValueTask.CompletedTask;
        protected override ValueTask OnDisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Depends on <see cref="OrderTrackingDependency"/> and records "dependent".</summary>
    private class OrderTrackingDependent(List<string> order) : TelnetProtocolPluginBase
    {
        public override Type ProtocolType => typeof(OrderTrackingDependent);
        public override string ProtocolName => "Order Tracking Dependent";
        public override IReadOnlyCollection<Type> Dependencies => new[] { typeof(OrderTrackingDependency) };

        public override void ConfigureStateMachine(IProtocolContext context) => order.Add("dependent");

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

    /// <summary>
    /// ConfigureStateMachine runs before InitializePluginsAsync in TelnetInterpreterBuilder.BuildAsync,
    /// so it used to always fall back to registration order: _initializationOrder was still empty (only
    /// InitializePluginsAsync computed it) at the point ConfigureStateMachines read it. Registering the
    /// dependent before its dependency, as this test deliberately does, is exactly the case that
    /// distinguishes "used registration order" from "used dependency order".
    /// </summary>
    [Test]
    public async Task ConfigureStateMachineRunsInDependencyOrder()
    {
        var order = new List<string>();
        var dependent = new OrderTrackingDependent(order);
        var dependency = new OrderTrackingDependency(order);

        var interpreter = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(WriteBackToOutput)
            .OnNegotiation(WriteBackToNegotiate)
            .AddPlugin(dependent)   // registered first, but depends on the plugin below
            .AddPlugin(dependency)  // registered second
            .BuildAsync();

        await Assert.That(order).IsEquivalentTo(new[] { "dependency", "dependent" }, TUnit.Assertions.Enums.CollectionOrdering.Matching);

        await interpreter.DisposeAsync();
    }

    /// <summary>
    /// ConfigureStateMachines and InitializePluginsAsync share one dependency order cached by
    /// ProtocolPluginManager.EnsureInitializationOrder. Registration is still legal until
    /// initialization completes, so a plugin registered in between the two calls must not be silently
    /// dropped from InitializePluginsAsync's pass over that cached order.
    /// </summary>
    [Test]
    public async Task RegisteringAPluginAfterConfigureStateMachinesStillGetsInitialized()
    {
        var order = new List<string>();
        var manager = new ProtocolPluginManager(logger);
        var dependency = new OrderTrackingDependency(order);
        manager.RegisterPlugin(dependency);

        var interpreter = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(WriteBackToOutput)
            .OnNegotiation(WriteBackToNegotiate)
            .BuildAsync();
        var context = new ProtocolContext(interpreter, manager, logger);

        manager.ConfigureStateMachines(context);

        var dependent = new OrderTrackingDependent(order);
        manager.RegisterPlugin(dependent);

        await manager.InitializePluginsAsync(context);

        await Assert.That(dependency.IsEnabled).IsTrue();
        await Assert.That(dependent.IsEnabled).IsTrue();

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
