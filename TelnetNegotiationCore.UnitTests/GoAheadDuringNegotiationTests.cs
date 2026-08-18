using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TUnit.Core;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Protocols;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// A bare <c>IAC GA</c> arriving while the state machine is mid-negotiation, which real servers do
/// — Iron Realms' "Rapture" engine (Achaea, Aetolia) sends one at the end of every prompt, including
/// the one immediately before it starts MCCP2. Nothing configured a transition for
/// <see cref="Trigger.GA"/> from <see cref="State.StartNegotiation"/>, so it always reached
/// <c>OnUnhandledTriggerAsync</c>, which logs Critical and recovers via <see cref="Trigger.Error"/>.
/// </summary>
/// <remarks>
/// Captured against <c>achaea.com:23</c> (2026-08-18): the connect screen arrives in the clear,
/// then <c>IAC GA</c>, three declines, <c>IAC SB COMPRESS2 IAC SE</c> — and depending on which
/// other plugins are registered and how the bytes happen to be chunked, the Error-recovery either
/// swallows the GA cleanly or, as it does in production, leaves the state machine unable to
/// complete the marker right behind it: MCCP2 never reports enabled, and every byte the server
/// sends from then on — still zlib on the wire — is read as if it were plain telnet and stored as
/// the game's public connect screen. Both outcomes start from the same place: a trigger with no
/// permitted transition. The fix is to give GA one, the same way <see cref="EORProtocol"/> gives
/// <see cref="Trigger.EOR"/> one, so <c>OnUnhandledTriggerAsync</c> is never reached for it at all
/// — a test asserting downstream behaviour would only ever prove one particular interleaving fixed,
/// not the defect.
/// </remarks>
public class GoAheadDuringNegotiationTests : BaseTest
{
	private static readonly byte[] s_willMccp2 = [(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.MCCP2];
	private static readonly byte[] s_goAhead = [(byte)Trigger.IAC, (byte)Trigger.GA];
	private static readonly byte[] s_startMccp2 = [(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.MCCP2, (byte)Trigger.IAC, (byte)Trigger.SE];
	private static readonly byte[] s_wont25 = [(byte)Trigger.IAC, (byte)Trigger.WONT, 25];
	private static readonly byte[] s_wont200 = [(byte)Trigger.IAC, (byte)Trigger.WONT, 200];
	private static readonly byte[] s_wont201 = [(byte)Trigger.IAC, (byte)Trigger.WONT, 201];

	private static byte[] Compress(string text)
	{
		using var sink = new MemoryStream();
		using (var deflate = new ZLibStream(sink, CompressionLevel.Optimal, leaveOpen: true))
		{
			var bytes = Encoding.ASCII.GetBytes(text);
			deflate.Write(bytes, 0, bytes.Length);
		}

		return sink.ToArray();
	}

	private static byte[] Concat(params byte[][] parts)
	{
		var result = new byte[parts.Sum(p => p.Length)];
		var at = 0;
		foreach (var part in parts)
		{
			part.CopyTo(result, at);
			at += part.Length;
		}

		return result;
	}

	private static async Task<(TelnetInterpreter Client, CapturingLogger Logs)> BuildClientAsync(
		List<string> submitted,
		List<(int Version, bool Enabled)> compressionEvents)
	{
		var captured = new CapturingLogger(logger);
		var client = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(captured)
			.OnSubmit((data, encoding, _) =>
			{
				submitted.Add(encoding.GetString(data));
				return ValueTask.CompletedTask;
			})
			.OnNegotiation(_ => ValueTask.CompletedTask)
			.AddPlugin<EORProtocol>()
				.OnPrompt(() => ValueTask.CompletedTask)
			.AddPlugin<GMCPProtocol>()
			.AddPlugin<MCCPProtocol>()
				.OnCompressionEnabled((version, enabled) =>
				{
					compressionEvents.Add((version, enabled));
					return ValueTask.CompletedTask;
				}));

		return (client, captured);
	}

	[Test]
	public async Task AGoAheadRightBeforeCompressionStartsIsHandledWithoutAnUnhandledTrigger()
	{
		var submitted = new List<string>();
		var compressionEvents = new List<(int Version, bool Enabled)>();
		var (client, logs) = await BuildClientAsync(submitted, compressionEvents);

		await InterpretAndWaitAsync(client, s_willMccp2);

		// The exact interleaving achaea.com sends after "Enter an option or enter your character's
		// name. ": a bare GA, three declines for options this client has no plugin for, then the
		// two-byte marker that starts compression, then the first compressed line.
		var compressed = Compress("first compressed line\n");
		await InterpretAndWaitAsync(
			client,
			Concat(s_goAhead, s_wont25, s_wont200, s_wont201, s_startMccp2, compressed));
		await PollUntilAsync(() => compressionEvents.Count > 0 || logs.Entries(Microsoft.Extensions.Logging.LogLevel.Critical).Count > 0);

		// The precise, general assertion: GA is a trigger the base protocol must handle on its own,
		// so it must never reach OnUnhandledTriggerAsync — regardless of what happens to arrive
		// right behind it, which is why this is checked directly rather than inferred from whether
		// compression happened to still work in this one interleaving.
		await Assert.That(logs.Entries(Microsoft.Extensions.Logging.LogLevel.Critical)).IsEmpty();

		// And, since a real consumer cares about this too: the marker was still read as the marker.
		await Assert.That(compressionEvents).IsEquivalentTo(new[] { (2, true) });
		await Assert.That(submitted).IsEquivalentTo(new[] { "first compressed line" });

		await client.DisposeAsync();
	}

	[Test]
	public async Task AWontWithNoOptionByteDoesNotLoseTheMarkerRightBehindIt()
	{
		// Captured verbatim from achaea.com:23 (2026-08-18), byte for byte, once EOR/GMCP/MSSP were
		// also negotiated: IAC GA, then IAC WONT with no option byte at all — the very next byte is
		// another IAC — then IAC SB COMPRESS2 IAC SE, then the compressed stream. Whatever the
		// server's own reason for the bare WONT, a compliant client must not let it corrupt parsing
		// of the marker sitting right behind it.
		var submitted = new List<string>();
		var compressionEvents = new List<(int Version, bool Enabled)>();
		var (client, logs) = await BuildClientAsync(submitted, compressionEvents);

		await InterpretAndWaitAsync(client, s_willMccp2);

		byte[] wontWithNoOption = [(byte)Trigger.IAC, (byte)Trigger.WONT];
		var compressed = Compress("first compressed line\n");
		await InterpretAndWaitAsync(
			client,
			Concat(s_goAhead, wontWithNoOption, s_startMccp2, compressed));
		await PollUntilAsync(() => compressionEvents.Count > 0 || logs.Entries(Microsoft.Extensions.Logging.LogLevel.Critical).Count > 0);

		await Assert.That(logs.Entries(Microsoft.Extensions.Logging.LogLevel.Critical)).IsEmpty();
		await Assert.That(compressionEvents).IsEquivalentTo(new[] { (2, true) });
		await Assert.That(submitted).IsEquivalentTo(new[] { "first compressed line" });

		await client.DisposeAsync();
	}

	[Test]
	public async Task AGoAheadOutsideNegotiationIsHandledWithoutAnUnhandledTrigger()
	{
		// The ordinary case a Go-Ahead-driven server produces continuously: a prompt with nothing
		// else going on.
		var submitted = new List<string>();
		var compressionEvents = new List<(int Version, bool Enabled)>();
		var (client, logs) = await BuildClientAsync(submitted, compressionEvents);

		await InterpretAndWaitAsync(client, Concat(Encoding.ASCII.GetBytes("a prompt"), s_goAhead));
		await InterpretAndWaitAsync(client, Encoding.ASCII.GetBytes("\nnext line\n"));
		await PollUntilAsync(() => submitted.Count > 1);

		await Assert.That(logs.Entries(Microsoft.Extensions.Logging.LogLevel.Critical)).IsEmpty();
		await Assert.That(submitted).IsEquivalentTo(new[] { "a prompt", "next line" });

		await client.DisposeAsync();
	}

	/// <summary>Captures formatted log output so a test can assert nothing was logged Critical.</summary>
	private sealed class CapturingLogger(Microsoft.Extensions.Logging.ILogger inner) : Microsoft.Extensions.Logging.ILogger
	{
		private readonly List<(Microsoft.Extensions.Logging.LogLevel Level, string Message)> _entries = [];

		public IDisposable BeginScope<TState>(TState state) where TState : notnull => null!;

		public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

		public void Log<TState>(
			Microsoft.Extensions.Logging.LogLevel logLevel,
			Microsoft.Extensions.Logging.EventId eventId,
			TState state,
			Exception exception,
			Func<TState, Exception, string> formatter)
		{
			var message = formatter(state, exception);
			lock (_entries)
			{
				_entries.Add((logLevel, message));
			}

			inner.Log(logLevel, eventId, state, exception, formatter);
		}

		public List<string> Entries(Microsoft.Extensions.Logging.LogLevel level)
		{
			lock (_entries)
			{
				return _entries.Where(x => x.Level == level).Select(x => x.Message).ToList();
			}
		}
	}
}
