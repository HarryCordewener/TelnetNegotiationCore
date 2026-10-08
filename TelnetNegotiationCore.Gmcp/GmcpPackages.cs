using System;

namespace TelnetNegotiationCore.Gmcp;

/// <summary>
/// Package names beyond <see cref="CorePackages"/>, spelled as their specifications spell them.
/// </summary>
/// <remarks>
/// <para>
/// The <c>Client.*</c>, <c>Char.Login.*</c>, <c>External.Discord.*</c> and <c>IRE.Composer.*</c> messages have fixed
/// shapes and a client that acts on them (Mudlet), so they have typed messages in this package.
/// </para>
/// <para>
/// The content packages (<c>Char.*</c>, <c>Room.*</c>, <c>Comm.*</c>) are named here, but their
/// keys "highly depend on the individual server": Aardwolf, Iron Realms, CoffeeMud and GoMud each
/// send <c>Char.Vitals</c> with different fields. Send your own JSON under these names, or use
/// <see cref="CharVitals"/>, <see cref="RoomInfo"/> and <see cref="CommChannelText"/>, which use
/// the keys Mudlet's bundled scripts read.
/// </para>
/// <para>
/// Send these spellings. Clients are meant to match package names without regard to case, but
/// Mudlet keeps the case it was sent when it builds its <c>gmcp</c> table, so <c>char.vitals</c>
/// and <c>Char.Vitals</c> land in different places for a Mudlet script.
/// </para>
/// </remarks>
public static class GmcpPackages
{
	/// <summary>Server to client: where Mudlet can download the game's UI package. <see cref="ClientGui"/>.</summary>
	public const string ClientGui = "Client.GUI";

	/// <summary>Server to client: where Mudlet can download the game's map. <see cref="ClientMap"/>.</summary>
	public const string ClientMap = "Client.Map";

	/// <summary>Server to client: the default location of media files. <see cref="MediaDefault"/>.</summary>
	public const string ClientMediaDefault = "Client.Media.Default";

	/// <summary>Server to client: download a media file ahead of use. <see cref="MediaLoad"/>.</summary>
	public const string ClientMediaLoad = "Client.Media.Load";

	/// <summary>Server to client: play a sound, music or video. <see cref="MediaPlay"/>.</summary>
	public const string ClientMediaPlay = "Client.Media.Play";

	/// <summary>Server to client: stop media. <see cref="MediaStop"/>.</summary>
	public const string ClientMediaStop = "Client.Media.Stop";

	/// <summary>Server to client: pause media. A Mudlet extension. <see cref="MediaPause"/>.</summary>
	public const string ClientMediaPause = "Client.Media.Pause";

	/// <summary>
	/// The obsolete name for <see cref="ClientMediaDefault"/>, which Mudlet still reads the same way.
	/// Receive it; send <see cref="ClientMediaDefault"/>.
	/// </summary>
	public const string ClientMediaObsoleteDefault = "Client.Media";

	/// <summary>Server to client: the sign-in methods the server accepts. <see cref="LoginDefault"/>.</summary>
	public const string CharLoginDefault = "Char.Login.Default";

	/// <summary>Client to server: an account name and password. <see cref="LoginCredentials"/>.</summary>
	public const string CharLoginCredentials = "Char.Login.Credentials";

	/// <summary>Server to client: whether the sign-in worked. <see cref="LoginResult"/>.</summary>
	public const string CharLoginResult = "Char.Login.Result";

	/// <summary>Server to client: a web page to sign in on. Version 2. <see cref="LoginUrl"/>.</summary>
	public const string CharLoginUrl = "Char.Login.URL";

	/// <summary>Server to client: a token the client keeps to sign in again. Version 2. <see cref="LoginToken"/>.</summary>
	public const string CharLoginToken = "Char.Login.Token";

	/// <summary>Client to server: signs in with a kept token. Version 2. <see cref="LoginReconnect"/>.</summary>
	public const string CharLoginReconnect = "Char.Login.Reconnect";

	/// <summary>Client to server: completes a client-driven OAuth sign-in. Version 2. <see cref="LoginAuthCode"/>.</summary>
	public const string CharLoginAuthCode = "Char.Login.AuthCode";

	/// <summary>Client to server: the player's Discord user. <see cref="DiscordHello"/>.</summary>
	public const string ExternalDiscordHello = "External.Discord.Hello";

	/// <summary>Server to client: the game's Discord invite and application. <see cref="DiscordInfo"/>.</summary>
	public const string ExternalDiscordInfo = "External.Discord.Info";

	/// <summary>Client to server: asks for <see cref="ExternalDiscordStatus"/>. No body.</summary>
	public const string ExternalDiscordGet = "External.Discord.Get";

	/// <summary>Server to client: the Discord rich presence to show. <see cref="DiscordStatus"/>.</summary>
	public const string ExternalDiscordStatus = "External.Discord.Status";

