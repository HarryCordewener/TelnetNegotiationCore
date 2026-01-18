# Plugin Architecture Implementation Guide

## Overview

This document describes the **class-based plugin architecture** for TelnetNegotiationCore. The plugin system allows for:

- **Modular protocol implementations** - Each protocol is a self-contained plugin
- **Explicit dependency management** - Plugins declare dependencies on other plugins
- **Runtime enable/disable** - Protocols can be enabled/disabled dynamically
- **Type-safe registration** - Plugins are identified by their class type
- **No option code coupling** - Uses class types instead of telnet option codes

## Core Architecture

### Key Components

```
ITelnetProtocolPlugin (interface)
    ↓
TelnetProtocolPluginBase (abstract base class)
    ↓
GMCPProtocol, MSDPProtocol, etc. (concrete implementations)

IProtocolContext (interface for plugin-interpreter bridge)
    ↓
ProtocolContext (internal implementation)

ProtocolPluginManager (registration, lifecycle, dependencies)
    ↓
TelnetInterpreterBuilder (fluent API for construction)
```

### Class Diagram

```
┌─────────────────────────────────┐
│  ITelnetProtocolPlugin          │
├─────────────────────────────────┤
│ + ProtocolType: Type            │
│ + ProtocolName: string          │
│ + Dependencies: Type[]          │
│ + IsEnabled: bool               │
│ + InitializeAsync()             │
│ + ConfigureStateMachine()       │
│ + OnEnabledAsync()              │
│ + OnDisabledAsync()             │
│ + DisposeAsync()                │
└─────────────────────────────────┘
         ▲
         │ implements
         │
┌─────────────────────────────────┐
│  TelnetProtocolPluginBase       │
├─────────────────────────────────┤
│ # Context: IProtocolContext     │
│ # OnInitializeAsync()           │
│ # OnProtocolEnabledAsync()      │
│ # OnProtocolDisabledAsync()     │
│ # OnDisposeAsync()              │
└─────────────────────────────────┘
         ▲
         │ inherits
         │
┌─────────────────────────────────┐
│  GMCPProtocol                   │
├─────────────────────────────────┤
│ + ProtocolType = typeof(GMCP... │
│ + ProtocolName = "GMCP"         │
│ + Dependencies = []             │
│ + ConfigureStateMachine()       │
│ + AddGMCPByte()                 │
│ + ProcessGMCPMessageAsync()     │
└─────────────────────────────────┘
```

## Usage Examples

### Basic Usage - Builder Pattern

```csharp
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Protocols;

// Create telnet interpreter with plugins
var telnet = await new TelnetInterpreterBuilder()
    .UseMode(TelnetInterpreter.TelnetMode.Server)
    .UseLogger(logger)
    .OnSubmit(async (bytes, encoding, interpreter) => {
        var text = encoding.GetString(bytes);
        Console.WriteLine($"Received: {text}");
    })
    .OnNegotiation(async (bytes) => {
        await stream.WriteAsync(bytes);
    })
    .AddPlugin<GMCPProtocol>()  // Register GMCP plugin
    .AddPlugin<MSDPProtocol>()  // Register MSDP plugin
    .BuildAsync();
```

### Advanced Usage - Runtime Enable/Disable

```csharp
// Build with plugins
var builder = new TelnetInterpreterBuilder()
    .UseMode(TelnetInterpreter.TelnetMode.Server)
    .UseLogger(logger)
    .OnSubmit(handleSubmit)
    .OnNegotiation(handleNegotiation)
    .AddPlugin<GMCPProtocol>()
    .AddPlugin<MSDPProtocol>()
    .AddPlugin<NAWSProtocol>();

var telnet = await builder.BuildAsync();

// Get plugin manager for runtime control
var pluginManager = builder.GetPluginManager();

// Disable GMCP at runtime
await pluginManager.DisablePluginAsync<GMCPProtocol>();

// Enable it again later
await pluginManager.EnablePluginAsync<GMCPProtocol>();

// Check if plugin is enabled
bool gmcpEnabled = pluginManager.IsPluginEnabled<GMCPProtocol>();
```

