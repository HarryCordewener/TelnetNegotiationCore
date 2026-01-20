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
	CompletingFLOWCONTROL
	#endregion Flow Control Negotiation
}
