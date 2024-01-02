using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using MoreLinq;
using OneOf;
using Stateless;
using TelnetNegotiationCore.Models;

namespace TelnetNegotiationCore.Interpretors
{
	public partial class TelnetInterpretor
	{
		private MSSPConfig _msspConfig;

		private List<byte> _currentVariable;
		private List<byte> _currentValue;

		/// <summary>
		/// Mud Server Status Protocol will provide information to the requestee about the server's contents.
		/// </summary>
		/// <param name="tsm">The state machine.</param>
		/// <returns>Itself</returns>
		private StateMachine<State, Trigger> SetupMSSPNegotiation(StateMachine<State, Trigger> tsm)
		{
			if (Mode == TelnetMode.Server)
			{
				tsm.Configure(State.Do)
					.Permit(Trigger.MSSP, State.DoMSSP);

				tsm.Configure(State.Dont)
					.Permit(Trigger.MSSP, State.DontMSSP);

				tsm.Configure(State.DoMSSP)
					.SubstateOf(State.Accepting)
					.OnEntryAsync(OnDoMSSPAsync);

				tsm.Configure(State.DontMSSP)
					.SubstateOf(State.Accepting)
					.OnEntry(() => _Logger.Debug("Connection: {connectionStatus}", "Client won't do MSSP - do nothing"));

				RegisterInitialWilling(WillingMSSPAsync);
			}
			else
			{
				tsm.Configure(State.Willing)
					.Permit(Trigger.MSSP, State.WillMSSP);

				tsm.Configure(State.Refusing)
					.Permit(Trigger.MSSP, State.WontMSSP);

				tsm.Configure(State.WillMSSP)
					.SubstateOf(State.Accepting)
					.OnEntryAsync(OnWillMSSPAsync);

				tsm.Configure(State.WontMSSP)
					.SubstateOf(State.Accepting)
					.OnEntry(() => _Logger.Debug("Connection: {connectionStatus}", "Server won't do MSSP - do nothing"));

				tsm.Configure(State.SubNegotiation)
					.Permit(Trigger.MSSP, State.AlmostNegotiatingMSSP)
					.OnEntry(() => {
						_currentValue = new List<byte>();
						_currentVariable = new List<byte>();
					});

				tsm.Configure(State.AlmostNegotiatingMSSP)
					.Permit(Trigger.MSSP_VAR, State.EvaluatingMSSPVar);

				tsm.Configure(State.EvaluatingMSSPVar)
					.Permit(Trigger.MSSP_VAL, State.EvaluatingMSSPVal)
					.Permit(Trigger.IAC, State.EscapingMSSPVar);

				tsm.Configure(State.EscapingMSSPVar)
					.Permit(Trigger.IAC, State.EvaluatingMSSPVar);

				tsm.Configure(State.EvaluatingMSSPVal)
					.Permit(Trigger.MSSP_VAR, State.EvaluatingMSSPVar)
					.Permit(Trigger.IAC, State.EscapingMSSPVal);

				tsm.Configure(State.EscapingMSSPVal)
					.Permit(Trigger.IAC, State.EvaluatingMSSPVal)
					.Permit(Trigger.SE, State.CompletingMSSP);

				tsm.Configure(State.CompletingMSSP)
					.SubstateOf(State.Accepting)
					.OnEntryAsync(ReadMSSPValues);

				TriggerHelper.ForAllTriggers(t => tsm.Configure(State.EvaluatingMSSPVal).OnEntryFrom(ParametarizedTrigger(t), CaptureValue));
				TriggerHelper.ForAllTriggers(t => tsm.Configure(State.EvaluatingMSSPVar).OnEntryFrom(ParametarizedTrigger(t), CaptureVariable));
			}

			return tsm;
		}

		private async Task ReadMSSPValues()
		{
			var variableList = _currentVariable.Split((byte)Trigger.MSSP_VAR);
			var valueList = _currentVariable.Split((byte)Trigger.MSSP_VAL);

			var vv = variableList.Zip(valueList);

			await Task.CompletedTask;
		}

		/*
		 * For ease of parsing, variables and values cannot contain the MSSP_VAL, MSSP_VAR, IAC, or NUL byte. 
		 * The value can be an empty string unless a numeric value is expected in which case the default value should be 0. 
		 * If your Mud can't calculate one of the numeric values for the World variables you can use "-1" to indicate that the data is not available. 
		 * If a list of responses is provided try to pick from the list, unless "Etc" is specified, which means it's open ended.
		 */
		private void CaptureVariable(OneOf<byte, Trigger> b)
		{
			// We could increment here based on having switched... Somehow?
			// We need a better state tracking for this, to indicate the transition.
			_currentVariable.Add(b.AsT0);
		}

