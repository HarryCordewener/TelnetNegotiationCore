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
/// ECHO's four verbs, in both roles. RFC 854 requires a <c>DO</c> or a <c>WILL</c> to be answered,
/// with <c>WILL</c>/<c>WONT</c> or <c>DO</c>/<c>DONT</c> respectively; silence leaves the peer
/// waiting.
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
	/// A client will not echo for a server — the server is the side that echoes — so it refuses.
	/// Silently accepting also set <c>_willEcho</c>, leaving the client believing it had agreed to
	/// something it never acknowledged.
	/// </summary>
	[Test]
	public async Task AClientRefusesDoEcho()
	{
		var sent = await AnswerTo(TelnetInterpreter.TelnetMode.Client, DoEcho);

		await AssertByteArraysEqual(sent, WontEcho);
	}

	/// <summary>And the mirror: a server has no use for a client offering to echo, so it refuses.</summary>
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
}
