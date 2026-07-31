using System;
using System.Collections.Generic;

namespace TelnetNegotiationCore.Models;

/// <summary>
/// Indicates the MSSP-safe name to send.
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public class NameAttribute(string name) : Attribute
{
	public string Name { get; private set; } = name;
}

/// <summary>
/// Indicates whether it's in the official MSSP definition.
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public class OfficialAttribute(bool official) : Attribute
{
	public bool Official { get; private set; } = official;
}

/// <summary>
/// Which transport carried an MSSP report.
/// </summary>
/// <remarks>
/// MSSP has two of them — telnet option 70, and the plaintext <c>MSSP-REQUEST</c> exchange — and a
/// server that implements both is not obliged to make them agree. Which one answered is therefore
/// part of the value rather than of the callback: it rides on the report, so it survives being
/// queued, stored, or handed on to something that never saw the connection.
/// </remarks>
public enum MSSPSource
{
	/// <summary>
	/// This configuration was not received from a peer: it was built by hand, typically to be
	/// advertised by a server.
	/// </summary>
	Unspecified = 0,

	/// <summary>Received as an <c>IAC SB MSSP … IAC SE</c> subnegotiation (telnet option 70).</summary>
	TelnetOption = 1,

	/// <summary>
	/// Received as a plaintext <c>MSSP-REPLY-START</c> … <c>MSSP-REPLY-END</c> block, in answer to a
	/// plaintext <c>MSSP-REQUEST</c>.
	/// </summary>
	Plaintext = 2
}

/// <summary>
/// The MSSP Configuration. Takes Functions for its inputs for the purpose of re-evaluation.
/// </summary>
public class MSSPConfig
{
	/// <summary>
	/// Which transport delivered this report, or <see cref="MSSPSource.Unspecified"/> for a
	/// configuration built by hand rather than received from a peer.
	/// </summary>
	/// <remarks>
	/// This is not an MSSP variable and is never sent: it carries no <see cref="NameAttribute"/>, so
	/// the send path does not see it.
	/// </remarks>
	public MSSPSource Source { get; set; } = MSSPSource.Unspecified;

	/// <summary>NAME: Name of the MUD.</summary>
	[Name("NAME"), Official(true)]
	public string? Name { get; set; }

	/// <summary>PLAYERS: Current number of logged in players.</summary>
	[Name("PLAYERS"), Official(true)]
	public int? Players { get; set; }

	/// <summary>UPTIME: Unix time value of the startup time of the MUD.</summary>
	[Name("UPTIME"), Official(true)]
	public int? Uptime { get; set; }

	/// <summary>CHARSET: ASCII, BIG5, CP437, CP949, CP1251, EUC-KR, GB18030, ISO-8859-1, ISO-8859-2, KOI8-R, UTF-8.
	/// Name of the charset in use. You can report multiple charsets using the array format, the preferred / default charset last.</summary>
	[Name("CHARSET"), Official(true)]
	public IEnumerable<string>? Charset { get; set; }

	/// <summary>CODEBASE: Name of the codebase, eg Merc 2.1. You can report multiple codebases using the array format, make sure to report the current codebase last.</summary>
	[Name("CODEBASE"), Official(true)]
	public IEnumerable<string>? Codebase { get; set; }

	/// <summary>CONTACT: Email address for contacting the MUD.</summary>
	[Name("CONTACT"), Official(true)]
	public string? Contact { get; set; }

	/// <summary>CRAWL DELAY: Preferred minimum number of hours between crawls. Send -1 to use the crawler's default.
	/// Recommended values are -1, 1, 5, 11, and 23.</summary>
	[Name("CRAWL DELAY"), Official(true)]
	public int? Crawl_Delay { get; set; }

	/// <summary>CREATED: Year the MUD was created.</summary>
	[Name("CREATED"), Official(true)]
	public string? Created { get; set; }

	/// <summary>DISCORD: URL to a Discord server, this should include the https:// prefix.</summary>
	[Name("DISCORD"), Official(true)]
	public string? Discord { get; set; }

	/// <summary>HOSTNAME: Current or new hostname.</summary>
	[Name("HOSTNAME"), Official(true)]
	public string? Hostname { get; set; }

	/// <summary>ICON: URL to a square image in bmp, png, jpg, or gif format. The icon should be equal or larger than 64x64 pixels, with a filesize no larger than 256KB.</summary>
	[Name("ICON"), Official(true)]
	public string? Icon { get; set; }

	/// <summary>IP: Current or new IP address.</summary>
	[Name("IP"), Official(true)]
	public string? IP { get; set; }

	/// <summary>IPV6: Current or new IPv6 address.</summary>
	[Name("IPV6"), Official(true)]
	public string? IPV6 { get; set; }

	/// <summary>LANGUAGE: English name of the language used, eg German or English</summary>
	[Name("LANGUAGE"), Official(true)]
	public string? Language { get; set; }