		private void CaptureValue(OneOf<byte, Trigger> b)
		{
			// We could increment here based on having switched... Somehow?
			// We need a better state tracking for this, to indicate the transition.
			_currentVariable.Add(b.AsT0);
		}

		/// <summary>
		/// Announce we do MSSP negotiation to the client.
		/// </summary>
		private async Task WillingMSSPAsync()
		{
			_Logger.Debug("Connection: {connectionStatus}", "Announcing willingness to MSSP!");
			await CallbackNegotiation(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.MSSP });
		}

		/// <summary>
		/// Announce the MSSP we support to the client after getting a Do.
		/// </summary>
		private async Task OnDoMSSPAsync(StateMachine<State, Trigger>.Transition _)
		{
			_Logger.Debug("Connection: {connectionStatus}", "Writing MSSP output");
			await CallbackNegotiation(ReportMSSP(_msspConfig).ToArray());
		}

		/// <summary>
		/// Announce we do MSSP negotiation to the server.
		/// </summary>
		private async Task OnWillMSSPAsync()
		{
			_Logger.Debug("Connection: {connectionStatus}", "Announcing willingness to MSSP!");
			await CallbackNegotiation(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MSSP });
		}

		/// <summary>
		/// Report the MSSP values to the client. 
		/// </summary>
		/// <param name="config">MSSP Configuration.</param>
		/// <returns>The full byte array to send the client.</returns>
		private IEnumerable<byte> ReportMSSP(MSSPConfig config)
		{
			byte[] prefix = new byte[] { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.MSSP };
			byte[] postfix = new byte[] { (byte)Trigger.IAC, (byte)Trigger.SE };
			return prefix.Concat(MSSPReadConfig(config)).Concat(postfix);
		}

		public TelnetInterpretor RegisterMSSPConfig(MSSPConfig config)
		{
			_msspConfig = config;
			return this;
		}

		private IEnumerable<byte> MSSPReadConfig(MSSPConfig config)
		{
			IEnumerable<byte> msspBytes = Array.Empty<byte>();

			var fields = typeof(MSSPConfig).GetFields();
			var knownFields = fields.Where(field => Attribute.IsDefined(field, typeof(MSSPConfig.NameAttribute)));

			foreach (var field in knownFields)
			{
				var b = field.GetValue(config)?.GetType();
				dynamic e = b?
					.GetMethod("Invoke")?
					.Invoke(field.GetValue(config), null);

				if (e == null) continue;

				var attr = Attribute.GetCustomAttribute(field, typeof(MSSPConfig.NameAttribute)) as MSSPConfig.NameAttribute;

				msspBytes = msspBytes.Concat(ConvertToMSSP(attr.Name, e) as IEnumerable<byte>);
			}

			foreach (var item in config.Extended ?? ImmutableDictionary<string, Func<dynamic>>.Empty)
			{
				if (item.Value == null) continue;
				msspBytes = msspBytes.Concat(ConvertToMSSP(item.Key, item.Value()) as IEnumerable<byte>);
			}

			return msspBytes;
		}

		// TODO: Cache this. This value shouldn't change.
		private IEnumerable<byte> ConvertToMSSP(string name, dynamic val)
		{
			IEnumerable<byte> bt = Array.Empty<byte>()
				.Concat(new byte[] { (byte)Trigger.MSSP_VAR })
				.Concat(ascii.GetBytes(name));

			switch (val)
			{
				case string s:
					{
						_Logger.Debug("MSSP Announcement: {msspkey}: {msspval}", name, s);
						return bt.Concat(new byte[] { (byte)Trigger.MSSP_VAL })
							.Concat(ascii.GetBytes(s));
					}
				case int i:
					{
						_Logger.Debug("MSSP Announcement: {msspkey}: {msspval}", name, i.ToString());
						return bt.Concat(new byte[] { (byte)Trigger.MSSP_VAL })
							.Concat(ascii.GetBytes(i.ToString()));
					}
				case bool boolean:
					{
						_Logger.Debug("MSSP Announcement: {msspkey}: {msspval}", name, boolean);
						return bt.Concat(new byte[] { (byte)Trigger.MSSP_VAL })
							.Concat(ascii.GetBytes(boolean ? "1" : "0"));
					}
				case IEnumerable<string> list:
					{
						foreach (var item in list)
						{
							_Logger.Debug("MSSP Announcement: {msspkey}[]: {msspval}", name, item);
							bt = bt.Concat(new byte[] { (byte)Trigger.MSSP_VAL })
								.Concat(ascii.GetBytes(item));
						}
						return bt;
					}
				default:
					{
						return Array.Empty<byte>();
					}
			}
		}
	}
}