### Plugin with Dependencies

```csharp
public class AdvancedGMCPProtocol : TelnetProtocolPluginBase
{
    public override Type ProtocolType => typeof(AdvancedGMCPProtocol);
    
    public override string ProtocolName => "Advanced GMCP";
    
    // This plugin depends on MSDP
    public override IReadOnlyCollection<Type> Dependencies => 
        new[] { typeof(MSDPProtocol) };
    
    public override void ConfigureStateMachine(
        StateMachine<State, Trigger> stateMachine, 
        IProtocolContext context)
    {
        // Configure state machine
        // Dependencies are guaranteed to be initialized first
        var msdpPlugin = context.GetPlugin<MSDPProtocol>();
        // msdpPlugin is guaranteed to be non-null and initialized
    }
}
```

### Plugin-to-Plugin Communication

```csharp
public class GMCPProtocol : TelnetProtocolPluginBase
{
    public async ValueTask ProcessGMCPMessageAsync()
    {
        var message = GetMessage();
        
        // Check if MSDP plugin is available
        var msdpPlugin = Context.GetPlugin<MSDPProtocol>();
        if (msdpPlugin != null && msdpPlugin.IsEnabled)
        {
            // Use shared state to communicate
            Context.SetSharedState("GMCP_Message", message);
            
            // Or call plugin methods directly
            await msdpPlugin.ProcessExternalMessageAsync(message);
        }
    }
}
```

## Creating a Custom Plugin

### Step 1: Create Plugin Class

```csharp
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Stateless;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Plugins;

namespace MyApp.TelnetPlugins;

public class MyCustomProtocol : TelnetProtocolPluginBase
{
    // Unique type identifier
    public override Type ProtocolType => typeof(MyCustomProtocol);
    
    // Human-readable name
    public override string ProtocolName => "My Custom Protocol";
    
    // Optional: Declare dependencies
    public override IReadOnlyCollection<Type> Dependencies => 
        Array.Empty<Type>(); // No dependencies
    
    // Configure state machine for this protocol
    public override void ConfigureStateMachine(
        StateMachine<State, Trigger> stateMachine, 
        IProtocolContext context)
    {
        context.Logger.LogInformation("Configuring {Protocol}", ProtocolName);
        
        // Add state machine configuration here
        // Example:
        // stateMachine.Configure(State.Accepting)
        //     .Permit(Trigger.CustomStart, State.CustomProcessing);
    }
    
    // Optional: Initialize resources
    protected override ValueTask OnInitializeAsync()
    {
        Context.Logger.LogInformation("{Protocol} initialized", ProtocolName);
        return ValueTask.CompletedTask;
    }
    
    // Optional: Handle enable event
    protected override ValueTask OnProtocolEnabledAsync()
    {
        Context.Logger.LogInformation("{Protocol} enabled", ProtocolName);
        return ValueTask.CompletedTask;
    }
    
    // Optional: Handle disable event
    protected override ValueTask OnProtocolDisabledAsync()
    {
        Context.Logger.LogInformation("{Protocol} disabled", ProtocolName);
        return ValueTask.CompletedTask;
    }
    
    // Optional: Cleanup resources
    protected override ValueTask OnDisposeAsync()
    {
        Context.Logger.LogInformation("{Protocol} disposed", ProtocolName);
        return ValueTask.CompletedTask;
    }
}
```

### Step 2: Register Plugin

```csharp
var telnet = await new TelnetInterpreterBuilder()
    .UseMode(TelnetInterpreter.TelnetMode.Server)
    .UseLogger(logger)
    .OnSubmit(handleSubmit)
    .OnNegotiation(handleNegotiation)
    .AddPlugin<MyCustomProtocol>()  // Register your plugin
    .BuildAsync();
```

## Plugin Lifecycle

The plugin lifecycle follows this sequence:

