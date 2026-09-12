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
// What this side will accept. The plugin sends it as the SUPPORT and then holds the peer to it, so
// the callback below is only ever reached for one of these types.
var offered = new List<byte>
{
    1,  // DES_CFB64
    3   // DES3_CFB64
};

var telnet = await new TelnetInterpreterBuilder()
    .UseMode(TelnetInterpreter.TelnetMode.Server)
    .UseLogger(logger)
    .OnSubmit((data, encoding, telnet) => HandleSubmitAsync(data, encoding, telnet))
    .OnNegotiation((data) => WriteToNetworkAsync(data))
    .AddPlugin<EncryptionProtocol>()
        // Declare which encryption types to offer
        .WithEncryptionTypes(() => new ValueTask<List<byte>>(offered))
        // Handle client encryption initialization
        .OnEncryptionRequest(async (encData) =>
        {
            // The subnegotiation body exactly as it arrived, command byte first. The type is
            // already known to be one of the two above — see "Only what you offered" below —
            // but the body still comes from a peer, so do not index it unchecked.
            if (encData.Length < 2) return;

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

## Only what you offered

**When `WithEncryptionTypes` is configured, an `IS` naming a type you did not offer never reaches
`OnEncryptionRequest`** — it is logged at `Warning` and dropped. That callback is where a consumer
initialises decryption, and initialising it for an algorithm this side never advertised is exactly
what this prevents. [Authentication](authentication.md#only-what-you-offered) does the same.

Configure no provider and there is nothing to enforce: the plugin advertises an empty list and
already rejects with NULL. A provider that *returns* an empty list is an advertisement saying you
accept nothing, and is enforced as such.

## What the callbacks are handed

`OnEncryptionSupport` and `OnEncryptionRequest` receive the **subnegotiation body as it arrived on
the wire, command byte included** — `[SUPPORT, type, type, …]` and `[IS, type, …initialisation]`
respectively. What you return is the same shape without the command byte; the plugin writes that.

## Encryption types

> **Every algorithm RFC 2946 names is legacy.** DES and CAST5-40 are broken outright, 3DES is
> withdrawn ([NIST SP 800-131A Rev. 2](https://csrc.nist.gov/pubs/sp/800/131/a/r2/final)), and all of
> them are unauthenticated CFB/OFB modes with no integrity check at all. Implement one only to talk
> to something that already speaks it. For anything new, run telnet over TLS, where the transport
> gives you an authenticated cipher suite and a key exchange rather than leaving both to you.

The types defined in RFC 2946, for interoperability:
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
- **Data confidentiality against a passive observer**, and only that: RFC 2946's modes are unauthenticated, so they do not detect a stream that has been altered
- **Interoperability**: Speak DES, 3DES or CAST to something that already does — see the warning above before choosing one for anything new
- **Key management**: Support multiple encryption keys via key identifiers
- **Dynamic control**: Start/stop encryption on demand during session
- **Backward compatibility**: Defaults to NULL rejection when callbacks not configured

## Security considerations
Per RFC 2946, the ENCRYPT option used in isolation provides protection against passive attacks but not against active attacks. It should be used alongside the Authentication option (with ENCRYPT_USING_TELOPT modifier) to provide protection against active attacks that attempt to prevent encryption negotiation.

**Note:** This protocol provides the negotiation framework. The cryptography is yours to implement in the callbacks, and none of the algorithms this option was designed around is fit for a new deployment — see the warning above. TLS underneath telnet is the shorter and safer answer.

