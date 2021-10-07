using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using MoreLinq;
using Stateless;
using TelnetNegotiationCore.Models;

namespace TelnetNegotiationCore.Interpretors
{
	public partial class TelnetInterpretor
	{
		private MSSPConfig _msspConfig;

		/// <summary>
		/// Mud Server Status Protocol will provide information to the requestee about the server's contents.
		/// </summary>
		/// <param name="tsm">The state machine.</param>
		/// <returns>Itself</returns>
		private StateMachine<State, Trigger> SetupMSSPNegotiation(StateMachine<State, Trigger> tsm)
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

			return tsm;
		}

		/// <summary>
		/// Announce we do MSSP negotiation to the client.
		/// </summary>
		private async Task WillingMSSPAsync()
		{
			_Logger.Debug("Connection: {connectionStatus}", "Announcing willingness to MSSP!");
			await _OutputStream.BaseStream.WriteAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.MSSP });
		}

		/// <summary>
		/// Announce the MSSP we support to the client after getting a Do.
		/// </summary>
		private async Task OnDoMSSPAsync(StateMachine<State, Trigger>.Transition _)
		{
			_Logger.Debug("Connection: {connectionStatus}", "Writing MSSP output");
			await _OutputStream.BaseStream.WriteAsync(ReportMSSP(_msspConfig).ToArray());
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
			IEnumerable<byte> msspBytes = new byte[] {};

			var fields = typeof(MSSPConfig).GetFields();
			var knownFields = fields.Where(field => Attribute.IsDefined(field, typeof(MSSPConfig.NameAttribute)));
			
			foreach(var field in knownFields)
			{
				var b = field.GetValue(config)?.GetType();
				dynamic e = b?
					.GetMethod("Invoke")?
					.Invoke(field.GetValue(config), null);

				if (e == null) continue;

				var attr = Attribute.GetCustomAttribute(field, typeof(MSSPConfig.NameAttribute)) as MSSPConfig.NameAttribute;

				msspBytes = msspBytes.Concat(MSSPConvert(attr.Name, e) as IEnumerable<byte>);
			}

			foreach(var item in config.Extended ?? ImmutableDictionary<string, Func<dynamic>>.Empty)
			{
				if(item.Value == null) continue;
				msspBytes = msspBytes.Concat(MSSPConvert(item.Key, item.Value()) as IEnumerable<byte>);
			}

			return msspBytes;
		}

		private IEnumerable<byte> MSSPConvert(string name, dynamic val)
		{
			IEnumerable<byte> bt = new byte[] { }
				.Concat(new byte[] {(byte)Trigger.MSSP_VAR})
				.Concat(ascii.GetBytes(name));

			if(val is string || val is int)
			{
				_Logger.Verbose("MSSP Announcement: {msspkey}: {msspval}", name, val);
				return bt.Concat(new byte[] {(byte)Trigger.MSSP_VAL})
					.Concat(ascii.GetBytes((string)val.ToString()));
			}
			else if(val is bool)
			{
				_Logger.Verbose("MSSP Announcement: {msspkey}: {msspval}", name, val);
				return bt.Concat(new byte[] {(byte)Trigger.MSSP_VAL})
					.Concat(ascii.GetBytes((bool)val ? "1" : "0") );
			}
			else if(val is IEnumerable<string>)
			{
				foreach(var item in val as IEnumerable<string>)
				{
					_Logger.Verbose("MSSP Announcement: {msspkey}[]: {msspval}", name, item);
					bt = bt.Concat(new byte[] {(byte)Trigger.MSSP_VAL})
						.Concat(ascii.GetBytes(item));
				}
				
				return bt;
			}
			else 
			{
				return new byte[] { };
			}
		}
	}
}
