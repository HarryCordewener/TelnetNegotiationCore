# Line mode (RFC 1184)

The Line Mode protocol (RFC 1184) allows negotiation of whether line editing should be done locally on the client or remotely on the server. This is useful for controlling how user input is processed.

## Server side
```csharp
var telnet = await new TelnetInterpreterBuilder()
    .UseMode(TelnetInterpreter.TelnetMode.Server)
    .UseLogger(logger)
    .OnSubmit((data, encoding, telnet) => HandleSubmitAsync(data, encoding, telnet))
    .OnNegotiation((data) => WriteToNetworkAsync(data))
    .AddPlugin<LineModeProtocol>()
        .OnModeChanged((mode) => 
        {
            logger.LogInformation("Line mode changed to: {Mode:X2}", mode);
            return ValueTask.CompletedTask;
        })
    .BuildAsync();

// Get the plugin to send mode commands
var lineMode = telnet.PluginManager!.GetPlugin<LineModeProtocol>();

// Enable EDIT mode (client does local line editing)
await lineMode!.EnableEditModeAsync();

// Enable TRAPSIG mode (client traps signals like Ctrl-C)
await lineMode.EnableTrapSigModeAsync();

// Or set both at once with a custom mode byte
await lineMode.SetModeAsync(0x03); // EDIT | TRAPSIG

// Disable modes
await lineMode.DisableEditModeAsync();
await lineMode.DisableTrapSigModeAsync();
```

The server automatically announces support and can control the client's line mode settings.

## Client side
The client automatically responds to server line mode commands. You can monitor mode changes:

```csharp
var telnet = await new TelnetInterpreterBuilder()
    .UseMode(TelnetInterpreter.TelnetMode.Client)
    .UseLogger(logger)
    .OnSubmit((data, encoding, telnet) => HandleSubmitAsync(data, encoding, telnet))
    .OnNegotiation((data) => WriteToNetworkAsync(data))
    .AddPlugin<LineModeProtocol>()
        .OnModeChanged((mode) =>
        {
            logger.LogInformation("Server set line mode to: {Mode:X2}", mode);
            return ValueTask.CompletedTask;
        })
    .BuildAsync();

// Check current state
var lineMode = telnet.PluginManager!.GetPlugin<LineModeProtocol>();
var isEditMode = lineMode!.IsEditModeEnabled;
var isTrapSig = lineMode.IsTrapSigModeEnabled;
```

## Line mode details
- **EDIT (0x01)**: When set, client performs local line editing; when unset, server handles all editing
- **TRAPSIG (0x02)**: When set, client traps interrupt/quit signals; when unset, signals are passed through
- **MODE_ACK (0x04)**: Acknowledgment bit used in mode negotiations
- **SOFT_TAB (0x08)**: Advises client on tab handling (expand to spaces vs. send as tab)
- **LIT_ECHO (0x10)**: Advises client to echo non-printable characters literally

**Note:** SLC (Set Local Characters) and FORWARDMASK subnegotiations are not currently implemented. The protocol focuses on MODE negotiations, which are the most commonly used feature.

