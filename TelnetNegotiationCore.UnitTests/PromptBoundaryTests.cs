using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using TUnit.Core;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Protocols;

namespace TelnetNegotiationCore.UnitTests;

public class PromptBoundaryTests : BaseTest
{
	[Test]
	public async Task AnEorPromptDoesNotLeakIntoTheNextSubmittedLine()
	{
		var lines = new List<string>();

		var client = await BuildAndWaitAsync(
			new TelnetInterpreterBuilder()
				.UseMode(TelnetInterpreter.TelnetMode.Client)
				.UseLogger(logger)
				.OnSubmit((data, _, _) => { lines.Add(Encoding.ASCII.GetString(data)); return ValueTask.CompletedTask; })
				.OnNegotiation(_ => ValueTask.CompletedTask)
				.AddPlugin<EORProtocol>());

		await InterpretAndWaitAsync(client, new byte[] { 255, 251, 25 });
		await InterpretAndWaitAsync(client, Encoding.ASCII.GetBytes("HP:100>"));
		await InterpretAndWaitAsync(client, new byte[] { 255, 239 });
		await InterpretAndWaitAsync(client, Encoding.ASCII.GetBytes("You wave.\r\n"));

		await Assert.That(lines.Count).IsEqualTo(1);
		await Assert.That(lines[0]).IsEqualTo("You wave.");

		await client.DisposeAsync();
	}

	[Test]
	public async Task AGoAheadPromptDoesNotLeakIntoTheNextSubmittedLine()
	{
		var lines = new List<string>();

		var client = await BuildAndWaitAsync(
			new TelnetInterpreterBuilder()
				.UseMode(TelnetInterpreter.TelnetMode.Client)
				.UseLogger(logger)
				.OnSubmit((data, _, _) => { lines.Add(Encoding.ASCII.GetString(data)); return ValueTask.CompletedTask; })
				.OnNegotiation(_ => ValueTask.CompletedTask)
				.AddPlugin<SuppressGoAheadProtocol>());

		await InterpretAndWaitAsync(client, Encoding.ASCII.GetBytes("HP:100>"));
		await InterpretAndWaitAsync(client, new byte[] { 255, 249 });
		await InterpretAndWaitAsync(client, Encoding.ASCII.GetBytes("You wave.\r\n"));

		await Assert.That(lines.Count).IsEqualTo(1);
		await Assert.That(lines[0]).IsEqualTo("You wave.");

		await client.DisposeAsync();
	}

	[Test]
	public async Task APromptLeavesItsTextOnLastPromptBytes()
	{
		var client = await BuildAndWaitAsync(
			new TelnetInterpreterBuilder()
				.UseMode(TelnetInterpreter.TelnetMode.Client)
				.UseLogger(logger)
				.OnSubmit(NoOpSubmitCallback)
				.OnNegotiation(_ => ValueTask.CompletedTask)
				.AddPlugin<SuppressGoAheadProtocol>());

		await InterpretAndWaitAsync(client, Encoding.ASCII.GetBytes("HP:100>"));
		await InterpretAndWaitAsync(client, new byte[] { 255, 249 });

		await Assert.That(Encoding.ASCII.GetString(client.LastPromptBytes.Span)).IsEqualTo("HP:100>");

		await client.DisposeAsync();
	}
}
