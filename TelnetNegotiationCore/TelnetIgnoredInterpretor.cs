using Stateless;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading.Tasks;
using System.Linq;

namespace TelnetNegotiationCore
{
	/// <summary>
	/// Creates a standard response to items we explicitly ignore.
	/// </summary>
	public partial class TelnetInterpretor
	{
		private IList<string> _Ignored = new List<string> {
			nameof(Trigger.ECHO),
			nameof(Trigger.SUPPRESSGOAHEAD),
			nameof(Trigger.TSPEED),
			nameof(Trigger.FLOWCONTROL),
			nameof(Trigger.LINEMODE),
			nameof(Trigger.XDISPLOC),
			nameof(Trigger.ENVIRON),
			nameof(Trigger.AUTHENTICATION),
			nameof(Trigger.ENCRYPT),
			nameof(Trigger.NEWENVIRON)
		};

		/// <summary>
		/// We ignore these known Telnet RFCs, as they are no longer relevant, but we want to explicitly ignore or deny them.
		/// </summary>
		/// <param name="tsm">The state machine</param>
		private void SetupIgnored(StateMachine<State, Trigger> tsm)
		{
			var enumNames = typeof(State).GetEnumNames();
			_Ignored.SelectMany(x => enumNames.Where(enumName => enumName.EndsWith(x.ToString())));

			foreach (var rfc in _Ignored)
			{
				var trigger = (Trigger)Enum.Parse(typeof(Trigger), rfc);
				var relevantEnums = enumNames.Where(enumName => enumName.EndsWith(rfc.ToString())).Select(y => (State)Enum.Parse(typeof(State), y));
				tsm.Configure(State.Willing)
					.Permit(trigger, relevantEnums.First(x => x.ToString() == $"WillDo{rfc}"));
				tsm.Configure(State.Refusing)
					.Permit(trigger, relevantEnums.First(x => x.ToString() == $"WontDo{rfc}"));
				tsm.Configure(State.Do)
					.Permit(trigger, relevantEnums.First(x => x.ToString() == $"Do{rfc}"));
				tsm.Configure(State.Dont)
					.Permit(trigger, relevantEnums.First(x => x.ToString() == $"Dont{rfc}"));

				tsm.Configure((State)Enum.Parse(typeof(State), $"WillDo{rfc}"))
					.SubstateOf(State.Accepting)
					.OnEntryAsync(async () =>
					{
						_Logger.Debug("{connectionStatus}", $"Telling the Client, Don't {rfc}.");
						await _OutputStream.BaseStream.WriteAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DONT, (byte)trigger });
					});

				tsm.Configure((State)Enum.Parse(typeof(State), $"Do{rfc}"))
					.SubstateOf(State.Accepting)
					.OnEntryAsync(async () =>
					{
						_Logger.Debug("{connectionStatus}", $"Telling the Client, Won't {rfc}.");
						await _OutputStream.BaseStream.WriteAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WONT, (byte)trigger });
					});

				tsm.Configure((State)Enum.Parse(typeof(State), $"WontDo{rfc}"))
					.SubstateOf(State.Accepting);


				tsm.Configure((State)Enum.Parse(typeof(State), $"Dont{rfc}"))
					.SubstateOf(State.Accepting);
			}
		}
	}
}
