# X-Display Location (RFC 1096)

RFC 1096 carries one string: the X display location a client would like a remote application to draw on — the value you would otherwise put in `DISPLAY`. That is the entire option. It does not set up an X connection, forward X traffic, or authenticate anything; an application that acts on the string brings all of that itself.

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
- **Telling an X11 application where to draw**: the remote application already has its own X
  connection, authentication (`XAUTHORITY`, magic cookies) and transport. RFC 1096's whole job is
  making the display *name* available to it. This option opens no connection, forwards no X traffic
  and authenticates nothing
- **Legacy Unix systems**: compatibility with hosts that expect `DISPLAY` to arrive this way

**Note:** This protocol is primarily useful for X Window System applications. Modern applications often use different display protocols (like VNC, RDP, or web-based interfaces).

