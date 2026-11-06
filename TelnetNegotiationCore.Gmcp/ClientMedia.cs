using System;
using System.Globalization;
using System.Text.Json.Nodes;

namespace TelnetNegotiationCore.Gmcp;

/// <summary>
/// The kinds of media <c>Client.Media</c> plays.
/// </summary>
public enum MediaType
{
	/// <summary>A sound effect, the default.</summary>
	Sound,

	/// <summary>Background music.</summary>
	Music,

	/// <summary>A video.</summary>
	Video
}

/// <summary>
/// <c>Client.Media.Default</c>: the location media files are downloaded from when a message names
/// none. "Perform a Client.Media.Default GMCP event once upon player login."
/// </summary>
/// <param name="Url">The location. Its last character must be a slash.</param>
public sealed record MediaDefault(string Url) : IGmcpMessage
{
	/// <inheritdoc />
	public string Package => GmcpPackages.ClientMediaDefault;

	/// <inheritdoc />
	public string ToJson() => new JsonFieldWriter().Add("url", Url).ToString();

	/// <summary>Reads a <c>Client.Media.Default</c> body.</summary>
	public static bool TryParse(string? data, out MediaDefault message) =>
		JsonFieldReader.TryRead(data, fields => new MediaDefault(fields.String("url") ?? ""), out message)
		&& message.Url.Length > 0;
}

/// <summary>
/// <c>Client.Media.Load</c>: download a media file now, so it is ready when played.
/// </summary>
/// <param name="Name">The file name, which may include directories, such as <c>weather/lightning.mp3</c>.</param>
/// <param name="Url">Where to download it from, when <see cref="MediaDefault"/> did not say.</param>
public sealed record MediaLoad(string Name, string? Url = null) : IGmcpMessage
{
	/// <inheritdoc />
	public string Package => GmcpPackages.ClientMediaLoad;

	/// <inheritdoc />
	public string ToJson() => new JsonFieldWriter().Add("name", Name).Add("url", Url).ToString();

	/// <summary>Reads a <c>Client.Media.Load</c> body.</summary>
	public static bool TryParse(string? data, out MediaLoad message) =>
		JsonFieldReader.TryRead(data, fields => new MediaLoad(fields.String("name") ?? "", fields.String("url")), out message)
		&& message.Name.Length > 0;
}

/// <summary>
/// <c>Client.Media.Play</c>: play a sound, music or video. Only <see cref="Name"/> is required;
/// every field left null is left out, and the client applies its default.
/// </summary>
/// <param name="Name">The file name, which may include directories.</param>
public sealed record MediaPlay(string Name) : IGmcpMessage
{
	/// <summary>Where to download the file from, when <see cref="MediaDefault"/> or <see cref="MediaLoad"/> did not say.</summary>
	public string? Url { get; init; }

	/// <summary>Sound, music or video. The client's default is sound.</summary>
	public MediaType? Type { get; init; }

	/// <summary>A category the client can stop by, such as <c>environment</c>.</summary>
	public string? Tag { get; init; }

	/// <summary>1 to 100, relative to the client's own volume. The default is 50.</summary>
	public int? Volume { get; init; }

	/// <summary>Milliseconds over which to fade in from the start.</summary>
	public int? FadeIn { get; init; }

	/// <summary>Milliseconds over which to fade out before the end.</summary>
	public int? FadeOut { get; init; }

	/// <summary>Where to start, in milliseconds.</summary>
	public int? Start { get; init; }

	/// <summary>Where to stop, in milliseconds.</summary>
	public int? Finish { get; init; }

	/// <summary>How many times to play: 1 or more, or -1 to loop until stopped.</summary>
	public int? Loops { get; init; }

	/// <summary>1 to 100. Media of lower priority is halted while this plays.</summary>
	public int? Priority { get; init; }

	/// <summary>For music: true keeps the same file playing when it is played again, false restarts it.</summary>
	public bool? Continue { get; init; }

