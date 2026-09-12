# ENVIRON — environment variables (RFC 1408)

The ENVIRON protocol (RFC 1408) is the original environment variable negotiation protocol. It's simpler than NEW-ENVIRON and supports only basic environment variables (no user variables). This protocol can be activated in isolation.

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

**Note:** If you need user-defined variables or more advanced features, use `NewEnvironProtocol` (RFC 1572) instead. Both protocols can coexist if needed.

