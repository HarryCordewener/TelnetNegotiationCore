# TelnetNegotiationCore - Architecture Recommendations

## Executive Summary

This document provides architectural recommendations for improving the TelnetNegotiationCore library to better support:
1. **Protocol Dependencies**: Allow protocols that rely on one another to be properly managed
2. **Dynamic Enable/Disable**: Enable runtime configuration of which protocols are active
3. **Modern C# Patterns**: Leverage contemporary software design patterns and NuGet packages

**Note**: This is a recommendation document only. No implementation has been performed.

---

## Table of Contents

1. [Current Architecture Analysis](#current-architecture-analysis)
2. [Identified Issues](#identified-issues)
3. [Recommended Architecture](#recommended-architecture)
4. [Design Patterns](#design-patterns)
5. [Recommended NuGet Packages](#recommended-nuget-packages)
6. [Implementation Strategy](#implementation-strategy)
7. [Migration Path](#migration-path)

---

## Current Architecture Analysis

### Current Implementation

The TelnetNegotiationCore library uses a **monolithic partial class pattern** where:

- **Core Class**: `TelnetInterpreter` is split across 10+ files
- **Protocol Files**: Each protocol (GMCP, MSDP, MSSP, EOR, SuppressGA, Charset, NAWS, TerminalType) has a partial class file
- **State Machine**: Uses the `Stateless` library for managing negotiation states
- **Initialization**: Hardcoded list of `Setup*` methods called via `AggregateRight`

```csharp
// Current initialization in TelnetStandardInterpreter.cs (lines 108-120)
new List<Func<StateMachine<State, Trigger>, StateMachine<State, Trigger>>>
{
    SetupSafeNegotiation,
    SetupEORNegotiation,
    SetupSuppressGANegotiation,
    SetupMSSPNegotiation,
    SetupMSDPNegotiation,
    SetupGMCPNegotiation,
    SetupTelnetTerminalType,
    SetupCharsetNegotiation,
    SetupNAWS,
    SetupStandardProtocol
}.AggregateRight(TelnetStateMachine, (func, stateMachine) => func(stateMachine));
```

### Design Patterns Currently Used

| Pattern | Usage | Location |
|---------|-------|----------|
| **Partial Classes** | Protocol separation | All Interpreter files |
| **Fluent Builder** | Configuration | `new TelnetInterpreter().RegisterMSSPConfig().BuildAsync()` |
| **State Machine** | Protocol negotiation | Stateless library throughout |
| **Callback Pattern** | Event handling | `SignalOnGMCPAsync`, `CallbackNegotiationAsync`, etc. |
| **Lazy Initialization** | Character set loading | `SupportedCharacterSets` |
| **Mode Pattern** | Server/Client behavior | `TelnetMode.Server` vs `TelnetMode.Client` |

---

## Identified Issues

### 1. **Hardcoded Protocol Dependencies**

**Issue**: GMCP has a hardcoded dependency on MSDP

```csharp
// TelnetGMCPInterpreter.cs:149
if (package == "MSDP")
{
    if (SignalOnMSDPAsync is not null)
        await SignalOnMSDPAsync(this, info);
}
```

**Impact**: Cannot disable MSDP without breaking GMCP message routing.

### 2. **Implicit Protocol Coupling**

**Issue**: EOR and SuppressGA share fallback logic

```csharp
// TelnetEORInterpreter.cs:124-135
public async ValueTask SendPromptAsync()
{
    if (_doEOR is true)
    {
        await CallbackNegotiationAsync([..IAC_EOR]);
    }
    else if (_doGA is not null)  // Fallback to GA
    {
        await CallbackNegotiationAsync([..IAC_GA]);
    }
}
```

**Impact**: Disabling EOR silently changes behavior to use SuppressGA without explicit configuration.

### 3. **Monolithic Class Structure**

**Issue**: TelnetInterpreter has 100+ state machine states spread across partial files

**Impact**:
- Difficult to test protocols in isolation
- Cannot selectively enable/disable protocols at runtime
- Adding new protocols requires modifying core class
- State explosion (each protocol adds 5-10 states)

### 4. **Fixed Initialization Order**

**Issue**: Protocol setup order is hardcoded in a list

**Impact**:
- Cannot conditionally include protocols
- Cannot reorder protocols without source changes
- No way to configure which protocols are active

### 5. **No Protocol Abstraction**

**Issue**: No interface or base class for protocols

**Impact**:
- Each protocol directly modifies TelnetInterpreter state
- No consistent protocol lifecycle (Initialize → Negotiate → Handle → Disable)
- Cannot unit test protocols independently

### 6. **Global State Management**

**Issue**: Each protocol adds fields to TelnetInterpreter

```csharp
private List<byte> _GMCPBytes = [];
private List<byte> _currentMSDPInfo = [];
private bool? _doEOR = null;
private bool? _doGA = null;
// ... many more
```

**Impact**: Pollutes global namespace, difficult to encapsulate, memory overhead for unused protocols.

---

## Recommended Architecture

### Overview: Plugin-Based Protocol Architecture

Transform from a monolithic partial class to a **modular plugin-based architecture** where:

1. Each protocol is an independent plugin implementing a common interface
2. Protocols declare their dependencies explicitly
3. A protocol manager handles lifecycle and dependency resolution
4. Protocols can be dynamically enabled/disabled via configuration

### Core Architecture Components

```
┌─────────────────────────────────────────────────────────────┐
│                    TelnetInterpreter                         │
│  (Orchestrator - manages protocol plugins and core logic)   │
└──────────────────────┬──────────────────────────────────────┘
                       │
                       │ uses
                       ▼
┌─────────────────────────────────────────────────────────────┐
│                  IProtocolManager                            │
│  - Register protocols                                        │
│  - Resolve dependencies                                      │
│  - Enable/Disable protocols                                  │
│  - Lifecycle management                                      │
└──────────────────────┬──────────────────────────────────────┘
                       │
                       │ manages
                       ▼
              ┌────────────────┐
              │  ITelnetProtocol  │
              │   (interface)     │
              └────────┬──────────┘
                       │
       ┌───────────────┼───────────────┬──────────────┐
       │               │               │              │
       ▼               ▼               ▼              ▼
┌──────────┐    ┌──────────┐    ┌──────────┐   ┌──────────┐
│  GMCP    │    │  MSDP    │    │   EOR    │   │  NAWS    │
│ Protocol │    │ Protocol │    │ Protocol │   │ Protocol │
└──────────┘    └──────────┘    └──────────┘   └──────────┘
```

---

## Design Patterns

### 1. **Plugin Pattern (Strategy + Factory)**

**Purpose**: Allow protocols to be independently developed, tested, and configured

**Implementation**:

```csharp
/// <summary>
/// Represents a telnet protocol option (RFC 855 compliant)
/// </summary>
public interface ITelnetProtocol
{
    /// <summary>
    /// Protocol identifier (e.g., GMCP = 201, MSDP = 69)
    /// </summary>
    byte OptionCode { get; }
    
    /// <summary>
    /// Human-readable protocol name
    /// </summary>
    string Name { get; }
    
    /// <summary>
    /// Protocols this protocol depends on (by option code)
    /// </summary>
    IReadOnlyCollection<byte> Dependencies { get; }
    
    /// <summary>
    /// Initialize the protocol with the state machine
    /// </summary>
    void Initialize(StateMachine<State, Trigger> stateMachine, TelnetMode mode, ILogger logger);
    
    /// <summary>
    /// Handle incoming subnegotiation data for this protocol
    /// </summary>
    ValueTask HandleSubnegotiationAsync(ReadOnlyMemory<byte> data);
    
    /// <summary>
    /// Called when negotiation completes (WILL/DO agreed)
    /// </summary>
    ValueTask OnNegotiationCompleteAsync();
    
    /// <summary>
    /// Called when protocol is disabled (WONT/DONT)
    /// </summary>
    ValueTask OnDisabledAsync();
    
    /// <summary>
    /// Get initial negotiation bytes to send
    /// </summary>
    byte[] GetInitialNegotiation();
}
```

**Benefits**:
- Each protocol is self-contained
- Clear lifecycle: Initialize → Negotiate → Handle → Disable
- Testable in isolation
- Can be loaded from external assemblies

### 2. **Dependency Injection (DI) Pattern**

**Purpose**: Manage protocol dependencies and configuration

**Implementation**:

```csharp
public class TelnetInterpreterBuilder
{
    private readonly IServiceCollection _services;
    private readonly List<Type> _protocolTypes = new();
    
    public TelnetInterpreterBuilder()
    {
        _services = new ServiceCollection();
        _services.AddSingleton<IProtocolManager, ProtocolManager>();
        _services.AddSingleton<TelnetInterpreter>();
    }
    
    /// <summary>
    /// Register a protocol to be included
    /// </summary>
    public TelnetInterpreterBuilder AddProtocol<TProtocol>() 
        where TProtocol : class, ITelnetProtocol
    {
        _services.AddSingleton<ITelnetProtocol, TProtocol>();
        _protocolTypes.Add(typeof(TProtocol));
        return this;
    }
    
    /// <summary>
    /// Configure protocol-specific options
    /// </summary>
    public TelnetInterpreterBuilder ConfigureProtocol<TProtocol>(Action<TProtocol> configure)
        where TProtocol : class, ITelnetProtocol
    {
        _services.PostConfigure<TProtocol>(configure);
        return this;
    }
    
    public async ValueTask<TelnetInterpreter> BuildAsync()
    {
        var provider = _services.BuildServiceProvider();
        var interpreter = provider.GetRequiredService<TelnetInterpreter>();
        await interpreter.InitializeAsync(provider);
        return interpreter;
    }
}
```

**Usage**:

```csharp
var telnet = await new TelnetInterpreterBuilder()
    .AddProtocol<GMCPProtocol>()
    .AddProtocol<MSDPProtocol>()
    .AddProtocol<NAWSProtocol>()
    .ConfigureProtocol<GMCPProtocol>(gmcp => 
    {
        gmcp.OnReceived = async (pkg, data) => Console.WriteLine($"GMCP: {pkg}");
    })
    .BuildAsync();
```

**Benefits**:
- Microsoft.Extensions.DependencyInjection integration
- Standard .NET pattern
- Supports configuration validation
- Easy testing with mock protocols

### 3. **Chain of Responsibility Pattern**

**Purpose**: Handle protocol dependencies and fallback behavior

**Implementation**:

```csharp
public interface IProtocolDependencyResolver
{
    /// <summary>
    /// Resolve protocol dependencies and return ordered list
    /// </summary>
    IReadOnlyList<ITelnetProtocol> ResolveDependencies(
        IEnumerable<ITelnetProtocol> protocols);
    
    /// <summary>
    /// Validate all dependencies are satisfied
    /// </summary>
    ValidationResult ValidateDependencies(IEnumerable<ITelnetProtocol> protocols);
}

public class ProtocolDependencyResolver : IProtocolDependencyResolver
{
    public IReadOnlyList<ITelnetProtocol> ResolveDependencies(
        IEnumerable<ITelnetProtocol> protocols)
    {
        var protocolList = protocols.ToList();
        var graph = BuildDependencyGraph(protocolList);
        return TopologicalSort(graph);
    }
    
    public ValidationResult ValidateDependencies(
        IEnumerable<ITelnetProtocol> protocols)
    {
        var protocolsByCode = protocols.ToDictionary(p => p.OptionCode);
        var errors = new List<string>();
        
        foreach (var protocol in protocols)
        {
            foreach (var depCode in protocol.Dependencies)
            {
                if (!protocolsByCode.ContainsKey(depCode))
                {
                    errors.Add(
                        $"Protocol '{protocol.Name}' depends on option code {depCode}, " +
                        $"but no such protocol is registered.");
                }
            }
        }
        
        return new ValidationResult(errors.Count == 0, errors);
    }
}
```

**Benefits**:
- Explicit dependency declaration
- Topological sort ensures correct initialization order
- Compile-time safety via validation
- Clear error messages for missing dependencies

### 4. **Observer Pattern (Events)**

**Purpose**: Decouple protocol events from handler implementation

**Implementation**:

```csharp
public class ProtocolEventArgs : EventArgs
{
    public byte OptionCode { get; init; }
    public ReadOnlyMemory<byte> Data { get; init; }
}

public interface IProtocolEventBus
{
    event EventHandler<ProtocolEventArgs> ProtocolNegotiated;
    event EventHandler<ProtocolEventArgs> ProtocolDisabled;
    event EventHandler<ProtocolEventArgs> SubnegotiationReceived;
    
    void PublishNegotiated(byte optionCode);
    void PublishDisabled(byte optionCode);
    void PublishSubnegotiation(byte optionCode, ReadOnlyMemory<byte> data);
}
```

**Benefits**:
- Protocols can react to other protocol events
- Loose coupling between protocols
- Easy to add cross-protocol features (e.g., GMCP listening to MSDP)

### 5. **Options Pattern**

**Purpose**: Configuration management for protocols

**Implementation**:

```csharp
public class TelnetOptions
{
    public TelnetMode Mode { get; set; }
    public ProtocolConfiguration Protocols { get; set; } = new();
}

public class ProtocolConfiguration
{
    /// <summary>
    /// Protocols to enable by option code
    /// </summary>
    public HashSet<byte> EnabledProtocols { get; set; } = new();
    
    /// <summary>
    /// Protocols to explicitly disable (overrides defaults)
    /// </summary>
    public HashSet<byte> DisabledProtocols { get; set; } = new();
    
    /// <summary>
    /// Protocol-specific configuration
    /// </summary>
    public Dictionary<byte, object> ProtocolSettings { get; set; } = new();
}

// Usage with appsettings.json
{
  "Telnet": {
    "Mode": "Server",
    "Protocols": {
      "EnabledProtocols": [201, 69, 31],  // GMCP, MSDP, NAWS
      "DisabledProtocols": [86],          // Disable MCCP
      "ProtocolSettings": {
        "201": {  // GMCP
          "MaxPackageSize": 8192
        }
      }
    }
  }
}
```

**Benefits**:
- Standard .NET configuration pattern
- JSON/YAML configuration support
- Environment-specific configurations
- Validation via `IValidateOptions<T>`

### 6. **Decorator Pattern**

**Purpose**: Add cross-cutting concerns without modifying protocols

**Implementation**:

```csharp
/// <summary>
/// Decorator that logs all protocol operations
/// </summary>
public class LoggingProtocolDecorator : ITelnetProtocol
{
    private readonly ITelnetProtocol _innerProtocol;
    private readonly ILogger _logger;
    
    public LoggingProtocolDecorator(ITelnetProtocol innerProtocol, ILogger logger)
    {
        _innerProtocol = innerProtocol;
        _logger = logger;
    }
    
    public byte OptionCode => _innerProtocol.OptionCode;
    public string Name => _innerProtocol.Name;
    public IReadOnlyCollection<byte> Dependencies => _innerProtocol.Dependencies;
    
    public async ValueTask HandleSubnegotiationAsync(ReadOnlyMemory<byte> data)
    {
        _logger.LogDebug("Protocol {Name} handling {Bytes} bytes", Name, data.Length);
        await _innerProtocol.HandleSubnegotiationAsync(data);
    }
    
    // ... other delegating methods
}

/// <summary>
/// Decorator that measures protocol performance
/// </summary>
public class MetricsProtocolDecorator : ITelnetProtocol
{
    private readonly ITelnetProtocol _innerProtocol;
    private readonly IMetricsCollector _metrics;
    
    public async ValueTask HandleSubnegotiationAsync(ReadOnlyMemory<byte> data)
    {
        using var timer = _metrics.MeasureProtocol(_innerProtocol.Name);
        await _innerProtocol.HandleSubnegotiationAsync(data);
    }
    
    // ... other delegating methods
}
```

**Benefits**:
- Add logging, metrics, caching without changing protocols
- Composable behaviors
- Testable decorators

### 7. **Specification Pattern**

**Purpose**: Protocol capability queries and compatibility checks

**Implementation**:

```csharp
public interface IProtocolSpecification
{
    bool IsSatisfiedBy(ITelnetProtocol protocol);
}

public class RequiresProtocolSpecification : IProtocolSpecification
{
    private readonly byte _requiredOptionCode;
    
    public RequiresProtocolSpecification(byte optionCode)
    {
        _requiredOptionCode = optionCode;
    }
    
    public bool IsSatisfiedBy(ITelnetProtocol protocol)
    {
        return protocol.Dependencies.Contains(_requiredOptionCode);
    }
}

public class ProtocolCompatibilityChecker
{
    public bool AreCompatible(ITelnetProtocol protocol1, ITelnetProtocol protocol2)
    {
        // Check for mutual exclusivity, conflicts, etc.
        return !HasConflicts(protocol1, protocol2);
    }
}
```

**Benefits**:
- Query protocol capabilities
- Validate compatibility
- Business rules as objects

---

## Recommended NuGet Packages

### 1. **Microsoft.Extensions.DependencyInjection** (Already available via Logging)

**Purpose**: Dependency injection container

**Current Version**: 9.0.0 (already indirect dependency)

**Usage**:
```csharp
services.AddSingleton<IProtocolManager, ProtocolManager>();
services.AddSingleton<ITelnetProtocol, GMCPProtocol>();
```

**Benefits**:
- Industry standard
- .NET native
- Excellent performance
- Supports validation

### 2. **Microsoft.Extensions.Options** (New)

**Purpose**: Configuration and options management

**Recommended Version**: 9.0.0

**Usage**:
```csharp
services.Configure<TelnetOptions>(configuration.GetSection("Telnet"));
services.AddSingleton<IValidateOptions<TelnetOptions>, TelnetOptionsValidator>();
```

**Benefits**:
- Named options support
- Validation pipeline
- Hot reload support
- Change tracking

### 3. **Microsoft.Extensions.Options.DataAnnotations** (New)

**Purpose**: Declarative validation

**Recommended Version**: 9.0.0

**Usage**:
```csharp
public class GMCPProtocolOptions
{
    [Range(1024, 65536)]
    public int MaxPackageSize { get; set; } = 8192;
    
    [Required]
    public string[] SupportedPackages { get; set; } = Array.Empty<string>();
}
```

**Benefits**:
- Declarative validation
- Standard .NET attributes
- Clear constraints

### 4. **Microsoft.Extensions.Hosting** (Optional)

**Purpose**: Background services and lifecycle management

**Recommended Version**: 9.0.0

**Usage**:
```csharp
public class TelnetServerService : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Run telnet server
    }
}
```

**Benefits**:
- Graceful shutdown
- Lifecycle hooks
- Health checks
- Standard hosting model

### 5. **Scrutor** (New - Optional but highly recommended)

**Purpose**: Assembly scanning and decoration for DI

**Recommended Version**: 5.0.1

**Usage**:
```csharp
services.Scan(scan => scan
    .FromAssemblyOf<ITelnetProtocol>()
    .AddClasses(classes => classes.AssignableTo<ITelnetProtocol>())
    .AsImplementedInterfaces()
    .WithSingletonLifetime());

// Auto-decorate all protocols with logging
services.Decorate<ITelnetProtocol, LoggingProtocolDecorator>();
```

**Benefits**:
- Auto-discovery of protocols
- Convention-based registration
- Decorator pattern support
- Reduces boilerplate

### 6. **Polly** (New - Optional)

**Purpose**: Resilience and retry policies for network operations

**Recommended Version**: 8.5.0

**Usage**:
```csharp
var retryPolicy = Policy
    .Handle<IOException>()
    .WaitAndRetryAsync(3, retryAttempt => 
        TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)));

await retryPolicy.ExecuteAsync(async () => 
    await protocol.HandleSubnegotiationAsync(data));
```

**Benefits**:
- Retry logic
- Circuit breaker
- Timeout policies
- Network resilience

### 7. **System.Threading.Channels** (Already in .NET 9)

**Purpose**: High-performance async data pipelines

**Usage**:
```csharp
var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(100)
{
    FullMode = BoundedChannelFullMode.Wait
});

// Producer
await channel.Writer.WriteAsync(negotiationData);

// Consumer
await foreach (var data in channel.Reader.ReadAllAsync())
{
    await ProcessNegotiationAsync(data);
}
```

**Benefits**:
- Better than BlockingCollection
- Async/await support
- Backpressure handling
- Memory efficient

### 8. **BenchmarkDotNet** (New - Development/Testing)

**Purpose**: Performance benchmarking

**Recommended Version**: 0.14.0

**Usage**:
```csharp
[MemoryDiagnoser]
public class ProtocolBenchmarks
{
    [Benchmark]
    public async Task GMCPMessageParsing()
    {
        await _gmcpProtocol.HandleSubnegotiationAsync(_testData);
    }
}
```

**Benefits**:
- Accurate performance metrics
- Memory allocation tracking
- Regression detection

### 9. **FluentValidation** (New - Optional alternative to DataAnnotations)

**Purpose**: Complex validation scenarios

**Recommended Version**: 11.9.0

**Usage**:
```csharp
public class TelnetOptionsValidator : AbstractValidator<TelnetOptions>
{
    public TelnetOptionsValidator()
    {
        RuleFor(x => x.Protocols.EnabledProtocols)
            .Must(HaveNoDuplicates)
            .WithMessage("Duplicate protocols detected");
            
        RuleFor(x => x)
            .Must(HaveSatisfiedDependencies)
            .WithMessage("Protocol dependencies not satisfied");
    }
}
```

**Benefits**:
- Complex validation logic
- Fluent API
- Custom validators
- Better error messages

### 10. **Keep Existing: Stateless** (5.15.0)

**Purpose**: State machine (currently used)

**Recommendation**: **Keep but encapsulate per-protocol**

**Rationale**: Stateless is excellent, but currently one global state machine has 100+ states. Instead:

```csharp
public abstract class StateMachineProtocol : ITelnetProtocol
{
    protected StateMachine<ProtocolState, ProtocolTrigger> StateMachine { get; }
    
    protected StateMachineProtocol()
    {
        StateMachine = new StateMachine<ProtocolState, ProtocolTrigger>(
            ProtocolState.Uninitialized);
        ConfigureStateMachine();
    }
    
    protected abstract void ConfigureStateMachine();
}

public class GMCPProtocol : StateMachineProtocol
{
    protected override void ConfigureStateMachine()
    {
        StateMachine.Configure(ProtocolState.Negotiating)
            .Permit(ProtocolTrigger.Accepted, ProtocolState.Active)
            .Permit(ProtocolTrigger.Rejected, ProtocolState.Inactive);
    }
}
```

**Benefits**:
- Each protocol has its own state machine (5-10 states instead of 100+)
- Easier to reason about
- Better encapsulation
- Can still share common states via inheritance

---

## Implementation Strategy

### Phase 1: Abstraction Layer (Non-Breaking)

**Goal**: Introduce protocol interface without breaking existing code

**Steps**:

1. Create `ITelnetProtocol` interface
2. Create `IProtocolManager` interface
3. Add adapter pattern to wrap existing partial classes as protocols
4. Introduce builder alongside existing constructor

**Example**:

```csharp
// New protocol adapter wraps existing functionality
internal class GMCPProtocolAdapter : ITelnetProtocol
{
    private readonly TelnetInterpreter _interpreter;
    
    public byte OptionCode => (byte)Trigger.GMCP;
    public string Name => "GMCP";
    public IReadOnlyCollection<byte> Dependencies => Array.Empty<byte>();
    
    public GMCPProtocolAdapter(TelnetInterpreter interpreter)
    {
        _interpreter = interpreter;
    }
    
    public void Initialize(StateMachine<State, Trigger> stateMachine, 
                          TelnetMode mode, ILogger logger)
    {
        // Calls existing SetupGMCPNegotiation
        _interpreter.SetupGMCPNegotiation(stateMachine);
    }
    
    public ValueTask HandleSubnegotiationAsync(ReadOnlyMemory<byte> data)
    {
        // Delegates to existing _interpreter logic
        return _interpreter.ProcessGMCPDataAsync(data);
    }
}
```

**Benefits**:
- No breaking changes
- Can migrate incrementally
- Test new architecture alongside old

### Phase 2: Extract Core Protocols

**Goal**: Move protocols to standalone classes

**Steps**:

1. Extract GMCP → GMCPProtocol.cs (new file, not partial)
2. Extract MSDP → MSDPProtocol.cs
3. Extract NAWS → NAWSProtocol.cs
4. Update builder to use new protocols
5. Keep old partial classes for backward compatibility

**Example**:

```csharp
public class GMCPProtocol : ITelnetProtocol
{
    private readonly ILogger _logger;
    private readonly List<byte> _gmcpBuffer = new();
    
    public byte OptionCode => 201;
    public string Name => "GMCP";
    public IReadOnlyCollection<byte> Dependencies => new byte[] { 69 }; // MSDP
    
    public Func<(string Package, string Info), ValueTask>? OnReceived { get; set; }
    
    public GMCPProtocol(ILogger<GMCPProtocol> logger)
    {
        _logger = logger;
    }
    
    public void Initialize(StateMachine<State, Trigger> stateMachine, 
                          TelnetMode mode, ILogger logger)
    {
        if (mode == TelnetMode.Server)
        {
            stateMachine.Configure(State.Do)
                .Permit(Trigger.GMCP, State.DoGMCP);
        }
        // ... rest of setup
    }
    
    public async ValueTask HandleSubnegotiationAsync(ReadOnlyMemory<byte> data)
    {
        // Parse GMCP package
        var str = Encoding.UTF8.GetString(data.Span);
        var spaceIndex = str.IndexOf(' ');
        var package = str[..spaceIndex];
        var json = str[(spaceIndex + 1)..];
        
        if (OnReceived != null)
        {
            await OnReceived((package, json));
        }
    }
    
    public ValueTask OnNegotiationCompleteAsync()
    {
        _logger.LogInformation("GMCP protocol enabled");
        return ValueTask.CompletedTask;
    }
    
    public ValueTask OnDisabledAsync()
    {
        _logger.LogInformation("GMCP protocol disabled");
        _gmcpBuffer.Clear();
        return ValueTask.CompletedTask;
    }
    
    public byte[] GetInitialNegotiation()
    {
        return new byte[] 
        { 
            (byte)Trigger.IAC, 
            (byte)Trigger.WILL, 
            OptionCode 
        };
    }
}
```

### Phase 3: Protocol Manager

**Goal**: Centralized protocol lifecycle management

**Steps**:

1. Implement `IProtocolManager`
2. Add dependency resolution
3. Add enable/disable at runtime
4. Integrate with DI container

**Example**:

```csharp
public interface IProtocolManager
{
    void RegisterProtocol(ITelnetProtocol protocol);
    void EnableProtocol(byte optionCode);
    void DisableProtocol(byte optionCode);
    ITelnetProtocol? GetProtocol(byte optionCode);
    IReadOnlyList<ITelnetProtocol> GetAllProtocols();
    Task InitializeAllAsync();
}

public class ProtocolManager : IProtocolManager
{
    private readonly Dictionary<byte, ITelnetProtocol> _protocols = new();
    private readonly IProtocolDependencyResolver _dependencyResolver;
    private readonly ILogger<ProtocolManager> _logger;
    
    public ProtocolManager(
        IEnumerable<ITelnetProtocol> protocols,
        IProtocolDependencyResolver dependencyResolver,
        ILogger<ProtocolManager> logger)
    {
        _dependencyResolver = dependencyResolver;
        _logger = logger;
        
        foreach (var protocol in protocols)
        {
            RegisterProtocol(protocol);
        }
    }
    
    public void RegisterProtocol(ITelnetProtocol protocol)
    {
        if (_protocols.ContainsKey(protocol.OptionCode))
        {
            throw new InvalidOperationException(
                $"Protocol with option code {protocol.OptionCode} already registered");
        }
        
        _protocols[protocol.OptionCode] = protocol;
        _logger.LogDebug("Registered protocol: {Name} ({OptionCode})", 
            protocol.Name, protocol.OptionCode);
    }
    
    public void EnableProtocol(byte optionCode)
    {
        if (!_protocols.TryGetValue(optionCode, out var protocol))
        {
            throw new InvalidOperationException(
                $"Protocol with option code {optionCode} not found");
        }
        
        // Validate dependencies
        foreach (var depCode in protocol.Dependencies)
        {
            if (!_protocols.ContainsKey(depCode))
            {
                throw new InvalidOperationException(
                    $"Protocol '{protocol.Name}' depends on option code {depCode}, " +
                    $"which is not registered");
            }
            
            // Auto-enable dependencies
            EnableProtocol(depCode);
        }
        
        _logger.LogInformation("Enabled protocol: {Name}", protocol.Name);
    }
    
    public void DisableProtocol(byte optionCode)
    {
        if (!_protocols.TryGetValue(optionCode, out var protocol))
        {
            return; // Already disabled or never existed
        }
        
        // Check if any enabled protocol depends on this
        var dependents = _protocols.Values
            .Where(p => p.Dependencies.Contains(optionCode))
            .ToList();
            
        if (dependents.Any())
        {
            throw new InvalidOperationException(
                $"Cannot disable protocol '{protocol.Name}' because it is required by: " +
                string.Join(", ", dependents.Select(p => p.Name)));
        }
        
        protocol.OnDisabledAsync().GetAwaiter().GetResult();
        _logger.LogInformation("Disabled protocol: {Name}", protocol.Name);
    }
    
    public ITelnetProtocol? GetProtocol(byte optionCode)
    {
        return _protocols.GetValueOrDefault(optionCode);
    }
    
    public IReadOnlyList<ITelnetProtocol> GetAllProtocols()
    {
        return _protocols.Values.ToList();
    }
    
    public async Task InitializeAllAsync()
    {
        var validationResult = _dependencyResolver.ValidateDependencies(_protocols.Values);
        if (!validationResult.IsValid)
        {
            throw new InvalidOperationException(
                "Protocol dependency validation failed:\n" + 
                string.Join("\n", validationResult.Errors));
        }
        
        var ordered = _dependencyResolver.ResolveDependencies(_protocols.Values);
        
        foreach (var protocol in ordered)
        {
            await protocol.OnNegotiationCompleteAsync();
        }
    }
}
```

### Phase 4: Builder Modernization

**Goal**: Fluent, discoverable, type-safe configuration

**Example**:

```csharp
// Simple usage - auto-discover protocols
var telnet = await new TelnetInterpreterBuilder()
    .UseMode(TelnetMode.Server)
    .AddProtocolsFromAssembly() // Auto-discover all ITelnetProtocol
    .ConfigureLogging(logging => logging.AddConsole())
    .BuildAsync();

// Advanced usage - explicit protocols
var telnet = await new TelnetInterpreterBuilder()
    .UseMode(TelnetMode.Client)
    .AddProtocol<GMCPProtocol>(gmcp =>
    {
        gmcp.OnReceived = async (pkg, data) => 
        {
            Console.WriteLine($"GMCP: {pkg} = {data}");
        };
    })
    .AddProtocol<MSDPProtocol>()
    .AddProtocol<NAWSProtocol>(naws =>
    {
        naws.InitialWidth = 120;
        naws.InitialHeight = 40;
    })
    .ConfigureProtocols(protocols =>
    {
        protocols.DisabledProtocols.Add((byte)Trigger.MCCP);
    })
    .BuildAsync();

// Configuration-driven usage
var configuration = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json")
    .Build();

var telnet = await new TelnetInterpreterBuilder()
    .UseConfiguration(configuration)
    .BuildAsync();
```

### Phase 5: Event Bus Integration

**Goal**: Decouple protocol interactions

**Example**:

```csharp
public class GMCPProtocol : ITelnetProtocol
{
    private readonly IProtocolEventBus _eventBus;
    
    public GMCPProtocol(IProtocolEventBus eventBus)
    {
        _eventBus = eventBus;
        
        // Listen to MSDP events if needed
        _eventBus.SubnegotiationReceived += OnOtherProtocolData;
    }
    
    private async void OnOtherProtocolData(object? sender, ProtocolEventArgs e)
    {
        if (e.OptionCode == 69) // MSDP
        {
            // Handle MSDP data within GMCP context
            await HandleMSDPViaGMCPAsync(e.Data);
        }
    }
    
    public async ValueTask HandleSubnegotiationAsync(ReadOnlyMemory<byte> data)
    {
        // Process GMCP data
        // ...
        
        // Notify others
        _eventBus.PublishSubnegotiation(OptionCode, data);
    }
}
```

---

## Migration Path

### Backward Compatibility Strategy

**Goal**: Allow gradual migration without breaking existing consumers

### Option A: Dual API (Recommended)

Maintain both APIs during transition period:

```csharp
// Old API (deprecated but still works)
var telnet = new TelnetInterpreter(TelnetMode.Client, logger)
{
    CallbackOnSubmitAsync = WriteBackAsync,
    CallbackNegotiationAsync = WriteToOutputAsync,
    SignalOnGMCPAsync = HandleGMCPAsync
}.BuildAsync();

// New API
var telnet = await new TelnetInterpreterBuilder()
    .UseMode(TelnetMode.Client)
    .AddProtocol<GMCPProtocol>()
    .OnSubmit(WriteBackAsync)
    .OnNegotiation(WriteToOutputAsync)
    .BuildAsync();
```

### Option B: Adapter Pattern

Keep old `TelnetInterpreter` class but delegate to new architecture:

```csharp
public partial class TelnetInterpreter
{
    private IProtocolManager? _protocolManager;
    
    // Old constructor - creates protocols internally
    public TelnetInterpreter(TelnetMode mode, ILogger logger)
    {
        // Build new architecture internally
        var builder = new TelnetInterpreterBuilder()
            .UseMode(mode)
            .AddProtocolsFromAssembly();
            
        _protocolManager = builder.BuildProtocolManager();
    }
    
    // New constructor - accepts protocol manager
    internal TelnetInterpreter(IProtocolManager protocolManager)
    {
        _protocolManager = protocolManager;
    }
}
```

### Deprecation Timeline

1. **Version 2.0**: Introduce new APIs alongside old ones (both work)
2. **Version 2.1-2.5**: Mark old APIs with `[Obsolete]` warnings
3. **Version 3.0**: Remove old APIs (breaking change, major version bump)

### Documentation Strategy

1. Create **MIGRATION.md** guide with examples
2. Update **README.md** to show new API first
3. Add XML docs with `[Obsolete("Use TelnetInterpreterBuilder instead")]`
4. Provide code-fix analyzers to auto-migrate (optional)

---

## Benefits Summary

### Current Issues → Solutions

| Issue | Current State | Recommended Solution |
|-------|---------------|---------------------|
| **Hardcoded Dependencies** | GMCP checks `if (package == "MSDP")` | `Dependencies` property + DI |
| **Fixed Initialization** | Hardcoded list of 10 Setup methods | Builder with `AddProtocol<T>()` |
| **Cannot Disable Protocols** | All protocols always active | `EnableProtocol()` / `DisableProtocol()` |
| **Monolithic Class** | 100+ states in one state machine | Per-protocol state machines |
| **No Abstraction** | Partial classes tightly coupled | `ITelnetProtocol` interface |
| **Implicit Fallbacks** | EOR falls back to GA silently | Explicit dependency resolution |
| **Difficult Testing** | Must test full interpreter | Mock individual protocols |
| **No Configuration** | Code-only configuration | appsettings.json support |

### Performance Improvements

1. **Memory**: Only load protocols that are enabled (save ~10-50KB per unused protocol)
2. **State Machine**: 100+ global states → 5-10 states per protocol (faster lookups)
3. **Initialization**: Parallel protocol initialization (async/await)
4. **Channels**: Better backpressure handling for high-throughput scenarios

### Developer Experience Improvements

1. **Discoverability**: `builder.AddProtocol<TAB>` shows all available protocols
2. **Type Safety**: Compile-time errors for missing dependencies
3. **Testing**: Mock `ITelnetProtocol` instead of full interpreter
4. **Debugging**: Per-protocol logging and metrics
5. **Documentation**: Each protocol is self-documenting via interface

---

## Example: Modern Protocol Implementation

```csharp
/// <summary>
/// Implements GMCP (Generic Mud Communication Protocol)
/// https://tintin.mudhalla.net/protocols/gmcp/
/// </summary>
public sealed class GMCPProtocol : ITelnetProtocol, IDisposable
{
    private readonly ILogger<GMCPProtocol> _logger;
    private readonly IProtocolEventBus _eventBus;
    private readonly GMCPOptions _options;
    private readonly List<byte> _buffer = new();
    private bool _isEnabled;
    
    public byte OptionCode => 201;
    public string Name => "GMCP";
    
    // GMCP can optionally use MSDP for some messages
    public IReadOnlyCollection<byte> Dependencies => new byte[] { 69 }; // MSDP
    
    public event EventHandler<GMCPMessageEventArgs>? MessageReceived;
    
    public GMCPProtocol(
        ILogger<GMCPProtocol> logger,
        IProtocolEventBus eventBus,
        IOptions<GMCPOptions> options)
    {
        _logger = logger;
        _eventBus = eventBus;
        _options = options.Value;
    }
    
    public void Initialize(
        StateMachine<State, Trigger> stateMachine, 
        TelnetMode mode, 
        ILogger logger)
    {
        _logger.LogDebug("Initializing GMCP protocol in {Mode} mode", mode);
        
        // Configure state machine for this protocol
        if (mode == TelnetMode.Server)
        {
            ConfigureServerStates(stateMachine);
        }
        else
        {
            ConfigureClientStates(stateMachine);
        }
    }
    
    public async ValueTask HandleSubnegotiationAsync(ReadOnlyMemory<byte> data)
    {
        if (data.Length > _options.MaxPackageSize)
        {
            _logger.LogWarning(
                "GMCP package exceeds max size: {Size} > {MaxSize}", 
                data.Length, _options.MaxPackageSize);
            return;
        }
        
        var message = ParseGMCPMessage(data);
        
        _logger.LogTrace("GMCP received: {Package} = {Data}", 
            message.Package, message.Data);
        
        MessageReceived?.Invoke(this, new GMCPMessageEventArgs(message));
        
        // Publish to event bus for other protocols
        _eventBus.PublishSubnegotiation(OptionCode, data);
        
        await ValueTask.CompletedTask;
    }
    
    public ValueTask OnNegotiationCompleteAsync()
    {
        _isEnabled = true;
        _logger.LogInformation("GMCP protocol enabled");
        return ValueTask.CompletedTask;
    }
    
    public ValueTask OnDisabledAsync()
    {
        _isEnabled = false;
        _buffer.Clear();
        _logger.LogInformation("GMCP protocol disabled");
        return ValueTask.CompletedTask;
    }
    
    public byte[] GetInitialNegotiation()
    {
        return new byte[] 
        { 
            (byte)Trigger.IAC, 
            (byte)Trigger.WILL, 
            OptionCode 
        };
    }
    
    /// <summary>
    /// Send a GMCP message
    /// </summary>
    public async ValueTask SendAsync(string package, string jsonData)
    {
        if (!_isEnabled)
        {
            throw new InvalidOperationException("GMCP protocol is not enabled");
        }
        
        var message = $"{package} {jsonData}";
        var bytes = Encoding.UTF8.GetBytes(message);
        
        _logger.LogTrace("GMCP sending: {Package} = {Data}", package, jsonData);
        
        _eventBus.PublishSubnegotiation(OptionCode, bytes);
        
        await ValueTask.CompletedTask;
    }
    
    private GMCPMessage ParseGMCPMessage(ReadOnlyMemory<byte> data)
    {
        var str = Encoding.UTF8.GetString(data.Span);
        var spaceIndex = str.IndexOf(' ');
        
        if (spaceIndex == -1)
        {
            return new GMCPMessage(str, string.Empty);
        }
        
        var package = str[..spaceIndex];
        var json = str[(spaceIndex + 1)..];
        
        return new GMCPMessage(package, json);
    }
    
    private void ConfigureServerStates(StateMachine<State, Trigger> stateMachine)
    {
        // Server-specific state machine configuration
        stateMachine.Configure(State.Do)
            .Permit(Trigger.GMCP, State.DoGMCP);
            
        stateMachine.Configure(State.DoGMCP)
            .SubstateOf(State.Accepting)
            .OnEntryAsync(async () => await OnNegotiationCompleteAsync());
    }
    
    private void ConfigureClientStates(StateMachine<State, Trigger> stateMachine)
    {
        // Client-specific state machine configuration
        stateMachine.Configure(State.Willing)
            .Permit(Trigger.GMCP, State.WillGMCP);
            
        stateMachine.Configure(State.WillGMCP)
            .SubstateOf(State.Accepting)
            .OnEntryAsync(async () => await OnNegotiationCompleteAsync());
    }
    
    public void Dispose()
    {
        _buffer.Clear();
    }
}

public record GMCPMessage(string Package, string Data);

public class GMCPMessageEventArgs : EventArgs
{
    public GMCPMessage Message { get; }
    public GMCPMessageEventArgs(GMCPMessage message) => Message = message;
}

public class GMCPOptions
{
    [Range(1024, 65536)]
    public int MaxPackageSize { get; set; } = 8192;
    
    public string[] SupportedPackages { get; set; } = Array.Empty<string>();
}
```

---

## Conclusion

This architecture modernization will transform TelnetNegotiationCore from a monolithic partial-class-based library into a **modular, plugin-based architecture** that:

✅ **Supports Protocol Dependencies**: Explicit dependency declaration and resolution  
✅ **Enables Dynamic Configuration**: Runtime enable/disable of protocols  
✅ **Uses Modern C# Patterns**: DI, Options, Builder, Plugin, Observer  
✅ **Improves Testability**: Each protocol is independently testable  
✅ **Maintains Backward Compatibility**: Old API works during migration  
✅ **Leverages Standard NuGet Packages**: Microsoft.Extensions.* ecosystem  
✅ **Reduces Complexity**: Per-protocol state machines instead of monolithic  
✅ **Enhances Performance**: Load only needed protocols, better memory usage  

### Next Steps

1. **Review** this document with stakeholders
2. **Prototype** Phase 1 (abstraction layer) in a feature branch
3. **Benchmark** current vs. new architecture
4. **Document** migration guide
5. **Implement** incrementally over multiple releases

---

## Appendix: Protocol Dependency Graph

```
┌─────────────────────────────────────────────────────────────────┐
│                     Protocol Dependencies                        │
└─────────────────────────────────────────────────────────────────┘

SafeNegotiation (Base)
    │
    ├── StandardProtocol (Core Telnet RFC 855)
    │       │
    │       ├── TerminalType (TTYPE - RFC 1091)
    │       │
    │       ├── NAWS (RFC 1073)
    │       │
    │       ├── Charset (RFC 2066)
    │       │
    │       ├── EOR (RFC 885)
    │       │
    │       └── SuppressGA (RFC 858)
    │               │
    │               └── [Fallback for EOR prompting]
    │
    ├── MSDP (Mud Server Data Protocol)
    │       │
    │       └── GMCP (Generic Mud Communication Protocol)
    │               │
    │               └── [Can route MSDP messages via GMCP]
    │
    └── MSSP (Mud Server Status Protocol)

Legend:
────  Hard dependency (required)
- - -  Soft dependency (optional/fallback)
```

### Protocol Option Codes

| Protocol | Option Code | RFC/Spec |
|----------|------------|----------|
| GMCP | 201 | https://tintin.mudhalla.net/protocols/gmcp |
| MSDP | 69 | https://tintin.mudhalla.net/protocols/msdp |
| MSSP | 70 | https://tintin.mudhalla.net/protocols/mssp |
| EOR | 25 | RFC 885 |
| SuppressGA | 3 | RFC 858 |
| NAWS | 31 | RFC 1073 |
| TerminalType | 24 | RFC 1091 |
| Charset | 42 | RFC 2066 |

---

**Document Version**: 1.0  
**Date**: January 18, 2026  
**Author**: GitHub Copilot  
**Status**: Recommendation (Not Implemented)  
