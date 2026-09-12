# MCP — MUD Client Protocol

[MCP 2.1](https://www.moo.mud.org/mcp/mcp2.html) is the out-of-band layer LambdaMOO and its
descendants carry over ordinary telnet text, on lines beginning `#$#`. It is not a telnet option:
there is no `IAC DO`, nothing to negotiate at the option level, and everything happens on assembled
lines of text.

It is **two plugins**, split where a consumer has an actual choice to make:

- **`MudClientProtocol`** — the session layer *and* its package negotiation. Framing, quoting, the
  version handshake, the authentication key, and the `mcp-negotiate` exchange that settles which
  packages the two sides share.
- **`McpCordProtocol`** — the `mcp-cord` package: named, typed channels multiplexed over the session,
  and the extension point that lets you define your own channel without a plugin in this library.

`mcp-negotiate` is a package in the specification's terms, versioned on its own (1.0, and 2.0 which
adds the line that ends the list). It lives in the session layer anyway, because it is the one package
**every MCP 2.1 implementation is REQUIRED to speak** — a session layer without it could only be built
into something non-conformant, so separating them offered a choice with exactly one correct answer at
the price of a second plugin to register and two places to look for one exchange. Packages you may
genuinely choose are their own plugins, depending on this one.

```csharp
.AddPlugin<MudClientProtocol>()
    .OnMcpMessage("dns-org-mud-moo-simpleedit-content", HandleEditAsync)
    .SupportsMcpPackage("dns-org-mud-moo-simpleedit", new McpVersion(1, 0), new McpVersion(1, 0))
    .OnMcpNegotiationComplete(agreed => { /* what both sides settled on */ })
```

**The handshake is asymmetric and the server opens it**, because nothing else can: a client has no
way to know MCP is on offer until the server says so. The two handshake messages are the protocol's
only exception to the key rule — the server's offer carries none at all, and the client's reply is
where the key is introduced rather than presented — so every message after them carries it.

```text
S: #$#mcp version: "2.1" to: "2.1"
C: #$#mcp authentication-key: "a3f1c0d29b4e7182" version: "2.1" to: "2.1"
C: #$#mcp-negotiate-can a3f1c0d29b4e7182 package: "mcp-negotiate" min-version: "1.0" max-version: "2.0"
C: #$#mcp-negotiate-end a3f1c0d29b4e7182
S: #$#mcp-negotiate-can a3f1c0d29b4e7182 package: "mcp-negotiate" min-version: "1.0" max-version: "2.0"
S: #$#mcp-negotiate-end a3f1c0d29b4e7182
```

The key is chosen by the **client** and adopted by the server, and from then on both sides *carry* it
as the first, unnamed argument of every **ordinary** message they send, rejecting any that arrives
without it. The two multiline frames are the exception and carry the data tag in that position
instead — `#$#* <tag> …` and `#$#: <tag>` — which is what authenticates them, since a tag is only ever
known to a peer this side already opened a message with. It
exists because anyone on a MUD can type `#$#` at the start of a line: without it, those keystrokes
would reach the other player's client as protocol. (This is a different mechanism from the `#$"`
quoting below, which is about ordinary output that happens to look like protocol — the key
authenticates messages, the prefix hides non-messages.) A key that is not a single unquoted token is
refused, because it is written back unquoted and could not survive the trip.

**Nothing MCP reaches your `OnSubmit`.** Handshake, messages, continuation lines and terminators are
all taken out of the stream. So is the quoting: a server in an MCP session prefixes any line of real
output that would otherwise look like protocol with `#$"`, and that prefix is stripped before the
line is delivered.

**As a server, send your output through `SendOutputAsync` while a session is up**, which is the
other half of that rule:

```csharp
var mcp = telnet.PluginManager!.GetPlugin<MudClientProtocol>()!;

await mcp.SendOutputAsync("#$#not a message, just text\r\n");   // goes out as #$"#$#not a message…
```

It quotes a line that begins `#$#`, and a line that begins `#$"` for the same reason in reverse —
unquoted, that one arrives at a peer which strips the prefix and the text loses three characters.
`#$#` in the *middle* of a line is not quoted, because nothing reads it as protocol there. Outside a
session nothing is quoted at all, since nothing is unquoting it. `MudClientProtocol.QuoteOutput` is
the same transformation on its own, for a caller that reaches the wire another way.

