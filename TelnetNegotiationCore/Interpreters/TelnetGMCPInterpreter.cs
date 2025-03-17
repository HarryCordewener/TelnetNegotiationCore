using Stateless;
using System.Threading.Tasks;
using System;
using TelnetNegotiationCore.Models;
using System.Collections.Generic;
using OneOf;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace TelnetNegotiationCore.Interpreters;

public partial class TelnetInterpreter
{
	private List<byte> _GMCPBytes = [];

	public Func<(string Package, string Info), ValueTask> SignalOnGMCPAsync { get; init; }

	private StateMachine<State, Trigger> SetupGMCPNegotiation(StateMachine<State, Trigger> tsm)
	{
		if (Mode == TelnetMode.Server)
		{
			tsm.Configure(State.Do)
				.Permit(Trigger.GMCP, State.DoGMCP);

			tsm.Configure(State.Dont)
				.Permit(Trigger.GMCP, State.DontGMCP);

			tsm.Configure(State.DoGMCP)
				.SubstateOf(State.Accepting)
				.OnEntry(() => _logger.LogDebug("Connection: {ConnectionState}", "Client will do GMCP"));

			tsm.Configure(State.DontGMCP)
				.SubstateOf(State.Accepting)
				.OnEntry(() => _logger.LogDebug("Connection: {ConnectionState}", "Client will not GMCP"));

			RegisterInitialWilling(async () => await WillGMCPAsync(null));
		}
		else if (Mode == TelnetMode.Client)
		{
			tsm.Configure(State.Willing)
				.Permit(Trigger.GMCP, State.WillGMCP);

			tsm.Configure(State.Refusing)
				.Permit(Trigger.GMCP, State.WontGMCP);

			tsm.Configure(State.WillGMCP)
				.SubstateOf(State.Accepting)
				.OnEntryAsync(async x => await DoGMCPAsync(x));

			tsm.Configure(State.WontGMCP)
				.SubstateOf(State.Accepting)
				.OnEntry(() => _logger.LogDebug("Connection: {ConnectionState}", "Client will GMCP"));
		}

		tsm.Configure(State.SubNegotiation)
			.Permit(Trigger.GMCP, State.AlmostNegotiatingGMCP)
			.OnEntry(() => _GMCPBytes.Clear());

		TriggerHelper.ForAllTriggersButIAC(t => tsm
				.Configure(State.EvaluatingGMCPValue)
				.PermitReentry(t)
				.OnEntryFrom(ParameterizedTrigger(t), RegisterGMCPValue));

		TriggerHelper.ForAllTriggersButIAC(t => tsm
				.Configure(State.AlmostNegotiatingGMCP)
				.Permit(t, State.EvaluatingGMCPValue));

		tsm.Configure(State.EvaluatingGMCPValue)
			.Permit(Trigger.IAC, State.EscapingGMCPValue);

		tsm.Configure(State.AlmostNegotiatingGMCP)
			.Permit(Trigger.IAC, State.EscapingGMCPValue);

		tsm.Configure(State.EscapingGMCPValue)
			.Permit(Trigger.IAC, State.EvaluatingGMCPValue)
			.Permit(Trigger.SE, State.CompletingGMCPValue);

		tsm.Configure(State.CompletingGMCPValue)
			.SubstateOf(State.Accepting)
			.OnEntryAsync(async x => await CompleteGMCPNegotiation(x));

		return tsm;
	}

	/// <summary>
	/// Adds a byte to the register.
	/// </summary>
	/// <param name="b">Byte.</param>
	private void RegisterGMCPValue(OneOf<byte, Trigger> b)
	{
		_GMCPBytes.Add(b.AsT0);
	}

	public ValueTask SendGMCPCommand(string package, string command) =>
		SendGMCPCommand(CurrentEncoding.GetBytes(package), CurrentEncoding.GetBytes(command));

	public ValueTask SendGMCPCommand(string package, byte[] command) =>
		SendGMCPCommand(CurrentEncoding.GetBytes(package), command);

	public async ValueTask SendGMCPCommand(byte[] package, byte[] command)
	{
		await CallbackNegotiationAsync(
			[
				(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.GMCP,
				.. package,
				.. CurrentEncoding.GetBytes(" "),
				.. command,
				.. new[] { (byte)Trigger.IAC, (byte)Trigger.SE },
			]);
	}

	/// <summary>
	/// Completes the GMCP Negotiation. This is currently assuming a golden path.
	/// </summary>
	/// <param name="_">Transition, ignored.</param>
	/// <returns>ValueTask</returns>
	private async ValueTask CompleteGMCPNegotiation(StateMachine<State, Trigger>.Transition _)
	{
		var space = CurrentEncoding.GetBytes(" ").First();
		var firstSpace = _GMCPBytes.FindIndex(x => x == space);
		var packageBytes = _GMCPBytes.Take(firstSpace).ToArray();
		var rest = _GMCPBytes.Skip(firstSpace + 1).ToArray();
		
		// TODO: Consideration: a version of this that sends back a Dynamic or other similar object.
		var package = CurrentEncoding.GetString(packageBytes);

		if(package == "MSDP")
		{
			await (SignalOnMSDPAsync?.Invoke(this, JsonSerializer.Serialize(Functional.MSDPLibrary.MSDPScan(packageBytes, CurrentEncoding))) ?? ValueTask.CompletedTask);
		}
		else
		{
			await (SignalOnGMCPAsync?.Invoke((Package: package, Info: CurrentEncoding.GetString(packageBytes))) ?? ValueTask.CompletedTask);
		}
	}

	/// <summary>
	/// Announces the Server will GMCP.
	/// </summary>
	/// <param name="_">Transition, ignored.</param>
	/// <returns>ValueTask</returns>
	private async ValueTask WillGMCPAsync(StateMachine<State, Trigger>.Transition _)
	{
		_logger.LogDebug("Connection: {ConnectionState}", "Announcing the server will GMCP");

		await CallbackNegotiationAsync([(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.GMCP]);
	}

	private async ValueTask DoGMCPAsync(StateMachine<State, Trigger>.Transition _)
	{
		_logger.LogDebug("Connection: {ConnectionState}", "Announcing the client can do GMCP");

		await CallbackNegotiationAsync([(byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.GMCP]);
	}
}
