# Phase 3: Analyzer Expansion - TNCP004 Rule

**Date:** January 19, 2026  
**Status:** Phase 3 Started - New Analyzer Rule Implemented

---

## Summary

Phase 3 expands the analyzer capabilities by implementing **TNCP004**, a new diagnostic rule that detects incomplete plugin integrations. This rule identifies protocols with empty or logging-only `ConfigureStateMachine` methods, highlighting incomplete migrations from the old interpreter-based architecture to the plugin-based architecture.

## Changes Made

### 1. New Analyzer Rule: TNCP004 ✅

**File:** `TelnetNegotiationCore.Analyzers/ConfigureStateMachineAnalyzer.cs`

**Rule ID:** TNCP004  
**Severity:** Info (visible but doesn't break builds)  
**Purpose:** Detect incomplete ConfigureStateMachine implementations

**What it detects:**
- Empty `ConfigureStateMachine` methods
- `ConfigureStateMachine` methods that only contain logging statements
- Highlights protocols that haven't migrated from interpreter-based to plugin-based state machine configuration

### 2. Analyzer Documentation Updated ✅

**File:** `TelnetNegotiationCore.Analyzers/README.md`

Added comprehensive documentation for TNCP004 including:
- Description and examples
- List of 8 protocols currently detected
- Explanation of incomplete migration pattern

---

## Current Detections

The TNCP004 analyzer successfully identifies **8 protocols** with incomplete `ConfigureStateMachine` implementations:

1. **CharsetProtocol** - Only logs "Configuring Charset state machine"
2. **EORProtocol** - Only logs "Configuring EOR state machine"
3. **GMCPProtocol** - Only logs "Configuring GMCP state machine"
4. **MSDPProtocol** - Only logs "Configuring MSDP state machine"
5. **MSSPProtocol** - Only logs "Configuring MSSP state machine"
6. **NAWSProtocol** - Only logs "Configuring NAWS state machine"
7. **SuppressGoAheadProtocol** - Only logs "Configuring SGA state machine"
8. **TerminalTypeProtocol** - Only logs "Configuring Terminal Type state machine"

### Example Detection

```csharp
// File: CharsetProtocol.cs, Line 50
public override void ConfigureStateMachine(StateMachine<State, Trigger> stateMachine, IProtocolContext context)
{
    context.Logger.LogInformation("Configuring Charset state machine");
    // ⚠️ TNCP004: This method only contains logging - no actual state configuration!
}
```

### What Should Be Done Instead

```csharp
public override void ConfigureStateMachine(StateMachine<State, Trigger> stateMachine, IProtocolContext context)
{
    // Actually configure state machine transitions
    if (Mode == TelnetMode.Server)
    {
        stateMachine.Configure(State.Willing)
            .Permit(Trigger.CHARSET, State.WillCharset);
            
        stateMachine.Configure(State.WillCharset)
            .SubstateOf(State.Accepting)
            .OnEntryAsync(async () => await HandleCharsetAsync());
    }
    
    context.Logger.LogInformation("Charset state machine configured");
}
```

---

## Architecture Insight

This analyzer reveals an important architectural pattern in the codebase:

### Dual State Machine Configuration Paths

The repository currently has **two parallel systems** for configuring telnet protocol state machines:

1. **Old System (Still Active):**
   - `TelnetInterpreter` constructor calls methods like `SetupMSSPNegotiation()`, `SetupCharsetNegotiation()`
   - State machine configuration happens in `TelnetMSSPInterpreter.cs`, `TelnetCharsetInterpreter.cs` partial classes
   - This is the code that actually runs

2. **New System (Incomplete):**
   - `TelnetInterpreterBuilder` calls `plugin.ConfigureStateMachine()`
   - Plugins are supposed to configure their own state machines
   - Currently these methods are empty stubs (just logging)

### The Migration Gap

The stored memory notes confirm this:
> "The migration to plugin-based API for MSSP and CHARSET protocols is incomplete. Plugins' ConfigureStateMachine methods are empty stubs, leaving old interpreter-based code running with default configuration."

**TNCP004 makes this gap visible** by highlighting which protocols haven't completed the migration.

---

## Benefits of TNCP004

### 1. Visibility ✅
- Developers now see at build time which plugins are incompletely migrated
- Info-level severity means it's informative without breaking builds
- IDE integration shows warnings in editor

### 2. Documentation ✅
- Serves as executable documentation of technical debt
- Lists exactly which protocols need migration work
- Provides examples of proper implementation

### 3. Future Migration Guidance ✅
- When migrating a protocol, the warning will disappear automatically
- Acts as a checklist for completing plugin-based architecture migration
- Helps prioritize which protocols to migrate next

---

## Testing Results

### Build Status ✅

```
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

Note: TNCP004 uses `Info` severity, which appears in IDE but not in build output. This prevents breaking existing builds while still providing valuable feedback to developers.

### All Tests Pass ✅

```
Passed!  - Failed: 0, Passed: 16, Skipped: 0, Total: 16
```

No regressions introduced by the new analyzer.

---

## Analyzer Rules Summary

| Rule ID | Severity | Description | Status |
|---------|----------|-------------|--------|
| TNCP001 | Error | ProtocolType must return declaring type | ✅ Active (Phase 1) |
| TNCP002 | - | Circular dependency detection | 📋 Planned |
| TNCP003 | - | Missing dependency validation | 📋 Planned |
| TNCP004 | Info | Empty/incomplete ConfigureStateMachine | ✅ Active (Phase 3) |
| TNCP005 | - | Missing parameterless constructor | 📋 Planned |

---

## Implementation Details

### Analyzer Algorithm

1. **Find Methods**: Scan for methods named `ConfigureStateMachine`
2. **Check Context**: Verify containing class implements `ITelnetProtocolPlugin`
3. **Analyze Body**: Examine method body statements
4. **Classify**: Determine if method is:
   - Empty (no statements)
   - Logging-only (all statements are logging calls)
   - Properly implemented (has non-logging statements)
5. **Report**: If empty or logging-only, report Info diagnostic

### Why Info Severity?

- **Info** doesn't count as a warning or error
- Won't break `TreatWarningsAsErrors=true` builds
- Still visible in IDEs (blue squiggle in VS/Rider)
- Appropriate for "this could be better" situations
- Doesn't create pressure to fix immediately

---

## Future Phase 3 Work

Additional analyzer rules that could be implemented:

### TNCP002: Circular Dependency Detection (High Value)
Detect circular dependencies at compile time instead of runtime:
```csharp
// Would detect:
PluginA depends on PluginB
PluginB depends on PluginA
```

### TNCP003: Missing Dependency Validation (High Value)
Ensure dependency types actually implement `ITelnetProtocolPlugin`:
```csharp
// Would catch:
public override IReadOnlyCollection<Type> Dependencies => new[] { typeof(NonPluginClass) };
```

### TNCP005: Constructor Validation (Medium Value)
Ensure plugins have parameterless constructors for `AddPlugin<T>()`:
```csharp
// Would catch:
public class MyProtocol : TelnetProtocolPluginBase
{
    public MyProtocol(string requiredParam) { } // Error: needs parameterless constructor
}
```

---

## Conclusion

✅ **Phase 3 Analyzer Expansion Started**

The TNCP004 rule successfully:
- **Identifies** 8 protocols with incomplete ConfigureStateMachine implementations
- **Documents** the dual state machine configuration architecture
- **Guides** future migration from interpreter-based to plugin-based approach
- **Validates** at compile time without breaking existing builds

This analyzer provides visibility into technical debt and serves as a roadmap for completing the plugin architecture migration.

---

**Next Phase 3 Tasks Available:**
1. Implement TNCP002 (circular dependency detection)
2. Implement TNCP003 (missing dependency validation)
3. Create enum extension generator (eliminate enum reflection)
4. Performance benchmarking (reflection vs generated code)
5. NuGet packaging

---

**Generated by:** Phase 3 Implementation  
**Date:** January 19, 2026
