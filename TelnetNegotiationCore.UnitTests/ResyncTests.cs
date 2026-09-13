using System.Threading.Tasks;
using TelnetNegotiationCore.Machine;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// The evidence behind <see cref="TelnetProbe.Resync"/>: that it returns the machine to a state
/// where text parses again, from every state any option this library implements can leave it in.
/// The liveness and fragmentation properties are built on this, so it is verified rather than
/// assumed.
/// </summary>
/// <remarks>
/// <c>IAC SE</c> alone is not enough, and the two tests at the bottom of this file pin why: from
/// <c>ReadingOption</c> the <c>IAC</c> is bound as the subnegotiation's option byte and the
/// <c>SE</c> becomes payload, and from <c>EndSubNegotiation</c> the <c>IAC</c> is consumed by
/// <c>EscapedInPayload</c> as a literal 255. Both leave the machine inside <c>SubNegotiating</c>.
/// </remarks>
public class ResyncTests
{
	private const byte SE = 240;
	private const byte SB = 250;
	private const byte WILL = 251;
	private const byte WONT = 252;
	private const byte DO = 253;
	private const byte DONT = 254;
	private const byte IAC = 255;

	/// <summary>Every option this library has a module or a protocol for, with its specification.</summary>
	private static readonly byte[] AllOptions =
	[
		1,   // ECHO, RFC 857
		3,   // SUPPRESS-GO-AHEAD, RFC 858
		24,  // TERMINAL-TYPE, RFC 1091
		25,  // END-OF-RECORD, RFC 885
		31,  // NAWS, RFC 1073
		32,  // TERMINAL-SPEED, RFC 1079
		33,  // TOGGLE-FLOW-CONTROL, RFC 1372
		34,  // LINEMODE, RFC 1184
		35,  // X-DISPLAY-LOCATION, RFC 1096
		36,  // ENVIRON, RFC 1408
		37,  // AUTHENTICATION, RFC 2941
		38,  // ENCRYPT, RFC 2946
		39,  // NEW-ENVIRON, RFC 1572
		42,  // CHARSET, RFC 2066
		69,  // MSDP
		70,  // MSSP
		85,  // MCCP1
		86,  // MCCP2
		87,  // MCCP3
		91,  // MXP
		201, // GMCP
	];

	/// <summary>
	/// The sub-command bytes worth stopping part-way through, one per option that has a command
	/// byte after the option byte. Stopping here is what leaves a module in its own substate,
	/// which is the case the core module's transition table cannot speak for.
	/// </summary>
	private static readonly (byte Option, byte Command)[] OptionCommands =
	[
		(24, 0), (24, 1),                       // TTYPE IS, SEND
		(32, 0), (32, 1),                       // TSPEED IS, SEND
		(33, 0), (33, 1), (33, 2), (33, 3),     // FLOWCONTROL OFF, ON, RESTART-ANY, RESTART-XON
		(34, 1), (34, 2), (34, 3),              // LINEMODE MODE, FORWARDMASK, SLC
		(35, 0), (35, 1),                       // XDISPLOC IS, SEND
		(36, 0), (36, 1), (36, 2),              // ENVIRON IS, SEND, INFO
		(39, 0), (39, 1), (39, 2),              // NEW-ENVIRON IS, SEND, INFO
		(37, 0), (37, 1), (37, 2), (37, 3),     // AUTHENTICATION IS, SEND, REPLY, NAME
		(38, 0), (38, 1), (38, 3), (38, 4),     // ENCRYPT IS, SUPPORT, START, END
		(42, 1), (42, 2), (42, 3), (42, 4),     // CHARSET REQUEST, ACCEPTED, REJECTED, TTABLE-IS
		(69, 1), (69, 2),                       // MSDP VAR, VAL
		(70, 1), (70, 2),                       // MSSP VAR, VAL
	];

	/// <summary>Fires a prefix, then the resync token, then the probe line.</summary>
	private static async Task<RecordingTelnetContext> Run(byte[] prefix)
	{
		var recorder = new RecordingTelnetContext();
		await using var machine = new TelnetCoreMachine(recorder);
		await machine.StartAsync();
		await machine.FireAsync(prefix);
		await machine.FireAsync(TelnetProbe.Resync);
		await machine.FireAsync(TelnetProbe.ProbeLine);
		return recorder;
	}

