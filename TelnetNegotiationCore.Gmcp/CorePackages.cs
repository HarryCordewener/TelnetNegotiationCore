namespace TelnetNegotiationCore.Gmcp;

/// <summary>
/// The messages of the GMCP <c>Core</c> package, spelled as the specification spells them.
/// </summary>
/// <remarks>
/// https://mudstandards.org/gmcp/core — "This package should be supported by any GMCP client".
/// Package names are matched without regard to case on receipt ("The package name can be case
/// insensitive"), but sent in this spelling, because clients such as Mudlet keep the case they were
/// sent: <c>gmcp.Core</c> and <c>gmcp.core</c> are different tables to a Mudlet script.
/// </remarks>
public static class CorePackages
{
	/// <summary>Client to server: identifies the client. "Needs to be the first message that the client sends."</summary>
	public const string Hello = "Core.Hello";

	/// <summary>Server to client: sent "immediately before terminating a connection", with a reason to show.</summary>
	public const string Goodbye = "Core.Goodbye";

	/// <summary>Client to server: resets the server's idle timeout for the character. No body.</summary>
	public const string KeepAlive = "Core.KeepAlive";

	/// <summary>Either way: a round-trip measurement. The answer has no body.</summary>
	public const string Ping = "Core.Ping";

	/// <summary>Client to server: the modules the client supports, replacing any earlier list.</summary>
	public const string SupportsSet = "Core.Supports.Set";

	/// <summary>Client to server: modules to add to the list, or whose version changes.</summary>
	public const string SupportsAdd = "Core.Supports.Add";

	/// <summary>Client to server: modules to take off the list.</summary>
	public const string SupportsRemove = "Core.Supports.Remove";
}
