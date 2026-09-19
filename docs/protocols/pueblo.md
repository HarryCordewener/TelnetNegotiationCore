# Pueblo

`PuebloProtocol` performs the Pueblo handshake. Pueblo has no telnet option: the whole exchange is
ordinary text, framed as PennMUSH frames it.

```text
server: This world is Pueblo 1.10 Enhanced.\r\n
client: PUEBLOCLIENT 2.50 md5="…"
server: </xch_mudtext><img xch_mode=purehtml><xch_page clear=text>\n
```

The server sends the hello with its initial negotiation. When a line beginning exactly `PUEBLOCLIENT `
arrives, at any point in the session, it is **consumed**: it never reaches `OnSubmit`. The server
answers with the start sequence, and that answer is what moves the client out of text mode. A client
that is never sent it shows every tag as text. A repeated `PUEBLOCLIENT`, from a client that thinks it
is still showing raw HTML, is answered again without the clear-screen, and does not fire the callback
a second time.

```csharp
.AddPlugin<PuebloProtocol>()
    .OnPuebloEnabled(client => SwitchToPuebloRendererAsync(client.Version))
```

`OnPuebloEnabled` receives a `PuebloClient`: the version the client announced, and its `md5="…"`
checksum if it sent one (at most 32 characters, as PennMUSH keeps). `IsPuebloActive` and `Client` are
the same facts as properties. PennMUSH shows its connect screen again at this point, now in HTML;
that is the host's to do in the callback.

**Adding the plugin is the opt-in.** The hello is unsolicited text that a client without Pueblo shows
on its first screen, and `PUEBLOCLIENT ` stops being usable as input. Registering the plugin is the
consent to both. Server mode only; in client mode the plugin does nothing.

**The markup is yours.** Pueblo's tags are HTML with its own attributes, such as `<a xch_cmd>` for a
command link. They are not MXP's: MXP writes a command link as `<send href>`, and each client prints
the other's as text. Which dialect to write, and which one wins for a client that negotiates both
Pueblo and [MXP](mxp.md), are the host application's decisions.
