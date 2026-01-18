# Architecture Diagrams and Visual Reference

This document provides visual diagrams to accompany the architectural recommendations.

**Note**: These are conceptual diagrams for the recommended architecture. No implementation has been performed.

---

## Current Architecture (Monolithic)

```
┌────────────────────────────────────────────────────────────────────────┐
│                         TelnetInterpreter                               │
│                     (Single Partial Class)                              │
├────────────────────────────────────────────────────────────────────────┤
│                                                                         │
│  TelnetStandardInterpreter.cs (Main)                                   │
│  │                                                                      │
│  ├── TelnetGMCPInterpreter.cs (Partial)                                │
│  │   ├── SetupGMCPNegotiation()                                        │
│  │   ├── _GMCPBytes: List<byte>                                        │
│  │   └── SignalOnGMCPAsync: Func<...>                                  │
│  │                                                                      │
│  ├── TelnetMSDPInterpreter.cs (Partial)                                │
│  │   ├── SetupMSDPNegotiation()                                        │
│  │   ├── _currentMSDPInfo: List<byte>                                  │
│  │   └── SignalOnMSDPAsync: Func<...>                                  │
│  │                                                                      │
│  ├── TelnetEORInterpreter.cs (Partial)                                 │
│  │   ├── SetupEORNegotiation()                                         │
│  │   ├── _doEOR: bool?                                                 │
│  │   └── SignalOnPromptingAsync: Func<...>                             │
│  │                                                                      │
│  ├── TelnetSuppressGAInterpreter.cs (Partial)                          │
│  │   ├── SetupSuppressGANegotiation()                                  │
│  │   └── _doGA: bool?                                                  │
│  │                                                                      │
│  ├── TelnetNAWSInterpreter.cs (Partial)                                │
│  ├── TelnetCharsetInterpreter.cs (Partial)                             │
│  ├── TelnetTerminalTypeInterpreter.cs (Partial)                        │
│  ├── TelnetMSSPInterpreter.cs (Partial)                                │
│  └── TelnetSafeInterpreter.cs (Partial)                                │
│                                                                         │
│  Global State Machine: 100+ States                                     │
│  ├── State.DoGMCP, State.WillGMCP, State.AlmostNegotiatingGMCP...     │
│  ├── State.DoMSDP, State.WillMSDP, State.AlmostNegotiatingMSDP...     │
│  ├── State.DoEOR, State.WillEOR, State.Prompting...                   │
│  └── ... (50+ more states)                                             │
│                                                                         │
│  Hardcoded Initialization (lines 108-120):                             │
│  new List<Func<...>> {                                                 │
│      SetupSafeNegotiation,                                             │
│      SetupEORNegotiation,                                              │
│      SetupSuppressGANegotiation,                                       │
│      SetupMSSPNegotiation,                                             │
│      SetupMSDPNegotiation,                                             │
│      SetupGMCPNegotiation,        ← Hardcoded dependency on MSDP       │
│      SetupTelnetTerminalType,                                          │
│      SetupCharsetNegotiation,                                          │
│      SetupNAWS,                                                        │
│      SetupStandardProtocol                                             │
│  }.AggregateRight(...)                                                 │
│                                                                         │
└────────────────────────────────────────────────────────────────────────┘

Issues:
❌ All protocols always loaded
❌ Cannot disable individual protocols
❌ Hidden dependencies (GMCP → MSDP hardcoded)
❌ Single massive state machine
❌ Global namespace pollution
❌ Difficult to test in isolation
```

---

## Recommended Architecture (Plugin-Based)

