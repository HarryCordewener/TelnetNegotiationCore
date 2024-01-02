using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using MoreLinq;
using OneOf;
using Stateless;
using TelnetNegotiationCore.Models;

namespace TelnetNegotiationCore.Interpreters
{
	public partial class TelnetInterpreter
	{
		private Func<MSSPConfig> _msspConfig = () => new();

		private List<byte> _currentVariable;
		private List<List<byte>> _currentValueList;
		private List<byte> _currentValue;
		private List<List<byte>> _currentVariableList;

		private IImmutableDictionary<string, (MemberInfo Member, NameAttribute Attribute)> MSSPAttributeMembers = typeof(MSSPConfig)
			.GetMembers()
			.Select(x => (Member: x, Attribute: x.GetCustomAttribute<NameAttribute>()))
			.Where(x => x.Attribute != null)
			.ToImmutableDictionary(x => x.Attribute.Name.ToUpper());

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
					.OnEntry(() =>
					{
						_currentValue = new List<byte>();
						_currentVariable = new List<byte>();
						_currentValueList = new List<List<byte>>();
						_currentVariableList = new List<List<byte>>();
					});

				tsm.Configure(State.AlmostNegotiatingMSSP)
					.Permit(Trigger.MSSP_VAR, State.EvaluatingMSSPVar);

				tsm.Configure(State.EvaluatingMSSPVar)
					.Permit(Trigger.MSSP_VAL, State.EvaluatingMSSPVal)
					.Permit(Trigger.IAC, State.EscapingMSSPVar)
					.OnEntryFrom(Trigger.MSSP_VAR, RegisterMSSPVal);

				tsm.Configure(State.EscapingMSSPVar)
					.Permit(Trigger.IAC, State.EvaluatingMSSPVar);

				tsm.Configure(State.EvaluatingMSSPVal)
					.Permit(Trigger.MSSP_VAR, State.EvaluatingMSSPVar)
					.Permit(Trigger.IAC, State.EscapingMSSPVal)
					.OnEntryFrom(Trigger.MSSP_VAL, RegisterMSSPVar);

				tsm.Configure(State.EscapingMSSPVal)
					.Permit(Trigger.IAC, State.EvaluatingMSSPVal)
					.Permit(Trigger.SE, State.CompletingMSSP);

				tsm.Configure(State.CompletingMSSP)
					.SubstateOf(State.Accepting)
					.OnEntryAsync(ReadMSSPValues);

				TriggerHelper.ForAllTriggersExcept(new[] { Trigger.MSSP_VAL, Trigger.MSSP_VAR, Trigger.IAC }, t => tsm.Configure(State.EvaluatingMSSPVal).OnEntryFrom(ParametarizedTrigger(t), CaptureMSSPValue));
				TriggerHelper.ForAllTriggersExcept(new[] { Trigger.MSSP_VAL, Trigger.MSSP_VAR, Trigger.IAC }, t => tsm.Configure(State.EvaluatingMSSPVar).OnEntryFrom(ParametarizedTrigger(t), CaptureMSSPVariable));

				TriggerHelper.ForAllTriggersExcept(new[] { Trigger.IAC, Trigger.MSSP_VAR },
					t => tsm.Configure(State.EvaluatingMSSPVal).PermitReentry(t));
				TriggerHelper.ForAllTriggersExcept(new[] { Trigger.IAC, Trigger.MSSP_VAL },
					t => tsm.Configure(State.EvaluatingMSSPVar).PermitReentry(t));
			}

