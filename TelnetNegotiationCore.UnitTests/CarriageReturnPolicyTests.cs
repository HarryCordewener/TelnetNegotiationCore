using System.Linq;
using System.Threading.Tasks;
using TelnetNegotiationCore.Machine;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// What the core machine does with a carriage return, pinned deliberately.
/// </summary>
/// <remarks>
/// <para>
/// <c>TelnetCoreModule.DropReturn</c> and <c>DropReturnInLine</c> discard a CR wherever it arrives,
/// and <c>EndOfLine</c> submits on an LF. So <c>CR LF</c>, a bare <c>LF</c> and a bare <c>CR</c>
/// followed by <c>LF</c> all end a line identically, and a trailing <c>CR</c> with nothing after it
/// contributes nothing at all.
/// </para>
/// <para>
/// <b>NUL is ordinary text.</b> It has no transition of its own, so RFC 854's <c>CR NUL</c> — which
/// the RFC defines as a bare carriage return in the data — reaches a consumer as a literal 0x00
/// inside the submitted line. That is a divergence from RFC 854, it predates the StateAlchemist
/// migration, and changing it would change what every existing consumer receives. It is pinned here
/// rather than fixed so that it stays a deliberate choice and a property-based test can compare
/// against the real policy instead of an invented one.
/// </para>
/// </remarks>
public class CarriageReturnPolicyTests
{
	private static async Task<RecordingTelnetContext> Run(byte[] bytes)
	{
		var recorder = new RecordingTelnetContext();
		await using var machine = new TelnetCoreMachine(recorder);
		await machine.StartAsync();
		await machine.FireAsync(bytes);
		return recorder;
	}

	[Test]
	public async Task CrLfEndsALine()
	{
		var recorder = await Run([.. "hello\r\n"u8]);

		await Assert.That(recorder.Lines).IsEquivalentTo(new[] { "hello" });
		await Assert.That(recorder.PendingText).IsEqualTo(string.Empty);
	}

	[Test]
	public async Task ABareLineFeedEndsALineToo()
	{
		var recorder = await Run([.. "hello\n"u8]);

		await Assert.That(recorder.Lines).IsEquivalentTo(new[] { "hello" });
	}

	[Test]
	public async Task ABareLineFeedOnItsOwnSubmitsAnEmptyLine()
	{
		var recorder = await Run([(byte)'\n']);

		await Assert.That(recorder.Lines).IsEquivalentTo(new[] { string.Empty });
	}

	[Test]
	public async Task ATrailingCarriageReturnContributesNothing()
	{
		var recorder = await Run([(byte)'\r']);

		await Assert.That(recorder.Lines).IsEmpty();
		await Assert.That(recorder.PendingText).IsEqualTo(string.Empty);
	}

	[Test]
	public async Task ACarriageReturnInTheMiddleOfALineIsDropped()
	{
		var recorder = await Run([.. "ab\rcd\n"u8]);

		await Assert.That(recorder.Lines).IsEquivalentTo(new[] { "abcd" });
	}

	/// <summary>
	/// RFC 854 defines CR NUL as a bare carriage return in the data. This library drops the CR and
	/// delivers the NUL as text, so a consumer sees a 0x00 in the line. Pinned, not endorsed; see
	/// the remarks on this class.
	/// </summary>
	[Test]
	public async Task CrNulDeliversALiteralNulIntoTheLine()
	{
		var recorder = await Run([.. "ab\r"u8, 0, .. "cd\n"u8]);

		await Assert.That(recorder.Lines.Count).IsEqualTo(1);
		await Assert.That(recorder.Lines[0].Any(c => c == '\0'))
			.IsTrue()
			.Because("RFC 854's CR NUL currently reaches the consumer as a literal 0x00");
		await Assert.That(recorder.Lines[0].Replace("\0", string.Empty)).IsEqualTo("abcd");
	}
}
