using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TUnit.Core;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Protocols;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// Every one of these drives real <see cref="TelnetInterpreter"/> instances - and where the defect is
/// only visible once the far side parses the bytes, it drives two of them against each other and
/// asserts on what the receiver actually understood.
/// </summary>
public class IACEscapingTests : BaseTest
{
	/// <summary>
	/// A client and a server interpreter wired so everything the client negotiates is fed into the
	/// server, and every line the server submits is recorded.
	/// </summary>
	private sealed class Pair : IAsyncDisposable
	{
		public TelnetInterpreter Client = null!;
		public TelnetInterpreter Server = null!;
		public readonly List<byte> ClientWire = [];
		public readonly List<string> ServerLines = [];
		public int NawsWidth = int.MinValue;
		public int NawsHeight = int.MinValue;

		public byte[] TakeClientWire()
		{
			lock (ClientWire)
			{
				var bytes = ClientWire.ToArray();
				ClientWire.Clear();
				return bytes;
			}
		}

		public async ValueTask DisposeAsync()
		{
			await Client.DisposeAsync();
			await Server.DisposeAsync();
		}
	}

	private static async Task<Pair> BuildNawsPairAsync()
	{
		var pair = new Pair();

		pair.Client = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(data =>
			{
				lock (pair.ClientWire) pair.ClientWire.AddRange(data.ToArray());
				return ValueTask.CompletedTask;
			})
			.AddPlugin<NAWSProtocol>()
			.BuildAsync();

		pair.Server = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit((data, encoding, _) =>
			{
				lock (pair.ServerLines) pair.ServerLines.Add(encoding.GetString(data));
				return ValueTask.CompletedTask;
			})
			.OnNegotiation(_ => ValueTask.CompletedTask)
			.AddPlugin<NAWSProtocol>()
				.OnNAWS((height, width) =>
				{
					pair.NawsHeight = height;
					pair.NawsWidth = width;
					return ValueTask.CompletedTask;
				})
			.BuildAsync();

		// The server enables NAWS on the client; the client offers it to the server.
		await InterpretAndWaitAsync(pair.Client, [(byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.NAWS]);
		await InterpretAndWaitAsync(pair.Server, [(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.NAWS]);
		pair.TakeClientWire();

		return pair;
	}

	// ---------------------------------------------------------------------------------------------
	// Defect 1: SendNAWS emitted a dimension byte of 255 unescaped.
	//
	// RFC 1073: "As required by the Telnet protocol, any occurrence of 255 in the subnegotiation must
	// be doubled to distinguish it from the IAC character (which has a value of 255)."
	// ---------------------------------------------------------------------------------------------

	[Test]
	[Arguments((short)255, (short)62)]
	[Arguments((short)80, (short)255)]
	[Arguments((short)255, (short)255)]
	[Arguments((short)80, (short)24)]
	public async Task SendNAWSRoundTripsThroughAServersReader(short width, short height)
	{
		await using var pair = await BuildNawsPairAsync();

		await pair.Client.SendNAWS(width, height);
		var wire = pair.TakeClientWire();

		await InterpretAndWaitAsync(pair.Server, wire);

		await Assert.That(pair.NawsWidth).IsEqualTo((int)width);
		await Assert.That(pair.NawsHeight).IsEqualTo((int)height);
		await Assert.That(pair.Server.ClientWidth).IsEqualTo((int)width);
		await Assert.That(pair.Server.ClientHeight).IsEqualTo((int)height);
	}

	[Test]
	[Arguments((short)255, (short)62)]
	[Arguments((short)80, (short)255)]
	[Arguments((short)255, (short)255)]
	public async Task SendNAWSDoesNotCorruptTheFollowingLine(short width, short height)
	{
		await using var pair = await BuildNawsPairAsync();

		await pair.Client.SendNAWS(width, height);
		var wire = pair.TakeClientWire();

		await InterpretAndWaitAsync(pair.Server, wire);
		await InterpretAndWaitAsync(pair.Server, Encoding.ASCII.GetBytes("connect wizard hunter2\r\n"));

		// Before the fix this was either swallowed entirely (the machine wedged in EvaluatingNAWS) or
		// arrived with a stray dimension byte glued to the front: ">connect wizard hunter2".
		await Assert.That(pair.ServerLines).IsEquivalentTo(new[] { "connect wizard hunter2" });
	}

	[Test]
	public async Task SendNAWSDoublesTheDimensionByte255OnTheWire()
	{
		await using var pair = await BuildNawsPairAsync();

		await pair.Client.SendNAWS(255, 62);

		await AssertByteArraysEqual(pair.TakeClientWire(),
		[
			(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.NAWS,
			0x00, 0xFF, 0xFF,   // width  = 255, the 0xFF doubled
			0x00, 0x3E,         // height = 62
			(byte)Trigger.IAC, (byte)Trigger.SE
		]);
	}

	// ---------------------------------------------------------------------------------------------
	// Defect 2: an unknown subnegotiation permanently killed the byte-processing loop.
	//
	// RFC 855: "the receiver may locate the end of a parameter string by searching for the SE command
	// (i.e., the string IAC SE), even if the receiver is unable to parse the parameters."
	// ---------------------------------------------------------------------------------------------

	private static byte[] SubNegotiation(byte option, params byte[] payload) =>
		[(byte)Trigger.IAC, (byte)Trigger.SB, option, .. payload, (byte)Trigger.IAC, (byte)Trigger.SE];

	public static IEnumerable<Func<(string, byte[])>> UnknownSubNegotiations() =>
	[
		() => ("option 99, two-byte payload", SubNegotiation(99, (byte)'a', (byte)'b')),
		() => ("option 99, empty payload", SubNegotiation(99)),
		() => ("option 200 (ATCP)", SubNegotiation(200, (byte)'x')),
		() => ("option 99, payload containing an escaped IAC",
			SubNegotiation(99, (byte)'a', (byte)Trigger.IAC, (byte)Trigger.IAC, (byte)'b')),
		() => ("option 90 (MSP), longer payload", SubNegotiation(90, Encoding.ASCII.GetBytes("!!SOUND(off)"))),
	];

	[Test]
	[MethodDataSource(nameof(UnknownSubNegotiations))]
	public async Task UnknownSubNegotiationIsSkippedAndTheConnectionSurvives(string name, byte[] bytes)
	{
		var lines = new List<string>();

		await using var server = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit((data, encoding, _) => { lock (lines) lines.Add(encoding.GetString(data)); return ValueTask.CompletedTask; })
			.OnNegotiation(_ => ValueTask.CompletedTask)
			.BuildAsync();

		await InterpretAndWaitAsync(server, bytes);
		await InterpretAndWaitAsync(server, Encoding.ASCII.GetBytes("connect wizard hunter2\r\n"));
		await InterpretAndWaitAsync(server, Encoding.ASCII.GetBytes("second line\r\n"));

		// Before the fix, everything except the empty-payload form left the machine stuck in
		// BadSubNegotiationEvaluating and ProcessBytesAsync exited: zero submissions, forever.
		await Assert.That(lines).IsEquivalentTo(new[] { "connect wizard hunter2", "second line" })
			.Because($"{name} must resynchronise at its IAC SE");
	}

	/// <summary>
	/// The root cause of defect 2 was structural: <c>Trigger.Error</c> sat in the set that every
	/// <c>ForAllTriggers*</c> loop enumerates, so states configured that way got a second, unguarded
	/// <c>Error</c> transition on top of the safe interpreter's single recovery transition. Stateless
	/// only notices at fire time, which is exactly when the interpreter is trying to recover.
	/// </summary>
	[Test]
	public async Task NoStateHasAmbiguousErrorRecovery()
	{
		await using var server = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(_ => ValueTask.CompletedTask)
			.AddPlugin<NAWSProtocol>()
			.AddPlugin<CharsetProtocol>()
			.AddPlugin<TerminalTypeProtocol>()
			.AddPlugin<LineModeProtocol>()
			.AddPlugin<GMCPProtocol>()
			.AddPlugin<MSSPProtocol>()
			.BuildAsync();

		var ambiguous = server.TelnetStateMachine.GetInfo().States
			.Select(state => (
				State: (State)state.UnderlyingState,
				ErrorTransitions: state.Transitions.Count(t => (Trigger)t.Trigger.UnderlyingTrigger == Trigger.Error)))
			.Where(x => x.ErrorTransitions > 1)
			.Select(x => $"{x.State} ({x.ErrorTransitions})")
			.ToArray();

		await Assert.That(ambiguous).IsEmpty()
			.Because("a state with two unguarded Error transitions throws 'Multiple permitted exit transitions' the moment recovery is attempted");
	}

	// ---------------------------------------------------------------------------------------------
	// Defect 3: the default ECHO handler re-emitted a bare 0xFF.
	//
	// RFC 854: "only the IAC need be doubled to be sent as data, and the other 255 codes may be
	// passed transparently."
	// ---------------------------------------------------------------------------------------------

	[Test]
	public async Task DefaultEchoHandlerReEscapesAnIacDataByte()
	{
		var echoed = new List<byte>();

		await using var server = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(data => { lock (echoed) echoed.AddRange(data.ToArray()); return ValueTask.CompletedTask; })
			.AddPlugin<EchoProtocol>()
				.UseDefaultEchoHandler()
			.BuildAsync();

		await InterpretAndWaitAsync(server, [(byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.ECHO]);
		lock (echoed) echoed.Clear();

		// The peer types 'a', U+00FF, 'b' in a single-byte encoding and escapes the 0xFF correctly.
		await InterpretAndWaitAsync(server,
			[(byte)'a', (byte)Trigger.IAC, (byte)Trigger.IAC, (byte)'b']);

		byte[] wire;
		lock (echoed) wire = echoed.ToArray();
		await AssertByteArraysEqual(wire, [(byte)'a', (byte)Trigger.IAC, (byte)Trigger.IAC, (byte)'b']);

		// And prove it end to end: the peer's own parser must get the three characters back.
		var decoded = new List<byte>();
		await using var peer = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit((data, _, _) => { lock (decoded) decoded.AddRange(data); return ValueTask.CompletedTask; })
			.OnNegotiation(_ => ValueTask.CompletedTask)
			.BuildAsync();

		await InterpretAndWaitAsync(peer, wire);
		await InterpretAndWaitAsync(peer, [(byte)'\n']);

		byte[] roundTripped;
		lock (decoded) roundTripped = decoded.ToArray();
		await AssertByteArraysEqual(roundTripped, [(byte)'a', 0xFF, (byte)'b']);
	}

	// ---------------------------------------------------------------------------------------------
	// Defect 4: NAWS receive incidentals.
	// ---------------------------------------------------------------------------------------------

	[Test]
	public async Task OverLongNAWSPayloadDoesNotKillTheConnection()
	{
		var lines = new List<string>();

		await using var server = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit((data, encoding, _) => { lock (lines) lines.Add(encoding.GetString(data)); return ValueTask.CompletedTask; })
			.OnNegotiation(_ => ValueTask.CompletedTask)
			.AddPlugin<NAWSProtocol>()
			.BuildAsync();

		await InterpretAndWaitAsync(server, [(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.NAWS]);

		// RFC 1073 defines exactly four payload bytes. A fifth used to index one past the end of the
		// four-byte capture buffer, throwing out of the state machine and ending the read loop.
		await InterpretAndWaitAsync(server,
			[(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.NAWS, 0, 80, 0, 24, 0, (byte)Trigger.IAC, (byte)Trigger.SE]);
		await InterpretAndWaitAsync(server, Encoding.ASCII.GetBytes("still alive\r\n"));

		await Assert.That(lines).IsEquivalentTo(new[] { "still alive" });
		await Assert.That(server.ClientWidth).IsEqualTo(80);
		await Assert.That(server.ClientHeight).IsEqualTo(24);
	}

	/// <summary>
	/// RFC 1073's whole motivation for replacing NAOL/NAOP was range: "the 253 character height and
	/// width limitation is too low so the new option has a limit of 65535 characters."
	/// <c>BitConverter.ToInt16</c> made everything above 32767 arrive negative.
	/// </summary>
	[Test]
	[Arguments((byte)0xFF, (byte)0xFF, 65535)]
	[Arguments((byte)0x80, (byte)0x00, 32768)]
	[Arguments((byte)0x9C, (byte)0x40, 40000)]
	public async Task NAWSDimensionsAboveShortMaxAreReadUnsigned(byte high, byte low, int expected)
	{
		var width = int.MinValue;
		var height = int.MinValue;

		await using var server = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(_ => ValueTask.CompletedTask)
			.AddPlugin<NAWSProtocol>()
				.OnNAWS((h, w) => { height = h; width = w; return ValueTask.CompletedTask; })
			.BuildAsync();

		await InterpretAndWaitAsync(server, [(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.NAWS]);

		// Any 0xFF in the payload has to be doubled on the way in, exactly as RFC 1073 requires.
		byte[] Escape(byte b) => b == 0xFF ? [0xFF, 0xFF] : [b];
		await InterpretAndWaitAsync(server,
		[
			(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.NAWS,
			.. Escape(high), .. Escape(low),
			.. Escape(high), .. Escape(low),
			(byte)Trigger.IAC, (byte)Trigger.SE
		]);

		await Assert.That(width).IsEqualTo(expected);
		await Assert.That(height).IsEqualTo(expected);
	}
}
