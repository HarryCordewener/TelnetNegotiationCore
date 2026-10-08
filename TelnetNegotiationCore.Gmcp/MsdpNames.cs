namespace TelnetNegotiationCore.Gmcp;

/// <summary>
/// The command, list and variable names of MSDP, spelled as the specification spells them.
/// </summary>
/// <remarks>
/// <para>
/// https://tintin.mudhalla.net/protocols/msdp/ — names are upper case and matched exactly. The
/// reportable and configurable variables are "mere suggestions": a server may send others, and need
/// not send these.
/// </para>
/// <para>
/// The same names are used over native MSDP and over GMCP as <see cref="GmcpPackage"/>.
/// </para>
/// </remarks>
public static class MsdpNames
{
	/// <summary>The GMCP package that carries MSDP over GMCP. "Case sensitive and MSDP must be fully capitalized."</summary>
	public const string GmcpPackage = "MSDP";

	/// <summary>
	/// What a client sends as the variable name of a request, with its argument as the value.
	/// </summary>
	public static class Command
	{
		/// <summary>Asks for one of the <see cref="MsdpNames.List"/> names. The server answers with an array.</summary>
		public const string List = "LIST";

		/// <summary>Asks the server to send a variable now and again each time it changes.</summary>
		public const string Report = "REPORT";

		/// <summary>Asks the server to reset a list or variable to its initial state.</summary>
		public const string Reset = "RESET";

		/// <summary>Asks the server to send a variable once.</summary>
		public const string Send = "SEND";

		/// <summary>Asks the server to stop reporting a variable.</summary>
		public const string Unreport = "UNREPORT";
	}

	/// <summary>
	/// The lists a client can ask for with <see cref="Command.List"/>.
	/// </summary>
	public static class List
	{
		/// <summary>The commands the server supports.</summary>
		public const string Commands = "COMMANDS";

		/// <summary>The lists the server supports, this one included.</summary>
		public const string Lists = "LISTS";

		/// <summary>The variables the client may set.</summary>
		public const string ConfigurableVariables = "CONFIGURABLE_VARIABLES";

		/// <summary>The variables the server will report.</summary>
		public const string ReportableVariables = "REPORTABLE_VARIABLES";

		/// <summary>The variables being reported to this client now.</summary>
		public const string ReportedVariables = "REPORTED_VARIABLES";

		/// <summary>The variables the server will send on request.</summary>
		public const string SendableVariables = "SENDABLE_VARIABLES";
	}

	/// <summary>
	/// Reportable variables about the account and the server.
	/// </summary>
	public static class General
	{
		/// <summary>Name of the player account.</summary>
		public const string AccountName = "ACCOUNT_NAME";

		/// <summary>Name of the player character.</summary>
		public const string CharacterName = "CHARACTER_NAME";

		/// <summary>Name of the MUD, or an otherwise unique ID.</summary>
		public const string ServerId = "SERVER_ID";

		/// <summary>The time on the server, in military or civilian time.</summary>
		public const string ServerTime = "SERVER_TIME";

		/// <summary>URL of the MUD's online MSDP specification, if any.</summary>
		public const string Specification = "SPECIFICATION";
	}

	/// <summary>
	/// Reportable variables about the player's character.
	/// </summary>
	public static class Character
	{
		/// <summary>Current affects, as an array.</summary>
		public const string Affects = "AFFECTS";

		/// <summary>Current alignment.</summary>
		public const string Alignment = "ALIGNMENT";

		/// <summary>Current total experience points. 0-100 for a percentage.</summary>
		public const string Experience = "EXPERIENCE";

		/// <summary>Current maximum experience points. 100 for a percentage.</summary>
		public const string ExperienceMax = "EXPERIENCE_MAX";

		/// <summary>Experience points till next level. 0-100 for a percentage.</summary>
		public const string ExperienceTnl = "EXPERIENCE_TNL";

		/// <summary>Maximum experience points till next level. 100 for a percentage.</summary>
		public const string ExperienceTnlMax = "EXPERIENCE_TNL_MAX";

		/// <summary>Current health points.</summary>
		public const string Health = "HEALTH";

		/// <summary>Current maximum health points.</summary>
		public const string HealthMax = "HEALTH_MAX";

