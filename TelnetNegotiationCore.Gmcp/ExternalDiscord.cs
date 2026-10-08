using System.Collections.Generic;

namespace TelnetNegotiationCore.Gmcp;

/// <summary>
/// <c>External.Discord.Hello</c>: the player's Discord user, sent by the client. The server answers
/// with <see cref="DiscordInfo"/>.
/// </summary>
/// <param name="User">The Discord user name. Mudlet sends the message with no body when it has none.</param>
/// <param name="Private">The player does not want the name listed in a directory. "This MUST be respected by the game."</param>
public sealed record DiscordHello(string? User = null, bool? Private = null) : IGmcpMessage
{
	/// <inheritdoc />
	public string Package => GmcpPackages.ExternalDiscordHello;

	/// <inheritdoc />
	public string ToJson() => User is null && Private is null
		? ""
		: new JsonFieldWriter().Add("user", User).Add("private", Private).ToString();

	/// <summary>Reads an <c>External.Discord.Hello</c> body. An empty body reads with both fields null.</summary>
	public static bool TryParse(string? data, out DiscordHello message)
	{
		if (string.IsNullOrWhiteSpace(data))
		{
			message = new DiscordHello();
			return true;
		}

		return JsonFieldReader.TryRead(data, fields => new DiscordHello(fields.String("user"), fields.Boolean("private")), out message);
	}
}

/// <summary>
/// <c>External.Discord.Info</c>: the game's Discord server invite and its own Discord application,
/// both optional. The answer to <see cref="DiscordHello"/>.
/// </summary>
/// <param name="InviteUrl">An invite to the game's Discord server.</param>
/// <param name="ApplicationId">A Discord application the client should use instead of its own, for the game's name and icons.</param>
public sealed record DiscordInfo(string? InviteUrl = null, string? ApplicationId = null) : IGmcpMessage
{
	/// <inheritdoc />
	public string Package => GmcpPackages.ExternalDiscordInfo;

	/// <inheritdoc />
	public string ToJson() => new JsonFieldWriter().Add("inviteurl", InviteUrl).Add("applicationid", ApplicationId).ToString();

	/// <summary>Reads an <c>External.Discord.Info</c> body.</summary>
	public static bool TryParse(string? data, out DiscordInfo message) =>
		JsonFieldReader.TryRead(data, fields => new DiscordInfo(fields.String("inviteurl"), fields.String("applicationid")), out message);
}

/// <summary>
/// <c>External.Discord.Status</c>: the rich presence the client shows in Discord. Sent on a change,
/// and in answer to <c>External.Discord.Get</c>. Every field is optional.
/// </summary>
public sealed record DiscordStatus : IGmcpMessage
{
	/// <summary>The game's name. Mudlet shows "Playing" and this.</summary>
	public string? Game { get; init; }

	/// <summary>The first line, such as the area the character is in.</summary>
	public string? Details { get; init; }

	/// <summary>The second line, such as what the character is doing.</summary>
	public string? State { get; init; }

	/// <summary>Large icon names, in lowercase, in order of preference: the client uses the first it has.</summary>
	public IReadOnlyList<string>? LargeImage { get; init; }

	/// <summary>The large icon's hover text.</summary>
	public string? LargeImageText { get; init; }

	/// <summary>Small icon names, in lowercase, in order of preference.</summary>
	public IReadOnlyList<string>? SmallImage { get; init; }

	/// <summary>The small icon's hover text.</summary>
	public string? SmallImageText { get; init; }

	/// <summary>When the activity started, in Unix seconds. Discord shows the time elapsed.</summary>
	public long? StartTime { get; init; }

	/// <summary>When the activity ends, in Unix seconds. Discord shows a countdown, and it wins over <see cref="StartTime"/>.</summary>
	public long? EndTime { get; init; }

	/// <summary>How many are in the character's party.</summary>
	public int? PartySize { get; init; }

	/// <summary>How many the party can hold.</summary>
	public int? PartyMax { get; init; }

	/// <inheritdoc />
	public string Package => GmcpPackages.ExternalDiscordStatus;

	/// <inheritdoc />
	public string ToJson() => new JsonFieldWriter()
		.Add("game", Game)
		.Add("details", Details)
		.Add("state", State)
		.Add("largeimage", LargeImage)
		.Add("largeimagetext", LargeImageText)
		.Add("smallimage", SmallImage)
		.Add("smallimagetext", SmallImageText)
		.Add("starttime", StartTime)
		.Add("endtime", EndTime)
		.Add("partysize", PartySize)
		.Add("partymax", PartyMax)
		.ToString();

	/// <summary>Reads an <c>External.Discord.Status</c> body.</summary>
	public static bool TryParse(string? data, out DiscordStatus message) =>
		JsonFieldReader.TryRead(data, fields => new DiscordStatus
		{
			Game = fields.String("game"),
			Details = fields.String("details"),
			State = fields.String("state"),
			LargeImage = fields.Strings("largeimage"),
			LargeImageText = fields.String("largeimagetext"),
			SmallImage = fields.Strings("smallimage"),
			SmallImageText = fields.String("smallimagetext"),
			StartTime = fields.Number("starttime"),
			EndTime = fields.Number("endtime"),
			PartySize = (int?)fields.Number("partysize"),
			PartyMax = (int?)fields.Number("partymax")
		}, out message);
}
