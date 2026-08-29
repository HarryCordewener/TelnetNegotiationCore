#nullable enable
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Protocols;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// <c>mcp-negotiate</c>: the package that tells the other side which packages this side speaks.
/// </summary>
/// <remarks>
/// <para>
/// It is a separate plugin from <see cref="MudClientProtocol"/> because it is a separate thing in
/// the specification: <c>mcp-negotiate</c> is a package carried over the MCP session layer, versioned
/// on its own (1.0, and 2.0 which adds the line that ends the list), exactly as
/// <c>dns-org-mud-moo-simpleedit</c> is. The session layer works with it absent; it is meaningless
/// with the session layer absent.
/// </para>
/// <para>
/// Everything here is driven by a scripted peer rather than a network. No live host is contacted.
/// </para>
/// </remarks>
public class McpNegotiateProtocolTests : BaseTest
{
	private static readonly Encoding Wire = Encoding.GetEncoding("iso-8859-1");

	private sealed class Peer : IAsyncDisposable
	{
		public TelnetInterpreter Interpreter { get; set; } = null!;

		/// <summary>Ends the interpreter's byte-processing task with the test that started it.</summary>
		public ValueTask DisposeAsync() => Interpreter.DisposeAsync();
		public MudClientProtocol Mcp => Interpreter.PluginManager!.GetPlugin<MudClientProtocol>()!;
		public McpNegotiateProtocol Negotiate => Interpreter.PluginManager!.GetPlugin<McpNegotiateProtocol>()!;
		private readonly List<byte> _written = [];

		public void Write(ReadOnlyMemory<byte> data)
		{
			lock (_written) _written.AddRange(data.ToArray());
		}

		public string Wired
		{
			get { lock (_written) return Wire.GetString(_written.ToArray()); }
		}

		public async Task FeedAsync(string text)
		{
			await Interpreter.InterpretByteArrayAsync(Wire.GetBytes(text));
			await Interpreter.WaitForProcessingAsync();
		}
	}

	/// <summary>A client with the MCP handshake already done and its packages advertised.</summary>
	private static async Task<Peer> EstablishedClientAsync(Action<McpNegotiateProtocol>? supports = null)
	{
		var peer = new Peer();

		var builder = new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(data =>
			{
				peer.Write(data);
				return ValueTask.CompletedTask;
			})
			.AddPlugin<MudClientProtocol>()
			.AddPlugin<McpNegotiateProtocol>();

		supports?.Invoke(builder.Plugin);

		peer.Interpreter = await builder.BuildAsync();

		await peer.FeedAsync("#$#mcp version: \"2.1\" to: \"2.1\"\r\n");
		await Assert.That(await PollUntilAsync(() => peer.Mcp.IsNegotiated, timeoutMs: 10000)).IsTrue();

		return peer;
	}

	/// <summary>
	/// The dependency is declared, so a consumer who adds the package without the session layer it
	/// rides on is told at <c>BuildAsync</c> rather than finding out from silence on the wire.
	/// </summary>
	[Test]
	public async Task NegotiateWithoutTheSessionLayerIsRefusedAtBuild()
	{
		var builder = new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(_ => ValueTask.CompletedTask)
			.AddPlugin<McpNegotiateProtocol>();

		await Assert.That(async () => await builder.BuildAsync()).Throws<InvalidOperationException>();
	}

