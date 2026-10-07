using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TelnetNegotiationCore.Attributes;
using TelnetNegotiationCore.Plugins;

namespace TelnetNegotiationCore.Protocols;

/// <summary>The questions <see cref="TerminalQueryProtocol.ProbeAsync"/> can ask a terminal.</summary>
[Flags]
public enum TerminalQueries
{
	/// <summary>Nothing beyond the device-attributes question that always ends a probe.</summary>
	None = 0,

	/// <summary>
	/// Whether the terminal draws Kitty graphics: a one-pixel query image (<c>a=q</c>), which is never
	/// stored or shown, and which only a terminal implementing the protocol answers.
	/// </summary>
	KittyGraphics = 1,

	/// <summary>The size of one character cell in pixels (<c>CSI 16 t</c>).</summary>
	CellSize = 2,

	/// <summary>The terminal's name and version (XTVERSION, <c>CSI &gt; 0 q</c>).</summary>
	Version = 4,

	/// <summary>Every question.</summary>
	All = KittyGraphics | CellSize | Version,
}

/// <summary>
/// What a terminal said about itself in answer to <see cref="TerminalQueryProtocol.ProbeAsync"/>. Every
/// field is <see langword="null"/> until the terminal has answered the question it belongs to.
/// </summary>
public sealed record TerminalReport
{
	/// <summary>The primary device attributes (DA1), the numbers after <c>CSI ?</c>.</summary>
	public IReadOnlyList<int>? DeviceAttributes { get; init; }

	/// <summary>
	/// Whether the terminal draws Kitty graphics. Known once the device attributes arrive after a probe
	/// that asked: a terminal that answered the query image first draws them, one that answered only
	/// the device attributes does not.
	/// </summary>
	public bool? KittyGraphics { get; init; }

	/// <summary>Whether the terminal draws sixel graphics: attribute 4 among the device attributes.</summary>
	public bool? Sixel => DeviceAttributes is null ? null : Contains(DeviceAttributes, 4);

	/// <summary>The name and version the terminal gave in answer to XTVERSION, such as <c>kitty(0.38.1)</c>.</summary>
	public string? Version { get; init; }

	/// <summary>The width of one character cell in pixels.</summary>
	public int? CellWidth { get; init; }

	/// <summary>The height of one character cell in pixels.</summary>
	public int? CellHeight { get; init; }

	private static bool Contains(IReadOnlyList<int> values, int value)
	{
		for (var i = 0; i < values.Count; i++)
			if (values[i] == value) return true;
		return false;
	}
}

/// <summary>
/// Asks the terminal behind a telnet connection what it can draw, and takes its answers out of the
/// input before the application sees them.
/// </summary>
/// <remarks>
/// <para>
/// Terminal questions are not telnet. They are escape sequences written into the data stream, and a
/// terminal answers in its own input — which, over telnet, is the user's input: the answer arrives in
/// the next line the user sends, in front of whatever they typed. This plugin recognises the answers
/// to the questions it asks wherever they sit in a line, removes them, and reports them through
/// <see cref="OnTerminalReport"/>. A line that held nothing else is consumed.
/// </para>
/// <para>
/// <b>Nothing is asked unless <see cref="ProbeAsync"/> is called.</b> Only a terminal emulator answers
/// these questions; a MUD client that is not one may print them. And a telnet client in line mode
/// echoes the terminal's answer through the local line discipline before the user presses Enter, so the
/// user can see it. When to ask is therefore the application's decision, made per connection — when the
/// terminal type names a terminal emulator, or when the user asks for it.
/// </para>
/// <para>
/// The questions asked, and the answers recognised:
/// </para>
/// <list type="table">
/// <item><term>Kitty graphics</term><description><c>ESC _ G i=31,s=1,v=1,a=q,t=d,f=24;AAAA ESC \</c>, answered by any <c>ESC _ G … ESC \</c>.</description></item>
/// <item><term>Cell size</term><description><c>CSI 16 t</c>, answered by <c>CSI 6 ; height ; width t</c>.</description></item>
/// <item><term>Version</term><description><c>CSI &gt; 0 q</c>, answered by <c>ESC P &gt; | text ESC \</c>.</description></item>
/// <item><term>Device attributes</term><description><c>CSI c</c>, always asked last, answered by <c>CSI ? n ; … c</c>. Every terminal answers it, and in order, so it is how a probe knows the Kitty question went unanswered.</description></item>
/// </list>
/// </remarks>
[RequiredMethod("OnTerminalReport", Description = "Configure the callback that receives what the terminal reported (optional but recommended)")]
public class TerminalQueryProtocol : TelnetProtocolPluginBase
{
	private const char Escape = '\u001b';
	private const char Bell = '\u0007';

