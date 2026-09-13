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
/// EOR, GMCP, MSDP and MSSP in the direction their handlers were not written for.
/// </summary>
/// <remarks>
/// <para>
/// All four announce <c>WILL &lt;option&gt;</c> on initialisation when they are a server, which is
/// what each specification asks of a server. A server that then receives <c>DO</c> is being
/// <em>agreed with</em>, not asked, and RFC 1143 has that noted rather than answered — so a server's
/// silence there is correct, and is pinned below so a fix to the client side cannot quietly turn it
/// into a loop.
/// </para>
/// <para>
/// A client receiving an unsolicited <c>DO</c> is the opposite case: a request, and RFC 1143 leaves
/// no room for ignoring one — "a TELNET implementation MUST refuse (DONT/WONT) a request to enable
/// an option for which it does not comply with the appropriate protocol specification." Three of the
/// four answered nothing at all; the fourth answered with a subnegotiation, which RFC 854 does not
/// accept as an answer to a <c>DO</c> either.
/// </para>
/// <para>
/// Whether the answer accepts or refuses follows from that same "does not comply" — what this
/// library can actually do, not which role convention expects to be asked:
/// </para>
/// <list type="bullet">
/// <item><description><b>EOR</b> accepts. RFC 885 negotiates it "independently for each direction"
/// and asks the receiver of a <c>DO</c> to emit the marker, and
/// <c>SendPromptAsync</c>/<c>PromptTerminator</c> emit <c>IAC EOR</c> without consulting the
/// interpreter's mode.</description></item>
/// <item><description><b>GMCP</b> and <b>MSDP</b> accept. Their specifications have the server offer
/// and the client answer, so an unsolicited <c>DO</c> is atypical — but both say that once enabled
/// "both the client and the server can send" subnegotiations, and
/// <c>SendGMCPCommand</c>/<c>SendMSDPCommand</c> have no mode references at all.</description></item>
/// <item><description><b>MSSP</b> refuses. It reports a server's own status; the specification
/// defines no client-side report and every variable in it flows server to client, so a client has
/// nothing it could comply with.</description></item>
/// </list>
/// </remarks>
public class WrongDirectionNegotiationTests : BaseTest
{
	private const byte EOR_OPTION = 25;
	private const byte MSDP_OPTION = 69;
	private const byte MSSP_OPTION = 70;
	private const byte GMCP_OPTION = 201;

	private const byte IAC = 255;

