using System.Linq;
using System.Threading.Tasks;
using TelnetNegotiationCore.Machine;
using TelnetNegotiationCore.Models;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// What the core machine does with a carriage return, across all three
/// <see cref="CarriageReturnMode"/> settings.
/// </summary>
/// <remarks>
/// <para>
/// <c>CR LF</c> ends a line in every mode, and a bare <c>LF</c> does too. The modes differ only on a
/// carriage return that is <em>not</em> part of <c>CR LF</c>:
/// </para>
/// <list type="bullet">
/// <item><description><see cref="CarriageReturnMode.Drop"/> — discard it, and consume a <c>NUL</c>
/// that follows. The default, and what this library has always done, except that the <c>NUL</c> used
/// to reach the consumer as a literal <c>0x00</c> inside the line.</description></item>
/// <item><description><see cref="CarriageReturnMode.EndOfLine"/> — <c>CR NUL</c> ends the line.
/// RFC 1123 §3.3.1: "CR LF and CR NUL MUST have the same effect on an ASCII server host when
/// received as input."</description></item>
/// <item><description><see cref="CarriageReturnMode.Preserve"/> — <c>CR NUL</c> yields a literal
/// <c>CR</c>, which is what RFC 854 defines it to mean and what libtelnet's
/// <c>TELNET_FLAG_NVT_EOL</c> implements.</description></item>
/// </list>
/// <para>
/// Both readings of <c>CR NUL</c> are legitimate, which is why this is a setting and not a fix with
/// one answer. What no RFC and no implementation does is deliver the <c>NUL</c> as data, and that is
/// what changed.
/// </para>
/// </remarks>
public class CarriageReturnPolicyTests
{
	private const byte NUL = 0;
	private const byte LF = 10;
	private const byte CR = 13;
	private const byte SE = 240;
	private const byte SB = 250;
	private const byte WILL = 251;
	private const byte IAC = 255;

	private static async Task<RecordingTelnetContext> Run(CarriageReturnMode mode, byte[] bytes)
	{
		var recorder = new RecordingTelnetContext();
		await using var machine = new TelnetCoreMachine(recorder, new TelnetMachineConfig(mode));
		await machine.StartAsync();
		await machine.FireAsync(bytes);
		return recorder;
	}

	// ---------------------------------------------------------------------------------------------
	// Shared across every mode.
	// ---------------------------------------------------------------------------------------------

	[Test]
	[Arguments(CarriageReturnMode.Drop)]
	[Arguments(CarriageReturnMode.EndOfLine)]
	[Arguments(CarriageReturnMode.Preserve)]
	public async Task CrLfEndsALineInEveryMode(CarriageReturnMode mode)
	{
		var recorder = await Run(mode, [.. "hello"u8, CR, LF]);

		await Assert.That(recorder.Lines).IsEquivalentTo(new[] { "hello" });
		await Assert.That(recorder.PendingText).IsEqualTo(string.Empty);
	}

	[Test]
	[Arguments(CarriageReturnMode.Drop)]
	[Arguments(CarriageReturnMode.EndOfLine)]
	[Arguments(CarriageReturnMode.Preserve)]
	public async Task ABareLineFeedEndsALineInEveryMode(CarriageReturnMode mode)
	{
		var recorder = await Run(mode, [.. "hello"u8, LF]);

		await Assert.That(recorder.Lines).IsEquivalentTo(new[] { "hello" });
	}

	[Test]
	[Arguments(CarriageReturnMode.Drop)]
	[Arguments(CarriageReturnMode.EndOfLine)]
	[Arguments(CarriageReturnMode.Preserve)]
	public async Task ABareLineFeedOnItsOwnSubmitsAnEmptyLineInEveryMode(CarriageReturnMode mode)
	{
		var recorder = await Run(mode, [LF]);

		await Assert.That(recorder.Lines).IsEquivalentTo(new[] { string.Empty });
	}

