using System.Linq;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// The liveness oracle the generated properties share: a byte sequence that returns the machine to
/// <c>Idle</c> from any state, a probe line to send afterwards, and the check that it arrived.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Resync"/> is <c>IAC SE IAC SE</c>. One pair is not enough: from <c>ReadingOption</c>
/// the <c>IAC</c> is bound as the subnegotiation's option byte and the <c>SE</c> becomes payload;
/// from <c>EndSubNegotiation</c> the <c>IAC</c> is consumed by <c>EscapedInPayload</c> as a literal
/// 255 and the <c>SE</c> is payload again. Both leave the machine inside <c>SubNegotiating</c>,
/// which the second pair then ends. <c>Idle</c> is closed under the sequence, because <c>IAC</c>
/// moves to <c>StartNegotiation</c> and <c>SE</c> is not a command there, so <c>UnknownCommand</c>
/// returns to <c>Idle</c>.
/// </para>
/// <para>
/// The trailing <c>CR LF</c> flushes whatever partial line the preceding garbage accumulated, so
/// that <see cref="ProbeLine"/> is compared exactly rather than by suffix. Without it a stream
/// ending in <c>"leftover"</c> would submit <c>"leftoverREGRESSION_PROBE"</c>.
/// </para>
/// <para>
/// <c>ResyncTests</c> is the evidence that this works for every option this library implements, and
/// pins the two cases that make a single pair insufficient. Shorten this and those tests fail.
/// </para>
/// </remarks>
internal static class TelnetProbe
{
	private const byte SE = 240;
	private const byte IAC = 255;

	/// <summary>Returns the machine to <c>Idle</c> from any state, then flushes the partial line.</summary>
	public static byte[] Resync { get; } = [IAC, SE, IAC, SE, (byte)'\r', (byte)'\n'];

	/// <summary>
	/// A line that no generator emits and no protocol payload contains, so that seeing it proves
	/// the machine is parsing text again rather than that a coincidence occurred.
	/// </summary>
	public static byte[] ProbeLine { get; } = [.. "REGRESSION_PROBE\r\n"u8];

	/// <summary>The text <see cref="ProbeLine"/> submits when the machine has recovered.</summary>
	public const string ProbeText = "REGRESSION_PROBE";

	/// <summary>Whether the probe line arrived as its own submitted line.</summary>
	public static bool Recovered(RecordingTelnetContext recorder) =>
		recorder.Lines.Any(line => line == ProbeText);
}
