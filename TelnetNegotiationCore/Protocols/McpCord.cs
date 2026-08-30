using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace TelnetNegotiationCore.Protocols;

/// <summary>
/// One open cord: a named, typed channel multiplexed over the MCP session.
/// </summary>
/// <remarks>
/// Handlers are set on the cord rather than on the plugin because a cord is the thing a consumer
/// actually has: it opened this one, or was handed this one, and what arrives on somebody else's is
/// not its business.
/// </remarks>
public sealed class McpCord
{
	private readonly McpCordProtocol _cords;
	private Func<Models.McpMessage, ValueTask>? _onMessage;
	private Func<ValueTask>? _onClosed;

	internal McpCord(McpCordProtocol cords, string id, string type)
	{
		_cords = cords;
		Id = id;
		Type = type;
	}

	/// <summary>
	/// The cord's identifier, unique within the session. Case-sensitive, as MCP's are.
	/// </summary>
	public string Id { get; }

	/// <summary>The cord type, which is what says who should be listening on it.</summary>
	public string Type { get; }

	/// <summary>Whether this cord is still open. False once either end has closed it.</summary>
	public bool IsOpen { get; internal set; } = true;

	/// <summary>
	/// Whether the peer opened this cord rather than this side. Only the peer's own count against the
	/// ceiling on how many it may hold open.
	/// </summary>
	internal bool OpenedByPeer { get; init; }

	/// <summary>Sets the handler for messages arriving on this cord.</summary>
	/// <param name="handler">Called once per complete cord message</param>
	/// <returns>This cord, for fluent chaining</returns>
	public McpCord OnMessage(Func<Models.McpMessage, ValueTask>? handler)
	{
		_onMessage = handler;
		return this;
	}

	/// <summary>
	/// Sets the handler for the peer closing this cord. Not called when this side closes it -- the
	/// caller already knows.
	/// </summary>
	/// <param name="handler">Called once, however many times the peer says it</param>
	/// <returns>This cord, for fluent chaining</returns>
	public McpCord OnClosed(Func<ValueTask>? handler)
	{
		_onClosed = handler;
		return this;
	}

	/// <summary>Sends a message along this cord.</summary>
	/// <param name="message">The cord message name, which becomes <c>_message</c></param>
	/// <param name="values">The message's own arguments</param>
	/// <exception cref="InvalidOperationException">This cord has been closed.</exception>
	public ValueTask SendAsync(string message, params (string Key, string Value)[] values) =>
		_cords.SendOnAsync(this, message, values);

	/// <summary>
	/// Sends a message along this cord carrying content that does not fit on a line, which is what
	/// lets a cord carry anything a package could.
	/// </summary>
	/// <param name="message">The cord message name, which becomes <c>_message</c></param>
	/// <param name="values">The message's own single-line arguments</param>
	/// <param name="multiline">The continuation keys and their content</param>
	/// <exception cref="InvalidOperationException">This cord has been closed.</exception>
	public ValueTask SendMultilineAsync(
		string message,
		IReadOnlyCollection<(string Key, string Value)> values,
		params (string Key, IReadOnlyCollection<string> Lines)[] multiline) =>
		_cords.SendOnAsync(this, message, values, multiline);

	/// <summary>
	/// Closes this cord and tells the peer. Doing it twice is not an error -- the second is a no-op,
	/// the same way a duplicate close from the peer is ignored.
	/// </summary>
	public ValueTask CloseAsync() => _cords.CloseAsync(this);

	internal ValueTask DeliverAsync(Models.McpMessage message) =>
		_onMessage?.Invoke(message) ?? default;

	internal ValueTask AnnounceClosedAsync() => _onClosed?.Invoke() ?? default;
}