	/// <summary>Identifies a slot: playing a different file under the same key stops the one before.</summary>
	public string? Key { get; init; }

	/// <summary>A caption for the sound, such as <c>thunderclap</c>, for players who cannot hear it.</summary>
	public string? Caption { get; init; }

	/// <summary>
	/// MCMP version 2: where the sound comes from. Unset, it plays as recorded. A version 1 client
	/// ignores it, so it is safe to send to every client.
	/// </summary>
	public MediaDirection? Direction { get; init; }

	/// <summary>
	/// MCMP version 2: -100 (left) to 100 (right), 0 being centred. A client that places sounds in 3D
	/// uses <see cref="Direction"/> when both are set; a stereo client uses this.
	/// </summary>
	public int? Pan { get; init; }

	/// <summary>
	/// Play from <see cref="Url"/> without downloading, such as internet radio. Optional for clients:
	/// send it only to one known to support it, as one that ignores it may try to download the stream.
	/// </summary>
	public bool? Stream { get; init; }

	/// <summary>For video: close the video window when the video ends or is stopped. Optional for clients.</summary>
	public bool? Close { get; init; }

	/// <inheritdoc />
	public string Package => GmcpPackages.ClientMediaPlay;

	/// <inheritdoc />
	public string ToJson() => new JsonFieldWriter()
		.Add("name", Name)
		.Add("url", Url)
		.Add("type", MediaTypes.Write(Type))
		.Add("tag", Tag)
		.Add("volume", Volume)
		.Add("fadein", FadeIn)
		.Add("fadeout", FadeOut)
		.Add("start", Start)
		.Add("finish", Finish)
		.Add("loops", Loops)
		.Add("priority", Priority)
		.Add("continue", Continue)
		.Add("key", Key)
		.Add("caption", Caption)
		.Add("direction", Direction?.ToNode())
		.Add("pan", Pan)
		.Add("stream", Stream)
		.Add("close", Close)
		.ToString();

	/// <summary>Reads a <c>Client.Media.Play</c> body.</summary>
	public static bool TryParse(string? data, out MediaPlay message) =>
		JsonFieldReader.TryRead(data, fields => new MediaPlay(fields.String("name") ?? "")
		{
			Url = fields.String("url"),
			Type = MediaTypes.Read(fields.String("type")),
			Tag = fields.String("tag"),
			Volume = (int?)fields.Number("volume"),
			FadeIn = (int?)fields.Number("fadein"),
			FadeOut = (int?)fields.Number("fadeout"),
			Start = (int?)fields.Number("start"),
			Finish = (int?)fields.Number("finish"),
			Loops = (int?)fields.Number("loops"),
			Priority = (int?)fields.Number("priority"),
			Continue = fields.Boolean("continue"),
			Key = fields.String("key"),
			Caption = fields.String("caption"),
			Direction = MediaDirection.Read(fields.String("direction")),
			Pan = (int?)fields.Number("pan"),
			Stream = fields.Boolean("stream"),
			Close = fields.Boolean("close")
		}, out message) && message.Name.Length > 0;
}

/// <summary>
/// <c>Client.Media.Stop</c>: stop media matching every field that is set. With nothing set it
/// stops all media: "An empty body will stop all media."
/// </summary>
public sealed record MediaStop : IGmcpMessage
{
	/// <summary>Stops media with this file name.</summary>
	public string? Name { get; init; }

	/// <summary>Stops media of this type.</summary>
	public MediaType? Type { get; init; }

	/// <summary>Stops media with this tag.</summary>
	public string? Tag { get; init; }

	/// <summary>Stops media of this priority or lower.</summary>
	public int? Priority { get; init; }

	/// <summary>Stops media with this key.</summary>
	public string? Key { get; init; }

	/// <summary>Fades the volume down before stopping, rather than cutting off.</summary>
	public bool? FadeAway { get; init; }

	/// <summary>Milliseconds to fade over, when the media's own <see cref="MediaPlay.FadeOut"/> was not set.</summary>
	public int? FadeOut { get; init; }

