# TelnetNegotiationCore.Analyzers

**Proof-of-Concept Roslyn Analyzer** for compile-time validation of Telnet protocol plugins.

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

### TNCP004: ConfigureStateMachine should configure state transitions (NEW in Phase 3)

**Severity:** Info

**Description:** Detects `ConfigureStateMachine` methods that are empty or only contain logging statements, suggesting incomplete plugin integration.

**Purpose:** Highlights protocols where the state machine configuration has not been migrated from the old interpreter-based approach to the plugin-based approach.

**Example:**

```csharp
// ⚠️ INFO - Detected by analyzer
public override void ConfigureStateMachine(StateMachine<State, Trigger> stateMachine, IProtocolContext context)
{
    context.Logger.LogInformation("Configuring MSSP state machine"); // Only logging - no actual configuration!
}

// ✅ PROPER - No analyzer warning
public override void ConfigureStateMachine(StateMachine<State, Trigger> stateMachine, IProtocolContext context)
{
    // Actually configure state transitions
    stateMachine.Configure(State.Do)
        .Permit(Trigger.MSSP, State.DoMSSP);
        
    stateMachine.Configure(State.DoMSSP)
        .SubstateOf(State.Accepting)
        .OnEntryAsync(async x => await HandleMSSPAsync(x));
}
```

**Current Detections:**
The analyzer currently identifies 8 protocols with incomplete `ConfigureStateMachine` implementations:
- CharsetProtocol
- EORProtocol  
- GMCPProtocol
- MSDPProtocol
- MSSPProtocol
- NAWSProtocol
- SuppressGoAheadProtocol
- TerminalTypeProtocol

These protocols currently rely on the old interpreter-based state machine configuration and represent opportunities for future migration to the plugin-based architecture.

## Integration

To use this analyzer in the main TelnetNegotiationCore library:

```xml
<ItemGroup>
  <ProjectReference Include="..\TelnetNegotiationCore.Analyzers\TelnetNegotiationCore.Analyzers.csproj"
                    OutputItemType="Analyzer"
                    ReferenceOutputAssembly="false" />
</ItemGroup>
```

## Future Rules (Planned)

- **TNCP002:** Circular dependency detection
- **TNCP003:** Missing dependency validation
- **TNCP005:** Missing parameterless constructor

## Building

```bash
dotnet build TelnetNegotiationCore.Analyzers.csproj
```

## Testing

Create a test project that references the analyzer to validate diagnostic behavior.

## License

Same as parent TelnetNegotiationCore project.