	/// <summary>
	/// A carriage return still pending when the stream stops writes nothing, in every mode. Until the
	/// next byte arrives the machine cannot know whether it is looking at <c>CR LF</c>, <c>CR NUL</c>
	/// or a data carriage return, and guessing here would make the same bytes parse differently
	/// depending on where the peer happened to stop sending.
	/// </summary>
	[Test]
	[Arguments(CarriageReturnMode.Drop)]
	[Arguments(CarriageReturnMode.EndOfLine)]
	[Arguments(CarriageReturnMode.Preserve)]
	public async Task ATrailingCarriageReturnContributesNothingInEveryMode(CarriageReturnMode mode)
	{
		var recorder = await Run(mode, [CR]);

		await Assert.That(recorder.Lines).IsEmpty();
		await Assert.That(recorder.PendingText).IsEqualTo(string.Empty);
	}

	/// <summary>A command after a carriage return is still a command in every mode.</summary>
	[Test]
	[Arguments(CarriageReturnMode.Drop)]
	[Arguments(CarriageReturnMode.EndOfLine)]
	[Arguments(CarriageReturnMode.Preserve)]
	public async Task ANegotiationAfterACarriageReturnStillNegotiates(CarriageReturnMode mode)
	{
		var recorder = await Run(mode, [.. "ab"u8, CR, IAC, WILL, 31, .. "cd"u8, LF]);

		await Assert.That(recorder.Negotiations).Contains("WILL 31");
		await Assert.That(recorder.Lines.Count).IsEqualTo(1);
	}

	// ---------------------------------------------------------------------------------------------
	// Drop: the default, and what the library has always done.
	// ---------------------------------------------------------------------------------------------

	[Test]
	public async Task DropDiscardsACarriageReturnInTheMiddleOfALine()
	{
		var recorder = await Run(CarriageReturnMode.Drop, [.. "ab"u8, CR, .. "cd"u8, LF]);

		await Assert.That(recorder.Lines).IsEquivalentTo(new[] { "abcd" });
	}

	/// <summary>
	/// The defect this fixes. RFC 854 defines <c>CR NUL</c> as a bare carriage return; the <c>NUL</c>
	/// used to reach the consumer as a literal <c>0x00</c> inside the line, which matches no RFC and
	/// no implementation.
	/// </summary>
	[Test]
	public async Task DropConsumesTheNulAfterACarriageReturn()
	{
		var recorder = await Run(CarriageReturnMode.Drop, [.. "ab"u8, CR, NUL, .. "cd"u8, LF]);

		await Assert.That(recorder.Lines).IsEquivalentTo(new[] { "abcd" });
		await Assert.That(recorder.Lines[0].Any(c => c == '\0'))
			.IsFalse()
			.Because("the NUL of a CR NUL pair must not reach the consumer as data");
	}

	[Test]
	public async Task DropDiscardsRepeatedCarriageReturns()
	{
		var recorder = await Run(CarriageReturnMode.Drop, [.. "ab"u8, CR, CR, CR, .. "cd"u8, LF]);

		await Assert.That(recorder.Lines).IsEquivalentTo(new[] { "abcd" });
	}

	// ---------------------------------------------------------------------------------------------
	// EndOfLine: RFC 1123's MUST for a server reading user input.
	// ---------------------------------------------------------------------------------------------

	[Test]
	public async Task EndOfLineTreatsCrNulAsTheEndOfTheLine()
	{
		var recorder = await Run(CarriageReturnMode.EndOfLine, [.. "ab"u8, CR, NUL, .. "cd"u8, LF]);

		await Assert.That(recorder.Lines).IsEquivalentTo(new[] { "ab", "cd" });
	}

	[Test]
	public async Task EndOfLineStillDiscardsACarriageReturnBeforeOrdinaryText()
	{
		var recorder = await Run(CarriageReturnMode.EndOfLine, [.. "ab"u8, CR, .. "cd"u8, LF]);

		await Assert.That(recorder.Lines).IsEquivalentTo(new[] { "abcd" });
	}

