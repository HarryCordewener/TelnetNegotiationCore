# Authentication (RFC 2941)

The Authentication protocol (RFC 2941) provides a framework for negotiating authentication between client and server. This implementation supports extensible authentication through callbacks, allowing consumers to implement any authentication mechanism.

## Default behaviour: no authentication
By default, the protocol rejects all authentication types with NULL response, allowing sessions to proceed without authentication:

```csharp
var telnet = await new TelnetInterpreterBuilder()
    .UseMode(TelnetInterpreter.TelnetMode.Server)
    .UseLogger(logger)
    .OnSubmit((data, encoding, telnet) => HandleSubmitAsync(data, encoding, telnet))
    .OnNegotiation((data) => WriteToNetworkAsync(data))
    .AddPlugin<AuthenticationProtocol>()
    .BuildAsync();

// Protocol auto-negotiates and rejects authentication with NULL
// Session continues without authentication
```

## Server side, with a mechanism of your own
Servers can provide custom authentication by specifying supported authentication types and handling client responses:

```csharp
// What this side will accept. The plugin sends it as the SEND and then holds the peer to it, so
// the callback below is only ever reached for one of these pairs.
var offered = new List<(byte AuthType, byte Modifiers)>
{
    (5, 0),  // SRP with no modifiers
    (6, 2)   // RSA with AUTH_HOW_MUTUAL (0x02)
};

var telnet = await new TelnetInterpreterBuilder()
    .UseMode(TelnetInterpreter.TelnetMode.Server)
    .UseLogger(logger)
    .OnSubmit((data, encoding, telnet) => HandleSubmitAsync(data, encoding, telnet))
    .OnNegotiation((data) => WriteToNetworkAsync(data))
    .AddPlugin<AuthenticationProtocol>()
        // Declare which authentication types to offer
        .WithAuthenticationTypes(() => new ValueTask<List<(byte AuthType, byte Modifiers)>>(offered))
        // Handle client authentication responses
        .OnAuthenticationResponse(async (authData) =>
        {
            // The subnegotiation body exactly as it arrived, command byte first. The pair is
            // already known to be one of the two above — see "Only what you offered" below —
            // but the body still comes from a peer, so do not index it unchecked.
            if (authData.Length < 3) return;

            var command = authData[0];                    // 0 = IS
            var authType = authData[1];
            var modifiers = authData[2];
            var credentials = authData.Skip(3).ToArray();

            logger.LogInformation("Received authentication type {Type} with {Bytes} bytes of credentials", 
                authType, credentials.Length);
            
            // Validate credentials and send REPLY if needed
            var isValid = await ValidateCredentials(authType, credentials);
            
            if (!isValid)
                await RejectAsync(authType, modifiers);
        })
    .BuildAsync();
```

## Client side, with a mechanism of your own
Clients can handle authentication requests from servers:

```csharp
var telnet = await new TelnetInterpreterBuilder()
    .UseMode(TelnetInterpreter.TelnetMode.Client)
    .UseLogger(logger)
    .OnSubmit((data, encoding, telnet) => HandleSubmitAsync(data, encoding, telnet))
    .OnNegotiation((data) => WriteToNetworkAsync(data))
    .AddPlugin<AuthenticationProtocol>()
        // Handle server authentication requests
        .OnAuthenticationRequest(async (authTypePairs) =>
        {
            // The subnegotiation body: the SEND command byte, then (authType, modifiers) pairs.
            logger.LogInformation("Server offers {Count} authentication types", (authTypePairs.Length - 1) / 2);
            
            // Choose the first type you support and provide credentials. Never index without this:
            // the body comes from the peer, and a SEND with no pairs is three bytes shorter.
            if (authTypePairs.Length >= 3)
            {
                var authType = authTypePairs[1];
                var modifiers = authTypePairs[2];
                var credentials = await GetCredentials(authType);
                
                // Return auth response: [authType, modifiers, ...credentials]
                var response = new List<byte> { authType, modifiers };
                response.AddRange(credentials);
                return response.ToArray();
            }
            
            // Return null to reject with NULL type
            return null;
        })
    .BuildAsync();
```

## Programmatic API
The protocol also provides methods to send authentication messages programmatically:

