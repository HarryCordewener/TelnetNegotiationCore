using System.Net.Sockets;
using TelnetNegotiationCore.Interpreters;
using System.IO.Pipelines;
using Pipelines.Sockets.Unofficial;
using System.Text;
using TelnetNegotiationCore.Models;
using Microsoft.Extensions.Logging;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Protocols;

namespace TelnetNegotiationCore.TestClient;

public class MockPipelineClient(ILogger<MockPipelineClient> logger)
{
	private async ValueTask WriteToOutputStreamAsync(byte[] arg, PipeWriter writer)
	{
		try
		{
			await writer.WriteAsync(new ReadOnlyMemory<byte>(arg), CancellationToken.None);
		}
		catch (ObjectDisposedException ode)
		{
			logger.LogError(ode, "Stream has been closed");
		}
	}

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
		var stream = client.GetStream();
		var pipe = StreamConnection.GetDuplex(stream, new PipeOptions());

		var telnet = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(WriteBackAsync)
			.OnNegotiation((x) => WriteToOutputStreamAsync(x, pipe.Output))
			.AddDefaultMUDProtocols()
			.BuildAsync();

		// Subscribe to protocol callbacks
		var gmcp = telnet.PluginManager!.GetPlugin<GMCPProtocol>();
		if (gmcp != null)
			gmcp.OnGMCPReceived = SignalGMCPAsync;

		var mssp = telnet.PluginManager!.GetPlugin<MSSPProtocol>();
		if (mssp != null)
			mssp.OnMSSPRequest = SignalMSSPAsync;

		var eor = telnet.PluginManager!.GetPlugin<EORProtocol>();
		if (eor != null)
			eor.OnPromptReceived = SignalPromptAsync;

		var backgroundTask = Task.Run(() => ReadFromPipeline(telnet, pipe.Input));

		while (true)
		{
			var read = Console.ReadLine() ?? string.Empty;
			if (telnet != null)
			{
				await telnet.SendPromptAsync(telnet?.CurrentEncoding.GetBytes(read) ?? []);
			}
		}
	}

	/// <summary>
	/// Read data coming from the server and interpret it.
	/// </summary>
	/// <param name="telnet">Interpreter</param>
	/// <param name="reader">The Pipeline Reader</param>
	/// <returns>A ValueTask</returns>
	static async ValueTask ReadFromPipeline(TelnetInterpreter telnet, PipeReader reader)
	{
		while (true)
		{
			// await some data being available
			var read = await reader.ReadAtLeastAsync(1);

			foreach(var segment in read.Buffer)
			{
				await telnet.InterpretByteArrayAsync(segment);
			}

			// tell the pipe that we used everything
			reader.AdvanceTo(read.Buffer.End);
		}
	}
}