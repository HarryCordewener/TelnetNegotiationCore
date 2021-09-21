namespace TelnetNegotiationCore
{
	public enum State
	{
		#region Standard Negotiation
		Accepting,
		ReadingCharacters,
		StartNegotiation,
		EndNegotiation,
		SubNegotiation,
		EndSubNegotiation,
		Do,
		Dont,
		Willing,
		Refusing,
		#endregion Standard Negotiation
		#region Echo - Ignored
		WillDoECHO,
		WontDoECHO,
		DoECHO,
		DontECHO,
		#endregion Echo - Ignored
		#region Suppress Go Ahead - Ignored
		WillDoSUPPRESSGOAHEAD,
		WontDoSUPPRESSGOAHEAD,
		DoSUPPRESSGOAHEAD,
		DontSUPPRESSGOAHEAD,
		#endregion Suppress Go Ahead - Ignored
		#region Terminal Speed - Ignored
		WillDoTSPEED,
		WontDoTSPEED,
		DoTSPEED,
		DontTSPEED,
		#endregion
		#region Flow Control - Ignored
		WillDoFLOWCONTROL,
		WontDoFLOWCONTROL,
		DoFLOWCONTROL,
		DontFLOWCONTROL,
		#endregion Flow Control - Ignored
		#region Line Mode - Ignored
		WillDoLINEMODE,
		WontDoLINEMODE,
		DoLINEMODE,
		DontLINEMODE,
		#endregion Line Mode - Ignored
		#region X Display Location - Ignored
		WillDoXDISPLOC,
		WontDoXDISPLOC,
		DoXDISPLOC,
		DontXDISPLOC,
		#endregion
		#region Environment - Ignored
		WillDoENVIRON,
		WontDoENVIRON,
		DoENVIRON,
		DontENVIRON,
		#endregion Environment - Ignored
		#region Authentication - Ignored
		WillDoAUTHENTICATION,
		WontDoAUTHENTICATION,
		DoAUTHENTICATION,
		DontAUTHENTICATION,
		#endregion Authentication - Ignored
		#region Encrypt - Ignored
		WillDoENCRYPT,
		WontDoENCRYPT,
		DoENCRYPT,
		DontENCRYPT,
		#endregion Encrypt - Ignored
		#region New Environment - Ignored
		WillDoNEWENVIRON,
		WontDoNEWENVIRON,
		DoNEWENVIRON,
		DontNEWENVIRON,
		#endregion
		#region NAWS
		WillDoNAWS,
		WontDoNAWS,
		NegotiatingNAWS,
		EvaluatingNAWS,
		EscapingNAWSValue,
		CompletingNAWS,
		DontNAWS,
		DoNAWS,
		#endregion NAWS
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
		NegotiatingTerminalType,
		EvaluatingTerminalType,
		EscapingTerminalTypeValue,
		CompletingTerminalType,
		#endregion Terminal Type Negotiation
	}
}