```
┌─────────────────────────────────────────────────────────────────┐
│                    TelnetInterpreter                             │
│               (Lightweight Orchestrator)                         │
└──────────────────────┬──────────────────────────────────────────┘
                       │
                       │ uses
                       ▼
┌──────────────────────────────────────────────────────────────────┐
│                   IProtocolManager                                │
│  • RegisterProtocol(ITelnetProtocol)                             │
│  • EnableProtocol(byte optionCode)                               │
│  • DisableProtocol(byte optionCode)                              │
│  • GetProtocol(byte optionCode)                                  │
│  • InitializeAllAsync()                                          │
└──────────────────────┬───────────────────────────────────────────┘
                       │
                       │ manages
                       ▼
            ┌──────────────────────┐
            │   ITelnetProtocol     │
            │   (interface)         │
            │                       │
            │ • OptionCode          │
            │ • Name                │
            │ • Dependencies        │
            │ • Initialize()        │
            │ • Handle...()         │
            └──────────┬────────────┘
                       │
       ┌───────────────┼───────────────┬──────────────┬─────────────┐
       │               │               │              │             │
       ▼               ▼               ▼              ▼             ▼
┌──────────┐    ┌──────────┐    ┌──────────┐   ┌──────────┐  ┌──────────┐
│  GMCP    │    │  MSDP    │    │   EOR    │   │  NAWS    │  │  MSSP    │
│ Protocol │    │ Protocol │    │ Protocol │   │ Protocol │  │ Protocol │
│          │    │          │    │          │   │          │  │          │
│ Code:201 │    │ Code:69  │    │ Code:25  │   │ Code:31  │  │ Code:70  │
│ Deps:[69]│    │ Deps:[]  │    │ Deps:[]  │   │ Deps:[]  │  │ Deps:[]  │
│          │    │          │    │          │   │          │  │          │
│ Own      │    │ Own      │    │ Own      │   │ Own      │  │ Own      │
│ State    │    │ State    │    │ State    │   │ State    │  │ State    │
│ Machine  │    │ Machine  │    │ Machine  │   │ Machine  │  │ Machine  │
│ (5-10    │    │ (5-10    │    │ (5-10    │   │ (5-10    │  │ (5-10    │
│ states)  │    │ states)  │    │ states)  │   │ states)  │  │ states)  │
└──────────┘    └──────────┘    └──────────┘   └──────────┘  └──────────┘

Benefits:
✅ Load only needed protocols
✅ Runtime enable/disable
✅ Explicit dependencies
✅ Per-protocol state machines
✅ Independent testing
✅ Easy to add new protocols
```

---

## Protocol Lifecycle Flow

```
┌─────────────────────────────────────────────────────────────────┐
│                     Protocol Lifecycle                           │
└─────────────────────────────────────────────────────────────────┘

1. REGISTRATION
   ┌──────────────────────┐
   │ Developer registers  │
   │ protocol via builder │
   └──────────┬───────────┘
              │
              ▼
   ┌──────────────────────────────┐
   │ builder.AddProtocol<GMCP>()  │
   └──────────┬───────────────────┘
              │
              ▼
   ┌──────────────────────────────────────────┐
   │ DI Container registers ITelnetProtocol   │
   └──────────┬───────────────────────────────┘
              │
              │
2. DEPENDENCY RESOLUTION
              │
              ▼
   ┌──────────────────────────────────────┐
   │ ProtocolManager validates deps       │
   │ • Checks all Dependencies[] exist    │
   │ • Detects circular dependencies      │
   │ • Topological sort                   │
   └──────────┬───────────────────────────┘
              │
              │ If GMCP registered
              │ and Dependencies = [69]
              │
              ▼
   ┌──────────────────────────────────────┐
   │ Auto-enable MSDP (option 69)         │
   │ before enabling GMCP                 │
   └──────────┬───────────────────────────┘
              │
              │
3. INITIALIZATION
              │
              ▼
   ┌──────────────────────────────────────┐
   │ Initialize protocols in order:       │
   │ 1. MSDP (no deps)                    │
   │ 2. GMCP (depends on MSDP)            │
   └──────────┬───────────────────────────┘
              │
              ▼
   ┌──────────────────────────────────────┐
   │ protocol.Initialize(...)             │
   │ • Configure state machine            │
   │ • Setup event handlers               │
   └──────────┬───────────────────────────┘
              │
              │
4. NEGOTIATION
              │
              ▼
   ┌──────────────────────────────────────┐
   │ Send initial negotiation             │
   │ IAC WILL [OptionCode]                │
   └──────────┬───────────────────────────┘
              │
              ▼
   ┌──────────────────────────────────────┐
   │ Receive IAC DO [OptionCode]          │
   └──────────┬───────────────────────────┘
              │
              ▼
   ┌──────────────────────────────────────┐
   │ protocol.OnNegotiationCompleteAsync()│
   │ • IsEnabled = true                   │
   │ • Ready to handle data               │
   └──────────┬───────────────────────────┘
              │
              │
5. ACTIVE
              │
              ▼
   ┌──────────────────────────────────────┐
   │ Protocol is active                   │
   │ • Handles subnegotiation data        │
   │ • Sends protocol messages            │
   │ • Publishes events                   │
   └──────────┬───────────────────────────┘
              │
              │
6. DISABLE (optional)
              │
              ▼
   ┌──────────────────────────────────────┐
   │ protocolManager.DisableProtocol(201) │
   │ • Checks no dependents               │
   │ • Sends IAC WONT [OptionCode]        │
   └──────────┬───────────────────────────┘
              │
              ▼
   ┌──────────────────────────────────────┐
   │ protocol.OnDisabledAsync()           │
   │ • IsEnabled = false                  │
   │ • Cleanup resources                  │
   └──────────────────────────────────────┘
```