	/// <summary>
	/// The list goes out as one burst the moment the session is up, and closes with the line that
	/// says there is no more of it.
	/// </summary>
	/// <remarks>
	/// It has to be one burst: <c>mcp-negotiate-end</c> is what tells the peer the list is complete,
	/// so a package that registered late would arrive after the peer had already stopped listening.
	/// </remarks>
	[Test]
	public async Task ThePackageListGoesOutAsSoonAsTheSessionIsUp()
	{
		await using var peer = await EstablishedClientAsync(
			negotiate => negotiate.Supports("dns-com-example-test", new McpVersion(1, 0), new McpVersion(2, 0)));

		var key = peer.Mcp.AuthenticationKey;

		var sent = await PollUntilAsync(() => peer.Wired.Contains("mcp-negotiate-end"), timeoutMs: 10000);
		await Assert.That(sent).IsTrue();

		await Assert.That(peer.Wired).Contains(
			$"#$#mcp-negotiate-can {key} package: \"dns-com-example-test\" min-version: \"1.0\" max-version: \"2.0\"\r\n");
		await Assert.That(peer.Wired).Contains($"#$#mcp-negotiate-end {key}\r\n");
	}

	/// <summary>
	/// A side that speaks <c>mcp-negotiate</c> at all speaks it as a package like any other, and says
	/// so in its own list.
	/// </summary>
	[Test]
	public async Task NegotiateAdvertisesItself()
	{
		await using var peer = await EstablishedClientAsync();

		await PollUntilAsync(() => peer.Wired.Contains("mcp-negotiate-end"), timeoutMs: 10000);

		await Assert.That(peer.Wired).Contains(
			"package: \"mcp-negotiate\" min-version: \"1.0\" max-version: \"2.0\"");
	}

	/// <summary>
	/// The agreed version is the highest both sides can speak: the lower of the two maxima, provided
	/// it is not below either minimum.
	/// </summary>
	[Test]
	public async Task TheAgreedVersionIsTheHighestBothSidesCanSpeak()
	{
		await using var peer = await EstablishedClientAsync(
			negotiate => negotiate.Supports("dns-com-example-test", new McpVersion(1, 0), new McpVersion(2, 0)));

		var key = peer.Mcp.AuthenticationKey;

		await peer.FeedAsync(
			$"#$#mcp-negotiate-can {key} package: \"dns-com-example-test\" min-version: \"1.5\" max-version: \"3.0\"\r\n");
		await peer.FeedAsync($"#$#mcp-negotiate-end {key}\r\n");

		await Assert.That(await PollUntilAsync(() => peer.Negotiate.Agreed.Count > 0, timeoutMs: 10000)).IsTrue();
		await Assert.That(peer.Negotiate.Agreed["dns-com-example-test"]).IsEqualTo(new McpVersion(2, 0));
	}

	/// <summary>
	/// Two ranges that do not overlap agree on nothing, and the package is not in the agreed set at
	/// all -- a peer's <c>can</c> line is a claim about the peer, not an agreement by itself.
	/// </summary>
	[Test]
	public async Task RangesThatDoNotOverlapAgreeOnNothing()
	{
		await using var peer = await EstablishedClientAsync(
			negotiate => negotiate.Supports("dns-com-example-test", new McpVersion(1, 0), new McpVersion(1, 0)));

		var key = peer.Mcp.AuthenticationKey;

		await peer.FeedAsync(
			$"#$#mcp-negotiate-can {key} package: \"dns-com-example-test\" min-version: \"2.0\" max-version: \"3.0\"\r\n");
		await peer.FeedAsync($"#$#mcp-negotiate-end {key}\r\n");

		await Assert.That(await PollUntilAsync(() => peer.Negotiate.IsComplete, timeoutMs: 10000)).IsTrue();
		await Assert.That(peer.Negotiate.Agreed.ContainsKey("dns-com-example-test")).IsFalse();
	}

	/// <summary>
	/// A package only this side speaks is not agreed either. Both halves of the intersection matter,
	/// and this is the half a table built from the peer's list alone would get wrong.
	/// </summary>
	[Test]
	public async Task APackageThePeerNeverOfferedIsNotAgreed()
	{
		await using var peer = await EstablishedClientAsync(
			negotiate => negotiate.Supports("dns-com-example-test", new McpVersion(1, 0), new McpVersion(2, 0)));

		var key = peer.Mcp.AuthenticationKey;

		await peer.FeedAsync($"#$#mcp-negotiate-end {key}\r\n");

		await Assert.That(await PollUntilAsync(() => peer.Negotiate.IsComplete, timeoutMs: 10000)).IsTrue();
		await Assert.That(peer.Negotiate.Agreed.ContainsKey("dns-com-example-test")).IsFalse();
	}

