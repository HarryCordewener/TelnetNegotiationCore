# MSSP — Mud Server Status Protocol

[MSSP](https://tintin.mudhalla.net/protocols/mssp) is how a server describes itself to a crawler or a
client: a name, a port, a player count, and around sixty other defined variables. This library speaks
it over telnet option 70 (`MSSPProtocol`) and, optionally, over the plaintext `MSSP-REQUEST` form
(`MSSPPlaintextProtocol`).

## Reading a report

`MSSPConfig` has strongly typed properties for every variable [the specification](https://tintin.mudhalla.net/protocols/mssp) defines, but those cannot represent MSSP on their own: a variable may carry **several values** — spelled either as repeated `MSSP_VAL` under one `MSSP_VAR`, or as the same `MSSP_VAR` repeated — and a server may send names this library has never heard of. `MSSPConfig.Variables` is the lossless record every property is projected from:

```csharp
ValueTask HandleMSSPAsync(MSSPConfig config)
{
    // Convenient scalars. For a multi-valued variable this is the *last* value,
    // which the specification defines as the default.
    Console.WriteLine(config.Name);            // "Test MUD"
    Console.WriteLine(config.Port);            // 4201, from PORT "80" "23" "4201"

    // Everything the server actually said, in wire order.
    foreach (var (variable, values) in config.Variables)
        Console.WriteLine($"{variable} = {string.Join(", ", values)}");

    var ports    = config.Variables["PORT"];          // ["80", "23", "4201"]
    var referral = config.Referral;                   // every REFERRAL entry
    var charset  = config.Variables.Default("CHARSET");
    var crawl    = config.Variables.Integer("CRAWL DELAY");  // -1 means "your default"
    var ansi     = config.Variables.Flag("ANSI");

    // Variables with no typed property — unofficial extras and invented names — are
    // kept too, in Variables and in Extended (as IReadOnlyList<string>).
    foreach (var name in config.Variables.UnofficialNames)
        Console.WriteLine($"{name} = {string.Join(", ", config.Variables[name])}");

    return ValueTask.CompletedTask;
}
```

Variable names are canonicalized to the specification's spaced, upper-case spelling, so the recommended underscore substitution reads back the same: `config.Variables["CRAWL_DELAY"]`, `config.Variables["CRAWL DELAY"]` and `config.Variables["crawl delay"]` are one variable.

When **sending**, a `MSSPConfig` you build by hand behaves exactly as before — set the properties, or add to `Extended`. A config you received from a peer round-trips verbatim, arrays and unknown variables included.

MSSP mandates no character set — its only byte-level rule is that *"variables and values cannot contain the MSSP_VAL, MSSP_VAR, IAC, or NUL byte"*, and its own `CHARSET` variable reports *"ASCII, BIG5, CP437, CP949, CP1251, EUC-KR, GB18030, ISO-8859-1, ISO-8859-2, KOI8-R, UTF-8"* — so a report is read and written with `TelnetInterpreter.CurrentEncoding`, whatever RFC 2066 CHARSET negotiation has settled on (UTF-8 until it settles on something else).

## Plaintext MSSP (`MSSP-REQUEST`)

A sizeable population of servers answers MSSP as **plain text** at the login screen as well as over
telnet option 70. The client sends the literal line `MSSP-REQUEST`; the server answers with a leading
CRLF, a start marker, tab-separated `name<TAB>value` lines, and an end marker:

```text
\r\nMSSP-REPLY-START\r\n
NAME<TAB>Some MUD\r\n
PLAYERS<TAB>4\r\n
MSSP-REPLY-END\r\n
```

The vocabulary is the same one — multi-word official names included — so it lands in the same
`MSSPConfig` and arrives through the same `OnMSSP` callback. It lives in its own plugin, and
**adding that plugin is the entire opt-in**:

```csharp
.AddPlugin<MSSPProtocol>()
    .OnMSSP(HandleMSSPAsync)
    .WithMSSPConfig(() => new MSSPConfig { Name = "My Server" })
.AddPlugin<MSSPPlaintextProtocol>()          // ← the opt-in; nothing else to switch on
    .WithReplyTimeout(TimeSpan.FromSeconds(10))
```

`MSSPPlaintextProtocol` depends on `MSSPProtocol` — it borrows that plugin's `OnMSSP` callback, its
`MaxMessageSize` and its `WithMSSPConfig` provider rather than duplicating them — so adding it alone
throws at `BuildAsync()` rather than going quiet on the wire.

**As a server, it is automatic.** An incoming `MSSP-REQUEST` line (matched case-insensitively, as
SMAUG's `str_cmp` does) is answered from your `WithMSSPConfig` and **consumed**, which means
`MSSP-REQUEST` stops being usable as a character name on your server. That is the whole consequence
of adding the plugin, and adding it is the consent to it.

**As a client, nothing is sent until you ask.** Unlike `IAC DO 70`, which is pure negotiation that a
non-supporting server ignores, this puts real text at a stranger's login prompt — two hosts probed
while this was written replied `Illegal name, try another.` and re-prompted:

```csharp
var plaintext = telnet.PluginManager!.GetPlugin<MSSPPlaintextProtocol>()!;

MSSPConfig? report = await plaintext.RequestReportAsync(cancellationToken);

if (report is null)
{
    // No answer: no plaintext MSSP, or it never finished, or it was over the ceiling.
}
```

The report is returned to the caller *and* delivered to `OnMSSP`, so a consumer already wired for
option 70 needs no new plumbing, while a crawler gets the value at the call site where it knows which
host it just asked.

**There is deliberately no timer.** No version of the MSSP specification gives timing for this
exchange — the only timing concept MSSP has is `CRAWL DELAY`, which is *hours between crawls*, not
when to speak within a connection. Grapevine's crawler asks 10 seconds after connecting and gives up
at 20, but that is [one crawler's published policy](https://grapevine.haus/mssp), and a library that
baked it in would be choosing, for every consumer, the moment to put text on a stranger's login
prompt — by which time an interactive client may already have sent a character name. Rebuilding that
exact policy on top of the explicit call is three lines, and all of it stays yours:

```csharp
// The telnet option may already have answered through OnMSSP by now.
MSSPConfig? report = null;

await Task.Delay(TimeSpan.FromSeconds(10), token);
if (report is null)
    report = await plaintext.RequestReportAsync(token);   // ReplyTimeout bounds the wait
```

Behaviour once the plugin is added:

| | |
| --- | --- |
| Client sends | `MSSP-REQUEST\r\n`, only from `RequestReportAsync`, once per call |
| Returns | the `MSSPConfig`, or `null` whenever the peer produced no report: never answered, reply never ended, reply over the ceiling, connection gone. Not an error — a server without the form is the ordinary case. Caller-side faults (already-cancelled token, disabled plugin) throw rather than returning `null` |
| Telnet option | untouched. This is a second transport, not a replacement; both can answer on one connection and each report says which it came from |
| Markers | matched as **whole lines**, not as substrings, so a MUD that merely says the words in output does not trip the parser |
| Field split | on the **first tab** only, so `MINIMUM AGE`, `PAY TO PLAY` and `XTERM 256 COLORS` survive, and so do values containing spaces |
| Size ceiling | `MSSPProtocol.MaxMessageSize`, counted over the reply's field lines. Over it the reply is **dropped, never truncated**, with an `Error` log and `OnMSSPMessageTooLarge((ReceivedBytes, MaxMessageSize))`; the call returns `null` |
| Reply ceiling | `ReplyTimeout` (default 10 s) bounds a caller that passes `CancellationToken.None`. Cancelling your own token throws `OperationCanceledException`; the ceiling and a dead connection return `null`, because those are answers about the peer |
| Encoding | `TelnetInterpreter.CurrentEncoding`, as on the subnegotiation path. `IAC` among the encoded bytes is doubled (RFC 854); a tab or line ending inside a name or value is replaced with a space, because this framing has no escape for one |
| Lines consumed | everything from the start marker to the end marker, so the reply never reaches your `OnSubmit` as if a user had typed it |

Which transport answered is part of the value, since the two can disagree:

```csharp
ValueTask HandleMSSPAsync(MSSPConfig config)
{
    // MSSPSource.TelnetOption, MSSPSource.Plaintext, or MSSPSource.Unspecified
    // for a config you built by hand rather than received.
    Console.WriteLine(config.Source);
    return ValueTask.CompletedTask;
}
```

**Specification status, stated plainly.** The plaintext form is *not* described on the current
[specification page](https://tintin.mudhalla.net/protocols/mssp/), nor on the mudstandards mirror.
That page's own [changelog](https://tintin.mudhalla.net/protocols/mssp/news.php) records *"Mar 20,
2009 - Plaintext version of MSSP finalized and added to specification"*, and the implementation ships
in the SMAUG family (`Arthmoor/SmaugFUSS` `src/mssp.c`, and the codebases derived from it) and is
read by Grapevine's crawler. So: it was specified, the specification page no longer carries it, and
it is deployed anyway — more widely implemented than it is currently documented. This library's
framing is matched against SmaugFUSS and exercised against a scripted peer; **no live host was
contacted to confirm it.**

