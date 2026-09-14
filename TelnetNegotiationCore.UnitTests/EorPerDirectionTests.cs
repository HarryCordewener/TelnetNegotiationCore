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
/// EOR's two directions, which RFC 885 negotiates separately and which shared one flag.
/// </summary>
/// <remarks>
/// <para>
/// RFC 885: "the use of EORs must be negotiated independently for each direction". The two verbs
/// establish different facts. A peer's <c>WILL</c> is the <em>inbound</em> direction — "the sender of
/// this command requests permission to begin transmission of the Telnet END-OF-RECORD (EOR) code
/// when transmitting data characters" — so it says the peer will send markers. A peer's <c>DO</c> is
/// the <em>outbound</em> direction — "the sender of this command requests that the sender of data
/// start transmitting the EOR code when transmitting data" — so it asks <em>this</em> end to send
/// them.
/// </para>
/// <para>
/// One flag held both, and the two readers disagreed about which it meant:
/// <c>PromptTerminator</c> read it as outbound when choosing a prompt's terminator, and the inbound
/// bare-<c>IAC EOR</c> handler read it as inbound when deciding between a prompt and a NOP. So each
/// of the two single-verb cases was wrong in one of the two places, and neither was covered by a
/// test.
/// </para>
/// <para>
/// The wrong cells were not exotic. A server offering <c>WILL EOR</c> and receiving <c>DO</c> is the
/// ordinary server handshake, and it left the server treating any inbound <c>IAC EOR</c> as a prompt
/// although the client never said it would send one — which RFC 885 answers directly: "When the
/// END-OF-RECORD option is not in effect, the IAC EOR command should be treated as a NOP if
/// received". The mirror case, a client receiving only <c>WILL</c>, is the ordinary client handshake
/// and had the client marking prompts the server never agreed to receive.
/// </para>
/// </remarks>
public class EorPerDirectionTests : BaseTest
{
	private const byte IAC = 255;
	private const byte SE = 240;
	private const byte SB = 250;
	private const byte GA = 249;
	private const byte EOR_MARKER = 239;
	private const byte EOR_OPTION = 25;

	private sealed record Harness(
		TelnetInterpreter Interpreter,
		EORProtocol Eor,
		List<byte[]> Sent,
		List<bool> Prompts);

