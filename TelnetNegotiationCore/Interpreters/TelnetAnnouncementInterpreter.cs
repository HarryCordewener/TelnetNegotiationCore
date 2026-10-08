using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Plugins;

namespace TelnetNegotiationCore.Interpreters;

public partial class TelnetInterpreter
{
	/// <summary>
	/// The offers being collected while an initial negotiation runs, and the interpreter running it,
	/// or null outside one.
	/// </summary>
	/// <remarks>
	/// Async-local so that only writes made by the initial negotiation itself are collected. The
	/// byte-processing loop can be answering the peer at the same moment, and its answers are not
	/// offers this side made. The owner is kept because the value flows into whatever the
	/// negotiation calls: a negotiation callback that writes to another interpreter, as a proxy
	/// does, must not have that interpreter's writes taken for this one's offers.
	/// </remarks>
	private static readonly AsyncLocal<OfferCapture?> s_offerCapture = new();

	private sealed record OfferCapture(TelnetInterpreter Owner, List<(byte Verb, byte Option)> Offers);

	private readonly List<(byte Verb, byte Option)> _initialOffers = [];

	/// <summary>
	/// The <c>WILL</c> and <c>DO</c> offers this side made when the connection started, or when
	/// <see cref="AnnounceSupportAsync"/> last made them, in the order they were sent.
	/// </summary>
	public IReadOnlyList<(byte Verb, byte Option)> InitialOffers
	{
		get
		{
			lock (_initialOffers)
			{
				return _initialOffers.ToArray();
			}
		}
	}

	/// <summary>
	/// Withdraws every option this side offered when the connection started: <c>WONT</c> for each
	/// <c>WILL</c> and <c>DONT</c> for each <c>DO</c>. Compression this side started is ended first,
	/// so the refusals go out in the clear.
	/// </summary>
	/// <remarks>
	/// <para>
	/// This is for a copyover, or any other point where the application is about to lose what the
	/// peer told it. GMCP, MSDP, MCCP3, MTTS and MNES all describe it the same way: "before the
	/// actual copyover, the MUD server should send IAC WONT GMCP, the client in turn should fully
	/// disable GMCP. After the copyover has finished the server and client behave as if the client
	/// has just connected", which is <see cref="AnnounceSupportAsync"/>. It is what MTH's
	/// <c>unannounce_support</c> does.
	/// </para>
	/// <para>
	/// The peer's answers arrive through the usual negotiation handlers, so each protocol's
	/// <see cref="Plugins.ITelnetProtocolPlugin.IsNegotiated"/> drops as the peer acknowledges.
	/// </para>
	/// </remarks>
	/// <param name="cancellationToken">Token to cancel the wait for the write lock.</param>
	public async ValueTask UnannounceSupportAsync(CancellationToken cancellationToken = default)
	{
		if (PluginManager is not null)
		{
			foreach (var plugin in PluginManager.GetAllPlugins())
			{
				if (plugin.IsEnabled && plugin is TelnetProtocolPluginBase withdrawing)
				{
					await withdrawing.OnUnannounceAsync();
				}
			}
		}

		foreach (var (verb, option) in InitialOffers)
		{
			var refusal = verb == (byte)Trigger.WILL ? (byte)Trigger.WONT : (byte)Trigger.DONT;
			_logger.LogDebug("Withdrawing offer {Verb} {Option}", verb, option);
			await WriteToNetworkAsync(new[] { (byte)Trigger.IAC, refusal, option }, cancellationToken);
		}
	}

	/// <summary>
	/// Makes this side's initial offers again, as if the connection had just started. The pair to
	/// <see cref="UnannounceSupportAsync"/>, for after a copyover.
	/// </summary>
	public ValueTask AnnounceSupportAsync() => RunInitialNegotiationAsync();

	private async ValueTask RunInitialNegotiationAsync()
	{
		var offers = new List<(byte Verb, byte Option)>();
		s_offerCapture.Value = new OfferCapture(this, offers);
		try
		{
			foreach (var initial in _initialCall)
			{
				await initial();
			}
		}
		finally
		{
			s_offerCapture.Value = null;

			// Even when one of them failed: the offers that did go out are the ones a later
			// UnannounceSupportAsync has to withdraw.
			lock (_initialOffers)
			{
				_initialOffers.Clear();
				_initialOffers.AddRange(offers);
			}
		}
	}

	/// <summary>
	/// Notes the <c>IAC WILL</c> and <c>IAC DO</c> frames in a write made by this interpreter's
	/// initial negotiation.
	/// </summary>
	private void NoteOffers(ReadOnlySpan<byte> data)
	{
		if (s_offerCapture.Value is { } capture && ReferenceEquals(capture.Owner, this))
		{
			CollectOffers(data, capture.Offers);
		}
	}

	/// <summary>
	/// Adds each <c>IAC WILL</c> and <c>IAC DO</c> frame in <paramref name="data"/> to
	/// <paramref name="offers"/>. An escaped <c>IAC IAC</c> is data, not the start of a command.
	/// </summary>
	internal static void CollectOffers(ReadOnlySpan<byte> data, List<(byte Verb, byte Option)> offers)
	{
		for (var i = 0; i + 1 < data.Length; i++)
		{
			if (data[i] != (byte)Trigger.IAC)
			{
				continue;
			}

			var command = data[i + 1];
			switch (command)
			{
				case (byte)Trigger.WILL or (byte)Trigger.DO when i + 2 < data.Length:
					offers.Add((command, data[i + 2]));
					i += 2;
					break;
				case (byte)Trigger.WONT or (byte)Trigger.DONT or (byte)Trigger.SB:
					// The option byte is part of the command, even when it is 255.
					i += 2;
					break;
				default:
					// IAC IAC, IAC SE and the two-byte commands.
					i++;
					break;
			}
		}
	}
}
