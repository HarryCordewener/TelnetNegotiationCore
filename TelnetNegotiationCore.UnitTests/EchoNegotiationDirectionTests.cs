using System;
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
/// ECHO's four verbs, in both roles. RFC 857 has a party receiving <c>DO ECHO</c> answer with
/// <c>WILL ECHO</c> or <c>WONT ECHO</c>, and refusal is always permitted — "if the request to operate
/// the connection in echo mode is refused, then the connection continues to operate in non-echo
/// mode". Silence is not one of the options, and leaves the peer waiting.
/// </summary>
/// <remarks>
/// <para>
/// Two of the eight combinations were silent. <c>EchoProtocol.OnPeerNegotiatedAsync</c> routed every
/// verb to a handler written for one role — <c>OnDoEchoAsync</c>'s own log line reads "Client
/// requests server to echo", which is only true when this side is the server — with no check on
/// which role this side actually has.
/// </para>
/// <para>
/// v3.0.0 answered both, not because it handled them but because it did not: it configured
/// <c>State.DoECHO</c> only in its server branch, so the other direction fell through to the
/// interpreter's unsupported-option refusal. The migration collapsed that branching into one switch
/// and the fall-through went with it — a registered plugin claims the option, so
/// <c>RefuseAsync</c> never runs. See
/// <see href="https://github.com/HarryCordewener/TelnetNegotiationCore/issues/118">#118</see>.
/// </para>
/// </remarks>
public class EchoNegotiationDirectionTests : BaseTest
{
	private static readonly byte[] DoEcho = [(byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.ECHO];
	private static readonly byte[] DontEcho = [(byte)Trigger.IAC, (byte)Trigger.DONT, (byte)Trigger.ECHO];
	private static readonly byte[] WillEcho = [(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.ECHO];
	private static readonly byte[] WontEcho = [(byte)Trigger.IAC, (byte)Trigger.WONT, (byte)Trigger.ECHO];

	/// <summary>Drives one negotiation and returns what went back, after clearing any initial offer.</summary>
	private static async Task<byte[]> AnswerTo(TelnetInterpreter.TelnetMode mode, byte[] incoming)
	{
		byte[] sent = null;

		ValueTask Capture(ReadOnlyMemory<byte> data)
		{
			sent = data.ToArray();
			return ValueTask.CompletedTask;
		}

		var interpreter = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
			.UseMode(mode)
			.UseLogger(silentLogger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(Capture)
			.AddPlugin<EchoProtocol>());

		// A server announces WILL ECHO on initialisation; that is not the answer under test.
		await interpreter.WaitForProcessingAsync();
		sent = null;

		await InterpretAndWaitAsync(interpreter, incoming);
		await PollUntilAsync(() => sent is not null, timeoutMs: 2_000);

		await interpreter.DisposeAsync();
		return sent;
	}

	// ---------------------------------------------------------------------------------------------
	// The two that were silent.
	// ---------------------------------------------------------------------------------------------

	/// <summary>
	/// This client does not implement echoing, so it refuses rather than agreeing to something it
	/// will not do. RFC 857's default condition is <c>WONT ECHO</c>.
	/// </summary>
	/// <remarks>
	/// Note what this is <em>not</em> justified by: RFC 857 permits either side to echo — "neither,
	/// either, or both directions may be operating simultaneously in echo mode" — so a client
	/// refusing is a fact about this implementation, not a rule about clients. Silently accepting
	/// also set <c>_willEcho</c>, leaving the client believing it had agreed to something it never
	/// acknowledged.
	/// </remarks>
	[Test]
	public async Task AClientRefusesDoEcho()
	{
		var sent = await AnswerTo(TelnetInterpreter.TelnetMode.Client, DoEcho);

		await AssertByteArraysEqual(sent, WontEcho);
	}

	/// <summary>
	/// And the mirror. RFC 857 would permit accepting, but this server announces <c>WILL ECHO</c> on
	/// initialisation, so it is already echoing — and the RFC is explicit that both ends echoing
	/// makes "any character transmitted in either direction ... echoed back and forth indefinitely",
	/// with "care" required so that "if one site is echoing, echoing is not permitted to be turned on
	/// at the other". Refusing is that care.
	/// </summary>
	[Test]
	public async Task AServerRefusesWillEcho()
	{
		var sent = await AnswerTo(TelnetInterpreter.TelnetMode.Server, WillEcho);

		await AssertByteArraysEqual(sent, DontEcho);
	}

	// ---------------------------------------------------------------------------------------------
	// The six that were already right, so the fix cannot quietly change them.
	// ---------------------------------------------------------------------------------------------

	/// <summary>A server offering to echo is what a client wants; it accepts.</summary>
	[Test]
	public async Task AClientAcceptsWillEcho()
	{
		var sent = await AnswerTo(TelnetInterpreter.TelnetMode.Client, WillEcho);

		await AssertByteArraysEqual(sent, DoEcho);
	}

	/// <summary>
	/// A server accepts a client's DO without answering again: it already announced WILL ECHO on
	/// initialisation, and RFC 1143 does not want that repeated.
	/// </summary>
	[Test]
	public async Task AServerAcceptsDoEchoWithoutAnsweringAgain()
	{
		var sent = await AnswerTo(TelnetInterpreter.TelnetMode.Server, DoEcho);

		await Assert.That(sent).IsNull();
	}

	/// <summary>A refusal needs no answer of its own, in either role.</summary>
	[Test]
	[Arguments(TelnetInterpreter.TelnetMode.Client)]
	[Arguments(TelnetInterpreter.TelnetMode.Server)]
	public async Task ARefusalIsNotAnswered(TelnetInterpreter.TelnetMode mode)
	{
		await Assert.That(await AnswerTo(mode, WontEcho)).IsNull();
		await Assert.That(await AnswerTo(mode, DontEcho)).IsNull();
	}
	// ---------------------------------------------------------------------------------------------
	// Turning echo off and asking again, once it is on (RFC 854, RFC 1143).
	// ---------------------------------------------------------------------------------------------

	/// <summary>
	/// Drives several negotiations in order and returns what went back after each one: an empty
	/// array where nothing was sent.
	/// </summary>
	private static async Task<byte[][]> AnswersTo(TelnetInterpreter.TelnetMode mode, params byte[][] incoming)
	{
		var sent = new System.Collections.Generic.List<byte>();

		ValueTask Capture(ReadOnlyMemory<byte> data)
		{
			lock (sent)
			{
				sent.AddRange(data.ToArray());
			}

			return ValueTask.CompletedTask;
		}

		var interpreter = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
			.UseMode(mode)
			.UseLogger(silentLogger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(Capture)
			.AddPlugin<EchoProtocol>());

		await interpreter.WaitForProcessingAsync();
		lock (sent)
		{
			sent.Clear();
		}

		var answers = new byte[incoming.Length][];
		for (var i = 0; i < incoming.Length; i++)
		{
			await InterpretAndWaitAsync(interpreter, incoming[i]);
			await interpreter.WaitForProcessingAsync();
			lock (sent)
			{
				answers[i] = sent.ToArray();
				sent.Clear();
			}
		}

		await interpreter.DisposeAsync();
		return answers;
	}

	/// <summary>
	/// A server that stops echoing (the end of a password prompt) is acknowledged: RFC 1143 has a
	/// <c>WONT</c> for an option that is on answered with <c>DONT</c>, because the server cannot tell an
	/// unanswered disable from a lost one.
	/// </summary>
	[Test]
	public async Task AClientAcknowledgesTheServerStoppingEcho()
	{
		var answers = await AnswersTo(TelnetInterpreter.TelnetMode.Client, WillEcho, WontEcho);

		await AssertByteArraysEqual(answers[0], DoEcho);
		await AssertByteArraysEqual(answers[1], DontEcho);
	}

	/// <summary>
	/// RFC 854: "if a party receives what appears to be a request to enter some mode it is already in,
	/// the request should not be acknowledged". Answering every <c>WILL ECHO</c> is how two
	/// implementations loop.
	/// </summary>
	[Test]
	public async Task AClientDoesNotAnswerARepeatedWillEcho()
	{
		var answers = await AnswersTo(TelnetInterpreter.TelnetMode.Client, WillEcho, WillEcho);

		await AssertByteArraysEqual(answers[0], DoEcho);
		await Assert.That(answers[1]).IsEmpty();
	}

	/// <summary>A password prompt is the round trip: on, off, on, off, each change answered once.</summary>
	[Test]
	public async Task AClientFollowsAPasswordPromptRoundTrip()
	{
		var answers = await AnswersTo(TelnetInterpreter.TelnetMode.Client,
			WillEcho, WontEcho, WontEcho, WillEcho, WontEcho);

		await AssertByteArraysEqual(answers[0], DoEcho);
		await AssertByteArraysEqual(answers[1], DontEcho);
		await Assert.That(answers[2]).IsEmpty(); // already off
		await AssertByteArraysEqual(answers[3], DoEcho);
		await AssertByteArraysEqual(answers[4], DontEcho);
	}

	/// <summary>
	/// The server's side of the same rule: a client asking it to stop echoing, once echo is on, is
	/// acknowledged with <c>WONT</c>; asked again afterwards, it agrees with <c>WILL</c>, since its
	/// initial offer has been answered and a <c>DO</c> is now a new request.
	/// </summary>
	[Test]
	public async Task AServerAcknowledgesDontAndAcceptsANewDo()
	{
		var answers = await AnswersTo(TelnetInterpreter.TelnetMode.Server, DoEcho, DontEcho, DoEcho, DoEcho);

		await Assert.That(answers[0]).IsEmpty();  // agrees with its own initial WILL
		await AssertByteArraysEqual(answers[1], WontEcho);
		await AssertByteArraysEqual(answers[2], WillEcho);
		await Assert.That(answers[3]).IsEmpty();  // already on
	}

	// ---------------------------------------------------------------------------------------------
	// Turning echo on and off by hand: a server's password prompt (EnableEchoAsync/DisableEchoAsync).
	// ---------------------------------------------------------------------------------------------

	private delegate ValueTask Step(TelnetInterpreter interpreter, EchoProtocol echo);

	private static Step Receive(byte[] bytes) => async (interpreter, _) => await InterpretAndWaitAsync(interpreter, bytes);
	private static readonly Step Enable = async (_, echo) => await echo.EnableEchoAsync();
	private static readonly Step Disable = async (_, echo) => await echo.DisableEchoAsync();

	/// <summary>
	/// Runs the steps in order and returns what went out after each one, plus whether this side
	/// ends up echoing.
	/// </summary>
	private static async Task<(byte[][] Answers, bool Echoing)> Run(TelnetInterpreter.TelnetMode mode, params Step[] steps)
	{
		var sent = new System.Collections.Generic.List<byte>();

		ValueTask Capture(ReadOnlyMemory<byte> data)
		{
			lock (sent)
			{
				sent.AddRange(data.ToArray());
			}

			return ValueTask.CompletedTask;
		}

		var interpreter = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
			.UseMode(mode)
			.UseLogger(silentLogger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(Capture)
			.AddPlugin<EchoProtocol>());
		var echo = interpreter.PluginManager!.GetPlugin<EchoProtocol>()!;

		await interpreter.WaitForProcessingAsync();
		lock (sent)
		{
			sent.Clear();
		}

		var answers = new byte[steps.Length][];
		for (var i = 0; i < steps.Length; i++)
		{
			await steps[i](interpreter, echo);
			await interpreter.WaitForProcessingAsync();
			lock (sent)
			{
				answers[i] = sent.ToArray();
				sent.Clear();
			}
		}

		var echoing = echo.IsEchoing;
		await interpreter.DisposeAsync();
		return (answers, echoing);
	}

	/// <summary>
	/// A server hides a password by turning echo off, and the client has to be told: RFC 857 has the
	/// client echo locally only while the server will not, so a state change the client never hears
	/// leaves it echoing nothing. Each change goes out once, and the client's agreement to it is not
	/// answered.
	/// </summary>
	[Test]
	public async Task AServerTellsTheClientWhenItStopsAndStartsEchoing()
	{
		var (answers, echoing) = await Run(TelnetInterpreter.TelnetMode.Server,
			Receive(DoEcho), Disable, Receive(DontEcho), Enable, Receive(DoEcho));

		await Assert.That(answers[0]).IsEmpty();
		await AssertByteArraysEqual(answers[1], WontEcho);
		await Assert.That(answers[2]).IsEmpty(); // the client agreeing
		await AssertByteArraysEqual(answers[3], WillEcho);
		await Assert.That(answers[4]).IsEmpty(); // the client agreeing
		await Assert.That(echoing).IsTrue();
	}

	/// <summary>RFC 854: a request for the mode already in force is not sent.</summary>
	[Test]
	public async Task TurningEchoToWhereItAlreadyIsSendsNothing()
	{
		var (answers, _) = await Run(TelnetInterpreter.TelnetMode.Server,
			Receive(DoEcho), Enable, Disable, Disable);

		await Assert.That(answers[1]).IsEmpty();
		await AssertByteArraysEqual(answers[2], WontEcho);
		await Assert.That(answers[3]).IsEmpty();
	}

	/// <summary>
	/// A client may ask the server to echo (RFC 857: either side may), and stop asking. The
	/// server's agreement each time is not answered.
	/// </summary>
	[Test]
	public async Task AClientAsksForEchoAndStopsAskingWithDoAndDont()
	{
		var (answers, echoing) = await Run(TelnetInterpreter.TelnetMode.Client,
			Enable, Receive(WillEcho), Disable, Receive(WontEcho));

		await AssertByteArraysEqual(answers[0], DoEcho);
		await Assert.That(answers[1]).IsEmpty();
		await AssertByteArraysEqual(answers[2], DontEcho);
		await Assert.That(answers[3]).IsEmpty();
		await Assert.That(echoing).IsFalse();
	}

	/// <summary>
	/// A client may refuse a server turning echo back on. That refusal is the answer to the
	/// server's WILL and is not answered with WONT (RFC 1143: WANTYES, DONT → NO).
	/// </summary>
	[Test]
	public async Task AServerDoesNotAnswerARefusalOfItsOwnOffer()
	{
		var (answers, echoing) = await Run(TelnetInterpreter.TelnetMode.Server,
			Receive(DoEcho), Disable, Receive(DontEcho), Enable, Receive(DontEcho));

		await AssertByteArraysEqual(answers[3], WillEcho);
		await Assert.That(answers[4]).IsEmpty();
		await Assert.That(echoing).IsFalse();
	}

	/// <summary>
	/// A peer may not refuse to turn an option off (RFC 1143: WANTNO, DO → error, NO). A DO
	/// answering the server's WONT does not turn echo back on.
	/// </summary>
	[Test]
	public async Task ADoAnsweringADisableDoesNotTurnEchoBackOn()
	{
		var (answers, echoing) = await Run(TelnetInterpreter.TelnetMode.Server,
			Receive(DoEcho), Disable, Receive(DoEcho));

		await AssertByteArraysEqual(answers[1], WontEcho);
		await Assert.That(answers[2]).IsEmpty();
		await Assert.That(echoing).IsFalse();
	}
}