	/// <summary>The Kitty query: a one-pixel RGB image sent with <c>a=q</c>, so it is checked and never stored.</summary>
	public const string KittyGraphicsQuery = "\u001b_Gi=31,s=1,v=1,a=q,t=d,f=24;AAAA\u001b\\";

	/// <summary>The cell-size question, <c>CSI 16 t</c>.</summary>
	public const string CellSizeQuery = "\u001b[16t";

	/// <summary>The version question, XTVERSION.</summary>
	public const string VersionQuery = "\u001b[>0q";

	/// <summary>The primary device-attributes question, DA1.</summary>
	public const string DeviceAttributesQuery = "\u001b[c";

	private readonly object _gate = new();
	private TerminalReport _report = new();
	private bool _awaitingKitty;
	private bool _sawKitty;
	private Func<TerminalReport, ValueTask>? _onTerminalReport;
	private volatile bool _disposed;

	/// <inheritdoc />
	public override Type ProtocolType => typeof(TerminalQueryProtocol);

	/// <inheritdoc />
	public override string ProtocolName => "Terminal queries";

	/// <summary>Everything the terminal has reported so far on this connection.</summary>
	public TerminalReport Report
	{
		get { lock (_gate) return _report; }
	}

	/// <summary>
	/// Called each time a line carries at least one answer, with everything reported so far, after the
	/// answers have been taken out of the line.
	/// </summary>
	/// <param name="callback">Receives the report.</param>
	/// <returns>This instance for fluent chaining</returns>
	public TerminalQueryProtocol OnTerminalReport(Func<TerminalReport, ValueTask>? callback)
	{
		_onTerminalReport = callback;
		return this;
	}

	/// <summary>
	/// Asks the terminal <paramref name="queries"/>, then for its device attributes. The answers arrive
	/// with the user's next line, if the terminal gives any.
	/// </summary>
	/// <param name="queries">The questions to ask.</param>
	/// <exception cref="InvalidOperationException">
	/// This interpreter is in client mode — the peer of a client is a server, which has no terminal to
	/// ask — or the plugin is disabled on this connection.
	/// </exception>
	/// <exception cref="ObjectDisposedException">The plugin has been disposed.</exception>
	public async ValueTask ProbeAsync(TerminalQueries queries = TerminalQueries.All)
	{
		if (_disposed) throw new ObjectDisposedException(nameof(TerminalQueryProtocol));

		if (Context.Mode != Interpreters.TelnetInterpreter.TelnetMode.Server)
		{
			throw new InvalidOperationException(
				"Only a server asks the terminal behind a connection what it can draw; a client's peer has no terminal.");
		}

		if (!IsEnabled)
		{
			throw new InvalidOperationException($"{nameof(TerminalQueryProtocol)} is disabled on this connection, so it will not ask.");
		}

		var probe = new StringBuilder();
		if ((queries & TerminalQueries.KittyGraphics) != 0) probe.Append(KittyGraphicsQuery);
		if ((queries & TerminalQueries.CellSize) != 0) probe.Append(CellSizeQuery);
		if ((queries & TerminalQueries.Version) != 0) probe.Append(VersionQuery);
		probe.Append(DeviceAttributesQuery);

		lock (_gate)
		{
			// A question asked again starts over: what the terminal said to the last one says nothing about
			// whether it will answer this one.
			if ((queries & TerminalQueries.KittyGraphics) != 0)
			{
				_awaitingKitty = true;
				_sawKitty = false;
			}
		}

		Context.Logger.LogDebug("Asking the terminal what it can draw ({Queries})", queries);
		await Context.SendNegotiationAsync(Encoding.ASCII.GetBytes(probe.ToString()));
	}

