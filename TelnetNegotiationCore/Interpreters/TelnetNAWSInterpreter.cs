namespace TelnetNegotiationCore.Interpreters;

/// <summary>
/// The interpreter's half of http://www.faqs.org/rfcs/rfc1073.html: the window size a peer last
/// reported. Everything else about NAWS — the negotiation, the capture, the callback and
/// <see cref="Protocols.NAWSProtocol.SendWindowSizeAsync"/> — lives on the plugin, which writes
/// these properties from the generated machine's authoritative connection state.
/// </summary>
public partial class TelnetInterpreter
{
	/// <summary>
	/// Currently known Client Height
	/// </summary>
	/// <remarks>
	/// Defaults to 24
	/// </remarks>
	public int ClientHeight => _generatedMachine is not null && _generatedMachine.TryGetConnected(out var connected)
		? connected.Height
		: 24;

	/// <summary>
	/// Currently known Client Width.
	/// </summary>
	/// <remarks>
	/// Defaults to 78
	/// </remarks>
	public int ClientWidth => _generatedMachine is not null && _generatedMachine.TryGetConnected(out var connected)
		? connected.Width
		: 78;
}