	/// <inheritdoc />
	public string Package => GmcpPackages.ClientMediaStop;

	/// <inheritdoc />
	public string ToJson() => new JsonFieldWriter()
		.Add("name", Name)
		.Add("type", MediaTypes.Write(Type))
		.Add("tag", Tag)
		.Add("priority", Priority)
		.Add("key", Key)
		.Add("fadeaway", FadeAway)
		.Add("fadeout", FadeOut)
		.ToString();

	/// <summary>Reads a <c>Client.Media.Stop</c> body. An empty body reads as "stop everything".</summary>
	public static bool TryParse(string? data, out MediaStop message)
	{
		if (string.IsNullOrWhiteSpace(data))
		{
			message = new MediaStop();
			return true;
		}

		return JsonFieldReader.TryRead(data, fields => new MediaStop
		{
			Name = fields.String("name"),
			Type = MediaTypes.Read(fields.String("type")),
			Tag = fields.String("tag"),
			Priority = (int?)fields.Number("priority"),
			Key = fields.String("key"),
			FadeAway = fields.Boolean("fadeaway"),
			FadeOut = (int?)fields.Number("fadeout")
		}, out message);
	}
}

/// <summary>
/// <c>Client.Media.Pause</c>, MCMP version 2: pause media matching every field that is set. With
/// nothing set it pauses all media. A later <see cref="MediaPlay"/> that matches the paused media
/// resumes it from where it stopped; <see cref="MediaStop"/> stops paused media too. A version 1
/// client ignores it.
/// </summary>
public sealed record MediaPause : IGmcpMessage
{
	/// <summary>Pauses media with this file name.</summary>
	public string? Name { get; init; }

	/// <summary>Pauses media of this type.</summary>
	public MediaType? Type { get; init; }

	/// <summary>Pauses media with this tag.</summary>
	public string? Tag { get; init; }

	/// <summary>Pauses media of lower priority than this, 1 to 100. Unlike <see cref="MediaStop.Priority"/>, media of this priority is not paused.</summary>
	public int? Priority { get; init; }

	/// <summary>Pauses media with this key.</summary>
	public string? Key { get; init; }

	/// <inheritdoc />
	public string Package => GmcpPackages.ClientMediaPause;

	/// <inheritdoc />
	public string ToJson() => new JsonFieldWriter()
		.Add("name", Name)
		.Add("type", MediaTypes.Write(Type))
		.Add("tag", Tag)
		.Add("priority", Priority)
		.Add("key", Key)
		.ToString();

	/// <summary>Reads a <c>Client.Media.Pause</c> body. An empty body reads as "pause everything".</summary>
	public static bool TryParse(string? data, out MediaPause message)
	{
		if (string.IsNullOrWhiteSpace(data))
		{
			message = new MediaPause();
			return true;
		}

		return JsonFieldReader.TryRead(data, fields => new MediaPause
		{
			Name = fields.String("name"),
			Type = MediaTypes.Read(fields.String("type")),
			Tag = fields.String("tag"),
			Priority = (int?)fields.Number("priority"),
			Key = fields.String("key")
		}, out message);
	}
}

/// <summary>
/// <c>Client.Media.Update</c>, MCMP version 2: change a playing sound's volume or position without
/// restarting it. It selects by <see cref="Key"/>, <see cref="Name"/> and <see cref="Tag"/>, of which
/// at least one must be set, and all that are set must match. Every other field left null is left
/// unchanged.
/// </summary>
/// <remarks>
/// A new <see cref="Direction"/> clears an earlier pan; a <see cref="Pan"/> alone leaves the
/// direction as it was. An update sent before the file has loaded applies once it plays. To move
/// copies of one sound apart, give each its own <see cref="MediaPlay.Key"/>.
/// </remarks>
public sealed record MediaUpdate : IGmcpMessage
{
	/// <summary>Updates media with this key.</summary>
	public string? Key { get; init; }

