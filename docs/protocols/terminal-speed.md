# Terminal speed (RFC 1079)

RFC 1079 exchanges the terminal's transmit and receive speeds in bits per second — a **declared** pair of numbers from the era of serial lines, not a measurement. The client states what it wants to claim and the server is told exactly that.

## Server side
```csharp
var telnet = await new TelnetInterpreterBuilder()
    .UseMode(TelnetInterpreter.TelnetMode.Server)
    .UseLogger(logger)
    .OnSubmit((data, encoding, telnet) => HandleSubmitAsync(data, encoding, telnet))
    .OnNegotiation((data) => WriteToNetworkAsync(data))
    .AddPlugin<TerminalSpeedProtocol>()
        .OnTerminalSpeed((transmitSpeed, receiveSpeed) => 
        {
            logger.LogInformation("Client terminal speed: {Transmit} bps transmit, {Receive} bps receive",
                transmitSpeed, receiveSpeed);
            return ValueTask.CompletedTask;
        })
    .BuildAsync();
```

The server automatically announces support and requests terminal speed from clients that support it.

## Client side
The client automatically responds to server requests for terminal speed. You can customize the speeds to send:

```csharp
// Option 1: Use defaults (38400 bps transmit and receive)
var telnet = await new TelnetInterpreterBuilder()
    .UseMode(TelnetInterpreter.TelnetMode.Client)
    .UseLogger(logger)
    .OnSubmit((data, encoding, telnet) => HandleSubmitAsync(data, encoding, telnet))
    .OnNegotiation((data) => WriteToNetworkAsync(data))
    .AddPlugin<TerminalSpeedProtocol>()
    .BuildAsync();

// Option 2: Configure custom terminal speeds
var telnet = await new TelnetInterpreterBuilder()
    .UseMode(TelnetInterpreter.TelnetMode.Client)
    .UseLogger(logger)
    .OnSubmit((data, encoding, telnet) => HandleSubmitAsync(data, encoding, telnet))
    .OnNegotiation((data) => WriteToNetworkAsync(data))
    .AddPlugin<TerminalSpeedProtocol>()
        .WithClientTerminalSpeed(115200, 115200)  // transmit speed, receive speed in bps
    .BuildAsync();
```

## Use cases
- **Server optimization**: Adjust output based on connection speed
- **Client diagnostics**: Report the speed the client is *configured* to claim. Nothing here measures the network — `WithClientTerminalSpeed(115200, 115200)` sends 115200 whatever the link is actually doing, and a server must not read it as bandwidth
- **Compatibility**: Support legacy systems that rely on terminal speed information

**Note:** Most modern applications don't need terminal speed information as network speeds far exceed terminal speeds. This protocol is primarily useful for compatibility with legacy systems or specialized use cases.

