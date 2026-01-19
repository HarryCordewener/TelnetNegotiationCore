using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OneOf;
using Stateless;
using TelnetNegotiationCore.Models;
using System.Threading.Channels;

namespace TelnetNegotiationCore.Interpreters;

/// <summary>
/// Implements http://www.faqs.org/rfcs/rfc1073.html
/// </summary>
/// <remarks>
/// TODO: Implement Client Side
/// </remarks>
public partial class TelnetInterpreter
{
	/// <summary>
	/// Bounded channel for MSDP message assembly (max 8KB per message).
	/// </summary>
	private Channel<byte> _msdpByteChannel = Channel.CreateBounded<byte>(new BoundedChannelOptions(8192)
	{
		FullMode = BoundedChannelFullMode.DropWrite  // Drop bytes if message too large (DOS protection)
	});

	public Func<TelnetInterpreter, string, ValueTask>? SignalOnMSDPAsync { get; init; }

	/// <summary>
	/// Mud Server Status Protocol will provide information to the requestee about the server's contents.
	/// </summary>
	/// <param name="tsm">The state machine.</param>
	/// <returns>Itself</returns>
	private StateMachine<State, Trigger> SetupMSDPNegotiation(StateMachine<State, Trigger> tsm)
	{
		if (Mode == TelnetMode.Server)
		{
			tsm.Configure(State.Do)
				.Permit(Trigger.MSDP, State.DoMSDP);

			tsm.Configure(State.Dont)
				.Permit(Trigger.MSDP, State.DontMSDP);

			tsm.Configure(State.DoMSDP)
				.SubstateOf(State.Accepting)
				.OnEntryAsync(async x => await OnDoMSDPAsync(x));

			tsm.Configure(State.DontMSDP)
				.SubstateOf(State.Accepting)
				.OnEntry(() => _logger.LogDebug("Connection: {ConnectionState}", "Client won't do MSDP - do nothing"));

			RegisterInitialWilling(async () => await WillingMSDPAsync());
		}
		else
		{
			tsm.Configure(State.Willing)
				.Permit(Trigger.MSDP, State.WillMSDP);

			tsm.Configure(State.Refusing)
				.Permit(Trigger.MSDP, State.WontMSDP);

			tsm.Configure(State.WillMSDP)
				.SubstateOf(State.Accepting)
				.OnEntryAsync(async () => await OnWillMSDPAsync());

			tsm.Configure(State.WontMSDP)
				.SubstateOf(State.Accepting)
				.OnEntry(() => _logger.LogDebug("Connection: {ConnectionState}", "Server won't do MSDP - do nothing"));

			tsm.Configure(State.SubNegotiation)
				.Permit(Trigger.MSDP, State.AlmostNegotiatingMSDP)
				.OnEntry(() => {
					// Reset channel for new message
					_msdpByteChannel = Channel.CreateBounded<byte>(new BoundedChannelOptions(8192)
					{
						FullMode = BoundedChannelFullMode.DropWrite
					});
				});

			tsm.Configure(State.AlmostNegotiatingMSDP)
				.Permit(Trigger.MSDP_VAR, State.EvaluatingMSDP)
				.Permit(Trigger.MSDP_VAL, State.EvaluatingMSDP)
				.Permit(Trigger.MSDP_ARRAY_OPEN, State.EvaluatingMSDP)
				.Permit(Trigger.MSDP_ARRAY_CLOSE, State.EvaluatingMSDP)
				.Permit(Trigger.MSDP_TABLE_OPEN, State.EvaluatingMSDP)
				.Permit(Trigger.MSDP_TABLE_CLOSE, State.EvaluatingMSDP);

			tsm.Configure(State.EvaluatingMSDP)
				.Permit(Trigger.IAC, State.EscapingMSDP);

			tsm.Configure(State.EscapingMSDP)
				.Permit(Trigger.IAC, State.EvaluatingMSDP)
				.Permit(Trigger.SE, State.CompletingMSDP);

			tsm.Configure(State.CompletingMSDP)
				.SubstateOf(State.Accepting)
				.OnEntry(ReadMSDPValues);

			TriggerHelper.ForAllTriggersButIAC(t =>
				tsm.Configure(State.EvaluatingMSDP).OnEntryFrom(ParameterizedTrigger(t), CaptureMSDPValue).PermitReentry(t));
		}

		return tsm;
	}

	private void CaptureMSDPValue(OneOf<byte, Trigger> b) => _msdpByteChannel.Writer.TryWrite(b.AsT0);

	private void ReadMSDPValues()
	{
		// Read all bytes from channel
		var msdpBytes = new List<byte>(256);
		while (_msdpByteChannel.Reader.TryRead(out var bt))
		{
			msdpBytes.Add(bt);
			if (msdpBytes.Count >= 8192)
			{
				_logger.LogWarning("MSDP message too large (>8KB), truncating");
				break;
			}
		}

		if (msdpBytes.Count > 0)
		{
			SignalOnMSDPAsync?.Invoke(this, JsonSerializer.Serialize(Functional.MSDPLibrary.MSDPScan(msdpBytes.Skip(1), CurrentEncoding)));
		}
	}

	/// <summary>
	/// Announce we do MSDP negotiation to the client.
	/// </summary>
	private async ValueTask WillingMSDPAsync()
	{
		_logger.LogDebug("Connection: {ConnectionState}", "Announcing willingness to MSDP!");
		await CallbackNegotiationAsync([(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.MSDP]);
	}

	/// <summary>
	/// Announce the MSDP we support to the client after getting a Do.
	/// </summary>
	private ValueTask OnDoMSDPAsync(StateMachine<State, Trigger>.Transition _)
	{
		_logger.LogDebug("Connection: {ConnectionState}", "Client will do MSDP output");
		return ValueTask.CompletedTask;
	}

	/// <summary>
	/// Announce we do MSDP negotiation to the server.
	/// </summary>
	private async ValueTask OnWillMSDPAsync()
	{
		_logger.LogDebug("Connection: {ConnectionState}", "Announcing willingness to MSDP!");
		await CallbackNegotiationAsync([(byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MSDP]);
	}
}
