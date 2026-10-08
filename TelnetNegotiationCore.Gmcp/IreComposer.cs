using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TelnetNegotiationCore.Gmcp;

/// <summary>
/// <c>IRE.Composer.Edit</c>: opens Mudlet's composer, a multi-line editor, on some text, such as a
/// board post or a description. Mudlet sends what the player writes back as
/// <see cref="ComposerSetBuffer"/>. Clients announce <c>IRE.Composer 1</c>.
/// </summary>
/// <param name="Title">The editor's title.</param>
/// <param name="Text">The text to start from. Empty for a new one.</param>
public sealed record ComposerEdit(string Title, string Text = "") : IGmcpMessage
{
	/// <inheritdoc />
	public string Package => GmcpPackages.IreComposerEdit;

	/// <inheritdoc />
	public string ToJson() => new JsonFieldWriter().Add("title", Title).Add("text", Text).ToString();

	/// <summary>Reads an <c>IRE.Composer.Edit</c> body. Mudlet needs both <c>title</c> and <c>text</c>.</summary>
	public static bool TryParse(string? data, out ComposerEdit message)
	{
		var complete = false;
		var read = JsonFieldReader.TryRead(data, fields =>
		{
			complete = fields.Has("title") && fields.Has("text");
			return new ComposerEdit(fields.String("title") ?? "", fields.String("text") ?? "");
		}, out message);
		return read && complete;
	}
}

/// <summary>
/// <c>IRE.Composer.SetBuffer</c>: the text the player wrote in the composer. Unlike other packages,
/// its data section is a JSON string, not an object, and Mudlet leaves it out when the text is empty.
/// </summary>
/// <param name="Text">The text.</param>
public sealed record ComposerSetBuffer(string Text) : IGmcpMessage
{
	/// <inheritdoc />
	public string Package => GmcpPackages.IreComposerSetBuffer;

	/// <inheritdoc />
	public string ToJson() => Text.Length == 0 ? "" : JsonValue.Create(Text).ToJsonString();

	/// <summary>
	/// Reads an <c>IRE.Composer.SetBuffer</c> body. No body reads as empty text. Mudlet escapes only
	/// backslashes, quotes and line feeds, so other control characters, such as a tab, are accepted
	/// unescaped.
	/// </summary>
	public static bool TryParse(string? data, out ComposerSetBuffer message)
	{
		message = new ComposerSetBuffer("");

		if (string.IsNullOrWhiteSpace(data))
		{
			return true;
		}

		var text = ReadString(data!) ?? ReadString(EscapeControlCharacters(data!));

		if (text is null)
		{
			return false;
		}

		message = new ComposerSetBuffer(text);
		return true;
	}

	private static string? ReadString(string data)
	{
		try
		{
			using var document = JsonDocument.Parse(data);
			return document.RootElement.ValueKind == JsonValueKind.String ? document.RootElement.GetString() : null;
		}
		catch (JsonException)
		{
			return null;
		}
	}

	private static string EscapeControlCharacters(string data)
	{
		var escaped = new StringBuilder(data.Length);

		foreach (var c in data)
		{
			if (c < ' ')
			{
				escaped.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
			}
			else
			{
				escaped.Append(c);
			}
		}

		return escaped.ToString();
	}
}
