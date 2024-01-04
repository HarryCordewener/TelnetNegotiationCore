using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Dynamic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using MoreLinq;
using OneOf;
using Stateless;
using TelnetNegotiationCore.Models;

namespace TelnetNegotiationCore.Interpreters
{
	/// <summary>
	/// Implements http://www.faqs.org/rfcs/rfc1073.html
	/// </summary>
	/// <remarks>
	/// TODO: Implement Client Side
	/// </remarks>
	public partial class TelnetInterpreter
	{
		private Func<dynamic> _MSDPConfig;

		private List<byte> _currentMSDPInfo;

		public Func<MSDPConfig, Task> SignalOnMSDPAsync { get; init; }

		/// <summary>
		/// Mud Server Status Protocol will provide information to the requestee about the server's contents.
		/// </summary>
		/// <param name="tsm">The state machine.</param>
		/// <returns>Itself</returns>
		private StateMachine<State, Trigger> SetupMSDPNegotiation(StateMachine<State, Trigger> tsm)
		{
			if (Mode == TelnetMode.Server)
			{
				tsm.Configure(State.Do)
					.Permit(Trigger.MSDP, State.DoMSDP);

				tsm.Configure(State.Dont)
					.Permit(Trigger.MSDP, State.DontMSDP);

				tsm.Configure(State.DoMSDP)
					.SubstateOf(State.Accepting)
					.OnEntryAsync(OnDoMSDPAsync);

				tsm.Configure(State.DontMSDP)
					.SubstateOf(State.Accepting)
					.OnEntry(() => _Logger.Debug("Connection: {ConnectionState}", "Client won't do MSDP - do nothing"));

				RegisterInitialWilling(WillingMSDPAsync);
			}
			else
			{
				tsm.Configure(State.Willing)
					.Permit(Trigger.MSDP, State.WillMSDP);

				tsm.Configure(State.Refusing)
					.Permit(Trigger.MSDP, State.WontMSDP);

				tsm.Configure(State.WillMSDP)
					.SubstateOf(State.Accepting)
					.OnEntryAsync(OnWillMSDPAsync);

				tsm.Configure(State.WontMSDP)
					.SubstateOf(State.Accepting)
					.OnEntry(() => _Logger.Debug("Connection: {ConnectionState}", "Server won't do MSDP - do nothing"));

				tsm.Configure(State.SubNegotiation)
					.Permit(Trigger.MSDP, State.AlmostNegotiatingMSDP)
					.OnEntry(() =>
					{
						_currentMSDPInfo = [];
					});

				tsm.Configure(State.AlmostNegotiatingMSDP)
					.Permit(Trigger.MSDP_VAR, State.EvaluatingMSDP)
					.Permit(Trigger.MSDP_VAL, State.EvaluatingMSDP)
					.Permit(Trigger.MSDP_ARRAY_OPEN, State.EvaluatingMSDP)
					.Permit(Trigger.MSDP_ARRAY_CLOSE, State.EvaluatingMSDP)
					.Permit(Trigger.MSDP_TABLE_OPEN, State.EvaluatingMSDP)
					.Permit(Trigger.MSDP_TABLE_CLOSE, State.EvaluatingMSDP);

				tsm.Configure(State.EvaluatingMSDP)
					.Permit(Trigger.IAC, State.EscapingMSDP);

				tsm.Configure(State.EscapingMSDP)
					.Permit(Trigger.IAC, State.EvaluatingMSDP)
					.Permit(Trigger.SE, State.CompletingMSDP);

				tsm.Configure(State.CompletingMSDP)
					.SubstateOf(State.Accepting)
					.OnEntryAsync(ReadMSDPValues);

				TriggerHelper.ForAllTriggersExcept([Trigger.IAC], t =>
					tsm.Configure(State.EvaluatingMSDP).OnEntryFrom(ParameterizedTrigger(t), CaptureMSDPValue).PermitReentry(t));
			}

			return tsm;
		}

		private void CaptureMSDPValue(OneOf<byte, Trigger> b)
		{
			_currentMSDPInfo.Add(b.AsT0);
		}

