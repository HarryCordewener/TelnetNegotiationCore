# NEW-ENVIRON and MNES

The NEW-ENVIRON protocol (RFC 1572) allows exchange of environment variables between client and server. MNES (Mud New Environment Standard) extends this with the MTTS flag 512.

## Server side
```csharp
var telnet = await new TelnetInterpreterBuilder()
    .UseMode(TelnetInterpreter.TelnetMode.Server)
    .UseLogger(logger)
    .OnSubmit((data, encoding, telnet) => HandleSubmitAsync(data, encoding, telnet))
    .OnNegotiation((data) => WriteToNetworkAsync(data))
    .AddPlugin<NewEnvironProtocol>()
        .OnEnvironmentVariables((envVars, userVars) => 
        {
            // envVars contains standard environment variables (USER, LANG, etc.)
            // userVars contains user-defined variables
            // The values come from the peer. Log the names, not the contents — MNES carries
            // CLIENT_NAME and IPADDRESS, and RFC 1572's USER is whatever the peer decided to send.
            logger.LogInformation("Received {EnvCount} environment variables: {Names}",
                envVars.Count, string.Join(", ", envVars.Keys));
            return ValueTask.CompletedTask;
        })
    .BuildAsync();
```

## Client side
The client answers a server's `SEND` with **exactly what you configured, and nothing else**. Nothing
is read from the environment of the process: RFC 1572's `USER` means *the account to log in as*, not
the operating-system account the client happens to run under, and a server has no business learning
the latter.

Most of what a MUD client wants to send here is its identity, so set that once and it feeds both
NEW-ENVIRON and TTYPE:

```csharp
var telnet = await new TelnetInterpreterBuilder()
    .UseMode(TelnetInterpreter.TelnetMode.Client)
    .UseLogger(logger)
    .OnSubmit((data, encoding, telnet) => HandleSubmitAsync(data, encoding, telnet))
    .OnNegotiation((data) => WriteToNetworkAsync(data))
    .WithClientIdentity(new ClientIdentity("MY-CLIENT") { Version = "1.0.0" })
    .AddPlugin<NewEnvironProtocol>()
    .BuildAsync();

// The server receives CLIENT_NAME, CLIENT_VERSION and the calculated MTTS when it asks.
```

Anything else — including a `USER` your application has genuinely decided to send — goes in the
variable map, where an entry overrides the identity-derived one of the same name:

```csharp
    .AddPlugin<NewEnvironProtocol>()
        .WithClientEnvironmentVariables(new Dictionary<string, string>
        {
            { "CHARSET", "UTF-8" },
            { "WORD_WRAP", "OFF" }
        })
```

Configure neither and a `SEND` is answered with an empty `IS`, which leaves the server in the same
position as one talking to a client that never negotiated NEW-ENVIRON at all.

A server may ask for particular variables rather than all of them, and the reply answers the request
that was made: only the variables it named, in the order it named them. A name you did not configure
comes back *undefined* — the name with no value, which is RFC 1572's way of saying "I have none" —
so a server that asks for `USER` is told there isn't one rather than being handed the account name
of whoever is running the client. A variable you configured with an empty string is a different
answer: it is defined, and empty.

## MNES support
MNES (Mud New Environment Standard) defines the variable names above — `CLIENT_NAME`,
`CLIENT_VERSION`, `TERMINAL_TYPE`, `MTTS`, `CHARSET`, `IPADDRESS` — and is advertised with MTTS flag
512. That flag is set for you, and only when it is true: registering a `NewEnvironProtocol` plugin is
what makes this connection able to answer MNES, so that is exactly when the bit goes out. See
[Saying who your application is](../guides/client-identity.md).


## Escaped type bytes

The four type bytes — `VAR` (0), `VALUE` (1), `ESC` (2) and `USERVAR` (3) — cannot appear
literally inside a name or a value, because a receiver would read them as structure. RFC 1572
therefore escapes each with a preceding `ESC`: a literal `VAR` is sent as `ESC VAR`, and an
`ESC` itself as `ESC ESC`. `IAC` is doubled separately, as everywhere else.

Both directions are handled for you. Names and values you supply are escaped on the way out, and a
peer's escapes are decoded on the way in, so a callback receives the bytes the peer meant rather
than the bytes it sent. Two cases the RFC leaves open are resolved the way libtelnet resolves them:
an `ESC` before a byte that did not need escaping is consumed and the byte delivered literally,
and an `ESC` immediately before `IAC SE` escapes nothing and is consumed.

Decoding on receive was added after the fact — see
[#110](https://github.com/HarryCordewener/TelnetNegotiationCore/issues/110). Before that an escaped
type byte was read as a real marker, which split the value it was inside. MNES forbids these bytes in
names and values, which is why it went unnoticed for so long; a general telnet peer is under no such
restriction.
