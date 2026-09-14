#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TelnetNegotiationCore.Machine;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// Every option's receive path, against RFC 855's rule that a 255 among a subnegotiation's
/// parameters arrives doubled.
/// </summary>
/// <remarks>
/// <para>
/// RFC 855 states it once, for all options: "if parameters in an option 'subnegotiation' include a
/// byte with a value of 255, it is necessary to double this byte in accordance the general TELNET
/// rules." So a receiver must collapse <c>IAC IAC</c> to one literal 255, and must not read the
/// <c>SE</c> after such a pair as the end of the frame.
/// </para>
/// <para>
/// The outbound half of this rule lives in one helper. The inbound half is implemented separately in
/// every option's state module, and before this sweep only four of them were covered — GMCP, MSDP,
/// CHARSET's translation table, and ENVIRON/NEW-ENVIRON. Nothing covered MSSP, NAWS, LINEMODE,
/// TTYPE, TSPEED, XDISPLOC, CHARSET's other payloads, AUTH, ENCRYPT, or any of the marker-only
/// states.
/// </para>
/// <para>
/// Each case asserts <em>both</em> halves, because either alone passes a mutant that gets the other
/// wrong — which is the defect shape that reached production once already (a flag cleared only on a
/// data byte, so an escaped 255 immediately before a structural byte swallowed the marker). The
/// literal 255 must reach the payload, <em>and</em> the byte after it must still be read as
/// structure. Assertions are made on undecoded payload bytes: the recorder's public event lists
/// decode with <c>Encoding.ASCII</c>, which turns 0xFF into <c>?</c> and so cannot tell a surviving
/// literal from a dropped one.
/// </para>
/// </remarks>
public class InboundEscapingSweepTests
{
	private const byte SE = 240;
	private const byte SB = 250;
	private const byte IAC = 255;

	private static async Task<RecordingTelnetContext> Run(params byte[] bytes)
	{
		var recorder = new RecordingTelnetContext();
		await using var machine = new TelnetCoreMachine(recorder);
		await machine.StartAsync();
		await machine.FireAsync(bytes);
		return recorder;
	}

	/// <summary><c>IAC SB option … IAC SE</c>.</summary>
	private static byte[] Frame(byte option, params byte[] payload) =>
		[IAC, SB, option, .. payload, IAC, SE];

	private static string Hex(IEnumerable<byte> b) => string.Concat(b.Select(x => x.ToString("x2")));

	/// <summary>The payload bytes of a marker/data trace, concatenated.</summary>
	private static string DataOf(IReadOnlyList<(bool IsData, string Marker, byte[] Data)> trace) =>
		Hex(trace.Where(e => e.IsData).SelectMany(e => e.Data));

	/// <summary>The marker names of a trace, in order.</summary>
	private static string MarkersOf(IReadOnlyList<(bool IsData, string Marker, byte[] Data)> trace) =>
		string.Join(",", trace.Where(e => !e.IsData).Select(e => e.Marker));

	// =============================================================================================
	// Payload-carrying options: a doubled IAC becomes one literal 255 in the payload.
	// =============================================================================================

	[Test]
	public async Task MsdpCollapsesADoubledIacAndStillReadsTheMarkerAfterIt()
	{
		var r = await Run(Frame(69, 1, (byte)'K', 2, IAC, IAC, 1, (byte)'L'));

		await Assert.That(Hex(r.MsdpMessages.Single())).IsEqualTo("014b02ff014c")
			.Because("the 0xFF must survive and the MSDP_VAR after it must still be a marker");
	}

	[Test]
	public async Task GmcpCollapsesADoubledIacAndDoesNotEndOnTheSeAfterIt()
	{
		var r = await Run(Frame(201, (byte)'a', (byte)' ', IAC, IAC, SE));

		await Assert.That(Hex(r.GmcpMessages.Single())).IsEqualTo("6120fff0")
			.Because("IAC IAC is one data byte, so the SE after it is payload, not the terminator");
	}

	/// <summary>
	/// MSSP asserted on undecoded bytes: its event list would render the 0xFF as <c>?</c>, so a
	/// mutant dropping the literal would pass an assertion made there.
	/// </summary>
	[Test]
	public async Task MsspCollapsesADoubledIacAndStillReadsTheVarAfterIt()
	{
		var r = await Run(Frame(70, 1, (byte)'A', 2, IAC, IAC, 1, (byte)'B', 2, (byte)'C'));

		await Assert.That(DataOf(r.MsspTrace)).IsEqualTo("41ff4243")
			.Because("the literal 0xFF must reach the payload");
		await Assert.That(MarkersOf(r.MsspTrace)).IsEqualTo("started,VAR,VAL,VAR,VAL,ended")
			.Because("the MSSP_VAR immediately after the escaped byte must still be a marker");
	}