	[Test]
	public async Task AnOptionLeftMidSubnegotiationResynchronises()
	{
		foreach (var option in AllOptions)
		{
			var recorder = await Run([IAC, SB, option]);

			await Assert.That(TelnetProbe.Recovered(recorder))
				.IsTrue()
				.Because($"option {option} wedged after IAC SB {option}");
		}
	}

	[Test]
	public async Task AnOptionLeftAfterAnIacInItsPayloadResynchronises()
	{
		foreach (var option in AllOptions)
		{
			var recorder = await Run([IAC, SB, option, 1, 2, 3, IAC]);

			await Assert.That(TelnetProbe.Recovered(recorder))
				.IsTrue()
				.Because($"option {option} wedged after a trailing IAC in its payload");
		}
	}

	[Test]
	public async Task AnOptionLeftMidCommandResynchronises()
	{
		foreach (var (option, command) in OptionCommands)
		{
			var recorder = await Run([IAC, SB, option, command]);

			await Assert.That(TelnetProbe.Recovered(recorder))
				.IsTrue()
				.Because($"option {option} wedged after command {command}");
		}
	}

	[Test]
	public async Task AnOptionLeftMidCommandWithAPartialPayloadResynchronises()
	{
		foreach (var (option, command) in OptionCommands)
		{
			var recorder = await Run([IAC, SB, option, command, 65, 66, 67]);

			await Assert.That(TelnetProbe.Recovered(recorder))
				.IsTrue()
				.Because($"option {option} wedged after command {command} and a partial payload");
		}
	}

	[Test]
	public async Task APendingVerbResynchronises()
	{
		foreach (var verb in new byte[] { WILL, WONT, DO, DONT })
		{
			var recorder = await Run([IAC, verb]);

			await Assert.That(TelnetProbe.Recovered(recorder))
				.IsTrue()
				.Because($"verb {verb} wedged while waiting for its option byte");
		}
	}

	[Test]
	public async Task ABareIacResynchronises()
	{
		var recorder = await Run([IAC]);

		await Assert.That(TelnetProbe.Recovered(recorder)).IsTrue();
	}

	[Test]
	public async Task APartialLineResynchronisesAndDoesNotContaminateTheProbe()
	{
		var recorder = await Run([.. "leftover"u8]);

		await Assert.That(TelnetProbe.Recovered(recorder)).IsTrue();
	}

	// -----------------------------------------------------------------------------------------
	// Why the token is two pairs and not one. These pin the reasoning so that a later shortening
	// of Resync fails here rather than silently weakening every property built on it.
	// -----------------------------------------------------------------------------------------

	[Test]
	public async Task OneIacSeDoesNotResynchroniseFromReadingOption()
	{
		byte[] intoReadingOption = [IAC, SB];
		byte[] onePair = [IAC, SE];

		var recorder = new RecordingTelnetContext();
		await using var machine = new TelnetCoreMachine(recorder);
		await machine.StartAsync();
		await machine.FireAsync(intoReadingOption);
		await machine.FireAsync(onePair);
		await machine.FireAsync(TelnetProbe.ProbeLine);

		// The IAC was bound as the subnegotiation's option byte and the SE became payload, so the
		// machine is still inside SubNegotiating and the probe line is payload too.
		await Assert.That(TelnetProbe.Recovered(recorder)).IsFalse();
	}

	[Test]
	public async Task OneIacSeDoesNotResynchroniseFromEndSubNegotiation()
	{
		byte[] intoEndSubNegotiation = [IAC, SB, 70, 1, IAC];
		byte[] onePair = [IAC, SE];

		var recorder = new RecordingTelnetContext();
		await using var machine = new TelnetCoreMachine(recorder);
		await machine.StartAsync();
		await machine.FireAsync(intoEndSubNegotiation);
		await machine.FireAsync(onePair);
		await machine.FireAsync(TelnetProbe.ProbeLine);

		// EscapedInPayload consumed the IAC as a literal 255, so the SE is payload and the machine
		// never left SubNegotiating.
		await Assert.That(TelnetProbe.Recovered(recorder)).IsFalse();
	}
}
