using OneOf;
using Stateless;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using TelnetNegotiationCore.Models;

namespace TelnetNegotiationCore.Interpreters
{
	/// <summary>
	/// Implements RFC 1091 and MTTS
	/// https://datatracker.ietf.org/doc/html/rfc1091
	/// https://tintin.mudhalla.net/protocols/mtts/
	/// 
	/// TODO: Allow the end-user to set TerminalTypes in Client Mode.
	/// TODO: Optimize byte array allocations that get commonly used.
	/// </summary>
	public partial class TelnetInterpreter
	{
		/// <summary>
		/// A list of terminal types for this connection.
		/// </summary>
		public ImmutableList<string> TerminalTypes { get; private set; } = [];

		/// <summary>
		/// The current selected Terminal Type. Use RequestTerminalTypeAsync if you want the client to switch to the next mode.
		/// </summary>
		public string CurrentTerminalType => _CurrentTerminalType == -1 ? "unknown" : TerminalTypes[Math.Min(_CurrentTerminalType, TerminalTypes.Count - 1)];

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
		/// A dictionary for MTTS support.
		/// </summary>
		private readonly Dictionary<int, string> _MTTS = new()
		{
			{1, "ANSI"},
			{2, "VT100"},
			{4, "UTF8"},
			{8, "256 COLORS"},
			{16, "MOUSE_TRACKING"},
			{32, "OSC_COLOR_PALETTE"},
			{64, "SCREEN_READER"},
			{128, "PROXY"},
			{256, "TRUECOLOR"},
			{512, "MNES"},
			{1024, "MSLP"}
		};

		/// <summary>
		/// Support for Client & Server Terminal Type negotiation
		/// RFC 1091
		/// </summary>
		/// <param name="tsm">The state machine.</param>
		/// <returns>Itself</returns>
		private StateMachine<State, Trigger> SetupTelnetTerminalType(StateMachine<State, Trigger> tsm)
		{
			tsm.Configure(State.Willing)
				.Permit(Trigger.TTYPE, State.WillDoTType);

			tsm.Configure(State.Refusing)
				.Permit(Trigger.TTYPE, State.WontDoTType);

			tsm.Configure(State.Do)
				.Permit(Trigger.TTYPE, State.DoTType);

			tsm.Configure(State.Dont)
				.Permit(Trigger.TTYPE, State.DontTType);

			return Mode switch
			{
				TelnetMode.Server => SetupTelnetTerminalTypeAsServer(tsm),
				TelnetMode.Client => SetupTelnetTerminalTypeAsClient(tsm),
				_ => throw new NotImplementedException()
			};
		}

		/// <summary>
		/// Sets up the Telnet Terminal Type negotiation in Client Mode.
		/// As the Client, we respond to DO and DONT, but not to WILL and WONT.
		/// </summary>
		/// <param name="tsm">The state machine.</param>
		/// <returns>Itself</returns>
		private StateMachine<State, Trigger> SetupTelnetTerminalTypeAsClient(StateMachine<State, Trigger> tsm)
		{
			_CurrentTerminalType = -1;
			TerminalTypes = TerminalTypes.AddRange([ "TNC", "XTERM", "MTTS 3853" ]);

			tsm.Configure(State.DoTType)
				.SubstateOf(State.Accepting)
				.OnEntryAsync(WillDoTerminalTypeAsync);

			tsm.Configure(State.DontTType)
				.SubstateOf(State.Accepting)
				.OnEntry(() => _Logger.Debug("Connection: {ConnectionState}", "Server telling us not to Terminal Type"));

			tsm.Configure(State.SubNegotiation)
				.Permit(Trigger.TTYPE, State.AlmostNegotiatingTerminalType);

			tsm.Configure(State.AlmostNegotiatingTerminalType)
				.Permit(Trigger.SEND, State.NegotiatingTerminalType);

			tsm.Configure(State.NegotiatingTerminalType)
				.Permit(Trigger.IAC, State.CompletingTerminalType)
				.OnEntry(GetTerminalType);

			tsm.Configure(State.CompletingTerminalType)
				.OnEntryAsync(ReportNextAvailableTerminalTypeAsync)
				.Permit(Trigger.SE, State.Accepting);

			return tsm;
		}

