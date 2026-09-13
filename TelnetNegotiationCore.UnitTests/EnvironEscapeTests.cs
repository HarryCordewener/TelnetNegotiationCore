using System.Linq;
using System.Threading.Tasks;
using TelnetNegotiationCore.Machine;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// RFC 1572's and RFC 1408's escaping of the type bytes inside a name or a value, on the receive
/// path.
/// </summary>
/// <remarks>
/// <para>
/// Both RFCs say the same thing: a literal <c>VAR</c> inside a name or value is sent as
/// <c>ESC VAR</c>, and likewise for <c>VALUE</c>, <c>USERVAR</c> and <c>ESC</c> itself.
/// <c>NewEnvironProtocol.AppendEscaped</c> has always honoured that when sending; neither module
/// decoded it when receiving, so the escape leaked through as a literal <c>0x02</c> and the byte it
/// was escaping was read as a real marker, splitting the value in two. The library mis-parsed its
/// own output — the same one-directional asymmetry PR #105 fixed for ENCRYPT and AUTHENTICATION.
/// </para>
/// <para>
/// This file replaces <c>EnvironEscapeDivergenceTests</c>, which pinned the broken behaviour so it
/// could not be mistaken for correct.
/// </para>
/// </remarks>
public class EnvironEscapeTests
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
	private const byte UserVar = 3;

	private static async Task<RecordingTelnetContext> Run(byte[] wire)
	{
		var recorder = new RecordingTelnetContext();
		await using var machine = new TelnetCoreMachine(recorder);
		await machine.StartAsync();
		await machine.FireAsync(wire);
		return recorder;
	}

	/// <summary>The type bytes as the recorder renders them, since they reach it as text.</summary>
	private const string EscText = "\u0002";

	private const string ValueText = "\u0001";

	/// <summary>The events for one option, as one string, so a mismatch prints readably.</summary>
	private static string Trace(RecordingTelnetContext recorder, bool newEnviron) =>
		string.Join(
			" | ",
			(newEnviron ? recorder.NewEnvironEvents : recorder.EnvironEvents)
				.Select(e => e.Replace(EscText, "<ESC>").Replace(ValueText, "<VALUE>").Replace("\0", "<NUL>")));

	// ---------------------------------------------------------------------------------------------
	// NEW-ENVIRON, RFC 1572.
	// ---------------------------------------------------------------------------------------------

	/// <summary>
	/// <c>VAR 'A' VALUE ESC VALUE 'B'</c>: the value of A is a literal <c>VALUE</c> byte followed by
	/// <c>'B'</c>, delivered as one value with one marker — not two.
	/// </summary>
	[Test]
	public async Task AnEscapedValueByteIsDataAndNotAMarker()
	{
		byte[] wire = [IAC, SB, NewEnviron, Is, Var, (byte)'A', Value, Esc, Value, (byte)'B', IAC, SE];

		var recorder = await Run(wire);

		await Assert.That(recorder.NewEnvironEvents.Count(e => e == "VALUE"))
			.IsEqualTo(1)
			.Because($"the escaped VALUE must be data, not a second marker. Got: {Trace(recorder, true)}");

		await Assert.That(recorder.NewEnvironEvents.Any(e => e == EscText))
			.IsFalse()
			.Because($"the ESC itself must be consumed. Got: {Trace(recorder, true)}");

		// The value is the literal VALUE byte then 'B'.
		var value = string.Concat(recorder.NewEnvironEvents
			.SkipWhile(e => e != "VALUE")
			.Skip(1)
			.TakeWhile(e => e != "ended"));

		await Assert.That(value).IsEqualTo(ValueText + "B");
	}

	[Test]
	public async Task AnEscapedVarByteIsDataAndNotAMarker()
	{
		byte[] wire = [IAC, SB, NewEnviron, Is, Var, (byte)'A', Value, Esc, Var, (byte)'B', IAC, SE];

		var recorder = await Run(wire);

		await Assert.That(recorder.NewEnvironEvents.Count(e => e == "VAR"))
			.IsEqualTo(1)
			.Because($"the escaped VAR must be data. Got: {Trace(recorder, true)}");
	}

	[Test]
	public async Task AnEscapedUserVarByteIsDataAndNotAMarker()
	{
		byte[] wire = [IAC, SB, NewEnviron, Is, Var, (byte)'A', Value, Esc, UserVar, (byte)'B', IAC, SE];

		var recorder = await Run(wire);

		await Assert.That(recorder.NewEnvironEvents.Count(e => e == "USERVAR"))
			.IsEqualTo(0)
			.Because($"the escaped USERVAR must be data. Got: {Trace(recorder, true)}");
	}

	/// <summary><c>ESC ESC</c> is one literal <c>ESC</c> of data.</summary>
	[Test]
	public async Task ADoubledEscapeCollapsesToOne()
	{
		byte[] wire = [IAC, SB, NewEnviron, Is, Var, (byte)'A', Value, Esc, Esc, (byte)'B', IAC, SE];

		var recorder = await Run(wire);

		var value = string.Concat(recorder.NewEnvironEvents
			.SkipWhile(e => e != "VALUE")
			.Skip(1)
			.TakeWhile(e => e != "ended"));

		await Assert.That(value)
			.IsEqualTo(EscText + "B")
			.Because($"ESC ESC is one literal ESC. Got: {Trace(recorder, true)}");
	}

	/// <summary>
	/// A trailing <c>ESC</c> escapes nothing and is consumed, matching libtelnet, which skips the
	/// <c>ESC</c> unconditionally.
	/// </summary>
	[Test]
	public async Task ATrailingEscapeIsConsumed()
	{
		byte[] wire = [IAC, SB, NewEnviron, Is, Var, (byte)'A', Value, (byte)'B', Esc, IAC, SE];

		var recorder = await Run(wire);

		await Assert.That(recorder.NewEnvironEvents.Any(e => e.Contains(EscText)))
			.IsFalse()
			.Because($"a trailing ESC escapes nothing and is consumed. Got: {Trace(recorder, true)}");
		await Assert.That(recorder.NewEnvironEvents).Contains("ended");
	}

	/// <summary>
	/// An <c>ESC</c> before a byte the RFC does not list as escapable is consumed and the byte
	/// delivered literally. The RFC leaves this undefined; libtelnet does the same.
	/// </summary>
	[Test]
	public async Task AnEscapeBeforeAnOrdinaryByteDeliversTheByte()
	{
		byte[] wire = [IAC, SB, NewEnviron, Is, Var, (byte)'A', Value, Esc, (byte)'B', (byte)'C', IAC, SE];

		var recorder = await Run(wire);

		var value = string.Concat(recorder.NewEnvironEvents
			.SkipWhile(e => e != "VALUE")
			.Skip(1)
			.TakeWhile(e => e != "ended"));

		await Assert.That(value)
			.IsEqualTo("BC")
			.Because($"the ESC is consumed and the byte delivered. Got: {Trace(recorder, true)}");
	}

	/// <summary>An unescaped marker is still a marker — the fix must not swallow real structure.</summary>
	[Test]
	public async Task AnUnescapedMarkerIsStillAMarker()
	{
		byte[] wire = [IAC, SB, NewEnviron, Is, Var, (byte)'A', Value, (byte)'B', Var, (byte)'C', IAC, SE];

		var recorder = await Run(wire);

		await Assert.That(recorder.NewEnvironEvents.Count(e => e == "VAR"))
			.IsEqualTo(2)
			.Because($"two unescaped VARs are two variables. Got: {Trace(recorder, true)}");
	}

	// ---------------------------------------------------------------------------------------------
	// ENVIRON, RFC 1408, whose escaping rules are stated identically.
	// ---------------------------------------------------------------------------------------------

	[Test]
	public async Task EnvironAlsoTreatsAnEscapedValueByteAsData()
	{
		byte[] wire = [IAC, SB, Environ, Is, Var, (byte)'A', Value, Esc, Value, (byte)'B', IAC, SE];

		var recorder = await Run(wire);

		await Assert.That(recorder.EnvironEvents.Count(e => e == "VALUE"))
			.IsEqualTo(1)
			.Because($"the escaped VALUE must be data, not a second marker. Got: {Trace(recorder, false)}");

		await Assert.That(recorder.EnvironEvents.Any(e => e == EscText))
			.IsFalse()
			.Because($"the ESC itself must be consumed. Got: {Trace(recorder, false)}");
	}

	[Test]
	public async Task EnvironAlsoCollapsesADoubledEscape()
	{
		byte[] wire = [IAC, SB, Environ, Is, Var, (byte)'A', Value, Esc, Esc, (byte)'B', IAC, SE];

		var recorder = await Run(wire);

		var value = string.Concat(recorder.EnvironEvents
			.SkipWhile(e => e != "VALUE")
			.Skip(1)
			.TakeWhile(e => e != "ended"));

		await Assert.That(value)
			.IsEqualTo(EscText + "B")
			.Because($"ESC ESC is one literal ESC. Got: {Trace(recorder, false)}");
	}

	// ---------------------------------------------------------------------------------------------
	// What the old divergence never excused, and still must hold.
	// ---------------------------------------------------------------------------------------------

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

	/// <summary>
	/// The escape must survive being split across a chunk boundary, since the pending flag lives in
	/// state data and a socket splits wherever it likes. <c>FragmentationProperties</c> asserts this
	/// generally; this pins the specific case that motivated the flag.
	/// </summary>
	[Test]
	public async Task AnEscapeSplitAcrossAChunkBoundaryStillEscapes()
	{
		byte[] first = [IAC, SB, NewEnviron, Is, Var, (byte)'A', Value, Esc];
		byte[] second = [Value, (byte)'B', IAC, SE];

		var recorder = new RecordingTelnetContext();
		await using var machine = new TelnetCoreMachine(recorder);
		await machine.StartAsync();
		await machine.FireAsync(first);
		await machine.FireAsync(second);

		await Assert.That(recorder.NewEnvironEvents.Count(e => e == "VALUE"))
			.IsEqualTo(1)
			.Because($"the escape must survive the boundary. Got: {Trace(recorder, true)}");
	}
}
