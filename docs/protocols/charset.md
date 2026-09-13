# CHARSET — character set negotiation

`CharsetProtocol` implements RFC 2066. `WithCharsetOrder(...)` states the encodings this side prefers,
most preferred first, and whatever the two sides settle on becomes `TelnetInterpreter.CurrentEncoding`
— which is the encoding every other protocol on the connection reads and writes text in. Until it
settles, that is UTF-8.

```csharp
.AddPlugin<CharsetProtocol>()
    .WithCharsetOrder(Encoding.UTF8, Encoding.GetEncoding("iso-8859-1"))
```

## TTABLE (translation tables)
The TTABLE feature of RFC 2066 Charset protocol allows negotiation of custom character set translation tables. This is useful for specialized character mappings, legacy systems, or private character sets not registered with IANA.

## Server side
```csharp
var telnet = await new TelnetInterpreterBuilder()
    .UseMode(TelnetInterpreter.TelnetMode.Server)
    .UseLogger(logger)
    .OnSubmit((data, encoding, telnet) => HandleSubmitAsync(data, encoding, telnet))
    .OnNegotiation((data) => WriteToNetworkAsync(data))
    .AddPlugin<CharsetProtocol>()
        .WithCharsetOrder(Encoding.UTF8, Encoding.GetEncoding("iso-8859-1"))
        .WithTTableSupport(true)
        .OnTTableReceived(async (ttableData) => 
        {
            // Parse and validate the TTABLE data
            // Version 1 format: <version> <sep> <charset1> <sep> <size1> <count1> <charset2> <sep> <size2> <count2> <map1> <map2>
            logger.LogInformation("Received TTABLE with {Bytes} bytes", ttableData.Length);
            
            // Validate the table structure
            if (ttableData.Length < 2 || ttableData[0] != 1)
            {
                logger.LogWarning("Invalid TTABLE version or format");
                return false; // NAK - request retransmission
            }
            
            // Store or apply the translation table
            await StoreTranslationTable(ttableData);
            return true; // ACK - accept the table
        })
    .BuildAsync();
```

The server automatically announces charset support and can receive TTABLE data when the client sends a translation table.

## Client side
```csharp
var telnet = await new TelnetInterpreterBuilder()
    .UseMode(TelnetInterpreter.TelnetMode.Client)
    .UseLogger(logger)
    .OnSubmit((data, encoding, telnet) => HandleSubmitAsync(data, encoding, telnet))
    .OnNegotiation((data) => WriteToNetworkAsync(data))
    .AddPlugin<CharsetProtocol>()
        .WithTTableSupport(true)
        .OnTTableRequested(async () => 
        {
            // Generate custom translation table
            // Return null to reject the request
            return BuildCustomTTable("custom-charset", "utf-8");
        })
    .BuildAsync();
```

## Programmatic TTABLE API
You can also send TTABLE messages programmatically:

```csharp
// Get the charset plugin
var charsetPlugin = telnet.PluginManager!.GetPlugin<CharsetProtocol>();

// Send a TTABLE-IS message
var ttableData = BuildTTableVersion1("my-charset", "utf-8", translationMap);
await charsetPlugin!.SendTTableAsync(ttableData);

// Reject a TTABLE request
await charsetPlugin!.SendTTableRejectedAsync();
```

## TTABLE version 1 format
The TTABLE version 1 format is defined in RFC 2066:
- **Version byte**: Always 1 for version 1
- **Separator**: Single byte separator character (e.g., ';' or ' ')
- **Charset 1 name**: ASCII string terminated by separator
- **Size 1**: 1 byte indicating bits per character (typically 8)
- **Count 1**: 3 bytes (network byte order) indicating number of characters in map
- **Charset 2 name**: ASCII string terminated by separator
- **Size 2**: 1 byte indicating bits per character
- **Count 2**: 3 bytes (network byte order) indicating number of characters in map
- **Map 1**: Translation from charset 1 to charset 2
- **Map 2**: Translation from charset 2 to charset 1

**Note:** TTABLE is an advanced feature. Most applications should use standard named character sets via the regular charset negotiation. TTABLE is primarily useful for legacy systems or specialized character mappings not available as standard encodings.