	[Test]
	public async Task NewEnvironCollapsesADoubledIacAndStillReadsTheVarAfterIt()
	{
		var r = await Run(Frame(39, 0, 0, (byte)'A', 1, IAC, IAC, 0, (byte)'B'));

		await Assert.That(DataOf(r.NewEnvironTrace)).IsEqualTo("41ff42");
		await Assert.That(MarkersOf(r.NewEnvironTrace)).IsEqualTo("started 0,VAR,VALUE,VAR,ended");
	}

	[Test]
	public async Task EnvironCollapsesADoubledIacAndStillReadsTheVarAfterIt()
	{
		var r = await Run(Frame(36, 0, 0, (byte)'A', 1, IAC, IAC, 0, (byte)'B'));

		await Assert.That(DataOf(r.EnvironTrace)).IsEqualTo("41ff42");
		await Assert.That(MarkersOf(r.EnvironTrace)).IsEqualTo("started 0,VAR,VALUE,VAR,ended");
	}

	/// <summary>A 255-column window is an ordinary terminal size, not a corner case.</summary>
	[Test]
	public async Task NawsCollapsesDoubledIacsInBothDimensions()
	{
		var r = await Run(Frame(31, IAC, IAC, 1, 0, IAC, IAC));

		await Assert.That(r.Windows.Single()).IsEqualTo((65281, 255));
	}

	[Test]
	public async Task LineModeCollapsesADoubledIacAndDoesNotEndOnTheSeAfterIt()
	{
		var r = await Run(Frame(34, 1, IAC, IAC, SE));

		var (kind, data) = r.LineModeMessages.Single();
		await Assert.That(kind).IsEqualTo((byte)1);
		await Assert.That(Hex(data)).IsEqualTo("fff0");
	}

	[Test]
	[Arguments((byte)24, "58ff54", new byte[] { 0, (byte)'X', IAC, IAC, (byte)'T' })]
	public async Task TerminalTypeCollapsesADoubledIac(byte option, string expected, byte[] payload)
	{
		var r = await Run(Frame(option, payload));

		await Assert.That(Hex(r.TerminalTypeReports.Single())).IsEqualTo(expected);
	}

	[Test]
	public async Task TerminalSpeedCollapsesADoubledIac()
	{
		var r = await Run(Frame(32, 0, (byte)'3', IAC, IAC, (byte)'8'));

		await Assert.That(Hex(r.TerminalSpeedReports.Single())).IsEqualTo("33ff38");
	}

	[Test]
	public async Task XDisplayLocationCollapsesADoubledIac()
	{
		var r = await Run(Frame(35, 0, IAC, IAC, (byte)':'));

		await Assert.That(Hex(r.XDisplayLocationReports.Single())).IsEqualTo("ff3a");
	}

	[Test]
	public async Task CharsetRequestCollapsesADoubledIac()
	{
		var r = await Run(Frame(42, 1, (byte)';', IAC, IAC, (byte)'u'));

		await Assert.That(Hex(r.CharsetRequests.Single())).IsEqualTo("3bff75");
	}

	[Test]
	public async Task CharsetTranslationTableCollapsesADoubledIac()
	{
		var r = await Run(Frame(42, 4, 1, IAC, IAC, 3));

		await Assert.That(Hex(r.CharsetTTables.Single())).IsEqualTo("01ff03");
	}

	[Test]
	public async Task AuthenticationCollapsesADoubledIac()
	{
		var r = await Run(Frame(37, 0, 1, IAC, IAC, 2));

		await Assert.That(Hex(r.AuthenticationIsMessages.Single())).IsEqualTo("01ff02");
	}

	[Test]
	public async Task EncryptionCollapsesADoubledIac()
	{
		var r = await Run(Frame(38, 0, IAC, IAC, 1));

		await Assert.That(Hex(r.EncryptionIsMessages.Single())).IsEqualTo("ff01");
	}

	// =============================================================================================
	// The negative twin: without the doubling, the same bytes must NOT decode the same way.
	// =============================================================================================