This is a separate call rather than a hook on the interpreter's own send path because quoting is a
line-level decision and that path is a byte stream: writing half a line, or several at once, is
ordinary use of it, and there is no honest place in the middle of that to decide what a line begins
with.

**A crawler wants the framing without the session.** `WithoutAnsweringMcpOffers()` consumes the
offer — it is protocol, and does not belong in a connect screen shown to a reader — but sends nothing
back, so no session is opened and every later `#$#` line is treated as it is outside one:

```csharp
.AddPlugin<MudClientProtocol>()
    .WithoutAnsweringMcpOffers()                        // read connect screens, never open a session
    .OnMcpOffered((lowest, highest) => Record("MCP"))   // the offer is still the evidence
```

`.OnMcpOffered` fires for every well-formed offer, whether or not this side answers it and whether or
not the ranges overlap, and reports the range the *server* named. For a client that declines it is the
only evidence there will be — `IsNegotiated` stays false, correctly, because no session was opened —
so a consumer recording what a peer supports does not have to open a session it does not want in order
to learn it. `MudClientProtocol.OfferedVersions` is the same fact as a property, and
`NegotiatedVersion` is what a session settled on (`min(server-max, client-max)`), null when none did.

Note that declining still leaves the *inbound* framing on: the offer is consumed, and so is any later
line-initial `#$#`, and a leading `#$"` is still stripped — none of those rules is conditional on a
session. **Outbound is the other way round**: `SendOutputAsync` and `QuoteOutput` add quoting only
while a session is up, because nothing is unquoting it before then and a peer with no session would
show the prefix to its reader as text.

Worth having: of the 57 lines beginning `#$#` across the connect screens MUIndex has stored, 54 are
exactly this offer — 37 written `#$#mcp version: 2.1 to: 2.1` and 17 with the versions quoted. Both
spellings are read. Answering them would put text on a stranger's login prompt for a session the
crawler will never use.

Two rules, the first from the specification and the second from the peer being a stranger:

- A line beginning `#$#` that fails to parse, carries an unknown message name, or carries the wrong
  key is **dropped, not displayed** — in a session or outside one. The specification is explicit:
  *"If an unrecognized or mangled MCP request is received, the implementation should either silently
  drop it on the floor, or notify the user in some reasonably unobtrusive way"*; of an unknown name,
  *"the message should be ignored"*; of a bad key, *"if it is incorrect, the message should be
  ignored"*. The rule is **line-initial**, which is what makes it safe — `#$#` in the middle of a line
  is untouched, and across the 918 connect screens in MUIndex's catalogue every line-initial `#$#` is
  protocol while every mid-line one is ASCII art.
- A multiline message is held open until its terminator arrives, and the peer decides whether one
  ever does — so at most 8 may be open at once, and at most 4096 continuation lines may accumulate in
  any one of them.

A *mangled* message is ignored rather than half-obeyed, following the specification's own examples: one
carrying the same keyword twice, and a multiline message whose continuation names a key the opening
message never declared. Delivering what survived would hand you a message missing content the peer
believes it sent.

Refused rather than mishandled, all at the point the value is made: an authentication key that is not
a single `<simple-char>` token (it is written back unquoted and could not survive the trip), a value
carrying a line ending passed to `SendAsync` (use `SendMultilineAsync`), the same keyword twice in one
message (the specification forbids it and no receiver has a defined way to resolve it), and a negative
`McpVersion` component (the grammar has no sign, so no peer could read it back).

Multiline messages arrive **whole, once**, when the line that closes them arrives — not once per
continuation line:

```csharp
.OnMcpMessage("dns-org-mud-moo-simpleedit-content", message =>
{
    string? reference = message.Value("reference");
    IReadOnlyList<string> content = message.Lines("content");
    return ValueTask.CompletedTask;
})
```

**Sending one is `SendMultilineAsync`** — the direction `dns-org-mud-moo-simpleedit` needs, a server
handing a client a buffer to edit:

```csharp
await mcp.SendMultilineAsync(
    "dns-org-mud-moo-simpleedit-content",
    [("reference", "#98:2"), ("name", "Test:look"), ("type", "moo-code")],
    ("content", lines));
```

