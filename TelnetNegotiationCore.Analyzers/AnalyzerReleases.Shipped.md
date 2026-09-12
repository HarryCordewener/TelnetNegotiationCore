; Shipped analyzer releases
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

## Release 1.0

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
TNCP001 | TelnetPlugin | Error | PluginProtocolTypeAnalyzer
TNCP002 | TelnetPlugin | Error | PluginCircularDependencyAnalyzer
TNCP003 | TelnetPlugin | Error | PluginDependencyValidationAnalyzer
TNCP004 | TelnetPlugin | Info | ConfigureStateMachineAnalyzer
TNCP005 | TelnetPlugin | Warning | PluginConstructorAnalyzer
TNCP006 | TelnetNegotiationCore.PluginArchitecture | Info | PluginRequiredMethodAnalyzer

## Release 1.1

### Removed Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
TNCP004 | TelnetPlugin | Info | ConfigureStateMachineAnalyzer -- every protocol has moved off Stateless, so ConfigureStateMachine is no longer a state machine wiring point and an empty-or-logging-only body no longer signals an incomplete migration.
