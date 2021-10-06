﻿using System;
using System.Collections.Generic;

namespace TelnetNegotiationCore
{
	public class MSSPConfig 
	{
		public class NameAttribute: Attribute
		{
			public string Name {get;private set;}

			public NameAttribute(string name)
			{
				Name = name;
			}
		}
		
		public class OfficialAttribute: Attribute
		{
			public bool Official {get;private set;}

			public OfficialAttribute(bool official)
			{
				Official = official;
			}
		}

		/// <summary>NAME: Name of the MUD.</summary>
		[Name("NAME"), Official(true)]
		public Func<string> Name;

		/// <summary>PLAYERS: Current number of logged in players.</summary>
		[Name("PLAYERS"), Official(true)]
		public Func<int> Players;
		
		/// <summary>UPTIME: Unix time value of the startup time of the MUD.</summary>
		[Name("UPTIME"), Official(true)]
		public Func<int> Uptime;

		/// <summary>CODEBASE: Name of the codebase, eg Merc 2.1. You can report multiple codebases using the array format, make sure to report the current codebase last.</summary>
		[Name("CODEBASE"), Official(true)]
		public Func<IEnumerable<string>> Codebase;

		/// <summary>CONTACT: Email address for contacting the MUD.</summary>
		[Name("CONTACT"), Official(true)]
		public Func<string> Contact;
		
		/// <summary>CRAWL DELAY: Preferred minimum number of hours between crawls. Send -1 to use the crawler's default.</summary>
		[Name("CRAWL DELAY"), Official(true)]
		public Func<int> Crawl_Delay;

		/// <summary>CREATED: Year the MUD was created.</summary>
		[Name("CREATED"), Official(true)]
		public Func<string> Created;

		/// <summary>HOSTNAME: Current or new hostname.</summary>
		[Name("HOSTNAME"), Official(true)]
		public Func<string> Hostname;

		/// <summary>ICON: URL to a square image in bmp, png, jpg, or gif format. The icon should be equal or larger than 64x64 pixels, with a filesize no larger than 256KB.</summary>
		[Name("ICON"), Official(true)]
		public Func<string> Icon;

		/// <summary>IP: Current or new IP address.</summary>
		[Name("IP"), Official(true)]
		public Func<string> IP;

		/// <summary>IPV6: Current or new IPv6 address.</summary>
		[Name("IPV6"), Official(true)]
		public Func<string> IPV6;

		/// <summary>LANGUAGE: English name of the language used, eg German or English</summary>
		[Name("LANGUAGE"), Official(true)]
		public Func<string> Language;

		/// <summary>LOCATION: English short name of the country where the server is located, using ISO 3166.</summary>
		[Name("LOCATION"), Official(true)]
		public Func<string> Location;
						
		/// <summary>MINIMUM AGE: Current minimum age requirement, omit if not applicable.</summary>
		[Name("MINIMUM_AGE"), Official(true)]
		public Func<string> Minimum_Age;

		/// <summary>PORT: Current or new port number. Can be used multiple times, most important port last.</summary>
		[Name("PORT"), Official(true)]
		public Func<int> Port;

		/// <summary>REFERRAL: A list of other MSSP enabled MUDs for the crawler to check using the host port format and array notation. Adding referrals is important to make MSSP decentralized. Make sure to separate the host and port with a space rather than : because IPv6 addresses contain colons.</summary>
		[Name("REFERRAL"), Official(true)]
		public Func<IEnumerable<string>> Referral;

		/// <summary>WEBSITE: URL to MUD website, this should include the http:// or https:// prefix.</summary>
		[Name("WEBSITE"), Official(true)]
		public Func<string> Website;

		/// <summary>FAMILY: AberMUD, CoffeeMUD, DikuMUD, Evennia, LPMud, MajorMUD, MOO, Mordor, SocketMud, TinyMUD, TinyMUCK, TinyMUSH, Custom.
		/// Report Custom unless it's a well established family.
		///
		/// You can report multiple generic codebases using the array format, make sure to report the most distant codebase (aka the family) last.
		///
		/// Check the MUD family tree for naming and capitalization.</summary>
		[Name("FAMILY"), Official(true)]
		public Func<IEnumerable<string>> Family;

		/// <summary>GENRE: Adult, Fantasy, Historical, Horror, Modern, Mystery, None, Romance, Science Fiction, Spiritual</summary>
		[Name("GENRE"), Official(true)]
		public Func<string> Genre;

		/// <summary>GAMEPLAY: Adventure, Educational, Hack and Slash, None, Player versus Player, Player versus Environment, Questing, Roleplaying, Simulation, Social, Strategy</summary>
		[Name("GAMEPLAY"), Official(true)]
		public Func<IEnumerable<string>> Gameplay;
						

		/// <summary>STATUS: Alpha, Closed Beta, Open Beta, Live</summary>
		[Name("STATUS"), Official(true)]
		public Func<string> Status;

		/// <summary>GAMESYSTEM: D&D, d20 System, World of Darkness, Etc. Use Custom if using a custom game system. Use None if not available.</summary>
		[Name("GAMESYSTEM"), Official(true)]
		public Func<string> Gamesystem;