	/// <summary>LOCATION: English short name of the country where the server is located, using ISO 3166.</summary>
	[Name("LOCATION"), Official(true)]
	public string? Location { get; set; }

	/// <summary>MINIMUM AGE: Current minimum age requirement, omit if not applicable.</summary>
	[Name("MINIMUM AGE"), Official(true)]
	public string? Minimum_Age { get; set; }

	/// <summary>PORT: Current or new port number. Can be used multiple times, most important port last.</summary>
	[Name("PORT"), Official(true)]
	public int? Port { get; set; }

	/// <summary>REFERRAL: A list of other MSSP enabled MUDs for the crawler to check using the host port format and array notation. Adding referrals is important to make MSSP decentralized. Make sure to separate the host and port with a space rather than : because IPv6 addresses contain colons.</summary>
	[Name("REFERRAL"), Official(true)]
	public IEnumerable<string>? Referral { get; set; }

	/// <summary>The port number for a SSL (Secure Socket Layer) encrypted connection.</summary>
	[Name("SSL"), Official(true)]
	public string? Ssl { get; set; }

	/// <summary>WEBSITE: URL to MUD website, this should include the http:// or https:// prefix.</summary>
	[Name("WEBSITE"), Official(true)]
	public string? Website { get; set; }

	/// <summary>FAMILY: AberMUD, CoffeeMUD, DikuMUD, Evennia, LPMud, MajorMUD, MOO, Mordor, SocketMud, TinyMUD, TinyMUCK, TinyMUSH, Custom.
	/// Report Custom unless it's a well established family.
	///
	/// You can report multiple generic codebases using the array format, make sure to report the most distant codebase (aka the family) last.
	///
	/// Check the MUD family tree for naming and capitalization.</summary>
	[Name("FAMILY"), Official(true)]
	public IEnumerable<string>? Family { get; set; }

	/// <summary>GENRE: Adult, Fantasy, Historical, Horror, Modern, Mystery, None, Romance, Science Fiction, Spiritual</summary>
	[Name("GENRE"), Official(true)]
	public string? Genre { get; set; }

	/// <summary>GAMEPLAY: Adventure, Educational, Hack and Slash, None, Player versus Player, Player versus Environment, Questing, Roleplaying, Simulation, Social, Strategy</summary>
	[Name("GAMEPLAY"), Official(true)]
	public IEnumerable<string>? Gameplay { get; set; }


	/// <summary>STATUS: Alpha, Closed Beta, Open Beta, Live</summary>
	[Name("STATUS"), Official(true)]
	public string? Status { get; set; }

	/// <summary>GAMESYSTEM: D&D, d20 System, World of Darkness, Etc.
	/// Use Real Time, Tick Based, Turn Based, or Custom if using a custom gamesystems. Use None if not available.</summary>
	[Name("GAMESYSTEM"), Official(true)]
	public string? Gamesystem { get; set; }

	/// <summary>INTERMUD: AberChat, I3, IMC2, MudNet, Etc. Can be used multiple times if you support several protocols, most important protocol last. Leave empty or omit if no Intermud protocol is supported.</summary>
	[Name("INTERMUD"), Official(true)]
	public IEnumerable<string>? Intermud { get; set; }

	/// <summary>SUBGENRE: Alternate History, Anime, Cyberpunk, Detective, Discworld, Dragonlance, Christian Fiction, Classical Fantasy,
	/// Crime, Dark Fantasy, Epic Fantasy, Erotic, Exploration, Forgotten Realms, Frankenstein, Gothic, High Fantasy,
	/// Magical Realism, Medieval Fantasy, Multiverse, Paranormal, Post-Apocalyptic, Military Science Fiction,
	/// Mythology, Pulp, Star Wars, Steampunk, Suspense, Time Travel, Weird Fiction, World War II, Urban Fantasy, Etc.
	///
	/// Use None if not applicable.</summary>
	[Name("SUBGENRE"), Official(true)]
	public string? Subgenre { get; set; }

	/// <summary>AREAS: Current number of areas.</summary>
	[Name("AREAS"), Official(true)]
	public int? Areas { get; set; }

	/// <summary>HELPFILES: Current number of help files.</summary>
	[Name("HELPFILES"), Official(true)]
	public int? Helpfiles { get; set; }

	/// <summary>MOBILES: Current number of unique mobiles.</summary>
	[Name("MOBILES"), Official(true)]
	public int? Mobiles { get; set; }

	/// <summary>OBJECTS: Current number of unique objects.</summary>
	[Name("OBJECTS"), Official(true)]
	public int? Objects { get; set; }

	/// <summary>ROOMS: Current number of unique rooms, use 0 if roomless.</summary>
	[Name("ROOMS"), Official(true)]
	public int? Rooms { get; set; }