---

## Dependency Resolution Example

```
┌──────────────────────────────────────────────────────────────────┐
│          Dependency Graph & Resolution Order                      │
└──────────────────────────────────────────────────────────────────┘

Input Protocols:
  • GMCP (option 201, depends on [69])
  • MSDP (option 69, depends on [])
  • EOR (option 25, depends on [])
  • NAWS (option 31, depends on [])

Step 1: Build Dependency Graph
───────────────────────────────

         ┌──────────┐
         │   MSDP   │
         │  (69)    │
         │  Deps:[] │
         └────┬─────┘
              │
              │ required by
              │
              ▼
         ┌──────────┐
         │   GMCP   │
         │  (201)   │
         │ Deps:[69]│
         └──────────┘

    ┌──────────┐         ┌──────────┐
    │   EOR    │         │   NAWS   │
    │  (25)    │         │  (31)    │
    │  Deps:[] │         │  Deps:[] │
    └──────────┘         └──────────┘


Step 2: Topological Sort (Kahn's Algorithm)
────────────────────────────────────────────

In-Degree Count:
  • MSDP: 0 (no dependencies)  ← Start here
  • GMCP: 1 (depends on MSDP)
  • EOR:  0 (no dependencies)  ← Start here
  • NAWS: 0 (no dependencies)  ← Start here

Processing Order:
  1. MSDP  (in-degree 0) → Add to result
     └─→ Decrement GMCP in-degree to 0
  2. EOR   (in-degree 0) → Add to result
  3. NAWS  (in-degree 0) → Add to result
  4. GMCP  (now in-degree 0) → Add to result

Final Initialization Order:
  [MSDP, EOR, NAWS, GMCP]
  or
  [EOR, MSDP, NAWS, GMCP]
  or
  [NAWS, MSDP, EOR, GMCP]
  
  Key: MSDP always before GMCP (guaranteed)


Step 3: Validation
──────────────────

✓ All dependencies satisfied
✓ No circular dependencies
✓ Topological order exists

Result: VALID ✅
```

---

## Circular Dependency Detection

```
┌──────────────────────────────────────────────────────────────────┐
│               Circular Dependency Example                         │
└──────────────────────────────────────────────────────────────────┘

Bad Configuration:
  • Protocol A (option 1, depends on [2])
  • Protocol B (option 2, depends on [3])
  • Protocol C (option 3, depends on [1])

Dependency Graph:
                    ┌──────────┐
            ┌───────│ Protocol │
            │       │    A     │
            │       │  (opt 1) │
            │       └──────┬───┘
            │              │
            │              │ depends on
            │              ▼
  depends on│       ┌──────────┐
            │       │ Protocol │
            │   ┌───│    B     │
            │   │   │  (opt 2) │
            │   │   └──────┬───┘
            │   │          │
            │   │          │ depends on
            │   │          ▼
            │   │   ┌──────────┐
            │   │   │ Protocol │
            │   └───│    C     │
            │       │  (opt 3) │
            └───────┴──────────┘

Topological Sort Attempt:
  1. In-degree: A=1, B=1, C=1
  2. No protocol has in-degree 0!
  3. Cannot find starting point

Result: INVALID ❌
Error: "Circular dependency detected among protocols: A, B, C"
```

---

## Builder Pattern Flow