	/// <summary>
	/// Both plugins are configured from the builder chain the way every other plugin in the library
	/// is, without reaching past the chain for the instance.
	/// </summary>
	[Test]
	public async Task BothPluginsAreConfigurableFromTheBuilderChain()
	{
		await using var peer = new Peer();

		peer.Interpreter = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(data =>
			{
				peer.Write(data);
				return ValueTask.CompletedTask;
			})
			.AddPlugin<MudClientProtocol>()
			.OnMcpMessage("dns-com-example-test", _ => ValueTask.CompletedTask)
			.AddPlugin<McpNegotiateProtocol>()
			.SupportsMcpPackage("dns-com-example-test", new McpVersion(1, 0), new McpVersion(2, 0))
			.OnMcpNegotiationComplete(_ => ValueTask.CompletedTask)
			.BuildAsync();

		await peer.FeedAsync("#$#mcp version: \"2.1\" to: \"2.1\"\r\n");

		await Assert.That(await PollUntilAsync(() => peer.Wired.Contains("mcp-negotiate-end"), timeoutMs: 10000))
			.IsTrue();
		await Assert.That(peer.Wired).Contains(
			"package: \"dns-com-example-test\" min-version: \"1.0\" max-version: \"2.0\"");
	}

	/// <summary>
	/// The server advertises its packages too, and on the same trigger: the session coming up. The
	/// handshake is asymmetric, everything after it is not.
	/// </summary>
	[Test]
	public async Task AServerAdvertisesItsPackagesOnceTheClientHasAnswered()
	{
		await using var peer = new Peer();

		peer.Interpreter = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(data =>
			{
				peer.Write(data);
				return ValueTask.CompletedTask;
			})
			.AddPlugin<MudClientProtocol>()
			.AddPlugin<McpNegotiateProtocol>()
			.SupportsMcpPackage("dns-com-example-test", new McpVersion(1, 0), new McpVersion(1, 0))
			.BuildAsync();

		await peer.FeedAsync("#$#mcp authentication-key: \"1234\" version: \"2.1\" to: \"2.1\"\r\n");

		await Assert.That(await PollUntilAsync(() => peer.Wired.Contains("mcp-negotiate-end"), timeoutMs: 10000))
			.IsTrue();
		await Assert.That(peer.Wired).Contains(
			"#$#mcp-negotiate-can 1234 package: \"dns-com-example-test\" min-version: \"1.0\" max-version: \"1.0\"\r\n");
	}

	/// <summary>
	/// The package list is finished when the peer says it is finished. A <c>can</c> line arriving
	/// afterwards does not change what was agreed, and a second <c>end</c> does not announce it twice.
	/// </summary>
	/// <remarks>
	/// The callback is handed a snapshot of what was agreed. If a later <c>can</c> could still add to
	/// the table, that snapshot stops being what the session actually settled on, and a consumer that
	/// acted on it acted on something that has since changed underneath it.
	/// </remarks>
	[Test]
	public async Task NothingAfterTheEndOfTheListChangesIt()
	{
		var completions = new List<int>();

		await using var peer = await EstablishedClientAsync(negotiate => negotiate
			.Supports("dns-com-example-test", new McpVersion(1, 0), new McpVersion(2, 0))
			.Supports("dns-com-example-late", new McpVersion(1, 0), new McpVersion(1, 0))
			.OnNegotiationComplete(agreed =>
			{
				lock (completions) completions.Add(agreed.Count);
				return ValueTask.CompletedTask;
			}));

		var key = peer.Mcp.AuthenticationKey;

		await peer.FeedAsync(
			$"#$#mcp-negotiate-can {key} package: \"dns-com-example-test\" min-version: \"1.0\" max-version: \"2.0\"\r\n");
		await peer.FeedAsync($"#$#mcp-negotiate-end {key}\r\n");

		await Assert.That(await PollUntilAsync(() => completions.Count > 0, timeoutMs: 10000)).IsTrue();

        // Late arrivals: a package the peer never mentioned before it said it was done, and a second
        // end line.
		await peer.FeedAsync(
			$"#$#mcp-negotiate-can {key} package: \"dns-com-example-late\" min-version: \"1.0\" max-version: \"1.0\"\r\n");
		await peer.FeedAsync($"#$#mcp-negotiate-end {key}\r\n");

		await Assert.That(peer.Negotiate.Agreed.ContainsKey("dns-com-example-late")).IsFalse();
		await Assert.That(completions.Count).IsEqualTo(1);
	}

