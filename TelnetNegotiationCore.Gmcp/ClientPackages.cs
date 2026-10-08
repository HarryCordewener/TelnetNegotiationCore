namespace TelnetNegotiationCore.Gmcp;

/// <summary>
/// <c>Client.GUI</c>: where Mudlet can download the game's UI package, and its version. Mudlet
/// installs it, and upgrades it when the version changes.
/// </summary>
/// <param name="Version">The package version. Mudlet compares it with the installed one.</param>
/// <param name="Url">Where the package can be downloaded.</param>
public sealed record ClientGui(string Version, string Url) : IGmcpMessage
{
	/// <summary>
	/// False tells Mudlet the game brings its own interface, so Mudlet does not offer its starter UI.
	/// Left out when null.
	/// </summary>
	public bool? BaseUi { get; init; }

	/// <summary>Declines Mudlet's starter UI without offering a package: <c>{"baseui":false}</c>.</summary>
	public static ClientGui DeclineBaseUi { get; } = new("", "") { BaseUi = false };

	/// <inheritdoc />
	public string Package => GmcpPackages.ClientGui;

	/// <inheritdoc />
	public string ToJson() => new JsonFieldWriter()
		.Add("version", Version.Length > 0 ? Version : null)
		.Add("url", Url.Length > 0 ? Url : null)
		.Add("baseui", BaseUi)
		.ToString();

	/// <summary>
	/// Reads a <c>Client.GUI</c> body: JSON, or the older plain form Mudlet also reads, the version
	/// on one line and the URL on the next.
	/// </summary>
	public static bool TryParse(string? data, out ClientGui message)
	{
		if (JsonFieldReader.TryRead(data, fields => new ClientGui(fields.String("version") ?? "", fields.String("url") ?? "")
		{
			BaseUi = fields.Boolean("baseui")
		}, out message))
		{
			return message.Url.Length > 0 || message.BaseUi.HasValue;
		}

		var lines = (data ?? "").Split('\n');
		message = lines.Length >= 2 ? new ClientGui(lines[0].Trim(), lines[1].Trim()) : new ClientGui("", "");
		return message.Version.Length > 0 && message.Url.Length > 0;
	}
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
