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
- **TNCP004:** Empty ConfigureStateMachine warning
- **TNCP005:** Missing parameterless constructor

## Building

```bash
dotnet build TelnetNegotiationCore.Analyzers.csproj
```

## Testing

Create a test project that references the analyzer to validate diagnostic behavior.

## License

Same as parent TelnetNegotiationCore project.
