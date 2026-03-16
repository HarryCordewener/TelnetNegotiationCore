using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Stateless;
using TelnetNegotiationCore.Models;

namespace TelnetNegotiationCore.Interpreters;

public partial class TelnetInterpreter
{
	private bool? _doEOR = null;

	// Cached negotiation byte arrays to avoid repeated allocations
	private static readonly byte[] s_willEOR = [(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.TELOPT_EOR];
	private static readonly byte[] s_doEOR = [(byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.TELOPT_EOR];

	/// <summary>
	/// Character set Negotiation will set the Character Set and Character Page Server & Client have agreed to.
	/// </summary>
	/// <param name="tsm">The state machine.</param>
	/// <returns>Itself</returns>
	private StateMachine<State, Trigger> SetupEORNegotiation(StateMachine<State, Trigger> tsm)
	{
		if (Mode == TelnetMode.Server)
		{
			tsm.Configure(State.Do)
				.Permit(Trigger.TELOPT_EOR, State.DoEOR);

			tsm.Configure(State.Dont)
				.Permit(Trigger.TELOPT_EOR, State.DontEOR);

			tsm.Configure(State.DoEOR)
				.SubstateOf(State.Accepting)
				.OnEntryAsync(async x => await OnDoEORAsync(x));

			tsm.Configure(State.DontEOR)
				.SubstateOf(State.Accepting)
				.OnEntryAsync(async () => await OnDontEORAsync());

			RegisterInitialWilling(async () => await WillingEORAsync());
		}
		else
		{
			tsm.Configure(State.Willing)
				.Permit(Trigger.TELOPT_EOR, State.WillEOR);

			tsm.Configure(State.Refusing)
				.Permit(Trigger.TELOPT_EOR, State.WontEOR);

			tsm.Configure(State.WontEOR)
				.SubstateOf(State.Accepting)
				.OnEntryAsync(async () => await WontEORAsync());

			tsm.Configure(State.WillEOR)
				.SubstateOf(State.Accepting)
				.OnEntryAsync(async x => await OnWillEORAsync(x));
		}

		tsm.Configure(State.StartNegotiation)
			.Permit(Trigger.EOR, State.Prompting);

		tsm.Configure(State.Prompting)
			.SubstateOf(State.Accepting)
			.OnEntryAsync(async () => await OnEORPrompt());

		return tsm;
	}

	private async ValueTask OnEORPrompt()
	{
		_logger.LogDebug("Connection: {ConnectionState}", "Server is prompting EOR");
		
		// Call EOR plugin if available
		var eorPlugin = PluginManager?.GetPlugin<Protocols.EORProtocol>();
		if (eorPlugin != null && eorPlugin.IsEnabled)
		{
			await eorPlugin.OnPromptAsync();
		}
	}

	private ValueTask OnDontEORAsync()
	{
		_logger.LogDebug("Connection: {ConnectionState}", "Client won't do EOR - do nothing");
		_doEOR = false;
		return default(ValueTask);
	}

	private ValueTask WontEORAsync()
	{
		_logger.LogDebug("Connection: {ConnectionState}", "Server  won't do EOR - do nothing");
		_doEOR = false;
		return default(ValueTask);
	}

	/// <summary>
	/// Announce we do EOR negotiation to the client.
	/// </summary>
	private async ValueTask WillingEORAsync()
	{
		_logger.LogDebug("Connection: {ConnectionState}", "Announcing willingness to EOR!");
		await WriteToNetworkAsync(s_willEOR);
	}

	/// <summary>
	/// Store that we are now in EOR mode.
	/// </summary>
	private ValueTask OnDoEORAsync(StateMachine<State, Trigger>.Transition _)
	{
		_logger.LogDebug("Connection: {ConnectionState}", "Client supports End of Record.");
		_doEOR = true;
		return default(ValueTask);
	}

	/// <summary>
	/// Store that we are now in EOR mode.
	/// </summary>
	private async ValueTask OnWillEORAsync(StateMachine<State, Trigger>.Transition _)
	{
		_logger.LogDebug("Connection: {ConnectionState}", "Server supports End of Record.");
		_doEOR = true;
		await WriteToNetworkAsync(s_doEOR);
	}

	/// <summary>
	/// Sends a byte message as a Prompt, if supported, by not sending an EOR at the end.
	/// IAC bytes (255) in <paramref name="send"/> are automatically escaped.
	/// </summary>
	/// <param name="send">Byte array</param>
	/// <returns>A completed ValueTask</returns>
	public async ValueTask SendPromptAsync(byte[] send)
	{
		var safeSend = TelnetSafeBytesInternal(send);
		if (_doEOR is null or false)
		{
			// Pre-allocate exact-size buffer: safeSend + CR LF
			var output = new byte[safeSend.Length + 2];
			safeSend.AsSpan().CopyTo(output);
			output[safeSend.Length] = (byte)'\r';
			output[safeSend.Length + 1] = (byte)'\n';
			await WriteToNetworkAsync(output);
		}
		else if(_doEOR is true)
		{
			// Pre-allocate exact-size buffer: safeSend + IAC EOR
			var output = new byte[safeSend.Length + 2];
			safeSend.AsSpan().CopyTo(output);
			output[safeSend.Length] = (byte)Trigger.IAC;
			output[safeSend.Length + 1] = (byte)Trigger.EOR;
			await WriteToNetworkAsync(output);
		}
		else if (_doGA is not null)
		{
			// Pre-allocate exact-size buffer: safeSend + IAC GA
			var output = new byte[safeSend.Length + 2];
			safeSend.AsSpan().CopyTo(output);
			output[safeSend.Length] = (byte)Trigger.IAC;
			output[safeSend.Length + 1] = (byte)Trigger.GA;
			await WriteToNetworkAsync(output);
		}
	}

	/// <summary>
	/// Sends a byte message, adding an EOR at the end if needed.
	/// IAC bytes (255) in <paramref name="send"/> are automatically escaped.
	/// </summary>
	/// <param name="send">Byte array</param>
	/// <returns>A completed ValueTask</returns>
	public async ValueTask SendAsync(byte[] send)
	{
		var safeSend = TelnetSafeBytesInternal(send);
		// Pre-allocate exact-size buffer: safeSend + CR LF
		var output = new byte[safeSend.Length + 2];
		safeSend.AsSpan().CopyTo(output);
		output[safeSend.Length] = (byte)'\r';
		output[safeSend.Length + 1] = (byte)'\n';
		await WriteToNetworkAsync(output);
	}
}