		/// <summary>INTERMUD: AberChat, I3, IMC2, MudNet, Etc. Can be used multiple times if you support several protocols, most important protocol last. Leave empty or omit if no Intermud protocol is supported.</summary>
		[Name("INTERMUD"), Official(true)]
		public Func<IEnumerable<string>> Intermud;

		/// <summary>SUBGENRE: Alternate History, Anime, Cyberpunk, Detective, Discworld, Dragonlance, Christian Fiction, Classical Fantasy,
		/// Crime, Dark Fantasy, Epic Fantasy, Erotic, Exploration, Forgotten Realms, Frankenstein, Gothic, High Fantasy,
		/// Magical Realism, Medieval Fantasy, Multiverse, Paranormal, Post-Apocalyptic, Military Science Fiction,
		/// Mythology, Pulp, Star Wars, Steampunk, Suspense, Time Travel, Weird Fiction, World War II, Urban Fantasy, Etc.
		///
		/// Use None if not applicable.</summary>
		[Name("SUBGENRE"), Official(true)]
		public Func<string> Subgenre;
						
		/// <summary>AREAS: Current number of areas.</summary>
		[Name("AREAS"), Official(true)]
		public Func<int> Areas;

		/// <summary>HELPFILES: Current number of help files.</summary>
		[Name("HELPFILES"), Official(true)]
		public Func<int> Helpfiles;

		/// <summary>MOBILES: Current number of unique mobiles.</summary>
		[Name("MOBILES"), Official(true)]
		public Func<int> Mobiles;

		/// <summary>OBJECTS: Current number of unique objects.</summary>
		[Name("OBJECTS"), Official(true)]
		public Func<int> Objects;

		/// <summary>ROOMS: Current number of unique rooms, use 0 if roomless.</summary>
		[Name("ROOMS"), Official(true)]
		public Func<int> Rooms;

		/// <summary>CLASSES: Number of player classes, use 0 if classless.</summary>
		[Name("CLASSES"), Official(true)]
		public Func<int> Classes;

		/// <summary>LEVELS: Number of player levels, use 0 if level-less.</summary>
		[Name("LEVELS"), Official(true)]
		public Func<int> Levels;

		/// <summary>RACES: Number of player races, use 0 if raceless.</summary>
		[Name("RACES"), Official(true)]
		public Func<int> Races;

		/// <summary>SKILLS: Number of player skills, use 0 if skill-less.</summary>
		[Name("SKILLS"), Official(true)]
		public Func<int> Skills;

		/// <summary>ANSI: Supports ANSI colors ? 1 or 0</summary>
		[Name("ANSI"), Official(true)]
		public Func<bool> Ansi;

		/// <summary>PUEBLO: Supports Pueblo ? 1 or 0</summary>
		[Name("PUEBLO"), Official(false)]
		public Func<bool> Pueblo;

		/// <summary>MSP: Supports MSP ? 1 or 0</summary>
		[Name("MSP"), Official(true)]
		public Func<bool> MSP;

		/// <summary>UTF-8: Supports UTF-8 ? 1 or 0</summary>
		[Name("UTF-8"), Official(true)]
		public Func<bool> UTF_8;

		/// <summary>VT100: Supports VT100 interface ?  1 or 0</summary>
		[Name("VT100"), Official(true)]
		public Func<bool> VT100;

		/// <summary>XTERM: 256 COLORS   Supports xterm 256 colors ?  1 or 0</summary>
		[Name("XTERM: 256 COLORS"), Official(true)]
		public Func<bool> XTerm_256_Colors;

		/// <summary>XTERM: TRUE COLORS  Supports xterm 24 bit colors ? 1 or 0</summary>
		[Name("XTERM: TRUE COLORS"), Official(true)]
		public Func<bool> XTerm_True_Colors;

		/// <summary>PAY: TO PLAY        Pay to play ? 1 or 0</summary>
		[Name("PAY: TO PLAY"), Official(true)]
		public Func<bool> Pay_To_Play;

		/// <summary>PAY: FOR PERKS      Pay for perks ? 1 or 0</summary>
		[Name("PAY: FOR PERKS"), Official(true)]
		public Func<bool> Pay_For_Perks;

		/// <summary>HIRING: BUILDERS    Game is hiring builders ? 1 or 0</summary>
		[Name("HIRING: BUILDERS"), Official(true)]
		public Func<bool> Hiring_Builders;

		/// <summary>HIRING: CODERS      Game is hiring coders ? 1 or 0</summary>
		[Name("HIRING: CODERS"), Official(true)]
		public Func<bool> Hiring_Coders;

		/// <summary>Additional information. 
		/// Dictionary Key serves as the MSSP Key
		/// Dictionary Value.obj serves as the MSSP Value
		/// Dictionary Value.type serves as the MSSP Value Type for unboxing
		/// We only support IEnumerable<string>, bool, int and string at this time</summary>
		[Official(false)]
		public IDictionary<string, Func<dynamic>> Extended = null;
	}
}
