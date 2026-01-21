using Stateless;
using System.Collections.Generic;
using System.Linq;
using System;
using System.Buffers;
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
	/// Cached array of all trigger values to avoid repeated allocations.
	/// </summary>
	private static readonly Trigger[] s_allTriggers = TriggerExtensions.AllValues.ToArray();
	/// <summary>
	/// Create a byte[] that is safe to send over telnet by repeating 255s. 
	/// </summary>
	/// <param name="str">The string intent to be sent across the wire.</param>
	/// <returns>The new byte[] with 255s duplicated.</returns>
	public byte[] TelnetSafeString(string str)
	{
		var byteSpan = CurrentEncoding.GetBytes(str).AsSpan();
		return TelnetSafeBytesInternal(byteSpan);
	}

	/// <summary>
	/// Create a byte[] that is safe to send over telnet by repeating 255s. 
	/// Only use this function if you do not intend to send any kind of negotiation.
	/// </summary>
	/// <param name="str">The original bytes intent to be sent.</param>
	/// <returns>The new byte[] with 255s duplicated.</returns>
	public byte[] TelnetSafeBytes(byte[] str)
	{
		return TelnetSafeBytesInternal(str.AsSpan());
	}

	/// <summary>
	/// Internal helper to escape IAC bytes (255) without MemoryStream allocation.
	/// </summary>
	private byte[] TelnetSafeBytesInternal(ReadOnlySpan<byte> input)
	{
		// Count how many 255s we have to determine final size
#if NET5_0_OR_GREATER
		// Use MemoryExtensions.Count for optimized SIMD counting (.NET 5+)
		int count255 = input.Count((byte)255);
#else
		// Fallback to manual counting for .NET Core 3.1 and earlier
		int count255 = 0;
		foreach (var bt in input)
		{
			if (bt == 255) count255++;
		}
#endif

		// If no 255s, return a copy of the input
		if (count255 == 0)
		{
			return input.ToArray();
		}

		// Allocate the exact size needed
		var result = new byte[input.Length + count255];
		int writePos = 0;

		// Copy bytes, duplicating 255s
		foreach (var bt in input)
		{
			if (bt == 255)
			{
				result[writePos++] = 255;
				result[writePos++] = 255;
			}
			else
			{
				result[writePos++] = bt;
			}
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
	internal void ApplySafetyConfiguration()
	{
		SetupSafeNegotiation(TelnetStateMachine);
	}

	private StateMachine<State, Trigger> SetupSafeNegotiation(StateMachine<State, Trigger> tsm)
	{
		var info = tsm.GetInfo();
		// Use cached static array instead of generating new one each time
		var triggers = s_allTriggers;
		var refuseThese = new List<State> { State.Willing, State.Refusing, State.Do, State.Dont };

		foreach (var stateInfo in info.States.Join(refuseThese, x => x.UnderlyingState, y => y, (x, y) => x))
		{
			var state = (State)stateInfo.UnderlyingState;
			// Use HashSet for O(1) lookups instead of O(n) with Except
			var handledTriggers = new HashSet<Trigger>(
				stateInfo.Transitions.Select(x => (Trigger)x.Trigger.UnderlyingTrigger));
			var outboundUnhandledTriggers = triggers.Where(t => !handledTriggers.Contains(t));

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

		var underlyingTriggers = new HashSet<Trigger>(
			info.States.First(x => (State)x.UnderlyingState == State.SubNegotiation).Transitions
				.Select(x => (Trigger)x.Trigger.UnderlyingTrigger));

		foreach(var trigger in triggers.Where(t => !underlyingTriggers.Contains(t)))
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
