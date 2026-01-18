# Architecture Code Examples

This document provides detailed code examples demonstrating the recommended architectural patterns from `ARCHITECTURE_RECOMMENDATIONS.md`.

**Note**: These are example implementations only. No actual code has been implemented.

---

## Table of Contents

1. [Protocol Interface Example](#protocol-interface-example)
2. [Protocol Manager Example](#protocol-manager-example)
3. [Dependency Resolver Example](#dependency-resolver-example)
4. [Builder Pattern Example](#builder-pattern-example)
5. [Configuration Example](#configuration-example)
6. [Testing Examples](#testing-examples)
7. [Event Bus Example](#event-bus-example)
8. [Complete Usage Examples](#complete-usage-examples)

---

## Protocol Interface Example

### ITelnetProtocol Interface

```csharp
namespace TelnetNegotiationCore.Protocols;

/// <summary>
/// Represents a telnet protocol option (RFC 855 compliant).
/// Each protocol implementation must specify its option code, dependencies,
/// and lifecycle methods.
/// </summary>
public interface ITelnetProtocol
{
    /// <summary>
    /// The telnet option code for this protocol (e.g., GMCP = 201, MSDP = 69).
    /// Must be unique within the interpreter instance.
    /// </summary>
    byte OptionCode { get; }
    
    /// <summary>
    /// Human-readable name for logging and debugging.
    /// </summary>
    string Name { get; }
    
    /// <summary>
    /// Option codes of protocols this protocol depends on.
    /// Dependencies are automatically enabled before this protocol.
    /// </summary>
    IReadOnlyCollection<byte> Dependencies { get; }
    
    /// <summary>
    /// Indicates whether this protocol is currently enabled.
    /// </summary>
    bool IsEnabled { get; }
    
    /// <summary>
    /// Initialize the protocol's state machine configuration.
    /// Called once during interpreter setup, before any negotiation.
    /// </summary>
    /// <param name="stateMachine">The shared state machine to configure</param>
    /// <param name="mode">Server or Client mode</param>
    /// <param name="logger">Logger for this protocol</param>
    void Initialize(StateMachine<State, Trigger> stateMachine, TelnetMode mode, ILogger logger);
    
    /// <summary>
    /// Handle incoming subnegotiation data for this protocol.
    /// Called when IAC SB [OptionCode] ... IAC SE is received.
    /// </summary>
    /// <param name="data">The subnegotiation data (without IAC SB/SE framing)</param>
    ValueTask HandleSubnegotiationAsync(ReadOnlyMemory<byte> data);
    
    /// <summary>
    /// Called when negotiation completes successfully (WILL/DO agreed).
    /// Protocol should transition to active state.
    /// </summary>
    ValueTask OnNegotiationCompleteAsync();
    
    /// <summary>
    /// Called when protocol is disabled (WONT/DONT received or sent).
    /// Protocol should clean up resources and reset state.
    /// </summary>
    ValueTask OnDisabledAsync();
    
    /// <summary>
    /// Get the initial negotiation bytes to send during handshake.
    /// Typically IAC WILL [OptionCode] or IAC DO [OptionCode].
    /// </summary>
    /// <returns>Byte array to send, or empty array if no initial negotiation</returns>
    byte[] GetInitialNegotiation();
}
```

### Base Protocol Implementation

```csharp
namespace TelnetNegotiationCore.Protocols;

/// <summary>
/// Base class providing common protocol implementation.
/// Inherit from this to create new protocols with less boilerplate.
/// </summary>
public abstract class TelnetProtocolBase : ITelnetProtocol
{
    protected ILogger Logger { get; }
    protected TelnetMode Mode { get; private set; }
    protected StateMachine<State, Trigger>? StateMachine { get; private set; }
    
    public abstract byte OptionCode { get; }
    public abstract string Name { get; }
    public virtual IReadOnlyCollection<byte> Dependencies => Array.Empty<byte>();
    public bool IsEnabled { get; private set; }
    
    protected TelnetProtocolBase(ILogger logger)
    {
        Logger = logger;
    }
    
    public virtual void Initialize(
        StateMachine<State, Trigger> stateMachine, 
        TelnetMode mode, 
        ILogger logger)
    {
        StateMachine = stateMachine;
        Mode = mode;
        
        Logger.LogDebug("Initializing {Protocol} in {Mode} mode", Name, mode);
        
        ConfigureStateMachine(stateMachine, mode);
    }
    
    /// <summary>
    /// Override to configure protocol-specific state machine transitions.
    /// </summary>
    protected abstract void ConfigureStateMachine(
        StateMachine<State, Trigger> stateMachine, 
        TelnetMode mode);
    
    public abstract ValueTask HandleSubnegotiationAsync(ReadOnlyMemory<byte> data);
    
    public virtual ValueTask OnNegotiationCompleteAsync()
    {
        IsEnabled = true;
        Logger.LogInformation("{Protocol} enabled", Name);
        return ValueTask.CompletedTask;
    }
    
    public virtual ValueTask OnDisabledAsync()
    {
        IsEnabled = false;
        Logger.LogInformation("{Protocol} disabled", Name);
        return ValueTask.CompletedTask;
    }
    
    public virtual byte[] GetInitialNegotiation()
    {
        // Default: send WILL [OptionCode]
        return new byte[] 
        { 
            (byte)Trigger.IAC, 
            (byte)Trigger.WILL, 
            OptionCode 
        };
    }
}
```

---

## Protocol Manager Example

### IProtocolManager Interface

```csharp
namespace TelnetNegotiationCore.Management;

/// <summary>
/// Manages the lifecycle and dependencies of telnet protocols.
/// </summary>
public interface IProtocolManager
{
    /// <summary>
    /// Register a protocol for use. Must be called before initialization.
    /// </summary>
    void RegisterProtocol(ITelnetProtocol protocol);
    
    /// <summary>
    /// Enable a protocol and its dependencies.
    /// </summary>
    /// <param name="optionCode">The protocol option code</param>
    /// <exception cref="InvalidOperationException">If dependencies are not satisfied</exception>
    void EnableProtocol(byte optionCode);
    
    /// <summary>
    /// Disable a protocol if no other protocols depend on it.
    /// </summary>
    /// <param name="optionCode">The protocol option code</param>
    /// <exception cref="InvalidOperationException">If other protocols depend on this</exception>
    void DisableProtocol(byte optionCode);
    
    /// <summary>
    /// Get a protocol by its option code.
    /// </summary>
    ITelnetProtocol? GetProtocol(byte optionCode);
    
    /// <summary>
    /// Get all registered protocols.
    /// </summary>
    IReadOnlyList<ITelnetProtocol> GetAllProtocols();
    
    /// <summary>
    /// Get all enabled protocols.
    /// </summary>
    IReadOnlyList<ITelnetProtocol> GetEnabledProtocols();
    
    /// <summary>
    /// Initialize all registered protocols in dependency order.
    /// </summary>
    Task InitializeAllAsync();
    
    /// <summary>
    /// Validate that all protocol dependencies are satisfied.
    /// </summary>
    ValidationResult ValidateDependencies();
}
```

### ProtocolManager Implementation

```csharp
namespace TelnetNegotiationCore.Management;

public class ProtocolManager : IProtocolManager
{
    private readonly Dictionary<byte, ITelnetProtocol> _protocols = new();
    private readonly HashSet<byte> _enabledProtocols = new();
    private readonly IProtocolDependencyResolver _dependencyResolver;
    private readonly ILogger<ProtocolManager> _logger;
    private bool _isInitialized;
    
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
        if (_isInitialized)
        {
            throw new InvalidOperationException(
                "Cannot register protocols after initialization");
        }
        
        if (_protocols.ContainsKey(protocol.OptionCode))
        {
            throw new InvalidOperationException(
                $"Protocol with option code {protocol.OptionCode} already registered. " +
                $"Existing: {_protocols[protocol.OptionCode].Name}, " +
                $"New: {protocol.Name}");
        }
        
        _protocols[protocol.OptionCode] = protocol;
        _logger.LogDebug("Registered protocol: {Name} (option code {OptionCode})", 
            protocol.Name, protocol.OptionCode);
    }
    
    public void EnableProtocol(byte optionCode)
    {
        if (!_protocols.TryGetValue(optionCode, out var protocol))
        {
            throw new InvalidOperationException(
                $"Protocol with option code {optionCode} not found. " +
                $"Available protocols: {string.Join(", ", _protocols.Keys)}");
        }
        
        if (_enabledProtocols.Contains(optionCode))
        {
            _logger.LogDebug("Protocol {Name} already enabled", protocol.Name);
            return;
        }
        
        // Recursively enable dependencies first
        foreach (var depCode in protocol.Dependencies)
        {
            if (!_protocols.ContainsKey(depCode))
            {
                throw new InvalidOperationException(
                    $"Protocol '{protocol.Name}' depends on option code {depCode}, " +
                    $"which is not registered. Please register the dependency first.");
            }
            
            EnableProtocol(depCode);
        }
        
        _enabledProtocols.Add(optionCode);
        _logger.LogInformation("Enabled protocol: {Name}", protocol.Name);
    }
    
    public void DisableProtocol(byte optionCode)
    {
        if (!_protocols.TryGetValue(optionCode, out var protocol))
        {
            _logger.LogWarning("Attempted to disable non-existent protocol {OptionCode}", 
                optionCode);
            return;
        }
        
        if (!_enabledProtocols.Contains(optionCode))
        {
            _logger.LogDebug("Protocol {Name} already disabled", protocol.Name);
            return;
        }
        
        // Check if any enabled protocol depends on this
        var dependents = _protocols.Values
            .Where(p => _enabledProtocols.Contains(p.OptionCode))
            .Where(p => p.Dependencies.Contains(optionCode))
            .ToList();
            
        if (dependents.Any())
        {
            throw new InvalidOperationException(
                $"Cannot disable protocol '{protocol.Name}' because it is required by: " +
                string.Join(", ", dependents.Select(p => p.Name)) + ". " +
                "Disable dependent protocols first.");
        }
        
        _enabledProtocols.Remove(optionCode);
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
    
    public IReadOnlyList<ITelnetProtocol> GetEnabledProtocols()
    {
        return _protocols.Values
            .Where(p => _enabledProtocols.Contains(p.OptionCode))
            .ToList();
    }
    
    public async Task InitializeAllAsync()
    {
        var validationResult = ValidateDependencies();
        if (!validationResult.IsValid)
        {
            throw new InvalidOperationException(
                "Protocol dependency validation failed:\n" + 
                string.Join("\n", validationResult.Errors));
        }
        
        var ordered = _dependencyResolver.ResolveDependencies(_protocols.Values);
        
        _logger.LogInformation(
            "Initializing {Count} protocols in dependency order: {Protocols}",
            ordered.Count,
            string.Join(" → ", ordered.Select(p => p.Name)));
        
        foreach (var protocol in ordered)
        {
            await protocol.OnNegotiationCompleteAsync();
        }
        
        _isInitialized = true;
    }
    
    public ValidationResult ValidateDependencies()
    {
        return _dependencyResolver.ValidateDependencies(_protocols.Values);
    }
}
```

---

## Dependency Resolver Example

### IProtocolDependencyResolver Interface

```csharp
namespace TelnetNegotiationCore.Management;

public interface IProtocolDependencyResolver
{
    /// <summary>
    /// Resolve protocol dependencies and return protocols in initialization order.
    /// Uses topological sorting to ensure dependencies are initialized first.
    /// </summary>
    /// <exception cref="InvalidOperationException">If circular dependencies detected</exception>
    IReadOnlyList<ITelnetProtocol> ResolveDependencies(
        IEnumerable<ITelnetProtocol> protocols);
    
    /// <summary>
    /// Validate that all protocol dependencies are satisfied.
    /// </summary>
    ValidationResult ValidateDependencies(IEnumerable<ITelnetProtocol> protocols);
}

public record ValidationResult(bool IsValid, IReadOnlyList<string> Errors);
```

### ProtocolDependencyResolver Implementation

```csharp
namespace TelnetNegotiationCore.Management;

public class ProtocolDependencyResolver : IProtocolDependencyResolver
{
    private readonly ILogger<ProtocolDependencyResolver> _logger;
    
    public ProtocolDependencyResolver(ILogger<ProtocolDependencyResolver> logger)
    {
        _logger = logger;
    }
    
    public IReadOnlyList<ITelnetProtocol> ResolveDependencies(
        IEnumerable<ITelnetProtocol> protocols)
    {
        var protocolList = protocols.ToList();
        var protocolsByCode = protocolList.ToDictionary(p => p.OptionCode);
        
        // Build adjacency list for dependency graph
        var graph = new Dictionary<byte, List<byte>>();
        var inDegree = new Dictionary<byte, int>();
        
        foreach (var protocol in protocolList)
        {
            if (!graph.ContainsKey(protocol.OptionCode))
            {
                graph[protocol.OptionCode] = new List<byte>();
                inDegree[protocol.OptionCode] = 0;
            }
            
            foreach (var depCode in protocol.Dependencies)
            {
                if (!protocolsByCode.ContainsKey(depCode))
                {
                    throw new InvalidOperationException(
                        $"Protocol '{protocol.Name}' depends on option code {depCode}, " +
                        $"but no such protocol is registered.");
                }
                
                // Add edge: dependency → protocol
                if (!graph.ContainsKey(depCode))
                {
                    graph[depCode] = new List<byte>();
                    inDegree[depCode] = 0;
                }
                
                graph[depCode].Add(protocol.OptionCode);
                inDegree[protocol.OptionCode]++;
            }
        }
        
        // Topological sort using Kahn's algorithm
        var queue = new Queue<byte>();
        var result = new List<ITelnetProtocol>();
        
        // Start with protocols that have no dependencies
        foreach (var kvp in inDegree.Where(x => x.Value == 0))
        {
            queue.Enqueue(kvp.Key);
        }
        
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            result.Add(protocolsByCode[current]);
            
            // Reduce in-degree for dependent protocols
            foreach (var dependent in graph[current])
            {
                inDegree[dependent]--;
                if (inDegree[dependent] == 0)
                {
                    queue.Enqueue(dependent);
                }
            }
        }
        
        // Check for circular dependencies
        if (result.Count != protocolList.Count)
        {
            var remaining = protocolList
                .Where(p => !result.Contains(p))
                .Select(p => p.Name);
                
            throw new InvalidOperationException(
                $"Circular dependency detected among protocols: " +
                string.Join(", ", remaining));
        }
        
        _logger.LogDebug(
            "Resolved protocol initialization order: {Order}",
            string.Join(" → ", result.Select(p => p.Name)));
        
        return result;
    }
    
    public ValidationResult ValidateDependencies(
        IEnumerable<ITelnetProtocol> protocols)
    {
        var protocolList = protocols.ToList();
        var protocolsByCode = protocolList.ToDictionary(p => p.OptionCode);
        var errors = new List<string>();
        
        // Check for missing dependencies
        foreach (var protocol in protocolList)
        {
            foreach (var depCode in protocol.Dependencies)
            {
                if (!protocolsByCode.ContainsKey(depCode))
                {
                    errors.Add(
                        $"Protocol '{protocol.Name}' (option {protocol.OptionCode}) " +
                        $"depends on option code {depCode}, which is not registered.");
                }
            }
        }
        
        // Check for duplicate option codes (should be caught earlier, but double-check)
        var duplicates = protocolList
            .GroupBy(p => p.OptionCode)
            .Where(g => g.Count() > 1)
            .ToList();
            
        foreach (var dup in duplicates)
        {
            errors.Add(
                $"Duplicate option code {dup.Key} used by: " +
                string.Join(", ", dup.Select(p => p.Name)));
        }
        
        // Check for circular dependencies
        try
        {
            ResolveDependencies(protocolList);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Circular"))
        {
            errors.Add(ex.Message);
        }
        
        return new ValidationResult(errors.Count == 0, errors);
    }
}
```

---

## Builder Pattern Example

### TelnetInterpreterBuilder

```csharp
namespace TelnetNegotiationCore.Builders;

public class TelnetInterpreterBuilder
{
    private readonly IServiceCollection _services;
    private readonly List<Type> _protocolTypes = new();
    private TelnetMode _mode = TelnetMode.Client;
    private IConfiguration? _configuration;
    
    public TelnetInterpreterBuilder()
    {
        _services = new ServiceCollection();
        
        // Register core services
        _services.AddSingleton<IProtocolDependencyResolver, ProtocolDependencyResolver>();
        _services.AddSingleton<IProtocolManager, ProtocolManager>();
        _services.AddSingleton<IProtocolEventBus, ProtocolEventBus>();
        _services.AddLogging();
    }
    
    /// <summary>
    /// Set the telnet mode (Client or Server).
    /// </summary>
    public TelnetInterpreterBuilder UseMode(TelnetMode mode)
    {
        _mode = mode;
        return this;
    }
    
    /// <summary>
    /// Load configuration from IConfiguration (e.g., appsettings.json).
    /// </summary>
    public TelnetInterpreterBuilder UseConfiguration(IConfiguration configuration)
    {
        _configuration = configuration;
        _services.AddSingleton(configuration);
        
        // Bind TelnetOptions from configuration
        _services.Configure<TelnetOptions>(
            configuration.GetSection("Telnet"));
        
        return this;
    }
    
    /// <summary>
    /// Add a specific protocol to the interpreter.
    /// </summary>
    public TelnetInterpreterBuilder AddProtocol<TProtocol>() 
        where TProtocol : class, ITelnetProtocol
    {
        _services.AddSingleton<ITelnetProtocol, TProtocol>();
        _protocolTypes.Add(typeof(TProtocol));
        return this;
    }
    
    /// <summary>
    /// Add a protocol with configuration action.
    /// </summary>
    public TelnetInterpreterBuilder AddProtocol<TProtocol>(Action<TProtocol> configure)
        where TProtocol : class, ITelnetProtocol
    {
        _services.AddSingleton<ITelnetProtocol, TProtocol>(provider =>
        {
            var protocol = ActivatorUtilities.CreateInstance<TProtocol>(provider);
            configure(protocol);
            return protocol;
        });
        _protocolTypes.Add(typeof(TProtocol));
        return this;
    }
    
    /// <summary>
    /// Auto-discover and register all protocols from the calling assembly.
    /// </summary>
    public TelnetInterpreterBuilder AddProtocolsFromAssembly(Assembly? assembly = null)
    {
        assembly ??= Assembly.GetCallingAssembly();
        
        _services.Scan(scan => scan
            .FromAssemblies(assembly)
            .AddClasses(classes => classes.AssignableTo<ITelnetProtocol>())
            .As<ITelnetProtocol>()
            .WithSingletonLifetime());
        
        return this;
    }
    
    /// <summary>
    /// Configure protocol-specific options.
    /// </summary>
    public TelnetInterpreterBuilder ConfigureProtocol<TProtocol, TOptions>(
        Action<TOptions> configure)
        where TProtocol : class, ITelnetProtocol
        where TOptions : class
    {
        _services.Configure<TOptions>(configure);
        return this;
    }
    
    /// <summary>
    /// Configure global protocol settings.
    /// </summary>
    public TelnetInterpreterBuilder ConfigureProtocols(
        Action<ProtocolConfiguration> configure)
    {
        _services.Configure<TelnetOptions>(options =>
        {
            configure(options.Protocols);
        });
        return this;
    }
    
    /// <summary>
    /// Configure logging.
    /// </summary>
    public TelnetInterpreterBuilder ConfigureLogging(
        Action<ILoggingBuilder> configure)
    {
        _services.AddLogging(configure);
        return this;
    }
    
    /// <summary>
    /// Add decorators to all protocols (e.g., logging, metrics).
    /// </summary>
    public TelnetInterpreterBuilder DecorateProtocols<TDecorator>()
        where TDecorator : class, ITelnetProtocol
    {
        _services.Decorate<ITelnetProtocol, TDecorator>();
        return this;
    }
    
    /// <summary>
    /// Build the TelnetInterpreter with all configured protocols.
    /// </summary>
    public async ValueTask<TelnetInterpreter> BuildAsync()
    {
        // Validate configuration if using Options pattern
        if (_configuration != null)
        {
            _services.AddSingleton<IValidateOptions<TelnetOptions>, 
                TelnetOptionsValidator>();
        }
        
        var provider = _services.BuildServiceProvider();
        
        // Validate options
        if (_configuration != null)
        {
            var options = provider.GetRequiredService<IOptions<TelnetOptions>>();
            _ = options.Value; // Force validation
        }
        
        var protocolManager = provider.GetRequiredService<IProtocolManager>();
        
        // Validate dependencies before building
        var validation = protocolManager.ValidateDependencies();
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(
                "Protocol dependency validation failed:\n" +
                string.Join("\n", validation.Errors));
        }
        
        // Initialize protocols
        await protocolManager.InitializeAllAsync();
        
        // Create interpreter
        var interpreter = new TelnetInterpreter(
            _mode,
            protocolManager,
            provider.GetRequiredService<ILogger<TelnetInterpreter>>());
        
        return interpreter;
    }
}
```

---

## Configuration Example

### appsettings.json

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "TelnetNegotiationCore": "Debug"
    }
  },
  "Telnet": {
    "Mode": "Server",
    "Protocols": {
      "EnabledProtocols": [201, 69, 31, 24],
      "DisabledProtocols": [86],
      "ProtocolSettings": {
        "201": {
          "MaxPackageSize": 8192,
          "SupportedPackages": ["Core.*", "Char.*", "Room.*"]
        },
        "31": {
          "InitialWidth": 120,
          "InitialHeight": 40
        }
      }
    },
    "CharsetOrder": ["utf-8", "iso-8859-1"],
    "ConnectionTimeout": "00:05:00"
  }
}
```

### TelnetOptions Class

```csharp
namespace TelnetNegotiationCore.Configuration;

public class TelnetOptions
{
    public const string SectionName = "Telnet";
    
    [Required]
    public TelnetMode Mode { get; set; } = TelnetMode.Client;
    
    public ProtocolConfiguration Protocols { get; set; } = new();
    
    public string[] CharsetOrder { get; set; } = new[] { "utf-8", "iso-8859-1" };
    
    public TimeSpan ConnectionTimeout { get; set; } = TimeSpan.FromMinutes(5);
}

public class ProtocolConfiguration
{
    /// <summary>
    /// Protocol option codes to enable
    /// </summary>
    public HashSet<byte> EnabledProtocols { get; set; } = new();
    
    /// <summary>
    /// Protocol option codes to explicitly disable
    /// </summary>
    public HashSet<byte> DisabledProtocols { get; set; } = new();
    
    /// <summary>
    /// Protocol-specific settings keyed by option code
    /// </summary>
    public Dictionary<byte, JsonElement> ProtocolSettings { get; set; } = new();
    
    public T? GetProtocolSettings<T>(byte optionCode) where T : class
    {
        if (!ProtocolSettings.TryGetValue(optionCode, out var json))
        {
            return null;
        }
        
        return JsonSerializer.Deserialize<T>(json.GetRawText());
    }
}

public class TelnetOptionsValidator : IValidateOptions<TelnetOptions>
{
    public ValidateOptionsResult Validate(string? name, TelnetOptions options)
    {
        var errors = new List<string>();
        
        if (options.Mode == TelnetMode.Error)
        {
            errors.Add("Invalid TelnetMode specified");
        }
        
        if (options.ConnectionTimeout <= TimeSpan.Zero)
        {
            errors.Add("ConnectionTimeout must be positive");
        }
        
        // Check for conflicts
        var conflicts = options.Protocols.EnabledProtocols
            .Intersect(options.Protocols.DisabledProtocols)
            .ToList();
            
        if (conflicts.Any())
        {
            errors.Add(
                $"Protocols cannot be both enabled and disabled: " +
                string.Join(", ", conflicts));
        }
        
        return errors.Any()
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}
```

---

## Testing Examples

### Unit Testing Individual Protocols

```csharp
namespace TelnetNegotiationCore.Tests.Protocols;

public class GMCPProtocolTests
{
    private readonly Mock<ILogger<GMCPProtocol>> _loggerMock;
    private readonly Mock<IProtocolEventBus> _eventBusMock;
    private readonly GMCPProtocol _protocol;
    
    public GMCPProtocolTests()
    {
        _loggerMock = new Mock<ILogger<GMCPProtocol>>();
        _eventBusMock = new Mock<IProtocolEventBus>();
        
        var options = Options.Create(new GMCPOptions
        {
            MaxPackageSize = 8192
        });
        
        _protocol = new GMCPProtocol(
            _loggerMock.Object,
            _eventBusMock.Object,
            options);
    }
    
    [Fact]
    public void OptionCode_ShouldBe201()
    {
        // Arrange & Act
        var optionCode = _protocol.OptionCode;
        
        // Assert
        Assert.Equal(201, optionCode);
    }
    
    [Fact]
    public void Dependencies_ShouldIncludeMSDP()
    {
        // Arrange & Act
        var dependencies = _protocol.Dependencies;
        
        // Assert
        Assert.Contains((byte)69, dependencies); // MSDP
    }
    
    [Fact]
    public async Task HandleSubnegotiation_ValidMessage_ShouldRaiseEvent()
    {
        // Arrange
        GMCPMessageEventArgs? receivedArgs = null;
        _protocol.MessageReceived += (sender, args) => receivedArgs = args;
        
        var message = "Core.Hello {\"client\":\"Test\",\"version\":\"1.0\"}";
        var data = Encoding.UTF8.GetBytes(message);
        
        // Act
        await _protocol.HandleSubnegotiationAsync(data);
        
        // Assert
        Assert.NotNull(receivedArgs);
        Assert.Equal("Core.Hello", receivedArgs.Message.Package);
        Assert.Contains("Test", receivedArgs.Message.Data);
    }
    
    [Fact]
    public async Task HandleSubnegotiation_TooLarge_ShouldLogWarning()
    {
        // Arrange
        var largeData = new byte[10000]; // Exceeds 8192 limit
        
        // Act
        await _protocol.HandleSubnegotiation Async(largeData);
        
        // Assert
        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("exceeds max size")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }
    
    [Fact]
    public async Task OnNegotiationComplete_ShouldEnableProtocol()
    {
        // Arrange
        Assert.False(_protocol.IsEnabled);
        
        // Act
        await _protocol.OnNegotiationCompleteAsync();
        
        // Assert
        Assert.True(_protocol.IsEnabled);
    }
}
```

### Integration Testing with Multiple Protocols

```csharp
namespace TelnetNegotiationCore.Tests.Integration;

public class ProtocolDependencyTests
{
    [Fact]
    public async Task Builder_WithGMCP_ShouldAutoEnableMSDP()
    {
        // Arrange & Act
        var interpreter = await new TelnetInterpreterBuilder()
            .UseMode(TelnetMode.Server)
            .AddProtocol<GMCPProtocol>()
            .AddProtocol<MSDPProtocol>()
            .BuildAsync();
        
        var protocolManager = interpreter.ProtocolManager;
        
        // Assert
        var gmcp = protocolManager.GetProtocol(201); // GMCP
        var msdp = protocolManager.GetProtocol(69);  // MSDP
        
        Assert.NotNull(gmcp);
        Assert.NotNull(msdp);
        Assert.True(gmcp.IsEnabled);
        Assert.True(msdp.IsEnabled); // Auto-enabled due to dependency
    }
    
    [Fact]
    public void ProtocolManager_DisableMSDP_WithGMCPEnabled_ShouldThrow()
    {
        // Arrange
        var protocolManager = CreateProtocolManager();
        protocolManager.EnableProtocol(201); // GMCP (depends on MSDP)
        
        // Act & Assert
        var ex = Assert.Throws<InvalidOperationException>(
            () => protocolManager.DisableProtocol(69)); // MSDP
        
        Assert.Contains("required by", ex.Message);
        Assert.Contains("GMCP", ex.Message);
    }
    
    [Fact]
    public void DependencyResolver_CircularDependency_ShouldThrow()
    {
        // Arrange
        var protocol1 = new Mock<ITelnetProtocol>();
        protocol1.Setup(p => p.OptionCode).Returns(1);
        protocol1.Setup(p => p.Dependencies).Returns(new byte[] { 2 });
        
        var protocol2 = new Mock<ITelnetProtocol>();
        protocol2.Setup(p => p.OptionCode).Returns(2);
        protocol2.Setup(p => p.Dependencies).Returns(new byte[] { 1 });
        
        var resolver = new ProtocolDependencyResolver(
            Mock.Of<ILogger<ProtocolDependencyResolver>>());
        
        // Act & Assert
        var ex = Assert.Throws<InvalidOperationException>(
            () => resolver.ResolveDependencies(new[] { protocol1.Object, protocol2.Object }));
        
        Assert.Contains("Circular dependency", ex.Message);
    }
}
```

---

## Event Bus Example

### IProtocolEventBus Interface

```csharp
namespace TelnetNegotiationCore.Events;

public interface IProtocolEventBus
{
    event EventHandler<ProtocolEventArgs>? ProtocolNegotiated;
    event EventHandler<ProtocolEventArgs>? ProtocolDisabled;
    event EventHandler<ProtocolEventArgs>? SubnegotiationReceived;
    
    void PublishNegotiated(byte optionCode);
    void PublishDisabled(byte optionCode);
    void PublishSubnegotiation(byte optionCode, ReadOnlyMemory<byte> data);
}

public class ProtocolEventArgs : EventArgs
{
    public byte OptionCode { get; init; }
    public ReadOnlyMemory<byte> Data { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}
```

### ProtocolEventBus Implementation

```csharp
namespace TelnetNegotiationCore.Events;

public class ProtocolEventBus : IProtocolEventBus
{
    private readonly ILogger<ProtocolEventBus> _logger;
    
    public event EventHandler<ProtocolEventArgs>? ProtocolNegotiated;
    public event EventHandler<ProtocolEventArgs>? ProtocolDisabled;
    public event EventHandler<ProtocolEventArgs>? SubnegotiationReceived;
    
    public ProtocolEventBus(ILogger<ProtocolEventBus> logger)
    {
        _logger = logger;
    }
    
    public void PublishNegotiated(byte optionCode)
    {
        _logger.LogDebug("Publishing ProtocolNegotiated event for option {OptionCode}", 
            optionCode);
            
        ProtocolNegotiated?.Invoke(this, new ProtocolEventArgs
        {
            OptionCode = optionCode
        });
    }
    
    public void PublishDisabled(byte optionCode)
    {
        _logger.LogDebug("Publishing ProtocolDisabled event for option {OptionCode}", 
            optionCode);
            
        ProtocolDisabled?.Invoke(this, new ProtocolEventArgs
        {
            OptionCode = optionCode
        });
    }
    
    public void PublishSubnegotiation(byte optionCode, ReadOnlyMemory<byte> data)
    {
        _logger.LogTrace(
            "Publishing SubnegotiationReceived event for option {OptionCode} ({Size} bytes)", 
            optionCode, data.Length);
            
        SubnegotiationReceived?.Invoke(this, new ProtocolEventArgs
        {
            OptionCode = optionCode,
            Data = data
        });
    }
}
```

### Using Events in Protocols

```csharp
public class GMCPProtocol : TelnetProtocolBase
{
    private readonly IProtocolEventBus _eventBus;
    
    public GMCPProtocol(
        ILogger<GMCPProtocol> logger,
        IProtocolEventBus eventBus) : base(logger)
    {
        _eventBus = eventBus;
        
        // Listen to MSDP events for routing MSDP via GMCP
        _eventBus.SubnegotiationReceived += OnOtherProtocolSubnegotiation;
    }
    
    private async void OnOtherProtocolSubnegotiation(
        object? sender, 
        ProtocolEventArgs e)
    {
        if (e.OptionCode == 69) // MSDP
        {
            // Route MSDP messages through GMCP if configured
            await HandleMSDPViaGMCPAsync(e.Data);
        }
    }
    
    public override async ValueTask HandleSubnegotiationAsync(ReadOnlyMemory<byte> data)
    {
        // Process GMCP message
        var message = ParseMessage(data);
        
        // Notify local handlers
        MessageReceived?.Invoke(this, new GMCPMessageEventArgs(message));
        
        // Publish to event bus for other protocols
        _eventBus.PublishSubnegotiation(OptionCode, data);
    }
}
```

---

## Complete Usage Examples

### Example 1: Simple Server Setup

```csharp
using Microsoft.Extensions.Logging;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Protocols;

// Configure logging
var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddConsole();
    builder.SetMinimumLevel(LogLevel.Debug);
});

// Build telnet interpreter with common protocols
var telnet = await new TelnetInterpreterBuilder()
    .UseMode(TelnetMode.Server)
    .ConfigureLogging(logging => logging.AddConsole())
    .AddProtocol<GMCPProtocol>()
    .AddProtocol<MSDPProtocol>()
    .AddProtocol<NAWSProtocol>()
    .AddProtocol<TerminalTypeProtocol>()
    .BuildAsync();

// Use the interpreter
await telnet.InterpretAsync(incomingByte);
```

### Example 2: Client with Configuration File

```csharp
using Microsoft.Extensions.Configuration;
using TelnetNegotiationCore.Builders;

// Load configuration
var configuration = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("ENVIRONMENT")}.json", 
        optional: true)
    .Build();

// Build from configuration
var telnet = await new TelnetInterpreterBuilder()
    .UseConfiguration(configuration)
    .BuildAsync();

// All protocols and settings loaded from appsettings.json
```

### Example 3: Advanced Server with Custom Protocol

```csharp
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Protocols;

// Custom protocol implementation
public class MyCustomProtocol : TelnetProtocolBase
{
    public override byte OptionCode => 200;
    public override string Name => "MyCustomProtocol";
    
    public MyCustomProtocol(ILogger<MyCustomProtocol> logger) : base(logger) { }
    
    protected override void ConfigureStateMachine(
        StateMachine<State, Trigger> stateMachine, 
        TelnetMode mode)
    {
        // Configure state transitions
    }
    
    public override ValueTask HandleSubnegotiationAsync(ReadOnlyMemory<byte> data)
    {
        Logger.LogInformation("Received custom protocol data: {Size} bytes", data.Length);
        return ValueTask.CompletedTask;
    }
}

// Build interpreter with custom protocol
var telnet = await new TelnetInterpreterBuilder()
    .UseMode(TelnetMode.Server)
    .AddProtocol<GMCPProtocol>(gmcp =>
    {
        gmcp.MessageReceived += (sender, args) =>
        {
            Console.WriteLine($"GMCP: {args.Message.Package} = {args.Message.Data}");
        };
    })
    .AddProtocol<MyCustomProtocol>()
    .ConfigureProtocols(protocols =>
    {
        // Disable compression
        protocols.DisabledProtocols.Add(86); // MCCP
    })
    .BuildAsync();
```

### Example 4: Runtime Protocol Management

```csharp
var telnet = await new TelnetInterpreterBuilder()
    .UseMode(TelnetMode.Server)
    .AddProtocolsFromAssembly()
    .BuildAsync();

var protocolManager = telnet.ProtocolManager;

// Query enabled protocols
var enabled = protocolManager.GetEnabledProtocols();
Console.WriteLine($"Enabled protocols: {string.Join(", ", enabled.Select(p => p.Name))}");

// Enable a protocol at runtime
protocolManager.EnableProtocol(201); // GMCP

// Disable a protocol (if no dependencies)
try
{
    protocolManager.DisableProtocol(69); // MSDP
}
catch (InvalidOperationException ex)
{
    Console.WriteLine($"Cannot disable: {ex.Message}");
}

// Get specific protocol
var gmcp = protocolManager.GetProtocol(201);
if (gmcp is GMCPProtocol gmcpProtocol)
{
    await gmcpProtocol.SendAsync("Core.Hello", "{\"client\":\"MyClient\"}");
}
```

### Example 5: Testing with Mocks

```csharp
using Moq;
using Xunit;

public class TelnetInterpreterTests
{
    [Fact]
    public async Task Interpreter_WithMockProtocol_ShouldWork()
    {
        // Arrange
        var mockProtocol = new Mock<ITelnetProtocol>();
        mockProtocol.Setup(p => p.OptionCode).Returns(200);
        mockProtocol.Setup(p => p.Name).Returns("MockProtocol");
        mockProtocol.Setup(p => p.Dependencies).Returns(Array.Empty<byte>());
        
        var builder = new TelnetInterpreterBuilder()
            .UseMode(TelnetMode.Server);
            
        // Inject mock protocol
        // (Would need to expose service collection or use factory)
        
        var telnet = await builder.BuildAsync();
        
        // Act
        var protocol = telnet.ProtocolManager.GetProtocol(200);
        
        // Assert
        Assert.Equal("MockProtocol", protocol?.Name);
    }
}
```

---

## Summary

These code examples demonstrate:

1. **Clear Separation of Concerns**: Each protocol is independent
2. **Dependency Management**: Explicit dependencies with validation
3. **Flexible Configuration**: Code, JSON, or hybrid approaches
4. **Testability**: Easy to mock and unit test
5. **Event-Driven**: Loose coupling via event bus
6. **Modern C# Patterns**: DI, Options, Builder, async/await
7. **Backward Compatible**: Can coexist with existing code during migration

**Next Steps**: Review these examples and select which patterns best fit the library's needs before implementing.

---

**Document Version**: 1.0  
**Date**: January 18, 2026  
**Status**: Example Code (Not Implemented)