	/// <summary>Updates media with this file name.</summary>
	public string? Name { get; init; }

	/// <summary>Updates media with this tag.</summary>
	public string? Tag { get; init; }

	/// <summary>Narrows the selection to this type. On its own it selects nothing.</summary>
	public MediaType? Type { get; init; }

	/// <summary>The new volume, 1 to 100.</summary>
	public int? Volume { get; init; }

	/// <summary>Where the sound now comes from.</summary>
	public MediaDirection? Direction { get; init; }

	/// <summary>The new pan, -100 (left) to 100 (right).</summary>
	public int? Pan { get; init; }

	/// <summary>Milliseconds over which to glide to the new values. The client's default is 0.</summary>
	public int? Duration { get; init; }

	/// <summary>True when <see cref="Key"/>, <see cref="Name"/> or <see cref="Tag"/> is set, without which a client selects nothing.</summary>
	public bool SelectsMedia => !string.IsNullOrEmpty(Key) || !string.IsNullOrEmpty(Name) || !string.IsNullOrEmpty(Tag);

	/// <inheritdoc />
	public string Package => GmcpPackages.ClientMediaUpdate;

	/// <inheritdoc />
	public string ToJson() => new JsonFieldWriter()
		.Add("key", Key)
		.Add("name", Name)
		.Add("tag", Tag)
		.Add("type", MediaTypes.Write(Type))
		.Add("volume", Volume)
		.Add("direction", Direction?.ToNode())
		.Add("pan", Pan)
		.Add("duration", Duration)
		.ToString();

	/// <summary>Reads a <c>Client.Media.Update</c> body. One that selects nothing is refused.</summary>
	public static bool TryParse(string? data, out MediaUpdate message) =>
		JsonFieldReader.TryRead(data, fields => new MediaUpdate
		{
			Key = fields.String("key"),
			Name = fields.String("name"),
			Tag = fields.String("tag"),
			Type = MediaTypes.Read(fields.String("type")),
			Volume = (int?)fields.Number("volume"),
			Direction = MediaDirection.Read(fields.String("direction")),
			Pan = (int?)fields.Number("pan"),
			Duration = (int?)fields.Number("duration")
		}, out message) && message.SelectsMedia;
}

/// <summary>
/// <c>Client.Media.Listener</c>, MCMP version 2: the way the player faces, so compass directions are
/// heard relative to it. Playing sounds turn with it, except those placed by pan alone.
/// </summary>
/// <remarks>
/// The facing lasts for the connection: after a reconnect the client faces north until this is sent
/// again. A client ignores a facing it cannot read and keeps the one before.
/// </remarks>
/// <param name="Facing">A compass word other than <c>up</c> or <c>down</c>, or degrees clockwise from north.</param>
public sealed record MediaListener(MediaDirection Facing) : IGmcpMessage
{
	/// <inheritdoc />
	public string Package => GmcpPackages.ClientMediaListener;

	/// <inheritdoc />
	public string ToJson() => new JsonFieldWriter().Add("facing", Facing.ToNode()).ToString();

	/// <summary>
	/// Reads a <c>Client.Media.Listener</c> body. A facing that is not degrees or a compass word
	/// other than <c>up</c> or <c>down</c> is refused, so the caller keeps the facing it had.
	/// </summary>
	public static bool TryParse(string? data, out MediaListener message)
	{
		message = null!;

		if (!JsonFieldReader.TryRead(data, fields => MediaDirection.Read(fields.String("facing")), out var facing)
			|| facing is null
			|| facing.Angle is null
			|| facing.IsRelative)
		{
			return false;
		}

		message = new MediaListener(facing);
		return true;
	}
}