	/// <inheritdoc />
	/// <remarks>No states or triggers: the answers are read from the assembled-line path, which is what lets this remove them.</remarks>
	public override void ConfigureStateMachine(IProtocolContext context)
	{
		context.Interpreter.RegisterInputLineObserver(async (line, encoding) => await OnInputLineAsync(line, encoding));
	}

	/// <inheritdoc />
	/// <remarks>Nothing to negotiate: registering the plugin is the whole of its consent, as with <see cref="PuebloProtocol"/>.</remarks>
	protected override async ValueTask OnInitializeAsync()
	{
		Context.Logger.LogInformation("Terminal query protocol initialized");
		await OnNegotiatedAsync(true);
	}

	/// <inheritdoc />
	protected override async ValueTask OnProtocolDisabledAsync() => await OnNegotiatedAsync(false);

	/// <inheritdoc />
	protected override ValueTask OnDisposeAsync()
	{
		_disposed = true;
		return default;
	}

	/// <summary>One assembled line: the line without any answers in it, or null when nothing else was in it.</summary>
	private async ValueTask<byte[]?> OnInputLineAsync(byte[] line, Encoding encoding)
	{
		if (!IsEnabled || _disposed || Array.IndexOf(line, (byte)Escape) < 0) return line;

		var text = encoding.GetString(line);
		var kept = new StringBuilder(text.Length);
		var answers = new List<Answer>();
		var position = 0;

		while (position < text.Length)
		{
			if (text[position] == Escape && TryReadAnswer(text, position, out var length, out var answer))
			{
				answers.Add(answer);
				position += length;
				continue;
			}

			kept.Append(text[position]);
			position++;
		}

		if (answers.Count == 0) return line;

		TerminalReport report;
		lock (_gate)
		{
			foreach (var answer in answers) _report = Apply(_report, answer);
			report = _report;
		}

		Context.Logger.LogDebug("The terminal answered {Count} question(s)", answers.Count);

		if (_onTerminalReport is not null) await _onTerminalReport(report);

		var rest = kept.ToString();
		return rest.Trim('\r', '\n').Length == 0 ? null : encoding.GetBytes(rest);
	}

	/// <summary>Folds one answer into the report. Called under <see cref="_gate"/>.</summary>
	private TerminalReport Apply(TerminalReport report, Answer answer)
	{
		switch (answer.Kind)
		{
			case AnswerKind.Kitty:
				_sawKitty = true;
				return report with { KittyGraphics = true };
			case AnswerKind.CellSize:
				return report with { CellHeight = answer.First, CellWidth = answer.Second };
			case AnswerKind.Version:
				return report with { Version = answer.Text };
			case AnswerKind.DeviceAttributes:
				var updated = report with { DeviceAttributes = answer.Numbers };
				if (_awaitingKitty)
				{
					// Terminals answer in the order they were asked, and this was asked last: no Kitty answer
					// by now means none is coming.
					_awaitingKitty = false;
					updated = updated with { KittyGraphics = _sawKitty };
				}

				return updated;
			default:
				return report;
		}
	}

	private enum AnswerKind
	{
		Kitty,
		CellSize,
		Version,
		DeviceAttributes,
	}

