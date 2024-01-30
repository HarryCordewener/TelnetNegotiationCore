using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Stateless;
using TelnetNegotiationCore.Models;

namespace TelnetNegotiationCore.Interpreters
{
	public partial class TelnetInterpreter
	{
		private bool? _doEOR = null;

		public Func<Task> SignalOnPromptingAsync { get; init; }

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
					.OnEntryAsync(OnDoEORAsync);

				tsm.Configure(State.DontEOR)
					.SubstateOf(State.Accepting)
					.OnEntryAsync(OnDontEORAsync);

				RegisterInitialWilling(WillingEORAsync);
			}
			else
			{
				tsm.Configure(State.Willing)
					.Permit(Trigger.TELOPT_EOR, State.WillEOR);

				tsm.Configure(State.Refusing)
					.Permit(Trigger.TELOPT_EOR, State.WontEOR);

				tsm.Configure(State.WontEOR)
					.SubstateOf(State.Accepting)
					.OnEntryAsync(WontEORAsync);

				tsm.Configure(State.WillEOR)
					.SubstateOf(State.Accepting)
					.OnEntryAsync(OnWillEORAsync);
			}

			tsm.Configure(State.StartNegotiation)
				.Permit(Trigger.EOR, State.Prompting);

			tsm.Configure(State.Prompting)
				.SubstateOf(State.Accepting)
				.OnEntryAsync(OnEORPrompt);

			return tsm;
		}

		private async Task OnEORPrompt()
		{
			_Logger.LogDebug("Connection: {ConnectionState}", "Server is prompting EOR");
			await (SignalOnPromptingAsync?.Invoke() ?? Task.CompletedTask);
		}

		private async Task OnDontEORAsync()
		{
			_Logger.LogDebug("Connection: {ConnectionState}", "Client won't do EOR - do nothing");
			_doEOR = false;
			await Task.CompletedTask;
		}

		private async Task WontEORAsync()
		{
			_Logger.LogDebug("Connection: {ConnectionState}", "Server  won't do EOR - do nothing");
			_doEOR = false;
			await Task.CompletedTask;
		}

		/// <summary>
		/// Announce we do EOR negotiation to the client.
		/// </summary>
		private async Task WillingEORAsync()
		{
			_Logger.LogDebug("Connection: {ConnectionState}", "Announcing willingness to EOR!");
			await CallbackNegotiationAsync([(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.TELOPT_EOR]);
		}

		/// <summary>
		/// Store that we are now in EOR mode.
		/// </summary>
		private Task OnDoEORAsync(StateMachine<State, Trigger>.Transition _)
		{
			_Logger.LogDebug("Connection: {ConnectionState}", "Client supports End of Record.");
			_doEOR = true;
			return Task.CompletedTask;
		}

		/// <summary>
		/// Store that we are now in EOR mode.
		/// </summary>
		private async Task OnWillEORAsync(StateMachine<State, Trigger>.Transition _)
		{
			_Logger.LogDebug("Connection: {ConnectionState}", "Server supports End of Record.");
			_doEOR = true;
			await CallbackNegotiationAsync([(byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.TELOPT_EOR]);
		}

		/// <summary>
		/// Sends a byte message as a Prompt, if supported, by not sending an EOR at the end.
		/// </summary>
		/// <param name="send">Byte array</param>
		/// <returns>A completed Task</returns>
		public async Task SendPromptAsync(byte[] send)
		{
			await CallbackNegotiationAsync(send);
			if (_doEOR is null or false)
			{
				await CallbackNegotiationAsync(CurrentEncoding.GetBytes(Environment.NewLine));
			}
			else
			{
				await CallbackNegotiationAsync([(byte)Trigger.IAC, (byte)Trigger.EOR]);
			}
			// TODO: Tie into _doGA
			if(_doEOR is not null or false)
			{
				await CallbackNegotiationAsync([(byte)Trigger.IAC, (byte)Trigger.GA]);
			}
		}

		/// <summary>
		/// Sends a byte message, adding an EOR at the end if needed.
		/// </summary>
		/// <param name="send">Byte array</param>
		/// <returns>A completed Task</returns>
		public async Task SendAsync(byte[] send)
		{
			await CallbackNegotiationAsync(send);
			await CallbackNegotiationAsync(CurrentEncoding.GetBytes(Environment.NewLine));
		}
	}
}
