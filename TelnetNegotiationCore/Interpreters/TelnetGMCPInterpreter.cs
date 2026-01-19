using Stateless;
using System.Threading.Tasks;
using System;
using TelnetNegotiationCore.Models;
using System.Collections.Generic;
using OneOf;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using System.Threading.Channels;

namespace TelnetNegotiationCore.Interpreters;

public partial class TelnetInterpreter
{
	/// <summary>
	/// Bounded channel for GMCP message assembly (max 8KB per message).
	/// </summary>
	private Channel<byte> _gmcpByteChannel = Channel.CreateBounded<byte>(new BoundedChannelOptions(8192)
	{
		FullMode = BoundedChannelFullMode.DropWrite  // Drop bytes if message too large (DOS protection)
	});

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
			.OnEntry(() => {
				// Reset channel for new message
				_gmcpByteChannel = Channel.CreateBounded<byte>(new BoundedChannelOptions(8192)
				{
					FullMode = BoundedChannelFullMode.DropWrite
				});
			});

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
		// Try to write to channel; if full (>8KB), byte is dropped (DOS protection)
		_gmcpByteChannel.Writer.TryWrite(b.AsT0);
	}

	/// <summary>
	/// Sends a GMCP command to the remote party.
	/// </summary>
	/// <param name="package">The GMCP package name (e.g., "Core.Hello", "Char.Vitals").</param>
	/// <param name="command">The JSON data to send as a string.</param>
	/// <returns>A ValueTask representing the asynchronous operation.</returns>
	/// <example>
	/// await telnet.SendGMCPCommand("Char.Vitals", "{\"hp\":1000,\"maxhp\":1500}");
	/// </example>
	public ValueTask SendGMCPCommand(string package, string command) =>
		SendGMCPCommand(CurrentEncoding.GetBytes(package), CurrentEncoding.GetBytes(command));

	/// <summary>
	/// Sends a GMCP command to the remote party.
	/// </summary>
	/// <param name="package">The GMCP package name (e.g., "Core.Hello", "Char.Vitals").</param>
	/// <param name="command">The JSON data to send as a byte array.</param>
	/// <returns>A ValueTask representing the asynchronous operation.</returns>
	public ValueTask SendGMCPCommand(string package, byte[] command) =>
		SendGMCPCommand(CurrentEncoding.GetBytes(package), command);

	/// <summary>
	/// Sends a GMCP command to the remote party.
	/// </summary>
	/// <param name="package">The GMCP package name as a byte array.</param>
	/// <param name="command">The JSON data to send as a byte array.</param>
	/// <returns>A ValueTask representing the asynchronous operation.</returns>
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
		// Read all bytes from channel into list
		var gmcpBytes = new List<byte>(256);
		while (_gmcpByteChannel.Reader.TryRead(out var bt))
		{
			gmcpBytes.Add(bt);
			if (gmcpBytes.Count >= 8192)
			{
				_logger.LogWarning("GMCP message too large (>8KB), truncating");
				break;
			}
		}

		if (gmcpBytes.Count == 0)
		{
			_logger.LogWarning("Empty GMCP message received");
			return;
		}

		var space = CurrentEncoding.GetBytes(" ").First();
		var firstSpace = gmcpBytes.FindIndex(x => x == space);
		
		if (firstSpace < 0)
		{
			_logger.LogWarning("Invalid GMCP message format (no space separator)");
			return;
		}

		var packageBytes = gmcpBytes.Take(firstSpace).ToArray();
		var rest = gmcpBytes.Skip(firstSpace + 1).ToArray();
		
		// TODO: Consideration: a version of this that sends back a Dynamic or other similar object.
		var package = CurrentEncoding.GetString(packageBytes);

		if(package == "MSDP")
		{
			// Call MSDP plugin if available
			var msdpPlugin = PluginManager?.GetPlugin<Protocols.MSDPProtocol>();
			if (msdpPlugin != null && msdpPlugin.IsEnabled)
			{
				await msdpPlugin.OnMSDPMessageAsync(this, JsonSerializer.Serialize(Functional.MSDPLibrary.MSDPScan(packageBytes, CurrentEncoding)));
			}
		}
		else
		{
			// Call GMCP plugin if available
			var gmcpPlugin = PluginManager?.GetPlugin<Protocols.GMCPProtocol>();
			if (gmcpPlugin != null && gmcpPlugin.IsEnabled)
			{
				await gmcpPlugin.OnGMCPMessageAsync((Package: package, Info: CurrentEncoding.GetString(rest)));
			}
		}
	}

	/// <summary>
	/// Announces the Server will GMCP.
	/// </summary>
	/// <param name="_">Transition, ignored.</param>
	/// <returns>ValueTask</returns>
	private async ValueTask WillGMCPAsync(StateMachine<State, Trigger>.Transition? _)
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
