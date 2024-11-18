using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OneOf;
using Stateless;
using TelnetNegotiationCore.Models;

namespace TelnetNegotiationCore.Interpreters;

public partial class TelnetInterpreter
{
	private Func<MSSPConfig> _msspConfig = () => new();

	private List<byte> _currentMSSPVariable;
	private List<List<byte>> _currentMSSPValueList;
	private List<byte> _currentMSSPValue;
	private List<List<byte>> _currentMSSPVariableList;

	public Func<MSSPConfig, Task> SignalOnMSSPAsync { get; init; }

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
				.OnEntry(() => _Logger.LogDebug("Connection: {ConnectionState}", "Client won't do MSSP - do nothing"));

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
				.OnEntry(() => _Logger.LogDebug("Connection: {ConnectionState}", "Server won't do MSSP - do nothing"));

			tsm.Configure(State.SubNegotiation)
				.Permit(Trigger.MSSP, State.AlmostNegotiatingMSSP)
				.OnEntry(() =>
				{
					_currentMSSPValue = [];
					_currentMSSPVariable = [];
					_currentMSSPValueList = [];
					_currentMSSPVariableList = [];
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

			TriggerHelper.ForAllTriggersExcept([Trigger.MSSP_VAL, Trigger.MSSP_VAR, Trigger.IAC], t => tsm.Configure(State.EvaluatingMSSPVal).OnEntryFrom(ParameterizedTrigger(t), CaptureMSSPValue));
			TriggerHelper.ForAllTriggersExcept([Trigger.MSSP_VAL, Trigger.MSSP_VAR, Trigger.IAC], t => tsm.Configure(State.EvaluatingMSSPVar).OnEntryFrom(ParameterizedTrigger(t), CaptureMSSPVariable));

			TriggerHelper.ForAllTriggersExcept([Trigger.IAC, Trigger.MSSP_VAR],
				t => tsm.Configure(State.EvaluatingMSSPVal).PermitReentry(t));
			TriggerHelper.ForAllTriggersExcept([Trigger.IAC, Trigger.MSSP_VAL],
				t => tsm.Configure(State.EvaluatingMSSPVar).PermitReentry(t));
		}

		return tsm;
	}

	private void RegisterMSSPVal()
	{
		if (_currentMSSPValue.Count == 0) return;

		_currentMSSPValueList.Add(_currentMSSPValue);
		_currentMSSPValue = [];
	}

	private void RegisterMSSPVar()
	{
		if (_currentMSSPVariable.Count == 0) return;

		_currentMSSPVariableList.Add(_currentMSSPVariable);
		_currentMSSPVariable = [];
	}

	private async Task ReadMSSPValues()
	{
		RegisterMSSPVal();
		RegisterMSSPVar();

		var grouping = _currentMSSPVariableList
			.Zip(_currentMSSPValueList)
			.GroupBy(x => CurrentEncoding.GetString([.. x.First]));

		foreach (var group in grouping)
		{
			StoreClientMSSPDetails(group.Key, group.Select(x => CurrentEncoding.GetString([.. x.Second])));
		}

		await (SignalOnMSSPAsync?.Invoke(_msspConfig()) ?? Task.CompletedTask);
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
			dynamic valueToSet = value.Count() > 1 ? value : value.First();

			if (!_msspConfig().Extended.TryAdd(variable, valueToSet))
			{
				_msspConfig().Extended[variable] = valueToSet;
			}
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
		_currentMSSPVariable.Add(b.AsT0);
	}

	private void CaptureMSSPValue(OneOf<byte, Trigger> b)
	{
		// We could increment here based on having switched... Somehow?
		// We need a better state tracking for this, to indicate the transition.
		_currentMSSPValue.Add(b.AsT0);
	}

	/// <summary>
	/// Announce we do MSSP negotiation to the client.
	/// </summary>
	private async Task WillingMSSPAsync()
	{
		_Logger.LogDebug("Connection: {ConnectionState}", "Announcing willingness to MSSP!");
		await CallbackNegotiationAsync([(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.MSSP]);
	}

	/// <summary>
	/// Announce the MSSP we support to the client after getting a Do.
	/// </summary>
	private async Task OnDoMSSPAsync(StateMachine<State, Trigger>.Transition _)
	{
		_Logger.LogDebug("Connection: {ConnectionState}", "Writing MSSP output");
		await CallbackNegotiationAsync(ReportMSSP(_msspConfig()));
	}

	/// <summary>
	/// Announce we do MSSP negotiation to the server.
	/// </summary>
	private async Task OnWillMSSPAsync()
	{
		_Logger.LogDebug("Connection: {ConnectionState}", "Announcing willingness to MSSP!");
		await CallbackNegotiationAsync([(byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MSSP]);
	}

	/// <summary>
	/// Report the MSSP values to the client. 
	/// </summary>
	/// <param name="config">MSSP Configuration.</param>
	/// <returns>The full byte array to send the client.</returns>
	private byte[] ReportMSSP(MSSPConfig config) => 
		[(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.MSSP, .. MSSPReadConfig(config), (byte)Trigger.IAC, (byte)Trigger.SE];

	public TelnetInterpreter RegisterMSSPConfig(Func<MSSPConfig> config)
	{
		_msspConfig = config;
		_Logger.LogDebug("Registering MSSP Config. Currently evaluates to: {@MSSPConfig}", config());
		return this;
	}

	private byte[] MSSPReadConfig(MSSPConfig config)
	{
		byte[] msspBytes = [];

		var fields = typeof(MSSPConfig).GetProperties();
		var knownFields = fields.Where(field => Attribute.IsDefined(field, typeof(NameAttribute)));

		foreach (var field in knownFields)
		{
			var b = field.GetValue(config);
			if (b == null) continue;

			var attr = Attribute.GetCustomAttribute(field, typeof(NameAttribute)) as NameAttribute;

			msspBytes = [.. msspBytes, .. ConvertToMSSP(attr.Name, b)];
		}

		foreach (var item in config.Extended ?? [])
		{
			if (item.Value == null) continue;
			msspBytes = [.. msspBytes, .. ConvertToMSSP(item.Key, item.Value) as byte[]];
		}

		return msspBytes;
	}

	private byte[] ConvertToMSSP(string name, dynamic val)
	{
		byte[] bt = [(byte)Trigger.MSSP_VAR, .. ascii.GetBytes(name)];

		switch (val)
		{
			case string s:
				{
					_Logger.LogDebug("MSSP Announcement: {MSSPKey}: {MSSPVal}", name, s);
					return [..bt, (byte)Trigger.MSSP_VAL, .. ascii.GetBytes(s)];
				}
			case int i:
				{
					_Logger.LogDebug("MSSP Announcement: {MSSPKey}: {MSSPVal}", name, i.ToString());
					return [.. bt, (byte)Trigger.MSSP_VAL, .. ascii.GetBytes(i.ToString())];
				}
			case bool boolean:
				{
					_Logger.LogDebug("MSSP Announcement: {MSSPKey}: {MSSPVal}", name, boolean);
					return [.. bt, (byte)Trigger.MSSP_VAL, .. ascii.GetBytes(boolean ? "1" : "0")];
				}
			case IEnumerable<string> list:
				{
					foreach (var item in list)
					{
						_Logger.LogDebug("MSSP Announcement: {MSSPKey}[]: {MSSPVal}", name, item);
						bt = [.. bt, (byte)Trigger.MSSP_VAL, .. ascii.GetBytes(item)];
					}
					return bt;
				}
			default:
				{
					return [];
				}
		}
	}
}
