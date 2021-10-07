using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using MoreLinq;
using Stateless;

namespace TelnetNegotiationCore
{
	public partial class TelnetInterpretor
	{
		private bool _doEOR = false;

		/// <summary>
		/// Character set Negotiation will set the Character Set and Character Page Server & Client have agreed to.
		/// </summary>
		/// <param name="tsm">The state machine.</param>
		/// <returns>Itself</returns>
		private StateMachine<State, Trigger> SetupEORNegotiation(StateMachine<State, Trigger> tsm)
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
				.OnEntry(() => _Logger.Debug("Connection: {connectionStatus}", "Client won't do EOR - do nothing"));

			RegisterInitialWilling(WillingEORAsync);

			return tsm;
		}

		/// <summary>
		/// Announce we do EOR negotiation to the client.
		/// </summary>
		private async Task WillingEORAsync()
		{
			_Logger.Debug("Connection: {connectionStatus}", "Announcing willingness to EOR!");
			await _OutputStream.BaseStream.WriteAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.TELOPT_EOR });
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
		/// Sends a byte message as a Prompt, adding EOR if desired.
		/// </summary>
		/// <param name="send">Byte array</param>
		/// <returns>A completed Task</returns>
		public async Task SendPromptAsync(byte[] send)
		{
			await _OutputStream.BaseStream.WriteAsync(send);
			if(_doEOR) await _OutputStream.BaseStream.WriteAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.EOR });
		}
	}
}