```csharp
// Get the authentication plugin
var authPlugin = telnet.PluginManager!.GetPlugin<AuthenticationProtocol>();

// Server: Send authentication request with specific types
await authPlugin!.SendAuthenticationRequestAsync(new List<(byte, byte)>
{
    (5, 0),  // SRP
    (6, 2)   // RSA with mutual auth
});

// Client: Send authentication response
await authPlugin!.SendAuthenticationResponseAsync(new byte[] 
{ 
    5, 0,           // SRP, no modifiers
    0x01, 0x02, 0x03 // Credential data
});

// Server: Send authentication reply (accept/reject/challenge)
await authPlugin!.SendAuthenticationReplyAsync(new byte[]
{
    5, 0,           // SRP, no modifiers  
    0x00            // Accept status
});
```

**What a rejection looks like is the mechanism's business.** RFC 2941 defines the framing —
`IAC SB AUTHENTICATION REPLY <authType> <modifiers> <data> IAC SE` — and leaves `<data>` to the
mechanism: RFC 2942's Kerberos has its own `ACCEPT` and `REJECT` subcommands, and SRP and RSA have
theirs. This library writes whatever bytes you pass and interprets none of them, so a `0x00` / `0xFF`
accept-or-reject convention only exists if both ends agree it does:

```csharp
// Whatever the mechanism you are implementing defines as a rejection.
ValueTask RejectAsync(byte authType, byte modifiers) =>
    telnet.PluginManager!.GetPlugin<AuthenticationProtocol>()!
        .SendAuthenticationReplyAsync([authType, modifiers, .. RejectionFor(authType)]);
```

## Only what you offered

A peer answering `SEND` names the mechanism it has chosen, and nothing on the wire obliges it to
choose one you listed. **When `WithAuthenticationTypes` is configured, a pair you did not offer never
reaches `OnAuthenticationResponse`**: it is logged at `Warning` and dropped, before any credential
validation your callback would do. Honouring your own advertisement is this side's job, and doing it
in the plugin means a consumer cannot forget to.

Nothing is sent back when that happens. RFC 2941 leaves everything after the (type, modifiers) pair
to the mechanism, so there is no rejection this library could write that the peer would understand —
see below.

Configure no types and there is nothing to enforce: the plugin advertises an empty list and already
answers `IS NULL`, and the callback keeps seeing whatever arrives.

## What the callbacks are handed

Both callbacks receive the **subnegotiation body as it arrived on the wire, command byte included** —
not a parsed structure. `OnAuthenticationResponse` gets `[IS, authType, modifiers, …credentials]`,
`OnAuthenticationRequest` gets `[SEND, authType, modifiers, authType, modifiers, …]`. What you send
back is the same shape without the command byte: the plugin writes that for you.

## Authentication types and modifiers
Common authentication types defined in RFC 2941:
- **0**: NULL (no authentication)
- **1**: KERBEROS_V4
- **2**: KERBEROS_V5
- **5**: SRP (Secure Remote Password)
- **6**: RSA
- **7**: SSL

Common modifiers (combine with bitwise OR):
- **AUTH_WHO_MASK (0x01)**: Direction
  - 0x00: CLIENT_TO_SERVER
  - 0x01: SERVER_TO_CLIENT
- **AUTH_HOW_MASK (0x02)**: Method
  - 0x00: ONE_WAY
  - 0x02: MUTUAL
- **ENCRYPT_MASK (0x14)**: Encryption
  - 0x00: ENCRYPT_OFF
  - 0x04: ENCRYPT_USING_TELOPT
  - 0x10: ENCRYPT_AFTER_EXCHANGE
- **INI_CRED_FWD_MASK (0x08)**: Credential forwarding
  - 0x00: OFF
  - 0x08: ON

## Use cases
- **Custom authentication**: Implement Kerberos, SRP, RSA, or any RFC 2941-compliant mechanism
- **Pass/fail control**: Full control over credential validation and authentication status
- **Multi-round authentication**: Support challenge-response protocols using REPLY messages
- **Backward compatibility**: Defaults to NULL rejection when callbacks not configured

**Note:** This protocol provides the negotiation framework. Actual cryptographic authentication mechanisms must be implemented in the callbacks using appropriate security libraries.

## Implementing a mechanism

This page covers the negotiation framework. Worked implementations of Kerberos V4 and V5, SRP, RSA and
SSL/TLS on top of it are in
[Implementing an authentication mechanism](../guides/authentication-mechanisms.md).
