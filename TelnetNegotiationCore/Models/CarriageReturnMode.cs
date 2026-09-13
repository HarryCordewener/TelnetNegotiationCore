namespace TelnetNegotiationCore.Models;

/// <summary>
/// What the interpreter does with a carriage return that is not part of a <c>CR LF</c> pair.
/// </summary>
/// <remarks>
/// <para>
/// <c>CR LF</c> ends a line in every mode, and so does a bare <c>LF</c>. These settings differ only
/// on a carriage return that turns out not to be part of <c>CR LF</c> — most importantly on
/// <c>CR NUL</c>, which two standards read two different ways:
/// </para>
/// <list type="bullet">
/// <item><description>RFC 1123 §3.3.1 requires that "CR LF and CR NUL MUST have the same effect on
/// an ASCII server host when received as input" — so for a server reading what a user typed, it ends
/// the line. See <see cref="EndOfLine"/>.</description></item>
/// <item><description>RFC 854 defines <c>CR NUL</c> as the way to send a bare carriage return in the
/// data, which is how libtelnet's <c>TELNET_FLAG_NVT_EOL</c> reads it. See
/// <see cref="Preserve"/>.</description></item>
/// </list>
/// <para>
/// Both are legitimate and which one is right depends on what the connection is carrying, which is
/// why this is a setting rather than a fixed behaviour. What no standard and no implementation does
/// is hand the <c>NUL</c> to the consumer as data.
/// </para>
/// </remarks>
public enum CarriageReturnMode
{
	/// <summary>
	/// Discard the carriage return, and consume a <c>NUL</c> that follows it.
	/// </summary>
	/// <remarks>
	/// The default, and what this library has always done, with one correction: the <c>NUL</c> of a
	/// <c>CR NUL</c> pair used to arrive as a literal <c>0x00</c> inside the line. Choose this for a
	/// line-oriented consumer that has no use for carriage returns — which is most of them.
	/// </remarks>
	Drop = 0,

	/// <summary>
	/// Treat <c>CR NUL</c> as the end of a line, exactly as <c>CR LF</c> is treated.
	/// </summary>
	/// <remarks>
	/// RFC 1123 §3.3.1's requirement for an ASCII server host reading user input. Choose this for a
	/// server whose clients may send <c>CR NUL</c> for the end-of-line key; RFC 1123 requires a User
	/// Telnet to be able to send <c>CR LF</c>, <c>CR NUL</c> or bare <c>LF</c>, and some do send
	/// <c>CR NUL</c>.
	/// </remarks>
	EndOfLine = 1,

	/// <summary>
	/// Deliver a literal carriage return for a <c>CR</c> that does not begin <c>CR LF</c>.
	/// </summary>
	/// <remarks>
	/// <para>
	/// RFC 854's reading, and what libtelnet implements. Choose this when the peer's carriage returns
	/// carry meaning — a MUD sending an ASCII spinner or a progress animation overprints with bare
	/// carriage returns, and the other modes discard exactly that.
	/// </para>
	/// <para>
	/// Note that this library's consumer surface is line-oriented: a submitted line arrives complete,
	/// so a carriage return reaches the consumer inside the line's text rather than as a cursor
	/// movement. Rendering it is the consumer's business.
	/// </para>
	/// </remarks>
	Preserve = 2,
}
