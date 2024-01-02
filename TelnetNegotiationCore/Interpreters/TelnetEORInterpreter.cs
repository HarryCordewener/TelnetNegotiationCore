using System.Threading.Tasks;
using Stateless;
using TelnetNegotiationCore.Models;

namespace TelnetNegotiationCore.Interpreters
{
	public partial class TelnetInterpreter
	{
		private bool? _doEOR = null;

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

			return tsm;
		}

		private async Task OnDontEORAsync()
		{
			_Logger.Debug("Connection: {connectionStatus}", "Client won't do EOR - do nothing");
			_doEOR = false;
			await Task.CompletedTask;
		}

		private async Task WontEORAsync()
		{
			_Logger.Debug("Connection: {connectionStatus}", "Server  won't do EOR - do nothing");
			_doEOR = false;
			await Task.CompletedTask;
		}

		/// <summary>
		/// Announce we do EOR negotiation to the client.
		/// </summary>
		private async Task WillingEORAsync()
		{
			_Logger.Debug("Connection: {connectionStatus}", "Announcing willingness to EOR!");
			await CallbackNegotiation(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.TELOPT_EOR });
		}

		/// <summary>
		/// Store that we are now in EOR mode.
		/// </summary>
		private Task OnDoEORAsync(StateMachine<State, Trigger>.Transition _)
		{
			_Logger.Debug("Connection: {connectionStatus}", "Client supports End of Record.");
			_doEOR = true;
			return Task.CompletedTask;
		}

		/// <summary>
		/// Store that we are now in EOR mode.
		/// </summary>
		private async Task OnWillEORAsync(StateMachine<State, Trigger>.Transition _)
		{
			_Logger.Debug("Connection: {connectionStatus}", "Server supports End of Record.");
			_doEOR = true;
			await CallbackNegotiation(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.TELOPT_EOR });
		}

		/// <summary>
		/// Sends a byte message as a Prompt, adding EOR if desired.
		/// </summary>
		/// <param name="send">Byte array</param>
		/// <returns>A completed Task</returns>
		public async Task SendPromptAsync(byte[] send)
		{
			if (_doEOR is null or false)
			{
				await CallbackNegotiation(send);
			}
			else
			{
				await CallbackNegotiation(new byte[] { (byte)Trigger.IAC, (byte)Trigger.EOR });
			}
		}
	}
}
