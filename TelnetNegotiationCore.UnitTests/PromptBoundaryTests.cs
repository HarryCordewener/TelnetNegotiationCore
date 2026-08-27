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

	/// <summary>
	/// A prompt boundary closes the line the same way a submitted line does: nothing pinned about
	/// the drained bytes may outlive it. Here, a CHARSET change lands between the prompt and the
	/// next line; the next line must be tagged with the encoding in force when *it* arrived, not
	/// whatever was pinned by the prompt text before the boundary.
	/// </summary>
	[Test]
	public async Task ALineAfterAPromptBoundaryIsTaggedWithTheEncodingInForceWhenItArrives()
	{
		var lines = new List<(byte[] Data, Encoding Encoding)>();

		var client = await BuildAndWaitAsync(
			new TelnetInterpreterBuilder()
				.UseMode(TelnetInterpreter.TelnetMode.Client)
				.UseLogger(logger)
				.OnSubmit((data, encoding, _) => { lines.Add((data, encoding)); return ValueTask.CompletedTask; })
				.OnNegotiation(_ => ValueTask.CompletedTask)
				.AddPlugin<SuppressGoAheadProtocol>()
				.AddPlugin<CharsetProtocol>());

		await InterpretAndWaitAsync(client, Encoding.UTF8.GetBytes("HP:100>"));
		await InterpretAndWaitAsync(client, new byte[] { (byte)Trigger.IAC, (byte)Trigger.GA });

		await InterpretAndWaitAsync(client, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.CHARSET });
		await InterpretAndWaitAsync(client, new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.CHARSET });
		var accepted = new List<byte> { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.CHARSET, (byte)Trigger.ACCEPTED };
		accepted.AddRange(Encoding.ASCII.GetBytes("iso-8859-1"));
		accepted.AddRange(new byte[] { (byte)Trigger.IAC, (byte)Trigger.SE });
		await InterpretAndWaitAsync(client, accepted.ToArray());

		await Assert.That(client.CurrentEncoding.WebName).IsEqualTo("iso-8859-1");

		var latin1 = Encoding.GetEncoding("iso-8859-1");
		await InterpretAndWaitAsync(client, latin1.GetBytes("Latin-1 à\r\n"));

		await Assert.That(lines.Count).IsEqualTo(1);
		await Assert.That(lines[0].Encoding.WebName).IsEqualTo("iso-8859-1");
		await Assert.That(latin1.GetString(lines[0].Data)).IsEqualTo("Latin-1 à");

		await client.DisposeAsync();
	}

	/// <summary>
	/// A prompt boundary closes the line the same way a submitted line does, including the
	/// overflow flag: a line that passed <see cref="TelnetInterpreter.MaxBufferSize"/> before its
	/// boundary arrived must not make the next, unrelated, ordinary-length line pay for it.
	/// </summary>
	[Test]
	public async Task ALineThatOverflowsBeforeItsPromptBoundaryDoesNotDropTheNextLine()
	{
		var lines = new List<string>();

		var client = await BuildAndWaitAsync(
			new TelnetInterpreterBuilder()
				.UseMode(TelnetInterpreter.TelnetMode.Client)
				.UseLogger(logger)
				.WithMaxBufferSize(8)
				.OnSubmit((data, encoding, _) => { lines.Add(encoding.GetString(data)); return ValueTask.CompletedTask; })
				.OnNegotiation(_ => ValueTask.CompletedTask)
				.AddPlugin<SuppressGoAheadProtocol>());

		// 20 bytes against an 8-byte MaxBufferSize: past the ceiling before the GA boundary arrives.
		await InterpretAndWaitAsync(client, Encoding.ASCII.GetBytes(new string('x', 20)));
		await InterpretAndWaitAsync(client, new byte[] { (byte)Trigger.IAC, (byte)Trigger.GA });

		await InterpretAndWaitAsync(client, Encoding.ASCII.GetBytes("hi\r\n"));

		await Assert.That(lines.Count).IsEqualTo(1);
		await Assert.That(lines[0]).IsEqualTo("hi");

		await client.DisposeAsync();
	}
}
