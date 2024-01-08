using System;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
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
		/// <summary>
		/// Internal NAWS Byte State
		/// </summary>
		private byte[] _nawsByteState;

		/// <summary>
		/// Internal NAWS Byte Index Value
		/// </summary>
		private int _nawsIndex = 0;

		/// <summary>
		/// Currently known Client Height
		/// </summary>
		/// <remarks>
		/// Defaults to 24
		/// </remarks>
		public int ClientHeight { get; private set; } = 24;

		/// <summary>
		/// Currently known Client Width.
		/// </summary>
		/// <remarks>
		/// Defaults to 78
		/// </remarks>
		public int ClientWidth { get; private set; } = 78;

		/// <summary>
		/// NAWS Callback function to alert server of Width & Height negotiation
		/// </summary>
		public Func<int, int, Task> SignalOnNAWSAsync { get; init; }

		/// <summary>
		/// This exists to avoid an infinite loop with badly conforming clients.
		/// </summary>
		private bool _WillingToDoNAWS = false;

		/// <summary>
		/// If the server you are connected to makes use of the client window in ways that are linked to its width and height, 
		/// then is useful for it to be able to find out how big it is, and also to get notified when it is resized.
		/// 
		/// NAWS can be initiated from the Client or the Server.
		/// </summary>
		/// <param name="tsm">The state machine.</param>
		/// <returns>Itself</returns>
		private StateMachine<State, Trigger> SetupNAWS(StateMachine<State, Trigger> tsm)
		{
			tsm.Configure(State.Willing)
				.Permit(Trigger.NAWS, State.WillDoNAWS);

			tsm.Configure(State.Refusing)
				.Permit(Trigger.NAWS, State.WontDoNAWS);

			tsm.Configure(State.Dont)
				.Permit(Trigger.NAWS, State.DontNAWS);

			tsm.Configure(State.Do)
				.Permit(Trigger.NAWS, State.DoNAWS);

			if (Mode == TelnetMode.Server)
			{
				tsm.Configure(State.DontNAWS)
					.SubstateOf(State.Accepting)
					.OnEntry(() => _Logger.Debug("Connection: {ConnectionState}", "Client won't do NAWS - do nothing"));

				tsm.Configure(State.DoNAWS)
					.SubstateOf(State.Accepting)
					.OnEntryAsync(ServerWontNAWSAsync);
			}

			if (Mode == TelnetMode.Client)
			{
				tsm.Configure(State.DontNAWS)
					.SubstateOf(State.Accepting)
					.OnEntry(() => _Logger.Debug("Connection: {ConnectionState}", "Server won't do NAWS - do nothing"));

				tsm.Configure(State.DoNAWS)
					.SubstateOf(State.Accepting)
					.OnEntry(() => _WillingToDoNAWS = true);
			}

			tsm.Configure(State.WillDoNAWS)
				.SubstateOf(State.Accepting)
				.OnEntryAsync(RequestNAWSAsync);

			tsm.Configure(State.WontDoNAWS)
				.SubstateOf(State.Accepting)
				.OnEntry(() => _WillingToDoNAWS = false);

			tsm.Configure(State.SubNegotiation)
				.Permit(Trigger.NAWS, State.NegotiatingNAWS);

			tsm.Configure(State.NegotiatingNAWS)
				.Permit(Trigger.IAC, State.EscapingNAWSValue)
				.OnEntry(GetNAWS);

			TriggerHelper.ForAllTriggersButIAC(t => tsm.Configure(State.NegotiatingNAWS).Permit(t, State.EvaluatingNAWS));

			tsm.Configure(State.EvaluatingNAWS)
				.PermitDynamic(Trigger.IAC, () => _nawsIndex < 4 ? State.EscapingNAWSValue : State.CompletingNAWS);

			TriggerHelper.ForAllTriggers(t => tsm.Configure(State.EvaluatingNAWS).OnEntryFrom(ParameterizedTrigger(t), CaptureNAWS));

			TriggerHelper.ForAllTriggersButIAC(t => tsm.Configure(State.EvaluatingNAWS).PermitReentry(t));

			tsm.Configure(State.EscapingNAWSValue)
				.Permit(Trigger.IAC, State.EvaluatingNAWS);

			tsm.Configure(State.CompletingNAWS)
				.SubstateOf(State.EndSubNegotiation)
				.OnEntryAsync(CompleteNAWSAsync);

			RegisterInitialWilling(async () => await RequestNAWSAsync(null));

			return tsm;
		}

		public async Task SendNAWS(short width, short height)
		{
			if(!_WillingToDoNAWS) await Task.CompletedTask;
			
			await CallbackNegotiationAsync([(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.NAWS, 
				.. BitConverter.GetBytes(width), .. BitConverter.GetBytes(height), 
				(byte)Trigger.IAC, (byte)Trigger.SE]);
		}

		/// <summary>
		/// Request NAWS from a client
		/// </summary>
		public async Task RequestNAWSAsync(StateMachine<State, Trigger>.Transition _)
		{
			if (!_WillingToDoNAWS)
			{
				_Logger.Debug("Connection: {ConnectionState}", "Requesting NAWS details from Client");

				await CallbackNegotiationAsync([(byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.NAWS]);
				_WillingToDoNAWS = true;
			}
		}

		/// <summary>
		/// Capture a byte and write it into the NAWS buffer
		/// </summary>
		/// <param name="b">The current byte</param>
		private void CaptureNAWS(OneOf<byte, Trigger> b)
		{
			if (_nawsIndex > _nawsByteState.Length) return;
			_nawsByteState[_nawsIndex] = b.AsT0;
			_nawsIndex++;
		}

		/// <summary>
		/// Initialize internal state values for NAWS.
		/// </summary>
		/// <param name="_">Ignored</param>
		private void GetNAWS(StateMachine<State, Trigger>.Transition _)
		{
			_nawsByteState = new byte[4];
			_nawsIndex = 0;
		}

		private async Task ServerWontNAWSAsync()
		{
			_Logger.Debug("Connection: {ConnectionState}", "Announcing refusing to send NAWS, this is a Server!");
			await CallbackNegotiationAsync([(byte)Trigger.IAC, (byte)Trigger.WONT, (byte)Trigger.NAWS]);
		}

		/// <summary>
		/// Read the NAWS state values and finalize it into width and height values.
		/// </summary>
		/// <param name="_">Ignored</param>
		private async Task CompleteNAWSAsync(StateMachine<State, Trigger>.Transition _)
		{
			byte[] width = [_nawsByteState[0], _nawsByteState[1]];
			byte[] height = [_nawsByteState[2], _nawsByteState[3]];

			if (BitConverter.IsLittleEndian)
			{
				Array.Reverse(width);
				Array.Reverse(height);
			}

			ClientWidth = BitConverter.ToInt16(width);
			ClientHeight = BitConverter.ToInt16(height);

			_Logger.Debug("Negotiated for: {clientWidth} width and {clientHeight} height", ClientWidth, ClientHeight);
			await (SignalOnNAWSAsync?.Invoke(ClientHeight, ClientWidth) ?? Task.CompletedTask);
		}
	}
}
