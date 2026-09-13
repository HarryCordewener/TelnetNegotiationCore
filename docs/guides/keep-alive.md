# Keep-alive

Off unless you ask for it. `WithKeepAlive()` sends `IAC NOP` after 30 seconds of outbound silence, which keeps NAT tables, load balancers and idle timers from tearing down a quiet connection:

```csharp
var (telnet, readTask) = await new TelnetInterpreterBuilder()
    .UseMode(TelnetInterpreter.TelnetMode.Server)
    .UseLogger(logger)
    .OnSubmit(HandleSubmitAsync)
    .WithKeepAlive()                              // IAC NOP after 30s of silence
    .AddDefaultMUDProtocols()
    .BuildAndStartAsync(connection.Transport);
```

The interval is an **idle** window, not a fixed heartbeat: every outbound write restarts it, so a connection that is already sending data never sends anything extra. Both the interval and the payload are configurable:

```csharp
    .WithKeepAlive(TimeSpan.FromSeconds(90))      // custom idle window

    .WithKeepAlive(TimeSpan.FromMinutes(2), async (telnet, token) =>
        await telnet.SendAsync(Encoding.ASCII.GetBytes("\r\n")))   // custom payload
```

The interval must be between **1 second** (`TelnetInterpreter.MinimumKeepAliveInterval`) and **24 hours** (`TelnetInterpreter.MaximumKeepAliveInterval`); the default is 30 seconds (`TelnetInterpreter.DefaultKeepAliveInterval`). Anything outside that range throws `ArgumentOutOfRangeException` rather than being quietly clamped, so a mistyped interval fails where you wrote it instead of running at a value you did not choose. Below a second a keep-alive is a flood — nothing it defends against, from NAT eviction to load balancer idle timeouts, is measured in anything shorter. The upper bound is the library's, not the network's — an idle timeout somewhere in the path may well be longer than a day, and a keep-alive above the maximum is refused rather than clamped, so a mistyped interval fails where you wrote it.

Works in both client and server mode. If the send throws — usually because the peer is gone — the exception is logged as a warning and the keep-alive stops for that connection. It is never rethrown onto your application and it does not disturb the byte-processing loop; you find out the connection is dead the same way you do today, from your read loop completing.

> **A NOP keep-alive is not peer-liveness detection.** A successful send only proves the local write succeeded — the peer is not required to answer, so a peer that has silently vanished is not noticed until TCP retransmission gives up. Confirming the peer is alive needs a round trip it must answer, such as TIMING-MARK (RFC 860, option 6), which this library does not implement.

