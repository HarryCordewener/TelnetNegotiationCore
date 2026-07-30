using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Protocols;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// MSSP at the byte level: how a field's bytes are turned back into text, what an escaped
/// <c>IAC IAC</c> inside a field means, and how much of a payload the library is willing to hold
/// for an untrusted peer.
/// </summary>
/// <remarks>
/// These are the wire concerns rather than the vocabulary concerns; the specification's rules about
/// variables, values and arrays are covered by <see cref="MSSPSpecificationTests"/>.
/// </remarks>
public class MSSPWireTests : BaseTest
{
	private static readonly byte[] WillMssp = [(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.MSSP];

	private static byte[] Subnegotiation(params byte[] payload) =>
		new[] { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.MSSP }
			.Concat(payload)
			.Concat([(byte)Trigger.IAC, (byte)Trigger.SE])
			.ToArray();

	/// <summary>
	/// A single variable with a single value, encoded with <paramref name="encoding"/>.
	/// </summary>
	private static byte[] Field(string name, string value, Encoding encoding) =>
		new[] { (byte)Trigger.MSSP_VAR }
			.Concat(encoding.GetBytes(name))
			.Concat([(byte)Trigger.MSSP_VAL])
			.Concat(encoding.GetBytes(value))
			.ToArray();

	/// <summary>
	/// A client that has agreed to MSSP and records every report it is handed.
	/// </summary>
	private static async Task<(TelnetInterpreter Client, List<MSSPConfig> Received)> ClientAsync(
		Action<PluginConfigurationContext<MSSPProtocol>> configure = null,
		bool withCharset = false)
	{
		var received = new List<MSSPConfig>();

		var builder = new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(_ => ValueTask.CompletedTask);

		if (withCharset)
		{
			builder = builder.AddPlugin<CharsetProtocol>();
		}

		var mssp = builder
			.AddPlugin<MSSPProtocol>()
			.OnMSSP(config =>
			{
				lock (received) received.Add(config);
				return ValueTask.CompletedTask;
			});

		configure?.Invoke(mssp);

		var client = await ((TelnetInterpreterBuilder)mssp).BuildAsync();

		await InterpretAndWaitAsync(client, WillMssp);
		return (client, received);
	}

	/// <summary>
	/// Drives the client through the server half of an RFC 2066 CHARSET negotiation, so that
	/// <see cref="TelnetInterpreter.CurrentEncoding"/> is <paramref name="webName"/> before MSSP
	/// arrives.
	/// </summary>
	private static async Task NegotiateCharsetAsync(TelnetInterpreter client, string webName)
	{
		await InterpretAndWaitAsync(client, [(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.CHARSET]);
		await InterpretAndWaitAsync(client, [(byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.CHARSET]);

		var accepted = new List<byte> { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.CHARSET, (byte)Trigger.ACCEPTED };
		accepted.AddRange(Encoding.ASCII.GetBytes(webName));
		accepted.AddRange([(byte)Trigger.IAC, (byte)Trigger.SE]);

		await InterpretAndWaitAsync(client, [.. accepted]);
		await PollUntilAsync(() => client.CurrentEncoding.WebName == webName);
	}

	private static async Task<MSSPConfig> ReceiveOneAsync(TelnetInterpreter client, List<MSSPConfig> received, byte[] bytes)
	{
		await client.InterpretByteArrayAsync(bytes);
		await client.WaitForProcessingAsync(maxWaitMs: 30000);
		await PollUntilAsync(() => received.Count > 0, timeoutMs: 30000);

		await Assert.That(received.Count).IsEqualTo(1);
		return received[0];
	}

	#region Encoding

	/// <summary>
	/// MSSP fixes no encoding. Its only byte-level rule is "For ease of parsing, variables and values
	/// cannot contain the MSSP_VAL, MSSP_VAR, IAC, or NUL byte", and it carries a CHARSET variable
	/// whose documented values are "ASCII, BIG5, CP437, CP949, CP1251, EUC-KR, GB18030, ISO-8859-1,
	/// ISO-8859-2, KOI8-R, UTF-8" — so a report is read with the encoding the connection is using,
	/// which for this library starts as UTF-8.
	/// </summary>
	[Test]
	public async Task ANonAsciiValueIsReadWithTheSessionEncoding()
	{
		var (client, received) = await ClientAsync();

		var config = await ReceiveOneAsync(client, received,
			Subnegotiation(Field("NAME", "Café Noir", Encoding.UTF8)));

		await Assert.That(config.Name).IsEqualTo("Café Noir");

		await client.DisposeAsync();
	}

	/// <summary>
	/// The same report under a negotiated ISO-8859-1 session: 0xE9 is é there and two bytes in UTF-8,
	/// so decoding with anything other than what CHARSET settled on produces different text.
	/// </summary>
	[Test]
	public async Task AfterCharsetNegotiationMsspUsesTheNegotiatedEncoding()
	{
		var latin1 = Encoding.GetEncoding("iso-8859-1");
		var (client, received) = await ClientAsync(withCharset: true);

		await NegotiateCharsetAsync(client, latin1.WebName);
		await Assert.That(client.CurrentEncoding.WebName).IsEqualTo(latin1.WebName);

		var config = await ReceiveOneAsync(client, received,
			Subnegotiation(Field("NAME", "Café Noir", latin1)));

		await Assert.That(config.Name).IsEqualTo("Café Noir");

		await client.DisposeAsync();
	}

	/// <summary>
	/// Every variable in a report shares one decoding, including the ones that reach typed
	/// properties and the ones that only reach <see cref="MSSPConfig.Extended"/>.
	/// </summary>
	[Test]
	public async Task EveryFieldOfAReportSharesTheSessionEncoding()
	{
		var (client, received) = await ClientAsync();

		var config = await ReceiveOneAsync(client, received, Subnegotiation(
			[.. Field("NAME", "Zeitgeist Mühle", Encoding.UTF8),
			 .. Field("LOCATION", "Köln", Encoding.UTF8),
			 .. Field("WORLD ORIGINALITY", "Все оригинально", Encoding.UTF8)]));

		await Assert.That(config.Name).IsEqualTo("Zeitgeist Mühle");
		await Assert.That(config.Location).IsEqualTo("Köln");
		await Assert.That(config.Variables.Default("WORLD ORIGINALITY")).IsEqualTo("Все оригинально");

		await client.DisposeAsync();
	}

	/// <summary>
	/// The write side reads the same way. A report is sent with the connection's encoding, so a
	/// server whose NAME is not ASCII puts its NAME on the wire rather than a row of question marks —
	/// and a report received from a peer and sent back on round-trips, which is what
	/// <see cref="MSSPSpecificationTests.AReceivedReportRoundTripsBackOntoTheWire"/> promises.
	/// </summary>
	[Test]
	public async Task ANonAsciiReportSurvivesBeingSentBackOut()
	{
		var sent = await SendAsync(new MSSPConfig { Name = "Café Noir", Location = "Köln" });

		var (client, received) = await ClientAsync();
		var config = await ReceiveOneAsync(client, received, sent);

		await Assert.That(config.Name).IsEqualTo("Café Noir");
		await Assert.That(config.Location).IsEqualTo("Köln");

		await client.DisposeAsync();
	}

	/// <summary>
	/// Under an encoding where one character is the IAC byte — ISO-8859-1 'ÿ' — the write side has to
	/// double it (RFC 854), or the value ends the subnegotiation early and desyncs the peer.
	/// </summary>
	[Test]
	public async Task AValueEncodingToAnIacByteIsDoubledOnTheWayOut()
	{
		var latin1 = Encoding.GetEncoding("iso-8859-1");
		var sent = await SendAsync(new MSSPConfig { Name = "aÿb" }, latin1);

		// Four IAC bytes: the opening IAC SB, the doubled data byte, and the closing IAC SE. Three
		// would mean the 0xFF went out bare, which is a subnegotiation that ends in the middle of NAME.
		await Assert.That(sent.Count(b => b == (byte)Trigger.IAC)).IsEqualTo(4);
		await Assert.That(sent[^2..]).IsEquivalentTo(new[] { (byte)Trigger.IAC, (byte)Trigger.SE });

		var (client, received) = await ClientAsync(withCharset: true);
		await NegotiateCharsetAsync(client, latin1.WebName);
		var config = await ReceiveOneAsync(client, received, sent);

		await Assert.That(config.Name).IsEqualTo("aÿb");

		await client.DisposeAsync();
	}

	/// <summary>
	/// Runs a server through <c>IAC DO MSSP</c> and returns the subnegotiation it answers with.
	/// </summary>
	private static async Task<byte[]> SendAsync(MSSPConfig config, Encoding encoding = null)
	{
		byte[] sent = null;

		var builder = new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(data =>
			{
				var bytes = data.ToArray();
				if (bytes.Length > 3 && bytes[1] == (byte)Trigger.SB && bytes[2] == (byte)Trigger.MSSP) sent = bytes;
				return ValueTask.CompletedTask;
			});

		if (encoding != null)
		{
			builder = builder.AddPlugin<CharsetProtocol>();
		}

		var server = await builder.AddPlugin<MSSPProtocol>().BuildAsync();

		server.PluginManager!.GetPlugin<MSSPProtocol>()!.SetMSSPConfig(() => config);

		if (encoding != null)
		{
			server.PluginManager!.GetPlugin<CharsetProtocol>()!.CharsetOrder = [encoding];

			await InterpretAndWaitAsync(server, [(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.CHARSET]);

			var request = new List<byte>
			{
				(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.CHARSET, (byte)Trigger.REQUEST, (byte)';'
			};
			request.AddRange(Encoding.ASCII.GetBytes(encoding.WebName));
			request.AddRange([(byte)Trigger.IAC, (byte)Trigger.SE]);

			await InterpretAndWaitAsync(server, [.. request]);
			await PollUntilAsync(() => server.CurrentEncoding.WebName == encoding.WebName);
			await Assert.That(server.CurrentEncoding.WebName).IsEqualTo(encoding.WebName);
		}

		await InterpretAndWaitAsync(server, [(byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MSSP]);
		await PollUntilAsync(() => sent != null);

		await Assert.That(sent).IsNotNull();
		await server.DisposeAsync();

		return sent;
	}

	#endregion

	#region Escaped IAC

	/// <summary>
	/// <c>IAC IAC</c> inside a subnegotiation is one literal 0xFF data byte (RFC 854: "the IAC need
	/// be doubled to be sent as data"). MSSP says a value "cannot contain the ... IAC ... byte", so a
	/// server sending one is malformed — but the framing rule still decides what the bytes mean, and
	/// the library must not quietly delete a byte the peer took the trouble to escape.
	/// </summary>
	[Test]
	public async Task AnEscapedIacInsideAValueKeepsTheLiteralByte()
	{
		var latin1 = Encoding.GetEncoding("iso-8859-1");
		var (client, received) = await ClientAsync(withCharset: true);

		await NegotiateCharsetAsync(client, latin1.WebName);

		var config = await ReceiveOneAsync(client, received, Subnegotiation(
			[(byte)Trigger.MSSP_VAR, .. latin1.GetBytes("NAME"),
			 (byte)Trigger.MSSP_VAL, (byte)'a', (byte)Trigger.IAC, (byte)Trigger.IAC, (byte)'b']));

		await Assert.That(config.Name).IsEqualTo("aÿb");

		await client.DisposeAsync();
	}

	/// <summary>
	/// The variable half of the same hole: <c>EscapingMSSPVar -&gt; EvaluatingMSSPVar</c> is the same
	/// un-escape, and drops the byte the same way.
	/// </summary>
	[Test]
	public async Task AnEscapedIacInsideAVariableNameKeepsTheLiteralByte()
	{
		var latin1 = Encoding.GetEncoding("iso-8859-1");
		var (client, received) = await ClientAsync(withCharset: true);

		await NegotiateCharsetAsync(client, latin1.WebName);

		var config = await ReceiveOneAsync(client, received, Subnegotiation(
			[(byte)Trigger.MSSP_VAR, (byte)'A', (byte)Trigger.IAC, (byte)Trigger.IAC, (byte)'B',
			 (byte)Trigger.MSSP_VAL, .. latin1.GetBytes("kept")]));

		await Assert.That(config.Variables.ContainsKey("AÿB")).IsTrue();
		await Assert.That(config.Variables["AÿB"]).IsEquivalentTo(new[] { "kept" });

		await client.DisposeAsync();
	}

	#endregion

	#region Payload ceiling

	/// <summary>
	/// A crawler connects to servers it does not trust by definition, so how much it is willing to
	/// allocate for one report cannot be the server's decision. The bound is over the whole
	/// subnegotiation payload, matching GMCP and MSDP.
	/// </summary>
	[Test]
	public async Task AReportBeyondTheDefaultCeilingIsDropped()
	{
		var (client, received) = await ClientAsync();

		// One value one byte past the 1 MiB default.
		var oversized = Subnegotiation(
			[(byte)Trigger.MSSP_VAR, .. Encoding.ASCII.GetBytes("NAME"),
			 (byte)Trigger.MSSP_VAL, .. Encoding.ASCII.GetBytes(new string('x', (1024 * 1024) + 1))]);

		await client.InterpretByteArrayAsync(oversized);
		await client.WaitForProcessingAsync(maxWaitMs: 120000);

		// A small, valid report behind it: when this one arrives, the oversized one has been through
		// the whole pipeline, so "nothing before it" is a real absence rather than a race.
		await client.InterpretByteArrayAsync(Subnegotiation(Field("NAME", "Small", Encoding.ASCII)));
		await client.WaitForProcessingAsync(maxWaitMs: 30000);
		await PollUntilAsync(() => received.Count > 0, timeoutMs: 30000);

		// Counted rather than compared, so a failure names how many oversized reports got through
		// instead of printing a megabyte of one.
		await Assert.That(received.Count(report => (report.Name?.Length ?? 0) > 1024 * 1024)).IsEqualTo(0);
		await Assert.That(received.Count).IsEqualTo(1);
		await Assert.That(received[0].Name).IsEqualTo("Small");

		await client.DisposeAsync();
	}

	/// <summary>
	/// The ceiling is configurable, and reaching it is reported rather than passed off as a complete
	/// report. The connection survives it.
	/// </summary>
	[Test]
	public async Task AReportBeyondTheConfiguredCeilingIsDroppedAndReported()
	{
		(long ReceivedBytes, int MaxMessageSize)? tooLarge = null;

		var (client, received) = await ClientAsync(mssp => mssp
			.WithMaxMessageSize(4096)
			.OnMSSPMessageTooLarge(overflow =>
			{
				tooLarge = overflow;
				return ValueTask.CompletedTask;
			}));

		// "NAME" + 8000 x's == 8004 payload bytes against a 4096 byte ceiling.
		await client.InterpretByteArrayAsync(Subnegotiation(
			[(byte)Trigger.MSSP_VAR, .. Encoding.ASCII.GetBytes("NAME"),
			 (byte)Trigger.MSSP_VAL, .. Encoding.ASCII.GetBytes(new string('x', 8000))]));
		await client.WaitForProcessingAsync(maxWaitMs: 30000);

		var reported = await PollUntilAsync(() => tooLarge != null, timeoutMs: 30000);
		if (!reported)
		{
			throw new Exception("Timeout waiting for the oversized-report callback.");
		}

		// Dropped, not truncated, and the consumer was told why.
		await Assert.That(received.Count).IsEqualTo(0);
		await Assert.That(tooLarge!.Value.MaxMessageSize).IsEqualTo(4096);
		await Assert.That(tooLarge!.Value.ReceivedBytes).IsEqualTo(8004);

		// The connection keeps working.
		await client.InterpretByteArrayAsync(Subnegotiation(Field("NAME", "Still here", Encoding.ASCII)));
		await client.WaitForProcessingAsync(maxWaitMs: 30000);

		var gotNext = await PollUntilAsync(() => received.Count > 0, timeoutMs: 30000);
		if (!gotNext)
		{
			throw new Exception("Timeout waiting for the report following an oversized one.");
		}

		await Assert.That(received[0].Name).IsEqualTo("Still here");

		await client.DisposeAsync();
	}

	/// <summary>
	/// The bound is on the subnegotiation, not on one field: a report made of many small variables
	/// costs the same memory as one large value and has to be bounded the same way.
	/// </summary>
	[Test]
	public async Task TheCeilingCountsTheWholePayloadNotASingleField()
	{
		(long ReceivedBytes, int MaxMessageSize)? tooLarge = null;

		var (client, received) = await ClientAsync(mssp => mssp
			.WithMaxMessageSize(1024)
			.OnMSSPMessageTooLarge(overflow =>
			{
				tooLarge = overflow;
				return ValueTask.CompletedTask;
			}));

		// 300 variables of 8 payload bytes each ("VAR" + 3-digit index, then "1") == 2100 bytes.
		var payload = new List<byte>();
		for (var i = 0; i < 300; i++)
		{
			payload.AddRange(Field($"VAR{i:D3}", "1", Encoding.ASCII));
		}

		await client.InterpretByteArrayAsync(Subnegotiation([.. payload]));
		await client.WaitForProcessingAsync(maxWaitMs: 30000);

		var reported = await PollUntilAsync(() => tooLarge != null, timeoutMs: 30000);
		if (!reported)
		{
			throw new Exception("Timeout waiting for the oversized-report callback.");
		}

		await Assert.That(received.Count).IsEqualTo(0);
		await Assert.That(tooLarge!.Value.ReceivedBytes).IsEqualTo(300 * 7);

		await client.DisposeAsync();
	}

	/// <summary>
	/// A report that exactly reaches the ceiling is not over it, and is delivered whole.
	/// </summary>
	[Test]
	public async Task AReportExactlyAtTheCeilingIsDelivered()
	{
		var (client, received) = await ClientAsync(mssp => mssp.WithMaxMessageSize(4096));

		// "NAME" + 4092 x's == exactly 4096 payload bytes.
		var value = new string('x', 4092);
		var config = await ReceiveOneAsync(client, received, Subnegotiation(
			[(byte)Trigger.MSSP_VAR, .. Encoding.ASCII.GetBytes("NAME"),
			 (byte)Trigger.MSSP_VAL, .. Encoding.ASCII.GetBytes(value)]));

		await Assert.That(config.Name).IsEqualTo(value);

		await client.DisposeAsync();
	}

	/// <summary>
	/// The ceiling is a byte count, so it must reject a value that cannot be one.
	/// </summary>
	[Test]
	public async Task MsspMaxMessageSizeRejectsNonPositiveValues()
	{
		var mssp = new MSSPProtocol();

		await Assert.That(mssp.MaxMessageSize).IsEqualTo(1024 * 1024);
		await Assert.That(() => mssp.WithMaxMessageSize(0)).Throws<ArgumentOutOfRangeException>();
		await Assert.That(() => mssp.WithMaxMessageSize(-1)).Throws<ArgumentOutOfRangeException>();
	}

	#endregion
}
