# MSP — MUD Sound Protocol

`MSPProtocol` negotiates telnet option 90, under which a server's output may carry
[MSP](https://www.zuggsoft.com/zmud/msp.htm) sound and music requests.

MSP has no subnegotiation. The server offers `IAC WILL MSP`, a client that can play sound answers
`IAC DO MSP`, and from then on the server writes its requests as ordinary lines:

```text
!!SOUND(weather/thunder.wav V=80 L=1 P=50 T=weather U=https://example.org/sounds/)
!!MUSIC(town.mid V=60 L=-1 C=1)
```

Those lines are in-band text, so they are the host's to recognise, play and take out of the output,
just as MXP's tags are. What the plugin settles is whether they are live: on a connection without MSP
agreed, `!!SOUND(...)` is text a player typed, and a client must show it as text.

```csharp
.AddPlugin<MSPProtocol>()
    .OnMSPEnabled(() => { soundTriggersLive = true; return ValueTask.CompletedTask; })
    .OnMSPDisabled(() => { soundTriggersLive = false; return ValueTask.CompletedTask; })
```

`IsMSPActive` is the same fact as a property. On a client the callback runs after the `DO` has been
sent; on a server it runs when the client's `DO` arrives. A repeated `WILL` or `DO` changes nothing and
does not run the callback again.

Registering the plugin is how a client says it can play sound. A client with no audio device, or whose
user has turned sound off, should leave it out: the server's offer is then refused with `DONT`, the same
answer every unregistered option gets.

## Directions

MSP is offered by the server. A client refuses a server's `DO MSP` with `WONT`, and a server refuses a
client's `WILL MSP` with `DONT`. A withdrawal of an agreed option (`WONT` to a client, `DONT` to a
server) is acknowledged as RFC 1143 asks, and turns the option off; one for an option never agreed is
not answered.
