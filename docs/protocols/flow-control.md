# Flow control (RFC 1372)

The Flow Control protocol (RFC 1372) allows servers to remotely control software flow control settings (XON/XOFF) on the client. This is useful for controlling when clients can send data and configuring restart behavior.

## Server side
```csharp
var telnet = await new TelnetInterpreterBuilder()
    .UseMode(TelnetInterpreter.TelnetMode.Server)
    .UseLogger(logger)
    .OnSubmit((data, encoding, telnet) => HandleSubmitAsync(data, encoding, telnet))
    .OnNegotiation((data) => WriteToNetworkAsync(data))
    .AddPlugin<FlowControlProtocol>()
    .BuildAsync();

// Get the plugin to send commands
var flowControl = telnet.PluginManager!.GetPlugin<FlowControlProtocol>();

// Enable flow control on the client
await flowControl!.EnableFlowControlAsync();

// Set restart mode to allow any character to restart output
await flowControl.SetRestartAnyAsync();

// Or set restart mode to allow only XON to restart output
await flowControl.SetRestartXONAsync();

// Disable flow control
await flowControl.DisableFlowControlAsync();
```

The server automatically announces support and can control the client's flow control settings.

## Client side
The client automatically responds to server flow control commands. You can monitor state changes:

```csharp
var telnet = await new TelnetInterpreterBuilder()
    .UseMode(TelnetInterpreter.TelnetMode.Client)
    .UseLogger(logger)
    .OnSubmit((data, encoding, telnet) => HandleSubmitAsync(data, encoding, telnet))
    .OnNegotiation((data) => WriteToNetworkAsync(data))
    .AddPlugin<FlowControlProtocol>()
        .OnFlowControlStateChanged((enabled) => 
        {
            logger.LogInformation("Flow control {State}", enabled ? "enabled" : "disabled");
            return ValueTask.CompletedTask;
        })
        .OnRestartModeChanged((mode) =>
        {
            logger.LogInformation("Restart mode changed to {Mode}", mode);
            return ValueTask.CompletedTask;
        })
    .BuildAsync();

// Check current state
var flowControl = telnet.PluginManager!.GetPlugin<FlowControlProtocol>();
var isEnabled = flowControl!.IsFlowControlEnabled;
var restartMode = flowControl.RestartMode;
```

## Flow control details
- **OFF (0)**: Disables flow control - XOFF/XON characters are passed through as normal input
- **ON (1)**: Enables flow control - XOFF stops output, XON resumes it
- **RESTART-ANY (2)**: Any character (except XOFF) can restart output after XOFF
- **RESTART-XON (3)**: Only XON character can restart output after XOFF

**Note:** Per RFC 1372, flow control is automatically enabled when the client accepts the protocol. The server can then send commands to toggle it or configure restart behavior.

