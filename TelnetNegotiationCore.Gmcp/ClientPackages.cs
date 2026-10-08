namespace TelnetNegotiationCore.Gmcp;

/// <summary>
/// <c>Client.GUI</c>: where Mudlet can download the game's UI package, and its version. Mudlet
/// installs it, and upgrades it when the version changes.
/// </summary>
/// <param name="Version">The package version. Mudlet compares it with the installed one.</param>
/// <param name="Url">Where the package can be downloaded.</param>
public sealed record ClientGui(string Version, string Url) : IGmcpMessage
{
	/// <inheritdoc />
	public string Package => GmcpPackages.ClientGui;

	/// <inheritdoc />
	public string ToJson() => new JsonFieldWriter().Add("version", Version).Add("url", Url).ToString();

	/// <summary>Reads a <c>Client.GUI</c> body.</summary>
	public static bool TryParse(string? data, out ClientGui message) =>
		JsonFieldReader.TryRead(data, fields => new ClientGui(fields.String("version") ?? "", fields.String("url") ?? ""), out message)
		&& message.Url.Length > 0;
}

/// <summary>
/// <c>Client.Map</c>: where Mudlet can download the game's map, "after GMCP has been enabled".
/// </summary>
/// <param name="Url">The map's location: an MMP XML map, or another format the client reads.</param>
public sealed record ClientMap(string Url) : IGmcpMessage
{
	/// <inheritdoc />
	public string Package => GmcpPackages.ClientMap;

	/// <inheritdoc />
	public string ToJson() => new JsonFieldWriter().Add("url", Url).ToString();

	/// <summary>Reads a <c>Client.Map</c> body.</summary>
	public static bool TryParse(string? data, out ClientMap message) =>
		JsonFieldReader.TryRead(data, fields => new ClientMap(fields.String("url") ?? ""), out message)
		&& message.Url.Length > 0;
}
