using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace TelnetNegotiationCore.Gmcp;

/// <summary>The kinds of frame <c>mudstd.frame</c> names, as they are written.</summary>
public static class FrameTypes
{
	/// <summary>A window outside the client's main area.</summary>
	public const string External = "external";

	/// <summary>Docked to an edge of the main area, which shrinks to make room.</summary>
	public const string Docked = "docked";

	/// <summary>Floating over the main area.</summary>
	public const string Floating = "floating";

	/// <summary>Split off inside another frame, its <see cref="FrameOpen.Parent"/>.</summary>
	public const string Child = "child";

	/// <summary>A tab shown instead of another frame, its <see cref="FrameOpen.Parent"/>.</summary>
	public const string Tab = "tab";
}

/// <summary>What a <c>mudstd.frame</c> frame shows, as it is written.</summary>
public static class FrameContents
{
	/// <summary>ANSI text, like the main window. Written to with <see cref="FrameTerminal"/>.</summary>
	public const string Terminal = "terminal";

	/// <summary>A web page that can send and receive GMCP. Opened at <see cref="FrameOpen.Url"/>.</summary>
	public const string WebView = "webview";

	/// <summary>One image. Set with <see cref="FrameImage"/>.</summary>
	public const string Image = "image";
}

/// <summary>
/// <c>mudstd.frame.support</c>: the frame kinds and contents the client supports. The client sends
/// it after connecting. A proposal.
/// </summary>
/// <param name="Types">Values from <see cref="FrameTypes"/>.</param>
/// <param name="Content">Values from <see cref="FrameContents"/>.</param>
public sealed record FrameSupport(IReadOnlyList<string> Types, IReadOnlyList<string> Content) : IGmcpMessage
{
	/// <inheritdoc />
	public string Package => GmcpPackages.MudstdFrameSupport;

	/// <inheritdoc />
	public string ToJson() => new JsonFieldWriter().Add("type", Types).Add("content", Content).ToString();

	/// <summary>Reads a <c>mudstd.frame.support</c> body.</summary>
	public static bool TryParse(string? data, out FrameSupport message) =>
		JsonFieldReader.TryRead(data, fields => new FrameSupport(fields.Strings("type") ?? [], fields.Strings("content") ?? []), out message);
}

/// <summary>Optional settings for a frame. A client need not honor any of them.</summary>
public sealed record FrameDetails
{
	/// <summary>The URL of a background image.</summary>
	public string? Background { get; init; }

	/// <summary>Background opacity, 0 to 100.</summary>
	public int? Opacity { get; init; }

	/// <summary>Which way it scrolls: <c>none</c>, <c>X</c>, <c>Y</c> or <c>both</c>.</summary>
	public string? Scrolling { get; init; }

	/// <summary>Which way the player can resize it: <c>none</c>, <c>X</c>, <c>Y</c> or <c>both</c>.</summary>
	public string? Resizeable { get; init; }

	/// <summary>Whether the player can close it.</summary>
	public bool? Closeable { get; init; }

	/// <summary>A label.</summary>
	public string? Label { get; init; }

	internal JsonObject ToNode() => new JsonFieldWriter()
		.Add("background", Background)
		.Add("opacity", Opacity)
		.Add("scrolling", Scrolling)
		.Add("resizeable", Resizeable)
		.Add("closeable", Closeable)
		.Add("label", Label)
		.ToNode();

	internal static FrameDetails Read(JsonFieldReader fields) => new()
	{
		Background = fields.String("background"),
		Opacity = (int?)fields.Number("opacity"),
		Scrolling = fields.String("scrolling"),
		Resizeable = fields.String("resizeable"),
		Closeable = fields.Boolean("closeable"),
		Label = fields.String("label")
	};
}

