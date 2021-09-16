using Stateless;
using System;
using System.Linq;
using System.Collections.Generic;
using MoreLinq;

namespace TelnetNegotiationCore
{
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
		/// Sub-negotiation SEND command.	
		/// </summary>
		/// <remarks>
		/// RFC 855: http://www.faqs.org/rfcs/rfc855.html
		/// </remarks>
		SEND = 1,
		/// <summary>
		/// Sub-negotiation ACCEPTED command.	
		/// </summary>
		/// <remarks>
		/// RFC 2066: http://www.faqs.org/rfcs/rfc2066.html
		/// </remarks>
		ACCEPTED = 2,
		/// <summary>
		/// Sub-negotiation REJECTED command.	
		/// </summary>
		/// <remarks>
		/// RFC 2066: http://www.faqs.org/rfcs/rfc2066.html
		/// </remarks>
		REJECTED = 3,
		/// <summary>
		/// Sub-negotiation TTABLE-IS command.	
		/// </summary>
		/// <remarks>
		/// RFC 2066: http://www.faqs.org/rfcs/rfc2066.html
		/// </remarks>
		TTABLE_IS = 4,
		/// <summary>
		/// Sub-negotiation TTABLE_REJECTED command.	
		/// </summary>
		/// <remarks>
		/// RFC 2066: http://www.faqs.org/rfcs/rfc2066.html
		/// </remarks>
		TTABLE_REJECTED = 5,
		/// <summary>
		/// Sub-negotiation TTABLE_ACK command.	
		/// </summary>
		/// <remarks>
		/// RFC 2066: http://www.faqs.org/rfcs/rfc2066.html
		/// </remarks>
		TTABLE_ACK = 6,
		/// <summary>
		/// Sub-negotiation TTABLE_NAK command.	
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
		NewLine = 10,
		/// <summary>
		/// Window size option.	
		/// </summary>
		/// <remarks>
		/// RFC 1073: http://www.faqs.org/rfcs/rfc1073.html
		/// </remarks>
		NAWS = 31,
		/// <summary>
		/// Charset option.	
		/// </summary>
		/// <remarks>
		/// RFC 2066: http://www.faqs.org/rfcs/rfc2066.html
		/// </remarks>
		CHARSET = 42,
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
		ReadNextCharacter = 256
	}
}