/// <summary>
/// Where a sound comes from, or which way the player faces, in MCMP version 2: a word, or degrees
/// clockwise from north. A string or an int converts to one, so <c>Direction = "north"</c> and
/// <c>Direction = 90</c> both work.
/// </summary>
/// <remarks>
/// The words are the compass points <c>north</c> to <c>northwest</c> with their short forms
/// <c>n</c> to <c>nw</c>, <c>up</c>/<c>u</c> and <c>down</c>/<c>d</c>, and <c>ahead</c>,
/// <c>right</c>, <c>behind</c> and <c>left</c>, which are relative to the player and ignore the
/// facing. Words are read without regard to case; a client plays a sound whose word it does not know
/// as recorded.
/// </remarks>
public sealed record MediaDirection
{
	private MediaDirection(string? word, int? degrees)
	{
		Word = word;
		Degrees = degrees;
	}

	/// <summary>The word, when given as one.</summary>
	public string? Word { get; }

	/// <summary>The degrees as given, when given as a number. Values outside 0 to 359 wrap around.</summary>
	public int? Degrees { get; }

	/// <summary>A direction given as a word, such as <c>north</c>, <c>ne</c> or <c>behind</c>.</summary>
	public static MediaDirection FromWord(string word) => new(word, null);

	/// <summary>A direction given as degrees clockwise from north.</summary>
	public static MediaDirection FromDegrees(int degrees) => new(null, degrees);

	/// <summary>A direction given as a word.</summary>
	public static implicit operator MediaDirection(string word) => FromWord(word);

	/// <summary>A direction given as degrees clockwise from north.</summary>
	public static implicit operator MediaDirection(int degrees) => FromDegrees(degrees);

	/// <summary>True for <c>ahead</c>, <c>right</c>, <c>behind</c> and <c>left</c>, which ignore the listener's facing.</summary>
	public bool IsRelative => Normalized is "ahead" or "right" or "behind" or "left";

	/// <summary>
	/// The angle 0 to 359, clockwise from north, or from straight ahead when <see cref="IsRelative"/>.
	/// Null for <c>up</c>, <c>down</c> and words the specification does not name.
	/// </summary>
	public int? Angle => Degrees is { } degrees
		? ((degrees % 360) + 360) % 360
		: Normalized switch
		{
			"north" or "n" or "ahead" => 0,
			"northeast" or "ne" => 45,
			"east" or "e" or "right" => 90,
			"southeast" or "se" => 135,
			"south" or "s" or "behind" => 180,
			"southwest" or "sw" => 225,
			"west" or "w" or "left" => 270,
			"northwest" or "nw" => 315,
			_ => null
		};

	/// <summary>True for <c>up</c>/<c>u</c> and <c>down</c>/<c>d</c>.</summary>
	public bool IsVertical => Normalized is "up" or "u" or "down" or "d";

	private string? Normalized => Word?.Trim().ToLowerInvariant();

	/// <inheritdoc />
	public override string ToString() => Degrees?.ToString(CultureInfo.InvariantCulture) ?? Word ?? "";

	internal JsonNode? ToNode() => Degrees is { } degrees ? JsonValue.Create(degrees) : JsonValue.Create(Word);

	/// <summary>
	/// Reads a direction field: a number, or a number in a string as some servers send, is degrees,
	/// rounded to a whole degree; any other text is a word.
	/// </summary>
	internal static MediaDirection? Read(string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return null;
		}

		if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var degrees))
		{
			return FromDegrees(degrees);
		}

		// 90.5 and 1e2 are numbers too. NaN and Infinity parse as doubles but are no direction.
		if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
			&& !double.IsNaN(number) && !double.IsInfinity(number))
		{
			return FromDegrees((int)Math.Round(number % 360));
		}

		return FromWord(value!.Trim());
	}
}

internal static class MediaTypes
{
	public static string? Write(MediaType? type) => type switch
	{
		MediaType.Sound => "sound",
		MediaType.Music => "music",
		MediaType.Video => "video",
		_ => null
	};

	public static MediaType? Read(string? type) => type?.Trim().ToLowerInvariant() switch
	{
		"sound" => MediaType.Sound,
		"music" => MediaType.Music,
		"video" => MediaType.Video,
		_ => null
	};
}