/// <summary><c>mudstd.frame.open</c>: opens a frame in the client. A proposal.</summary>
/// <param name="Id">Identifies the frame in later messages.</param>
/// <param name="Type">A value from <see cref="FrameTypes"/>.</param>
public sealed record FrameOpen(string Id, string Type) : IGmcpMessage
{
	/// <summary>A value from <see cref="FrameContents"/>.</summary>
	public string? Content { get; init; }

	/// <summary>Which side of the reference frame it appears on: <c>top</c>, <c>bottom</c>, <c>left</c> or <c>right</c>.</summary>
	public string? Align { get; init; }

	/// <summary>A title. Without one the client reserves no room for a title.</summary>
	public string? Label { get; init; }

	/// <summary>For a child, the frame it is inside; for a tab, the frame it alternates with.</summary>
	public string? Parent { get; init; }

	/// <summary>The frame's size, in <see cref="SizeUnit"/>.</summary>
	public long? SizeValue { get; init; }

	/// <summary><c>c</c> for characters, <c>px</c> for pixels, or <c>%</c>.</summary>
	public string? SizeUnit { get; init; }

	/// <summary>For a web view, the page to open.</summary>
	public string? Url { get; init; }

	/// <summary>Further settings.</summary>
	public FrameDetails? Details { get; init; }

	/// <inheritdoc />
	public string Package => GmcpPackages.MudstdFrameOpen;

	/// <inheritdoc />
	public string ToJson() => new JsonFieldWriter()
		.Add("id", Id)
		.Add("type", Type)
		.Add("content", Content)
		.Add("align", Align)
		.Add("label", Label)
		.Add("parent", Parent)
		.Add("sizeValue", SizeValue)
		.Add("sizeUnit", SizeUnit)
		.Add("url", Url)
		.Add("details", Details?.ToNode())
		.ToString();

	/// <summary>Reads a <c>mudstd.frame.open</c> body. <c>sizeValue</c> is read from a number or a string.</summary>
	public static bool TryParse(string? data, out FrameOpen message) =>
		JsonFieldReader.TryRead(data, fields => new FrameOpen(fields.String("id") ?? "", fields.String("type") ?? "")
		{
			Content = fields.String("content"),
			Align = fields.String("align"),
			Label = fields.String("label"),
			Parent = fields.String("parent"),
			SizeValue = fields.Number("sizeValue"),
			SizeUnit = fields.String("sizeUnit"),
			Url = fields.String("url"),
			Details = fields.Object("details", FrameDetails.Read)
		}, out message) && message.Id.Length > 0;
}

/// <summary><c>mudstd.frame.close</c>: asks the client to close a frame.</summary>
/// <param name="Id">The frame.</param>
public sealed record FrameClose(string Id) : IGmcpMessage
{
	/// <inheritdoc />
	public string Package => GmcpPackages.MudstdFrameClose;

	/// <inheritdoc />
	public string ToJson() => new JsonFieldWriter().Add("id", Id).ToString();

	/// <summary>Reads a <c>mudstd.frame.close</c> body.</summary>
	public static bool TryParse(string? data, out FrameClose message) =>
		JsonFieldReader.TryRead(data, fields => new FrameClose(fields.String("id") ?? ""), out message) && message.Id.Length > 0;
}

/// <summary><c>mudstd.frame.terminal</c>: writes text, which may carry ANSI codes, to a terminal frame.</summary>
/// <param name="Id">The frame.</param>
/// <param name="Ansi">The text.</param>
/// <param name="Clear">True clears the frame first.</param>
public sealed record FrameTerminal(string Id, string Ansi, bool? Clear = null) : IGmcpMessage
{
	/// <inheritdoc />
	public string Package => GmcpPackages.MudstdFrameTerminal;

	/// <inheritdoc />
	public string ToJson() => new JsonFieldWriter().Add("id", Id).Add("clear", Clear).Add("ansi", Ansi).ToString();

	/// <summary>Reads a <c>mudstd.frame.terminal</c> body.</summary>
	public static bool TryParse(string? data, out FrameTerminal message) =>
		JsonFieldReader.TryRead(data, fields => new FrameTerminal(fields.String("id") ?? "", fields.String("ansi") ?? "", fields.Boolean("clear")), out message)
		&& message.Id.Length > 0;
}

