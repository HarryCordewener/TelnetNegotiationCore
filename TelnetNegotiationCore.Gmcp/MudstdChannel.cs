using System.Collections.Generic;

namespace TelnetNegotiationCore.Gmcp;

/// <summary>One channel in <c>mudstd.channel.definitions</c>.</summary>
/// <param name="Id">Identifies the channel. Not shown.</param>
/// <param name="Label">The name to show, on a tab or a filter.</param>
public sealed record ChannelDefinition(string Id, string Label)
{
	/// <summary>An RGB color in hex.</summary>
	public string? Color { get; init; }

	/// <summary>An ANSI color, 0 to 15.</summary>
	public int? ColorAnsi { get; init; }
}

/// <summary>
/// <c>mudstd.channel.definitions</c>: the channels the player can use. Sent once on connecting, and
/// again when they change.
/// </summary>
/// <remarks>
/// The page writes the list in braces, which is not JSON. This sends a JSON array and reads either.
/// </remarks>
/// <param name="Channels">The channels.</param>
public sealed record ChannelDefinitions(IReadOnlyList<ChannelDefinition> Channels) : IGmcpMessage
{
	/// <inheritdoc />
	public string Package => GmcpPackages.MudstdChannelDefinitions;

	/// <inheritdoc />
	public string ToJson() => JsonNodes.Array(Channels, channel => new JsonFieldWriter()
		.Add("id", channel.Id)
		.Add("label", channel.Label)
		.Add("color", channel.Color)
		.Add("colorANSI", channel.ColorAnsi)
		.ToNode()).ToJsonString();

	/// <summary>Reads a <c>mudstd.channel.definitions</c> body.</summary>
	public static bool TryParse(string? data, out ChannelDefinitions message)
	{
		var read = JsonFieldReader.TryReadArray(data, fields => new ChannelDefinition(fields.String("id") ?? "", fields.String("label") ?? "")
		{
			Color = fields.String("color"),
			ColorAnsi = (int?)fields.Number("colorANSI")
		}, out var channels);
		message = new ChannelDefinitions(channels);
		return read;
	}
}

/// <summary>
/// <c>mudstd.channel.event</c>: something said on a channel. Text is UTF-8; <see cref="Msg"/> and
/// <see cref="Player"/> carry no color codes, the <c>Ansi</c> fields may.
/// </summary>
/// <param name="Chan">The channel's <see cref="ChannelDefinition.Id"/>.</param>
/// <param name="Msg">The message, without color codes.</param>
public sealed record ChannelEvent(string Chan, string Msg) : IGmcpMessage
{
	/// <summary>Who said it. Left out for system messages.</summary>
	public string? Player { get; init; }

	/// <summary><see cref="Player"/> with ANSI color codes.</summary>
	public string? PlayerAnsi { get; init; }

	/// <summary><see cref="Msg"/> with ANSI color codes.</summary>
	public string? MsgAnsi { get; init; }

	/// <inheritdoc />
	public string Package => GmcpPackages.MudstdChannelEvent;

	/// <inheritdoc />
	public string ToJson() => new JsonFieldWriter()
		.Add("chan", Chan)
		.Add("player", Player)
		.Add("msg", Msg)
		.Add("playerANSI", PlayerAnsi)
		.Add("msgANSI", MsgAnsi)
		.ToString();

	/// <summary>
	/// Reads a <c>mudstd.channel.event</c> body: an object, or the page's object wrapped in a
	/// second pair of braces.
	/// </summary>
	public static bool TryParse(string? data, out ChannelEvent message)
	{
		message = new ChannelEvent("", "");

		if (!JsonFieldReader.TryReadArray(data, fields => new ChannelEvent(fields.String("chan") ?? "", fields.String("msg") ?? "")
		{
			Player = fields.String("player"),
			PlayerAnsi = fields.String("playerANSI"),
			MsgAnsi = fields.String("msgANSI")
		}, out var events, single: true) || events.Count != 1)
		{
			return false;
		}

		message = events[0];
		return message.Chan.Length > 0;
	}
}