		private async Task ReadMSDPValues()
		{
			dynamic root = new ExpandoObject();
			var array = _currentMSDPInfo.Skip(1).ToImmutableArray();

			MSDPScan(root, array, Trigger.MSDP_VAR);
			await Task.CompletedTask;
		}

		private dynamic MSDPScan(dynamic root, ImmutableArray<byte> array, Trigger type)
		{
			if (array.Length == 0) return root;

			if (type == Trigger.MSDP_VAR)
			{
				var variableName = array.TakeUntil(x => x is (byte)Trigger.MSDP_VAL).ToArray();
				((IDictionary<string, object>)root).Add(CurrentEncoding.GetString(variableName),
					MSDPScan(root, array.SkipUntil(x => x is (byte)Trigger.MSDP_VAL).ToImmutableArray(), Trigger.MSDP_VAL));
			}
			else if (type == Trigger.MSDP_VAL)
			{
				var nextType = array.FirstOrDefault(x => x is
					(byte)Trigger.MSDP_ARRAY_OPEN or
					(byte)Trigger.MSDP_TABLE_OPEN or
					(byte)Trigger.MSDP_ARRAY_CLOSE or
					(byte)Trigger.MSDP_TABLE_CLOSE or
					(byte)Trigger.MSDP_VAL);
				dynamic result = root;

				if (nextType == default) // We have hit the end.
				{
					result = CurrentEncoding.GetString(array.ToArray());
				}
				else if (nextType is (byte)Trigger.MSDP_VAL)
				{
					var value = array.TakeWhile(x => x != nextType);
					var startOfRest = array.SkipUntil(x => x is (byte)Trigger.MSDP_VAL).ToImmutableArray();
					result = MSDPScan(((ImmutableList<dynamic>)root).Add(CurrentEncoding.GetString(value.ToArray())), startOfRest, Trigger.MSDP_VAL);
				}
				else if (nextType is (byte)Trigger.MSDP_ARRAY_OPEN)
				{
					var startOfArray = array.SkipUntil(x => x is (byte)Trigger.MSDP_ARRAY_OPEN).ToImmutableArray();
					result = MSDPScan(root, startOfArray, Trigger.MSDP_ARRAY_OPEN);
				}
				else if (nextType is (byte)Trigger.MSDP_TABLE_OPEN)
				{
					var startOfTable = array.SkipUntil(x => x is (byte)Trigger.MSDP_TABLE_OPEN).ToImmutableArray();
					result = MSDPScan(root, startOfTable, Trigger.MSDP_ARRAY_OPEN);
				}
				else if (nextType is (byte)Trigger.MSDP_ARRAY_CLOSE)
				{
					result = root;
				}
				else if (nextType is (byte)Trigger.MSDP_TABLE_CLOSE)
				{
					result = root;
				}

				return result;
			}
			else if (type == Trigger.MSDP_ARRAY_OPEN)
			{
				return MSDPScan(ImmutableList<dynamic>.Empty, array.Skip(1).ToImmutableArray(), Trigger.MSDP_VAL);
			}
			else if (type == Trigger.MSDP_TABLE_OPEN)
			{
				return MSDPScan(new ExpandoObject(), array.Skip(1).ToImmutableArray(), Trigger.MSDP_VAR);
			}

			return root;
		}

		/// <summary>
		/// Announce we do MSDP negotiation to the client.
		/// </summary>
		private async Task WillingMSDPAsync()
		{
			_Logger.Debug("Connection: {ConnectionState}", "Announcing willingness to MSDP!");
			await CallbackNegotiationAsync([(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.MSDP]);
		}

		/// <summary>
		/// Announce the MSDP we support to the client after getting a Do.
		/// </summary>
		private async Task OnDoMSDPAsync(StateMachine<State, Trigger>.Transition _)
		{
			_Logger.Debug("Connection: {ConnectionState}", "Client will do MSDP output");
			await Task.CompletedTask;
		}

		/// <summary>
		/// Announce we do MSDP negotiation to the server.
		/// </summary>
		private async Task OnWillMSDPAsync()
		{
			_Logger.Debug("Connection: {ConnectionState}", "Announcing willingness to MSDP!");
			await CallbackNegotiationAsync([(byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MSDP]);
		}
	}
}
