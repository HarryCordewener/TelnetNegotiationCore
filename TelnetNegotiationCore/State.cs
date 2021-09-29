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
		#region MSSP
        DoMSSP,
        DontMSSP,
		#endregion MSSP
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
    }
}