	/// <summary>`CR NUL` on its own submits an empty line, exactly as a bare `LF` does.</summary>
	[Test]
	public async Task EndOfLineSubmitsAnEmptyLineForABareCrNul()
	{
		var recorder = await Run(CarriageReturnMode.EndOfLine, [CR, NUL]);

		await Assert.That(recorder.Lines).IsEquivalentTo(new[] { string.Empty });
	}

	// ---------------------------------------------------------------------------------------------
	// Preserve: RFC 854's reading, and what libtelnet implements.
	// ---------------------------------------------------------------------------------------------

	[Test]
	public async Task PreserveYieldsALiteralCarriageReturnForCrNul()
	{
		var recorder = await Run(CarriageReturnMode.Preserve, [.. "ab"u8, CR, NUL, .. "cd"u8, LF]);

		await Assert.That(recorder.Lines).IsEquivalentTo(new[] { "ab\rcd" });
	}

	[Test]
	public async Task PreserveYieldsALiteralCarriageReturnBeforeOrdinaryText()
	{
		var recorder = await Run(CarriageReturnMode.Preserve, [.. "ab"u8, CR, .. "cd"u8, LF]);

		await Assert.That(recorder.Lines).IsEquivalentTo(new[] { "ab\rcd" });
	}

	/// <summary>
	/// Each carriage return that turns out not to begin <c>CR LF</c> or <c>CR NUL</c> yields one, so a
	/// run of three followed by text yields two and leaves the third pending.
	/// </summary>
	[Test]
	public async Task PreserveYieldsOneCarriageReturnPerNonTerminatingOne()
	{
		var recorder = await Run(CarriageReturnMode.Preserve, [.. "ab"u8, CR, CR, CR, .. "cd"u8, LF]);

		await Assert.That(recorder.Lines).IsEquivalentTo(new[] { "ab\r\r\rcd" });
	}

	/// <summary>
	/// A pending carriage return is data, so it is written before the command that follows it.
	/// </summary>
	[Test]
	public async Task PreserveWritesThePendingCarriageReturnBeforeANegotiation()
	{
		var recorder = await Run(CarriageReturnMode.Preserve, [.. "ab"u8, CR, IAC, WILL, 31, .. "cd"u8, LF]);

		await Assert.That(recorder.Lines).IsEquivalentTo(new[] { "ab\rcd" });
		await Assert.That(recorder.Negotiations).Contains("WILL 31");
	}

	/// <summary>
	/// `CR LF` still ends the line even in Preserve: the carriage return is part of the terminator,
	/// not data.
	/// </summary>
	[Test]
	public async Task PreserveDoesNotWriteTheCarriageReturnOfACrLfPair()
	{
		var recorder = await Run(CarriageReturnMode.Preserve, [.. "ab"u8, CR, LF]);

		await Assert.That(recorder.Lines).IsEquivalentTo(new[] { "ab" });
	}

	/// <summary>
	/// A subnegotiation is unaffected: its payload goes to the option's own module, never through the
	/// text path, so a <c>CR NUL</c> inside one is payload in every mode.
	/// </summary>
	/// <remarks>
	/// Asserted through <c>MsspEvents</c> rather than <c>SubNegotiations</c>: option 70 has a module
	/// of its own, so a well-formed MSSP frame is reported by that module and never reaches the
	/// core's generic <c>SubNegotiatedAsync</c>. Only a malformed frame falls through to the generic
	/// path, which is what <c>MalformedSubnegotiationRecoveryTests</c> relies on.
	/// </remarks>
	[Test]
	public async Task PreserveDoesNotDisturbASubnegotiationPayload()
	{
		var recorder = await Run(CarriageReturnMode.Preserve, [IAC, SB, 70, 1, CR, NUL, IAC, SE, .. "hi"u8, LF]);

		await Assert.That(recorder.MsspEvents).Contains("VAR");
		await Assert.That(recorder.Lines).IsEquivalentTo(new[] { "hi" });
	}
}