```
┌──────────────────────────────────────────────────────────────────┐
│                  Builder Pattern Flow                             │
└──────────────────────────────────────────────────────────────────┘

Developer Code:
───────────────

var telnet = await new TelnetInterpreterBuilder()
    .UseMode(TelnetMode.Server)
    .AddProtocol<GMCPProtocol>()
    .AddProtocol<MSDPProtocol>()
    .ConfigureProtocol<GMCPProtocol>(gmcp => {
        gmcp.MaxPackageSize = 16384;
    })
    .BuildAsync();


Behind the Scenes:
──────────────────

1. new TelnetInterpreterBuilder()
   ↓
   Creates IServiceCollection
   Registers core services:
   • IProtocolManager
   • IProtocolDependencyResolver
   • IProtocolEventBus

2. .UseMode(TelnetMode.Server)
   ↓
   Stores mode for later use

3. .AddProtocol<GMCPProtocol>()
   ↓
   services.AddSingleton<ITelnetProtocol, GMCPProtocol>()

4. .AddProtocol<MSDPProtocol>()
   ↓
   services.AddSingleton<ITelnetProtocol, MSDPProtocol>()

5. .ConfigureProtocol<GMCPProtocol>(...)
   ↓
   services.Configure<GMCPProtocolOptions>(...)

6. .BuildAsync()
   ↓
   a) Build service provider
      provider = services.BuildServiceProvider()
   
   b) Resolve all ITelnetProtocol instances
      protocols = provider.GetServices<ITelnetProtocol>()
   
   c) Register with ProtocolManager
      foreach protocol in protocols:
          manager.RegisterProtocol(protocol)
   
   d) Validate dependencies
      validation = manager.ValidateDependencies()
      if not valid: throw exception
   
   e) Initialize in dependency order
      await manager.InitializeAllAsync()
   
   f) Create TelnetInterpreter
      interpreter = new TelnetInterpreter(mode, manager, logger)
   
   g) Return interpreter
      return interpreter
```

---

## Event Bus Flow

```
┌──────────────────────────────────────────────────────────────────┐
│                     Event Bus Architecture                        │
└──────────────────────────────────────────────────────────────────┘

Scenario: GMCP Protocol needs to route MSDP messages

┌─────────────┐          ┌─────────────┐          ┌─────────────┐
│    GMCP     │          │   Event     │          │    MSDP     │
│  Protocol   │          │    Bus      │          │  Protocol   │
└──────┬──────┘          └──────┬──────┘          └──────┬──────┘
       │                        │                        │
       │                        │                        │
       │  Subscribe to events   │                        │
       │  ────────────────────> │                        │
       │  eventBus.             │                        │
       │  SubnegotiationReceived│                        │
       │  += OnOtherProtocol    │                        │
       │                        │                        │
       │                        │   Receives MSDP data   │
       │                        │  <──────────────────── │
       │                        │  HandleSubneg...()     │
       │                        │                        │
       │                        │  Publish event         │
       │                        │  <──────────────────── │
       │                        │  eventBus.Publish...() │
       │                        │                        │
       │  Event notification    │                        │
       │  <──────────────────── │                        │
       │  OnOtherProtocol(      │                        │
       │    optionCode: 69,     │                        │
       │    data: [...]         │                        │
       │  )                     │                        │
       │                        │                        │
       │  Check if MSDP         │                        │
       ├──────┐                 │                        │
       │      │ if (e.OptionCode│                        │
       │      │     == 69)      │                        │
       │      │   RouteViagMCP  │                        │
       │<─────┘                 │                        │
       │                        │                        │

Benefits:
• Loose coupling between protocols
• GMCP doesn't directly reference MSDP
• Easy to add new protocol interactions
• Testable with mock event bus
```

---

## State Machine: Current vs. Recommended

```
┌──────────────────────────────────────────────────────────────────┐
│              Current: Single Monolithic State Machine             │
└──────────────────────────────────────────────────────────────────┘

State Machine with 100+ states:

State.Accepting
State.ReadingCharacters
State.Act
State.StartNegotiation
State.Willing
State.Refusing
State.Do
State.Dont
State.SubNegotiation

GMCP States:
State.DoGMCP
State.DontGMCP
State.WillGMCP
State.WontGMCP
State.AlmostNegotiatingGMCP
State.EvaluatingGMCPValue
State.EscapingGMCPValue
State.CompletingGMCPValue

MSDP States:
State.DoMSDP
State.DontMSDP
State.WillMSDP
State.WontMSDP
State.AlmostNegotiatingMSDP
State.EvaluatingMSDP
State.EscapingMSDP
State.CompletingMSDP

... (50+ more protocol-specific states)

Issues:
❌ State explosion
❌ Hard to navigate
❌ Protocols can't have overlapping state names
❌ Global state shared by all protocols


┌──────────────────────────────────────────────────────────────────┐
│           Recommended: Per-Protocol State Machines                │
└──────────────────────────────────────────────────────────────────┘

Core State Machine (shared):
  State.Accepting
  State.ReadingCharacters
  State.Act
  State.StartNegotiation
  State.SubNegotiation
  (10-15 states)

GMCP Protocol State Machine:
  ProtocolState.Uninitialized
  ProtocolState.Negotiating
  ProtocolState.Active
  ProtocolState.ReceivingData
  ProtocolState.Inactive
  (5-8 states)

MSDP Protocol State Machine:
  ProtocolState.Uninitialized
  ProtocolState.Negotiating
  ProtocolState.Active
  ProtocolState.ParsingArray
  ProtocolState.ParsingTable
  ProtocolState.Inactive
  (6-10 states)

Benefits:
✅ Smaller, focused state machines
✅ Protocols can reuse state names
✅ Easier to reason about
✅ Better encapsulation
```

