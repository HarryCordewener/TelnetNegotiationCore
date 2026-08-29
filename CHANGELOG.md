# Change Log
All notable changes to this project will be documented in this file.

## [2.13.0]

### Added
- **MCP, the MUD Client Protocol (2.1), as three plugins.** `MudClientProtocol` is the session layer:
  the `#$#` framing, the `#$"` quoting rule, the version handshake and the authentication key.
  `McpNegotiateProtocol` is the `mcp-negotiate` package that advertises which packages this side
  speaks and settles the version of each. They are separate because the specification separates
  them — `mcp-negotiate` is a package carried over the session layer, versioned on its own (1.0, and
  2.0 which adds `mcp-negotiate-end`), exactly as `dns-org-mud-moo-simpleedit` is. The dependency
  runs one way, so `McpNegotiateProtocol` declares `MudClientProtocol` and adding it alone throws at
  `BuildAsync()`. Configure both from the builder chain with `.OnMcpMessage(...)`,
  `.SupportsMcpPackage(...)` and `.OnMcpNegotiationComplete(...)`.
  - Nothing MCP reaches `OnSubmit`: handshake, messages, continuation lines and terminators are
    taken out of the stream, and a line the peer quoted as `#$"…` is delivered unquoted.
  - Multiline messages (`_data-tag`, `#$#* <tag> <key>: …`, `#$#: <tag>`) are carried in both
    directions: they arrive whole and once, on the terminator, and `SendMultilineAsync` writes one —
    the direction `dns-org-mud-moo-simpleedit` needs, a server handing a client a buffer to edit. The
    tag is generated per message and the whole message goes out under one lock, because the peer
    reassembles by tag and a foreign line landing inside it would be read as belonging to whatever
    tag it names. Continuation text runs verbatim to the end of the line, so a string containing line
    breaks becomes several continuation lines rather than one that ends early. Because the peer decides whether a terminator ever arrives, at most 8 may be
    open at a time and at most 4096 continuation lines may accumulate in any one of them.
  - A line beginning `#$#` that fails to parse, carries an unknown message name, or carries the wrong
    authentication key is dropped rather than shown — in a session or outside one, as the
    specification requires of an unrecognised or mangled request. The rule is line-initial, so `#$#`
    inside ASCII art is untouched.
  - `MudClientProtocol.SendOutputAsync` is a server's half of the framing rule: while a session is
    up it quotes a line of real output that begins `#$#`, and one that begins `#$"` (which would
    otherwise lose that prefix to the peer's unquoting). `#$#` mid-line is left alone, and outside a
    session nothing is quoted. `QuoteOutput` exposes the same transformation on its own. It is a
    separate call rather than a hook on the interpreter's send path because quoting is a line-level
    decision and that path is a byte stream.
  - `MudClientProtocol.AnswersOffers` (fluent: `.WithoutAnsweringMcpOffers()`) takes MCP out of the
    stream without ever speaking it — the offer is consumed, nothing is sent back, no session is
    opened. For a consumer that reads connect screens from strangers and has no use for a session:
    of the 57 lines beginning `#$#` across MUIndex's stored connect screens, 54 are exactly this
    offer, in both the quoted and the unquoted spelling. Both are read.
  - `MudClientProtocol.OnOffered` (fluent: `.OnMcpOffered(...)`) reports a server's offer with the
    range the server named, for every well-formed offer, whether or not this side answers it. For a
    client that declines it is the only evidence there will be — `IsNegotiated` stays false because
    no session was opened — so recording that a peer speaks MCP does not require opening a session.
  - A mangled message is ignored rather than half-obeyed, per the specification's own list of what
    counts as mangled: one carrying the same keyword twice (forbidden to send, and a receiver has no
    defined way to resolve it, so taking either value is a guess), and a multiline message whose
    continuation names a key the opening message never declared (delivering what survived would hand
    a consumer a message missing content the peer believes it sent).
  - Refused rather than mishandled, all three found in review: an authentication key that is not a
    single unquoted token (it is written back unquoted, so a key with a space in it would give a
    session that reports as established and messages that are all malformed); a value carrying a line
    ending passed to `SendAsync` (it would end the message early and put the rest on the wire as a
    line of its own — `SendMultilineAsync` is the mechanism for that); and a negative `McpVersion`
    component (the grammar has no sign, so it could not be read back by any peer).
  - `mcp-negotiate` is terminal after the peer's `mcp-negotiate-end`: a later `mcp-negotiate-can` no
    longer changes `Agreed` after `OnNegotiationComplete` has been handed its snapshot, and a
    repeated end no longer invokes the callback twice.
  - `MudClientProtocol.NegotiatedVersion` (what the session settled on, `min(server-max, client-max)`)
    and `OfferedVersions` (the range the peer named, recorded whether or not it was answered).
  - `McpNegotiateProtocol.PeerPackages`: everything the peer advertised, including packages this side
    does not speak. `Agreed` is an intersection and throws away the larger half, but "what does this
    peer support" is a different question from "what can the two of us do together".
  - **`McpCordProtocol`, the `mcp-cord` package** — named, typed channels multiplexed over the one
    session, which the specification calls strongly encouraged. A cord is not a negotiation: the
    negotiating happened a layer down, and opening one is use of a capability already agreed. It is
    the extension point that lets a consumer define its own channel without a plugin in this library.
    Identifiers follow the specification's role-prefix scheme (`I` for the endpoint that initiated
    MCP, `R` for the responder), messages may be single-line or multiline, an unsupported cord type
    or a message for an unknown cord is dropped, a duplicate close is ignored, and sending on a
    closed cord throws rather than being swallowed. At most 64 peer-opened cords at once.
  - New public models: `McpMessage`, `McpVersion` and `McpCord`.

### Fixed
- **`TelnetInterpreter.WaitForProcessingAsync` returned while the last byte was still being
  handled.** It watched `_byteChannel.Reader.Count`, which goes to zero when a byte is *dequeued*,
  not when the state machine and the consumer's callbacks have finished with it — and the byte still
  in flight is the one that completes a subnegotiation or submits a line. A fixed 100ms delay
  afterwards covered the gap on an idle machine and stopped covering it on a loaded one, which is a
  CI flake rather than a barrier: `CharsetTests.ServerEvaluationCheck` failed on GitHub Actions
  against a server that had answered correctly, and passed on re-run with nothing changed. The wait
  is now on a pair of counters — items accepted onto the channel, items the processing loop has
  finished handling — so it covers the handling and not just the queue. Counted per channel item,
  which is the unit that gets queued, so a caller waiting on compressed input waits for the decoded
  bytes too; and counted in a `finally`, so a callback that throws cannot strand every later barrier
  on a target it can never reach. `additionalDelayMs` stays, and stays at 100ms by default, but it
  now covers only work a consumer starts that is not itself the handling of an input byte — a timer
  a plugin arms, say. Nothing inside byte handling needs it. The unit suite runs about 9 seconds
  faster for it.

### Changed
- **Two framing rules now follow the specification rather than a stricter reading of it**, both found
  by re-reading MCP 2.1 against the implementation.
  - A line beginning `#$"` is unquoted **unconditionally**, not only once a session is negotiated.
    The specification states the translation without any condition, and a server has already begun
    speaking MCP by the time it makes its offer — so a client that waited showed the prefix to the
    reader.
  - A line beginning `#$#` that fails to parse, carries an unknown message name, or carries the wrong
    key is **dropped rather than displayed**, outside a session as well as inside one. The
    specification says to silently drop it or notify unobtrusively; putting it in the output is
    neither. The rule stays line-initial, so `#$#` inside ASCII art is untouched.
- **The internal assembled-line observer hook can now rewrite a line, not only consume it.**
  `TelnetInterpreter.RegisterInputLineObserver` takes a
  `Func<byte[], Encoding, ValueTask<byte[]?>>`: an observer returns the line to carry on with — the
  same bytes or different ones — or `null` to consume it. MCP's quoting rule needs the middle case,
  which the previous `ValueTask<bool>` could only express by consuming the line and re-injecting it,
  reordering it against anything already queued behind it. The hook is `internal`, so no public API
  changed; `MSSPPlaintextProtocol` is the only other caller and is unaffected in behaviour.

## [2.12.0]

### Fixed
- **A prompt's text was left in the line buffer and prepended to the next line submitted.**
  `EORProtocol` and `SuppressGoAheadProtocol` invoked the consumer's prompt callback without
  draining the accumulated partial line, so the next `CRLF` submitted it as the head of a line it
  was never part of. `TelnetInterpreter.TakePartialLineAsPrompt` (public, alongside `HasPartialLine`
  and `HasSeenMarkedPrompt`) now takes the partial line at the boundary and clears it before either
  marker invokes its callback. The text is not lost: it lands on `TelnetInterpreter.LastPromptBytes`.
  Reached through `IProtocolContext.Interpreter`, so no external `IProtocolContext` implementation
  needs to change.
- **A client refused a server's offer to stop sending Go-Ahead, which RFC 1123 §3.2.2 forbids.** A
  client now accepts `WILL SUPPRESS-GO-AHEAD` unconditionally. Losing GA to a server's veto no
  longer loses the prompt: `PacketPatchProtocol` is the fallback. Server mode had the same gap for
  the same RFC, which names Server Telnet explicitly: a peer's `WILL SUPPRESS-GO-AHEAD` now gets a
  `DO` in server mode too, instead of falling through to a default `DONT`.
- **A client answered an inbound `DO SUPPRESS-GO-AHEAD` with `WONT`, refusing the same option in the
  other direction.** The client branch now handles `DO`/`DONT` itself, tracking its own outbound
  suppression separately from the peer's (RFC 858 §5), and honours RFC 854 §3(b): a change of mode
  is always answered, a request to enter the mode already in force is not.
- **A client that had agreed to suppress its own Go-Ahead sent `IAC GA` at the end of its prompts
  anyway.** `PromptTerminator` read the field that tracks the peer's direction, not this end's own.
  A new `SuppressGoAheadProtocol.SuppressesOutboundGoAhead` reads this end's own direction.
- **A burst of ordinary output arriving while a silence-inferred prompt was still held could report
  a phantom prompt mid-burst and truncate the line it interrupted.** `PacketPatchProtocol`'s
  hold-time arm/disarm was idle-driven: an arm placed before a sustained burst survived the whole
  burst, because the channel never emptied out while more was still arriving, so a stale timer fire
  could enqueue its report behind a backlog and drain whatever partial line was standing once it was
  finally read. Arm and disarm are now byte-driven -- every byte disarms, and only a genuinely idle
  byte may re-arm, fresh from the buffer's state at that instant.

### Added
- **`PacketPatchProtocol`, for the servers that mark a prompt with nothing at all.** RFC 854 gives a
  server `IAC GA` and RFC 885 gives it `IAC EOR`; many MUD servers offer neither and end a prompt
  with a bare unterminated fragment. The plugin holds it and calls it a prompt after `HoldTime` of
  silence — 500 ms by default, settable 0–10 s, rejected rather than clamped outside that range.
  Registering the plugin is its whole opt-in; it never arms in server mode. `AddDefaultMUDProtocols`
  adds it only when given an `onPrompt` callback (a consumer who never asked for prompts should not
  lose a held fragment to a guess), and gains an appended, defaulted `packetPatchHoldTime` parameter.
  The first genuine `IAC GA` or `IAC EOR` on a connection retires it permanently, and disabling it at
  runtime through `ProtocolPluginManager.DisablePluginAsync<PacketPatchProtocol>()` disarms it
  rather than leaving an already-armed timer free to fire once more.