	/// <summary>
	/// Every case above passes a mutant that treats <c>On(IAC)</c> as "consume and carry on", because
	/// such a mutant also yields one 255 from two. These are the cases that separate un-doubling from
	/// swallowing: an <em>un</em>-doubled 255 is the start of a command, not a payload byte.
	/// </summary>
	[Test]
	public async Task AnUndoubledIacIsNotPayload()
	{
		var msdp = await Run(Frame(69, 1, (byte)'K', 2, (byte)'L'));
		await Assert.That(Hex(msdp.MsdpMessages.Single())).IsEqualTo("014b024c")
			.Because("baseline: no 255 anywhere when none was sent");

		// IAC SE here is the terminator, so the frame ends and 'X' is ordinary text.
		var r = await Run([IAC, SB, 69, 1, (byte)'K', IAC, SE, (byte)'X', (byte)'\n']);
		await Assert.That(Hex(r.MsdpMessages.Single())).IsEqualTo("014b")
			.Because("a single IAC before SE ends the frame; it is not a literal");
		await Assert.That(r.Lines).IsEquivalentTo(new[] { "X" });
	}

	// =============================================================================================
	// Marker-only states: IAC IAC SE is not a terminator there either.
	// =============================================================================================

	/// <summary>
	/// These states carry no payload, so their flag only ever had to recognise the terminator — and
	/// nine of them latched instead of toggling, which made <c>IAC IAC SE</c> end the frame one byte
	/// early. RFC 855 does not exempt a payload-less subnegotiation from the doubling rule.
	/// </summary>
	[Test]
	public async Task MxpDoesNotEndOnADoubledIacAndDoesNotLeakThePayloadIntoText()
	{
		var r = await Run([IAC, SB, 91, IAC, IAC, SE, (byte)'A', IAC, SE, (byte)'h', (byte)'i', (byte)'\n']);

		await Assert.That(r.MxpStarts).IsEqualTo(1)
			.Because("the frame ends at the real IAC SE, not the escaped one");
		await Assert.That(r.Lines).IsEquivalentTo(new[] { "hi" })
			.Because("the 'A' is inside the subnegotiation; it used to leak into the text stream");
	}

	[Test]
	[Arguments((byte)86)]
	[Arguments((byte)87)]
	public async Task MccpMarkersDoNotEndOnADoubledIac(byte option)
	{
		var r = await Run([IAC, SB, option, IAC, IAC, SE]);

		var markers = option == 86 ? r.Mccp2Markers : r.Mccp3Markers;

		await Assert.That(markers).IsEqualTo(0)
			.Because("compression starting at the wrong stream position is what this module's own comment warns of");
	}

	/// <summary>
	/// The three <c>SEND</c> requests and the two ending markers, all of which latched.
	/// </summary>
	[Test]
	public async Task ARequestOrEndingMarkerDoesNotFireOnADoubledIac()
	{
		var ttype = await Run([IAC, SB, 24, 1, IAC, IAC, SE]);
		await Assert.That(ttype.TerminalTypeRequests).IsEqualTo(0);

		var tspeed = await Run([IAC, SB, 32, 1, IAC, IAC, SE]);
		await Assert.That(tspeed.TerminalSpeedRequests).IsEqualTo(0);

		var xdisp = await Run([IAC, SB, 35, 1, IAC, IAC, SE]);
		await Assert.That(xdisp.XDisplayLocationRequests).IsEqualTo(0);

		var charset = await Run([IAC, SB, 42, 3, IAC, IAC, SE]);
		await Assert.That(charset.CharsetRejections).IsEqualTo(0);

		var encrypt = await Run([IAC, SB, 38, 4, IAC, IAC, SE]);
		await Assert.That(encrypt.EncryptionEnds).IsEqualTo(0);
	}

	/// <summary>
	/// The live bug in <c>TspeedSend</c>: its malformed-byte handler had an empty body where both its
	/// siblings clear the flag, so a stray byte between an <c>IAC</c> and an unrelated later
	/// <c>SE</c> still satisfied the terminator guard. <c>MalformedSubnegotiationRecoveryTests</c>
	/// covers this class for every other such state and missed only this one.
	/// </summary>
	[Test]
	public async Task ATerminalSpeedRequestNeedsAnAdjacentIacSe()
	{
		var r = await Run([IAC, SB, 32, 1, IAC, 1, SE]);

		await Assert.That(r.TerminalSpeedRequests).IsEqualTo(0)
			.Because("the IAC and the SE are not adjacent, so this is not a terminator");
	}

