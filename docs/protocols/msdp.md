# MSDP — Mud Server Data Protocol

`MSDPProtocol` carries a tree of variables in its own byte encoding; `OnMSDPMessage` hands it to you
as JSON. A server answers `LIST`, `REPORT`, `SEND` and `RESET` through `MSDPServerHandler` — see
[writing a server](../guides/server.md) for the handler, and [GMCP](gmcp.md#msdp-over-gmcp-mog) for
the same data carried over GMCP instead.

The standard names are constants in `MsdpNames`, in the TelnetNegotiationCore.Gmcp package:
`MsdpNames.Command` for `LIST`, `REPORT` and the rest, `MsdpNames.List` for what `LIST` takes, and the
specification's reportable and configurable variables grouped as it groups them
(`MsdpNames.Character.Health`, `MsdpNames.Configurable.ClientName`). `MsdpNames.Configurable.ClientId`
is not standard: it is the name KaVir's snippet uses for `CLIENT_NAME`.

MSDP is a tree of variables on the wire, and the library translates between that tree and either
JSON or a type of yours. Nothing on the path reflects over a type, so it survives trimming and
`PublishAot`; a type of your own is carried by the contract the `System.Text.Json` source generator
writes for it.

```csharp
public sealed class Room
{
    [JsonPropertyName("VNUM")] public int Vnum { get; set; }
    [JsonPropertyName("NAME")] public string Name { get; set; } = "";
    [JsonPropertyName("EXITS")] public Dictionary<string, string> Exits { get; set; } = [];
}

// MSDP has no types beyond text, so numbers arrive as strings.
[JsonSourceGenerationOptions(NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(Room))]
public partial class MsdpJsonContext : JsonSerializerContext;
```

```csharp
// Your type, out as the payload of a subnegotiation, and back again.
var payload = MSDPLibrary.ReportVariables(room, encoding, MsdpJsonContext.Default.Room);
await telnet.SendMSDPPayloadAsync(payload);

var received = MSDPLibrary.Scan(payload, encoding, MsdpJsonContext.Default.Room);

// Or as JSON, which is what OnMSDPMessage hands you.
var json = MSDPLibrary.ScanToJson(payload, encoding);
```

## Sending reported variables once per tick

`NotifyChangeAsync` sends a reported variable straight away, so a game that changes HP five times
in a tick sends five messages. To send once per tick instead, call `MarkChanged` on each change and
`FlushChangesAsync` on the tick. The flush sends every marked variable in one message, and leaves
out a variable whose value is the same as the one last sent.

```csharp
player.Health -= damage;
model.MarkChanged("HEALTH");

// In the game loop, once per tick:
await model.FlushChangesAsync();
```

## The client side

`MSDPClientHandler` asks a server for variables and keeps the latest value of each one the server
sends. Several arguments are sent as repeated values, the form the specification shows:
`MSDP_VAR "REPORT" MSDP_VAL "HEALTH" MSDP_VAL "HEALTH_MAX"`.

```csharp
var msdp = new MSDPClientHandler { OnVariableAsync = (name, value) => ShowAsync(name, value) };

.AddPlugin<MSDPProtocol>().OnMSDPMessage(msdp.HandleAsync)

await msdp.ListAsync(telnet, "REPORTABLE_VARIABLES");
await msdp.ReportAsync(telnet, "HEALTH", "HEALTH_MAX");
await msdp.SetAsync(telnet, "CLIENT_NAME", "MyClient");

var health = msdp.Variables["HEALTH"];
```

Requests go over GMCP as MSDP over GMCP when the server agreed to GMCP and not to MSDP.
