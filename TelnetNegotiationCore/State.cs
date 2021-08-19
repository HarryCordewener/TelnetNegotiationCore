namespace TelnetNegotiationCore
{
	public enum State
	{
		Accepting,
		ReadingCharacters,
		StartNegotiation,
		EndNegotiation,
		SubNegotiation,
		EndSubNegotiation,
		Willing,
		Refusing,
		WillDoNAWS,
		WontDoNAWS,
		NegotiatingNAWS,
		EvaluatingNAWS,
		EscapingNAWSValue,
		CompletingNAWS,
		WillDoCharset,
		WontDoCharset,
		NegotiatingCharset,
		EvaluatingCharset,
		EscapingCharsetValue,
		CompletingCharset,
		Act
	}
}
