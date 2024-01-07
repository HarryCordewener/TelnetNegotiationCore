namespace TelnetNegotiationCore.Models
{
	public enum State : sbyte
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
		EscapingMSDP
		#endregion MSDP Negotiation
	}
}