```
1. Registration
   ↓
2. Dependency Resolution (topological sort)
   ↓
3. State Machine Configuration (in dependency order)
   ↓
4. Initialization (in dependency order)
   ↓
5. Runtime (enabled)
   ↓
6. Optional: Disable/Enable cycles
   ↓
7. Disposal (reverse dependency order)
```

### Lifecycle Methods

| Method | When Called | Purpose |
|--------|-------------|---------|
| `ConfigureStateMachine()` | During build, before initialization | Set up state machine transitions |
| `InitializeAsync()` | During build, after state machine config | Initialize resources, subscribe to events |
| `OnEnabledAsync()` | When enabled (initially or at runtime) | Start protocol functionality |
| `OnDisabledAsync()` | When disabled at runtime | Pause protocol functionality |
| `DisposeAsync()` | During shutdown | Clean up resources |

## Dependency Management

### Automatic Resolution

The `ProtocolPluginManager` automatically:

1. **Validates dependencies** - Ensures all required plugins are registered
2. **Detects circular dependencies** - Throws exception if cycle detected
3. **Topological sort** - Initializes plugins in correct order
4. **Prevents orphaning** - Cannot disable plugin if others depend on it

### Example Dependency Graph

```
NAWSProtocol (no dependencies)
     ↑
MSDPProtocol (no dependencies)
     ↑
GMCPProtocol (depends on MSDP)
     ↑
AdvancedGMCP (depends on GMCP)
```

**Initialization order**: NAWS → MSDP → GMCP → AdvancedGMCP

## Shared State

Plugins can communicate via shared state:

```csharp
// Plugin A sets shared state
Context.SetSharedState("config_value", myValue);

// Plugin B reads shared state
if (Context.TryGetSharedState<int>("config_value", out var value))
{
    // Use value
}
```

### Best Practices for Shared State

- Use namespaced keys: `"PluginName.KeyName"`
- Document shared state in plugin documentation
- Prefer direct plugin references over shared state when possible
- Use strongly-typed getters: `TryGetSharedState<T>()`

## Protocol Context

The `IProtocolContext` provides plugins access to:

```csharp
interface IProtocolContext
{
    // Core services
    ILogger Logger { get; }
    Encoding CurrentEncoding { get; }
    TelnetMode Mode { get; }
    StateMachine<State, Trigger> StateMachine { get; }
    
    // Actions
    void SetEncoding(Encoding encoding);
    ValueTask SendNegotiationAsync(ReadOnlyMemory<byte> bytes);
    ValueTask WriteToBufferAsync(ReadOnlyMemory<byte> data);
    
    // Plugin discovery
    T? GetPlugin<T>() where T : class, ITelnetProtocolPlugin;
    bool IsPluginEnabled<T>() where T : class, ITelnetProtocolPlugin;
    IReadOnlyCollection<ITelnetProtocolPlugin> GetAllPlugins();
    
    // Shared state
    void SetSharedState(string key, object? value);
    object? GetSharedState(string key);
    bool TryGetSharedState<T>(string key, out T? value);
}
```

## Migration from Partial Classes

### Before (Monolithic)

```csharp
// TelnetGMCPInterpreter.cs - partial class
public partial class TelnetInterpreter
{
    private List<byte> _GMCPBytes = [];
    
    private StateMachine<State, Trigger> SetupGMCPNegotiation(
        StateMachine<State, Trigger> stateMachine)
    {
        // Configure state machine
        // Hardcoded dependency on MSDP
        if (package == "MSDP")
        {
            await SignalOnMSDPAsync(this, info);
        }
    }
}
```

### After (Plugin)

```csharp
// GMCPProtocol.cs - standalone plugin
public class GMCPProtocol : TelnetProtocolPluginBase
{
    private readonly List<byte> _gmcpBytes = [];
    
    public override IReadOnlyCollection<Type> Dependencies => 
        new[] { typeof(MSDPProtocol) }; // Explicit dependency
    
    public override void ConfigureStateMachine(
        StateMachine<State, Trigger> stateMachine, 
        IProtocolContext context)
    {
        // Configure state machine
        // Use plugin context instead of hardcoded coupling
        var msdpPlugin = context.GetPlugin<MSDPProtocol>();
    }
}
```