/// <summary><c>mudstd.frame.image</c>: sets the image of an image frame.</summary>
/// <param name="Id">The frame.</param>
/// <param name="Image">An image URL, or <c>base64:</c> and the image's data.</param>
public sealed record FrameImage(string Id, string Image) : IGmcpMessage
{
	/// <inheritdoc />
	public string Package => GmcpPackages.MudstdFrameImage;

	/// <inheritdoc />
	public string ToJson() => new JsonFieldWriter().Add("id", Id).Add("image", Image).ToString();

	/// <summary>Reads a <c>mudstd.frame.image</c> body.</summary>
	public static bool TryParse(string? data, out FrameImage message) =>
		JsonFieldReader.TryRead(data, fields => new FrameImage(fields.String("id") ?? "", fields.String("image") ?? ""), out message)
		&& message.Id.Length > 0;
}

/// <summary>A frame's size in characters or pixels.</summary>
/// <param name="Width">The usable width.</param>
/// <param name="Height">The usable height.</param>
public sealed record FrameSize(long Width, long Height)
{
	internal JsonObject ToNode() => new JsonFieldWriter().Add("width", Width).Add("height", Height).ToNode();

	internal static FrameSize Read(JsonFieldReader fields) => new(fields.Number("width") ?? 0, fields.Number("height") ?? 0);
}

/// <summary>
/// <c>mudstd.frame.opened</c> or <c>mudstd.frame.resized</c>: the client opened, reopened or
/// resized a frame, and its new size.
/// </summary>
/// <param name="Id">The frame.</param>
/// <param name="SizeChar">Its size in characters.</param>
/// <param name="Resized">True for <c>mudstd.frame.resized</c>.</param>
public sealed record FrameSized(string Id, FrameSize SizeChar, bool Resized = false) : IGmcpMessage
{
	/// <summary>Its inner size in pixels, after any scaling.</summary>
	public FrameSize? SizePixel { get; init; }

	/// <inheritdoc />
	public string Package => Resized ? GmcpPackages.MudstdFrameResized : GmcpPackages.MudstdFrameOpened;

	/// <inheritdoc />
	public string ToJson() => new JsonFieldWriter()
		.Add("id", Id)
		.Add("sizeChar", SizeChar.ToNode())
		.Add("sizePixel", SizePixel?.ToNode())
		.ToString();

	/// <summary>Reads a <c>mudstd.frame.opened</c> or <c>resized</c> body.</summary>
	public static bool TryParse(string? data, out FrameSized message, bool resized = false) =>
		JsonFieldReader.TryRead(data, fields => new FrameSized(fields.String("id") ?? "", fields.Object("sizeChar", FrameSize.Read) ?? new FrameSize(0, 0), resized)
		{
			SizePixel = fields.Object("sizePixel", FrameSize.Read)
		}, out message) && message.Id.Length > 0;
}

/// <summary><c>mudstd.frame.closed</c>: the client closed a frame.</summary>
/// <param name="Id">The frame.</param>
/// <param name="Reason"><c>user</c> when the player closed it, <c>system</c> otherwise.</param>
public sealed record FrameClosed(string Id, string? Reason = null) : IGmcpMessage
{
	/// <inheritdoc />
	public string Package => GmcpPackages.MudstdFrameClosed;

	/// <inheritdoc />
	public string ToJson() => new JsonFieldWriter().Add("id", Id).Add("reason", Reason).ToString();

	/// <summary>Reads a <c>mudstd.frame.closed</c> body.</summary>
	public static bool TryParse(string? data, out FrameClosed message) =>
		JsonFieldReader.TryRead(data, fields => new FrameClosed(fields.String("id") ?? "", fields.String("reason")), out message)
		&& message.Id.Length > 0;
}
