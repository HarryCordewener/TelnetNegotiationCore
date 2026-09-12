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
            logger.LogInformation("Received {EnvCount} environment variables", envVars.Count);
            foreach (var (key, value) in envVars)
            {
                logger.LogInformation("  {Key} = {Value}", key, value);
            }
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

