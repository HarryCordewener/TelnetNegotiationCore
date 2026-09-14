#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Protocols;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// <see cref="SuppressGoAheadProtocol.ShouldUseEORFallback"/>: whether an outbound prompt has to
/// fall back to <c>IAC EOR</c> because this end promised not to send <c>IAC GA</c>.
/// </summary>
/// <remarks>
/// <para>
/// Public, and until now entirely untested — which is how it came to be wrong on both of its two
/// terms. The Go-Ahead term asked <c>IsGoAheadSuppressed</c>, the <em>peer's</em> direction, which
/// says nothing about whether this end may still send a GA; RFC 858 §5 makes the two independent.
/// The EOR term asked <c>EORProtocol.IsEnabled</c>, which is plugin lifetime rather than negotiated
/// state: true from initialisation onwards for every registered plugin, so it answered "yes, use
/// EOR" on a connection where EOR had never been negotiated at all.
/// </para>
/// <para>
/// The load-bearing test is the last one. This method answers the same question
/// <c>PromptTerminator</c> decides for itself, so the two agreeing across every combination is the
/// property worth pinning — a method that disagrees with what the library actually sends is worse
/// than no method.
/// </para>
/// </remarks>
public class EorFallbackTests : BaseTest
{
	private const byte IAC = 255;
	private const byte GA = 249;
	private const byte EOR_MARKER = 239;
	private const byte EOR_OPTION = 25;
	private const byte SGA_OPTION = 3;

	private sealed record Harness(TelnetInterpreter Interpreter, List<byte[]> Sent);

