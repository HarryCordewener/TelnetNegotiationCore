using System.Collections.Generic;

namespace TelnetNegotiationCore.Gmcp;

/// <summary>
/// <c>Char.Skills.Get</c>: asks for skills. With a group and a name the server answers with
/// <see cref="SkillInfo"/>; with only a group, <see cref="SkillsList"/>; with neither,
/// <see cref="SkillGroups"/>.
/// </summary>
/// <param name="Group">The skill group, such as <c>Elemancy</c>.</param>
/// <param name="Name">The skill.</param>
public sealed record SkillsGet(string? Group = null, string? Name = null) : IGmcpMessage
{
	/// <inheritdoc />
	public string Package => GmcpPackages.CharSkillsGet;

	/// <inheritdoc />
	public string ToJson() => new JsonFieldWriter().Add("group", Group).Add("name", Name).ToString();

	/// <summary>Reads a <c>Char.Skills.Get</c> body. No body asks for the groups.</summary>
	public static bool TryParse(string? data, out SkillsGet message)
	{
		if (string.IsNullOrWhiteSpace(data))
		{
			message = new SkillsGet();
			return true;
		}

		return JsonFieldReader.TryRead(data, fields => new SkillsGet(fields.String("group"), fields.String("name")), out message);
	}
}

/// <summary>One skill group and the character's rank in it.</summary>
/// <param name="Name">The group, such as <c>Survival</c>.</param>
/// <param name="Rank">The rank, such as <c>Adept (40%)</c>.</param>
public sealed record SkillGroup(string Name, string? Rank = null);

/// <summary>
/// <c>Char.Skills.Groups</c>: the character's skill groups. Sent on request, or whenever they change.
/// </summary>
/// <remarks>
/// Iron Realms names this package <c>Char.Skills.Groups</c>; the MUD Standards page heads it
/// <c>Char.Skills.Group</c>. <see cref="GmcpPackages.CharSkillsGroups"/> is the Iron Realms name.
/// </remarks>
/// <param name="Groups">The groups.</param>
public sealed record SkillGroups(IReadOnlyList<SkillGroup> Groups) : IGmcpMessage
{
	/// <inheritdoc />
	public string Package => GmcpPackages.CharSkillsGroups;

	/// <inheritdoc />
	public string ToJson() => JsonNodes.Array(Groups, group => new JsonFieldWriter().Add("name", group.Name).Add("rank", group.Rank).ToNode()).ToJsonString();

	/// <summary>Reads a <c>Char.Skills.Groups</c> body, an array.</summary>
	public static bool TryParse(string? data, out SkillGroups message)
	{
		var read = JsonFieldReader.TryReadArray(data, fields => new SkillGroup(fields.String("name") ?? "", fields.String("rank")), out var groups);
		message = new SkillGroups(groups);
		return read;
	}
}

/// <summary>
/// <c>Char.Skills.List</c>: the skills in a group, learned or not. Sent on request.
/// </summary>
/// <param name="Group">The group.</param>
/// <param name="List">The skills' names.</param>
/// <param name="Descriptions">A description of each skill, in the order of <see cref="List"/>. Written as <c>desc</c>.</param>
public sealed record SkillsList(string Group, IReadOnlyList<string> List, IReadOnlyList<string>? Descriptions = null) : IGmcpMessage
{
	/// <inheritdoc />
	public string Package => GmcpPackages.CharSkillsList;

	/// <inheritdoc />
	public string ToJson() => new JsonFieldWriter().Add("group", Group).Add("list", List).Add("desc", Descriptions).ToString();

	/// <summary>Reads a <c>Char.Skills.List</c> body.</summary>
	public static bool TryParse(string? data, out SkillsList message) =>
		JsonFieldReader.TryRead(data, fields => new SkillsList(fields.String("group") ?? "", fields.Strings("list") ?? [], fields.Strings("desc")), out message);
}

/// <summary>
/// <c>Char.Skills.Info</c>: one skill's description and usage. Sent on request.
/// </summary>
/// <param name="Group">The group.</param>
/// <param name="Skill">The skill.</param>
/// <param name="Info">The description, usually several lines.</param>
public sealed record SkillInfo(string Group, string Skill, string Info) : IGmcpMessage
{
	/// <inheritdoc />
	public string Package => GmcpPackages.CharSkillsInfo;

	/// <inheritdoc />
	public string ToJson() => new JsonFieldWriter().Add("group", Group).Add("skill", Skill).Add("info", Info).ToString();

	/// <summary>Reads a <c>Char.Skills.Info</c> body.</summary>
	public static bool TryParse(string? data, out SkillInfo message) =>
		JsonFieldReader.TryRead(data, fields => new SkillInfo(fields.String("group") ?? "", fields.String("skill") ?? "", fields.String("info") ?? ""), out message)
		&& message.Skill.Length > 0;
}