	private static async Task<Harness> BuildAsync(TelnetInterpreter.TelnetMode mode)
	{
		var sent = new List<byte[]>();
		var prompts = new List<bool>();

		var interpreter = await new TelnetInterpreterBuilder()
			.UseMode(mode)
			.UseLogger(silentLogger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(f => { lock (sent) { sent.Add(f.ToArray()); } return ValueTask.CompletedTask; })
			.AddPlugin<EORProtocol>()
				.OnPrompt(() => { prompts.Add(true); return ValueTask.CompletedTask; })
			.BuildAsync();

		await interpreter.WaitForProcessingAsync();
		await Task.Delay(150);
		lock (sent) { sent.Clear(); }

		return new Harness(interpreter, interpreter.PluginManager!.GetPlugin<EORProtocol>()!, sent, prompts);
	}

	/// <summary>Drives the negotiation verbs, then reports what the two directions became.</summary>
	private static async Task<Harness> NegotiateAsync(TelnetInterpreter.TelnetMode mode, params byte[] verbs)
	{
		var h = await BuildAsync(mode);

		foreach (var verb in verbs)
		{
			await InterpretAndWaitAsync(h.Interpreter, [IAC, verb, EOR_OPTION]);
		}

		await Task.Delay(100);
		return h;
	}

	/// <summary>The last two bytes of what a prompt put on the wire — its terminator.</summary>
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

	// ---------------------------------------------------------------------------------------------
	// The two directions are independent state.
	// ---------------------------------------------------------------------------------------------

	/// <summary>A <c>DO</c> establishes the outbound direction only.</summary>
	[Test]
	[Arguments(TelnetInterpreter.TelnetMode.Client)]
	[Arguments(TelnetInterpreter.TelnetMode.Server)]
	public async Task ADoEstablishesOnlyTheOutboundDirection(TelnetInterpreter.TelnetMode mode)
	{
		await using var h = (await NegotiateAsync(mode, (byte)Trigger.DO)).Interpreter;
		var eor = h.PluginManager!.GetPlugin<EORProtocol>()!;

		await Assert.That(eor.MarksOutboundRecords).IsTrue()
			.Because("RFC 885's DO asks this end to send the marker");
		await Assert.That(eor.PeerMarksRecords).IsFalse()
			.Because("a DO says nothing about whether the peer will send one");
	}

	/// <summary>A <c>WILL</c> establishes the inbound direction only.</summary>
	[Test]
	[Arguments(TelnetInterpreter.TelnetMode.Client)]
	[Arguments(TelnetInterpreter.TelnetMode.Server)]
	public async Task AWillEstablishesOnlyTheInboundDirection(TelnetInterpreter.TelnetMode mode)
	{
		await using var h = (await NegotiateAsync(mode, (byte)Trigger.WILL)).Interpreter;
		var eor = h.PluginManager!.GetPlugin<EORProtocol>()!;

		await Assert.That(eor.PeerMarksRecords).IsTrue()
			.Because("RFC 885's WILL announces the peer will send the marker");
		await Assert.That(eor.MarksOutboundRecords).IsFalse()
			.Because("a peer's WILL grants no permission to send one back");
	}

	// ---------------------------------------------------------------------------------------------
	// An inbound bare IAC EOR follows the inbound direction.
	// ---------------------------------------------------------------------------------------------

	/// <summary>
	/// The wrong cell on the ordinary server handshake: a server that offered <c>WILL</c> and was
	/// answered <c>DO</c> used to treat an inbound marker as a prompt.
	/// </summary>
	[Test]
	public async Task ABareEorIsANopWhenOnlyTheOutboundDirectionWasNegotiated()
	{
		var h = await NegotiateAsync(TelnetInterpreter.TelnetMode.Server, (byte)Trigger.DO);
		await using var _ = h.Interpreter;

		await InterpretAndWaitAsync(h.Interpreter, [.. "x"u8, IAC, EOR_MARKER]);
		await Task.Delay(150);

		await Assert.That(h.Prompts).IsEmpty()
			.Because("the peer never agreed to send EOR, and RFC 885 makes an unnegotiated IAC EOR a NOP");
	}

	[Test]
	public async Task ABareEorIsAPromptWhenTheInboundDirectionWasNegotiated()
	{
		var h = await NegotiateAsync(TelnetInterpreter.TelnetMode.Client, (byte)Trigger.WILL);
		await using var _ = h.Interpreter;

		await InterpretAndWaitAsync(h.Interpreter, [.. "x"u8, IAC, EOR_MARKER]);
		await PollUntilAsync(() => h.Prompts.Count > 0, timeoutMs: 400);

		await Assert.That(h.Prompts.Count).IsEqualTo(1)
			.Because("the peer said it would send EOR, so the marker is a prompt boundary");
	}

	// ---------------------------------------------------------------------------------------------
	// An outbound prompt's terminator follows the outbound direction.
	// ---------------------------------------------------------------------------------------------

	/// <summary>
	/// The mirror wrong cell, on the ordinary client handshake: a client answered <c>WILL</c> with
	/// <c>DO</c> and then marked its own prompts with a marker the peer never agreed to receive.
	/// </summary>
	[Test]
	public async Task APromptDoesNotUseEorWhenOnlyTheInboundDirectionWasNegotiated()
	{
		var h = await NegotiateAsync(TelnetInterpreter.TelnetMode.Client, (byte)Trigger.WILL);
		await using var _ = h.Interpreter;

		var terminator = await PromptTerminatorOf(h);

		await Assert.That(Hex(terminator)).IsEqualTo(Hex([IAC, GA]))
			.Because("the peer's WILL did not ask this end to mark anything; Go-Ahead still marks the turn");
	}

	[Test]
	public async Task APromptUsesEorWhenTheOutboundDirectionWasNegotiated()
	{
		var h = await NegotiateAsync(TelnetInterpreter.TelnetMode.Server, (byte)Trigger.DO);
		await using var _ = h.Interpreter;

		var terminator = await PromptTerminatorOf(h);

		await Assert.That(Hex(terminator)).IsEqualTo(Hex([IAC, EOR_MARKER]))
			.Because("the peer asked this end to mark its records");
	}

	// ---------------------------------------------------------------------------------------------
	// A refusal in one direction leaves the other alone.
	// ---------------------------------------------------------------------------------------------

	/// <summary>
	/// With one flag, a <c>WONT</c> after a <c>DO</c> switched off prompt marking the peer had
	/// explicitly asked for.
	/// </summary>
	[Test]
	public async Task AWontDoesNotWithdrawTheOutboundAgreement()
	{
		var h = await NegotiateAsync(TelnetInterpreter.TelnetMode.Server, (byte)Trigger.DO, (byte)Trigger.WONT);
		await using var _ = h.Interpreter;

		await Assert.That(h.Eor.MarksOutboundRecords).IsTrue()
			.Because("the peer asked for marked records and then said only that it would not send its own");

		var terminator = await PromptTerminatorOf(h);
		await Assert.That(Hex(terminator)).IsEqualTo(Hex([IAC, EOR_MARKER]));
	}

	/// <summary>
	/// And the mirror: a <c>DONT</c> after a <c>WILL</c> used to turn an inbound marker back into a
	/// NOP while the peer's <c>WILL</c> still stood, dropping real prompts.
	/// </summary>
	[Test]
	public async Task ADontDoesNotWithdrawTheInboundAgreement()
	{
		var h = await NegotiateAsync(TelnetInterpreter.TelnetMode.Client, (byte)Trigger.WILL, (byte)Trigger.DONT);
		await using var _ = h.Interpreter;

		await Assert.That(h.Eor.PeerMarksRecords).IsTrue()
			.Because("the peer said it would send EOR and then only forbade this end from doing so");

		await InterpretAndWaitAsync(h.Interpreter, [.. "x"u8, IAC, EOR_MARKER]);
		await PollUntilAsync(() => h.Prompts.Count > 0, timeoutMs: 400);

		await Assert.That(h.Prompts.Count).IsEqualTo(1);
	}

	// ---------------------------------------------------------------------------------------------
	// IsNegotiated is the aggregate, not whichever direction resolved last.
	// ---------------------------------------------------------------------------------------------

	/// <summary>
	/// <c>OnNegotiatedAsync</c> is transition-only, so reporting one direction's own outcome would
	/// let the second to resolve stomp the first. <c>MCCPProtocol</c> reports an aggregate for the
	/// same reason.
	/// </summary>
	[Test]
	public async Task IsNegotiatedReflectsEitherDirectionNotWhicheverSettledLast()
	{
		var h = await NegotiateAsync(TelnetInterpreter.TelnetMode.Server, (byte)Trigger.DO, (byte)Trigger.WONT);
		await using var _ = h.Interpreter;

		await Assert.That(h.Eor.IsNegotiated).IsTrue()
			.Because("the outbound direction is still live, so EOR is still negotiated");
	}

	/// <summary>
	/// <c>IsEOREnabled</c> is the aggregate and reports exactly what the single flag behind it used
	/// to: true once either direction is on. That is what keeps the split off a consumer's radar.
	/// </summary>
	[Test]
	[Arguments((byte)Trigger.DO, true)]
	[Arguments((byte)Trigger.WILL, true)]
	[Arguments((byte)Trigger.DONT, false)]
	[Arguments((byte)Trigger.WONT, false)]
	public async Task IsEorEnabledIsTheAggregateOfBothDirections(byte verb, bool expected)
	{
		var h = await NegotiateAsync(TelnetInterpreter.TelnetMode.Server, verb);
		await using var _ = h.Interpreter;

		await Assert.That(h.Eor.IsEOREnabled).IsEqualTo(expected);
		await Assert.That(h.Eor.IsEOREnabled)
			.IsEqualTo(h.Eor.PeerMarksRecords || h.Eor.MarksOutboundRecords);
	}

	/// <summary>Neither verb: nothing is on, and a prompt falls back to Go-Ahead.</summary>
	[Test]
	public async Task NeitherDirectionIsOnBeforeAnyNegotiation()
	{
		var h = await BuildAsync(TelnetInterpreter.TelnetMode.Client);
		await using var _ = h.Interpreter;

		await Assert.That(h.Eor.PeerMarksRecords).IsFalse();
		await Assert.That(h.Eor.MarksOutboundRecords).IsFalse();
		await Assert.That(h.Eor.IsEOREnabled).IsFalse();

		await InterpretAndWaitAsync(h.Interpreter, [.. "x"u8, IAC, EOR_MARKER]);
		await Task.Delay(150);
		await Assert.That(h.Prompts).IsEmpty();
	}

	// ---------------------------------------------------------------------------------------------
	// The manual knobs, and the lifecycle hooks that used to fabricate an agreement.
	// ---------------------------------------------------------------------------------------------

	/// <summary>
	/// <c>docs/protocols/eor.md</c> documents these as the server's own switch, so they drive the
	/// outbound direction and leave the peer's alone.
	/// </summary>
	[Test]
	public async Task TheManualKnobsDriveTheOutboundDirectionOnly()
	{
		var h = await BuildAsync(TelnetInterpreter.TelnetMode.Server);
		await using var _ = h.Interpreter;

		await h.Eor.EnableEORAsync();
		await Assert.That(h.Eor.MarksOutboundRecords).IsTrue();
		await Assert.That(h.Eor.PeerMarksRecords).IsFalse();

		await h.Eor.DisableEORAsync();
		await Assert.That(h.Eor.MarksOutboundRecords).IsFalse();
	}

	/// <summary>
	/// Enabling the plugin is not a negotiation. The lifecycle hooks used to write "EOR is on", so
	/// disabling and re-enabling the plugin through the manager fabricated an agreement no peer had
	/// made — and then a prompt went out marked.
	/// </summary>
	[Test]
	public async Task ReEnablingThePluginDoesNotFabricateAnAgreement()
	{
		var h = await BuildAsync(TelnetInterpreter.TelnetMode.Server);
		await using var _ = h.Interpreter;

		await h.Interpreter.PluginManager!.DisablePluginAsync<EORProtocol>();
		await h.Interpreter.PluginManager!.EnablePluginAsync<EORProtocol>();

		await Assert.That(h.Eor.MarksOutboundRecords).IsFalse()
			.Because("no peer ever asked this end to mark its records");
		await Assert.That(h.Eor.PeerMarksRecords).IsFalse();
	}
}
