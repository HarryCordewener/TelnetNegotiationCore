using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Protocols;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// The Pueblo handshake, framed as PennMUSH frames it: the hello on connect, the client's
/// <c>PUEBLOCLIENT</c> line consumed, and the start sequence that moves the client into HTML mode.
/// </summary>
public class PuebloProtocolTests : BaseTest
{
	private sealed class Peer
	{
		public TelnetInterpreter Interpreter { get; set; } = null!;
		public PuebloProtocol Pueblo => Interpreter.PluginManager!.GetPlugin<PuebloProtocol>()!;
		public List<string> Submitted { get; } = [];
		public List<PuebloClient> Enabled { get; } = [];
		private readonly StringBuilder _wired = new();

		public void Write(ReadOnlyMemory<byte> data)
		{
			lock (_wired) _wired.Append(Encoding.ASCII.GetString(data.Span));
		}

		public string Wired
		{
			get { lock (_wired) return _wired.ToString(); }
		}

		public async Task FeedAsync(string text)
		{
			await Interpreter.InterpretByteArrayAsync(Encoding.ASCII.GetBytes(text));
			await Interpreter.WaitForProcessingAsync();
		}
	}

	private static async Task<Peer> PeerAsync(
		TelnetInterpreter.TelnetMode mode = TelnetInterpreter.TelnetMode.Server,
		bool withPueblo = true,
		bool callbackThrows = false)
	{
		var peer = new Peer();
		TelnetInterpreterBuilder builder = new TelnetInterpreterBuilder()
			.UseMode(mode)
			.UseLogger(logger)
			.OnSubmit((data, encoding, _) =>
			{
				lock (peer.Submitted) peer.Submitted.Add(encoding.GetString(data));
				return ValueTask.CompletedTask;
			})
			.OnNegotiation(data =>
			{
				peer.Write(data);
				return ValueTask.CompletedTask;
			});

		if (withPueblo)
		{
			builder = builder.AddPlugin<PuebloProtocol>().OnPuebloEnabled(client =>
			{
				lock (peer.Enabled) peer.Enabled.Add(client);
				return callbackThrows
					? ValueTask.FromException(new InvalidOperationException("host failure"))
					: ValueTask.CompletedTask;
			});
		}

		peer.Interpreter = await builder.BuildAsync();
		return peer;
	}

	[Test]
	public async Task AServerAnnouncesPuebloOnConnect()
	{
		var peer = await PeerAsync();

		await Assert.That(await PollUntilAsync(() => peer.Wired.Contains(PuebloProtocol.Hello), timeoutMs: 5000)).IsTrue();
	}

	[Test]
	public async Task TheHandshake_IsConsumed_AndAnsweredWithTheStartSequence()
	{
		var peer = await PeerAsync();

		await peer.FeedAsync("PUEBLOCLIENT 2.50 md5=\"0123456789abcdef\"\r\n");

		await Assert.That(peer.Wired).Contains(PuebloProtocol.Start)
			.Because("the start sequence is what moves the client out of text mode");
		await Assert.That(peer.Submitted).IsEmpty()
			.Because("the handshake would otherwise reach the application as a command");
		await Assert.That(peer.Pueblo.IsPuebloActive).IsTrue();
		await Assert.That(peer.Enabled).Count().IsEqualTo(1);
		await Assert.That(peer.Enabled[0]).IsEqualTo(new PuebloClient("2.50", "0123456789abcdef"));
	}

	[Test]
	public async Task ARepeatedHandshake_IsAnsweredWithoutTheClear_AndEnablesOnce()
	{
		var peer = await PeerAsync();

		await peer.FeedAsync("PUEBLOCLIENT 2.50\r\n");
		await peer.FeedAsync("PUEBLOCLIENT 2.50\r\n");

		await Assert.That(peer.Wired).Contains(PuebloProtocol.Restart);
		await Assert.That(peer.Enabled).Count().IsEqualTo(1);
		await Assert.That(peer.Submitted).IsEmpty();
	}

	/// <summary>
	/// The callback is the host's code, run inside byte processing. A throw from it leaves the client
	/// in Pueblo mode, the handshake consumed, and the input after it flowing.
	/// </summary>
	[Test]
	public async Task AThrowingCallback_LeavesTheConnectionWorking()
	{
		var peer = await PeerAsync(callbackThrows: true);

		await peer.FeedAsync("PUEBLOCLIENT 2.50\r\nlook\r\n");

		await Assert.That(peer.Pueblo.IsPuebloActive).IsTrue();
		await Assert.That(peer.Submitted).Contains("look");
	}

	[Test]
	[Arguments("PUEBLOCLIENTX 2.50")]
	[Arguments("puebloclient 2.50")]
	[Arguments("say PUEBLOCLIENT 2.50")]
	public async Task OnlyTheExactCommand_IsTheHandshake(string line)
	{
		var peer = await PeerAsync();

		await peer.FeedAsync(line + "\r\n");

		await Assert.That(peer.Submitted).Contains(line)
			.Because("PennMUSH matches \"PUEBLOCLIENT \" with strncmp, so anything else is ordinary input");
		await Assert.That(peer.Pueblo.IsPuebloActive).IsFalse();
	}

	[Test]
	public async Task WithoutThePlugin_NothingIsSentAndNothingIsConsumed()
	{
		var peer = await PeerAsync(withPueblo: false);

		await peer.FeedAsync("PUEBLOCLIENT 2.50\r\n");

		await Assert.That(peer.Wired).DoesNotContain("Pueblo");
		await Assert.That(peer.Submitted).Contains("PUEBLOCLIENT 2.50");
	}

	[Test]
	public async Task AClient_NeverAnnouncesPueblo()
	{
		var peer = await PeerAsync(TelnetInterpreter.TelnetMode.Client);
		await Task.Delay(100);

		await Assert.That(peer.Wired).DoesNotContain(PuebloProtocol.Hello);
	}

	[Test]
	[Arguments("PUEBLOCLIENT 2.50 md5=\"abc\"", "2.50", "abc")]
	[Arguments("PUEBLOCLIENT md5=\"abc\"", "", "abc")]
	[Arguments("PUEBLOCLIENT 2.01", "2.01", null)]
	[Arguments("PUEBLOCLIENT 2.50 md5=\"0123456789abcdef0123456789abcdef0\"", "2.50", null)]
	public async Task TheClientLine_IsReadAsPennMUSHReadsIt(string line, string version, string checksum)
	{
		await Assert.That(PuebloProtocol.Parse(line)).IsEqualTo(new PuebloClient(version, checksum));
	}
}
