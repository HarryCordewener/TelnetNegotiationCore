using System;
using Stateless;

namespace TelnetNegotiationCore
{
	public partial class TelnetInterpretor
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
		/// If the server you are connected to makes use of the client window in ways that are linked to its width and height, 
		/// then is useful for it to be able to find out how big it is, and also to get notified when it is resized.
		/// 
		/// NAWS can be initiated from the Client or the Server.
		/// </summary>
		/// <param name="tsm">The state machine.</param>
		private void SetupNAWS(StateMachine<State, Trigger> tsm)
		{
			tsm.Configure(State.Willing)
				.Permit(Trigger.NAWS, State.WillDoNAWS);

			tsm.Configure(State.Refusing)
				.Permit(Trigger.NAWS, State.WontDoNAWS);

			tsm.Configure(State.Dont)
				.Permit(Trigger.NAWS, State.DontNAWS);
			
			tsm.Configure(State.DontNAWS)
				.SubstateOf(State.Accepting)
				.OnEntry(() => Console.WriteLine("Client won't do NAWS - do nothing"));

			tsm.Configure(State.WillDoNAWS)
				.SubstateOf(State.Accepting)
				.OnEntry(RequestNAWS);

			tsm.Configure(State.WontDoNAWS)
				.SubstateOf(State.Accepting);

			tsm.Configure(State.SubNegotiation)
				.Permit(Trigger.NAWS, State.NegotiatingNAWS);

			tsm.Configure(State.NegotiatingNAWS)
				.Permit(Trigger.IAC, State.EscapingNAWSValue)
				.OnEntry(GetNAWS);

			TriggerHelper.ForAllTriggersButIAC(t => tsm.Configure(State.NegotiatingNAWS).Permit(t, State.EvaluatingNAWS));

			tsm.Configure(State.EvaluatingNAWS)
				.PermitDynamic(Trigger.IAC, () => _nawsIndex < 4 ? State.EscapingNAWSValue : State.CompletingNAWS);

			// Source of Error: This gets triggered because CompletingNAWS is a substate of EvaluatingNAWS
			// Either CompletingNAWS needs to be turned into a different substate, or the Dynamic method above needs to change, 
			// thus removing the need for the Substate.
			// Likely the latter.
			TriggerHelper.ForAllTriggers(t => tsm.Configure(State.EvaluatingNAWS).OnEntryFrom(BTrigger(t), CaptureNAWS));

			TriggerHelper.ForAllTriggersButIAC(t => tsm.Configure(State.EvaluatingNAWS).PermitReentry(t));

			tsm.Configure(State.EscapingNAWSValue)
				.Permit(Trigger.IAC, State.EvaluatingNAWS);

			tsm.Configure(State.CompletingNAWS)
				.SubstateOf(State.EndSubNegotiation)
				.OnEntry(CompleteNAWS);
		}

		/// <summary>
		/// Request NAWS from a client.
		/// </summary>
		public void RequestNAWS(StateMachine<State, Trigger>.Transition _)
		{
			Console.WriteLine("Requesting NAWS details from Client");
			_OutputStream.BaseStream.Write(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.NAWS });
		}

		/// <summary>
		/// Capture a byte and write it into the NAWS buffer
		/// </summary>
		/// <param name="b">The current byte</param>
		private void CaptureNAWS(byte b)
		{
			_nawsByteState[_nawsIndex] = b;
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

		private void WillingNAWS()
		{
			Console.WriteLine("Announcing willingness to NAWS!");
			_OutputStream.BaseStream.Write(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.NAWS });
		}

		/// <summary>
		/// Read the NAWS state values and finalize it into width and height values.
		/// </summary>
		/// <param name="_">Ignored</param>
		private void CompleteNAWS(StateMachine<State, Trigger>.Transition _)
		{
			byte[] width = new byte[] { _nawsByteState[0], _nawsByteState[1] };
			byte[] height = new byte[] { _nawsByteState[2], _nawsByteState[3] };

			if (BitConverter.IsLittleEndian)
			{
				Array.Reverse(width);
				Array.Reverse(height);
			}

			ClientWidth = BitConverter.ToInt16(width);
			ClientHeight = BitConverter.ToInt16(height);

			Console.WriteLine($"Negotiated for: {ClientWidth} width and {ClientHeight} height");
		}
	}
}
