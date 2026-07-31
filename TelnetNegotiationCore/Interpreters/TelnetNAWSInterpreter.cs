namespace TelnetNegotiationCore.Interpreters;

/// <summary>
/// The interpreter's half of http://www.faqs.org/rfcs/rfc1073.html: the window size a peer last
/// reported. Everything else about NAWS — the negotiation, the capture, the callback and
/// <see cref="Protocols.NAWSProtocol.SendWindowSizeAsync"/> — lives on the plugin, which writes
/// these two properties as each subnegotiation completes.
/// </summary>
public partial class TelnetInterpreter
{
	/// <summary>
	/// Currently known Client Height
	/// </summary>
	/// <remarks>
	/// Defaults to 24
	/// </remarks>
	public int ClientHeight { get; private set; } = 24;

	/// <summary>
	/// Currently known Client Width.
	/// </summary>
	/// <remarks>
	/// Defaults to 78
	/// </remarks>
	public int ClientWidth { get; private set; } = 78;
}
