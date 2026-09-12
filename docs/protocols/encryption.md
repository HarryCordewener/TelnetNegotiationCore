# Encryption (RFC 2946)

The Encryption protocol (RFC 2946) provides a framework for negotiating telnet data stream encryption between client and server. This implementation supports extensible encryption through callbacks, allowing consumers to implement any encryption algorithm.

## Default behaviour: no encryption
By default, the protocol rejects all encryption types with NULL response, allowing sessions to proceed without encryption:

```csharp
var telnet = await new TelnetInterpreterBuilder()
    .UseMode(TelnetInterpreter.TelnetMode.Server)
    .UseLogger(logger)
    .OnSubmit((data, encoding, telnet) => HandleSubmitAsync(data, encoding, telnet))
    .OnNegotiation((data) => WriteToNetworkAsync(data))
    .AddPlugin<EncryptionProtocol>()
    .BuildAsync();

// Protocol auto-negotiates and rejects encryption with NULL
// Session continues without encryption
```

## Server side, with an algorithm of your own
Servers can provide custom encryption by specifying supported encryption types and handling client initialization:

```csharp
var telnet = await new TelnetInterpreterBuilder()
    .UseMode(TelnetInterpreter.TelnetMode.Server)
    .UseLogger(logger)
    .OnSubmit((data, encoding, telnet) => HandleSubmitAsync(data, encoding, telnet))
    .OnNegotiation((data) => WriteToNetworkAsync(data))
    .AddPlugin<EncryptionProtocol>()
        // Declare which encryption types to offer
        .WithEncryptionTypes(async () => new List<byte>
        {
            1,  // DES_CFB64
            3   // DES3_CFB64
        })
        // Handle client encryption initialization
        .OnEncryptionRequest(async (encData) =>
        {
            // The subnegotiation body exactly as it arrived, command byte first.
            var command = encData[0];                 // 0 = IS
            var encType = encData[1];
            var initData = encData.Skip(2).ToArray();
            
            logger.LogInformation("Received encryption type {Type} with {Bytes} bytes of init data", 
                encType, initData.Length);
            
            // Initialize decryption and send REPLY if needed
            await InitializeDecryption(encType, initData);
        })
        .OnEncryptionStart(async (keyId) =>
        {
            logger.LogInformation("Encryption started with keyId {KeyId}", BitConverter.ToString(keyId));
            await ActivateDecryption();
        })
        .OnEncryptionEnd(async () =>
        {
            logger.LogInformation("Encryption ended");
            await DeactivateDecryption();
        })
    .BuildAsync();
```

## Client side, with an algorithm of your own
Clients can handle encryption requests from servers:

```csharp
var telnet = await new TelnetInterpreterBuilder()
    .UseMode(TelnetInterpreter.TelnetMode.Client)
    .UseLogger(logger)
    .OnSubmit((data, encoding, telnet) => HandleSubmitAsync(data, encoding, telnet))
    .OnNegotiation((data) => WriteToNetworkAsync(data))
    .AddPlugin<EncryptionProtocol>()
        // Handle server encryption support
        .OnEncryptionSupport(async (supportedTypes) =>
        {
            // The subnegotiation body: the SUPPORT command byte, then the types.
            logger.LogInformation("Server offers {Count} encryption types", supportedTypes.Length - 1);
            
            // Choose a type we can speak and provide initialization data
            if (supportedTypes.Skip(1).Contains((byte)1)) // DES_CFB64
            {
                var encType = (byte)1;
                var initData = await GenerateEncryptionInitData(encType);
                
                // Return encryption initialization: [encType, ...init data]
                var response = new List<byte> { encType };
                response.AddRange(initData);
                return response.ToArray();
            }
            
            // Return null to reject with NULL type
            return null;
        })
        .OnEncryptionStart(async (keyId) =>
        {
            logger.LogInformation("Encryption started with keyId {KeyId}", BitConverter.ToString(keyId));
            await ActivateEncryption();
        })
        .OnEncryptionEnd(async () =>
        {
            logger.LogInformation("Encryption ended");
            await DeactivateEncryption();
        })
    .BuildAsync();
```

## Programmatic API
The protocol also provides methods to send encryption messages programmatically:

```csharp
// Get the encryption plugin
var encPlugin = telnet.PluginManager!.GetPlugin<EncryptionProtocol>();

// Server: Send encryption support message with specific types
await encPlugin!.SendEncryptionSupportAsync(new List<byte>
{
    1,  // DES_CFB64
    3   // DES3_CFB64
});

// Client: Send encryption IS message to initialize
await encPlugin!.SendEncryptionIsAsync(new byte[] 
{ 
    1,              // DES_CFB64
    0x01, 0x02      // Init data
});

// Server: Send encryption REPLY message
await encPlugin!.SendEncryptionReplyAsync(new byte[]
{
    1,              // DES_CFB64  
    0x03, 0x04      // Reply data
});

// Either side (WILL side): Start encryption
await encPlugin!.SendEncryptionStartAsync(new byte[] { 0 }); // Default keyid

// Either side (WILL side): End encryption
await encPlugin!.SendEncryptionEndAsync();

// Either side (DO side): Request encryption start
await encPlugin!.SendEncryptionRequestStartAsync(new byte[] { 0 }); // Optional keyid

// Either side (DO side): Request encryption end
await encPlugin!.SendEncryptionRequestEndAsync();
```

## What the callbacks are handed

`OnEncryptionSupport` and `OnEncryptionRequest` receive the **subnegotiation body as it arrived on
the wire, command byte included** — `[SUPPORT, type, type, …]` and `[IS, type, …initialisation]`
respectively. What you return is the same shape without the command byte; the plugin writes that.

## Encryption types
Common encryption types defined in RFC 2946:
- **0**: NULL (no encryption)
- **1**: DES_CFB64
- **2**: DES_OFB64
- **3**: DES3_CFB64
- **4**: DES3_OFB64
- **8**: CAST5_40_CFB64
- **9**: CAST5_40_OFB64
- **10**: CAST128_CFB64
- **11**: CAST128_OFB64

## Encryption commands
- **IS (0)**: Sent by WILL side to initialize encryption type
- **SUPPORT (1)**: Sent by DO side with list of supported types
- **REPLY (2)**: Sent by DO side to continue initialization exchange
- **START (3)**: Sent by WILL side to begin encrypting data
- **END (4)**: Sent by WILL side to stop encrypting data
- **REQUEST-START (5)**: Sent by DO side to request encryption
- **REQUEST-END (6)**: Sent by DO side to request stopping encryption
- **ENC_KEYID (7)**: Verify encryption key identifier
- **DEC_KEYID (8)**: Verify decryption key identifier

## Use cases
- **Data confidentiality**: Protect telnet data stream from eavesdropping
- **Custom encryption**: Implement DES, 3DES, CAST, or any RFC 2946-compliant algorithm
- **Key management**: Support multiple encryption keys via key identifiers
- **Dynamic control**: Start/stop encryption on demand during session
- **Backward compatibility**: Defaults to NULL rejection when callbacks not configured

## Security considerations
Per RFC 2946, the ENCRYPT option used in isolation provides protection against passive attacks but not against active attacks. It should be used alongside the Authentication option (with ENCRYPT_USING_TELOPT modifier) to provide protection against active attacks that attempt to prevent encryption negotiation.

**Note:** This protocol provides the negotiation framework. Actual cryptographic encryption algorithms must be implemented in the callbacks using appropriate security libraries. Consider using modern alternatives like TLS for new implementations.

