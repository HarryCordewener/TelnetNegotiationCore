using Stateless;
using System.Collections.Generic;
using System.Linq;
using MoreLinq;
using System;
using TelnetNegotiationCore.Models;
using System.IO;
using Microsoft.Extensions.Logging;

namespace TelnetNegotiationCore.Interpreters
{
	/// <summary>
	/// The Safe Interpreter, providing ways to not crash the system when we are given a STATE we were not expecting.
	/// </summary>
	public partial class TelnetInterpreter
	{
		/// <summary>
		/// Create a byte[] that is safe to send over telnet by repeating 255s. 
		/// </summary>
		/// <param name="str">The string intent to be sent across the wire.</param>
		/// <returns>The new byte[] with 255s duplicated.</returns>
		public byte[] TelnetSafeString(string str)
		{
			byte[] result;
			var x = CurrentEncoding.GetBytes(str).AsSpan();

			using (var memStream = new MemoryStream())
			{
				foreach (byte bt in x)
				{
					memStream.Write(bt == 255 ? ([255, 255]) : (byte[])[bt]);
				}
				result = memStream.ToArray();
			}

			return result;
		}

		/// <summary>
		/// Create a byte[] that is safe to send over telnet by repeating 255s. 
		/// Only use this function if you do not intend to send any kind of negotiation.
		/// </summary>
		/// <param name="str">The original bytes intent to be sent.</param>
		/// <returns>The new byte[] with 255s duplicated.</returns>
		public byte[] TelnetSafeBytes(byte[] str)
		{
			byte[] result;
			var x = str;

			using (var memStream = new MemoryStream())
			{
				foreach (byte bt in x)
				{
					memStream.Write(bt == 255 ? ([255, 255]) : (byte[])[bt]);
				}
				result = memStream.ToArray();
			}

			return result;
		}

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

			foreach (var stateInfo in info.States.Join(refuseThese, x => x.UnderlyingState, y => y, (x, y) => x))
			{
				var state = (State)stateInfo.UnderlyingState;
				var outboundUnhandledTriggers = triggers.Except(stateInfo.Transitions.Select(x => (Trigger)x.Trigger.UnderlyingTrigger));

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
								_Logger.LogDebug("Connection: {ConnectionState}", $"Telling the Client, Won't respond to the trigger: {trigger}.");
								await CallbackNegotiationAsync([(byte)Trigger.IAC, (byte)Trigger.WONT, (byte)trigger]);
							});
					}
					else if (state is State.Willing)
					{
						tsm.Configure((State)Enum.Parse(typeof(State), $"Bad{state}"))
							.OnEntryFromAsync(trigger, async () =>
							{
								_Logger.LogDebug("Connection: {ConnectionState}", $"Telling the Client, Don't send {trigger}.");
								await CallbackNegotiationAsync([(byte)Trigger.IAC, (byte)Trigger.DONT, (byte)trigger]);
							});
					}
				}
			}

			var underlyingTriggers = info.States.First(x => (State)x.UnderlyingState == State.SubNegotiation).Transitions
					.Select(x => (Trigger)x.Trigger.UnderlyingTrigger);

			triggers
				.Except(underlyingTriggers)
				.ForEach(trigger => tsm.Configure(State.SubNegotiation).Permit(trigger, State.BadSubNegotiation));

			TriggerHelper.ForAllTriggersButIAC(t => tsm.Configure(State.BadSubNegotiation).Permit(t, State.BadSubNegotiationEvaluating));
			TriggerHelper.ForAllTriggersButIAC(t => tsm.Configure(State.BadSubNegotiationEvaluating).PermitReentry(t));

			tsm.Configure(State.BadSubNegotiation)
				.Permit(Trigger.IAC, State.BadSubNegotiationEscaping)
				.OnEntry(() => _Logger.LogDebug("Connection: {ConnectionState}", $"Unsupported SubNegotiation."));
			tsm.Configure(State.BadSubNegotiationEscaping)
				.Permit(Trigger.IAC, State.BadSubNegotiationEvaluating)
				.Permit(Trigger.SE, State.BadSubNegotiationCompleting);
			tsm.Configure(State.BadSubNegotiationCompleting)
				.OnEntry(() => _Logger.LogDebug("Connection: Explicitly ignoring the SubNegotiation that was sent."))
				.SubstateOf(State.Accepting);

			var states = tsm.GetInfo().States;
			var acceptingStateInfo = states.Where(x => (State)x.UnderlyingState == State.Accepting);

			var statesAllowingForErrorTransitions = states
				.Except(acceptingStateInfo);

			statesAllowingForErrorTransitions.ForEach(state => tsm.Configure((State)state.UnderlyingState).Permit(Trigger.Error, State.Accepting));

			tsm.OnUnhandledTrigger(async (state, trigger, unmetguards) =>
			{
				_Logger.LogCritical("Bad transition from {@State} with trigger {@Trigger} due to unmet guards: {@UnmetGuards}. Cannot recover. " +
					"Ignoring character and attempting to recover.", state, trigger, unmetguards);
				await tsm.FireAsync(ParameterizedTrigger(Trigger.Error), Trigger.Error);
			});

			return tsm;
		}
	}
}