	/// <summary>CLASSES: Number of player classes, use 0 if classless.</summary>
	[Name("CLASSES"), Official(true)]
	public int? Classes { get; set; }

	/// <summary>LEVELS: Number of player levels, use 0 if level-less.</summary>
	[Name("LEVELS"), Official(true)]
	public int? Levels { get; set; }

	/// <summary>RACES: Number of player races, use 0 if raceless.</summary>
	[Name("RACES"), Official(true)]
	public int? Races { get; set; }

	/// <summary>SKILLS: Number of player skills, use 0 if skill-less.</summary>
	[Name("SKILLS"), Official(true)]
	public int? Skills { get; set; }

	/// <summary>ANSI: Supports ANSI colors ? 1 or 0</summary>
	[Name("ANSI"), Official(true)]
	public bool? Ansi { get; set; }

	/// <summary>PUEBLO: Supports Pueblo ? 1 or 0</summary>
	[Name("PUEBLO"), Official(false)]
	public bool? Pueblo { get; set; }

	/// <summary>MSP: Supports MSP ? 1 or 0. Not in the specification's official tables.</summary>
	[Name("MSP"), Official(false)]
	public bool? MSP { get; set; }

	/// <summary>UTF-8: Supports UTF-8 ? 1 or 0</summary>
	[Name("UTF-8"), Official(true)]
	public bool? UTF_8 { get; set; }

	/// <summary>VT100: Supports VT100 interface ?  1 or 0</summary>
	[Name("VT100"), Official(true)]
	public bool? VT100 { get; set; }

	/// <summary>XTERM: 256 COLORS   Supports xterm 256 colors ?  1 or 0</summary>
	[Name("XTERM 256 COLORS"), Official(true)]
	public bool? XTerm_256_Colors { get; set; }

	/// <summary>XTERM: TRUE COLORS  Supports xterm 24 bit colors ? 1 or 0</summary>
	[Name("XTERM TRUE COLORS"), Official(true)]
	public bool? XTerm_True_Colors { get; set; }

	/// <summary>PAY: TO PLAY        Pay to play ? 1 or 0</summary>
	[Name("PAY TO PLAY"), Official(true)]
	public bool? Pay_To_Play { get; set; }

	/// <summary>PAY: FOR PERKS      Pay for perks ? 1 or 0</summary>
	[Name("PAY FOR PERKS"), Official(true)]
	public bool? Pay_For_Perks { get; set; }

	/// <summary>HIRING: BUILDERS    Game is hiring builders ? 1 or 0</summary>
	[Name("HIRING BUILDERS"), Official(true)]
	public bool? Hiring_Builders { get; set; }

	/// <summary>HIRING: CODERS      Game is hiring coders ? 1 or 0</summary>
	[Name("HIRING CODERS"), Official(true)]
	public bool? Hiring_Coders { get; set; }

	/// <summary>Additional information.
	/// Dictionary Key serves as the MSSP Key
	/// Dictionary Value.obj serves as the MSSP Value
	/// Dictionary Value.type serves as the MSSP Value Type for unboxing
	/// We only support IEnumerable&lt;string&gt;, bool, int and string at this time.</summary>
	/// <remarks>
	/// When a report is <em>received</em>, every variable without a strongly typed property above --
	/// whether one of the specification's unofficial extras or a name a codebase invented -- is added
	/// here as an <see cref="IReadOnlyList{T}"/> of its values in wire order, so a single-valued
	/// variable arrives as a one-element list. <see cref="Variables"/> holds the same data for every
	/// variable, typed or not.
	/// </remarks>
	[Official(false)]
	public Dictionary<string, dynamic> Extended { get; set; } = [];

	/// <summary>
	/// Every variable reported, with every value, in wire order -- the lossless record the strongly
	/// typed properties above are projected from.
	/// </summary>
	/// <remarks>
	/// <para>
	/// The typed properties cannot represent MSSP faithfully: a variable may carry several values
	/// ("The same variable can be send more than once with different values, in which case the last
	/// reported value should be used as the default value"), and a server may send names this library
	/// has never heard of, which is the whole point of a protocol for servers describing themselves.
	/// Read this when you need what the peer actually said; read the properties when you want the
	/// convenient scalar. For a scalar property the value here is the <em>last</em> reported one, per
	/// the specification's rule.
	/// </para>
	/// <para>
	/// When sending, this takes precedence: any variable present here is written verbatim, with one
	/// <c>MSSP_VAL</c> per value, and the typed properties and <see cref="Extended"/> only supply
	/// names it does not mention. A configuration built by hand leaves it empty, so nothing changes
	/// for a server that only sets properties; a configuration received from a peer round-trips
	/// exactly.
	/// </para>
	/// </remarks>
	public MSSPVariableCollection Variables { get; } = new();
}