---

## Configuration Sources

```
┌──────────────────────────────────────────────────────────────────┐
│                 Configuration Source Priority                     │
└──────────────────────────────────────────────────────────────────┘

Priority (highest to lowest):

1. Code Configuration (Highest Priority)
   ────────────────────────────────────
   var telnet = await builder
       .AddProtocol<GMCPProtocol>(gmcp => {
           gmcp.MaxPackageSize = 16384;  ← Overrides everything
       })
       .BuildAsync();


2. Environment Variables
   ──────────────────────
   TELNET__PROTOCOLS__ENABLEDPROTOCOLS__0=201
   TELNET__PROTOCOLS__ENABLEDPROTOCOLS__1=69
   ↓
   Overrides JSON configuration


3. appsettings.{Environment}.json
   ──────────────────────────────
   appsettings.Production.json:
   {
     "Telnet": {
       "Protocols": {
         "EnabledProtocols": [201, 69, 31]
       }
     }
   }
   ↓
   Overrides base appsettings.json


4. appsettings.json (Lowest Priority)
   ───────────────────────────────────
   {
     "Telnet": {
       "Mode": "Server",
       "Protocols": {
         "EnabledProtocols": [201]  ← Base config
       }
     }
   }


Example Resolution:
───────────────────

appsettings.json:
  EnabledProtocols: [201]

appsettings.Production.json:
  EnabledProtocols: [201, 69]

Environment Variable:
  TELNET__PROTOCOLS__ENABLEDPROTOCOLS__2=31

Code:
  .AddProtocol<TerminalTypeProtocol>()  (option 24)

Final Result:
  EnabledProtocols: [201, 69, 31, 24]
                     ↑    ↑   ↑   ↑
                     │    │   │   └─ Code
                     │    │   └───── Environment
                     │    └───────── Production JSON
                     └────────────── Base JSON
```

---

## Testing Strategy

```
┌──────────────────────────────────────────────────────────────────┐
│                     Testing Pyramid                               │
└──────────────────────────────────────────────────────────────────┘

                        ┌──────────────┐
                        │  End-to-End  │  ← Full telnet session
                        │   Tests      │     (Slow, few tests)
                        │    (E2E)     │
                        └──────┬───────┘
                               │
                   ┌───────────┴───────────┐
                   │   Integration Tests   │  ← Multiple protocols
                   │  (Protocol Manager,   │     interacting
                   │   Dependency Resolver)│     (Medium speed)
                   └───────────┬───────────┘
                               │
              ┌────────────────┴────────────────┐
              │     Unit Tests                  │  ← Individual
              │  (Individual Protocols,         │     protocols
              │   State Machines, Builders)     │     (Fast, many)
              └─────────────────────────────────┘


Unit Test Example:
──────────────────

[Fact]
public async Task GMCPProtocol_ParseMessage_ShouldSplitCorrectly()
{
    // Arrange
    var protocol = new GMCPProtocol(logger, eventBus, options);
    var data = Encoding.UTF8.GetBytes("Core.Hello {\"test\":true}");
    
    // Act
    await protocol.HandleSubnegotiationAsync(data);
    
    // Assert
    Assert.Equal("Core.Hello", receivedPackage);
    Assert.Equal("{\"test\":true}", receivedData);
}


Integration Test Example:
─────────────────────────

[Fact]
public async Task ProtocolManager_GMCPWithMSDP_ShouldAutoEnableDependency()
{
    // Arrange
    var manager = CreateProtocolManager();
    manager.RegisterProtocol(new GMCPProtocol(...)); // Depends on MSDP
    manager.RegisterProtocol(new MSDPProtocol(...));
    
    // Act
    manager.EnableProtocol(201); // Enable GMCP
    
    // Assert
    Assert.True(manager.GetProtocol(69).IsEnabled); // MSDP auto-enabled
    Assert.True(manager.GetProtocol(201).IsEnabled); // GMCP enabled
}


E2E Test Example:
─────────────────

[Fact]
public async Task FullTelnetSession_GMCPNegotiation_ShouldSucceed()
{
    // Arrange
    var server = CreateTestServer();
    var client = CreateTestClient();
    
    // Act
    await client.ConnectAsync(server);
    await client.SendAsync(IAC_WILL_GMCP);
    var response = await client.ReceiveAsync();
    await client.SendGMCPAsync("Core.Hello", "{\"client\":\"Test\"}");
    
    // Assert
    Assert.Equal(IAC_DO_GMCP, response);
    Assert.Contains("Core.Hello", server.ReceivedMessages);
}
```