	/// <summary>
	/// Everything the peer said it speaks is kept, including packages this side does not -- which is
	/// most of what a peer offers, and the only place the fact survives.
	/// </summary>
	/// <remarks>
	/// <see cref="McpNegotiateProtocol.Agreed"/> is an intersection and so throws away the larger
	/// half: a directory recording that a game offers <c>dns-org-mud-moo-simpleedit</c> cares that the
	/// game offers it, not that this crawler happens not to implement it.
	/// </remarks>
	[Test]
	public async Task WhatThePeerOffersIsKeptEvenWhenThisSideCannotSpeakIt()
	{
		await using var peer = await EstablishedClientAsync();

		var key = peer.Mcp.AuthenticationKey;

		await peer.FeedAsync(
			$"#$#mcp-negotiate-can {key} package: \"dns-org-mud-moo-simpleedit\" min-version: \"1.0\" max-version: \"1.0\"\r\n");
		await peer.FeedAsync($"#$#mcp-negotiate-end {key}\r\n");

		await Assert.That(await PollUntilAsync(() => peer.Negotiate.IsComplete, timeoutMs: 10000)).IsTrue();

		// Not agreed -- this side never declared it.
		await Assert.That(peer.Negotiate.Agreed.ContainsKey("dns-org-mud-moo-simpleedit")).IsFalse();

		// But recorded, with the range the peer named.
		await Assert.That(peer.Negotiate.PeerPackages["dns-org-mud-moo-simpleedit"])
			.IsEqualTo((new McpVersion(1, 0), new McpVersion(1, 0)));
	}

	/// <summary>
	/// A peer offering a range whose minimum is above its maximum has described no range at all, and
	/// it is not recorded as one.
	/// </summary>
	/// <remarks>
	/// The overlap check already stops it being agreed. What it did not stop was the inverted range
	/// reaching <see cref="McpNegotiateProtocol.PeerPackages"/>, where a consumer reading "what does
	/// this peer support" would be handed a claim that cannot be true.
	/// </remarks>
	[Test]
	public async Task AnInvertedPeerRangeIsNotRecordedAtAll()
	{
		await using var peer = await EstablishedClientAsync(negotiate =>
			negotiate.Supports("dns-com-example-test", new McpVersion(1, 0), new McpVersion(3, 0)));

		var key = peer.Mcp.AuthenticationKey;

		await peer.FeedAsync(
			$"#$#mcp-negotiate-can {key} package: \"dns-com-example-test\" min-version: \"2.0\" max-version: \"1.0\"\r\n");
		await peer.FeedAsync($"#$#mcp-negotiate-end {key}\r\n");

		await Assert.That(await PollUntilAsync(() => peer.Negotiate.IsComplete, timeoutMs: 10000)).IsTrue();
		await Assert.That(peer.Negotiate.PeerPackages.ContainsKey("dns-com-example-test")).IsFalse();
		await Assert.That(peer.Negotiate.Agreed.ContainsKey("dns-com-example-test")).IsFalse();
	}
}
