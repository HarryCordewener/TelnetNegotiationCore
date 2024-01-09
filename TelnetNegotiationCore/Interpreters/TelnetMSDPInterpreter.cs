using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
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
					.OnEntry(() => _Logger.LogDebug("Connection: {ConnectionState}", "Client won't do MSDP - do nothing"));

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
					.OnEntry(() => _Logger.LogDebug("Connection: {ConnectionState}", "Server won't do MSDP - do nothing"));

				tsm.Configure(State.SubNegotiation)
					.Permit(Trigger.MSDP, State.AlmostNegotiatingMSDP)
					.OnEntry(() => _currentMSDPInfo = []);

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
					.OnEntry(ReadMSDPValues);

				TriggerHelper.ForAllTriggersButIAC(t =>
					tsm.Configure(State.EvaluatingMSDP).OnEntryFrom(ParameterizedTrigger(t), CaptureMSDPValue).PermitReentry(t));
			}

			return tsm;
		}

		private void CaptureMSDPValue(OneOf<byte, Trigger> b) => _currentMSDPInfo.Add(b.AsT0);

		private void ReadMSDPValues() => Functional.MSDPLibrary.MSDPScan(_currentMSDPInfo.Skip(1), CurrentEncoding);

		/// <summary>
		/// Announce we do MSDP negotiation to the client.
		/// </summary>
		private async Task WillingMSDPAsync()
		{
			_Logger.LogDebug("Connection: {ConnectionState}", "Announcing willingness to MSDP!");
			await CallbackNegotiationAsync([(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.MSDP]);
		}

		/// <summary>
		/// Announce the MSDP we support to the client after getting a Do.
		/// </summary>
		private Task OnDoMSDPAsync(StateMachine<State, Trigger>.Transition _)
		{
			_Logger.LogDebug("Connection: {ConnectionState}", "Client will do MSDP output");
			return Task.CompletedTask;
		}

		/// <summary>
		/// Announce we do MSDP negotiation to the server.
		/// </summary>
		private async Task OnWillMSDPAsync()
		{
			_Logger.LogDebug("Connection: {ConnectionState}", "Announcing willingness to MSDP!");
			await CallbackNegotiationAsync([(byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MSDP]);
		}
	}
}
