using Microsoft.Extensions.Logging;
using Stateless;
using System.Threading.Tasks;
using TelnetNegotiationCore.Models;

namespace TelnetNegotiationCore.Interpreters;

public partial class TelnetInterpreter
{
	private bool? _doGA = true;

	/// <summary>
	/// Character set Negotiation will set the Character Set and Character Page Server & Client have agreed to.
	/// </summary>
	/// <param name="tsm">The state machine.</param>
	/// <returns>Itself</returns>
	private StateMachine<State, Trigger> SetupSuppressGANegotiation(StateMachine<State, Trigger> tsm)
	{
		if (Mode == TelnetMode.Server)
		{
			tsm.Configure(State.Do)
				.Permit(Trigger.SUPPRESSGOAHEAD, State.DoSUPPRESSGOAHEAD);

			tsm.Configure(State.Dont)
				.Permit(Trigger.SUPPRESSGOAHEAD, State.DontSUPPRESSGOAHEAD);

			tsm.Configure(State.DoSUPPRESSGOAHEAD)
				.SubstateOf(State.Accepting)
				.OnEntryAsync(OnDoSuppressGAAsync);

			tsm.Configure(State.DontSUPPRESSGOAHEAD)
				.SubstateOf(State.Accepting)
				.OnEntryAsync(OnDontSuppressGAAsync);

			RegisterInitialWilling(WillingSuppressGAAsync);
		}
		else
		{
			tsm.Configure(State.Willing)
				.Permit(Trigger.SUPPRESSGOAHEAD, State.WillSUPPRESSGOAHEAD);

			tsm.Configure(State.Refusing)
				.Permit(Trigger.SUPPRESSGOAHEAD, State.WontSUPPRESSGOAHEAD);

			tsm.Configure(State.WontSUPPRESSGOAHEAD)
				.SubstateOf(State.Accepting)
				.OnEntryAsync(WontSuppressGAAsync);

			tsm.Configure(State.WillSUPPRESSGOAHEAD)
				.SubstateOf(State.Accepting)
				.OnEntryAsync(OnWillSuppressGAAsync);
		}

		return tsm;
	}


	private async Task OnSUPPRESSGOAHEADPrompt()
	{
		_Logger.LogDebug("Connection: {ConnectionState}", "Server is prompting SUPPRESSGOAHEAD");
		await (SignalOnPromptingAsync?.Invoke() ?? Task.CompletedTask);
	}

	private async Task OnDontSuppressGAAsync()
	{
		_Logger.LogDebug("Connection: {ConnectionState}", "Client won't do SUPPRESSGOAHEAD - do nothing");
		_doGA = true;
		await Task.CompletedTask;
	}

	private async Task WontSuppressGAAsync()
	{
		_Logger.LogDebug("Connection: {ConnectionState}", "Server won't do SUPPRESSGOAHEAD - do nothing");
		_doGA = true;
		await Task.CompletedTask;
	}

	/// <summary>
	/// Announce we do SUPPRESSGOAHEAD negotiation to the client.
	/// </summary>
	private async Task WillingSuppressGAAsync()
	{
		_Logger.LogDebug("Connection: {ConnectionState}", "Announcing willingness to SUPPRESSGOAHEAD!");
		await CallbackNegotiationAsync([(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.SUPPRESSGOAHEAD]);
	}

	/// <summary>
	/// Store that we are now in SUPPRESSGOAHEAD mode.
	/// </summary>
	private Task OnDoSuppressGAAsync(StateMachine<State, Trigger>.Transition _)
	{
		_Logger.LogDebug("Connection: {ConnectionState}", "Client supports End of Record.");
		_doGA =false;
		return Task.CompletedTask;
	}

	/// <summary>
	/// Store that we are now in SUPPRESSGOAHEAD mode.
	/// </summary>
	private async Task OnWillSuppressGAAsync(StateMachine<State, Trigger>.Transition _)
	{
		_Logger.LogDebug("Connection: {ConnectionState}", "Server supports End of Record.");
		_doGA = false;
		await CallbackNegotiationAsync([(byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.SUPPRESSGOAHEAD]);
	}
}
