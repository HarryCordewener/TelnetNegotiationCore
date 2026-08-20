using System.Collections.Generic;
using System.Threading.Tasks;
using TUnit.Core;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Protocols;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// When a bare <c>IAC GA</c> or <c>IAC EOR</c> from the server is a prompt boundary and when it is a
/// NOP — which the two RFCs answer, in the same sentence shape, and which this library answered
/// neither way.
/// </summary>
/// <remarks>
/// <para>
/// GA was reached by <c>tdome.nukefire.org:4000</c> (2026-08-20): a default NVT that negotiates
/// neither EOR nor SUPPRESS-GO-AHEAD and ends every prompt with <c>IAC GA</c>, which is precisely
/// what RFC 854 says it must do — "when a process at one end of a TELNET connection cannot proceed
/// without input from the other end, the process must transmit the TELNET Go Ahead (GA) command".
/// Nothing raised a prompt for it, so a client holding an unterminated line until a boundary arrives
/// never showed the character-creation prompt at all; the session looked like a server sending
/// nothing back.
/// </para>
/// <para>
/// EOR is the mirror defect found reading RFC 885 against the code: "When the END-OF-RECORD option is
/// not in effect, the IAC EOR command should be treated as a NOP if received". The prompt callback
/// was gated on <c>IsEnabled</c>, which is plugin lifetime and true from initialisation, so an
/// unnegotiated EOR raised a prompt on any connection that merely registered the plugin.
/// </para>
/// </remarks>
public class PromptMarkerTests : BaseTest
{
	private static readonly byte[] s_goAhead = [(byte)Trigger.IAC, (byte)Trigger.GA];
	private static readonly byte[] s_endOfRecord = [(byte)Trigger.IAC, (byte)Trigger.EOR];
	private static readonly byte[] s_willSga = [(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.SUPPRESSGOAHEAD];
	private static readonly byte[] s_willEor = [(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.TELOPT_EOR];

	private static Task<TelnetInterpreter> BuildClientAsync(List<string> prompts) =>
		BuildAndWaitAsync(new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(_ => ValueTask.CompletedTask)
			.AddPlugin<EORProtocol>()
				.OnPrompt(() =>
				{
					prompts.Add("EOR");
					return ValueTask.CompletedTask;
				})
			.AddPlugin<SuppressGoAheadProtocol>()
				.OnPrompt(() =>
				{
					prompts.Add("GA");
					return ValueTask.CompletedTask;
				}));

	private static Task<TelnetInterpreter> BuildServerAsync(List<string> prompts) =>
		BuildAndWaitAsync(new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(_ => ValueTask.CompletedTask)
			.AddPlugin<EORProtocol>()
				.OnPrompt(() =>
				{
					prompts.Add("EOR");
					return ValueTask.CompletedTask;
				})
			.AddPlugin<SuppressGoAheadProtocol>()
				.OnPrompt(() =>
				{
					prompts.Add("GA");
					return ValueTask.CompletedTask;
				}));

	/// <summary>
	/// The NukeFire case, and the default NVT of RFC 854: nothing negotiated, GA ends the prompt.
	/// </summary>
	[Test]
	public async Task AGoAheadIsAPromptWhenNothingSuppressedIt()
	{
		var prompts = new List<string>();
		var client = await BuildClientAsync(prompts);

		await InterpretAndWaitAsync(client, s_goAhead);
		await PollUntilAsync(() => prompts.Count > 0);

		await Assert.That(prompts).IsEquivalentTo(new[] { "GA" });

		await client.DisposeAsync();
	}

	/// <summary>
	/// RFC 858: once suppression is in effect "the IAC GA command should be treated as a NOP if
	/// received, although IAC GA should not normally be sent in this mode".
	/// </summary>
	[Test]
	public async Task AGoAheadIsANopOnceSuppressGoAheadIsInEffect()
	{
		var prompts = new List<string>();
		var client = await BuildClientAsync(prompts);

		await InterpretAndWaitAsync(client, s_willSga);
		await InterpretAndWaitAsync(client, s_goAhead);

		// Nothing to poll for: the assertion is that no callback happens, so the wait is the
		// interpreter draining both batches, which InterpretAndWaitAsync already does.
		await Assert.That(prompts).IsEmpty();

		await client.DisposeAsync();
	}

	/// <summary>
	/// EOR does not suppress GA. RFC 885 is about a different marker and says nothing about
	/// Go-Ahead, so a server that negotiated EOR and sends GA anyway is still saying it cannot
	/// proceed without input.
	/// </summary>
	[Test]
	public async Task NegotiatingEorDoesNotTakeTheMeaningOutOfAGoAhead()
	{
		var prompts = new List<string>();
		var client = await BuildClientAsync(prompts);

		await InterpretAndWaitAsync(client, s_willEor);
		await InterpretAndWaitAsync(client, s_goAhead);
		await PollUntilAsync(() => prompts.Count > 0);

		await Assert.That(prompts).IsEquivalentTo(new[] { "GA" });

		await client.DisposeAsync();
	}

	/// <summary>RFC 885: an EOR is a prompt boundary once the option is in effect.</summary>
	[Test]
	public async Task AnEndOfRecordIsAPromptOnceTheOptionIsInEffect()
	{
		var prompts = new List<string>();
		var client = await BuildClientAsync(prompts);

		await InterpretAndWaitAsync(client, s_willEor);
		await InterpretAndWaitAsync(client, s_endOfRecord);
		await PollUntilAsync(() => prompts.Count > 0);

		await Assert.That(prompts).IsEquivalentTo(new[] { "EOR" });

		await client.DisposeAsync();
	}

	/// <summary>
	/// RFC 885: "When the END-OF-RECORD option is not in effect, the IAC EOR command should be
	/// treated as a NOP if received".
	/// </summary>
	[Test]
	public async Task AnEndOfRecordIsANopWhileTheOptionIsNotInEffect()
	{
		var prompts = new List<string>();
		var client = await BuildClientAsync(prompts);

		await InterpretAndWaitAsync(client, s_endOfRecord);

		await Assert.That(prompts).IsEmpty();

		await client.DisposeAsync();
	}

	/// <summary>
	/// A server told a GA is told nothing. RFC 854 says of the user-to-server direction only that
	/// "GAs may be sent at any time, but need not ever be sent", so a GA arriving here is not a
	/// statement about anything — and this is also the direction whose suppression the plugin does
	/// not record, because in server mode its flag tracks the peer's <c>DO</c>, which asks this end
	/// to suppress and says nothing about what the peer sends. This pins the client-only wiring: a
	/// GA must still cost nothing but a state transition.
	/// </summary>
	[Test]
	public async Task AGoAheadReachingAServerRaisesNoPrompt()
	{
		var prompts = new List<string>();
		var server = await BuildServerAsync(prompts);

		await InterpretAndWaitAsync(server, s_goAhead);

		await Assert.That(prompts).IsEmpty();

		await server.DisposeAsync();
	}

	/// <summary>
	/// Both markers on one connection raise one prompt each and neither raises the other's. The pair
	/// share a callback in <c>AddDefaultMUDProtocols</c>, so a consumer that cannot tell them apart
	/// still must not be told twice about one boundary.
	/// </summary>
	[Test]
	public async Task EachMarkerRaisesItsOwnPromptAndOnlyItsOwn()
	{
		var prompts = new List<string>();
		var client = await BuildClientAsync(prompts);

		await InterpretAndWaitAsync(client, s_willEor);
		await InterpretAndWaitAsync(client, s_endOfRecord);
		await InterpretAndWaitAsync(client, s_goAhead);
		await PollUntilAsync(() => prompts.Count > 1);

		await Assert.That(prompts).IsEquivalentTo(new[] { "EOR", "GA" });

		await client.DisposeAsync();
	}
}
