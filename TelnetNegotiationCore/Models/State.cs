namespace TelnetNegotiationCore.Models;

public enum State : short
{
	#region Standard Negotiation
	Accepting,
	ReadingCharacters,
	StartNegotiation,
	EndNegotiation,
	SubNegotiation,
	EndSubNegotiation,
	DoNothing,
	GoAhead,
	Do,
	Dont,
	Willing,
	Refusing,
	#endregion Standard Negotiation
	#region MSSP Negotiation
	DoMSSP,
	DontMSSP,
	WontMSSP,
	WillMSSP,
	AlmostNegotiatingMSSP,
	EvaluatingMSSPVar,
	EvaluatingMSSPVal,
	EscapingMSSPVar,
	EscapingMSSPVal,
	CompletingMSSP,
	#endregion MSSP Negotiation
	#region Window Size Negotiation
	WillDoNAWS,
	WontDoNAWS,
	NegotiatingNAWS,
	EvaluatingNAWS,
	EscapingNAWSValue,
	CompletingNAWS,
	DontNAWS,
	DoNAWS,
	#endregion Window Size Negotiation
	#region Charset Negotation
	WillDoCharset,
	WontDoCharset,
	AlmostNegotiatingCharset,
	NegotiatingCharset,
	EvaluatingCharset,
	EscapingCharsetValue,
	CompletingCharset,
	Act,
	DoCharset,
	DontCharset,
	EndingCharsetSubnegotiation,
	NegotiatingAcceptedCharset,
	EvaluatingAcceptedCharsetValue,
	EscapingAcceptedCharsetValue,
	CompletingAcceptedCharset,
	NegotiatingTTABLE,
	EvaluatingTTABLE,
	EscapingTTABLEValue,
	CompletingTTABLE,
	TTableWaitingForACK,
	EndingTTABLESubnegotiation,
	#endregion Charset Negotation
	#region Terminal Type Negotiation
	WillDoTType,
	WontDoTType,
	DoTType,
	DontTType,
	EndingTerminalTypeNegotiation,
	AlmostNegotiatingTerminalType,
	NegotiatingTerminalType,
	EvaluatingTerminalType,
	EscapingTerminalTypeValue,
	CompletingTerminalType,
	#endregion Terminal Type Negotiation
	#region Safe Negotiation
	BadWilling,
	BadRefusing,
	BadDo,
	BadDont,
	BadSubNegotiation,
	BadSubNegotiationEscaping,
	BadSubNegotiationEvaluating,
	BadSubNegotiationCompleting,
	#endregion
	#region End of Record Negotiation
	DoEOR,
	DontEOR,
	WontEOR,
	WillEOR,
	Prompting,
	#endregion End of Record Negotiation
	#region GMCP Negotiation
	DoGMCP,
	DontGMCP,
	WontGMCP,
	WillGMCP,
	AlmostNegotiatingGMCP,
	EvaluatingGMCPValue,
	EscapingGMCPValue,
	CompletingGMCPValue,
	#endregion GMCP Negotiation
	#region MXP Negotiation
	DoMXP,
	DontMXP,
	WillMXP,
	WontMXP,
	#endregion MXP Negotiation
	#region MSDP Negotiation
	DontMSDP,
	DoMSDP,
	WillMSDP,
	WontMSDP,
	NegotiatingMSDP,
	EvaluatingMSDP,
	CompletingMSDP,
	AlmostNegotiatingMSDP,
	EscapingMSDP,
	DoSUPPRESSGOAHEAD,
	DontSUPPRESSGOAHEAD,
	WillSUPPRESSGOAHEAD,
	WontSUPPRESSGOAHEAD,
	#endregion MSDP Negotiation
	#region NEW-ENVIRON Negotiation
	DoNEWENVIRON,
	DontNEWENVIRON,
	WillNEWENVIRON,
	WontNEWENVIRON,
	AlmostNegotiatingNEWENVIRON,
	NegotiatingNEWENVIRON,
	EvaluatingNEWENVIRONVar,
	EvaluatingNEWENVIRONValue,
	EscapingNEWENVIRONVar,
	EscapingNEWENVIRONValue,
	CompletingNEWENVIRON,
	#endregion NEW-ENVIRON Negotiation
	#region ENVIRON Negotiation
	DoENVIRON,
	DontENVIRON,
	WillENVIRON,
	WontENVIRON,
	AlmostNegotiatingENVIRON,
	NegotiatingENVIRON,
	EvaluatingENVIRONVar,
	EvaluatingENVIRONValue,
	EscapingENVIRONVar,
	EscapingENVIRONValue,
	CompletingENVIRON,
	#endregion ENVIRON Negotiation
	#region MCCP Negotiation
	DoMCCP2,
	DontMCCP2,
	WillMCCP2,
	WontMCCP2,
	NegotiatingMCCP2,
	CompletingMCCP2,
	DoMCCP3,
	DontMCCP3,
	WillMCCP3,
	WontMCCP3,
	NegotiatingMCCP3,
	CompletingMCCP3,
	#endregion MCCP Negotiation
	#region Terminal Speed Negotiation
	DoTSPEED,
	DontTSPEED,
	WillTSPEED,
	WontTSPEED,
	AlmostNegotiatingTSPEED,
	NegotiatingTSPEED,
	EvaluatingTSPEED,
	EscapingTSPEEDValue,
	CompletingTSPEED,
	#endregion Terminal Speed Negotiation
	#region ECHO Negotiation
	DoECHO,
	DontECHO,
	WillECHO,
	WontECHO,
	#endregion ECHO Negotiation
	#region Flow Control Negotiation
	DoFLOWCONTROL,
	DontFLOWCONTROL,
	WillFLOWCONTROL,
	WontFLOWCONTROL,
	AlmostNegotiatingFLOWCONTROL,
	NegotiatingFLOWCONTROL,
	CompletingFLOWCONTROL,
	#endregion Flow Control Negotiation
	#region Line Mode Negotiation
	DoLINEMODE,
	DontLINEMODE,
	WillLINEMODE,
	WontLINEMODE,
	AlmostNegotiatingLINEMODE,
	NegotiatingLINEMODE,
	EvaluatingLINEMODE,
	EscapingLINEMODEValue,
	CompletingLINEMODE,
	#endregion Line Mode Negotiation
	#region X-Display Location Negotiation
	DoXDISPLOC,
	DontXDISPLOC,
	WillXDISPLOC,
	WontXDISPLOC,
	AlmostNegotiatingXDISPLOC,
	NegotiatingXDISPLOC,
	EvaluatingXDISPLOC,
	EscapingXDISPLOCValue,
	CompletingXDISPLOC,
	#endregion X-Display Location Negotiation
	#region Authentication Negotiation
	DoAuthentication,
	DontAuthentication,
	WillAuthentication,
	WontAuthentication,
	AlmostNegotiatingAuthentication,
	NegotiatingAuthenticationSend,
	CompletingAuthenticationNegotiation,
	SendingAuthenticationResponse,
	#endregion Authentication Negotiation
	#region Encryption Negotiation
	DoEncryption,
	DontEncryption,
	WillEncryption,
	WontEncryption,
	AlmostNegotiatingEncryption,
	NegotiatingEncryptionIs,
	NegotiatingEncryptionSupport,
	CompletingEncryptionNegotiation,
	ProcessingEncryptionIs,
	ProcessingEncryptionSupport,
	#endregion Encryption Negotiation

	// Appended, not filed under the protocol region they belong to. This enum is public, its values
	// are implicit, and C# inlines an enum constant into the assembly that names it -- so a plugin
	// compiled against an earlier package carries the numbers, not the names. Inserting a member into
	// a region renumbers every member after it, and that plugin would then configure a state other
	// than the one it was written against. Anything new goes here.
	#region Appended after 2.15.0
	/// <summary>MXP: reading <c>IAC SB MXP</c>, the start marker's option byte. See <c>MXPProtocol</c>.</summary>
	NegotiatingMXP,
	/// <summary>MXP: the start marker's second <c>IAC</c>; its <c>SE</c> has not been read yet.</summary>
	CompletingMXP,
	#endregion Appended after 2.15.0
	#region Appended after 2.16.0
	/// <summary>MCCP v1: reading <c>IAC SB COMPRESS</c>, the start marker's option byte. See <c>MCCPProtocol</c>.</summary>
	NegotiatingMCCP1,
	/// <summary>MCCP v1: the start marker's <c>WILL</c>; its <c>SE</c>, which has no <c>IAC</c> before it, has not been read yet.</summary>
	CompletingMCCP1
	#endregion Appended after 2.16.0
}
