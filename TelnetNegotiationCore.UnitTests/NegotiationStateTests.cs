using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Plugins;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// <see cref="ITelnetProtocolPlugin.IsEnabled"/> has always meant "this plugin is attached to the
/// interpreter and processing" -- true the instant <c>InitializeAsync</c> runs, which is before any
/// telnet negotiation happens at all. That is a different question from "did the peer actually agree
/// to this option", which nothing exposed until now. These tests cover
/// <see cref="ITelnetProtocolPlugin.IsNegotiated"/> and <see cref="ITelnetProtocolPlugin.OnNegotiatedAsync"/>:
/// false before a real WILL/DO exchange completes, true once the peer agrees, and false again if the
/// peer refuses or withdraws -- wired from each protocol's own <c>ConfigureStateMachine</c> handlers,
/// not from plugin attachment.
/// </summary>
public class NegotiationStateTests : BaseTest
{
	/// <summary>
	/// Server mode: <see cref="Protocols.MSDPProtocol"/> is attached (<c>IsEnabled</c> true) well
	/// before the client has said anything, but <c>IsNegotiated</c> must stay false until the client's
	/// own <c>IAC DO MSDP</c> actually arrives.
	/// </summary>
	[Test]
	public async Task MSDPIsNotNegotiatedUntilThePeerSendsDo()
	{
		var telnet = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation((data) => ValueTask.CompletedTask)
			.AddPlugin<Protocols.MSDPProtocol>()
			.BuildAsync();

		var msdp = telnet.PluginManager!.GetPlugin<Protocols.MSDPProtocol>()!;

		await Assert.That(msdp.IsEnabled).IsTrue();
		await Assert.That(msdp.IsNegotiated).IsFalse();

		await InterpretAndWaitAsync(telnet, [(byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MSDP]);

		await Assert.That(msdp.IsNegotiated).IsTrue();

		await telnet.DisposeAsync();
	}

	/// <summary>
	/// The same exchange, but the client refuses: <c>IsNegotiated</c> must never become true.
	/// </summary>
	[Test]
	public async Task MSDPStaysNotNegotiatedWhenThePeerRefuses()
	{
		var telnet = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation((data) => ValueTask.CompletedTask)
			.AddPlugin<Protocols.MSDPProtocol>()
			.BuildAsync();

		var msdp = telnet.PluginManager!.GetPlugin<Protocols.MSDPProtocol>()!;

		await InterpretAndWaitAsync(telnet, [(byte)Trigger.IAC, (byte)Trigger.DONT, (byte)Trigger.MSDP]);

		await Assert.That(msdp.IsNegotiated).IsFalse();
		await Assert.That(msdp.IsEnabled).IsTrue();

		await telnet.DisposeAsync();
	}

	/// <summary>
	/// Client mode: the peer here is the server, offering with <c>WILL</c> rather than responding to
	/// a <c>DO</c>. Confirms the false-until-real-negotiation contract holds in both directions.
	/// </summary>
	[Test]
	public async Task MSDPIsNotNegotiatedUntilThePeerSendsWill_ClientMode()
	{
		var telnet = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation((data) => ValueTask.CompletedTask)
			.AddPlugin<Protocols.MSDPProtocol>()
			.BuildAsync();

		var msdp = telnet.PluginManager!.GetPlugin<Protocols.MSDPProtocol>()!;

		await Assert.That(msdp.IsNegotiated).IsFalse();

		await InterpretAndWaitAsync(telnet, [(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.MSDP]);

		await Assert.That(msdp.IsNegotiated).IsTrue();

		await InterpretAndWaitAsync(telnet, [(byte)Trigger.IAC, (byte)Trigger.WONT, (byte)Trigger.MSDP]);

		await Assert.That(msdp.IsNegotiated).IsFalse();

		await telnet.DisposeAsync();
	}

	/// <summary>
	/// GMCP, server mode: negotiated true on <c>DO</c>, false again if the client later sends a fresh
	/// <c>DONT</c>. Round-tripping both directions on one connection is what proves this reflects the
	/// live wire state rather than a one-shot flag.
	/// </summary>
	[Test]
	public async Task GMCPIsNegotiatedTracksRealDoAndDontOverTheConnection()
	{
		var telnet = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation((data) => ValueTask.CompletedTask)
			.AddPlugin<Protocols.GMCPProtocol>()
			.BuildAsync();

		var gmcp = telnet.PluginManager!.GetPlugin<Protocols.GMCPProtocol>()!;

		await Assert.That(gmcp.IsNegotiated).IsFalse();

		await InterpretAndWaitAsync(telnet, [(byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.GMCP]);
		await Assert.That(gmcp.IsNegotiated).IsTrue();

		await InterpretAndWaitAsync(telnet, [(byte)Trigger.IAC, (byte)Trigger.DONT, (byte)Trigger.GMCP]);
		await Assert.That(gmcp.IsNegotiated).IsFalse();

		await telnet.DisposeAsync();
	}

	/// <summary>
	/// GMCP, client mode: the peer is the server here, so acceptance arrives as <c>WILL</c>/<c>WONT</c>
	/// instead of <c>DO</c>/<c>DONT</c>.
	/// </summary>
	[Test]
	public async Task GMCPIsNegotiatedTracksRealWillAndWont_ClientMode()
	{
		var telnet = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation((data) => ValueTask.CompletedTask)
			.AddPlugin<Protocols.GMCPProtocol>()
			.BuildAsync();

		var gmcp = telnet.PluginManager!.GetPlugin<Protocols.GMCPProtocol>()!;

		await Assert.That(gmcp.IsNegotiated).IsFalse();

		await InterpretAndWaitAsync(telnet, [(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.GMCP]);
		await Assert.That(gmcp.IsNegotiated).IsTrue();

		await telnet.DisposeAsync();
	}

	/// <summary>
	/// MSSP, server mode. The server offers <c>WILL MSSP</c> on its own initiative
	/// (<c>RegisterInitialNegotiation</c>); <c>IsNegotiated</c> must not become true until the client's
	/// <c>DO</c> actually arrives, regardless of what the server already sent.
	/// </summary>
	[Test]
	public async Task MSSPIsNotNegotiatedUntilThePeerSendsDo()
	{
		var telnet = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation((data) => ValueTask.CompletedTask)
			.AddPlugin<Protocols.MSSPProtocol>()
				.OnMSSP(config => ValueTask.CompletedTask)
			.BuildAsync();

		var mssp = telnet.PluginManager!.GetPlugin<Protocols.MSSPProtocol>()!;

		// The server already sent its own WILL MSSP as part of BuildAsync (RegisterInitialNegotiation).
		// That is this side announcing intent, not the peer agreeing to anything.
		await Assert.That(mssp.IsEnabled).IsTrue();
		await Assert.That(mssp.IsNegotiated).IsFalse();

		await InterpretAndWaitAsync(telnet, [(byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MSSP]);

		await Assert.That(mssp.IsNegotiated).IsTrue();

		await telnet.DisposeAsync();
	}

	/// <summary>
	/// MSSP, client mode: the server's <c>WILL MSSP</c> is what completes negotiation from the
	/// client's side.
	/// </summary>
	[Test]
	public async Task MSSPIsNotNegotiatedUntilThePeerSendsWill_ClientMode()
	{
		var telnet = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation((data) => ValueTask.CompletedTask)
			.AddPlugin<Protocols.MSSPProtocol>()
				.OnMSSP(config => ValueTask.CompletedTask)
			.BuildAsync();

		var mssp = telnet.PluginManager!.GetPlugin<Protocols.MSSPProtocol>()!;

		await Assert.That(mssp.IsNegotiated).IsFalse();

		await InterpretAndWaitAsync(telnet, [(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.MSSP]);

		await Assert.That(mssp.IsNegotiated).IsTrue();

		await telnet.DisposeAsync();
	}

	/// <summary>
	/// The real hook a consumer is meant to gate behaviour on: <c>OnNegotiatedAsync</c> must fire
	/// exactly once per real transition, with the right value, in wire order -- not on plugin
	/// attachment, and not more than once for one transition.
	/// </summary>
	[Test]
	public async Task OnNegotiatedAsyncFiresOnceForEachRealTransitionInOrder()
	{
		var observed = new List<bool>();

		var telnet = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation((data) => ValueTask.CompletedTask)
			.AddPlugin<ObservingMSDPProtocol>()
			.BuildAsync();

		// ObservingMSDPProtocol does not override ProtocolType, so it is keyed (and looked up) as
		// MSDPProtocol -- ProtocolPluginManager keys registrations by the plugin's declared
		// ProtocolType, not by the concrete type parameter.
		var msdp = (ObservingMSDPProtocol)telnet.PluginManager!.GetPlugin<Protocols.MSDPProtocol>()!;

		// Nothing has fired from attachment alone.
		await Assert.That(msdp.Changes.Count).IsEqualTo(0);

		await InterpretAndWaitAsync(telnet, [(byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MSDP]);
		await InterpretAndWaitAsync(telnet, [(byte)Trigger.IAC, (byte)Trigger.DONT, (byte)Trigger.MSDP]);
		await InterpretAndWaitAsync(telnet, [(byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MSDP]);

		// Checked element-by-element rather than with IsEquivalentTo, which is a set comparison and
		// would not catch the calls arriving out of order.
		await Assert.That(msdp.Changes.Count).IsEqualTo(3);
		await Assert.That(msdp.Changes[0]).IsTrue();
		await Assert.That(msdp.Changes[1]).IsFalse();
		await Assert.That(msdp.Changes[2]).IsTrue();
		await Assert.That(msdp.IsNegotiated).IsTrue();

		await telnet.DisposeAsync();
	}

	/// <summary>
	/// The transition-only guarantee <see cref="OnNegotiatedAsyncFiresOnceForEachRealTransitionInOrder"/>
	/// covers for alternating values: a second <c>DO</c> arriving while already negotiated true must not
	/// re-fire <c>OnNegotiationChangedAsync</c> for a change that did not happen. A protocol's own state
	/// machine can legitimately re-enter its accepted state (a re-affirmed <c>DO</c>, a retried
	/// handshake) for reasons that are its business, not this base class's -- but a consumer reacting to
	/// "negotiation just flipped" would otherwise see a spurious repeat.
	/// </summary>
	[Test]
	public async Task OnNegotiatedAsyncDoesNotFireTwiceForTheSameValue()
	{
		var telnet = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation((data) => ValueTask.CompletedTask)
			.AddPlugin<ObservingMSDPProtocol>()
			.BuildAsync();

		var msdp = (ObservingMSDPProtocol)telnet.PluginManager!.GetPlugin<Protocols.MSDPProtocol>()!;

		await InterpretAndWaitAsync(telnet, [(byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MSDP]);
		await InterpretAndWaitAsync(telnet, [(byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MSDP]);

		await Assert.That(msdp.Changes.Count).IsEqualTo(1);
		await Assert.That(msdp.Changes[0]).IsTrue();
		await Assert.That(msdp.IsNegotiated).IsTrue();

		await telnet.DisposeAsync();
	}

	/// <summary>
	/// LineMode's <c>WillLINEMODE</c>/<c>WontLINEMODE</c> states -- the peer announcing its own
	/// capability, as distinct from <c>DoLINEMODE</c>/<c>DontLINEMODE</c> (the peer asking us to use it)
	/// -- only logged before this fix and never called <see cref="Protocols.LineModeProtocol.OnNegotiatedAsync"/>
	/// at all, so <c>IsNegotiated</c> stayed false forever on this path regardless of what the peer said.
	/// </summary>
	[Test]
	public async Task LineModeIsNegotiatedTracksRealWillAndWont_ClientMode()
	{
		var telnet = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation((data) => ValueTask.CompletedTask)
			.AddPlugin<Protocols.LineModeProtocol>()
			.BuildAsync();

		var lineMode = telnet.PluginManager!.GetPlugin<Protocols.LineModeProtocol>()!;

		await Assert.That(lineMode.IsNegotiated).IsFalse();

		await InterpretAndWaitAsync(telnet, [(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.LINEMODE]);
		await Assert.That(lineMode.IsNegotiated).IsTrue();

		await InterpretAndWaitAsync(telnet, [(byte)Trigger.IAC, (byte)Trigger.WONT, (byte)Trigger.LINEMODE]);
		await Assert.That(lineMode.IsNegotiated).IsFalse();

		await telnet.DisposeAsync();
	}

	/// <summary>
	/// MCCP2 and MCCP3 negotiate independently -- one compresses server-to-client, the other
	/// client-to-server -- and each used to report its own true/false straight to
	/// <see cref="ITelnetProtocolPlugin.OnNegotiatedAsync"/>, so whichever settled second stomped the
	/// first: MCCP3 being refused cleared <c>IsNegotiated</c> even while MCCP2 compression was genuinely
	/// running. <c>IsNegotiated</c> must reflect the aggregate -- true while either version is accepted,
	/// false only once both are refused.
	/// </summary>
	[Test]
	public async Task MCCPIsNegotiatedReflectsEitherVersionNotWhicheverSettledLast()
	{
		var telnet = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation((data) => ValueTask.CompletedTask)
			.AddPlugin<Protocols.MCCPProtocol>()
			.BuildAsync();

		var mccp = telnet.PluginManager!.GetPlugin<Protocols.MCCPProtocol>()!;

		await Assert.That(mccp.IsNegotiated).IsFalse();

		// Client accepts MCCP2 (server-to-client compression).
		await InterpretAndWaitAsync(telnet, [(byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MCCP2]);
		await Assert.That(mccp.IsNegotiated).IsTrue();

		// Client refuses MCCP3 (client-to-server). MCCP2 is still running -- IsNegotiated must stay true.
		await InterpretAndWaitAsync(telnet, [(byte)Trigger.IAC, (byte)Trigger.DONT, (byte)Trigger.MCCP3]);
		await Assert.That(mccp.IsNegotiated).IsTrue();

		// Now MCCP2 is refused too. Nothing is negotiated any more.
		await InterpretAndWaitAsync(telnet, [(byte)Trigger.IAC, (byte)Trigger.DONT, (byte)Trigger.MCCP2]);
		await Assert.That(mccp.IsNegotiated).IsFalse();

		await telnet.DisposeAsync();
	}

	/// <summary>
	/// A plugin that records every <c>OnNegotiationChangedAsync</c> call it receives, so a test can
	/// assert on the hook itself rather than only on <see cref="ITelnetProtocolPlugin.IsNegotiated"/>.
	/// </summary>
	private sealed class ObservingMSDPProtocol : Protocols.MSDPProtocol
	{
		public List<bool> Changes { get; } = [];

		protected override ValueTask OnNegotiationChangedAsync(bool isNegotiated)
		{
			Changes.Add(isNegotiated);
			return base.OnNegotiationChangedAsync(isNegotiated);
		}
	}
}