	/// <summary>And the well-formed request still fires, in all three.</summary>
	[Test]
	public async Task AWellFormedRequestStillFires()
	{
		await Assert.That((await Run([IAC, SB, 24, 1, IAC, SE])).TerminalTypeRequests).IsEqualTo(1);
		await Assert.That((await Run([IAC, SB, 32, 1, IAC, SE])).TerminalSpeedRequests).IsEqualTo(1);
		await Assert.That((await Run([IAC, SB, 35, 1, IAC, SE])).XDisplayLocationRequests).IsEqualTo(1);
		await Assert.That((await Run([IAC, SB, 91, IAC, SE])).MxpStarts).IsEqualTo(1);
		await Assert.That((await Run([IAC, SB, 86, IAC, SE])).Mccp2Markers).IsEqualTo(1);
	}

	/// <summary>
	/// FLOWCONTROL was the one payload-carrying option with no un-doubling at all: it latched
	/// <em>and</em> took bytes one at a time, so a doubled IAC ended the frame early and the literal
	/// never reached the command byte.
	/// </summary>
	[Test]
	public async Task FlowControlKeepsAnEscapedCommandByte()
	{
		var r = await Run(Frame(33, IAC, IAC));

		await Assert.That(r.FlowControlCommands).IsEquivalentTo(new List<byte> { 255 })
			.Because("RFC 1372 defines no command 255, but dropping it and ending early are separate wrongs");
	}

	[Test]
	public async Task FlowControlStillReadsAnOrdinaryCommand()
	{
		var r = await Run(Frame(33, 1));

		await Assert.That(r.FlowControlCommands).IsEquivalentTo(new List<byte> { 1 });
	}

	// =============================================================================================
	// A bare SE inside a payload is data, which is the other half of what the flag means.
	// =============================================================================================

	/// <summary>
	/// Reached by guard fall-through rather than by a transition of its own, so a mutant making the
	/// terminator guard unconditional passes everything above and fails here.
	/// </summary>
	[Test]
	public async Task ABareSeInsideAPayloadIsData()
	{
		var r = await Run(Frame(24, 0, (byte)'A', SE, (byte)'B'));

		await Assert.That(Hex(r.TerminalTypeReports.Single())).IsEqualTo("41f042")
			.Because("only an SE immediately after an IAC ends a subnegotiation");
	}

	// =============================================================================================
	// Bounded buffers: the two states that accumulated without a ceiling.
	// =============================================================================================

	/// <summary>
	/// CHARSET's request/acceptance text and TSPEED's speed text buffered into a <c>List&lt;byte&gt;</c>
	/// with no ceiling, while five sibling states carry an 8192-byte cap with a comment saying it
	/// "exists only to bound a peer that never sends IAC SE". A peer that opens the subnegotiation
	/// and simply keeps sending grew the list until the process ran out of memory.
	/// </summary>
	/// <remarks>
	/// An overflowed buffer is dropped rather than reported truncated, which is what the capped
	/// siblings do: a cut-short charset list names a different set of charsets than the peer offered,
	/// and a cut-short "transmit,receive" parses as a different speed.
	/// </remarks>
	[Test]
	public async Task AnOversizedCharsetRequestIsBoundedAndDropped()
	{
		var flood = new byte[9000];
		Array.Fill(flood, (byte)'a');

		var r = await Run(Frame(42, [1, .. flood]));

		await Assert.That(r.CharsetRequests).IsEmpty()
			.Because("a truncated charset list is different data, not less of it");
	}

	[Test]
	public async Task AnOversizedTerminalSpeedIsBoundedAndDropped()
	{
		var flood = new byte[9000];
		Array.Fill(flood, (byte)'1');

		var r = await Run(Frame(32, [0, .. flood]));

		await Assert.That(r.TerminalSpeedReports).IsEmpty();
	}

	/// <summary>And a report inside the ceiling still arrives intact, escaping and all.</summary>
	[Test]
	public async Task AReportInsideTheCeilingIsStillReported()
	{
		var text = new byte[4000];
		Array.Fill(text, (byte)'u');

		var charset = await Run(Frame(42, [1, (byte)';', .. text, IAC, IAC]));
		await Assert.That(charset.CharsetRequests.Single().Length).IsEqualTo(4002);
		await Assert.That(charset.CharsetRequests.Single()[^1]).IsEqualTo(IAC);

		var tspeed = await Run(Frame(32, [0, .. text, IAC, IAC]));
		await Assert.That(tspeed.TerminalSpeedReports.Single().Length).IsEqualTo(4001);
	}
}
