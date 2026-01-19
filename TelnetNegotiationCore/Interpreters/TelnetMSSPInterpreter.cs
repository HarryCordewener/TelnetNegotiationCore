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
	private Func<MSSPConfig> _msspConfig = () => new MSSPConfig();

	private List<byte> _currentMSSPVariable = [];
	private List<List<byte>> _currentMSSPValueList = [];
	private List<byte> _currentMSSPValue = [];
	private List<List<byte>> _currentMSSPVariableList = [];

	private readonly IImmutableDictionary<string, (MemberInfo Member, NameAttribute Attribute)> _msspAttributeMembers = typeof(MSSPConfig)
		.GetMembers()
		.Select(x => (Member: x, Attribute: x.GetCustomAttribute<NameAttribute>()))
		.Where(x => x.Attribute != null)
		.Select(x => (x.Member, Attribute: x.Attribute!))
		.ToImmutableDictionary(x => x.Attribute.Name.ToUpper());

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

	/// <summary>
	/// 
	/// </summary>
	/// <param name="variable"></param>
	/// <param name="value"></param>
	private void StoreClientMSSPDetails(string variable, IEnumerable<string> value)
	{
		if (_msspAttributeMembers.ContainsKey(variable.ToUpper()))
		{
			var foundAttribute = _msspAttributeMembers[variable.ToUpper()];
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
		_currentMSSPVariable.Add(b.AsT0);
	}

	private void CaptureMSSPValue(OneOf<byte, Trigger> b)
	{
		_currentMSSPValue.Add(b.AsT0);
	}

	private async ValueTask ReadMSSPValues()
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

		// Call MSSP plugin if available
		var msspPlugin = PluginManager?.GetPlugin<Protocols.MSSPProtocol>();
		if (msspPlugin != null && msspPlugin.IsEnabled)
		{
			await msspPlugin.OnMSSPRequestAsync(_msspConfig());
		}
	}

	/// <summary>
	/// Gets the MSSP configuration, preferring plugin config over interpreter config
	/// </summary>
	private MSSPConfig GetMSSPConfig()
	{
		var msspPlugin = PluginManager?.GetPlugin<Protocols.MSSPProtocol>();
		if (msspPlugin != null && msspPlugin.IsEnabled)
		{
			return msspPlugin.GetMSSPConfig();
		}
		return _msspConfig();
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
		_logger.LogDebug("Registering MSSP Config. Currently evaluates to: {@MSSPConfig}", config());
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

			msspBytes = [.. msspBytes, .. ConvertToMSSP(attr!.Name, b)];
		}

		foreach (var item in config.Extended ?? [])
		{
			if (item.Value == null) continue;
			msspBytes = [.. msspBytes, .. (byte[])ConvertToMSSP(item.Key, item.Value)];
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
					_logger.LogDebug("MSSP Announcement: {MSSPKey}: {MSSPVal}", name, s);
					return [..bt, (byte)Trigger.MSSP_VAL, .. ascii.GetBytes(s)];
				}
			case int i:
				{
					_logger.LogDebug("MSSP Announcement: {MSSPKey}: {MSSPVal}", name, i.ToString());
					return [.. bt, (byte)Trigger.MSSP_VAL, .. ascii.GetBytes(i.ToString())];
				}
			case bool boolean:
				{
					_logger.LogDebug("MSSP Announcement: {MSSPKey}: {MSSPVal}", name, boolean);
					return [.. bt, (byte)Trigger.MSSP_VAL, .. ascii.GetBytes(boolean ? "1" : "0")];
				}
			case IEnumerable<string> list:
				{
					foreach (var item in list)
					{
						_logger.LogDebug("MSSP Announcement: {MSSPKey}[]: {MSSPVal}", name, item);
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
