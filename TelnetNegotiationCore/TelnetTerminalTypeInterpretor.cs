using Stateless;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace TelnetNegotiationCore
{
	public partial class TelnetInterpretor
	{
		/// <summary>
		/// A list of terminal types for this connection.
		/// </summary>
		public List<string> TerminalTypes { get; private set; } = new List<string>();

		/// <summary>
		/// The current selected Terminal Type. Use RequestTerminalTypeAsync if you want the client to switch to another mode.
		/// </summary>
		public string CurrentTerminalType => _CurrentTerminalType == -1 ? "unknown" : TerminalTypes[_CurrentTerminalType];

		/// <summary>
		/// Currently selected Terminal Type index.
		/// </summary>
		private int _CurrentTerminalType = -1;

		/// <summary>
		/// Internal Terminal Type Byte State
		/// </summary>
		private byte[] _ttypeByteState;

		/// <summary>
		/// Internal Terminal Type Byte Index
		/// </summary>
		private int _ttypeIndex = 0;

		/// <summary>
		/// Support for Client & Server Terminal Type negotiation
		/// RFC 1091
		/// </summary>
		/// <param name="tsm">The state machine</param>
		private void SetupTelnetTerminalType(StateMachine<State, Trigger> tsm)
		{
			tsm.Configure(State.Willing)
				.Permit(Trigger.TTYPE, State.WillDoTType);

			tsm.Configure(State.Refusing)
				.Permit(Trigger.TTYPE, State.WontDoTType);

			tsm.Configure(State.Do)
				.Permit(Trigger.TTYPE, State.DoTType);

			tsm.Configure(State.Dont)
				.Permit(Trigger.TTYPE, State.DontTType);

			tsm.Configure(State.WillDoTType)
				.SubstateOf(State.Accepting)
				.OnEntryAsync(RequestTerminalTypeAsync);

			tsm.Configure(State.WontDoTType)
				.SubstateOf(State.Accepting)
				.OnEntry(() => _Logger.Debug("Connection: {connectionStatus}", "Client won't do Terminal Type - do nothing"));

			tsm.Configure(State.DoTType)
				.SubstateOf(State.Accepting)
				.OnEntryAsync(WillDoTerminalTypeAsync);

			tsm.Configure(State.DontTType)
				.SubstateOf(State.Accepting)
				.OnEntry(() => _Logger.Debug("Connection: {connectionStatus}", "Client telling us not to Terminal Type - do nothing"));

			tsm.Configure(State.SubNegotiation)
				.Permit(Trigger.TTYPE, State.AlmostNegotiatingTerminalType);

			tsm.Configure(State.AlmostNegotiatingTerminalType)
				.Permit(Trigger.IS, State.NegotiatingTerminalType);

			tsm.Configure(State.NegotiatingTerminalType)
				.Permit(Trigger.IAC, State.EscapingTerminalTypeValue)
				.OnEntry(GetTerminalType);

			TriggerHelper.ForAllTriggersButIAC(t => tsm.Configure(State.NegotiatingTerminalType).Permit(t, State.EvaluatingTerminalType));
			TriggerHelper.ForAllTriggers(t => tsm.Configure(State.EvaluatingTerminalType).OnEntryFrom(BTrigger(t), CaptureTerminalType));
			TriggerHelper.ForAllTriggersButIAC(t => tsm.Configure(State.EvaluatingTerminalType).PermitReentry(t));

			tsm.Configure(State.EvaluatingTerminalType)
				.Permit(Trigger.IAC, State.EscapingTerminalTypeValue);

			tsm.Configure(State.EscapingTerminalTypeValue)
				.Permit(Trigger.IAC, State.EvaluatingTerminalType)
				.Permit(Trigger.SE, State.CompletingTerminalType);

			tsm.Configure(State.CompletingTerminalType)
				.OnEntryAsync(CompleteTerminalTypeAsync)
				.SubstateOf(State.Accepting);

			RegisterInitialWilling(SendDoTerminalTypeAsync);
		}

		/// <summary>
		/// Initialize internal state values for Terminal Type.
		/// </summary>
		/// <param name="_">Ignored</param>
		private void GetTerminalType()
		{
			_ttypeByteState = new byte[1024];
			_ttypeIndex = 0;
		}

		/// <summary>
		/// Capture a byte and write it into the Terminal Type buffer
		/// </summary>
		/// <param name="b">The current byte</param>
		private void CaptureTerminalType(byte b)
		{
			_ttypeByteState[_ttypeIndex] = b;
			_ttypeIndex++;
		}

		/// <summary>
		/// Read the Terminal Type state values and finalize it into the Terminal Types List.
		/// Then, if we have not seen this Terminal Type before, request another!
		/// </summary>
		/// <param name="_">Ignored</param>
		private async Task CompleteTerminalTypeAsync()
		{
			var ttype = ascii.GetString(_ttypeByteState, 0, _ttypeIndex);
			if (TerminalTypes.Contains(ttype))
			{
				_Logger.Debug("Connection: {connectionStatus}: {@ttypes}", "Completing Terminal Type negotiation. List as follows", TerminalTypes);
				_CurrentTerminalType = (_CurrentTerminalType + 1) % TerminalTypes.Count;
			}
			else
			{
				_Logger.Debug("Connection: {connectionStatus}: {ttype}", "Registering Terminal Type. Requesting the next", ttype);
				TerminalTypes.Add(ttype);
				_CurrentTerminalType++;
				await RequestTerminalTypeAsync();
			}
		}

		/// <summary>
		/// Tell the Client that the Server is willing to listen to Terminal Type.
		/// </summary>
		/// <returns></returns>
		private async Task WillDoTerminalTypeAsync()
		{
			_Logger.Debug("Connection: {connectionStatus}", "Telling the Client, Willing to do Terminal Type.");
			await _OutputStream.BaseStream.WriteAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.TTYPE });
		}

		/// <summary>
		/// Tell the Client to do Terminal Type. This should not happen as a Server.
		/// </summary>
		private async Task SendDoTerminalTypeAsync()
		{
			_Logger.Debug("Connection: {connectionStatus}", "Telling the Client, to do Terminal Type.");
			await _OutputStream.BaseStream.WriteAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.TTYPE });
		}

		/// <summary>
		/// Request Terminal Type from Client.
		/// </summary>
		public async Task RequestTerminalTypeAsync()
		{
			_Logger.Debug("Connection: {connectionStatus}", "Telling the Client, to send Terminal Type.");
			await _OutputStream.BaseStream.WriteAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TTYPE, (byte)Trigger.SEND, (byte)Trigger.IAC, (byte)Trigger.SE });
		}
	}
}
