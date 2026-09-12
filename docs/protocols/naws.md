# NAWS — window size

`NAWSProtocol` implements RFC 1073: how wide and how tall the client's terminal is, and an update
every time that changes. It is the option a server needs before it can wrap a line honestly.

A server learns the client's window through `.OnNAWS((height, width) => ...)`, and reads the latest
values back from `telnet.ClientWidth` / `telnet.ClientHeight`.

A client **reports** its window with `NAWSProtocol.SendWindowSizeAsync`, including whenever the
terminal is resized:

```csharp
var naws = telnet.PluginManager!.GetPlugin<NAWSProtocol>();
await naws!.SendWindowSizeAsync(width: 132, height: 43);
```

Nothing goes out until the server has enabled NAWS with `DO NAWS` (RFC 1073 — an unsolicited
`SB NAWS` desyncs a strict server's parser); `NAWSProtocol.WindowSizeReportingEnabled` says whether
it has. Both dimensions are 16-bit **unsigned**, which is the option's whole reason for existing —
*"the 253 character height and width limitation is too low so the new option has a limit of 65535
characters"* — so anything from `0` to `NAWSProtocol.MaxWindowDimension` (65535) can be reported.
A value outside that range throws `ArgumentOutOfRangeException` rather than being truncated into
the two bytes the wire has.