	/// <summary>Server to client: open Mudlet's composer on some text. <see cref="ComposerEdit"/>.</summary>
	public const string IreComposerEdit = "IRE.Composer.Edit";

	/// <summary>Client to server: the text the player wrote in the composer. <see cref="ComposerSetBuffer"/>.</summary>
	public const string IreComposerSetBuffer = "IRE.Composer.SetBuffer";

	/// <summary>Server to client: rarely changing character facts such as name, class and race.</summary>
	public const string CharBase = "Char.Base";

	/// <summary>Server to client: the character's name and full name.</summary>
	public const string CharName = "Char.Name";

	/// <summary>Server to client: health, mana, movement and similar. <see cref="CharVitals"/>.</summary>
	public const string CharVitals = "Char.Vitals";

	/// <summary>Server to client: the maximums that go with <see cref="CharVitals"/>, when sent apart. Spelled as Mudlet's base UI reads it.</summary>
	public const string CharMaxStats = "Char.Maxstats";

	/// <summary>Server to client: level, experience and other status values.</summary>
	public const string CharStatus = "Char.Status";

	/// <summary>Server to client: labels for the fields of <see cref="CharStatus"/>.</summary>
	public const string CharStatusVars = "Char.StatusVars";

	/// <summary>Server to client: money and similar.</summary>
	public const string CharWorth = "Char.Worth";

	/// <summary>Client to server: asks for the items in a location.</summary>
	public const string CharItemsInv = "Char.Items.Inv";

	/// <summary>Client to server: asks for the items in a container. <see cref="ItemsContents"/>.</summary>
	public const string CharItemsContents = "Char.Items.Contents";

	/// <summary>Client to server: asks for the items in the room.</summary>
	public const string CharItemsRoom = "Char.Items.Room";

	/// <summary>Server to client: the items in a location. <see cref="ItemsList"/>.</summary>
	public const string CharItemsList = "Char.Items.List";

	/// <summary>Server to client: an item arrived in a location. <see cref="ItemChange"/>.</summary>
	public const string CharItemsAdd = "Char.Items.Add";

	/// <summary>Server to client: an item left a location. <see cref="ItemChange"/>.</summary>
	public const string CharItemsRemove = "Char.Items.Remove";

	/// <summary>Server to client: an item in a location changed. <see cref="ItemChange"/>.</summary>
	public const string CharItemsUpdate = "Char.Items.Update";

	/// <summary>Client to server: asks for skill groups, a group's skills, or one skill. <see cref="SkillsGet"/>.</summary>
	public const string CharSkillsGet = "Char.Skills.Get";

	/// <summary>Server to client: the skill groups. <see cref="SkillGroups"/>.</summary>
	public const string CharSkillsGroups = "Char.Skills.Groups";

	/// <summary>Server to client: the skills in a group. <see cref="SkillsList"/>.</summary>
	public const string CharSkillsList = "Char.Skills.List";

	/// <summary>Server to client: one skill. <see cref="SkillInfo"/>.</summary>
	public const string CharSkillsInfo = "Char.Skills.Info";

	/// <summary>Server to client: every affliction the character has. <see cref="AfflictionsList"/>.</summary>
	public const string CharAfflictionsList = "Char.Afflictions.List";

	/// <summary>Server to client: the character gained an affliction. <see cref="Affliction"/>.</summary>
	public const string CharAfflictionsAdd = "Char.Afflictions.Add";

	/// <summary>Server to client: afflictions the character lost. <see cref="AfflictionsRemove"/>.</summary>
	public const string CharAfflictionsRemove = "Char.Afflictions.Remove";

	/// <summary>Server to client: every defence the character has. <see cref="DefencesList"/>.</summary>
	public const string CharDefencesList = "Char.Defences.List";

	/// <summary>Server to client: every defence, with category, icon and color. <see cref="DefencesList"/>.</summary>
	public const string CharDefencesInfoList = "Char.Defences.InfoList";

	/// <summary>Server to client: the character gained a defence. <see cref="Defence"/>.</summary>
	public const string CharDefencesAdd = "Char.Defences.Add";

	/// <summary>Server to client: defences the character lost. <see cref="DefencesRemove"/>.</summary>
	public const string CharDefencesRemove = "Char.Defences.Remove";

	/// <summary>Server to client: the resources the game tracks. A proposal. <see cref="StatDefinitions"/>.</summary>
	public const string MudstdResourcesDefinitions = "mudstd.resources.definitions";

	/// <summary>Server to client: the resources' values. A proposal. <see cref="StatUpdate"/>.</summary>
	public const string MudstdResourcesUpdate = "mudstd.resources.update";

	/// <summary>Server to client: the resources, under the character. A proposal. <see cref="StatDefinitions"/>.</summary>
	public const string MudstdCharResourcesDefinitions = "mudstd.char.resources.definitions";

