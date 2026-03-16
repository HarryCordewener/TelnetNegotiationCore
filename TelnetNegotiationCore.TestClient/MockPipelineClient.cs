using System.Net.Sockets;
using TelnetNegotiationCore.Interpreters;
using System.Text;
using TelnetNegotiationCore.Models;
using Microsoft.Extensions.Logging;
using TelnetNegotiationCore.Builders;
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

	public async Task StartAsync(string address, int port)
	{
		var client = new TcpClient(address, port);

		var (telnet, readTask) = await telnetFactory.CreateBuilder()
			.OnSubmit(WriteBackAsync)
			.AddPlugin<NAWSProtocol>()
			.AddPlugin<GMCPProtocol>()
				.OnGMCPMessage(SignalGMCPAsync)
			.AddPlugin<MSSPProtocol>()
				.OnMSSP(SignalMSSPAsync)
			.AddPlugin<TerminalTypeProtocol>()
			.AddPlugin<CharsetProtocol>()
			.AddPlugin<EORProtocol>()
				.OnPrompt(SignalPromptAsync)
			.AddPlugin<SuppressGoAheadProtocol>()
			.BuildAndStartAsync(client);

		// readTask completes when the server closes the connection.
		// Log any unexpected errors from the network read loop.
		_ = readTask.ContinueWith(
			t => logger.LogError(t.Exception, "Network read loop ended with an error"),
			TaskContinuationOptions.OnlyOnFaulted);

		while (true)
		{
			var read = Console.ReadLine() ?? string.Empty;
			await telnet.SendPromptAsync(telnet.CurrentEncoding.GetBytes(read));
		}
	}
}