## [2.11.0]

### Fixed
- **`IAC GA` now raises the prompt callback.** RFC 854 makes Go-Ahead the server-to-user prompt
  boundary, and a default NVT — one that negotiates neither EOR nor SUPPRESS-GO-AHEAD — ends every
  prompt with it. It was accepted and discarded, so a client that holds an unterminated line until a
  boundary arrives was never told one had arrived. Client mode only: RFC 854 gives GA no meaning in
  the other direction, and that is also the only direction whose suppression this library records.
- **`IAC GA` is a NOP once SUPPRESS-GO-AHEAD is in effect**, per RFC 858. EOR is not consulted —
  RFC 885 is a different marker and says nothing about Go-Ahead.
- **`IAC EOR` is a NOP while the END-OF-RECORD option is not in effect**, per RFC 885. The prompt was
  gated on `IsEnabled`, which is plugin lifetime and true from initialisation, so an unnegotiated
  `IAC EOR` raised a prompt on any connection that merely registered the plugin. It is gated on
  `IsEOREnabled` — the negotiated state — now.

## [2.10.0]

### Added
- **An MSSP report now carries the bytes each value was decoded from, not only the text.**
  `MSSPVariableCollection.Raw(variable)` returns every value of a variable exactly as it arrived, at
  the same indices as the strings, and `RawDefault(variable)` returns the bytes of the default value
  — the one `Default` returns. A peer's declared encoding is not a measurement of the bytes it sends:
  `mud.pkuxkx.net` negotiates CHARSET down to UTF-8 and then sends GBK anyway, because on that game
  the encoding is chosen from a menu on the login screen, which is a later point in the session than
  the negotiation; `bl.mud.at` answers `;UTF-8` and sends ISO-8859-1 umlauts in `DESCRIPTION-DE`.
  Both sides negotiated correctly and the server simply sent something else. Decoding those bytes
  with the negotiated encoding substitutes `U+FFFD` for each one it cannot read, and that
  substitution is not reversible — the original byte is gone before any consumer sees the report —
  so a crawler that wants to make its own decision about a suspect field had no way to make it. The
  strings are unchanged and no callback moved, so this is additive: existing consumers see exactly
  what they saw before, and one that cares reads the bytes beside them.
  - Both transports carry them, so a report is not worth less for having arrived as
    `MSSP-REPLY-START` lines than as a subnegotiation. The plaintext transport finds its tab in the
    bytes rather than reusing the decoded string's tab index, because those two agree only while
    every character before the tab is a single byte.
  - The bytes are copied as each field closes rather than being windows onto the parse buffer, which
    keeps growing and is cleared between reports; a view would be reading someone else's field by
    the time a consumer looked.
  - `Add(variable, value)` still exists and is what a program building a report for itself uses. It
    records an empty entry, so an entry is empty either for a value that never came off a wire or
    for one the peer really did send as zero bytes, which the specification allows.

## [2.9.1]

### Fixed
- **A bare `IAC GA` had no permitted transition from `StartNegotiation` at all**, so a server sending
  one — the original 1983 prompt marker (RFC 854), which predates `EOR` and `SUPPRESS-GO-AHEAD` and
  which several real servers still send regardless of what either side negotiated — always reached
  `OnUnhandledTriggerAsync`: a `Critical` log and a recovery through `Trigger.Error` on every single
  occurrence. Harmless on its own; not harmless when a server pairs a trailing `GA` with another `IAC`
  sequence right behind it. `achaea.com` does exactly that at the moment it starts MCCP2 — the prompt's
  `GA` arrives immediately before `IAC SB COMPRESS2 IAC SE` — and the recovery could cost that marker
  the same way losing any other subnegotiation start would. `StartNegotiation` now permits `GA`
  directly, the same way it already permits `NOP`: consumed and dropped, nothing more, since reacting
  to it as a prompt boundary would be wrong for a connection that also negotiated `EOR` or
  `SUPPRESS-GO-AHEAD` and is sending `GA` for nothing.
- **`Willing`, `Refusing`, `Do` and `Dont` had no permitted transition for `IAC` either**, and each of
  those states expects exactly one more byte: the option number the `WILL`/`WONT`/`DO`/`DONT` was
  about. Measured against `achaea.com` once `EOR`, `GMCP` and `MSSP` were also negotiated: the server
  sent `IAC WONT` with no option byte at all — the very next byte was another `IAC` — immediately
  before the same `IAC SB COMPRESS2 IAC SE` marker. Whatever the server's own reason for the bare
  `WONT`, the unhandled `IAC` there recovered into `Accepting`, which had no idea a subnegotiation was
  about to start; the marker right behind it was read as four bytes of plain text, and the zlib stream
  that followed was never inflated — read as telnet, forever, for the rest of the connection. All four
  states now treat a fresh `IAC` as abandoning the incomplete negotiation and restart parsing from
  there, the same way `IAC IAC` is already handled as an escaped literal from `StartNegotiation`
  rather than through the same generic recovery.
  - Both were found and fixed together because the first one alone was not enough to make MCCP2 work
    against the real server that motivated it: fixing only `GA` still left the second, independent gap
    reachable by the same connection. `GoAheadDuringNegotiationTests` asserts on the absence of a
    `Critical` log entry rather than on any one interleaving's downstream behaviour recovering by
    luck, which is what the first, narrower version of this fix looked like passing under.

## [2.9.0]

