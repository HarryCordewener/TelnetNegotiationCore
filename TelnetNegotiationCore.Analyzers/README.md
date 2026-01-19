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

## Benefits

**Compile-Time Safety:**
- ✅ Catches ProtocolType mismatches, circular dependencies, invalid dependencies
- ✅ Warns about missing parameterless constructors
- ✅ Highlights incomplete plugin migrations

**Developer Experience:**
- ✅ Errors appear in IDE immediately with clear messages
- ✅ Prevents runtime failures during development

## License

Same as parent TelnetNegotiationCore project.