			return tsm;
		}

		private void RegisterMSSPVal()
		{
			if (!_currentValue.Any()) return;

			_currentValueList.Add(_currentValue);
			_currentValue = new List<byte>();
		}

		private void RegisterMSSPVar()
		{
			if (!_currentVariable.Any()) return;

			_currentVariableList.Add(_currentVariable);
			_currentVariable = new List<byte>();
		}

		private async Task ReadMSSPValues()
		{
			RegisterMSSPVal();
			RegisterMSSPVar();

			var grouping = _currentVariableList
				.Zip(_currentValueList)
				.GroupBy(x => CurrentEncoding.GetString(x.First.ToArray()));

			foreach (var group in grouping)
			{
				StoreClientMSSPDetails(group.Key, group.Select(x => CurrentEncoding.GetString(x.Second.ToArray())));
			}

			_Logger.Debug("Registering MSSP: {@msspConfig}", _msspConfig());

			await Task.CompletedTask;
		}

		/// <summary>
		/// 
		/// </summary>
		/// <param name="variable"></param>
		/// <param name="value"></param>
		private void StoreClientMSSPDetails(string variable, IEnumerable<string> value)
		{
			if (MSSPAttributeMembers.ContainsKey(variable.ToUpper()))
			{
				var foundAttribute = MSSPAttributeMembers[variable.ToUpper()];
				var fieldInfo = (PropertyInfo)foundAttribute.Member;

				var msspConfig = _msspConfig();
				if (fieldInfo.PropertyType == typeof(string))
				{
					fieldInfo.SetValue(msspConfig, value.First());
				}
				else if (fieldInfo.PropertyType == typeof(int))
				{
					var val = int.Parse(value.First());
					fieldInfo.SetValue(_msspConfig(), val);
				}
				else if (fieldInfo.PropertyType == typeof(bool))
				{
					var val = value.First() == "1";
					fieldInfo.SetValue(_msspConfig(), val);
				}
				else if (fieldInfo.PropertyType == typeof(IEnumerable<string>))
				{
					fieldInfo.SetValue(_msspConfig(), value);
				}
				_msspConfig = () => msspConfig;
			}
			else
			{
				// We are using the Extended section.
				_msspConfig().Extended.Add(variable, value.Count() > 1 ? value : value.First());
			}
		}

		/*
		 * For ease of parsing, variables and values cannot contain the MSSP_VAL, MSSP_VAR, IAC, or NUL byte. 
		 * The value can be an empty string unless a numeric value is expected in which case the default value should be 0. 
		 * If your Mud can't calculate one of the numeric values for the World variables you can use "-1" to indicate that the data is not available. 
		 * If a list of responses is provided try to pick from the list, unless "Etc" is specified, which means it's open ended.
		 * 
		 * TODO: Support -1 on reporting.
		 */
		private void CaptureMSSPVariable(OneOf<byte, Trigger> b)
		{
			// We could increment here based on having switched... Somehow?
			// We need a better state tracking for this, to indicate the transition.
			_currentVariable.Add(b.AsT0);
		}

		private void CaptureMSSPValue(OneOf<byte, Trigger> b)
		{
			// We could increment here based on having switched... Somehow?
			// We need a better state tracking for this, to indicate the transition.
			_currentValue.Add(b.AsT0);
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
			await CallbackNegotiation(ReportMSSP(_msspConfig()).ToArray());
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

		public TelnetInterpreter RegisterMSSPConfig(Func<MSSPConfig> config)
		{
			_msspConfig = config;
			_Logger.Debug("Registering MSSP Config. Currently evaluates to: {@msspConfig}", config());
			return this;
		}

		private IEnumerable<byte> MSSPReadConfig(MSSPConfig config)
		{
			IEnumerable<byte> msspBytes = Array.Empty<byte>();

			var fields = typeof(MSSPConfig).GetProperties();
			var knownFields = fields.Where(field => Attribute.IsDefined(field, typeof(NameAttribute)));

			foreach (var field in knownFields)
			{
				var b = field.GetValue(config);
				if (b == null) continue;

				var attr = Attribute.GetCustomAttribute(field, typeof(NameAttribute)) as NameAttribute;

				msspBytes = msspBytes.Concat(ConvertToMSSP(attr.Name, b));
			}

			foreach (var item in config.Extended ?? new Dictionary<string, dynamic>())
			{
				if (item.Value == null) continue;
				msspBytes = msspBytes.Concat(ConvertToMSSP(item.Key, item.Value) as IEnumerable<byte>);
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