### Added
- **`IsEnabled` never meant "the peer agreed to this" — it meant "this plugin is attached", and
  nothing distinguished the two.** `TelnetProtocolPluginBase.InitializeAsync` set `_isEnabled = true`
  the moment a plugin was constructed and registered, which happens before any `WILL`/`DO` byte has
  crossed the wire in either direction. Every downstream check that read `IsEnabled` to mean "is this
  option live" — this library's own `TelnetEORInterpreter`/`TelnetMSSPInterpreter`, and any consumer
  gating behaviour on a plugin's state — was reading a signal that had been true since before
  negotiation started, and stayed true regardless of whether the peer ever answered, refused, or
  later withdrew the option. The only path that could set it correctly, `OnEnabledAsync`, is reachable
  only through `ProtocolPluginManager.EnablePluginAsync<T>()`, which nothing in this codebase calls —
  so in practice `IsEnabled` was a constant, not a signal. A consumer that needed to know "did the peer
  really negotiate MSDP before I send it a request" had no way to ask this library that question, and
  had to work around it by sending unconditionally and absorbing whatever a server that never
  negotiated the option sent back.
  - **`IsEnabled` is unchanged and keeps its existing meaning**: whether the plugin is attached to the
    interpreter and processing. That is a real and useful question — every internal guard that already
    read it to mean "has this plugin been initialized" is still correct — and changing its meaning
    under a name every consumer already reads would have been a silent semantic break, not a fix.
  - **A new, orthogonal `IsNegotiated` property answers the question `IsEnabled` could not**: false
    until the peer has genuinely agreed to the option over the wire, true once a `WILL`/`DO` (or
    `DO`/`WILL`) exchange actually completes in the peer's favour, and false again if the peer refuses
    outright or later withdraws an option it had previously accepted. A matching
    `OnNegotiatedAsync(bool isNegotiated)` — and its overridable `OnNegotiationChangedAsync(bool)` hook,
    named to sit alongside the existing `OnEnabledAsync`/`OnDisabledAsync`/`OnProtocolEnabledAsync`/
    `OnProtocolDisabledAsync` pattern — fires exactly at the point each protocol's own state machine
    resolves that question, giving a consumer a real hook to gate behaviour on instead of the
    always-true `IsEnabled`.
  - **Wired from the real state machine transition, not a second manual switch.** Each protocol already
    implements `ConfigureStateMachine`, the designed extension point for a plugin's own negotiation
    handlers; every protocol under `Protocols/` that negotiates an option over `WILL`/`WONT`/`DO`/`DONT`
    now calls `OnNegotiatedAsync(true)` from the handler entered on genuine acceptance and
    `OnNegotiatedAsync(false)` from the one entered on refusal or withdrawal, for both server mode and
    client mode — the two see opposite halves of the same exchange (a server offering `WILL` and
    reading the peer's `DO`/`DONT`; a client reading an offered `WILL`/`WONT` and answering `DO`), so
    each is wired to the trigger that is a genuine response from the peer, not to this side's own
    outbound announcement. `MSSPPlaintextProtocol` is the one exception worth calling out: it carries
    no `WILL`/`DO` exchange of its own — registering the plugin at all is its whole opt-in, as its own
    documentation already said — so it reports `IsNegotiated = true` from `OnInitializeAsync` rather
    than staying false forever for want of a handshake that protocol does not have.
  - This changes no wire behaviour anywhere — every existing test still passes unmodified — only the
    accuracy of an internal signal nothing was reading correctly before.

## [2.8.3]

### Added
- **A client can now ask for an MSDP variable.** `MSDPProtocol` could negotiate the option and
  receive whatever a server chose to send, but had no way to send anything: `MSDPServerHandler`
  exists to answer a client's `LIST`, `REPORT`, `SEND`, `RESET` and `UNREPORT`, and
  `MSDPClientHandler` — the half that would build those requests — was a stub that threw
  `NotImplementedException`. A consumer negotiating MSDP as a client had the receiving half of the
  protocol and nothing to say with it. `TelnetInterpreter.SendMSDPCommand(variable, value)` sends
  the one wire shape all five commands share — `IAC SB MSDP MSDP_VAR <command> MSDP_VAL <argument>
  IAC SE` — mirroring `SendGMCPCommand`, which already did the equivalent for GMCP. `await
  telnet.SendMSDPCommand("SEND", "PLAYERS")` is the client half of asking a server what it is
  willing to report. Both `SendMSDPCommand` and the existing `SendGMCPCommand` now escape a literal
  `IAC` (0xFF) byte in their payload through the shared `TelnetSafeBytes` — the same helper
  `SendAsync`/`SendPromptAsync` already use — rather than sending it unescaped and desyncing the
  peer's state machine, or reimplementing the doubling a second time.

## [2.8.2]

### Fixed
- **`TelnetInterpreter` never declared `IAsyncDisposable`, so nothing generic ever disposed it** — the class has had a complete `DisposeAsync` for as long as it has had a processing loop: it completes the byte channel, cancels the loop, stops keep-alive, disposes every plugin (MCCP's zlib streams among them), retires the byte transforms and disposes the token source and the write lock. None of that was reachable through the type. C# binds `await using` to a `DisposeAsync` *method* by pattern, not to the interface, so the disposal ran perfectly for anyone who wrote the `await using` by hand — and not at all for a DI container, a `List<IAsyncDisposable>`, or any `is IAsyncDisposable` test, all of which look at the type and found nothing there. A server registering interpreters in a container leaked one interpreter, one processing task and one set of plugin state per connection, with no error to notice it by. The interface is now declared; the method it binds to is the one that was already there, unchanged.
- **Disposing twice threw** — which had been mostly academic while only hand-written `await using` blocks reached the method, and stops being so the moment a container can see it: the container disposes what it owns whether or not the consumer already did. `ChannelWriter.Complete()` throws `ChannelClosedException` on an already-completed channel, which is what came out first, and had it not, `CancellationTokenSource.CancelAsync()` would have thrown `ObjectDisposedException` on the token source the previous call disposed. `IAsyncDisposable` requires repeated calls to be tolerated, so the first caller now takes the whole shutdown and every later one returns having done nothing. The claim is made with `Interlocked.Exchange` rather than a `bool` check-then-set, because the two callers most likely to race are the owner of the connection and the read loop that has just noticed the connection went away, and they are on different threads.

## [2.8.1]

### Fixed
- **A client went permanently deaf when the server ended its MCCP2 stream** — the specification says a server may stop compressing whenever it likes: *"The server may terminate compression at any point by sending an orderly stream end (Z_FINISH). Following this, the connection continues as a normal telnet connection."* 2.8.0 had nothing that noticed. Once the peer's zlib stream ended, `ZLibStream` returned 0 for ever, and every byte after it — plain telnet, by the specification — was fed to the finished inflater and silently discarded. Not an error, not a log line: the connection simply stopped delivering, for the rest of its life. Reported as issue #66, where the connect screen arrives and everything after it is lost.
  - Captured off the wire from `realms.reichel.net:4000` (ROM 2.4) and checked in as `TelnetNegotiationCore.UnitTests/Fixtures/rom-mccp2-stream-end.bin`, which is what the regression test replays — no test contacts the network. The 516 bytes are: option negotiation, `IAC SB COMPRESS2 IAC SE`, 475 bytes of zlib whose last seven are `00 00 FF FF` (a `Z_SYNC_FLUSH`), `03 00` (the final empty block, i.e. `Z_FINISH`) and a four-byte Adler-32, and then **18 bytes of uncompressed telnet**: `IAC DONT TTYPE`, `IAC DONT NAWS`, `IAC DONT NEW-ENVIRON`, `IAC WONT CHARSET`, `IAC WONT MSSP`, `IAC WONT COMPRESS2`. All 18 were being thrown away, including the server's own announcement that it had stopped compressing — so the plugin went on reporting `IsMCCP2Enabled == true` over a connection it was no longer reading.
  - This is why a short session against a failing server looked fine and a longer one did not: the stream ends when the *server* decides to stop, which on ROM is when it drops the connection, so a probe that read the connect screen and disconnected never reached the end and a probe that stayed connected did. It is not a function of how many reads the payload spans.
  - `MCCPInflateTransform` now detects the end of the peer's stream, hands back any bytes the inflater declined to consume — they are past the end of the zlib stream, which makes them the first plain telnet since the marker — and passes everything after it through untouched. `MCCPProtocol` removes the inflater and fires `OnCompressionEnabled(version, false)`, so a consumer learns that compression stopped rather than inferring it from silence. Detection is the same signal on both implementations, and needs no version-specific behaviour from either: a running inflater always empties its input, so input left unconsumed means it has stopped asking, which for zlib means the stream is over.
  - A peer that ends its stream and then offers MCCP2 again now gets a fresh inflater, because the connection really is back to plain telnet at that point. A second `IAC SB MCCP2 IAC SE` *inside* a live stream is still ignored, as in 2.8.0 — that one would throw away the deflate window the rest of the stream is encoded against.
- **`IAC WONT COMPRESS2` arriving inside a live compressed stream tore the inflater out mid-stream** — an option going away is not a stream ending. Only `Z_FINISH` ends a zlib stream, and the bytes already in flight behind such a refusal are still compressed. 2.8.0 removed the inflater on the spot, which handed the rest of the peer's zlib stream to the telnet state machine as if it were telnet and left the plugin believing nothing was running — so the peer's next compression marker installed a **fresh inflater onto the middle of the old stream**, where the first byte read is not a zlib header. That is the only path by which `MCCPInflateTransform` can raise `InvalidDataException` after the first chunk has inflated, and it is the exception reported in issue #66. A refusal that arrives while the stream is open is now logged and the inflating continues until the peer actually ends the stream.
  - For the record, since the exception's text invites the wrong diagnosis: *"The archive entry was compressed using an unsupported compression method"* is .NET's message for **any** zlib `Z_DATA_ERROR`, not specifically a header check. Flipping bits 177 bytes into the deflate data of the capture above — nowhere near a header — produces exactly that message. It does not, on its own, mean the inflater was looking at a stream header.
- **Refusing an option that was not running was reported as a state change** — `IAC WONT COMPRESS2` or `IAC DONT MCCP3` fired `OnCompressionEnabled(version, false)` whether or not anything had been running, and would do so again on every repeat. A peer may refuse an option it never used, and may refuse it twice. With the stream-end handling above it would also have fired a second time on ROM's trailing `IAC WONT COMPRESS2`, which arrives *after* the stream has already ended and been reported. Only an actual stop is now announced.

## [2.8.0]

### Fixed
- **A client sent the operating-system account name to any server that asked for environment variables.** `NewEnvironProtocol` answered a server's `IAC SB NEW-ENVIRON SEND` with `USER` filled in from `Environment.GetEnvironmentVariable("USER") ?? Environment.UserName`, plus a `LANG` hardcoded to `en_US.UTF-8`. There was no opt-in and no way to turn it off — the dictionary was a local variable inside a private method, and the code's own comment said it was meant to be configurable. **The OS account name is frequently a person's real name**, the servers on the other end are run by strangers, and a player connecting to a game had neither consented to telling its administrator what their laptop account is called nor any way to find out that they had. `EnvironProtocol` (RFC 1408) leaked the same value the same way, and also reported the machine's locale as `LANG`.
  - **The default is now empty.** A client sends exactly the variables the application configured, and answers `SEND` with an empty `IAC SB NEW-ENVIRON IS IAC SE` when it configured none — which leaves the server in the same position as one talking to a client that never negotiated NEW-ENVIRON at all, which is most of them. The `SEND` handshake still completes; the server's callback still fires, with nothing in it.
  - **`USER` is never populated from the environment by this library again**, whatever else changes. It is also the wrong variable: RFC 1572 defines `USER` as *the account to log in as* — the telnet analogue of a username prompt — which has nothing to do with the local OS account. An application that genuinely has a login name to send can set one, having decided that itself.
  - **`LANG` is gone.** `en_US.UTF-8` is a straightforward false claim on any non-English system, and the RFC 1408 path's "improvement" on it — deriving the value from `CultureInfo.CurrentCulture` — reported the operator's locale to a stranger instead.
  - A test plants a sentinel in the process environment the old code read from and fails if it reaches the wire; a second test fails if any file under `Protocols/` so much as mentions `Environment.UserName`, `Environment.GetEnvironmentVariable` or `CultureInfo.CurrentCulture` again.
- **Every application built on this library introduced itself as `TNC`.** `TerminalTypeProtocol.ConfigureAsClient` seeded the terminal type list with the literals `["TNC", "XTERM", "MTTS 3853"]`, `_terminalTypes` was private and `TerminalTypes` get-only, so there was no seam a consumer could reach. MTTS defines the **first** TTYPE response as the *client name*, so the slot meant to identify the application carried the name of the library underneath it: an administrator reading their logs could not tell who was visiting, or how to ask them to stop, which is the question a TTYPE exists to answer. `MTTS 3853` was worse than useless — it asserted ANSI, UTF-8, 256 colours, truecolour, MNES and MSLP on the application's behalf, true of a terminal emulator and false of a headless crawler, with no way to correct it. An application now says who it is with `WithClientIdentity`, and a client that says nothing answers `UNKNOWN` rather than borrowing the library's name.
- **A malformed MTTS response from a peer crashed the server-side reader.** `int.Parse(MTTS.Remove(0, 5))` threw a `FormatException` inside the state machine for anything a peer chose to send that started with `MTTS` and did not continue with a number, and parsed the digits under the current culture. It is now an invariant-culture `TryParse` that logs and ignores the entry.
- **A server's `SEND` was read and then ignored: the client always replied with everything it had.** RFC 1572 lets a server ask for particular variables — `IAC SB NEW-ENVIRON SEND VAR USER VAR DISPLAY IAC SE` — and says *"if a list of variables is specified, then only those variables should be sent"*, in the order they were named, with *"a response for each 'type ...' explicitly requested"*. The client parsed those names into a field and then threw them away, answering every `SEND` with its whole variable list regardless of what was asked. Both plugins now answer the request that was actually made:
  - **Only the named variables, in the order they were named.** A server asking for two of six gets two, in its own order rather than the client's.
  - **A requested variable this client does not have is answered as undefined** — its name, carrying no value — rather than being silently dropped, which left the server waiting for something it had asked for. A variable configured with an empty value is a different answer: it is defined, and goes out with an empty `VALUE`. (Previously an empty value was skipped entirely, so an application that configured one sent nothing.)
  - **A type marker with no name after it still means "every variable of that type"**, and a `SEND` with no list at all still means everything, as before. Everything this library sends is a well-known `VAR` — MNES's own names included — so a request for every `USERVAR` is answered by there being none.
  - **Being asked is not consent.** A server that asks for `USER` by name is told the client has none; nothing goes looking for a value the application did not supply. The test for that lives with the rest of the privacy guarantee, sentinel and all.
- **A NEW-ENVIRON value carrying a control byte could end the subnegotiation early.** Names and values are now written with RFC 1572's `ESC` escapes for the `VAR`, `VALUE`, `ESC` and `USERVAR` bytes, and `IAC` is doubled (RFC 854). MNES forbids those bytes inside a value, but an application supplies these strings, so a value that carries one anyway must not be able to truncate the frame.
- **MCCP2 was negotiated but the stream was never inflated** — a client that accepted a server's MCCP2 offer lost every byte from the compression marker onward, which is worse than not implementing MCCP at all: declining the offer would have got the same text in the clear. `OnCompressionEnabled` fired, so the library reported compression was running, and then the raw deflate bytes were handed to the telnet state machine and to `OnSubmit` as if they were text. Measured against a live ROM server (`realms.reichel.net:4000`), the connect screen arrived as `HǌR]k?@?!?ւm????4???<???.?5?…`; the same 469 bytes inflate cleanly to 1108 bytes of ASCII with a plain zlib inflater. With `CharsetProtocol` negotiating UTF-8 the same bytes arrive as a wall of U+FFFD, which is what made this look like a charset fault rather than a compression one.
  - Nothing was wired to decompress. `MCCPProtocol.DecompressData` was public, correct-looking and had **zero callers** in the repository; the only other mentions of it were two comments claiming decompression was "done per-message in `DecompressData`". `CompressData` had no callers either, and the `context.SetSharedState("MCCP_Protocol", this)` that might have let a consumer reach them was never read back.
  - What the client built at the marker was a **compressor**. `CompleteMCCP2NegotiationAsync` ran on the client — the MCCP2 subnegotiation transitions were registered outside the mode branch — and constructed `new ZLibStream(buffer, CompressionMode.Compress)`. MCCP2 is server-to-client compression; the client's job at that marker is to inflate. That deflater also landed in the field `CompressData` read, so a client that had never negotiated MCCP3 would have started compressing its outbound data if anything had called it.
  - **The deflate stream was parsed as telnet.** Roughly one byte in 256 of deflate output is `0xFF`; each one was interpreted as `IAC` and drove the state machine into a negotiation state, so any real negotiation arriving later was interpreted against corrupted state. In the 469-byte sample there were four.
  - Even called per network read, `DecompressData` could not have worked: it created a fresh `ZLibStream` per call. MCCP is **one** zlib stream for the life of the connection — the back-reference window and Huffman state carry across everything the peer sends after the marker — so a per-call inflater decodes the first read and then fails on the second.
  - In a survey of 38 MU\* codebases (one live game each), 13 negotiated MCCP2: CoffeeMUD, LPMud, Evennia, DikuMUD, ROM, NarutoMUD Engine, GWM, Epiphany, FluffOS, Anatolia, Dark City, IME, LoFP. On every one of them a client using this library lost the connect screen, the whole `WHO` reply, and any MSSP that did not arrive immediately.
- **MCCP3 was announced and then not performed** — a client that answered a server's `WILL MCCP3` sent `DO MCCP3` and `IAC SB MCCP3 IAC SE`, told the consumer compression was on, and then went on sending in the clear. A server that believed the marker and started inflating got nothing usable. Both halves are now real: the client deflates everything it writes after the marker, and the server inflates everything it reads after receiving one.
- **A line of input longer than the buffer was written past the end of the array** — `TelnetInterpreter` wrote every non-negotiation byte into a fixed buffer with no bounds check, so a peer that sent more than `MaxBufferSize` bytes without a newline caused an `IndexOutOfRangeException` inside the state machine. A peer decides when to send a newline, so this is the one accumulator an untrusted peer can grow at will — and plaintext MSSP is the first thing in the library that deliberately asks a stranger for text. An over-long line is now dropped whole (never truncated: a line cut at an arbitrary point is a different line, not a shorter one), with an `Error` log naming the limit, and the connection continues with the next line.
- **Every connection allocated the whole line-buffer ceiling** — `MaxBufferSize` (5 MiB by default) was allocated up front for each connection, so a server holding a thousand connections that were each saying `look` paid 5 GiB for it. The ceiling bounds what a hostile peer may do; it is not the size of any real line. The buffer now starts at 1 KiB and doubles towards `MaxBufferSize` only as a line needs it, and is released after delivering a line that grew it past 64 KiB — the same retain policy `SubnegotiationBuffer` uses. `MaxBufferSize` means exactly what it meant, an over-long line is still dropped whole with an `Error` log, and the per-byte path is still a single comparison (capacity starts at zero, so one test covers both "not allocated yet" and "full").
- **`TelnetInterpreterBuilder.WithMaxBufferSize(bytes)` had no effect** — it logged `"MaxBufferSize cannot be set after construction. Using default."` and dropped the value, because the buffer was allocated in the constructor before the init property was assigned. The buffer is now allocated lazily from `MaxBufferSize` on the first byte of input, so the setting is honoured (and a negotiation-only connection allocates nothing). Non-positive values are rejected by the builder and by `BuildAsync`. `TelnetInterpreter.DefaultMaxBufferSize` names the 5 MiB default.
- **An `int` MSSP value was serialized with the current culture** — `CRAWL DELAY -1` would go out as `−1` (U+2212) under a culture whose `NegativeSign` is not `-`, which no peer parses as a number. Both transports now format integers with `CultureInfo.InvariantCulture`.
- **Plugins were never disposed** — `TelnetInterpreter.DisposeAsync` shut down its channel, keep-alive and processing task, but never called `ProtocolPluginManager.DisposeAllAsync`, so `OnDisposeAsync` did not run on any plugin. Harmless while no plugin held a resource; MCCP holds zlib streams.
- **`SendPromptAsync` never marked a prompt as a prompt** — every prompt went out as text followed by CR LF, on every connection, whatever had been negotiated. A prompt was therefore indistinguishable from an ordinary line, which is the one thing the method exists to prevent: a client cannot keep `HP: 100/100>` on the input line if it arrives looking like output. Both of the markers the method appeared to send were unreachable.
  - `IAC EOR` was unreachable because the interpreter's `_doEOR` was never assigned. Its four assignments live in handlers registered by `SetupEORNegotiation`, and **nothing has ever called `SetupEORNegotiation`** — the live End of Record negotiation belongs to `EORProtocol`, which keeps its own flag. So the field stayed `null` for the life of every connection and `if (_doEOR is null or false)` always won, even when the peer had negotiated EOR with `IAC DO TELOPT_EOR`.
  - `IAC GA` was unreachable a second way, independent of the first: the branches were `if (_doEOR is null or false)`, `else if (_doEOR is true)`, `else if (_doGA is not null)`, and `null`/`false`/`true` exhausts `bool?`. The third branch could not run for any value of any field. Its condition read a `_doGA` that was itself never assigned anywhere — `SuppressGoAheadProtocol` owns the real one.
  - A prompt is now terminated the way RFC 885 and RFC 854 say: `IAC EOR` where End of Record was negotiated; otherwise `IAC GA`; otherwise CR LF, where the peer negotiated RFC 858 SUPPRESS-GO-AHEAD and has been promised no Go-Ahead. The negotiated state is read from `EORProtocol.IsEOREnabled` and `SuppressGoAheadProtocol.IsGoAheadSuppressed`, which are the copies that negotiation actually updates.
  - **Behaviour change:** prompts now put `IAC EOR` or `IAC GA` on the wire where before they put CR LF. A consumer that has been reading prompts as ordinary lines — or a test asserting the trailing CR LF — will see the marker instead. Adding `SuppressGoAheadProtocol` and negotiating it restores CR LF endings.

### Added
- **`TelnetInterpreterBuilder.WithClientIdentity(...)` — one place to say who the application is.** A client introduces itself down two channels, and they carry the same fact: MTTS reads the first TTYPE response as the client name, and MNES calls it `CLIENT_NAME`. `ClientIdentity` holds that fact once — `Name`, and optionally `Version`, `TerminalType` and an `Mtts` claim — and both protocols read it, so they cannot disagree. There is also `WithClientIdentity(name, version)` for the common case, and the same call is reachable from inside a plugin chain, as `WithKeepAlive` already is.
  - **Nothing is invented in its absence.** Without an identity, TTYPE answers `UNKNOWN` — RFC 1091's own word for a terminal that will not name itself — and NEW-ENVIRON sends nothing. A part of the identity that is not set is left out rather than filled in: an application that renders nothing has no terminal type, and the library will not pick one for it.
  - `TerminalTypeProtocol.WithTerminalTypes("NAME", "TERM", "MTTS 9")` remains for an application that would rather state its whole TTYPE list itself; it is sent verbatim, in that order, and nothing is added to it.
  - `NewEnvironProtocol.WithClientEnvironmentVariables(...)` sets everything else a client wants to send, in the order to send it, with an entry overriding the identity-derived variable of the same name. MNES's names are documented on it.
- **`MttsCapabilities`, and an MTTS bitvector that is calculated rather than stated.** The flags enum replaces the bare integers on both sides of MTTS, and `MttsCapabilityNames.Expand` is now the single table a server expands a peer's bitvector through. What a client claims is the union of two different kinds of thing:
  - **What the library can see for itself**, via `TerminalTypeProtocol.ObservedCapabilities`: `Utf8` (4) while the interpreter is decoding UTF-8, and `Mnes` (512) exactly when a `NewEnvironProtocol` plugin is registered and this connection really will answer MNES. This is what the README has claimed since MNES support was added — *"automatically indicated via the MTTS flag 512 when both Terminal Type and NEW-ENVIRON protocols are enabled"* — and it was never true: the flag was a constant that happened to include 512 whether or not NEW-ENVIRON was there.
  - **What only the application can know**, via `ClientIdentity.Mtts`: colour depth, mouse tracking, a screen reader, a proxy. This library renders nothing, so it cannot observe any of them and no longer claims them. The two are OR-ed, and when the result is empty no `MTTS` response is sent at all.
- **Plaintext MSSP (`MSSP-REQUEST`), as the new `MSSPPlaintextProtocol` plugin** — MSSP's second transport, alongside telnet option 70. A client sends the literal line `MSSP-REQUEST` at the connect screen; the server answers with `\r\nMSSP-REPLY-START\r\n`, tab-separated `name<TAB>value` lines, and `MSSP-REPLY-END\r\n`. The vocabulary is identical to the subnegotiation form — multi-word official names (`MINIMUM AGE`, `PAY TO PLAY`, `XTERM 256 COLORS`) included — so it lands in the same `MSSPConfig` through the same `OnMSSP` callback; only the framing and the field split differ. A crawler that reads only option 70 records "no MSSP" for this whole population, indistinguishable from a server that genuinely has none.
  - **Adding the plugin is the entire opt-in.** There is no flag. `MSSPPlaintextProtocol` declares `MSSPProtocol` as a dependency and borrows its `OnMSSP` callback, its `MaxMessageSize` and its `WithMSSPConfig` provider rather than duplicating them, so adding it alone fails at `BuildAsync()` with the dependency error instead of going quiet on the wire. A consumer that does not add it sees no behaviour change on either side.
  - **Server side: automatic.** An incoming `MSSP-REQUEST` line — matched case-insensitively, as SMAUG's `str_cmp` does — is answered from the configured `MSSPConfig` and **consumed**, which means that word is no longer usable as a character name on that server. That is the whole consequence of registering the plugin, and registering it is the consent to it.
  - **Client side: explicit.** `await plaintext.RequestReportAsync(cancellationToken)` sends the request and returns the peer's `MSSPConfig`, or `null` for every case where the peer produced no report — never answered, reply never ended, reply over the ceiling, connection gone. A fault on the caller's side — an already-cancelled token, a disabled plugin — throws instead, so the two are never confused. The report is *also* delivered to `OnMSSP`, so a consumer wired for option 70 needs no new plumbing, while a crawler gets the value at the call site where it knows which host it just asked. `null` is not an error: a server without the plaintext form answering "Illegal name, try another." is the ordinary case.
  - **There is deliberately no timer.** No version of the MSSP specification gives timing for this exchange — the only timing concept MSSP has is `CRAWL DELAY`, which is *hours between crawls*, not when to speak within a connection. Grapevine's crawler asks 10 seconds after connecting and gives up at 20, but that is one crawler's published policy, and a library that baked it in would be choosing, for every consumer, the moment to put text on a stranger's login prompt — by which time an interactive client may already have sent a character name, and injecting a request mid-login corrupts the sequence invisibly. A crawler can rebuild Grapevine's exact policy on top of the explicit call in three lines; nobody can recover that precision back out of a baked-in timer.
  - **Bounded in bytes and in time.** The reply is counted against `MSSPProtocol.MaxMessageSize` (default 1 MiB) and is **dropped rather than truncated** past it, with an `Error` log and the existing `OnMSSPMessageTooLarge((ReceivedBytes, MaxMessageSize))` callback; the call returns `null`. `MSSPPlaintextProtocol.ReplyTimeout` (default 10 seconds) bounds a caller that passes `CancellationToken.None`, so an unanswered request cannot wait forever. Cancelling your own token throws `OperationCanceledException`; the ceiling and a dead connection return `null`, because those are answers about the peer rather than about the caller.
  - **Markers are matched as whole lines**, not as substrings of a receive buffer. Grapevine detects a reply with `string =~ "MSSP-REPLY-START"`, which on a MUD — where people type things on purpose — is trippable by saying the words out loud. Everything from the start marker to the end marker is consumed, so a reply never reaches `OnSubmit` as if a user had typed it.
  - **The field split is on the first tab and nothing else**, which is what keeps multi-word names and values containing spaces intact. A line inside a reply with no tab is not a field. An empty value is, since the specification says "The value can be an empty string".
  - Decoded and encoded with `TelnetInterpreter.CurrentEncoding`, consistent with 2.7.0. `IAC` among the encoded bytes is doubled (RFC 854) — this is text, but it is text on a telnet connection — and lines are terminated CR LF. A tab or line ending inside a name or value is replaced with a space on the way out, because this framing has no escape for one and a frame the peer cannot parse back is worse than a mangled character.
  - Nothing reaches the peer unless the consumer asked: `RequestReportAsync` refuses an already-cancelled token (`OperationCanceledException`, before the send rather than after it) and returns `null` without sending on a connection that is already closing, which is the documented no-report shape for a dead connection.
  - A disabled plugin does nothing on either side: `RequestReportAsync` throws rather than putting `MSSP-REQUEST` on the wire that nothing would then collect, and a server does not answer on behalf of an `MSSPProtocol` that has been disabled. `ProtocolPluginManager` already refuses to disable `MSSPProtocol` while this plugin is enabled — that is what declaring the dependency buys — so the server-side check only matters for a consumer calling `ITelnetProtocolPlugin.OnDisabledAsync` directly, which is public and skips that check.
  - **Specification status, stated plainly:** the plaintext form is *not* on the current [specification page](https://tintin.mudhalla.net/protocols/mssp/), nor on the mudstandards mirror, but that page's own [changelog](https://tintin.mudhalla.net/protocols/mssp/news.php) records *"Mar 20, 2009 - Plaintext version of MSSP finalized and added to specification"*, and the implementation ships across the SMAUG family and is read by Grapevine's crawler. It was specified, the page no longer carries it, and it is deployed anyway. No version of it ever gave timing guidance. The framing here is matched against `Arthmoor/SmaugFUSS` (`src/comm.c`, `src/mssp.c`) and exercised against a scripted peer; **no live host was contacted to confirm it.**
- **`MSSPConfig.Source`** — which transport delivered a report: `MSSPSource.TelnetOption`, `MSSPSource.Plaintext`, or `MSSPSource.Unspecified` for a configuration built by hand rather than received. The two transports can disagree, so which one answered is provenance a consumer needs; it rides on the report rather than on the callback so that it survives being queued, stored or handed on. `OnMSSP` is unchanged, so no existing consumer needs rewiring.
- **`IInboundByteTransform` / `IOutboundByteTransform`, installed via `IProtocolContext.SetInboundByteTransform` and `SetOutboundByteTransformAsync`** — a general seam for a protocol whose negotiation changes what every byte after it *means*, rather than MCCP knowledge hard-coded into the interpreter. `TelnetInterpreter.ProcessBytesAsync` had no interception point at all: it fired every byte off the channel straight into the state machine.
  - Inbound, the transform sits between the network and the state machine: one wire byte in, zero or many telnet bytes out. Outbound, it sits between the library and the network, inside the existing write lock, so a stateful encoder sees writes in the order the network will.
  - Installation from a state machine handler takes effect from the next byte, because handlers run on the byte-processing loop itself. MCCP relies on this: the inflater goes in on the way *out* of the completing state — the moment the marker's trailing `SE` is consumed — because that state is entered on the marker's second `IAC`, and installing on entry would feed the `SE` itself to zlib.
  - `SetOutboundByteTransformAsync` takes the interpreter's write lock, and takes an optional `sendFirst` payload it writes in the clear before the switch-over, because both halves are otherwise racy. Swapping without the lock can dispose an encoder a write is inside — an ordinary `IAC DONT MCCP2` arriving while another thread is writing was enough to throw `ObjectDisposedException` out of `WriteToNetworkAsync`, and for a zlib deflater the same window is a native use-after-free. Sending the marker and installing as two steps lets another thread's write land between them, going out in the clear after the peer has already started inflating, which destroys the peer's zlib stream.
  - The inbound side reaches the same guarantee without a lock, because only the byte-processing loop calls a decoder: swapping publishes the new transform atomically and hands the old one to the loop, which disposes it between bytes — the one point where it is provably not inside one. Disposal cannot be left to the caller, since `ProtocolPluginManager.DisablePluginAsync<T>()` is public and a consumer disabling MCCP mid-stream would otherwise dispose the inflater while the loop was reading from it: an `ObjectDisposedException` on the processing loop, which the loop's own handler turns into the connection going silently deaf, and underneath it a native use-after-free. The per-byte cost is one `volatile bool` read; the queue is only touched after an actual retirement.
  - The inbound seam is asynchronous (`DecodeAsync`) purely so a decoder that fails terminally can report it before returning. Decoding is pure computation, and the MCCP implementation returns an already-completed `ValueTask`, so the per-byte path stays allocation free.
  - **The interpreter feeds transforms exactly one byte per call, and that is load-bearing.** DEFLATE expands at most 1032:1 from a single input byte, so an inflater's output buffer plateaus at 2 KiB regardless of what the peer sends — a 16 MiB zip bomb arrives as 1032 bytes per call, 16,315 times, with the buffer never exceeding 2,048 bytes. Batching the feed would make that ceiling 1032 × batch size, chosen by the peer. Both the invariant and its reason are now pinned by a test.

### Changed
- **What a client puts on the wire about itself changed, deliberately, and a consumer relying on the old values must now set them.** No API was removed and everything still compiles; the difference is in the bytes.
  - TTYPE answered `TNC`, `XTERM`, `MTTS 3853`. It now answers `UNKNOWN`, plus an MTTS bitvector holding only what is true — `MTTS 4` for a client with nothing else registered, `MTTS 516` once NEW-ENVIRON is added. Pass `WithClientIdentity(...)` to be named, and `ClientIdentity.Mtts` to claim what the library cannot see. A consumer that wants the old three strings back can ask for them exactly: `.AddPlugin<TerminalTypeProtocol>().WithTerminalTypes("TNC", "XTERM", "MTTS 3853")`.
  - NEW-ENVIRON and ENVIRON answered a `SEND` with `USER` and `LANG`. They now answer with what was configured, which by default is nothing. A consumer that wants variables sent must pass them to `WithClientEnvironmentVariables` or set an identity. This is the direction the safety runs in: the fix cannot leak by omission.
  - `EnvironProtocol.WithClientEnvironmentVariables` and its builder extension take an `IReadOnlyDictionary<string, string>` rather than a `Dictionary<string, string>`. Source-compatible for any caller passing a `Dictionary`; a **binary** break for anything bound to the old signature and not rebuilt, alongside the ones 2.8.0 already carries.
- **`MCCPProtocol.CompressData` and `MCCPProtocol.DecompressData` are gone.** Both were dead, and `DecompressData`'s per-call `ZLibStream` was wrong for the protocol in a way no caller could have fixed. Compression is now a property of the connection, applied by the transforms above; there is nothing for a consumer to call, and there never was.
- **`IsMCCP2Enabled` and `IsMCCP3Enabled` now mean compression is actually running** in that direction — this side is deflating its output or inflating its input — rather than that a negotiation went by. On the client `IsMCCP2Enabled` becomes true at `IAC SB MCCP2 IAC SE`, not at `WILL MCCP2`; on the server `IsMCCP3Enabled` becomes true when the client's marker arrives, not at `DO MCCP3`. `OnCompressionEnabled` fires at the same moments.
- The MCCP subnegotiation transitions are now registered per mode: only the client listens for `IAC SB MCCP2 IAC SE` and only the server listens for `IAC SB MCCP3 IAC SE`, since those are the only directions in which either marker can legitimately arrive.
- A compressed stream that turns out not to be valid zlib can no longer be resynchronized, so the inflater stops for good: the error is logged, the matching `IsMCCPnEnabled` goes back to `false`, **`OnCompressionEnabled(version, false)` fires**, and nothing further is delivered from that direction. It no longer answers with `DONT`/`WONT` MCCP — MCCP has no way for a peer to stop compressing mid-stream, so that reply was noise on a connection that is already unreadable.
- **Starting compression is idempotent in both directions.** A peer chooses when markers arrive and can send a second one. Replacing a running inflater would throw away the deflate window the rest of the peer's stream is encoded against, losing every byte after it; restarting the deflater would do the same to the peer. A repeated `IAC SB MCCPn IAC SE`, or a repeated `DO MCCP2` for an option already in effect (RFC 854 loop avoidance), is now logged and ignored, and `OnCompressionEnabled` fires once rather than once per repeat.
- The four `OnDont*`/`OnWont*` handlers now share one stop path, so removing a transform, clearing the flag and notifying the consumer cannot drift apart between them.
- A host that switches on `System.IO.Compression.UseStrictValidation` makes `ZLibStream` report a base stream that has run dry as truncated data rather than returning 0. For MCCP that is never truncation — the peer has simply not sent the rest of the symbol yet — so it is now treated as "resume when more arrives", guarded so that genuine corruption, which is always detected on bytes the inflater already holds, still fails the stream. Without this, every MCCP connection in such a process died on the first partial read.
- **Reporting a window size has moved to the NAWS plugin and now covers RFC 1073's full range: `TelnetInterpreter.SendNAWS(short, short)` is gone, replaced by `NAWSProtocol.SendWindowSizeAsync(int, int)`.** RFC 1073 defines both dimensions as 16-bit **unsigned** fields, and says so as the reason the option exists at all — *"the 253 character height and width limitation is too low so the new option has a limit of 65535 characters"*. `short` reaches 32767, so the top half of that range was only expressible by passing a negative two's-complement bit pattern: `SendNAWS(-1, -1)` really did put 65535 on the wire, and nothing said so. 2.7.0 widened the **receive** path to the full unsigned range, so the library read 0–65535 and wrote 0–32767; that asymmetry is now closed. Everything else about NAWS — the callback, `ClientWidth`/`ClientHeight`, the `DO NAWS` gate, the escaping — is unchanged, and the widened sender round-trips through the existing reader, including 65535 × 65535, whose eight `0xFF` payload bytes are all doubled as the same RFC requires.
  - **Two breaks, both requiring a code change rather than just a rebuild.** A caller of `interpreter.SendNAWS(w, h)` no longer compiles: the entry point is now `interpreter.PluginManager!.GetPlugin<NAWSProtocol>()!.SendWindowSizeAsync(w, h)`, which is how every other protocol's operations are already reached (`CharsetProtocol.SendTTableAsync`, `AuthenticationProtocol.SendAuthenticationRequestAsync`, and so on). Anything compiled against the old `SendNAWS(int16, int16)` and not rebuilt gets `MissingMethodException`. 2.8.0 is binary-breaking regardless — `MCCPProtocol.CompressData`/`DecompressData` are gone in the same release — so consumers recompile once.
  - **Behaviour change: an out-of-range dimension now throws instead of wrapping.** `SendWindowSizeAsync` rejects anything outside 0–65535 with an `ArgumentOutOfRangeException` naming the parameter, because truncating an `int` into the two bytes the wire has would report a size nobody asked for: 65536 would go out as 0. This retires the `SendNAWS(-1, -1)` trick — a negative value is no longer reinterpreted as its unsigned form, it throws. Pass `65535` (or `NAWSProtocol.MaxWindowDimension`).
  - **Every unreachable duplicate of NAWS's own logic goes with it, three of them public.** Once the sender moved to the plugin, `Interpreters/TelnetNAWSInterpreter.cs` was left holding nothing but copies of what `NAWSProtocol` already does: a private `CompleteNAWSAsync` with its own capture buffer, wired into no state machine and kept alive only by a `#pragma warning disable CS0414`; and the public `RequestNAWSAsync`, whose two apparent call sites both bind to the plugin's own private overload taking an `IProtocolContext`, together with the `_WillingToDoNAWS` flag and cached `DO NAWS` bytes that existed only to serve it. `NAWSProtocol` itself held the public `ProcessNAWSByte(byte)` and `CompleteNAWSNegotiationAsync()`, plus the internal `OnNAWSNegotiatedAsync` that only the interpreter's dead copy called. Not one of them had a caller in the library, the tests, TestClient or TestServer, and the duplicated readers had already drifted apart — `CompleteNAWSNegotiationAsync` reassembled the dimensions with `+` where the live path uses `|`, so it never received the unsigned-range fix. Its only consumer hook was a `NAWS_Callback` shared-state key that nothing in the repository ever writes, read from inside the unreachable method itself, so it could not have fired even if a consumer had set it. Window size has always arrived through `.OnNAWS(...)` and the state machine's own `CaptureNAWS`/`CompleteNAWSAsync`. **Removing `RequestNAWSAsync`, `ProcessNAWSByte` and `CompleteNAWSNegotiationAsync` is a further binary break** on top of `SendNAWS` — `MissingMethodException` for anything bound to them — and there is nothing to migrate to, because none of that code ever ran. `TelnetInterpreter.ClientWidth` and `ClientHeight` are untouched: they are the live properties 2.7.0 widened to the full unsigned range, and the plugin writes them as each subnegotiation completes.

### Removed
- **`EORProtocol.SendEORMarkerAsync()` and `MSSPProtocol.ProcessMSSPMessageAsync()` are gone.** Both were public, both were unreachable, and each existed to invoke a shared-state callback key — `"Prompting_Callback"` and `"MSSP_Callback"` — that nothing in the repository has ever written. Every `SetSharedState` call in the library writes a `*_Protocol` key; across the library, the unit tests, TestClient, TestServer, `Functional`, the analyzers, the source generators and their generated output, no `*_Callback` key is written anywhere, so neither branch could fire even for a consumer who called `IProtocolContext.SetSharedState("MSSP_Callback", …)` themselves. This is the same shape as the `NAWS_Callback` removed above, and the last of it.
  - **`SendEORMarkerAsync` never sent an EOR marker.** Past its two guards and a debug log, the dead branch was its entire body — no `IAC EOR` was ever written — so calling it did nothing at all. The marker goes on the wire from `TelnetInterpreter.SendPromptAsync`, which this release taught to pick a terminator from what the peer negotiated. The inbound direction is untouched: a prompt received from a peer still reaches `.OnPrompt(...)` through the state machine's `State.Prompting`.
  - **`ProcessMSSPMessageAsync` was an unreachable duplicate of `ReadMSSPValues`**, the live handler the state machine runs on entry to `State.CompletingMSSP`, differing only in delivering its report to the never-written key instead of to `.OnMSSP(...)`. Received reports have always arrived through `.OnMSSP(...)`, from both transports.
  - **A binary break** — `MissingMethodException` for anything bound to either name — on top of the ones 2.8.0 already carries, and there is nothing to migrate to, because neither method ever ran to any effect. Send a prompt with `SendPromptAsync`; receive MSSP with `.OnMSSP(...)`.
- **`MSSPProtocol.AddMSSPVariableByte`, `AddMSSPValueByte`, `CompleteMSSPVariable` and `CompleteMSSPValue` are gone.** Four public methods for hand-driving the MSSP field parser, none of them called anywhere in the library, the tests, TestClient, TestServer, `Functional`, the analyzers or the source generators. They were left behind by the plugin migration alongside `ProcessMSSPMessageAsync`, which was the only thing that ever finished what they started.
  - With that method removed above, they became worse than dead: they write `_currentFieldIsValue` and call `FlushField` on the *same* parser state the live `ReadMSSPValues` handler uses, with nothing left to terminate a report. A consumer calling them could begin a report that could never complete, and corrupt a genuine subnegotiation already in flight. A public method that can break live parsing is worse than one that merely does nothing.
  - **A binary break**, with nothing to migrate to: they could not produce a report even when `ProcessMSSPMessageAsync` existed to end one, because nothing ever called either. Received reports arrive through `.OnMSSP(...)`; a report is sent from a configured `MSSPConfig`. The receive path is untouched — `ReadMSSPValues` still owns `_msspBytes`, `_currentFieldIsValue`, `_fieldStart`, `FlushField`, `BuildReceivedConfig` and `ClearMSSPState`, and the existing `MSSPTests` and `MSSPSpecificationTests` cover it unchanged.
- **`TelnetInterpreter.CharsetOrder` is gone.** The `init`-only property was never read: it wrote a delegate that only the interpreter's own copy of the charset offering used, and that copy was reachable only from a `Lazy<byte[]>` nothing ever evaluated. Charset order has been `CharsetProtocol.CharsetOrder` since the plugin migration, which is what `.WithCharsetOrder(...)` has always configured, so code using the builder is unaffected; code setting `CharsetOrder` in a `TelnetInterpreter` object initializer must move to `.WithCharsetOrder(...)`.

## [2.7.0]

### Fixed
- **MSSP was decoded as ASCII rather than the negotiated character set** — `MSSPProtocol` read and wrote every variable name and value with `Encoding.ASCII`, so a server whose `NAME`, `WEBSITE` or `CONTACT` is not ASCII arrived as question marks, one per *byte*: `NAME "Café Noir"` reached `OnMSSP` as `"Caf?? Noir"` under UTF-8 and `"Caf? Noir"` under ISO-8859-1. Every other subnegotiation reader in the library decodes through `TelnetInterpreter.CurrentEncoding`; this one did not, and neither did the send path, so a report received from a peer could not round-trip either.
  - The specification mandates no encoding. Its only byte-level rule is *"For ease of parsing, variables and values cannot contain the MSSP_VAL, MSSP_VAR, IAC, or NUL byte"* — four byte values, not a character set — and its own `CHARSET` variable is documented as reporting *"ASCII, BIG5, CP437, CP949, CP1251, EUC-KR, GB18030, ISO-8859-1, ISO-8859-2, KOI8-R, UTF-8"*, so a protocol whose vocabulary includes saying "I am GB18030" cannot be read as ASCII. Both directions now use `CurrentEncoding`, which is UTF-8 until RFC 2066 CHARSET negotiation settles on something else.
  - The write path now doubles an `IAC` byte among a value's encoded bytes (RFC 854: *"the IAC need be doubled to be sent as data"*). MSSP forbids that byte in a value, so this should never fire — but under ISO-8859-1 the single character `ÿ` encodes to `0xFF`, and an unescaped one would end the subnegotiation in the middle of the report.
  - **Behaviour change:** a consumer that has been reading `?` for non-ASCII MSSP data now receives the real text. Anything downstream that assumed `MSSPConfig` strings were ASCII-only — a fixed-width column, a byte-length assumption, a comparison against the mangled form — needs re-checking.
- **An escaped `IAC IAC` inside an MSSP field lost the literal byte** — `IAC IAC` in a subnegotiation is one 0xFF data byte, and the state machine's un-escape transitions (`EscapingMSSPVal → EvaluatingMSSPVal` and `EscapingMSSPVar → EvaluatingMSSPVar`, both on `Trigger.IAC`) had no capture handler registered, because the handlers are registered for every trigger *except* `IAC`. A value of `a<IAC><IAC>b` arrived as `"ab"`. Both halves of the hole are fixed; the same payload now arrives as `"aÿb"` under ISO-8859-1. MSSP forbids `IAC` inside a field, so this is only reachable on a malformed report — but RFC 854 framing still decides what the bytes mean, and a byte the peer took the trouble to escape must not be deleted in silence.
- **MSSP had no payload size cap** — GMCP, MSDP and CHARSET's TTABLE have been bounded at a configurable 1 MiB since 2.6.5; MSSP's field buffer was unbounded, so a 200 KB value arrived whole and a hostile or broken server decided how much the client allocated. This matters most for a crawler, which connects to servers it does not trust by definition. MSSP now uses the same `SubnegotiationBuffer` with the same behaviour at the ceiling: the report is **dropped rather than truncated**, with an `Error` log naming the byte count and the limit, plus the new `.OnMSSPMessageTooLarge((ReceivedBytes, MaxMessageSize))` callback. Configurable via `MSSPProtocol.MaxMessageSize` and `.WithMaxMessageSize(bytes)`, matching GMCP and MSDP.
  - The bound is over the **whole subnegotiation payload** — every variable name and value together — not over one field: a report of a hundred thousand tiny variables costs the same memory as one enormous value.
  - **Behaviour change:** an MSSP report whose payload exceeds 1 MiB is no longer delivered. No real server sends one; a consumer that wants the old behaviour can raise `MaxMessageSize`.
- **`SendNAWS` emitted a dimension byte of 255 unescaped** — RFC 1073's subnegotiation is `IAC SB NAWS WIDTH[1] WIDTH[0] HEIGHT[1] HEIGHT[0] IAC SE`, and the RFC is explicit that *"any occurrence of 255 in the subnegotiation must be doubled to distinguish it from the IAC character (which has a value of 255)"*. The four dimension bytes were written raw, so the peer read the 255 as the `IAC` that ends the subnegotiation. Measured by round-tripping a client's output through a server interpreter: `SendNAWS(255, 62)` produced `FF FA 1F 00 FF 00 3E FF F0`, the server's NAWS callback never fired, and the user's next line arrived as `">connect wizard hunter2"` — the height byte glued to the front of it. `SendNAWS(80, 255)` wedged the server in `EvaluatingNAWS` and swallowed the following line entirely. This is an ordinary terminal size, not a corner case. The receive side was already correct, so the fixed sender round-trips against the existing reader.
- **An unknown subnegotiation permanently killed the byte-processing loop** — `IAC SB 99 'a' 'b' IAC SE`, for any option with no registered plugin, logged `Bad transition from BadSubNegotiationEvaluating with trigger IAC`, threw `InvalidOperationException: Multiple permitted exit transitions are configured from state 'BadSubNegotiationEvaluating' for trigger 'Error'`, and `ProcessBytesAsync` exited: zero submissions for the rest of the connection. Reachable from any peer speaking ATCP (200), ZMP or MSP. Only the empty form `IAC SB <opt> IAC SE` recovered. RFC 855: *"the receiver may locate the end of a parameter string by searching for the SE command (i.e., the string IAC SE), even if the receiver is unable to parse the parameters."* `BadSubNegotiationEvaluating` now has that `IAC` transition, the payload is skipped, and the log names the option instead of just saying "Unsupported SubNegotiation".
  - The underlying cause was that `Trigger.Error` — the recovery trigger the interpreter fires at itself, value 257, "outside of what a byte can contain" — was in the set every `TriggerHelper.ForAllTriggers*` loop enumerates. Each such loop handed its state a second, unguarded `Error` transition on top of the safe interpreter's single `Permit(Trigger.Error, State.Accepting)`; Stateless only notices the ambiguity at fire time, which is precisely when recovery is being attempted. **22 states were affected**, including `ReadingCharacters`, `Willing`, `Refusing`, `Do`, `Dont`, `SubNegotiation`, `EvaluatingNAWS`, `EvaluatingTerminalType`, `EvaluatingCharset` and `EvaluatingLINEMODE`. `Trigger.Error` is now excluded at the source (`TriggerHelper.DataTriggers`), which fixes every one of them.
  - One malformed byte can no longer end a connection: a throw out of the state machine is now caught per byte, logged with the byte, trigger and state, and the loop continues. Previously any such throw escaped the `await foreach` and every subsequent byte on the socket was discarded for the life of the connection.
- **The default ECHO handler re-emitted a bare `0xFF`** — the byte handed to the handler has already been un-escaped, so a peer's correctly-doubled `IAC IAC` came back as a single `0xFF` and desynced the peer's parser. Reachable from one ordinary keypress: `ÿ` in ISO-8859-1, `я` in CP1251, `Ъ` in KOI8-R. Measured end to end: the peer sent `61 FF FF 62` and read back only `61`. RFC 854: *"only the IAC need be doubled to be sent as data"*. The handler now routes through the same escaping `SendAsync` applies.
- **A five-byte NAWS payload killed the connection** — `NAWSProtocol.CaptureNAWS` guarded with `_nawsIndex > _nawsByteState.Length`, which let the index reach 4 on a four-byte array and threw `IndexOutOfRangeException` out of the state machine. Surplus payload bytes are now dropped; RFC 1073 defines exactly four.

### Changed
- **NAWS dimensions above 32767 are now read unsigned.** RFC 1073's stated motivation for replacing NAOL/NAOP was range — *"the 253 character height and width limitation is too low so the new option has a limit of 65535 characters"* — but the pair was read with `BitConverter.ToInt16`, so `0xFFFF` reached `OnNAWS`, `ClientWidth` and `ClientHeight` as `-1` and `40000` as `-25536`. These properties and the callback are already `int`, so no signature changes; a consumer that special-cased the negative values needs re-checking.

## [2.6.5] - 2026-07-30

### Fixed
- **Silent truncation of large subnegotiation payloads (GMCP, MSDP, CHARSET TTABLE)** — GMCP messages were capped at 8192 bytes across the whole payload (package name, separator and data together), and everything past that was dropped. There was no error, no exception and no signal the consumer could observe: the callback fired with a shortened string, which for any JSON payload means invalid JSON that cannot be told apart from a malformed server. Measured with a client-mode interpreter: `"Ab " + 8189` bytes arrived whole, `"Ab " + 8190` arrived one byte short, and a 15-character package name left 8176 bytes for the data. The `Warning` that was logged did not help — it fired at exactly 8192 bytes whether or not anything had been lost.
  - MSDP shared the ceiling (its own 8192-byte constant, with a warning logged *per dropped byte*), as did the CHARSET TTABLE buffer (a fixed 8192-byte array); a table for a 16-bit character set legitimately exceeds it. MXP, MSSP, NAWS, TERMINAL-TYPE, ENVIRON/NEW-ENVIRON and the other subnegotiations do not accumulate an unbounded payload and were unaffected.
  - All three now share one bounded accumulator (`SubnegotiationBuffer`), which records that it overflowed instead of quietly discarding bytes. Neither the GMCP nor the MSDP specification defines a maximum message size, so the ceiling is a library policy: **1 MiB per message by default**, configurable via `GMCPProtocol.MaxMessageSize` / `MSDPProtocol.MaxMessageSize` / `CharsetProtocol.MaxTTableSize` and the matching `.WithMaxMessageSize(bytes)` / `.WithMaxTTableSize(bytes)` fluent methods.
  - At the ceiling the message is **dropped rather than truncated**, and said out loud: an `Error` level log naming the package and byte count, plus the new `.OnGMCPMessageTooLarge(...)` / `.OnMSDPMessageTooLarge(...)` callbacks for consumers that want to observe it. An oversized TTABLE is answered with `TTABLE-REJECTED` (RFC 2066). The connection is unaffected and the next message is processed normally.
  - Small messages, the overwhelmingly common case, allocate *less*: the per-message `Channel<byte>` (allocated fresh for every GMCP and MSDP subnegotiation) and the copy out of it are gone, replaced by a reused `List<byte>` that releases its backing array only after an unusually large message.
  - Removed the unreachable duplicates of the GMCP and MSDP receive paths in `Interpreters/TelnetGMCPInterpreter.cs` and `Interpreters/TelnetMSDPInterpreter.cs`, which carried their own copies of the 8192-byte limit but were never wired into the state machine. `SendGMCPCommand` is unchanged.

## [2.6.3] - 2026-07-30

### Fixed
- **MSSP multi-value variables, booleans and unknown variables** — the MSSP reader could not represent what the protocol sends, and destroyed the data before any consumer saw it.
  - Every `MSSP_VAL` under one `MSSP_VAR` was accumulated into a single byte buffer with no separator, so `PORT "80" "23" "4201"` arrived as the integer `80234201`. The specification says "It's also possible to attach several values to a single variable by using MSSP_VAL more than once, with the default value reported last."
  - The same variable repeated (`MSSP_VAR "PORT" MSSP_VAL "80" MSSP_VAR "PORT" MSSP_VAL "23"`) kept only whichever value happened to be bound last, and paired names to values by index, so one malformed field misaligned the whole report.
  - `REFERRAL` — a list by definition, and the variable a crawler runs on — came out `null`: the run-together string could not bind to its list-typed property and was dropped.
  - Booleans (`ANSI`, `UTF-8`, `PAY TO PLAY`, …) were parsed with `bool.TryParse`, which rejects MSSP's `1`/`0`, so every one of them was dropped.
  - Variables with no property on `MSSPConfig` were discarded rather than collected, and `MSSPConfig.Extended` was never populated on receive.
  - Variable names were matched case-sensitively after `ToUpper()` (culture-sensitive) and without the specification's recommended underscore-for-space substitution, so a server sending `CRAWL_DELAY` or `MINIMUM_AGE` was ignored.
  - A payload whose last field was a variable name with no value (`… MSSP_VAR "FOO" IAC SE`) hit an unhandled trigger and left the MSSP state machine wedged for the rest of the connection.
- **Bodyless GMCP messages were dropped** — a message with no data section, such as `Core.Ping`, never reached `OnGMCPMessage`. The parser required a space separator unconditionally, so the one form the specification prescribes for a command without data was the one form that was discarded: *"The `<data>` field is optional and should be separated from the package field with a space. When sending a command without a data section the space should be omitted."* The same message *with* a trailing space was delivered fine. Such messages are now delivered with `Package = "Core.Ping"` and `Info = ""` — an empty string rather than a fabricated `"{}"`, so the callback reports what was actually on the wire (Mudlet substitutes `{}` because its Lua API has no way to express "no data section"; this one does).
- **A GMCP message whose data ran into the package name was dropped without naming it** — `Char.Vitals{"hp":1}` is malformed by that same sentence, and was discarded with a log line that did not say which package it had been. A server spelling it that way was indistinguishable from a server with no GMCP at all. A package name cannot contain `{`, so the message is now split at the first character that cannot belong to a package name, delivered, and logged as a `Warning` naming the package — tolerated, but never in silence. A payload with no package name at all (`{"hp":1}` on its own) is still discarded, now with a warning quoting what was thrown away; previously it was delivered with an empty package name.
- **MSDP over GMCP delivered the package name instead of the message** — a MoG message (`IAC SB GMCP 'MSDP {"LIST" : "COMMANDS"}' IAC SE`) had its data section discarded, and the *package name bytes* were handed to the MSDP byte-encoding scanner instead. Every MoG message, whatever it carried, reached `OnMSDPMessage` as the four characters `"MSDP"`; the body never arrived at all. The GMCP specification carries MSDP as JSON, not as MSDP's own encoding — *"The data field must use the JSON data syntax with keywords being case sensitive using UTF-8 encoding"* — and that is the same shape `OnMSDPMessage` already receives from a native `IAC SB MSDP` subnegotiation (MSDP tables are JSON objects, MSDP arrays are JSON arrays), so the data section is now forwarded verbatim. No callback signature changes.
  - The package name is matched case sensitively, as the specification requires: *"When using MoG (MSDP over GMCP) the package name is considered case sensitive and MSDP must be fully capitalized."* A `msdp` package is an ordinary GMCP package.
  - A data section that is not valid JSON is discarded with an `Error` log naming the payload, rather than forwarded to a callback whose contract is JSON. Parsing happens inside the protocol, so a malformed payload cannot throw onto the read loop, and the connection continues with the next message.
  - A MoG message received with no `MSDPProtocol` plugin registered is now delivered to `OnGMCPMessage` with `Package = "MSDP"`, instead of being dropped silently.

### Added
- **`MSSPConfig.Variables`** — an ordered, canonicalized variable name → value **list** map holding everything a peer reported, which the strongly typed properties are projected from. Scalar properties take the last value (the specification's default); list-typed properties take all of them. `Default`, `Flag` and `Integer` read the default value in the shape a caller usually wants, and `OfficialNames` / `UnofficialNames` partition a report against the specification's tables.
- **`MSSPVariables`** — `Canonicalize` (upper case, underscores folded to spaces, whitespace runs collapsed), `IsOfficial`, `IsKnown`, and the official name list.
- **`MSSPConfig.Charset` and `MSSPConfig.Discord`** — official Generic variables that were missing from the model. `CHARSET` is array-capable.
- **`MSSPConfigAccessor.TrySetValues`** — binds every value of a variable at once. `TrySetProperty` is unchanged in signature and now defers to it.
- When a received `MSSPConfig` is sent back out, `Variables` is written verbatim — arrays and unknown variables included — and the typed properties and `Extended` supply only names it does not mention. A configuration built by hand has an empty map and sends exactly what it always did.

### Changed
- `MSSPConfig.MSP` is now marked `[Official(false)]`. `MSP` is not in the specification's official variable tables.

## [2.6.0] - 2026-07-29

### Added
- **Keep-alive** — opt-in idle keep-alive that stops NAT tables, load balancers and idle timers from dropping a quiet connection:
  - `.WithKeepAlive(TimeSpan? interval = null, Func<TelnetInterpreter, CancellationToken, ValueTask>? sendAsync = null)` on `TelnetInterpreterBuilder` (and on a plugin configuration chain). Off unless called, so upgrading changes nothing for existing consumers.
  - The interval is an **idle** window, not a fixed heartbeat: it is restarted by every outbound write through `WriteToNetworkAsync`, so a connection that is already sending data sends nothing extra. Defaults to 30 seconds (`TelnetInterpreter.DefaultKeepAliveInterval`).
  - The interval is bounded: at least 1 second (`TelnetInterpreter.MinimumKeepAliveInterval`) and at most 24 hours (`TelnetInterpreter.MaximumKeepAliveInterval`), inclusive. Out-of-range values throw `ArgumentOutOfRangeException` naming both bounds rather than being clamped, and the check applies both to `.WithKeepAlive(...)` and to `BuildAsync()` for anyone assigning the `KeepAliveInterval` init property directly. Under a second a keep-alive is a flood — no idle timeout it defends against is that short — and over a day it cannot keep anything alive, besides approaching the interval at which `Task.Delay` itself throws (over `int.MaxValue` ms on .NET Framework, reachable through the `netstandard2.0` target).
  - The payload is overridable via `sendAsync`; the default is `IAC NOP` (RFC 854), also available on its own as `TelnetInterpreter.SendKeepAliveAsync()`.
  - Works in both client and server mode. A failing send (typically a vanished peer) is logged as a warning and stops the keep-alive for that connection; it is never rethrown onto the host application and does not disturb the byte-processing loop.
  - **Note:** a NOP keep-alive is not peer-liveness detection. A successful send only proves the local write succeeded, not that anyone is still listening — the peer is not required to answer. Verifying the peer is alive needs a round trip it must answer, such as TIMING-MARK (RFC 860, option 6), which this library does not implement.

## [2.5.3]

### Fixed
- **NAWS negotiation direction (RFC 1073)** — the client offered `DO NAWS` (asking the server for a window it has no concept of) and then sent an unsolicited `SB NAWS` because `SendNAWS`'s guard was a no-op. A strict server answered the stray `DO NAWS` with `WONT`, and the unsolicited subnegotiation then desynced its parser and swallowed the following line, making typed logins intermittently bounce back to the login screen. The client now offers `WILL NAWS` and only reports its size once the server enables it with `DO NAWS`.

## [2.5.2]

### Fixed
- **CHARSET initiation (RFC 2066)** — the CHARSET plugin registered its proactive `WILL CHARSET` offer in both client and server mode. Two peers both offering `WILL CHARSET` collided and never resolved, leaving a stuck CHARSET state that discarded the client's first line. The initial offer is now gated to server mode, matching how GMCP and MCCP gate theirs.

## [2.5.1]

### Fixed
- **Missing `FSharp.Core` dependency** — the package bundles the F# assembly `TelnetNegotiationCore.Functional.dll` (MSDP support) but its `FSharp.Core` dependency was not declared in the nuspec (the F# project is referenced with `PrivateAssets="all"`). Consumers therefore never restored `FSharp.Core`, and the first MSDP negotiation threw `Could not load file or assembly 'FSharp.Core'` — a hard native `SIGSEGV` under Mono / .NET-Android. `FSharp.Core` is now declared as an explicit package dependency (pinned to 10.1.203 to match the bundled assembly).

## [2.5.0]

### Added
- **MXP Protocol** — `MXPProtocol` plugin implementing MUD eXtension Protocol (telnet option 91) handshake negotiation. Server sends `IAC WILL MXP`, client responds `IAC DO MXP`. Included in `AddDefaultMUDProtocols()`.
- `.OnMXPEnabled(() => ...)` fluent callback for MXP negotiation success
- `MXPProtocol.IsMXPActive` property to check negotiation state

## [2.4.2]

### Added
- **Automatic pipe/stream wiring** — `BuildAndStartAsync` overloads on `TelnetInterpreterBuilder` and `PluginConfigurationContext<T>` eliminate boilerplate read loops and `OnNegotiation` wiring:
  - `BuildAndStartAsync(IDuplexPipe, CancellationToken)` — wires negotiation output to `pipe.Output`, starts read loop; returns `(TelnetInterpreter, Task readTask)`
  - `BuildAndStartAsync(Stream, CancellationToken)` — wraps any `Stream` (e.g. `NetworkStream`, `SslStream`) using `PipeReader.Create` / `PipeWriter.Create`
  - `BuildAndStartAsync(TcpClient, CancellationToken)` — convenience overload; delegates to the `Stream` overload via `client.GetStream()`
  - `UsePipe(IDuplexPipe)` — wires `OnNegotiation` to `pipe.Output`; leaves read loop to the caller
  - `UseStream(Stream)` — wires `OnNegotiation` via `PipeWriter.Create(stream)`; leaves read loop to the caller
  - `ReadFromPipeAsync(TelnetInterpreter, PipeReader, CancellationToken)` — static helper that drives the standard read loop
- Added `System.IO.Pipelines` as an explicit package reference for `net8.0` and `netstandard2.0` targets
- **Dependency injection integration** — `AddTelnetServer()` and `AddTelnetClient()` extension methods on `IServiceCollection` register an `ITelnetInterpreterFactory` that creates pre-configured `TelnetInterpreterBuilder` instances with mode and logger resolved from DI:
  - `ITelnetInterpreterFactory` — factory interface; call `CreateBuilder()` to get a fresh builder per connection
  - `TelnetServiceCollectionExtensions.AddTelnetServer(Action<TelnetInterpreterBuilder>?)` — registers the factory in server mode
  - `TelnetServiceCollectionExtensions.AddTelnetClient(Action<TelnetInterpreterBuilder>?)` — registers the factory in client mode
  - Added `Microsoft.Extensions.DependencyInjection.Abstractions` as a package dependency
- **Modernized test projects** — TestServer updated from legacy `WebHost.CreateDefaultBuilder` + `Startup` class to `WebApplication.CreateBuilder()` minimal hosting; TestClient updated from `Host.CreateDefaultBuilder` to `Host.CreateApplicationBuilder()`; both now use `ITelnetInterpreterFactory` from DI

## [2.3.0] - 2026-02-13

### Performance Improvements
- **GMCP Protocol**: Optimized message parsing using `CollectionsMarshal.AsSpan()` for .NET 5+ to eliminate 2 `ToArray()` allocations per message
- **MSSP Protocol**: Optimized string encoding operations using `CollectionsMarshal.AsSpan()` to avoid intermediate array allocations
- **NAWS Protocol**: Replaced `BitConverter.GetBytes()` with `BinaryPrimitives.WriteInt16BigEndian()` and `stackalloc` for explicit big-endian encoding and improved performance on .NET 5+
- **TelnetStandardInterpreter**: Simplified `WriteToOutput()` method by removing unnecessary ArrayPool pattern
- **Documentation**: Added inline comments explaining design decisions for performance-critical code paths

## [2.0.0] - 2026-01-19

### Added
- **Plugin Architecture**: Class-based plugin system for protocol management
  - `ITelnetProtocolPlugin` interface for type-safe protocol contracts
  - `TelnetProtocolPluginBase` abstract base class
  - `ProtocolPluginManager` for dependency resolution and lifecycle management
  - `IProtocolContext` for plugin-to-plugin communication
  - `TelnetInterpreterBuilder` fluent API for construction
- **System.Threading.Channels Integration**: High-performance async byte processing
  - Bounded channel with 10,000 byte capacity for automatic backpressure
  - Non-blocking `InterpretAsync()` and `InterpretByteArrayAsync()` operations
  - Background processing with graceful shutdown via `IAsyncDisposable`
- **DOS Protection**: 8KB message size limits for GMCP and MSDP protocols
- **Protocol Plugins**: All 8 protocols migrated to plugin architecture
  - `GMCPProtocol` - Generic MUD Communication Protocol
  - `MSDPProtocol` - MUD Server Data Protocol
  - `NAWSProtocol` - Negotiate About Window Size (RFC 1073)
  - `TerminalTypeProtocol` - Terminal Type (RFC 1091 + MTTS)
  - `CharsetProtocol` - Character encoding (RFC 2066)
  - `MSSPProtocol` - MUD Server Status Protocol
  - `EORProtocol` - End of Record
  - `SuppressGoAheadProtocol` - Suppress Go-Ahead
- **Configurable Buffer**: `MaxBufferSize` property for line buffer (default 5MB)
- **Fluent Configuration Extensions**: Inline protocol configuration methods
  - `WithCharsetOrder()` - Configure charset order fluently on CharsetProtocol
  - `WithMSSPConfig()` - Configure MSSP settings fluently on MSSPProtocol
  - `AddDefaultMUDProtocols()` overload with optional parameters for inline configuration of all protocol callbacks and settings (onNAWS, onGMCPMessage, onMSSP, msspConfig, onMSDPMessage, onPrompt, charsetOrder)

### Changed
- Library architecture modernized with plugin-based design patterns
- Improved performance with non-blocking async operations
- Enhanced testability with independent protocol implementations

### Security
- Added comprehensive input validation and size limits
- Implemented automatic backpressure to prevent memory bloat
- DOS protection for protocol message buffers

**Note**: The legacy API remains fully supported for backward compatibility. All existing code will continue to work without modifications.

## [1.1.1] - 2024-12-30

### Fixed
- Fixed GMCP message receiving bug where the package name was incorrectly duplicated instead of the JSON message content being parsed.

### Added
- Added comprehensive test suite for GMCP functionality covering both client and server send/receive operations.

## [1.1.0] - 2025-03-16

### Changed
- Use nullable language feature for better null checks.
- Mark required items are required to assist with Validation.
- Adjusted F# code to use language features and more constants.
- Added caching for Byte -> Trigger mapping for faster performance.

## [1.0.9] - 2024-11-17

### Changed
- Fix a bug in 1.0.8 by downgrading the Stateless package.

## [1.0.8] - 2024-11-17

### Changed
- Use ValueTasks instead of Tasks for improved performance.

## [1.0.7] - 2024-03-19

### Changed
- Get NuGet to play nice about dependencies.

## [1.0.6] - 2024-01-09

### Changed
- Replaces 1.0.5, which was an invalid package update.
- Removed MoreLINQ dependency by making a copy of the function I needed and keep dependencies lower. License retained in the source file - to abide by Apache2 License. 

## [1.0.5] - 2024-01-09

### Changed
- Removed MoreLINQ dependency by making a copy of the function I needed and keep dependencies lower. License retained in the source file - to abide by Apache2 License. 

## [1.0.4] - 2024-01-09
  
### Changed
- Removed Serilog dependency in favor of Microsoft.Extensions.Logging.Abstractions, which allows one to inject the preferred logger.
 
## [1.0.3] - 2024-01-08
  
### Fixed
- Ensure that the Project Dependency on TelnetNegotiationCore.Functional is added as a DLL.
 
## [1.0.2] - 2024-01-07
  
### Added
- Add MSDP support.
- Added a helper function to convert strings to safe byte arrays.

### Changed
- Altered EOR functionality.

## [1.0.1] - 2024-01-03
  
### Added
- Add callback function for MSSP.
 
### Changed
- Target .NET 8.0.
- Change Methods to be properly async.
- Modernized TestClient example to use Pipes.
- Modernized TestServer example to use Pipes and Kestrel.
 
## [1.0.0] - 2024-01-03
  
Initial version.
 
### Added
- Initial support for RFC855 (TELOPT)
- Initial support for RFC858 (GOAHEAD)
- Initial support for RFC1091 (TTERM)
- Initial support for MTTS
- Initial support for RFC885 (EOR)
- Initial support for RFC1073 (NAWS)
- Initial support for RFC2066 (CHARSET)
- Initial support for MSSP
- Initial support for GMCP
