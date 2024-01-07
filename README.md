# Telnet Negotiation Core
## Summary
This project is intended to be a library that implements basic telnet functionality. 
This is done with an eye on MUDs at this time, but may improve to support more terminal capabilities as time permits.

At this time, this repository is in a rough state and does not yet implement some common modern code standards. 

## State
This library is in a state where breaking changes to the interface are expected.

## Support
| RFC                                         | Description                        | Supported  | Comments           |
| ------------------------------------------- | ---------------------------------- |------------| ------------------ |
| http://www.faqs.org/rfcs/rfc855.html        | Telnet Option Specification        | Full       |                    |
| http://www.faqs.org/rfcs/rfc1091.html       | Terminal Type Negotiation          | Full       |                    |
| https://tintin.mudhalla.net/protocols/mtts  | MTTS Negotiation (Extends TTYPE)   | Full       |                    |
| http://www.faqs.org/rfcs/rfc1073.html       | Window Size Negotiation (NAWS)     | Full       |                    |
| https://tintin.mudhalla.net/protocols/gmcp  | Generic Mud Communication Protocol | Full       |                    |
| https://tintin.mudhalla.net/protocols/mssp  | MSSP Negotiation (Extents 855)     | Full       | Untested           |
| http://www.faqs.org/rfcs/rfc885.html        | End Of Record Negotiation          | Full       | Untested, Error    | 
| https://tintin.mudhalla.net/protocols/eor   | End Of Record Negotiation          | Full       | Untested, Error    |
| http://www.faqs.org/rfcs/rfc858.html        | Suppress GOAHEAD Negotiation       | Full       | Untested           |
| https://tintin.mudhalla.net/protocols/msdp  | Mud Server Data Protocol           | Partial    | Planned            |
| http://www.faqs.org/rfcs/rfc2066.html       | Charset Negotiation                | Partial    | No TTABLE support  |
| http://www.faqs.org/rfcs/rfc1572.html       | New Environment Negotiation        | No         | Planned            |
| https://tintin.mudhalla.net/protocols/mnes  | Mud New Environment Negotiation    | No         | Planned            |
| https://tintin.mudhalla.net/protocols/mccp  | Mud Client Compression Protocol 	 | No         | Rejects            |
| https://tintin.mudhalla.net/rfc/rfc1950/    | ZLIB Compression                   | No         | Rejects            |
| http://www.faqs.org/rfcs/rfc857.html        | Echo Negotiation                   | No         | Rejects            |
| http://www.faqs.org/rfcs/rfc1079.html       | Terminal Speed Negotiation         | No         | Rejects            |
| http://www.faqs.org/rfcs/rfc1372.html       | Flow Control Negotiation           | No         | Rejects            |
| http://www.faqs.org/rfcs/rfc1184.html       | Line Mode Negotiation              | No         | Rejects            |
| http://www.faqs.org/rfcs/rfc1096.html       | X-Display Negotiation              | No         | Rejects            |
| http://www.faqs.org/rfcs/rfc1408.html       | Environment Negotiation            | No         | Rejects            | 
| http://www.faqs.org/rfcs/rfc2941.html       | Authentication Negotiation         | No         | Rejects            |
| http://www.faqs.org/rfcs/rfc2946.html       | Encryption Negotiation             | No         | Rejects            |

## ANSI Support, ETC?
Being a Telnet Negotiation Library, this library doesn't give support for extensions like ANSI, Pueblo, MXP, etc at this time.

## TODO:
Implement EOR correctly.

## Use 
### Client
A messy example exists in the TestClient Project.

Initiate a logger. A Serilog logger is required by this library at this time.
```csharp
	var log = new LoggerConfiguration()
		.Enrich.FromLogContext()
		.WriteTo.Console()
		.WriteTo.File(new CompactJsonFormatter(), "LogResult.log")
		.MinimumLevel.Debug()
		.CreateLogger();

	Log.Logger = log;
```

Create functions that implement your desired behavior on getting a signal.
```csharp
	private async Task WriteToOutputStreamAsync(byte[] arg, StreamWriter writer)
	{
		try 
		{ 
			await writer.BaseStream.WriteAsync(arg, CancellationToken.None);
		}
		catch(ObjectDisposedException ode)
		{
			_Logger.Information("Stream has been closed", ode);
		}
	}

	public static Task WriteBackAsync(byte[] writeback, Encoding encoding) =>
		Task.Run(() => Console.WriteLine(encoding.GetString(writeback)));

	public Task SignalGMCPAsync((string module, string writeback) val, Encoding encoding) =>
		Task.Run(() => _Logger.Debug("GMCP Signal: {Module}: {WriteBack}", val.module, val.writeback));

	public Task SignalMSSPAsync(MSSPConfig val) =>
		Task.Run(() => _Logger.Debug("New MSSP: {@MSSP}", val));

	public Task SignalPromptAsync() =>
		Task.Run(() => _Logger.Debug("Prompt"));

	public Task SignalNAWSAsync(int height, int width) => 
		Task.Run(() => _Logger.Debug("Client Height and Width updated: {Height}x{Width}", height, width));
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

Start interpreting.
```csharp
	for (int currentByte = 0; currentByte != -1; currentByte = input.BaseStream.ReadByte())
	{
		telnet.InterpretAsync((byte)currentByte).GetAwaiter().GetResult();
	}
```

### Server
A messy example exists in the TestServer Project.