## Benefits of Plugin Architecture

### 1. **Modularity**
- Each protocol is self-contained
- Can develop/test protocols independently
- Clear boundaries between protocols

### 2. **Explicit Dependencies**
- Dependencies declared in code
- Automatic validation and ordering
- No hidden coupling

### 3. **Runtime Flexibility**
- Enable/disable protocols dynamically
- Hot-swap protocol implementations
- Load protocols from external assemblies

### 4. **Testability**
- Mock individual plugins
- Test protocols in isolation
- No need for full interpreter instance

### 5. **Extensibility**
- Third-party plugins without modifying core
- Plugin discovery via assembly scanning
- Version-independent plugin API

## Backward Compatibility

The plugin architecture can coexist with the existing partial class implementation:

```csharp
// Option 1: Use new plugin architecture
var telnet = await new TelnetInterpreterBuilder()
    .UseMode(TelnetMode.Server)
    .UseLogger(logger)
    .OnSubmit(handleSubmit)
    .OnNegotiation(handleNegotiation)
    .AddPlugin<GMCPProtocol>()
    .BuildAsync();

// Option 2: Use existing constructor (still works)
var telnet = new TelnetInterpreter(TelnetMode.Server, logger)
{
    CallbackOnSubmitAsync = handleSubmit,
    CallbackNegotiationAsync = handleNegotiation
};
await telnet.BuildAsync();
```

## Performance Considerations

### Plugin Overhead

- **Registration**: O(n) where n = number of plugins
- **Dependency resolution**: O(n + e) where e = number of dependencies
- **Initialization**: O(n) sequential initialization
- **Runtime**: Negligible overhead (direct method calls)

### Memory

- Each plugin instance: ~100-500 bytes
- Plugin manager overhead: ~1KB
- Shared state dictionary: ~100 bytes + content

### Optimization Tips

1. **Lazy plugin loading** - Only load plugins when needed
2. **Plugin pooling** - Reuse plugin instances
3. **Batch operations** - Process multiple messages together
4. **Minimize shared state** - Prefer direct plugin references

## Thread Safety

### Thread-Safe Operations

- Plugin registration (before Build)
- Plugin initialization (sequential)
- Shared state access (dictionary is not thread-safe)

### Not Thread-Safe

- Concurrent plugin enable/disable
- Concurrent shared state writes
- Plugin state mutation from multiple threads

### Recommendations

```csharp
// Use locks for concurrent access
private readonly object _stateLock = new();

public void SafeSharedStateWrite(string key, object value)
{
    lock (_stateLock)
    {
        Context.SetSharedState(key, value);
    }
}
```

## Future Enhancements

### Planned Features

1. **Assembly scanning** - Auto-discover plugins
2. **Plugin metadata** - Version, author, license
3. **Plugin configuration** - Per-plugin settings
4. **Event bus** - Pub/sub between plugins
5. **Plugin priorities** - Control initialization order
6. **Hot reload** - Reload plugins without restart

### Example: Assembly Scanning

```csharp
// Future API
var telnet = await new TelnetInterpreterBuilder()
    .UseMode(TelnetMode.Server)
    .UseLogger(logger)
    .OnSubmit(handleSubmit)
    .OnNegotiation(handleNegotiation)
    .ScanAssemblyForPlugins(typeof(GMCPProtocol).Assembly)
    .BuildAsync();
```

## Summary

The class-based plugin architecture provides:

✅ **Type-safe** - Uses class types instead of option codes  
✅ **Modular** - Self-contained protocol implementations  
✅ **Flexible** - Runtime enable/disable  
✅ **Testable** - Mock and test plugins independently  
✅ **Extensible** - Easy to add new protocols  
✅ **Maintainable** - Clear dependencies and boundaries  
✅ **Backward compatible** - Works alongside existing code  

This architecture supports the goal of protocol dependency management and runtime configuration while providing a modern, maintainable codebase.
