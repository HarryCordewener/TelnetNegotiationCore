namespace TelnetNegotiationCore.Gmcp;

/// <summary>
/// A GMCP message with a fixed package name and a JSON body, which a session can send as it is.
/// </summary>
public interface IGmcpMessage
{
	/// <summary>
	/// The package name the message is sent under, such as <c>Client.Media.Play</c>.
	/// </summary>
	string Package { get; }

	/// <summary>
	/// The message's data section as JSON. Optional fields that are not set are left out.
	/// </summary>
	string ToJson();
}
