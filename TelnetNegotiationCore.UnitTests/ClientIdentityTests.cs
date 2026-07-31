using TUnit.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Protocols;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// How an application introduces itself: one <see cref="ClientIdentity"/> feeding both the TTYPE
/// first response (MTTS calls it the client name) and the MNES CLIENT_NAME variable.
/// See issues #70 and #71.
/// </summary>
public class ClientIdentityTests : BaseTest
{
	private static byte[] TTypeSend =>
	[
		(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TTYPE, (byte)Trigger.SEND,
		(byte)Trigger.IAC, (byte)Trigger.SE
	];

	private static byte[] TTypeIs(string value) =>
	[
		(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TTYPE, (byte)Trigger.IS,
		.. Encoding.ASCII.GetBytes(value),
		(byte)Trigger.IAC, (byte)Trigger.SE
	];

	[Test]
	public async Task ConfiguredClientNameIsTheFirstTerminalTypeResponse()
	{
		byte[] negotiation = null;

		var client = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(data => { negotiation = data.ToArray(); return ValueTask.CompletedTask; })
			.WithClientIdentity(new ClientIdentity("MUINDEX-CRAWLER"))
			.AddPlugin<TerminalTypeProtocol>()
			.BuildAsync();

		await InterpretAndWaitAsync(client, [(byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.TTYPE]);
		negotiation = null;

		await InterpretAndWaitAsync(client, TTypeSend);

		await AssertByteArraysEqual(negotiation, TTypeIs("MUINDEX-CRAWLER"));

		await client.DisposeAsync();
	}

	[Test]
	public async Task IdentityIsAlsoReachableAfterAddingAPlugin()
	{
		byte[] negotiation = null;

		var client = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(data => { negotiation = data.ToArray(); return ValueTask.CompletedTask; })
			.AddPlugin<TerminalTypeProtocol>()
				.WithClientIdentity(new ClientIdentity("SHARPMUTERM") { Version = "0.1" })
			.BuildAsync();

		await InterpretAndWaitAsync(client, [(byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.TTYPE]);
		negotiation = null;

		await InterpretAndWaitAsync(client, TTypeSend);

		await AssertByteArraysEqual(negotiation, TTypeIs("SHARPMUTERM"));

		await client.DisposeAsync();
	}

	/// <summary>
	/// Nothing configured must not become a claim about the application. The client is UNKNOWN —
	/// RFC 1091's own word for a terminal that will not name itself — with no terminal type, and the
	/// only MTTS bit set is the one the library can see for itself: it really is decoding UTF-8.
	/// </summary>
	[Test]
	public async Task UnconfiguredClientReportsUnknownAndOnlyWhatTheLibraryCanSee()
	{
		var negotiations = new List<byte[]>();

		var client = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(data => { negotiations.Add(data.ToArray()); return ValueTask.CompletedTask; })
			.AddPlugin<TerminalTypeProtocol>()
			.BuildAsync();

		await InterpretAndWaitAsync(client, [(byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.TTYPE]);
		negotiations.Clear();

		await InterpretAndWaitAsync(client, TTypeSend);
		await InterpretAndWaitAsync(client, TTypeSend);
		await InterpretAndWaitAsync(client, TTypeSend);

		await AssertByteArraysEqual(negotiations[0], TTypeIs("UNKNOWN"));
		await AssertByteArraysEqual(negotiations[1], TTypeIs("MTTS 4"));
		await AssertByteArraysEqual(negotiations[2], TTypeIs("MTTS 4"));

		var everythingSent = Encoding.ASCII.GetString(negotiations.SelectMany(x => x).ToArray());
		await Assert.That(everythingSent).DoesNotContain("TNC");
		await Assert.That(everythingSent).DoesNotContain("XTERM");

		await client.DisposeAsync();
	}

	/// <summary>
	/// The MTTS bitvector is calculated, not stated: MNES (512) is claimed exactly when this
	/// connection has a NEW-ENVIRON plugin to answer MNES with.
	/// </summary>
	[Test]
	public async Task TheMnesBitIsCalculatedFromTheProtocolsInUse()
	{
		var negotiations = new List<byte[]>();

		var client = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(data => { negotiations.Add(data.ToArray()); return ValueTask.CompletedTask; })
			.AddPlugin<TerminalTypeProtocol>()
			.AddPlugin<NewEnvironProtocol>()
			.BuildAsync();

		await InterpretAndWaitAsync(client, [(byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.TTYPE]);
		negotiations.Clear();

		await InterpretAndWaitAsync(client, TTypeSend);
		await InterpretAndWaitAsync(client, TTypeSend);

		await AssertByteArraysEqual(negotiations[0], TTypeIs("UNKNOWN"));
		await AssertByteArraysEqual(negotiations[1], TTypeIs("MTTS 516"));

		var ttype = client.PluginManager!.GetPlugin<TerminalTypeProtocol>()!;
		await Assert.That(ttype.ClientCapabilities)
			.IsEqualTo(MttsCapabilities.Utf8 | MttsCapabilities.Mnes);

		await client.DisposeAsync();
	}

	[Test]
	public async Task TerminalTypeAndMttsAreReportedOnlyWhenClaimed()
	{
		var negotiations = new List<byte[]>();

		var client = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(data => { negotiations.Add(data.ToArray()); return ValueTask.CompletedTask; })
			.WithClientIdentity(new ClientIdentity("MUINDEX-CRAWLER")
			{
				TerminalType = "XTERM",
				Mtts = MttsCapabilities.Ansi | MttsCapabilities.Truecolor
			})
			.AddPlugin<TerminalTypeProtocol>()
			.BuildAsync();

		await InterpretAndWaitAsync(client, [(byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.TTYPE]);
		negotiations.Clear();

		await InterpretAndWaitAsync(client, TTypeSend);
		await InterpretAndWaitAsync(client, TTypeSend);
		await InterpretAndWaitAsync(client, TTypeSend);

		await AssertByteArraysEqual(negotiations[0], TTypeIs("MUINDEX-CRAWLER"));
		await AssertByteArraysEqual(negotiations[1], TTypeIs("XTERM"));
		// 257 claimed (ANSI + TRUECOLOR), 4 observed (UTF-8).
		await AssertByteArraysEqual(negotiations[2], TTypeIs("MTTS 261"));

		await client.DisposeAsync();
	}

	[Test]
	public async Task ExplicitTerminalTypesReplaceTheIdentityDerivedList()
	{
		var negotiations = new List<byte[]>();

		var client = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(data => { negotiations.Add(data.ToArray()); return ValueTask.CompletedTask; })
			.WithClientIdentity(new ClientIdentity("IGNORED") { TerminalType = "XTERM" })
			.AddPlugin<TerminalTypeProtocol>()
				.WithTerminalTypes("MUINDEX-CRAWLER", "MUINDEX", "MTTS 9")
			.BuildAsync();

		await InterpretAndWaitAsync(client, [(byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.TTYPE]);
		negotiations.Clear();

		await InterpretAndWaitAsync(client, TTypeSend);
		await InterpretAndWaitAsync(client, TTypeSend);
		await InterpretAndWaitAsync(client, TTypeSend);

		await AssertByteArraysEqual(negotiations[0], TTypeIs("MUINDEX-CRAWLER"));
		await AssertByteArraysEqual(negotiations[1], TTypeIs("MUINDEX"));
		await AssertByteArraysEqual(negotiations[2], TTypeIs("MTTS 9"));

		await client.DisposeAsync();
	}

	/// <summary>
	/// The same fact, reported down both channels: TTYPE says the client name because MTTS defines
	/// the first response that way, and MNES says CLIENT_NAME because that is its name for it.
	/// </summary>
	[Test]
	public async Task OneIdentityFeedsBothTerminalTypeAndMnes()
	{
		byte[] negotiation = null;

		var client = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(data => { negotiation = data.ToArray(); return ValueTask.CompletedTask; })
			.WithClientIdentity(new ClientIdentity("MUINDEX-CRAWLER")
			{
				Version = "1.2.0",
				TerminalType = "XTERM",
				Mtts = MttsCapabilities.Ansi
			})
			.AddPlugin<TerminalTypeProtocol>()
			.AddPlugin<NewEnvironProtocol>()
			.BuildAsync();

		await InterpretAndWaitAsync(client, [(byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.TTYPE]);
		negotiation = null;
		await InterpretAndWaitAsync(client, TTypeSend);
		await AssertByteArraysEqual(negotiation, TTypeIs("MUINDEX-CRAWLER"));

		await InterpretAndWaitAsync(client, [(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.NEWENVIRON]);
		negotiation = null;
		await InterpretAndWaitAsync(client,
		[
			(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.NEWENVIRON, (byte)Trigger.SEND,
			(byte)Trigger.IAC, (byte)Trigger.SE
		]);

		var expected = new List<byte>
		{
			(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.NEWENVIRON, (byte)Trigger.IS
		};
		AppendVariable(expected, "CLIENT_NAME", "MUINDEX-CRAWLER");
		AppendVariable(expected, "CLIENT_VERSION", "1.2.0");
		AppendVariable(expected, "TERMINAL_TYPE", "XTERM");
		// 1 claimed (ANSI), 4 observed (UTF-8) and 512 observed (NEW-ENVIRON answers MNES).
		AppendVariable(expected, "MTTS", "517");
		expected.Add((byte)Trigger.IAC);
		expected.Add((byte)Trigger.SE);

		await AssertByteArraysEqual(negotiation, expected.ToArray());

		await client.DisposeAsync();
	}

	internal static void AppendVariable(List<byte> target, string name, string value)
	{
		target.Add((byte)Trigger.NEWENVIRON_VAR);
		target.AddRange(Encoding.ASCII.GetBytes(name));
		target.Add((byte)Trigger.NEWENVIRON_VALUE);
		target.AddRange(Encoding.ASCII.GetBytes(value));
	}

	[Test]
	public async Task MttsBitmaskMatchesTheMttsSpecification()
	{
		var everything = Enum.GetValues<MttsCapabilities>().Aggregate(MttsCapabilities.None, (all, x) => all | x);

		await Assert.That(Convert.ToInt32(everything)).IsEqualTo(2047);
		await Assert.That(MttsCapabilityNames.Expand(everything)).IsEquivalentTo(
		[
			"ANSI", "VT100", "UTF8", "256 COLORS", "MOUSE_TRACKING", "OSC_COLOR_PALETTE",
			"SCREEN_READER", "PROXY", "TRUECOLOR", "MNES", "MSLP"
		]);
		await Assert.That(MttsCapabilityNames.Expand(MttsCapabilities.Mnes | MttsCapabilities.Ansi))
			.IsEquivalentTo(["ANSI", "MNES"]);
	}

	[Test]
	public async Task AClientIdentityNeedsARealName()
	{
		await Assert.That(() => new ClientIdentity(null!)).Throws<ArgumentException>();
		await Assert.That(() => new ClientIdentity("   ")).Throws<ArgumentException>();
	}
}