	/// <summary>
	/// Fires one negotiation at a freshly built interpreter and returns every frame it sent in
	/// response, with whatever it offered on its own account already discarded.
	/// </summary>
	/// <remarks>
	/// An answer, when one is owed, goes out on the same byte-processing pass as the verb that
	/// provoked it, so <see cref="BaseTest.PollUntilAsync"/> returns as soon as it lands and a test
	/// asserting silence only has to outlast one pass.
	/// </remarks>
	private static async Task<List<byte[]>> AnswersTo(
		TelnetInterpreter.TelnetMode mode,
		Func<TelnetInterpreterBuilder, Task<TelnetInterpreter>> build,
		byte verb,
		byte option)
	{
		var sent = new List<byte[]>();

		void Record(ReadOnlyMemory<byte> frame)
		{
			lock (sent)
			{
				sent.Add(frame.ToArray());
			}
		}

		var interpreter = await build(new TelnetInterpreterBuilder()
			.UseMode(mode)
			.UseLogger(silentLogger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(frame => { Record(frame); return ValueTask.CompletedTask; }));

		await using (interpreter)
		{
			// A server's initial offers are not the answer under test.
			await interpreter.WaitForProcessingAsync();
			await Task.Delay(150);
			lock (sent)
			{
				sent.Clear();
			}

			await InterpretAndWaitAsync(interpreter, [IAC, verb, option]);
			await PollUntilAsync(() => Snapshot(sent).Count > 0, timeoutMs: 400);
		}

		return Snapshot(sent);
	}

	private static List<byte[]> Snapshot(List<byte[]> sent)
	{
		lock (sent)
		{
			return [.. sent];
		}
	}

	private static bool Contains(List<byte[]> sent, byte verb, byte option) =>
		sent.Any(f => f.Length >= 3 && f[0] == IAC && f[1] == verb && f[2] == option);

	/// <summary>Whether anything at all was said about <paramref name="option"/>.</summary>
	private static bool Mentions(List<byte[]> sent, byte option) =>
		sent.Any(f => f.Length >= 3 && f[2] == option);

	private static string Render(List<byte[]> sent) =>
		sent.Count == 0
			? "(nothing)"
			: string.Join(" | ", sent.Select(f => string.Concat(f.Select(b => b.ToString("x2")))));

	private static Task<TelnetInterpreter> AllFour(TelnetInterpreterBuilder b) =>
		b.AddPlugin<EORProtocol>()
			.AddPlugin<GMCPProtocol>()
			.AddPlugin<MSDPProtocol>()
			.AddPlugin<MSSPProtocol>()
			.BuildAsync();

	// ---------------------------------------------------------------------------------------------
	// A client asked to enable an option it can actually honour.
	// ---------------------------------------------------------------------------------------------

	/// <summary>
	/// RFC 885's <c>DO</c> asks the receiver to emit the marker — "the sender of this command
	/// requests that the sender of data start transmitting the EOR code when transmitting data" — and
	/// a client can: <c>PromptTerminator</c> picks <c>IAC EOR</c> on negotiated state alone.
	/// </summary>
	[Test]
	public async Task AClientAnswersDoEorWithWill()
	{
		var sent = await AnswersTo(TelnetInterpreter.TelnetMode.Client,
			b => b.AddPlugin<EORProtocol>().BuildAsync(), (byte)Trigger.DO, EOR_OPTION);

		await Assert.That(Contains(sent, (byte)Trigger.WILL, EOR_OPTION))
			.IsTrue().Because($"a client can emit IAC EOR, so RFC 1143 has it agree. Sent: {Render(sent)}");
	}

	[Test]
	public async Task AClientAnswersDoGmcpWithWill()
	{
		var sent = await AnswersTo(TelnetInterpreter.TelnetMode.Client,
			b => b.AddPlugin<GMCPProtocol>().BuildAsync(), (byte)Trigger.DO, GMCP_OPTION);

		await Assert.That(Contains(sent, (byte)Trigger.WILL, GMCP_OPTION))
			.IsTrue().Because($"both sides may send GMCP once enabled. Sent: {Render(sent)}");
	}

	[Test]
	public async Task AClientAnswersDoMsdpWithWill()
	{
		var sent = await AnswersTo(TelnetInterpreter.TelnetMode.Client,
			b => b.AddPlugin<MSDPProtocol>().BuildAsync(), (byte)Trigger.DO, MSDP_OPTION);

		await Assert.That(Contains(sent, (byte)Trigger.WILL, MSDP_OPTION))
			.IsTrue().Because($"both sides may send MSDP once enabled. Sent: {Render(sent)}");
	}

	/// <summary>
	/// Agreeing has to mean the option is actually on, or the next prompt goes out with the wrong
	/// terminator and the agreement was a lie.
	/// </summary>
	[Test]
	public async Task AClientThatAgreesToDoEorThenMarksItsPrompts()
	{
		var sent = new List<byte[]>();

		await using var client = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(silentLogger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(f => { sent.Add(f.ToArray()); return ValueTask.CompletedTask; })
			.AddPlugin<EORProtocol>()
			.BuildAsync();

		await InterpretAndWaitAsync(client, [IAC, (byte)Trigger.DO, EOR_OPTION]);
		await PollUntilAsync(() => sent.Count > 0, timeoutMs: 400);

		var eor = client.PluginManager!.GetPlugin<EORProtocol>();

		await Assert.That(eor!.IsEOREnabled)
			.IsTrue().Because("a WILL EOR that does not turn the option on is an empty promise");
	}

	// ---------------------------------------------------------------------------------------------
	// A client asked for something it cannot produce.
	// ---------------------------------------------------------------------------------------------

	[Test]
	public async Task AClientAnswersDoMsspWithWont()
	{
		var sent = await AnswersTo(TelnetInterpreter.TelnetMode.Client,
			b => b.AddPlugin<MSSPProtocol>().BuildAsync(), (byte)Trigger.DO, MSSP_OPTION);

		await Assert.That(Contains(sent, (byte)Trigger.WONT, MSSP_OPTION))
			.IsTrue().Because($"MSSP defines no client-side report, so RFC 1143 has it refused. Sent: {Render(sent)}");
	}

	/// <summary>
	/// What a client used to answer with: an <c>IAC SB MSSP IAC SE</c> carrying no variables at all.
	/// A subnegotiation is not an answer to a <c>DO</c> under RFC 854, and an empty report is not one
	/// under MSSP.
	/// </summary>
	[Test]
	public async Task AClientDoesNotServeAnMsspReportForADo()
	{
		var sent = await AnswersTo(TelnetInterpreter.TelnetMode.Client,
			b => b.AddPlugin<MSSPProtocol>().BuildAsync(), (byte)Trigger.DO, MSSP_OPTION);

		await Assert.That(sent.Any(f => f.Length >= 3 && f[1] == (byte)Trigger.SB))
			.IsFalse().Because($"a client has no status of its own to report. Sent: {Render(sent)}");
	}

	/// <summary>
	/// A refusal is not a state change dressed as one: the option must stay off, or the client keeps
	/// a plugin that believes it negotiated something it just declined.
	/// </summary>
	[Test]
	public async Task AClientThatRefusesMsspDoesNotMarkItNegotiated()
	{
		await using var client = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(silentLogger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(_ => ValueTask.CompletedTask)
			.AddPlugin<MSSPProtocol>()
			.BuildAsync();

		await InterpretAndWaitAsync(client, [IAC, (byte)Trigger.DO, MSSP_OPTION]);
		await Task.Delay(150);

		var mssp = client.PluginManager!.GetPlugin<MSSPProtocol>();

		await Assert.That(mssp!.IsNegotiated)
			.IsFalse().Because("the client refused, so nothing was negotiated");
	}

	// ---------------------------------------------------------------------------------------------
	// The server side of a DO, which is right today and must stay that way.
	// ---------------------------------------------------------------------------------------------

	/// <summary>
	/// A server announced <c>WILL</c> at startup, so a <c>DO</c> is the agreement to it. Answering
	/// with a <c>WILL</c> of its own is what RFC 1143 warns starts a loop.
	/// </summary>
	[Test]
	[Arguments(EOR_OPTION)]
	[Arguments(GMCP_OPTION)]
	[Arguments(MSDP_OPTION)]
	[Arguments(MSSP_OPTION)]
	public async Task AServerDoesNotAnswerTheAgreementToItsOwnOffer(byte option)
	{
		var sent = await AnswersTo(TelnetInterpreter.TelnetMode.Server, AllFour, (byte)Trigger.DO, option);

		var answered = sent.Any(f => f.Length >= 3 && f[2] == option
			&& (f[1] == (byte)Trigger.WILL || f[1] == (byte)Trigger.WONT));

		await Assert.That(answered)
			.IsFalse().Because($"option {option}: the DO was our own WILL coming back. Sent: {Render(sent)}");
	}

	/// <summary>
	/// The exception to that silence, and the one the MSSP specification spells out: "if the server
	/// receives IAC DO MSSP it should respond with: IAC SB MSSP MSSP_VAR "variable" MSSP_VAL
	/// "value"...IAC SE." The report is the response, and gating the client side must not take it
	/// away.
	/// </summary>
	[Test]
	public async Task AServerStillServesItsMsspReportForADo()
	{
		var sent = await AnswersTo(TelnetInterpreter.TelnetMode.Server, AllFour, (byte)Trigger.DO, MSSP_OPTION);

		await Assert.That(sent.Any(f => f.Length >= 3 && f[0] == IAC && f[1] == (byte)Trigger.SB && f[2] == MSSP_OPTION))
			.IsTrue().Because($"the report is what answers a DO MSSP at a server. Sent: {Render(sent)}");
	}

	// ---------------------------------------------------------------------------------------------
	// The WILL direction, unchanged in both roles.
	// ---------------------------------------------------------------------------------------------

	/// <summary>
	/// A peer offering to enable the option is answered <c>DO</c> — RFC 854's other pairing — and
	/// that holds in both roles. For a client it is the ordinary case each specification describes;
	/// for a server it is an out-of-spec offer accepted leniently, which is defensible because the
	/// receiving side of all four parses a peer's subnegotiation without reference to mode.
	/// </summary>
	[Test]
	[Arguments(EOR_OPTION)]
	[Arguments(GMCP_OPTION)]
	[Arguments(MSDP_OPTION)]
	[Arguments(MSSP_OPTION)]
	public async Task AWillIsAnsweredWithDoInEitherRole(byte option)
	{
		foreach (var mode in new[] { TelnetInterpreter.TelnetMode.Client, TelnetInterpreter.TelnetMode.Server })
		{
			var sent = await AnswersTo(mode, AllFour, (byte)Trigger.WILL, option);

			await Assert.That(Contains(sent, (byte)Trigger.DO, option))
				.IsTrue().Because($"{mode}, option {option}: a WILL is answered DO. Sent: {Render(sent)}");
		}
	}

	// ---------------------------------------------------------------------------------------------
	// Refusals, which are answers themselves.
	// ---------------------------------------------------------------------------------------------

	/// <summary>
	/// Refusing a refusal is not a telnet exchange, and RFC 1143 warns that answering one is how a
	/// negotiation loop starts. Both roles, both refusing verbs, all four options.
	/// </summary>
	[Test]
	[Arguments(EOR_OPTION)]
	[Arguments(GMCP_OPTION)]
	[Arguments(MSDP_OPTION)]
	[Arguments(MSSP_OPTION)]
	public async Task ARefusalIsNeverAnswered(byte option)
	{
		foreach (var mode in new[] { TelnetInterpreter.TelnetMode.Client, TelnetInterpreter.TelnetMode.Server })
		{
			foreach (var verb in new[] { (byte)Trigger.DONT, (byte)Trigger.WONT })
			{
				var sent = await AnswersTo(mode, AllFour, verb, option);

				await Assert.That(Mentions(sent, option))
					.IsFalse().Because($"{mode} answered verb {verb} for option {option}. Sent: {Render(sent)}");
			}
		}
	}
}
