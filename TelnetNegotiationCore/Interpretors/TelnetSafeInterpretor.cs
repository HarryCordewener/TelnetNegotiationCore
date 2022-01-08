using Stateless;
using System.Collections.Generic;
using System.Linq;
using MoreLinq;
using System;
using TelnetNegotiationCore.Models;

namespace TelnetNegotiationCore.Interpretors
{
	/// <summary>
	/// The Safe Interpretor, providing ways to not crash the system when we are given a STATE we were not expecting.
	/// </summary>
	public partial class TelnetInterpretor
	{
		/// <summary>
		/// Protect against State Transitions and Telnet Negotiations we do not recognize.
		/// </summary>
		/// <remarks>
		/// TODO: Log what byte was sent using TriggerWithParameter output.
		/// </remarks>
		/// <param name="tsm">The state machine.</param>
		/// <returns>Itself</returns>
		private StateMachine<State, Trigger> SetupSafeNegotiation(StateMachine<State, Trigger> tsm)
		{
			var info = tsm.GetInfo();
			var triggers = Enum.GetValues(typeof(Trigger)).OfType<Trigger>();
			var refuseThese = new List<State> { State.Willing, State.Refusing, State.Do, State.Dont };

			foreach (var stateinfo in info.States.Join(refuseThese, x => x.UnderlyingState, y => y, (x,y) => x))
			{
				var state = (State)stateinfo.UnderlyingState;
				var outboundUnhandledTriggers = triggers.Except(stateinfo.Transitions.Select(x => (Trigger)x.Trigger.UnderlyingTrigger));

				foreach (var trigger in outboundUnhandledTriggers)
				{
					tsm.Configure(state).Permit(trigger, (State)Enum.Parse(typeof(State), $"Bad{state}"));
					tsm.Configure((State)Enum.Parse(typeof(State), $"Bad{state}"))
						.SubstateOf(State.Accepting);

					if (state is State.Do)
					{
						tsm.Configure((State)Enum.Parse(typeof(State), $"Bad{state}"))
							.OnEntryFromAsync(trigger, async () =>
							{
								_Logger.Debug("Connection: {connectionStatus}", $"Telling the Client, Won't respond to the trigger: {trigger}.");
								await CallbackNegotiation(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WONT, (byte)trigger });
							});
					}
					else if (state is State.Willing)
					{
						tsm.Configure((State)Enum.Parse(typeof(State), $"Bad{state}"))
							.OnEntryFromAsync(trigger, async () =>
							{
								_Logger.Debug("Connection: {connectionStatus}", $"Telling the Client, Don't send {trigger}.");
								await CallbackNegotiation(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DONT, (byte)trigger });
							});
					}
				}
			}

			var subnegotiationUnhandledTriggers = triggers
				.Except(info.States.First(x => (State)x.UnderlyingState == State.SubNegotiation).Transitions
					.Select(x => (Trigger)x.Trigger.UnderlyingTrigger));

			foreach (var trigger in subnegotiationUnhandledTriggers)
			{
				tsm.Configure(State.SubNegotiation)
					.Permit(trigger, State.BadSubNegotiation);
			}

			TriggerHelper.ForAllTriggersButIAC(t => tsm.Configure(State.BadSubNegotiation).Permit(t, State.BadSubNegotiationEvaluating));
			TriggerHelper.ForAllTriggersButIAC(t => tsm.Configure(State.BadSubNegotiationEvaluating).PermitReentry(t));

			tsm.Configure(State.BadSubNegotiation)
				.Permit(Trigger.IAC, State.BadSubNegotiationEscaping)
				.OnEntry(() => _Logger.Debug("Connection: {connectionState}", $"Unsupported subnegotiation."));
			tsm.Configure(State.BadSubNegotiationEscaping)
				.Permit(Trigger.IAC, State.BadSubNegotiationEvaluating)
				.Permit(Trigger.SE, State.BadSubNegotiationCompleting);
			tsm.Configure(State.BadSubNegotiationCompleting)
				.OnEntry(() => _Logger.Debug("Connection: Explicitly ignoring the subnegotiation that was sent."))
				.SubstateOf(State.Accepting);

			var states = tsm.GetInfo().States;
			var acceptingStateInfo = states.Where(x => (State)x.UnderlyingState == State.Accepting);

			foreach (var state in states
				.Except(acceptingStateInfo)
				.Where(x => x.Transitions.Any(x => (Trigger)x.Trigger.UnderlyingTrigger == Trigger.Error)))
			{
				tsm.Configure((State)state.UnderlyingState)
					.Permit(Trigger.Error, State.Accepting);
			}

			tsm.OnUnhandledTrigger(async (state, trigger) =>
			{
				_Logger.Fatal("Bad transition from {@state} with trigger {@trigger}. Cannot recover. Ignoring character and attempting to recover.", state, trigger);
				await tsm.FireAsync(Trigger.Error);
			});

			return tsm;
		}
	}
}
