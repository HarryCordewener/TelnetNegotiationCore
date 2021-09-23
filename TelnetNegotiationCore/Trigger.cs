using Stateless;
using System;
using System.Linq;
using System.Collections.Generic;
using MoreLinq;

namespace TelnetNegotiationCore
{
	/// <summary>
	/// Helper class to create TriggerWithParameter objects.
	/// </summary>
	public static class ParameterizedTriggers
	{
		private readonly static Dictionary<Trigger, StateMachine<State, Trigger>.TriggerWithParameters<byte>> _cache;

		static ParameterizedTriggers()
		{
			_cache = new Dictionary<Trigger, StateMachine<State, Trigger>.TriggerWithParameters<byte>>();
		}

		public static StateMachine<State, Trigger>.TriggerWithParameters<byte> ByteTrigger(StateMachine<State, Trigger> stm, Trigger t)
		{
			if(!_cache.ContainsKey(t))
			{
				_cache.Add(t, stm.SetTriggerParameters<byte>(t));
			}
			return _cache[t];
		}
	}

	/// <summary>
	/// A helper class to create state transitions for a list of triggers.
	/// </summary>
	public static class TriggerHelper
	{
		public static IEnumerable<Trigger> AllTriggers = (Trigger[])Enum.GetValues(typeof(Trigger));
		public static IEnumerable<Trigger> AllTriggersButIAC = AllTriggers.Where(x => x != Trigger.IAC).ToArray();

		public static void ForAllTriggers(Action<Trigger> f) 
			=> AllTriggers.ForEach(f);
		public static void ForAllTriggersButIAC(Action<Trigger> f) 
			=> AllTriggersButIAC.ForEach(f);
	}