		/// <summary>Current level.</summary>
		public const string Level = "LEVEL";

		/// <summary>Current mana points.</summary>
		public const string Mana = "MANA";

		/// <summary>Current maximum mana points.</summary>
		public const string ManaMax = "MANA_MAX";

		/// <summary>Current amount of money.</summary>
		public const string Money = "MONEY";

		/// <summary>Current movement points.</summary>
		public const string Movement = "MOVEMENT";

		/// <summary>Current maximum movement points.</summary>
		public const string MovementMax = "MOVEMENT_MAX";
	}

	/// <summary>
	/// Reportable variables about the character's opponent.
	/// </summary>
	public static class Combat
	{
		/// <summary>Level of the opponent.</summary>
		public const string OpponentLevel = "OPPONENT_LEVEL";

		/// <summary>Current health points of the opponent. 0-100 for a percentage.</summary>
		public const string OpponentHealth = "OPPONENT_HEALTH";

		/// <summary>Current maximum health points of the opponent. 100 for a percentage.</summary>
		public const string OpponentHealthMax = "OPPONENT_HEALTH_MAX";

		/// <summary>Name of the opponent.</summary>
		public const string OpponentName = "OPPONENT_NAME";

		/// <summary>Relative strength of the opponent, like the consider command.</summary>
		public const string OpponentStrength = "OPPONENT_STRENGTH";
	}

	/// <summary>
	/// The reportable <see cref="Room"/> variable and the keys of the table it holds.
	/// </summary>
	/// <remarks>
	/// Only <see cref="Room"/> is a variable. The others are keys inside it, and <see cref="X"/>,
	/// <see cref="Y"/> and <see cref="Z"/> are keys inside <see cref="Coords"/>.
	/// </remarks>
	public static class Mapping
	{
		/// <summary>The room the character is in, as a table of the keys below.</summary>
		public const string Room = "ROOM";

		/// <summary>In <see cref="Room"/>: a number that identifies the room.</summary>
		public const string Vnum = "VNUM";

		/// <summary>In <see cref="Room"/>: the name of the room.</summary>
		public const string Name = "NAME";

		/// <summary>In <see cref="Room"/>: the area the room is in.</summary>
		public const string Area = "AREA";

		/// <summary>In <see cref="Room"/>: a table of <see cref="X"/>, <see cref="Y"/> and <see cref="Z"/>.</summary>
		public const string Coords = "COORDS";

		/// <summary>In <see cref="Coords"/>: the X coordinate of the room.</summary>
		public const string X = "X";

		/// <summary>In <see cref="Coords"/>: the Y coordinate of the room.</summary>
		public const string Y = "Y";

		/// <summary>In <see cref="Coords"/>: the Z coordinate of the room.</summary>
		public const string Z = "Z";

		/// <summary>In <see cref="Room"/>: the terrain type of the room, such as forest or ocean.</summary>
		public const string Terrain = "TERRAIN";

		/// <summary>In <see cref="Room"/>: a table of abbreviated exit directions (n, e, w) to destination VNUMs.</summary>
		public const string Exits = "EXITS";
	}

	/// <summary>
	/// Reportable variables about the game world.
	/// </summary>
	public static class World
	{
		/// <summary>The in-game time, in military or civilian time.</summary>
		public const string WorldTime = "WORLD_TIME";
	}

	/// <summary>
	/// Variables a client may set on the server. Supporting them is optional.
	/// </summary>
	public static class Configurable
	{
		/// <summary>Name of the MUD client.</summary>
		public const string ClientName = "CLIENT_NAME";

		/// <summary>Version of the MUD client.</summary>
		public const string ClientVersion = "CLIENT_VERSION";

		/// <summary>Unique ID of the MSDP plugin or script.</summary>
		public const string PluginId = "PLUGIN_ID";

		/// <summary>Not in the specification. What KaVir's protocol snippet calls <see cref="ClientName"/>.</summary>
		/// <remarks>
		/// The snippet, used by tbaMUD and others, lists <c>CLIENT_ID</c> rather than
		/// <c>CLIENT_NAME</c> among its configurable variables (protocol.c:352 in tbaMUD). A server
		/// that wants to hear from clients written against it can accept both.
		/// </remarks>
		public const string ClientId = "CLIENT_ID";
	}
}
