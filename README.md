[![Discord](https://img.shields.io/discord/1193672869104861195?style=for-the-badge)](https://discord.gg/SK2cWERJF7) [![Build Status](https://img.shields.io/github/actions/workflow/status/HarryCordewener/TelnetNegotiationCore/dotnet.yml?style=for-the-badge)](https://github.com/HarryCordewener/TelnetNegotiationCore/actions/workflows/dotnet.yml) [![NuGet](https://img.shields.io/nuget/dt/TelnetNegotiationCore?style=for-the-badge&color=blue)](https://www.nuget.org/packages/TelnetNegotiationCore)
![Larger Logo](LargerLogo.png)

# Telnet Negotiation Core
## Summary 
This project provides a library that implements telnet functionality, and as many of its RFCs are viable, in a testable manner. 
This is done with an eye on MUDs at this time, but may improve to support more terminal capabilities as time permits and if there is ask for it.

At this time, this repository is in a rough state and does not yet implement some common modern code standards, but is constantly evolving.

## State
This library is in a state where breaking changes to the interface are expected.

## Support
| RFC                                                 | Description                        | Supported  | Comments           |
| --------------------------------------------------- | ---------------------------------- |------------| ------------------ |
| [RFC 855](http://www.faqs.org/rfcs/rfc855.html)     | Telnet Option Specification        | Full       |                    |
| [RFC 1091](http://www.faqs.org/rfcs/rfc1091.html)   | Terminal Type Negotiation          | Full       |                    |
| [MTTS](https://tintin.mudhalla.net/protocols/mtts)  | MTTS Negotiation (Extends TTYPE)   | Full       |                    |
| [RFC 1073](http://www.faqs.org/rfcs/rfc1073.html)   | Window Size Negotiation (NAWS)     | Full       |                    |
| [GMCP](https://tintin.mudhalla.net/protocols/gmcp)  | Generic Mud Communication Protocol | Full       |                    |
| [MSSP](https://tintin.mudhalla.net/protocols/mssp)  | MSSP Negotiation                   | Full       | Untested           |
| [RFC 885](http://www.faqs.org/rfcs/rfc885.html)     | End Of Record Negotiation          | Full       | Untested           | 
| [EOR](https://tintin.mudhalla.net/protocols/eor)    | End Of Record Negotiation          | Full       | Untested           |
| [MSDP](https://tintin.mudhalla.net/protocols/msdp)  | Mud Server Data Protocol           | Partial    | Partial Tested     |
| [RFC 2066](http://www.faqs.org/rfcs/rfc2066.html)   | Charset Negotiation                | Partial    | No TTABLE support  |
| [RFC 858](http://www.faqs.org/rfcs/rfc858.html)     | Suppress GOAHEAD Negotiation       | Full       | Untested           |
| [RFC 1572](http://www.faqs.org/rfcs/rfc1572.html)   | New Environment Negotiation        | No         | Planned            |
| [MNES](https://tintin.mudhalla.net/protocols/mnes)  | Mud New Environment Negotiation    | No         | Planned            |
| [MCCP](https://tintin.mudhalla.net/protocols/mccp)  | Mud Client Compression Protocol    | No         | Rejects            |
| [RFC 1950](https://tintin.mudhalla.net/rfc/rfc1950) | ZLIB Compression                   | No         | Rejects            |
| [RFC 857](http://www.faqs.org/rfcs/rfc857.html)     | Echo Negotiation                   | No         | Rejects            |
| [RFC 1079](http://www.faqs.org/rfcs/rfc1079.html)   | Terminal Speed Negotiation         | No         | Rejects            |
| [RFC 1372](http://www.faqs.org/rfcs/rfc1372.html)   | Flow Control Negotiation           | No         | Rejects            |
| [RFC 1184](http://www.faqs.org/rfcs/rfc1184.html)   | Line Mode Negotiation              | No         | Rejects            |
| [RFC 1096](http://www.faqs.org/rfcs/rfc1096.html)   | X-Display Negotiation              | No         | Rejects            |
| [RFC 1408](http://www.faqs.org/rfcs/rfc1408.html)   | Environment Negotiation            | No         | Rejects            | 
| [RFC 2941](http://www.faqs.org/rfcs/rfc2941.html)   | Authentication Negotiation         | No         | Rejects            |
| [RFC 2946](http://www.faqs.org/rfcs/rfc2946.html)   | Encryption Negotiation             | No         | Rejects            |

## ANSI Support, ETC?
Being a Telnet Negotiation Library, this library doesn't give support for extensions like ANSI, Pueblo, MXP, etc at this time.

## Use 
### Client
A documented example exists in the [TestClient Project](TelnetNegotiationCore.TestClient/MockPipelineClient.cs).

Initiate a logger. A Serilog logger is required by this library at this time.
```csharp
var log = new LoggerConfiguration()
  .Enrich.FromLogContext()
  .WriteTo.Console()
  .WriteTo.File(new CompactJsonFormatter(), "LogResult.log")
  .MinimumLevel.LogDebug()
  .CreateLogger();

Log.Logger = log;
```

Create functions that implement your desired behavior on getting a signal.
```csharp
private async ValueTask WriteToOutputStreamAsync(byte[] arg, StreamWriter writer)
{
  try 
  { 
    await writer.BaseStream.WriteAsync(arg, CancellationToken.None);
  }
  catch(ObjectDisposedException ode)
  {
    _Logger.LogInformation("Stream has been closed", ode);
  }
}

public static ValueTask WriteBackAsync(byte[] writeback, Encoding encoding) =>
  Task.Run(() => Console.WriteLine(encoding.GetString(writeback)));

public ValueTask SignalGMCPAsync((string module, string writeback) val, Encoding encoding) =>
  Task.Run(() => _Logger.LogDebug("GMCP Signal: {Module}: {WriteBack}", val.module, val.writeback));

public ValueTask SignalMSSPAsync(MSSPConfig val) =>
  Task.Run(() => _Logger.LogDebug("New MSSP: {@MSSP}", val));

public ValueTask SignalPromptAsync() =>
  Task.Run(() => _Logger.LogDebug("Prompt"));

public ValueTask SignalNAWSAsync(int height, int width) => 
  Task.Run(() => _Logger.LogDebug("Client Height and Width updated: {Height}x{Width}", height, width));
```

Initialize the Interpreter.
```csharp
var telnet = new TelnetInterpreter(TelnetInterpreter.TelnetMode.Client, _Logger.ForContext<TelnetInterpreter>())
{
  CallbackOnSubmitAsync = WriteBackAsync,
  CallbackNegotiationAsync = (x) => WriteToOutputStreamAsync(x, output),
  SignalOnGMCPAsync = SignalGMCPAsync,
  SignalOnMSSPAsync = SignalMSSPAsync,
  SignalOnNAWSAsync = SignalNAWSAsync,
  SignalOnPromptingAsync = SignalPromptAsync,
  CharsetOrder = new[] { Encoding.GetEncoding("utf-8"), Encoding.GetEncoding("iso-8859-1") }
}.BuildAsync();
```

### Sending GMCP Messages
Both clients and servers can send GMCP messages using the `SendGMCPCommand` method. The method takes a package name and JSON data.

```csharp
// Send a simple GMCP message
await telnet.SendGMCPCommand("Core.Hello", "{\"client\":\"MyClient\",\"version\":\"1.0\"}");

// Send character vitals
await telnet.SendGMCPCommand("Char.Vitals", "{\"hp\":1000,\"maxhp\":1500,\"mp\":500,\"maxmp\":800}");

// Send room information
await telnet.SendGMCPCommand("Room.Info", "{\"num\":12345,\"name\":\"A dark room\",\"area\":\"The Dungeon\"}");

// The telnet interpreter will automatically handle GMCP negotiation
// Messages will only be sent if the remote party supports GMCP
```

To receive GMCP messages, implement the `SignalOnGMCPAsync` callback as shown in the initialization example above.

Start interpreting.
```csharp
for (int currentByte = 0; currentByte != -1; currentByte = input.BaseStream.ReadByte())
{
  telnet.InterpretAsync((byte)currentByte).GetAwaiter().GetResult();
}
```

### Server
A documented example exists in the [TestServer Project](TelnetNegotiationCore.TestServer/KestrelMockServer.cs). 
This uses a Kestrel server to make the TCP handling easier.
```csharp
public class KestrelMockServer : ConnectionHandler
{
  private readonly ILogger _Logger;

  public KestrelMockServer(ILogger<KestrelMockServer> logger) : base()
  {
    Console.OutputEncoding = Encoding.UTF8;
    _Logger = logger;
  }

  private async ValueTask WriteToOutputStreamAsync(byte[] arg, PipeWriter writer)
  {
    try
    {
      await writer.WriteAsync(new ReadOnlyMemory<byte>(arg), CancellationToken.None);
    }
    catch (ObjectDisposedException ode)
    {
      _Logger.LogError(ode, "Stream has been closed");
    }
  }

  public ValueTask SignalGMCPAsync((string module, string writeback) val)
  {
    _Logger.LogDebug("GMCP Signal: {Module}: {WriteBack}", val.module, val.writeback);
    return ValueTask.CompletedTask;
  }

  public ValueTask SignalMSSPAsync(MSSPConfig val)
  {
    _Logger.LogDebug("New MSSP: {@MSSPConfig}", val);
    return ValueTask.CompletedTask;
  }

  public ValueTask SignalNAWSAsync(int height, int width)
  {
    _Logger.LogDebug("Client Height and Width updated: {Height}x{Width}", height, width);
    return ValueTask.CompletedTask;
  }

  private static async ValueTask SignalMSDPAsync(MSDPServerHandler handler, TelnetInterpreter telnet, string config) =>
    await handler.HandleAsync(telnet, config);

  public static async ValueTask WriteBackAsync(byte[] writeback, Encoding encoding, TelnetInterpreter telnet)
  {
    var str = encoding.GetString(writeback);
    if (str.StartsWith("echo"))
    {
      await telnet.SendAsync(encoding.GetBytes($"We heard: {str}" + Environment.NewLine));
    }
    Console.WriteLine(encoding.GetString(writeback));
  }

  private async ValueTask MSDPUpdateBehavior(string resetVariable)
  {
    _Logger.LogDebug("MSDP Reset Request: {@Reset}", resetVariable);
    await ValueTask.CompletedTask;
  }

  public async override ValueTask OnConnectedAsync(ConnectionContext connection)
  {
    using (_Logger.BeginScope(new Dictionary<string, object> { { "ConnectionId", connection.ConnectionId } }))
    {
      _Logger.LogInformation("{ConnectionId} connected", connection.ConnectionId);

      var MSDPHandler = new MSDPServerHandler(new MSDPServerModel(MSDPUpdateBehavior)
      {
        Commands = () => ["help", "stats", "info"],
        Configurable_Variables = () => ["CLIENT_NAME", "CLIENT_VERSION", "PLUGIN_ID"],
        Reportable_Variables = () => ["ROOM"],
        Sendable_Variables = () => ["ROOM"],
      });

      var telnet = await new TelnetInterpreter(TelnetInterpreter.TelnetMode.Server, _Logger)
      {
        CallbackOnSubmitAsync = WriteBackAsync,
        SignalOnGMCPAsync = SignalGMCPAsync,
        SignalOnMSSPAsync = SignalMSSPAsync,
        SignalOnNAWSAsync = SignalNAWSAsync,
        SignalOnMSDPAsync = (telnet, config) => SignalMSDPAsync(MSDPHandler, telnet, config),
        CallbackNegotiationAsync = (x) => WriteToOutputStreamAsync(x, connection.Transport.Output),
        CharsetOrder = new[] { Encoding.GetEncoding("utf-8"), Encoding.GetEncoding("iso-8859-1") }
      }
        .RegisterMSSPConfig(() => new MSSPConfig
        {
          Name = "My Telnet Negotiated Server",
          UTF_8 = true,
          Gameplay = ["ABC", "DEF"],
          Extended = new Dictionary<string, dynamic>
        {
          { "Foo",  "Bar"},
          { "Baz", (string[])["Moo", "Meow"] }
        }
        })
        .BuildAsync();

      while (true)
      {
        var result = await connection.Transport.Input.ReadAsync();
        var buffer = result.Buffer;

        foreach (var segment in buffer)
        {
          await telnet.InterpretByteArrayAsync(segment.Span.ToImmutableArray());
        }

        if (result.IsCompleted)
        {
          break;
        }

        connection.Transport.Input.AdvanceTo(buffer.End);
      }
      _Logger.LogInformation("{ConnectionId} disconnected", connection.ConnectionId);
    }
  }
}
```
