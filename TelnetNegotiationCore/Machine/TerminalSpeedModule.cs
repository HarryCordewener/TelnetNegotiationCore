using System.Collections.Generic;
using System.Threading.Tasks;
using StateAlchemist;

namespace TelnetNegotiationCore.Machine;

// TSPEED (RFC 1079, option 32): SEND asks the other end to report it; IS carries the report as ASCII
// "transmit,receive". Parsing the two integers out of that text stays ordinary C#, called from the context —
// the module's job is only to frame the bytes.

/// <summary>Reading which of SEND or IS this subnegotiation is.</summary>
public struct Tspeed : IState<SubNegotiation>
{
}

/// <summary>SEND carries nothing of its own — just the closing IAC SE.</summary>
public struct TspeedSend : IState<SubNegotiation>
{
    public bool Escaping;
}

/// <summary>Reading the "transmit,receive" text an IS carries.</summary>
public struct TspeedValue : IState<SubNegotiation>
{
    public List<byte>? Text;

    public bool Escaping;

    /// <summary>
    /// True once the peer has sent more than <see cref="TerminalSpeedModule.MaxTextBytes"/> bytes.
    /// RFC 1079's speed is "transmit,receive" in decimal, so a dozen characters at most; this is far
    /// above any legitimate value and exists only to bound a peer that never sends IAC SE.
    /// </summary>
    public bool Overflowed;
}

public abstract partial class TelnetCoreContext
{
    /// <summary>The peer asked to be sent our terminal speed.</summary>
    public abstract ValueTask TerminalSpeedRequestedAsync();

    /// <summary>The peer reported its terminal speed, as the ASCII text it sent.</summary>
    public abstract ValueTask TerminalSpeedAsync(byte[] text);
}

[Module]
public static class TerminalSpeedModule
{
    private const byte SE = 240;
    private const byte IAC = 255;
    private const byte IS = 0;
    private const byte SendCommand = 1;
    private const byte Option = 32;

    /// <summary>See <see cref="TspeedValue.Overflowed"/>.</summary>
    public const int MaxTextBytes = 8192;

    [Transition(From = typeof(ReadingOption), To = typeof(Tspeed)), On(Option)]
    public static void Begin(ref SubNegotiation parent) => parent.Option = Option;

    [Transition(From = typeof(Tspeed), To = typeof(TspeedSend)), On(SendCommand)]
    public static void Requested()
    {
    }

    /// <summary>
    /// Anything but SEND or IS here is malformed. Discarded through the core's own IAC-SE skipper rather
    /// than left as a self-loop with no way out: a self-loop from this state has no reachable IAC/SE
    /// transition of its own, so a bad command byte would otherwise wedge the connection for its entire
    /// remaining lifetime, not just this subnegotiation.
    /// </summary>
    [Transition(From = typeof(Tspeed), To = typeof(SubNegotiating)), OnAny]
    public static void IgnoreMalformed()
    {
    }

    [Transition(From = typeof(TspeedSend)), On(IAC)]
    /// <summary>
    /// An <c>IAC</c>: either the terminator is starting, or this is the second of a doubled pair and
    /// so a literal 255 in the payload.
    /// </summary>
    /// <remarks>
    /// A toggle, not a latch. RFC 855 requires a 255 among a subnegotiation's parameters to be sent
    /// doubled -- "if parameters in an option 'subnegotiation' include a byte with a value of 255, it
    /// is necessary to double this byte in accordance the general TELNET rules" -- so <c>IAC IAC</c>
    /// is one data byte and the <c>SE</c> that follows it is data too, not the end of the frame.
    /// Latching meant <c>IAC IAC SE</c> terminated here, one byte early.
    /// </remarks>
    public static void MarkSend(ref TspeedSend self) => self.Escaping = !self.Escaping;

    /// <summary>
    /// Anything but IAC here is malformed; ignored rather than left unhandled. Must clear
    /// <see cref="TspeedSend.Escaping"/>, not just self-loop: otherwise a stray byte between a
    /// genuine IAC and an unrelated later SE would still satisfy <see cref="RequestedEnded"/>'s
    /// guard and report a request without an adjacent IAC SE.
    /// </summary>
    /// <remarks>
    /// This body was empty, alone among the three options with a SEND state --
    /// <c>TerminalTypeModule.IgnoreMalformedSend</c> and <c>XDisplayModule.IgnoreMalformedSend</c>
    /// both clear it and both carry the comment above. So <c>IAC SB TSPEED SEND IAC 0x01 SE</c>
    /// reported a terminal-speed request with no adjacent <c>IAC SE</c>.
    /// </remarks>
    [Transition(From = typeof(TspeedSend)), OnAny]
    public static void IgnoreMalformedSend(ref TspeedSend self) => self.Escaping = false;

    [Transition(From = typeof(TspeedSend), To = typeof(Idle)), On(SE)]
    public static class RequestedEnded
    {
        public static bool Guard(in TspeedSend self) => self.Escaping;

        public static void Transform()
        {
        }

        public static ValueTask CompletedAsync(TelnetCoreContext context) => context.TerminalSpeedRequestedAsync();
    }

    [Transition(From = typeof(Tspeed), To = typeof(TspeedValue)), On(IS)]
    public static void Reporting()
    {
    }

    [Transition(From = typeof(TspeedValue)), OnAny, Run]
    public static void Capture(ref TspeedValue self, System.ReadOnlySpan<byte> run)
    {
        self.Escaping = false;
        if (self.Overflowed)
        {
            return;
        }

        self.Text ??= [];
        if (self.Text.Count + run.Length > MaxTextBytes)
        {
            self.Overflowed = true;
            return;
        }

        self.Text.AddRange(run.ToArray());
    }

    [Transition(From = typeof(TspeedValue)), On(IAC)]
    public static void Mark(ref TspeedValue self)
    {
        if (self.Escaping)
        {
            self.Escaping = false;
            if (self.Overflowed)
            {
                return;
            }

            self.Text ??= [];
            if (self.Text.Count + 1 > MaxTextBytes)
            {
                self.Overflowed = true;
                return;
            }

            self.Text.Add(IAC);
            return;
        }

        self.Escaping = true;
    }

    [Transition(From = typeof(TspeedValue), To = typeof(Idle)), On(SE)]
    public static class Ended
    {
        public static bool Guard(in TspeedValue self) => self.Escaping;

        public static void Transform()
        {
        }

        // Dropped rather than reported truncated: a cut-short "transmit,receive" parses as a
        // different speed than the peer sent, not a slower one.
        public static ValueTask CompletedAsync(TelnetCoreContext context, in TspeedValue from) =>
            from.Overflowed ? default : context.TerminalSpeedAsync(from.Text?.ToArray() ?? []);
    }
}
