using Stateless;
using System.Collections.Generic;
using System.Linq;
using System;
using System.Collections.Immutable;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Generated;
using System.IO;
using Microsoft.Extensions.Logging;

namespace TelnetNegotiationCore.Interpreters;

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
		var byteSpan = CurrentEncoding.GetBytes(str).AsSpan();

		using var memStream = new MemoryStream();
		foreach (var bt in byteSpan)
		{
			memStream.Write(bt == 255 
				? [255, 255] 
				: (byte[])[bt]);
		}
		return memStream.ToArray();
	}

	/// <summary>
	/// Create a byte[] that is safe to send over telnet by repeating 255s. 
	/// Only use this function if you do not intend to send any kind of negotiation.
	/// </summary>
	/// <param name="str">The original bytes intent to be sent.</param>
	/// <returns>The new byte[] with 255s duplicated.</returns>
	public byte[] TelnetSafeBytes(byte[] str)
	{
		var x = str;

		using var memStream = new MemoryStream();
		foreach (var bt in x.AsSpan())
		{
			memStream.Write(bt == 255 
				? [255, 255] 
				: [bt]);
		}
		return memStream.ToArray();
	}

	/// <summary>
	/// Protect against State Transitions and Telnet Negotiations we do not recognize.
	/// </summary>
	/// <remarks>
	/// TODO: Log what byte was sent using TriggerWithParameter output.
	/// </remarks>
	/// <param name="tsm">The state machine.</param>
	/// <returns>Itself</returns>
	internal void ApplySafetyConfiguration()
	{
		SetupSafeNegotiation(TelnetStateMachine);
	}

	private StateMachine<State, Trigger> SetupSafeNegotiation(StateMachine<State, Trigger> tsm)
	{
		var info = tsm.GetInfo();
		// Use generated AllValues instead of reflection
		var triggers = TriggerExtensions.AllValues.ToArray();
		var refuseThese = new List<State> { State.Willing, State.Refusing, State.Do, State.Dont };

		foreach (var stateInfo in info.States.Join(refuseThese, x => x.UnderlyingState, y => y, (x, y) => x))
		{
			var state = (State)stateInfo.UnderlyingState;
			var outboundUnhandledTriggers = triggers.Except(stateInfo.Transitions.Select(x => (Trigger)x.Trigger.UnderlyingTrigger));

			foreach (var trigger in outboundUnhandledTriggers)
			{
				// Use generated GetBadState method instead of Enum.Parse
				var badState = StateExtensions.GetBadState(state);
				tsm.Configure(state).Permit(trigger, badState);
				tsm.Configure(badState)
					.SubstateOf(State.Accepting);

				if (state is State.Do)
				{
					tsm.Configure(badState)
						.OnEntryFromAsync(trigger, async () =>
						{
							_logger.LogDebug("Connection: {ConnectionState}", $"Telling the Client, Won't respond to the trigger: {trigger}.");
							await CallbackNegotiationAsync([(byte)Trigger.IAC, (byte)Trigger.WONT, (byte)trigger]);
						});
				}
				else if (state is State.Willing)
				{
					tsm.Configure(badState)
						.OnEntryFromAsync(trigger, async () =>
						{
							_logger.LogDebug("Connection: {ConnectionState}", $"Telling the Client, Don't send {trigger}.");
							await CallbackNegotiationAsync([(byte)Trigger.IAC, (byte)Trigger.DONT, (byte)trigger]);
						});
				}
			}
		}

		var underlyingTriggers = info.States.First(x => (State)x.UnderlyingState == State.SubNegotiation).Transitions
				.Select(x => (Trigger)x.Trigger.UnderlyingTrigger);

		foreach(var trigger in triggers.Except(underlyingTriggers))
		{
			tsm.Configure(State.SubNegotiation).Permit(trigger, State.BadSubNegotiation);
		}

		TriggerHelper.ForAllTriggersButIAC(t => tsm.Configure(State.BadSubNegotiation).Permit(t, State.BadSubNegotiationEvaluating));
		TriggerHelper.ForAllTriggersButIAC(t => tsm.Configure(State.BadSubNegotiationEvaluating).PermitReentry(t));

		tsm.Configure(State.BadSubNegotiation)
			.Permit(Trigger.IAC, State.BadSubNegotiationEscaping)
			.OnEntry(() => _logger.LogDebug("Connection: {ConnectionState}", $"Unsupported SubNegotiation."));
		tsm.Configure(State.BadSubNegotiationEscaping)
			.Permit(Trigger.IAC, State.BadSubNegotiationEvaluating)
			.Permit(Trigger.SE, State.BadSubNegotiationCompleting);
		tsm.Configure(State.BadSubNegotiationCompleting)
			.OnEntry(() => _logger.LogDebug("Connection: Explicitly ignoring the SubNegotiation that was sent."))
			.SubstateOf(State.Accepting);

		var states = tsm.GetInfo().States.ToImmutableArray();
		var acceptingStateInfo = states.Where(x => (State)x.UnderlyingState == State.Accepting);

		var statesAllowingForErrorTransitions = states
			.Except(acceptingStateInfo);

		foreach(var state in statesAllowingForErrorTransitions)
		{
			tsm.Configure((State)state.UnderlyingState).Permit(Trigger.Error, State.Accepting);
		}

		tsm.OnUnhandledTriggerAsync(async (state, trigger, unmetGuards) =>
		{
			_logger.LogCritical("Bad transition from {@State} with trigger {@Trigger} due to unmet guards: {@UnmetGuards}. Cannot recover. " +
				"Ignoring character and attempting to recover.", state, trigger, unmetGuards);
			await tsm.FireAsync(ParameterizedTrigger(Trigger.Error), Trigger.Error);
		});

		return tsm;
	}
}
