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
			Caption = fields.String("caption")
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
/// <c>Client.Media.Pause</c>: pause media matching every field that is set. A Mudlet extension to
/// the specification. A later <see cref="MediaPlay"/> that matches the paused media resumes it.
/// </summary>
/// <remarks>Mudlet ignores a pause with no field set, so set at least one.</remarks>
public sealed record MediaPause : IGmcpMessage
{
	/// <summary>Pauses media with this file name.</summary>
	public string? Name { get; init; }

	/// <summary>Pauses media of this type.</summary>
	public MediaType? Type { get; init; }

	/// <summary>Pauses media with this tag.</summary>
	public string? Tag { get; init; }

	/// <summary>Pauses media of this priority or lower.</summary>
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

	/// <summary>Reads a <c>Client.Media.Pause</c> body.</summary>
	public static bool TryParse(string? data, out MediaPause message) =>
		JsonFieldReader.TryRead(data, fields => new MediaPause
		{
			Name = fields.String("name"),
			Type = MediaTypes.Read(fields.String("type")),
			Tag = fields.String("tag"),
			Priority = (int?)fields.Number("priority"),
			Key = fields.String("key")
		}, out message);
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
