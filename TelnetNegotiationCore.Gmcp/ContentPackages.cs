using System.Collections.Generic;

namespace TelnetNegotiationCore.Gmcp;

/// <summary>
/// <c>Char.Vitals</c> in the keys Mudlet's base UI reads for its gauges: <c>hp</c>/<c>maxhp</c>,
/// <c>mp</c>/<c>maxmp</c>, <c>mv</c>/<c>maxmv</c>, <c>xp</c>/<c>maxxp</c> and <c>nl</c> (percent
/// to next level). Iron Realms games send the same <c>hp</c>, <c>maxhp</c>, <c>mp</c>,
/// <c>maxmp</c> and <c>nl</c>.
/// </summary>
/// <remarks>
/// There is no one <c>Char.Vitals</c>: "the keys used in this command highly depend on the
/// individual server". These are the keys most scripts already look for. Anything else the game
/// tracks goes in <see cref="Additional"/>, written as fields of their own.
/// </remarks>
public sealed record CharVitals : IGmcpMessage
{
	/// <summary>Current health.</summary>
	public long? Hp { get; init; }

	/// <summary>Maximum health.</summary>
	public long? MaxHp { get; init; }

	/// <summary>Current mana, or the game's equivalent.</summary>
	public long? Mp { get; init; }

	/// <summary>Maximum mana.</summary>
	public long? MaxMp { get; init; }

	/// <summary>Current movement.</summary>
	public long? Mv { get; init; }

	/// <summary>Maximum movement.</summary>
	public long? MaxMv { get; init; }

	/// <summary>Current experience.</summary>
	public long? Xp { get; init; }

	/// <summary>Experience for the next level.</summary>
	public long? MaxXp { get; init; }

	/// <summary>Percent of the way to the next level, 0 to 100.</summary>
	public long? Nl { get; init; }

	/// <summary>Further fields, such as <c>ep</c> and <c>maxep</c>, written alongside the ones above.</summary>
	public IReadOnlyDictionary<string, long>? Additional { get; init; }

	/// <inheritdoc />
	public string Package => GmcpPackages.CharVitals;

	/// <inheritdoc />
	public string ToJson() => new JsonFieldWriter()
		.Add("hp", Hp)
		.Add("maxhp", MaxHp)
		.Add("mp", Mp)
		.Add("maxmp", MaxMp)
		.Add("mv", Mv)
		.Add("maxmv", MaxMv)
		.Add("xp", Xp)
		.Add("maxxp", MaxXp)
		.Add("nl", Nl)
		.AddEach(Additional)
		.ToString();

	/// <summary>
	/// Reads a <c>Char.Vitals</c> body in these keys. Numbers sent as strings, as Iron Realms
	/// games send them, are read too.
	/// </summary>
	public static bool TryParse(string? data, out CharVitals message) =>
		JsonFieldReader.TryRead(data, fields => new CharVitals
		{
			Hp = fields.Number("hp"),
			MaxHp = fields.Number("maxhp"),
			Mp = fields.Number("mp"),
			MaxMp = fields.Number("maxmp"),
			Mv = fields.Number("mv"),
			MaxMv = fields.Number("maxmv"),
			Xp = fields.Number("xp"),
			MaxXp = fields.Number("maxxp"),
			Nl = fields.Number("nl")
		}, out message);
}

/// <summary>
/// <c>Room.Info</c> in the Iron Realms shape, which Mudlet's generic mapper script reads:
/// <c>num</c>, <c>name</c>, <c>area</c>, <c>environment</c>, <c>coords</c>, <c>map</c>,
/// <c>exits</c> and <c>details</c>.
/// </summary>
/// <param name="Num">A number that identifies the room. The mapper keys rooms by it.</param>
/// <param name="Name">The room's name.</param>
public sealed record RoomInfo(long Num, string Name) : IGmcpMessage
{
	/// <summary>The area the room is in. The mapper groups rooms by it.</summary>
	public string? Area { get; init; }

	/// <summary>The terrain, such as <c>Hills</c> or <c>forest</c>. The mapper colours rooms by it.</summary>
	public string? Environment { get; init; }

	/// <summary>Coordinates, comma separated, such as <c>45,5,4,3</c>.</summary>
	public string? Coords { get; init; }

	/// <summary>A link to a map of the area.</summary>
	public string? Map { get; init; }

	/// <summary>Each exit's direction, such as <c>n</c> or <c>se</c>, and the <see cref="Num"/> of the room it leads to.</summary>
	public IReadOnlyDictionary<string, long>? Exits { get; init; }

	/// <summary>What else the room is, such as <c>shop</c> or <c>bank</c>.</summary>
	public IReadOnlyList<string>? Details { get; init; }

	/// <inheritdoc />
	public string Package => GmcpPackages.RoomInfo;

	/// <inheritdoc />
	public string ToJson() => new JsonFieldWriter()
		.Add("num", Num)
		.Add("name", Name)
		.Add("area", Area)
		.Add("environment", Environment)
		.Add("coords", Coords)
		.Add("map", Map)
		.Add("exits", Exits)
		.Add("details", Details)
		.ToString();

	/// <summary>
	/// Reads a <c>Room.Info</c> body. Aardwolf's shape shares <c>num</c>, <c>name</c> and
	/// <c>exits</c> with this one, and its <c>zone</c> reads as <see cref="Area"/> and its
	/// <c>terrain</c> as <see cref="Environment"/>.
	/// </summary>
	public static bool TryParse(string? data, out RoomInfo message) =>
		JsonFieldReader.TryRead(data, fields => fields.Number("num") is { } num
			? new RoomInfo(num, fields.String("name") ?? "")
			{
				Area = fields.String("area") ?? fields.String("zone"),
				Environment = fields.String("environment") ?? fields.String("terrain"),
				Coords = fields.String("coords"),
				Map = fields.String("map"),
				Exits = fields.NumberTable("exits"),
				Details = fields.Strings("details")
			}
			: null, out message!) && message is not null;
}

/// <summary>
/// <c>Comm.Channel.Text</c>: one line said on a channel, in the Iron Realms shape. Mudlet's base UI
/// shows <c>text</c> in its chat window, ANSI colour included.
/// </summary>
/// <param name="Channel">The channel, such as <c>ct</c> or <c>says</c>.</param>
/// <param name="Talker">Who said it.</param>
/// <param name="Text">The whole line as the player would see it, which may carry ANSI colour.</param>
public sealed record CommChannelText(string Channel, string Talker, string Text) : IGmcpMessage
{
	/// <inheritdoc />
	public string Package => GmcpPackages.CommChannelText;

	/// <inheritdoc />
	public string ToJson() => new JsonFieldWriter().Add("channel", Channel).Add("talker", Talker).Add("text", Text).ToString();

	/// <summary>Reads a <c>Comm.Channel.Text</c> body.</summary>
	public static bool TryParse(string? data, out CommChannelText message) =>
		JsonFieldReader.TryRead(data, fields => new CommChannelText(
			fields.String("channel") ?? "", fields.String("talker") ?? "", fields.String("text") ?? ""), out message)
		&& message.Text.Length > 0;
}
