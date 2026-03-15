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
	internal byte[] TelnetSafeString(string str)
	{
		var byteSpan = CurrentEncoding.GetBytes(str).AsSpan();
		return TelnetSafeBytesInternal(byteSpan);
	}

	/// <summary>
	/// Create a byte[] that is safe to send over telnet by repeating 255s. 
	/// This is handled automatically by <see cref="SendAsync"/> and <see cref="SendPromptAsync"/>.
	/// </summary>
	/// <param name="str">The original bytes intent to be sent.</param>
	/// <returns>The new byte[] with 255s duplicated.</returns>
	internal byte[] TelnetSafeBytes(byte[] str)
	{
		return TelnetSafeBytesInternal(str.AsSpan());
	}

	/// <summary>
	/// Internal helper to escape IAC bytes (255) without MemoryStream allocation.
	/// Uses <see cref="MemoryExtensions.IndexOf"/> for an early exit when no IAC bytes are present.
	/// On .NET 9+, <see cref="MemoryExtensions.Split"/> enumerates segments between each IAC byte,
	/// giving us the exact segment count (and therefore the exact output length) so we can allocate
	/// precisely and bulk-copy each segment via <see cref="MemoryExtensions.CopyTo"/> in a second pass.
	/// On earlier runtimes the same <see cref="ArrayPool{T}"/> + <c>IndexOf</c> loop is used instead.
	/// </summary>
	private static byte[] TelnetSafeBytesInternal(ReadOnlySpan<byte> input)
	{
		// Use IndexOf for early exit - stops at first IAC byte without scanning the whole input
		if (input.IndexOf((byte)255) < 0)
		{
			return input.ToArray();
		}

#if NET9_0_OR_GREATER
		// First pass: count segments produced by splitting on 0xFF.
		// N segments means (N-1) IAC bytes, each of which expands to two bytes in the output.
		int segmentCount = 0;
		foreach (var _ in input.Split((byte)255))
		{
			segmentCount++;
		}

		// Allocate exactly the right size: original length + one extra byte per IAC byte
		var result = new byte[input.Length + segmentCount - 1];
		int writePos = 0;
		int segIdx = 0;

		// Second pass: bulk-copy each segment, inserting the doubled IAC between segments
		foreach (var range in input.Split((byte)255))
		{
			if (segIdx++ > 0)
			{
				result[writePos++] = 255;
				result[writePos++] = 255;
			}

			var segment = input[range];
			segment.CopyTo(result.AsSpan(writePos));
			writePos += segment.Length;
		}

		return result;
#else
		// Fallback for netstandard2.1 and .NET 8: ArrayPool worst-case buffer + IndexOf loop
		// with CopyTo block copies. (MemoryExtensions.Split<T>(T) requires .NET 9+.)
		var pooled = ArrayPool<byte>.Shared.Rent(input.Length * 2);
		try
		{
			int writePos = 0;
			var remaining = input;

			while (!remaining.IsEmpty)
			{
				int iacPos = remaining.IndexOf((byte)255);
				if (iacPos < 0)
				{
					remaining.CopyTo(pooled.AsSpan(writePos));
					writePos += remaining.Length;
					break;
				}

				remaining[..iacPos].CopyTo(pooled.AsSpan(writePos));
				writePos += iacPos;

				pooled[writePos++] = 255;
				pooled[writePos++] = 255;

				remaining = remaining[(iacPos + 1)..];
			}

			return pooled[..writePos].ToArray();
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(pooled);
		}
#endif
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
