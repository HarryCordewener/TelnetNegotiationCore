using Stateless;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Collections.Immutable;
using OneOf;
using TelnetNegotiationCore.Generated;

namespace TelnetNegotiationCore.Models;

/// <summary>
/// Helper class to create TriggerWithParameter objects.
/// </summary>
public class ParameterizedTriggers
{
	private readonly Dictionary<Trigger, StateMachine<State, Trigger>.TriggerWithParameters<OneOf<byte, Trigger>>> _cache = [];

	/// <summary>
	/// Returns a (cached) Parameterized Trigger. 
	/// </summary>
	/// <param name="stm">State Machine</param>
	/// <param name="t">The Trigger</param>
	/// <returns>One of Byte or Trigger, allowing both the 255 byte range excluding standard triggers, and Triggers above the number</returns>
	public StateMachine<State, Trigger>.TriggerWithParameters<OneOf<byte, Trigger>> ParameterizedTrigger(StateMachine<State, Trigger> stm, Trigger t)
	{

		if (_cache.TryGetValue(t, out var value))
		{
			return value;
		}
		_cache.Add(t, stm.SetTriggerParameters<OneOf<byte, Trigger>>(t));
		return _cache[t];
	}
}

/// <summary>
/// A helper class to create state transitions for a list of triggers.
/// </summary>
public static class TriggerHelper
{
	// Use generated AllValues instead of reflection
	private static readonly ImmutableHashSet<Trigger> AllTriggers = TriggerExtensions.AllValues;

	public static void ForAllTriggers(Action<Trigger> f)
	{
		foreach(var trigger in AllTriggers) f(trigger);
	} 

	public static void ForAllTriggersExcept(IEnumerable<Trigger> except, Action<Trigger> f)
	{
		foreach (var trigger in AllTriggers.Except(except)) f(trigger);
	}

	public static void ForAllTriggersButIAC(Action<Trigger> f)
		=> ForAllTriggersExcept([Trigger.IAC], f);
}

