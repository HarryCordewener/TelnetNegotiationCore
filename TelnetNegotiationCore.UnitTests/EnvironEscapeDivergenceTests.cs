using System.Linq;
using System.Threading.Tasks;
using TelnetNegotiationCore.Machine;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// A known divergence from RFC 1572 and RFC 1408 on the receive path, pinned so that it is a
/// recorded fact rather than an accident, and so that fixing it fails these tests loudly.
/// </summary>
/// <remarks>
/// <para>
/// Both RFCs require the type bytes to be escaped inside a name or a value: "if a variable or a
/// value contains a VAR, it must be sent as ESC VAR", and likewise for VALUE, USERVAR and ESC
/// itself. <see cref="TelnetNegotiationCore.Protocols.NewEnvironProtocol"/> honours that when it
/// sends — see its <c>AppendEscaped</c> — but neither <c>NewEnvironModule</c> nor
/// <c>EnvironModule</c> has any transition for ESC when receiving. ESC is not even among their
/// declared constants.
/// </para>
/// <para>
/// So <c>ESC VALUE</c> inside a value arrives as a literal <c>0x02</c> followed by a <em>real</em>
/// VALUE marker: the escape leaks through as data and the byte it was escaping splits the value in
/// two. This library would mis-parse its own output, which is the same asymmetry PR #105 fixed for
/// ENCRYPT and AUTHENTICATION — escaping honoured in one direction only.
/// </para>
/// <para>
/// It is recorded rather than fixed here because the fix is a change to the state machine, not to a
/// helper: the marker transitions need guards, and how a guarded transition interacts with
/// StateAlchemist's <c>[Run]</c> batching — whether a run's stop set is computed statically from
/// triggers or dynamically from guards — decides whether the obvious implementation works or wedges
/// the connection. That deserves its own change and its own review.
/// </para>
/// <para>
/// In practice MNES, the MUD profile this library is mostly used under, forbids these bytes inside
/// names and values, which is why the gap has gone unnoticed. It is still wrong against the RFCs.
/// </para>
/// </remarks>
public class EnvironEscapeDivergenceTests
{
	private const byte SE = 240;
	private const byte SB = 250;
	private const byte IAC = 255;

	private const byte NewEnviron = 39;
	private const byte Environ = 36;

	private const byte Is = 0;
	private const byte Var = 0;
	private const byte Value = 1;
	private const byte Esc = 2;

	/// <summary>The ESC byte as the recorder renders it, since it reaches the consumer as text.</summary>
	private const string EscAsText = "\u0002";

	private static async Task<RecordingTelnetContext> Run(byte[] wire)
	{
		var recorder = new RecordingTelnetContext();
		await using var machine = new TelnetCoreMachine(recorder);
		await machine.StartAsync();
		await machine.FireAsync(wire);
		return recorder;
	}

	/// <summary>
	/// <c>IAC SB NEW-ENVIRON IS VAR 'A' VALUE ESC VALUE 'B' IAC SE</c>. RFC 1572 says the value of
	/// A is a literal <c>0x01</c> followed by <c>'B'</c>, delivered as one value. What arrives
	/// instead is the ESC as data and a second VALUE marker.
	/// </summary>
	[Test]
	public async Task NewEnvironDoesNotUnescapeAnEscapedValueByte()
	{
		byte[] wire = [IAC, SB, NewEnviron, Is, Var, (byte)'A', Value, Esc, Value, (byte)'B', IAC, SE];

		var recorder = await Run(wire);

		// A conforming parse reports one VALUE marker. Two arrive, because the escaped one was read
		// as structure rather than as data.
		await Assert.That(recorder.NewEnvironEvents.Count(e => e == "VALUE"))
			.IsEqualTo(2)
			.Because("the escaped VALUE byte is read as a second marker instead of as data");

		// And the ESC itself is handed to the consumer as a literal 0x02.
		await Assert.That(recorder.NewEnvironEvents.Contains(EscAsText))
			.IsTrue()
			.Because("RFC 1572's ESC leaks through as data rather than being consumed");
	}

	/// <summary>The same gap in ENVIRON, whose escaping rules RFC 1408 states identically.</summary>
	[Test]
	public async Task EnvironDoesNotUnescapeAnEscapedValueByte()
	{
		byte[] wire = [IAC, SB, Environ, Is, Var, (byte)'A', Value, Esc, Value, (byte)'B', IAC, SE];

		var recorder = await Run(wire);

		await Assert.That(recorder.EnvironEvents.Count(e => e == "VALUE"))
			.IsEqualTo(2)
			.Because("the escaped VALUE byte is read as a second marker instead of as data");

		await Assert.That(recorder.EnvironEvents.Contains(EscAsText))
			.IsTrue()
			.Because("RFC 1408's ESC leaks through as data rather than being consumed");
	}

	/// <summary>
	/// <c>ESC ESC</c> should collapse to one literal ESC in the data. Both bytes arrive instead.
	/// This is the one case where the current behaviour merely doubles the data rather than
	/// restructuring the message.
	/// </summary>
	[Test]
	public async Task NewEnvironDoesNotCollapseADoubledEscape()
	{
		byte[] wire = [IAC, SB, NewEnviron, Is, Var, (byte)'A', Value, Esc, Esc, (byte)'B', IAC, SE];

		var recorder = await Run(wire);

		var value = string.Concat(recorder.NewEnvironEvents
			.SkipWhile(e => e != "VALUE")
			.Skip(1)
			.TakeWhile(e => e != "ended"));

		await Assert.That(value)
			.IsEqualTo(EscAsText + EscAsText + "B")
			.Because("a doubled ESC should collapse to one literal ESC and does not");
	}

	/// <summary>
	/// Whatever else is true, a payload full of escapes must not wedge the connection. That is the
	/// part the divergence does not excuse, and it holds.
	/// </summary>
	[Test]
	public async Task AnEscapeHeavyPayloadStillDoesNotWedgeTheConnection()
	{
		byte[] wire =
		[
			IAC, SB, NewEnviron, Is,
			Var, Esc, Var, Esc, Value, Esc, Esc,
			Value, Esc, Var, (byte)'x', Esc,
			IAC, SE,
		];

		var recorder = new RecordingTelnetContext();
		await using var machine = new TelnetCoreMachine(recorder);
		await machine.StartAsync();
		await machine.FireAsync(wire);
		await machine.FireAsync(TelnetProbe.Resync);
		await machine.FireAsync(TelnetProbe.ProbeLine);

		await Assert.That(TelnetProbe.Recovered(recorder)).IsTrue();
	}
}