	private readonly struct Answer
	{
		public Answer(AnswerKind kind, int first = 0, int second = 0, string? text = null, IReadOnlyList<int>? numbers = null)
		{
			Kind = kind;
			First = first;
			Second = second;
			Text = text;
			Numbers = numbers;
		}

		public AnswerKind Kind { get; }
		public int First { get; }
		public int Second { get; }
		public string? Text { get; }
		public IReadOnlyList<int>? Numbers { get; }
	}

	/// <summary>
	/// Reads the answer starting at <paramref name="start"/>, an escape. Only the shapes this plugin asks
	/// for are taken; any other escape sequence is the user's and is left where it is.
	/// </summary>
	private static bool TryReadAnswer(string text, int start, out int length, out Answer answer)
	{
		length = 0;
		answer = default;
		if (start + 1 >= text.Length) return false;

		switch (text[start + 1])
		{
			case '_':
				// APC: a Kitty graphics answer is ESC _ G ... ST.
				if (start + 2 < text.Length && text[start + 2] == 'G' && TryFindStringEnd(text, start + 3, out var apcEnd))
				{
					length = apcEnd - start;
					answer = new Answer(AnswerKind.Kitty);
					return true;
				}

				return false;
			case 'P':
				// DCS: XTVERSION is ESC P > | text ST.
				if (start + 3 < text.Length && text[start + 2] == '>' && text[start + 3] == '|'
					&& TryFindStringEnd(text, start + 4, out var dcsEnd))
				{
					length = dcsEnd - start;
					var bodyEnd = dcsEnd - (text[dcsEnd - 1] == Bell ? 1 : 2);
					answer = new Answer(AnswerKind.Version, text: text.Substring(start + 4, bodyEnd - (start + 4)));
					return true;
				}

				return false;
			case '[':
				return TryReadControlAnswer(text, start, out length, out answer);
			default:
				return false;
		}
	}

	/// <summary>A CSI answer: device attributes (<c>CSI ? … c</c>) or the cell size (<c>CSI 6 ; h ; w t</c>).</summary>
	private static bool TryReadControlAnswer(string text, int start, out int length, out Answer answer)
	{
		length = 0;
		answer = default;

		var position = start + 2;
		var privateMarker = position < text.Length && text[position] == '?';
		if (privateMarker) position++;

		var numbers = new List<int>();
		var current = -1;
		while (position < text.Length)
		{
			var c = text[position];
			if (c >= '0' && c <= '9')
			{
				current = (current < 0 ? 0 : current * 10) + (c - '0');
				if (current > 1_000_000) return false;
			}
			else if (c == ';')
			{
				numbers.Add(current < 0 ? 0 : current);
				current = -1;
			}
			else
			{
				break;
			}

			position++;
		}

		if (position >= text.Length) return false;
		if (current >= 0) numbers.Add(current);

		var final = text[position];
		if (privateMarker && final == 'c' && numbers.Count > 0)
		{
			length = position + 1 - start;
			answer = new Answer(AnswerKind.DeviceAttributes, numbers: numbers);
			return true;
		}

		if (!privateMarker && final == 't' && numbers.Count == 3 && numbers[0] == 6 && numbers[1] > 0 && numbers[2] > 0)
		{
			length = position + 1 - start;
			answer = new Answer(AnswerKind.CellSize, first: numbers[1], second: numbers[2]);
			return true;
		}

		return false;
	}

	/// <summary>
	/// The index just past the string terminator — <c>ESC \</c>, or BEL, which some terminals use — of a
	/// control string whose body starts at <paramref name="from"/>.
	/// </summary>
	private static bool TryFindStringEnd(string text, int from, out int end)
	{
		for (var i = from; i < text.Length; i++)
		{
			if (text[i] == Bell)
			{
				end = i + 1;
				return true;
			}

			if (text[i] == Escape && i + 1 < text.Length && text[i + 1] == '\\')
			{
				end = i + 2;
				return true;
			}
		}

		end = 0;
		return false;
	}
}
