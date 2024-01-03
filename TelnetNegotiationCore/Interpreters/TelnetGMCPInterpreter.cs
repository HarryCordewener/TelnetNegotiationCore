using Stateless;
using System.Text;
using System.Threading.Tasks;
using System;
using TelnetNegotiationCore.Models;
using System.Collections.Generic;
using OneOf;
using MoreLinq;
using System.Linq;

namespace TelnetNegotiationCore.Interpreters
{
	public partial class TelnetInterpreter
	{
		private List<byte> _gmcpBytes = new();

		public Func<(string Package, byte[] Info), Encoding, Task> CallbackOnGMCP { get; init; }

		private StateMachine<State, Trigger> SetupGMCPNegotiation(StateMachine<State, Trigger> tsm)
		{
			if (Mode == TelnetMode.Server)
			{
				tsm.Configure(State.Do)
					.Permit(Trigger.GMCP, State.DoGMCP);

				tsm.Configure(State.Dont)
					.Permit(Trigger.GMCP, State.DontGMCP);

				tsm.Configure(State.DoGMCP)
					.SubstateOf(State.Accepting)
					.OnEntry(() => _Logger.Debug("Connection: {connectionStatus}", "Client will do GMCP"));

				tsm.Configure(State.DontGMCP)
					.SubstateOf(State.Accepting)
					.OnEntry(() => _Logger.Debug("Connection: {connectionStatus}", "Client will not GMCP"));
			}
			else if (Mode == TelnetMode.Client)
			{
				tsm.Configure(State.Willing)
					.Permit(Trigger.GMCP, State.WillGMCP);

				tsm.Configure(State.Refusing)
					.Permit(Trigger.GMCP, State.WontGMCP);

				tsm.Configure(State.WillGMCP)
					.SubstateOf(State.Accepting)
					.OnEntry(() => _Logger.Debug("Connection: {connectionStatus}", "Server will do GMCP"));

				tsm.Configure(State.WontGMCP)
					.SubstateOf(State.Accepting)
					.OnEntry(() => _Logger.Debug("Connection: {connectionStatus}", "Client will GMCP"));
			}

			tsm.Configure(State.SubNegotiation)
				.Permit(Trigger.GMCP, State.AlmostNegotiatingGMCP)
				.OnEntry(() => _gmcpBytes.Clear());

			TriggerHelper.ForAllTriggersButIAC(t => tsm
					.Configure(State.EvaluatingGMCPValue)
					.PermitReentry(t)
					.OnEntryFrom(ParametarizedTrigger(t), RegisterGMCPValue));

			TriggerHelper.ForAllTriggersButIAC(t => tsm
					.Configure(State.AlmostNegotiatingGMCP)
					.Permit(t, State.EvaluatingGMCPValue)
					.OnEntryFrom(ParametarizedTrigger(t), RegisterGMCPValue));

			tsm.Configure(State.EvaluatingGMCPValue)
				.Permit(Trigger.IAC, State.EscapingGMCPValue);

			tsm.Configure(State.AlmostNegotiatingGMCP)
				.Permit(Trigger.IAC, State.EscapingGMCPValue);

			tsm.Configure(State.EscapingGMCPValue)
				.Permit(Trigger.IAC, State.EvaluatingGMCPValue)
				.Permit(Trigger.SE, State.CompletingGMCPValue);

			tsm.Configure(State.CompletingGMCPValue)
				.SubstateOf(State.Accepting)
				.OnEntryAsync(CompleteGMCPNegotiation);

			RegisterInitialWilling(async () => await WillGMCPAsync(null));

			return tsm;
		}

		/// <summary>
		/// Adds a byte to the register.
		/// </summary>
		/// <param name="b">Byte.</param>
		private void RegisterGMCPValue(OneOf<byte, Trigger> b)
		{
			_gmcpBytes.Add(b.AsT0);
		}

		public Task SendGMCPCommand(string package, string command) =>
			SendGMCPCommand(CurrentEncoding.GetBytes(package), CurrentEncoding.GetBytes(command));

		public Task SendGMCPCommand(string package, byte[] command) =>
			SendGMCPCommand(CurrentEncoding.GetBytes(package), command);

		public async Task SendGMCPCommand(byte[] package, byte[] command)
		{
			await CallbackNegotiation(
				new byte[] { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.GMCP }
				.Concat(package)
				.Concat(CurrentEncoding.GetBytes(" "))
				.Concat(command)
				.Concat(new byte[] { (byte)Trigger.IAC, (byte)Trigger.SE }).ToArray());
		}

		/// <summary>
		/// Completes the GMCP Negotiation. This is currently assuming a golden path.
		/// </summary>
		/// <param name="_">Transition, ignored.</param>
		/// <returns>Task</returns>
		private async Task CompleteGMCPNegotiation(StateMachine<State, Trigger>.Transition _)
		{
			var space = CurrentEncoding.GetBytes(" ").First();
			var firstSpace = _gmcpBytes.FindIndex(x => x == space);
			var packageBytes = _gmcpBytes.Skip(1).Take(firstSpace - 1).ToArray();
			var rest = _gmcpBytes.Skip(firstSpace + 1).ToArray();
			await CallbackOnGMCP((Package: CurrentEncoding.GetString(packageBytes), Info: rest), CurrentEncoding);
		}

		/// <summary>
		/// Announces the Server will GMCP.
		/// </summary>
		/// <param name="_">Transition, ignored.</param>
		/// <returns>Task</returns>
		private async Task WillGMCPAsync(StateMachine<State, Trigger>.Transition _)
		{
			_Logger.Debug("Connection: {connectionStatus}", "Announcing the server will GMCP");

			await CallbackNegotiation(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.GMCP });
		}
	}
}
