# TelnetNegotiationCore.Analyzers

**Roslyn Analyzer** for compile-time validation of Telnet protocol plugins.

## Purpose

This analyzer provides build-time validation for classes implementing `ITelnetProtocolPlugin`, catching common configuration errors before runtime.

## Implemented Rules

### TNCP001: ProtocolType must return declaring type

**Severity:** Error

**Description:** Ensures that the `ProtocolType` property returns `typeof(DeclaringClass)` to enable proper plugin registration.

**Example:**

```csharp
// ❌ WRONG - Analyzer error
public class MSSPProtocol : TelnetProtocolPluginBase
{
    public override Type ProtocolType => typeof(GMCPProtocol); // Error: Should be typeof(MSSPProtocol)
}

// ✅ CORRECT
public class MSSPProtocol : TelnetProtocolPluginBase
{
    public override Type ProtocolType => typeof(MSSPProtocol);
}
```

### TNCP002: Circular dependency detection (NEW)

**Severity:** Error

**Description:** Detects circular dependencies between plugins at compile time, preventing runtime initialization failures.

**Purpose:** The plugin system uses topological sort to initialize plugins in dependency order. Circular dependencies make this impossible and cause runtime failures.

**Example:**

```csharp
// ❌ ERROR - Circular dependency detected
public class PluginA : TelnetProtocolPluginBase
{
    public override IReadOnlyCollection<Type> Dependencies => new[] { typeof(PluginB) };
}

public class PluginB : TelnetProtocolPluginBase
{
    public override IReadOnlyCollection<Type> Dependencies => new[] { typeof(PluginA) };
}

// ✅ CORRECT - No circular dependencies
public class PluginA : TelnetProtocolPluginBase
{
    public override IReadOnlyCollection<Type> Dependencies => Array.Empty<Type>();
}

public class PluginB : TelnetProtocolPluginBase
{
    public override IReadOnlyCollection<Type> Dependencies => new[] { typeof(PluginA) };
}
```

### TNCP003: Dependency type validation (NEW)

**Severity:** Error

**Description:** Validates that all types in the `Dependencies` property implement `ITelnetProtocolPlugin`.

**Example:**

```csharp
// ❌ ERROR - Invalid dependency type
public class MyProtocol : TelnetProtocolPluginBase
{
    public override IReadOnlyCollection<Type> Dependencies => new[] { typeof(StringBuilder) };
}

// ✅ CORRECT - All dependencies are plugins
public class MyProtocol : TelnetProtocolPluginBase
{
    public override IReadOnlyCollection<Type> Dependencies => new[] { typeof(GMCPProtocol) };
}
```

### TNCP004: ConfigureStateMachine should configure state transitions

**Severity:** Info

**Description:** Detects `ConfigureStateMachine` methods that are empty or only contain logging statements, suggesting incomplete plugin integration.

**Current Detections:** Identifies 8 protocols with incomplete implementations (CharsetProtocol, EORProtocol, GMCPProtocol, MSDPProtocol, MSSPProtocol, NAWSProtocol, SuppressGoAheadProtocol, TerminalTypeProtocol)

### TNCP005: Plugin must have parameterless constructor (NEW)

**Severity:** Warning

**Description:** Ensures plugin classes have a public or internal parameterless constructor for use with the builder pattern.

**Example:**

```csharp
// ⚠️ WARNING - No parameterless constructor
public class MyProtocol : TelnetProtocolPluginBase
{
    public MyProtocol(string requiredParameter) { }
}

// ✅ CORRECT - Has parameterless constructor
public class MyProtocol : TelnetProtocolPluginBase
{
    public MyProtocol() { }
}
```

### TNCP006: Required method call documentation (NEW)

**Severity:** Info

**Description:** Documents which methods must be called on a plugin before initialization. Uses the `[RequiredMethod]` attribute to provide executable documentation for plugin consumers.

**Purpose:** Plugins may require specific setup methods to be called before they are initialized. This analyzer makes those requirements visible at compile time.

**Example:**

```csharp
// Plugin declares required methods using attributes
[RequiredMethod("SetConfiguration")]
[RequiredMethod("EnableFeature", Description = "Required for advanced features")]
public class MyProtocol : TelnetProtocolPluginBase
{
    private bool _configured;
    private bool _featureEnabled;
    
    public void SetConfiguration(MyConfig config)
    {
        _configured = true;
        // Configure the plugin
    }
    
    public void EnableFeature()
    {
        _featureEnabled = true;
        // Enable specific features
    }
    
    protected override ValueTask OnInitializeAsync()
    {
        // Validate required methods were called
        if (!_configured || !_featureEnabled)
            throw new InvalidOperationException("Required methods not called");
        return ValueTask.CompletedTask;
    }
}

// Compiler shows:
// Info TNCP006: Plugin 'MyProtocol' requires calls to the following methods 
//               before InitializeAsync: SetConfiguration, EnableFeature
```

**Usage:** This serves as executable documentation. When a developer uses a plugin, the IDE will show an info message listing all required setup methods, making the plugin's requirements explicit.

## Benefits

**Compile-Time Safety:**
- ✅ Catches ProtocolType mismatches, circular dependencies, invalid dependencies
- ✅ Warns about missing parameterless constructors
- ✅ Highlights incomplete plugin migrations
- ✅ Documents required method calls

**Developer Experience:**
- ✅ Errors appear in IDE immediately with clear messages
- ✅ Prevents runtime failures during development
- ✅ Executable documentation shows plugin requirements
- ✅ Makes plugin contract explicit and discoverable

## Analyzer Rules Summary

| Rule | Severity | Description |
|------|----------|-------------|
| TNCP001 | Error | ProtocolType must return declaring type |
| TNCP002 | Error | No circular dependencies |
| TNCP003 | Error | Dependencies must implement ITelnetProtocolPlugin |
| TNCP004 | Info | ConfigureStateMachine should not be empty |
| TNCP005 | Warning | Plugin must have parameterless constructor |
| TNCP006 | Info | Documents required method calls |

## License

Same as parent TelnetNegotiationCore project.
