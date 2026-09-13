# MSDP — Mud Server Data Protocol

`MSDPProtocol` carries a tree of variables in its own byte encoding; `OnMSDPMessage` hands it to you
as JSON. A server answers `LIST`, `REPORT`, `SEND` and `RESET` through `MSDPServerHandler` — see
[writing a server](../guides/server.md) for the handler, and [GMCP](gmcp.md#msdp-over-gmcp-mog) for
the same data carried over GMCP instead.

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