---

## Migration Path Timeline

```
┌──────────────────────────────────────────────────────────────────┐
│                    Migration Timeline                             │
└──────────────────────────────────────────────────────────────────┘

Current Version: 1.1.1
Target Version: 3.0.0


Phase 1: Foundation (Version 2.0.0)
────────────────────────────────────
Duration: 2-3 months
Effort: Medium

Tasks:
  ✓ Create ITelnetProtocol interface
  ✓ Create IProtocolManager interface
  ✓ Create adapter wrappers for existing code
  ✓ Add TelnetInterpreterBuilder
  ✓ Both old and new APIs work

Status: ⚠️ Old API still primary, new API experimental


Phase 2: Protocol Extraction (Version 2.1.0 - 2.5.0)
─────────────────────────────────────────────────────
Duration: 4-6 months (incremental releases)
Effort: High

Tasks:
  ✓ Extract GMCP → standalone class (v2.1)
  ✓ Extract MSDP → standalone class (v2.2)
  ✓ Extract NAWS, EOR, SuppressGA (v2.3)
  ✓ Extract remaining protocols (v2.4)
  ✓ Deprecate old API with [Obsolete] (v2.5)

Status: ⚠️ Both APIs work, new API recommended


Phase 3: Cleanup (Version 3.0.0)
─────────────────────────────────
Duration: 1-2 months
Effort: Low

Tasks:
  ✓ Remove old partial class API
  ✓ Remove deprecated methods
  ✓ Remove adapter wrappers
  ✓ Update all documentation
  ✓ Publish migration guide

Status: ✅ Only new API available (breaking change)


Timeline Visualization:
───────────────────────

Today                    6 months                 12 months
  │                         │                         │
  v                         v                         v
  
v1.1.1 ──> v2.0.0 ──> v2.1 → v2.2 → v2.3 → v2.4 → v2.5 ──> v3.0.0
  │          │                                      │          │
  │          │                                      │          │
Current   Foundation                           Deprecation  Cleanup
          (Both APIs)                         (Old API     (New API
                                              marked        only)
                                              obsolete)


Deprecation Warnings:
─────────────────────

Version 2.5.0 code:

[Obsolete("Use TelnetInterpreterBuilder instead. " +
          "This constructor will be removed in version 3.0.0. " +
          "See MIGRATION.md for details.")]
public TelnetInterpreter(TelnetMode mode, ILogger logger)
{
    // Old implementation
}
```

---

## Summary

These diagrams illustrate:

1. **Current Architecture**: Monolithic partial class with hidden dependencies
2. **Recommended Architecture**: Plugin-based with explicit dependencies
3. **Protocol Lifecycle**: Clear stages from registration to active use
4. **Dependency Resolution**: Automatic ordering and validation
5. **Builder Pattern**: Fluent, discoverable API
6. **Event Bus**: Loose coupling between protocols
7. **State Machines**: Per-protocol instead of global
8. **Configuration**: Multiple sources with clear priority
9. **Testing**: Pyramid strategy for comprehensive coverage
10. **Migration Path**: Gradual transition with backward compatibility

---

**Document Version**: 1.0  
**Date**: January 18, 2026  
**Status**: Conceptual Diagrams (Not Implemented)
