# ENVIRON — environment variables (RFC 1408)

RFC 1408 is the original environment-variable option, and `EnvironProtocol` speaks it. It can be
activated on its own, and it coexists with `NewEnvironProtocol` (RFC 1572) on the same connection.

**RFC 1408 defines `USERVAR` as well as `VAR`; this plugin implements `VAR` only.** That is a
limitation of the implementation, not of the RFC — a `USERVAR` in an incoming `IS` is not surfaced,
and there is no way to send one. If you need user-defined variables, use
[`NewEnvironProtocol`](new-environ.md), where they are supported.

## Server side
```csharp
var telnet = await new TelnetInterpreterBuilder()
    .UseMode(TelnetInterpreter.TelnetMode.Server)
    .UseLogger(logger)
    .OnSubmit((data, encoding, telnet) => HandleSubmitAsync(data, encoding, telnet))
    .OnNegotiation((data) => WriteToNetworkAsync(data))
    .AddPlugin<EnvironProtocol>()
        .OnEnvironmentVariables((envVars) => 
        {
            // envVars contains standard environment variables (USER, LANG, etc.)
            // The values come from the peer. Log the names, not the contents: RFC 1408's own
            // vocabulary includes USER, and a peer is free to put anything in any of them.
            logger.LogInformation("Received {EnvCount} environment variables: {Names}",
                envVars.Count, string.Join(", ", envVars.Keys));
            return ValueTask.CompletedTask;
        })
    .BuildAsync();
```

## Client side
The client answers a server's request for environment variables with **exactly what you configured,
and nothing else**. Configure nothing and it answers with an empty list — no `USER`, no `LANG`, and
in particular nothing read out of the environment of the process:

```csharp
// Option 1: send nothing (the default)
var telnet = await new TelnetInterpreterBuilder()
    .UseMode(TelnetInterpreter.TelnetMode.Client)
    .UseLogger(logger)
    .OnSubmit((data, encoding, telnet) => HandleSubmitAsync(data, encoding, telnet))
    .OnNegotiation((data) => WriteToNetworkAsync(data))
    .AddPlugin<EnvironProtocol>()
    .BuildAsync();

// Option 2: Configure custom environment variables
var telnet = await new TelnetInterpreterBuilder()
    .UseMode(TelnetInterpreter.TelnetMode.Client)
    .UseLogger(logger)
    .OnSubmit((data, encoding, telnet) => HandleSubmitAsync(data, encoding, telnet))
    .OnNegotiation((data) => WriteToNetworkAsync(data))
    .AddPlugin<EnvironProtocol>()
        .WithClientEnvironmentVariables(new Dictionary<string, string>
        {
            { "USER", "myusername" },
            { "LANG", "en_US.UTF-8" },
            { "TERM", "xterm-256color" }
        })
    .BuildAsync();
```

**Note:** `NewEnvironProtocol` (RFC 1572) is the option to use for user-defined variables and for
MNES. Both can be registered on one connection.