	/// <summary>Server to client: the resources' values, under the character. A proposal. <see cref="StatUpdate"/>.</summary>
	public const string MudstdCharResourcesUpdate = "mudstd.char.resources.update";

	/// <summary>Server to client: the attributes the game tracks. A proposal. <see cref="StatDefinitions"/>.</summary>
	public const string MudstdCharAttributesDefinitions = "mudstd.char.attributes.definitions";

	/// <summary>Server to client: the attributes' values. A proposal. <see cref="StatUpdate"/>.</summary>
	public const string MudstdCharAttributesUpdate = "mudstd.char.attributes.update";

	/// <summary>Server to client: the channels the player can use. <see cref="ChannelDefinitions"/>.</summary>
	public const string MudstdChannelDefinitions = "mudstd.channel.definitions";

	/// <summary>Server to client: something said on a channel. <see cref="ChannelEvent"/>.</summary>
	public const string MudstdChannelEvent = "mudstd.channel.event";

	/// <summary>Server to client: the terrains rooms refer to. A proposal. <see cref="RoomTerrain"/>.</summary>
	public const string MudstdRoomTerrain = "mudstd.room.terrain";

	/// <summary>Server to client: the room, with ids that need not be numbers. A proposal. <see cref="MudstdRoomInfo"/>.</summary>
	public const string MudstdRoomInfo = "mudstd.room.info";

	/// <summary>Server to client: the NPCs, items and players about. A proposal. <see cref="RoomEntities"/>.</summary>
	public const string MudstdRoomEntities = "mudstd.room.entities";

	/// <summary>Client to server: the frames the client supports. A proposal. <see cref="FrameSupport"/>.</summary>
	public const string MudstdFrameSupport = "mudstd.frame.support";

	/// <summary>Server to client: opens a frame. A proposal. <see cref="FrameOpen"/>.</summary>
	public const string MudstdFrameOpen = "mudstd.frame.open";

	/// <summary>Server to client: closes a frame. A proposal. <see cref="FrameClose"/>.</summary>
	public const string MudstdFrameClose = "mudstd.frame.close";

	/// <summary>Server to client: text for a terminal frame. A proposal. <see cref="FrameTerminal"/>.</summary>
	public const string MudstdFrameTerminal = "mudstd.frame.terminal";

	/// <summary>Server to client: the image for an image frame. A proposal. <see cref="FrameImage"/>.</summary>
	public const string MudstdFrameImage = "mudstd.frame.image";

	/// <summary>Client to server: a frame opened, and its size. A proposal. <see cref="FrameSized"/>.</summary>
	public const string MudstdFrameOpened = "mudstd.frame.opened";

	/// <summary>Client to server: a frame closed. A proposal. <see cref="FrameClosed"/>.</summary>
	public const string MudstdFrameClosed = "mudstd.frame.closed";

	/// <summary>Client to server: a frame's size changed. A proposal. <see cref="FrameSized"/>.</summary>
	public const string MudstdFrameResized = "mudstd.frame.resized";

	/// <summary>Server to client: the tilesets a map draws from. A proposal. <see cref="TilemapTilesets"/>.</summary>
	public const string MudstdTilemapTilesets = "mudstd.tilemap.tilesets";

	/// <summary>Server to client: the map's size and tile ranges. A proposal. <see cref="TilemapInfo"/>.</summary>
	public const string MudstdTilemapInfo = "mudstd.tilemap.info";

	/// <summary>Server to client: the map's tiles. A proposal. <see cref="TilemapUpdate"/>.</summary>
	public const string MudstdTilemapUpdate = "mudstd.tilemap.update";

	/// <summary>Server to client: the room the character is in. <see cref="RoomInfo"/>.</summary>
	public const string RoomInfo = "Room.Info";

	/// <summary>Server to client: the character tried an exit that does not exist.</summary>
	public const string RoomWrongDir = "Room.WrongDir";

	/// <summary>Server to client: the players in the room.</summary>
	public const string RoomPlayers = "Room.Players";

	/// <summary>Server to client: a player entered the room.</summary>
	public const string RoomAddPlayer = "Room.AddPlayer";

	/// <summary>Server to client: a player left the room.</summary>
	public const string RoomRemovePlayer = "Room.RemovePlayer";

	/// <summary>Server to client: one line of channel text. <see cref="CommChannelText"/>.</summary>
	public const string CommChannelText = "Comm.Channel.Text";

	/// <summary>Server to client: the channels the character can use.</summary>
	public const string CommChannelList = "Comm.Channel.List";

	/// <summary>Server to client: who is on which channel.</summary>
	public const string CommChannelPlayers = "Comm.Channel.Players";

	/// <summary>
	/// True when <paramref name="package"/> names <paramref name="expected"/>. GMCP package names
	/// are matched without regard to case.
	/// </summary>
	public static bool Is(string? package, string expected) =>
		string.Equals(package, expected, StringComparison.OrdinalIgnoreCase);
}