		/// <summary>
		/// Sets up the Telnet Terminal Type negotiation in Server Mode.
		/// As the server, we respond to WILL and WONT, but not to DO and DONT.
		/// We initiate the request for Telnet Negotiation.
		/// </summary>
		/// <param name="tsm">The state machine.</param>
		/// <returns>Itself</returns>
		private StateMachine<State, Trigger> SetupTelnetTerminalTypeAsServer(StateMachine<State, Trigger> tsm)
		{
			tsm.Configure(State.WillDoTType)
				.SubstateOf(State.Accepting)
				.OnEntryAsync(RequestTerminalTypeAsync);

			tsm.Configure(State.WontDoTType)
				.SubstateOf(State.Accepting)
				.OnEntry(() => _Logger.Debug("Connection: {ConnectionState}", "Client won't do Terminal Type"));

			tsm.Configure(State.SubNegotiation)
				.Permit(Trigger.TTYPE, State.AlmostNegotiatingTerminalType);

			tsm.Configure(State.AlmostNegotiatingTerminalType)
				.Permit(Trigger.IS, State.NegotiatingTerminalType);

			tsm.Configure(State.NegotiatingTerminalType)
				.Permit(Trigger.IAC, State.EscapingTerminalTypeValue)
				.OnEntry(GetTerminalType);

			TriggerHelper.ForAllTriggersButIAC(t => tsm.Configure(State.NegotiatingTerminalType).Permit(t, State.EvaluatingTerminalType));
			TriggerHelper.ForAllTriggers(t => tsm.Configure(State.EvaluatingTerminalType).OnEntryFrom(ParameterizedTrigger(t), CaptureTerminalType));
			TriggerHelper.ForAllTriggersButIAC(t => tsm.Configure(State.EvaluatingTerminalType).PermitReentry(t));

			tsm.Configure(State.EvaluatingTerminalType)
				.Permit(Trigger.IAC, State.EscapingTerminalTypeValue);

			tsm.Configure(State.EscapingTerminalTypeValue)
				.Permit(Trigger.IAC, State.EvaluatingTerminalType)
				.Permit(Trigger.SE, State.CompletingTerminalType);

			tsm.Configure(State.CompletingTerminalType)
				.OnEntryAsync(CompleteTerminalTypeAsServerAsync)
				.SubstateOf(State.Accepting);

			RegisterInitialWilling(SendDoTerminalTypeAsync);

			return tsm;
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
		private void CaptureTerminalType(OneOf<byte, Trigger> b)
		{
			if (_ttypeIndex > _ttypeByteState.Length) return;
			_ttypeByteState[_ttypeIndex] = b.AsT0;
			_ttypeIndex++;
		}

		/// <summary>
		/// Read the Terminal Type state values and finalize it into the Terminal Types List.
		/// Then, if we have not seen this Terminal Type before, request another!
		/// </summary>
		private async Task CompleteTerminalTypeAsServerAsync()
		{
			var TType = ascii.GetString(_ttypeByteState, 0, _ttypeIndex);
			if (TerminalTypes.Contains(TType))
			{
				_CurrentTerminalType = (_CurrentTerminalType + 1) % TerminalTypes.Count;
				var MTTS = TerminalTypes.FirstOrDefault(x => x.StartsWith("MTTS"));
				if (MTTS != default)
				{
					var mttsVal = int.Parse(MTTS.Remove(0, 5));

					TerminalTypes = TerminalTypes.AddRange(_MTTS.Where(x => (mttsVal & x.Key) != 0).Select(x => x.Value));
				}

				TerminalTypes = TerminalTypes.Remove(MTTS);
				_Logger.Debug("Connection: {ConnectionState}: {@TerminalTypes}", "Completing Terminal Type negotiation. List as follows", TerminalTypes);
			}
			else
			{
				_Logger.Verbose("Connection: {ConnectionState}: {TerminalType}", "Registering Terminal Type. Requesting the next", TType);
				TerminalTypes = TerminalTypes.Add(TType);
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
			_Logger.Debug("Connection: {ConnectionState}", "Telling the other party, Willing to do Terminal Type.");
			await CallbackNegotiationAsync([(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.TTYPE]);
		}

		/// <summary>
		/// Tell the Client to do Terminal Type. This should not happen as a Server.
		/// </summary>
		private async Task SendDoTerminalTypeAsync()
		{
			_Logger.Debug("Connection: {ConnectionState}", "Telling the other party, to do Terminal Type.");
			await CallbackNegotiationAsync([(byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.TTYPE]);
		}

		/// <summary>
		/// Request Terminal Type from Client. This flips to the next one.
		/// </summary>
		public async Task RequestTerminalTypeAsync()
		{
			_Logger.Debug("Connection: {ConnectionState}", "Telling the client, to send the next Terminal Type.");
			await CallbackNegotiationAsync([(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TTYPE, (byte)Trigger.SEND, (byte)Trigger.IAC, (byte)Trigger.SE]);
		}

		private async Task ReportNextAvailableTerminalTypeAsync()
		{
			_CurrentTerminalType = (_CurrentTerminalType + 1) % (TerminalTypes.Count + 1);
			_Logger.Debug("Connection: {ConnectionState}", "Reporting the next Terminal Type to the server.");
			byte[] terminalType = [
				(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TTYPE, (byte)Trigger.IS,
				.. ascii.GetBytes(CurrentTerminalType),
				(byte)Trigger.IAC, (byte)Trigger.SE ];

			await CallbackNegotiationAsync(terminalType);
		}
	}
}