	/// <summary>
	/// Builds a server with both plugins and feeds it the given verbs. A <c>DO</c> establishes
	/// <em>this</em> end's own direction for both options; a <c>WILL</c> establishes the peer's.
	/// </summary>
	private static async Task<Harness> BuildAsync(params (byte Verb, byte Option)[] negotiation)
	{
		var sent = new List<byte[]>();

		var interpreter = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(silentLogger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(f => { lock (sent) { sent.Add(f.ToArray()); } return ValueTask.CompletedTask; })
			.AddPlugin<EORProtocol>()
			.AddPlugin<SuppressGoAheadProtocol>()
			.BuildAsync();

		await interpreter.WaitForProcessingAsync();
		await Task.Delay(150);

		foreach (var (verb, option) in negotiation)
		{
			await InterpretAndWaitAsync(interpreter, [IAC, verb, option]);
		}

		await Task.Delay(100);
		lock (sent) { sent.Clear(); }

		return new Harness(interpreter, sent);
	}

	private static bool Fallback(Harness h) =>
		h.Interpreter.PluginManager!.GetPlugin<SuppressGoAheadProtocol>()!.ShouldUseEORFallback();

	/// <summary>The last two bytes a prompt actually put on the wire.</summary>
	private static async Task<byte[]> PromptTerminatorOf(Harness h)
	{
		lock (h.Sent) { h.Sent.Clear(); }

		await h.Interpreter.SendPromptAsync("hi"u8.ToArray());
		await PollUntilAsync(() => { lock (h.Sent) { return h.Sent.Count > 0; } }, timeoutMs: 400);

		byte[] frame;
		lock (h.Sent) { frame = h.Sent.LastOrDefault() ?? []; }

		return frame.Length >= 2 ? [frame[frame.Length - 2], frame[frame.Length - 1]] : frame;
	}

	private static string Hex(byte[] b) => string.Concat(b.Select(x => x.ToString("x2")));

	[Test]
	public async Task NothingNegotiatedMeansNoFallback()
	{
		await using var h = (await BuildAsync()).Interpreter;

		await Assert.That(h.PluginManager!.GetPlugin<SuppressGoAheadProtocol>()!.ShouldUseEORFallback())
			.IsFalse().Because("Go-Ahead is still available, so nothing needs replacing");
	}

	/// <summary>
	/// The peer suppressing <em>its own</em> outbound Go-Ahead is not this end's promise, and does
	/// not stop this end sending a GA. This is the term that used to read the wrong direction.
	/// </summary>
	[Test]
	public async Task ThePeersOwnSuppressionDoesNotForceAFallback()
	{
		var h = await BuildAsync(((byte)Trigger.WILL, SGA_OPTION), ((byte)Trigger.DO, EOR_OPTION));
		await using var _ = h.Interpreter;

		await Assert.That(Fallback(h))
			.IsFalse().Because("RFC 858 §5: the peer's direction is independent of this end's");
		await Assert.That(Hex(await PromptTerminatorOf(h))).IsEqualTo(Hex([IAC, EOR_MARKER]))
			.Because("EOR still wins on its own merits, but not because of any fallback");
	}

	/// <summary>
	/// This end suppressed its own Go-Ahead but EOR was never negotiated. The old code answered
	/// "yes" here, because a registered plugin reports <c>IsEnabled</c> from initialisation onwards.
	/// </summary>
	[Test]
	public async Task SuppressedGoAheadWithoutNegotiatedEorIsNotAFallback()
	{
		var h = await BuildAsync(((byte)Trigger.DO, SGA_OPTION));
		await using var _ = h.Interpreter;

		await Assert.That(Fallback(h))
			.IsFalse().Because("the EOR plugin being registered is not the peer having agreed to anything");

		await Assert.That(Hex(await PromptTerminatorOf(h))).IsEqualTo(Hex([(byte)'\r', (byte)'\n']))
			.Because("no marker is available, so a prompt ends as a line does");
	}

	[Test]
	public async Task SuppressedGoAheadWithNegotiatedEorIsAFallback()
	{
		var h = await BuildAsync(((byte)Trigger.DO, SGA_OPTION), ((byte)Trigger.DO, EOR_OPTION));
		await using var _ = h.Interpreter;

		await Assert.That(Fallback(h))
			.IsTrue().Because("this end may not send GA and has agreed to mark its records");
		await Assert.That(Hex(await PromptTerminatorOf(h))).IsEqualTo(Hex([IAC, EOR_MARKER]));
	}

	/// <summary>
	/// A peer's <c>WILL EOR</c> turns on the inbound direction only, so it cannot license this end to
	/// substitute an outbound marker for the Go-Ahead it promised not to send.
	/// </summary>
	[Test]
	public async Task ThePeersInboundEorIsNotAFallbackForThisEndsSuppressedGoAhead()
	{
		var h = await BuildAsync(((byte)Trigger.DO, SGA_OPTION), ((byte)Trigger.WILL, EOR_OPTION));
		await using var _ = h.Interpreter;

		await Assert.That(Fallback(h))
			.IsFalse().Because("RFC 885 negotiates each direction separately; the peer's WILL is not permission to send");
		await Assert.That(Hex(await PromptTerminatorOf(h))).IsEqualTo(Hex([(byte)'\r', (byte)'\n']));
	}

	/// <summary>
	/// The property that matters: this method and <c>PromptTerminator</c> answer the same question
	/// from the same two directions, so they must never disagree. Across all sixteen combinations of
	/// the two options' two verbs.
	/// </summary>
	[Test]
	public async Task TheFallbackAnswerNeverDisagreesWithWhatAPromptActuallySends()
	{
		byte[] verbs = [(byte)Trigger.DO, (byte)Trigger.WILL, (byte)Trigger.DONT, (byte)Trigger.WONT];

		foreach (var sgaVerb in verbs)
		{
			foreach (var eorVerb in verbs)
			{
				var h = await BuildAsync((sgaVerb, SGA_OPTION), (eorVerb, EOR_OPTION));
				await using var _ = h.Interpreter;

				var claimed = Fallback(h);
				var actual = Hex(await PromptTerminatorOf(h)) == Hex([IAC, EOR_MARKER]);
				var sentGoAhead = Hex(await PromptTerminatorOf(h)) == Hex([IAC, GA]);

				// The method claims a fallback only where a prompt genuinely ends with EOR *and*
				// Go-Ahead was genuinely unavailable -- claiming it anywhere else is the disagreement.
				if (claimed)
				{
					await Assert.That(actual).IsTrue()
						.Because($"SGA {sgaVerb} / EOR {eorVerb}: claimed an EOR fallback, but the prompt did not use EOR");
					await Assert.That(sentGoAhead).IsFalse()
						.Because($"SGA {sgaVerb} / EOR {eorVerb}: claimed a fallback while still sending GA");
				}
			}
		}
	}
}
