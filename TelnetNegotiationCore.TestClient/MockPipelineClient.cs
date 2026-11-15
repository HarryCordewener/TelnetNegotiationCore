using System.Net.Sockets;
using TelnetNegotiationCore.Interpreters;
using System.Text;
using TelnetNegotiationCore.Models;
using Microsoft.Extensions.Logging;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Connections;
using TelnetNegotiationCore.WebSockets;
using TelnetNegotiationCore.Protocols;

namespace TelnetNegotiationCore.TestClient;

public class MockPipelineClient(ILogger<MockPipelineClient> logger, ITelnetInterpreterFactory telnetFactory)
{
	public static async ValueTask WriteBackAsync(byte[] writeback, Encoding encoding, TelnetInterpreter t) =>
		await Task.Run(() => Console.WriteLine(encoding.GetString(writeback.AsSpan())));

	public ValueTask SignalGMCPAsync((string module, string writeback) val)
	{
		logger.LogDebug("GMCP Signal: {Module}: {WriteBack}", val.module, val.writeback);
		return ValueTask.CompletedTask;
	}

	public ValueTask SignalMSSPAsync(MSSPConfig val)
	{
		logger.LogDebug("New MSSP: {@MSSPConfig}", val);
		return ValueTask.CompletedTask;
	}

	public async ValueTask SignalPromptAsync() =>
		await Task.Run(() => logger.LogDebug("Prompt"));

	/// <summary>The builder for one connection, shared by the TCP and WebSocket clients.</summary>
	private TelnetInterpreterBuilder CreateBuilder() =>
		telnetFactory.CreateBuilder()
			.OnSubmit(WriteBackAsync)
			.WithClientIdentity(new ClientIdentity("TNC-TESTCLIENT")
			{
				Version = "2.9.0",
				TerminalType = "XTERM",
				Mtts = MttsCapabilities.Ansi | MttsCapabilities.Colors256
			})
			.AddPlugin<NAWSProtocol>()
			.AddPlugin<GMCPProtocol>()
				.OnGMCPMessage(SignalGMCPAsync)
			.AddPlugin<MSSPProtocol>()
				.OnMSSP(SignalMSSPAsync)
			.AddPlugin<TerminalTypeProtocol>()
			.AddPlugin<CharsetProtocol>()
			.AddPlugin<EORProtocol>()
				.OnPrompt(SignalPromptAsync)
			.AddPlugin<SuppressGoAheadProtocol>();

	/// <summary>Telnet over TCP.</summary>
	public async Task StartAsync(string address, int port)
	{
		using var client = new TcpClient(address, port);
		await using var connection = await CreateBuilder().StartAsync(client);
		await PlayAsync(connection);
	}

	/// <summary>A WebSocket: <c>telnet.mudstandards.org</c>, no subprotocol for a plain bridge, or <c>gmcp</c>/<c>terminal</c> (Evennia).</summary>
	public async Task StartAsync(Uri uri)
	{
		using var socket = await TelnetWebSocketClient.ConnectAsync(uri, options =>
			// A ping every 30 seconds keeps proxies from timing an idle connection out.
			options.KeepAliveInterval = TimeSpan.FromSeconds(30));
		logger.LogInformation("Connected to {Uri} with subprotocol {Subprotocol}", uri, socket.SubProtocol ?? "(none)");
		await using var connection = await CreateBuilder().StartAsync(socket);
		await PlayAsync(connection);
	}

	/// <summary>Sends each console line until the input ends or the server closes the connection.</summary>
	private async Task PlayAsync(TelnetConnection connection)
	{
		// Log any unexpected errors from the network read loop. Completion completes when the server closes the
		// connection, and faults only when the transport fails.
		_ = connection.Completion.ContinueWith(
			t => logger.LogError(t.Exception, "Network read loop ended with an error"),
			TaskContinuationOptions.OnlyOnFaulted);

		var telnet = connection.Interpreter;
		while (!connection.Closed.IsCancellationRequested)
		{
			var read = Console.ReadLine();
			if (read is null) break; // end of input: leave, and close the connection
			await telnet.SendPromptAsync(telnet.CurrentEncoding.GetBytes(read));
		}

		await connection.CloseAsync();
	}
}
