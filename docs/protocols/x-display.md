# X-Display Location (RFC 1096)

The X-Display Location protocol (RFC 1096) allows clients and servers to exchange X Window System display location information. This is useful for X11 applications that need to know where to display their GUI.

## Server side
```csharp
var telnet = await new TelnetInterpreterBuilder()
    .UseMode(TelnetInterpreter.TelnetMode.Server)
    .UseLogger(logger)
    .OnSubmit((data, encoding, telnet) => HandleSubmitAsync(data, encoding, telnet))
    .OnNegotiation((data) => WriteToNetworkAsync(data))
    .AddPlugin<XDisplayProtocol>()
        .OnDisplayLocation((displayLocation) => 
        {
            logger.LogInformation("Client X display location: {DisplayLocation}", displayLocation);
            return ValueTask.CompletedTask;
        })
    .BuildAsync();
```

The server automatically announces support and requests the X display location from clients that support it.

## Client side
The client automatically responds to server requests for X display location. You can customize the display location to send:

```csharp
// Option 1: Use default (empty display location)
var telnet = await new TelnetInterpreterBuilder()
    .UseMode(TelnetInterpreter.TelnetMode.Client)
    .UseLogger(logger)
    .OnSubmit((data, encoding, telnet) => HandleSubmitAsync(data, encoding, telnet))
    .OnNegotiation((data) => WriteToNetworkAsync(data))
    .AddPlugin<XDisplayProtocol>()
    .BuildAsync();

// Option 2: Configure custom X display location
var telnet = await new TelnetInterpreterBuilder()
    .UseMode(TelnetInterpreter.TelnetMode.Client)
    .UseLogger(logger)
    .OnSubmit((data, encoding, telnet) => HandleSubmitAsync(data, encoding, telnet))
    .OnNegotiation((data) => WriteToNetworkAsync(data))
    .AddPlugin<XDisplayProtocol>()
        .WithClientDisplayLocation("localhost:0.0")  // Standard X display format
    .BuildAsync();
```

## Display location format
The X display location typically follows the format: `hostname:displaynumber.screennumber`

Examples:
- `localhost:0.0` - Local X server, display 0, screen 0
- `192.168.1.100:0` - Remote X server at specific IP
- `myhost.example.com:10.0` - Remote X server via hostname

## Use cases
- **X11 Forwarding**: Enable X Window System applications to display on client's screen
- **Remote Desktop**: Support applications that need to know the display location
- **Legacy Unix Systems**: Compatibility with older Unix/Linux systems using X11

**Note:** This protocol is primarily useful for X Window System applications. Modern applications often use different display protocols (like VNC, RDP, or web-based interfaces).