	public enum Trigger
	{
		/// <summary>
		/// Sub-negotiation IS command.	
		/// </summary>
		/// <remarks>
		/// RFC 855: http://www.faqs.org/rfcs/rfc855.html
		/// </remarks>
		IS = 0,
		/// <summary>
		/// Sub-negotiation SEND command
		/// ECHO negotiation (Unsupported)
		/// </summary>
		/// <remarks>
		/// RFC 855: http://www.faqs.org/rfcs/rfc855.html
		/// RFC 857: http://www.faqs.org/rfcs/rfc857.html
		/// </remarks>
		SEND = 1,
		ECHO = 1,
		/// <summary>
		/// Sub-negotiation ACCEPTED command.	
		/// </summary>
		/// <remarks>
		/// RFC 2066: http://www.faqs.org/rfcs/rfc2066.html
		/// </remarks>
		ACCEPTED = 2,
		/// <summary>
		/// Sub-negotiation REJECTED command.	
		/// Suppress Go Ahead
		/// </summary>
		/// <remarks>
		/// RFC 2066: http://www.faqs.org/rfcs/rfc2066.html
		/// RFC 858: http://www.faqs.org/rfcs/rfc858.html
		/// </remarks>
		REJECTED = 3,
		SUPPRESSGOAHEAD = 3,
		/// <summary>
		/// Sub-negotiation TTABLE-IS command. (Unsupported)
		/// </summary>
		/// <remarks>
		/// RFC 2066: http://www.faqs.org/rfcs/rfc2066.html
		/// </remarks>
		TTABLE_IS = 4,
		/// <summary>
		/// Sub-negotiation TTABLE_REJECTED command. (Unsupported)
		/// </summary>
		/// <remarks>
		/// RFC 2066: http://www.faqs.org/rfcs/rfc2066.html
		/// </remarks>
		TTABLE_REJECTED = 5,
		/// <summary>
		/// Sub-negotiation TTABLE_ACK command. (Unsupported)
		/// </summary>
		/// <remarks>
		/// RFC 2066: http://www.faqs.org/rfcs/rfc2066.html
		/// </remarks>
		TTABLE_ACK = 6,
		/// <summary>
		/// Sub-negotiation TTABLE_NAK command. (Unsupported)
		/// </summary>
		/// <remarks>
		/// RFC 2066: http://www.faqs.org/rfcs/rfc2066.html
		/// </remarks>
		TTABLE_NAK = 7,
		/// <summary>
		/// Carriage Return
		/// </summary>
		/// <remarks>
		/// We treat this as 'now act'
		/// </remarks>
		NEWLINE = 10,
		/// <summary>
		/// Terminal Type
		/// </summary>
		/// <remarks>
		/// RFC 1091: http://www.faqs.org/rfcs/rfc1091.html
		/// MTTS: https://tintin.mudhalla.net/protocols/mtts/ (Unsupported) (Support Planned)
		/// </remarks>
		TTYPE = 24,
		/// <summary>
		/// Window size option.	
		/// </summary>
		/// <remarks>
		/// RFC 1073: http://www.faqs.org/rfcs/rfc1073.html
		/// </remarks>
		NAWS = 31,
		/// <summary>
		/// Terminal Speed option (Unsupported)
		/// </summary>
		/// <remarks>
		/// RFC 1079: http://www.faqs.org/rfcs/rfc1079.html
		/// </remarks>
		TSPEED = 32,
		/// <summary>
		/// Toggle Flow Control (Unsupported)
		/// </summary>
		/// <remarks>
		/// RFC 1372: http://www.faqs.org/rfcs/rfc1372.html
		/// </remarks>
		FLOWCONTROL = 33,
		/// <summary>
		/// Linemode option (Unsupported)
		/// </summary>
		/// <remarks>
		/// RFC 1184: http://www.faqs.org/rfcs/rfc1184.html
		/// </remarks>
		LINEMODE = 34,
		/// <summary>
		/// X-Display Location (Unsupported)
		/// </summary>
		/// <remarks>
		/// RFC 1096: http://www.faqs.org/rfcs/rfc1096.html
		/// </remarks>
		XDISPLOC = 35,
		/// <summary>
		/// Environment (Unsupported)
		/// </summary>
		/// <remarks>
		/// RFC 1408: http://www.faqs.org/rfcs/rfc1408.html
		/// </remarks>
		ENVIRON = 36,
		/// <summary>
		/// Authentication (Unsupported)
		/// </summary>
		/// <remarks>
		/// RFC 2941: http://www.faqs.org/rfcs/rfc2941.html
		/// </remarks>
		AUTHENTICATION = 37,
		/// <summary>
		/// Encrypt (Unsupported)
		/// </summary>
		/// <remarks>
		/// RFC 2946: http://www.faqs.org/rfcs/rfc2946.html
		/// </remarks>
		ENCRYPT = 38,
		/// <summary>
		/// New Environment (Unsupported) (Support Planned)
		/// </summary>
		/// <remarks>
		/// MNES: https://tintin.mudhalla.net/protocols/mnes/
		/// RFC 1572: http://www.faqs.org/rfcs/rfc1572.html
		/// </remarks>
		NEWENVIRON = 39,
		/// <summary>
		/// Charset option
		/// </summary>
		/// <remarks>
		/// RFC 2066: http://www.faqs.org/rfcs/rfc2066.html
		/// </remarks>
		CHARSET = 42,
		/// <summary>
		/// Mud Server Data Protocol (Unsupported) (Support Planned)
		/// </summary>
		/// <remarks>
		/// MSDP: https://tintin.mudhalla.net/protocols/msdp/
		/// </remarks>
		MSDP = 69,
		/// <summary>
		/// https://tintin.mudhalla.net/protocols/mssp/ (Unsupported) (Support Planned)
		/// </summary>
		/// <remarks>
		/// MSSP: https://tintin.mudhalla.net/protocols/mssp/
		/// </remarks>
		MSSP = 70,
		/// <summary>
		/// Mud Client Compression Protocol	(Unsupported)
		/// </summary>
		/// <remarks>
		/// MCCP: https://tintin.mudhalla.net/protocols/mccp
		/// RFC 1950: https://tintin.mudhalla.net/rfc/rfc1950/
		/// </remarks>
		MCCP2 = 86,
		MCCP3 = 87,
		/// <summary>
		/// Generic Mud Communication Protocol	
		/// </summary>
		/// <remarks>
		/// GMCP: https://tintin.mudhalla.net/protocols/gmcp/
		/// </remarks>
		GMCP = 240,
		/// <summary>
		/// The end of sub-negotiation options.	
		/// </summary>
		/// <remarks>
		/// RFC 855: http://www.faqs.org/rfcs/rfc855.html
		/// </remarks>
		SE = 240,
		/// <summary>
		/// No operation.	
		/// </summary>
		/// <remarks>
		/// RFC 855: http://www.faqs.org/rfcs/rfc855.html
		/// </remarks>
		NOP = 241,
		/// <summary>
		/// The start of sub-negotiation options.	
		/// </summary>
		/// <remarks>
		/// RFC 855: http://www.faqs.org/rfcs/rfc855.html
		/// </remarks>
		SB = 250,
		/// <summary>
		/// Confirm willingness to negotiate.	
		/// </summary>
		/// <remarks>
		/// RFC 855: http://www.faqs.org/rfcs/rfc855.html
		/// </remarks>
		WILL = 251,
		/// <summary>
		/// Confirm unwillingness to negotiate.	
		/// </summary>
		/// <remarks>
		/// RFC 855: http://www.faqs.org/rfcs/rfc855.html
		/// </remarks>
		WONT = 252,
		/// <summary>
		/// Indicate willingness to negotiate.	
		/// </summary>
		/// <remarks>
		/// RFC 855: http://www.faqs.org/rfcs/rfc855.html
		/// </remarks>
		DO = 253,
		/// <summary>
		/// Indicate unwillingness to negotiate.	
		/// </summary>
		/// <remarks>
		/// RFC 855: http://www.faqs.org/rfcs/rfc855.html
		/// </remarks>
		DONT = 254,
		/// <summary>
		/// Marks the start of a negotiation sequence.	
		/// </summary>
		/// <remarks>
		/// RFC 855: http://www.faqs.org/rfcs/rfc855.html
		/// </remarks>
		IAC = 255,
		/// <summary>
		/// A generic trigger, outside of what a byte can contain, to indicate generic progression.
		/// </summary>
		ReadNextCharacter = 256
	}
}
