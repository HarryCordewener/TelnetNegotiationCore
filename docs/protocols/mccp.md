# MCCP — compression

The MCCP (Mud Client Compression Protocol) provides bandwidth reduction through zlib compression. MCCP2 compresses server-to-client data, while MCCP3 compresses client-to-server data.

Both work the same way: the side that is about to compress sends `IAC SB MCCPn IAC SE`, and from the byte after that `SE` everything it sends in that direction — text *and* telnet negotiation — is one continuous zlib stream that never returns to plain telnet. This library handles that with a stream transform installed on the interpreter: inbound bytes are inflated before the telnet state machine sees them, and outbound writes are deflated after everything else in the library has had its say. There is no per-message compression call, because MCCP has no messages.

## Server side
```csharp
var telnet = await new TelnetInterpreterBuilder()
    .UseMode(TelnetInterpreter.TelnetMode.Server)
    .UseLogger(logger)
    .OnSubmit((data, encoding, telnet) => HandleSubmitAsync(data, encoding, telnet))
    .OnNegotiation((data) => WriteToNetworkAsync(data))
    .AddPlugin<MCCPProtocol>()
        .OnCompressionEnabled((version, enabled) => 
        {
            logger.LogInformation("MCCP{Version} compression {State}", 
                version, enabled ? "enabled" : "disabled");
            return ValueTask.CompletedTask;
        })
    .BuildAsync();
```

The server automatically announces MCCP2 and MCCP3 support. When the client answers `DO MCCP2` the server sends the marker and compresses everything it writes from then on; when the client sends its own `IAC SB MCCP3 IAC SE` the server starts inflating everything it reads.

## Client side
The client automatically responds to server MCCP offers. On `IAC SB MCCP2 IAC SE` it starts inflating everything the server sends; when the server offers MCCP3 it starts compressing everything it writes.

```csharp
var telnet = await new TelnetInterpreterBuilder()
    .UseMode(TelnetInterpreter.TelnetMode.Client)
    .UseLogger(logger)
    .OnSubmit((data, encoding, telnet) => HandleSubmitAsync(data, encoding, telnet))
    .OnNegotiation((data) => WriteToNetworkAsync(data))
    .AddPlugin<MCCPProtocol>()
        .OnCompressionEnabled((version, enabled) => 
        {
            logger.LogInformation("MCCP{Version} compression {State}", 
                version, enabled ? "enabled" : "disabled");
            return ValueTask.CompletedTask;
        })
    .BuildAsync();

// Compression is handled automatically - no manual intervention needed.
// OnSubmit still receives plain text, and OnNegotiation is still given plain
// telnet: the compression happens on the far side of both callbacks.
```

## What it buys
- **MCCP2**: Reduces server-to-client bandwidth by 75-90%
- **MCCP3**: Reduces client-to-server bandwidth and provides security through obscurity
- **Automatic**: Compression/decompression is transparent once negotiated, including for telnet negotiation that arrives inside the compressed stream
- **Standards-compliant**: Uses zlib (RFC 1950) compression via `System.IO.Compression.ZLibStream` (SharpZipLib on `netstandard2.0`), as one stream per connection per direction

## Failure handling
A deflate stream that goes wrong cannot be resynchronized, so if the peer's compressed stream turns out not to be valid zlib the inflater stops for good: the error is logged, `IsMCCP2Enabled` / `IsMCCP3Enabled` goes back to `false`, and nothing further is delivered from that direction rather than garbage being delivered.

