# Builder reference

Every callback and setting on `TelnetInterpreterBuilder`, in one list. A setting written after an
`.AddPlugin<T>()` belongs to that plugin; the rest belong to the interpreter.

All plugin callbacks and settings are set inline on the builder:

- `.OnNAWS((height, width) => ...)` — window size changes
- `.OnGMCPMessage(msg => ...)` — GMCP messages
- `.OnMSSP(config => ...)` — MSSP requests
- `.OnMSDPMessage((telnet, data) => ...)` — MSDP messages
- `.OnPrompt(() => ...)` — EOR / Suppress Go-Ahead / Packet Patch prompt signals (see
  [Detecting prompts](../guides/prompts.md))
- `.WithHoldTime(...)` (`PacketPatchProtocol`) — how long an unterminated fragment is held before it
  is reported as a prompt; also settable in one call via `AddDefaultMUDProtocols`'s
  `packetPatchHoldTime` parameter
- `.WithMSSPConfig(() => new MSSPConfig { ... })` — server advertisement data
- `.WithCharsetOrder(Encoding.UTF8, ...)` — encoding preference order
- `.WithTTableSupport(true)` / `.OnTTableReceived(...)` / `.OnTTableRequested(...)` — TTABLE support (RFC 2066)
- `.OnMXPEnabled(() => ...)` — MXP negotiation success
- `.WithKeepAlive()` — idle keep-alive (off by default, see [Keep-alive](../guides/keep-alive.md))
- `.WithMaxMessageSize(bytes)` / `.OnGMCPMessageTooLarge(...)` / `.OnMSDPMessageTooLarge(...)` / `.OnMSSPMessageTooLarge(...)` — subnegotiation size limits (see [Limits](../concepts/limits.md))
- `.WithMaxTTableSize(bytes)` — TTABLE size limit (RFC 2066)
- `.WithReplyTimeout(...)` — plaintext MSSP reply ceiling (`MSSPPlaintextProtocol`, see [MSSP](../protocols/mssp.md#plaintext-mssp-mssp-request))
- `.OnMcpMessage("package-message", msg => ...)` — MCP messages (`MudClientProtocol`, see [MCP](../protocols/mcp.md))
- `.SupportsMcpPackage(name, min, max)` / `.OnMcpNegotiationComplete(agreed => ...)` — MCP package negotiation (`MudClientProtocol`)
- `.SupportsCordType("type", cord => ...)` — accept a cord type (`McpCordProtocol`, see [Cords](../protocols/mcp.md#cords))
- `.WithMaxBufferSize(bytes)` — longest line of ordinary input the interpreter will assemble (default 5 MiB; a longer line is dropped, not truncated)