```text
#$#dns-org-mud-moo-simpleedit-content 1234 reference: "#98:2" name: "Test:look" type: "moo-code" content*: "" _data-tag: "1"
#$#* 1 content: "This is a test.";
#$#* 1 content: return 1;
#$#: 1
```

The data tag is generated per message, and the whole message goes out under one lock — a foreign line
landing between the opening message and its terminator is not merely out of order, since the peer
reassembles by tag and would read it as belonging to whatever tag it names. Continuation text runs
verbatim to the end of the line with no quoting of its own, so a string containing line breaks becomes
several continuation lines rather than one line with an embedded newline that would end it early.

Agreement is worked out as each `mcp-negotiate-can` arrives rather than when the list ends, because
a 1.0 peer never sends an end; `MudClientProtocol.IsComplete` is the extra thing 2.0 buys, not a
precondition for agreeing on anything. A package is in `Agreed` only if this side declared it, the
peer offered it, and the two ranges overlap — the agreed version being the highest both can speak.

**`OnMcpNegotiationComplete` fires on the peer's `mcp-negotiate-end`, so against a 1.0 peer it never
fires at all.** That is not a failure — 1.0 has no such line — but it means the callback is the wrong
place to hang anything you need against every peer. Read `Agreed` instead, which fills in as each
`can` arrives. Once the end line has been seen the negotiation is terminal: a later `can` does not
change `Agreed`, and a repeated end does not announce it twice, so the set the callback was handed
stays the set that was agreed.

`MudClientProtocol.PeerPackages` keeps **everything the peer advertised**, including packages this
side does not speak — `Agreed` is an intersection and throws away the larger half. "What does this peer
support" and "what can the two of us do together" are different questions, and only the first survives
in `PeerPackages`.

## Cords

`mcp-cord` gives you named, typed channels over the one session. **A cord is not a negotiation**,
which is worth saying because its open/close lifecycle looks like one — the negotiating happened
before it, when `mcp-negotiate` established that both sides speak `mcp-cord`. Opening one is use of
a capability already agreed: no offer, no counter-offer, no version intersection. Closer to opening a
connection on an agreed port than to agreeing which ports exist.

What it buys is that **you can define your own channel without a plugin in this library**:

Cords ride on the session layer, so both go on together — `McpCordProtocol` alone throws at
`BuildAsync()`:

```csharp
.AddPlugin<MudClientProtocol>()
.AddPlugin<McpCordProtocol>()
    .SupportsCordType("dns-com-example-chat", cord =>
    {
        cord.OnMessage(m => Handle(m.Value("_message"), m));
        cord.OnClosed(() => Forget(cord.Id));
        return ValueTask.CompletedTask;
    })
```

```csharp
var cords = telnet.PluginManager!.GetPlugin<McpCordProtocol>()!;

// Cords are an optional negotiated package: wait until the peer has agreed it, which
// OnMcpNegotiationComplete tells you, or watch Agreed fill in. Opening before that throws.
McpCord cord = await cords.OpenAsync("dns-com-example-chat", c =>
{
    // Wired here, not after the call returns: mcp-cord-open has already gone out by then, and a
    // peer is free to send on the cord before your next statement runs.
    c.OnMessage(m => Handle(m));
    c.OnClosed(() => Forget(c.Id));
});

await cord.SendAsync("say", ("text", "hello there"));
await cord.SendMultilineAsync("content", [("name", "note")], ("body", lines));
await cord.CloseAsync();
```

Identifiers carry a role prefix, which is the specification's own scheme for keeping the two ends from
colliding: the endpoint that initiated MCP — the server, which makes the offer — prefixes `I`, the
responder prefixes `R`, and each side is then only obliged to be unique against itself.

A cord of a type you never declared is dropped, as is a message for a cord that was never opened or
has been closed (*"treat as an unrecognized MCP message, silently dropping it"*). A duplicate
`mcp-cord-closed` is ignored, because the specification says race conditions produce them. Sending on
a closed cord throws rather than being dropped — a message the peer will never see is a mistake by the
caller, not something to swallow. At most 64 *peer-opened* cords may be held at once; cords you opened are your own business and do not spend that allowance.

**Register every package before `BuildAsync()`**, or from a package plugin's own `InitializeAsync`.
The whole list goes out in one burst the moment the session comes up, closed by `mcp-negotiate-end`,
so a package that registers after that has told the peer nothing.

