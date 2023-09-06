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
		#endregion End of Record Negotiation
	}
}