#pragma warning disable CA1069 // Enums values should not be duplicated
public enum Trigger : short
{
	/// <summary>
	/// Sub-negotiation IS command.	
	/// Sub-negotiation NEW-ENVIRON VAR command
	/// Sub-negotiation FLOWCONTROL OFF command
	/// </summary>
	/// <remarks>
	/// RFC 855: http://www.faqs.org/rfcs/rfc855.html
	/// RFC 1572: http://www.faqs.org/rfcs/rfc1572.html
	/// RFC 1372: http://www.faqs.org/rfcs/rfc1372.html
	/// </remarks>
	IS = 0,
	NEWENVIRON_VAR = 0,
	FLOWCONTROL_OFF = 0,
	/// <summary>
	/// Sub-negotiation SEND command
	/// ECHO negotiation
	/// Sub-negotiation MSSP_VAR
	/// Sub-negotiation MSDP_VAR
	/// Sub-negotiation NEW-ENVIRON VALUE command
	/// Sub-negotiation FLOWCONTROL ON command
	/// </summary>
	/// <remarks>
	/// RFC 855: http://www.faqs.org/rfcs/rfc855.html
	/// RFC 857: http://www.faqs.org/rfcs/rfc857.html
	/// RFC 2066: http://www.faqs.org/rfcs/rfc2066.html
	/// RFC 1572: http://www.faqs.org/rfcs/rfc1572.html
	/// RFC 1372: http://www.faqs.org/rfcs/rfc1372.html
	/// MSSP: https://tintin.mudhalla.net/protocols/mssp/
	/// MSDP: https://tintin.mudhalla.net/protocols/msdp/
	/// </remarks>
	ECHO = 1,
	MSSP_VAR = 1,
	MSDP_VAR = 1,
	SEND = 1,
	REQUEST = 1,
	NEWENVIRON_VALUE = 1,
	FLOWCONTROL_ON = 1,
	/// <summary>
	/// Sub-negotiation ACCEPTED command.	
	/// Sub-negotiation MSSP_VAL
	/// Sub-negotiation MSDP_VAL
	/// Sub-negotiation NEW-ENVIRON INFO command
	/// Sub-negotiation NEW-ENVIRON ESC command
	/// Sub-negotiation FLOWCONTROL RESTART_ANY command
	/// Sub-negotiation LINEMODE FORWARDMASK command
	/// </summary>
	/// <remarks>
	/// MSSP: https://tintin.mudhalla.net/protocols/mssp/
	/// RFC 2066: http://www.faqs.org/rfcs/rfc2066.html
	/// RFC 1572: http://www.faqs.org/rfcs/rfc1572.html
	/// RFC 1372: http://www.faqs.org/rfcs/rfc1372.html
	/// RFC 1184: http://www.faqs.org/rfcs/rfc1184.html
	/// MSDP: https://tintin.mudhalla.net/protocols/msdp/
	/// </remarks>
	MSSP_VAL = 2,
	MSDP_VAL = 2,
	ACCEPTED = 2,
	NEWENVIRON_INFO = 2,
	NEWENVIRON_ESC = 2,
	FLOWCONTROL_RESTART_ANY = 2,
	LINEMODE_FORWARDMASK = 2,
	/// <summary>
	/// Sub-negotiation REJECTED command.	
	/// Suppress Go Ahead
	/// Sub-negotiation MSDP_TABLE_OPEN
	/// Sub-negotiation NEW-ENVIRON USERVAR command
	/// Sub-negotiation FLOWCONTROL RESTART_XON command
	/// Sub-negotiation LINEMODE SLC command
	/// </summary>
	/// <remarks>
	/// RFC 858: http://www.faqs.org/rfcs/rfc858.html
	/// RFC 2066: http://www.faqs.org/rfcs/rfc2066.html
	/// RFC 1572: http://www.faqs.org/rfcs/rfc1572.html
	/// RFC 1372: http://www.faqs.org/rfcs/rfc1372.html
	/// RFC 1184: http://www.faqs.org/rfcs/rfc1184.html
	/// MSDP: https://tintin.mudhalla.net/protocols/msdp/
	/// </remarks>
	SUPPRESSGOAHEAD = 3,
	REJECTED = 3,
	MSDP_TABLE_OPEN = 3,
	NEWENVIRON_USERVAR = 3,
	FLOWCONTROL_RESTART_XON = 3,
	LINEMODE_SLC = 3,
	/// <summary>
	/// Sub-negotiation TTABLE-IS command.
	/// Sub-negotiation MSDP_TABLE_CLOSE
	/// </summary>
	/// <remarks>
	/// RFC 2066: http://www.faqs.org/rfcs/rfc2066.html
	/// MSDP: https://tintin.mudhalla.net/protocols/msdp/
	/// </remarks>
	TTABLE_IS = 4,
	MSDP_TABLE_CLOSE = 4,
	/// <summary>
	/// Sub-negotiation TTABLE_REJECTED command.
	/// Sub-negotiation MSDP_ARRAY_OPEN
	/// </summary>
	/// <remarks>
	/// RFC 2066: http://www.faqs.org/rfcs/rfc2066.html
	/// </remarks>
	TTABLE_REJECTED = 5,
	MSDP_ARRAY_OPEN = 5,
	/// <summary>
	/// Sub-negotiation TTABLE_ACK command.
	/// Sub-negotiation MSDP_ARRAY_CLOSE   
	/// </summary>
	/// <remarks>
	/// RFC 2066: http://www.faqs.org/rfcs/rfc2066.html
	/// </remarks>
	TTABLE_ACK = 6,
	MSDP_ARRAY_CLOSE = 6,
	/// <summary>
	/// Sub-negotiation TTABLE_NAK command.
	/// </summary>
	/// <remarks>
	/// RFC 2066: http://www.faqs.org/rfcs/rfc2066.html
	/// </remarks>
	TTABLE_NAK = 7,
	/// <summary>
	/// Newline Indicator
	/// </summary>
	/// <remarks>
	/// We treat this as 'now act'
	/// </remarks>
	NEWLINE = 10,
	/// <summary>
	/// Carriage Return
	/// </summary>
	/// <remarks>
	/// We ignore this, due to its relationship to Newline Indication.
	/// </remarks>
	CARRIAGERETURN = 13,
	/// <summary>
	/// Terminal Type
	/// </summary>
	/// <remarks>
	/// RFC 1091: http://www.faqs.org/rfcs/rfc1091.html
	/// MTTS: https://tintin.mudhalla.net/protocols/mtts/
	/// </remarks>
	TTYPE = 24,
	/// <summary>
	/// End of Record Negotiation
	/// </summary>
	/// <remarks>
	/// EOR: https://tintin.mudhalla.net/protocols/eor/
	/// RFC 885: http://www.faqs.org/rfcs/rfc885.html
	/// </remarks>
	/// <summary>
	TELOPT_EOR = 25,
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
	/// Toggle Flow Control
	/// </summary>
	/// <remarks>
	/// RFC 1372: http://www.faqs.org/rfcs/rfc1372.html
	/// </remarks>
	FLOWCONTROL = 33,
	/// <summary>
	/// Linemode option
	/// </summary>
	/// <remarks>
	/// RFC 1184: http://www.faqs.org/rfcs/rfc1184.html
	/// </remarks>
	LINEMODE = 34,
	/// <summary>
	/// Sub-negotiation LINEMODE MODE command
	/// </summary>
	/// <remarks>
	/// RFC 1184: http://www.faqs.org/rfcs/rfc1184.html
	/// </remarks>
	LINEMODE_MODE = 1,
	/// <summary>
	/// X-Display Location
	/// </summary>
	/// <remarks>
	/// RFC 1096: http://www.faqs.org/rfcs/rfc1096.html
	/// </remarks>
	XDISPLOC = 35,
	/// <summary>
	/// Environment
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
	/// New Environment
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
	/// Mud Server Status Protocol
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
	/// Generic Mud Communication Protocol	
	/// </summary>
	/// <remarks>
	/// GMCP: https://tintin.mudhalla.net/protocols/gmcp/
	/// </remarks>
	GMCP = 201,
	/// <summary>
	/// End of Record
	/// </summary>
	/// <remarks>
	/// EOR: https://tintin.mudhalla.net/protocols/eor/
	/// RFC 885: http://www.faqs.org/rfcs/rfc885.html
	/// </remarks>
	/// <summary>
	EOR = 239,
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
	/// Go ahead. Used, under certain circumstances, to tell the other end that it can transmit.
	/// </summary>
	/// <remarks>
	/// RFC 854: http://www.faqs.org/rfcs/rfc854.html
	/// </remarks>
	GA = 249,
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
	ReadNextCharacter = 256,
	/// <summary>
	/// A generic bad state trigger, outside of what a byte can contain, to indicate a generic bad state transition.
	/// </summary>
	Error = 257
}
#pragma warning restore CA1069 // Enums values should not be duplicated

