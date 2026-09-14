using System.Collections.Generic;
using System.Threading.Tasks;
using StateAlchemist;

namespace TelnetNegotiationCore.Machine;

// XDISPLOC (RFC 1096, option 35): the same SEND/IS shape as TSPEED, carrying the X11 display location
// instead of a speed pair.

/// <summary>Reading which of SEND or IS this subnegotiation is.</summary>
public struct XDisplayLocation : IState<SubNegotiation>
{
}

public struct XDisplayLocationSend : IState<SubNegotiation>
{
    public bool Escaping;
}

/// <summary>Reading the display location text an IS carries, such as <c>unix:0.0</c>.</summary>
public struct XDisplayLocationValue : IState<SubNegotiation>
{
    public List<byte>? Text;

    public bool Escaping;

    /// <summary>
    /// True once the peer has sent more than <see cref="XDisplayModule.MaxTextBytes"/> bytes. An X11
    /// display location is a handful of ASCII characters at most, so this is far above any legitimate
    /// value and exists only to bound a peer that never sends IAC SE.
    /// </summary>
    public bool Overflowed;
}

public abstract partial class TelnetCoreContext
{
    /// <summary>The peer asked to be sent our X display location.</summary>
    public abstract ValueTask XDisplayLocationRequestedAsync();

    /// <summary>The peer reported its X display location, as the ASCII text it sent.</summary>
    public abstract ValueTask XDisplayLocationAsync(byte[] text);
}

[Module]
public static class XDisplayModule
{
    private const byte SE = 240;
    private const byte IAC = 255;
    private const byte IS = 0;
    private const byte SendCommand = 1;
    private const byte Option = 35;

    /// <summary>See <see cref="XDisplayLocationValue.Overflowed"/>.</summary>
    public const int MaxTextBytes = 8192;

    [Transition(From = typeof(ReadingOption), To = typeof(XDisplayLocation)), On(Option)]
    public static void Begin(ref SubNegotiation parent) => parent.Option = Option;

    [Transition(From = typeof(XDisplayLocation), To = typeof(XDisplayLocationSend)), On(SendCommand)]
    public static void Requested()
    {
    }

    /// <summary>
    /// Anything but SEND or IS here is malformed. Discarded through the core's own IAC-SE skipper rather
    /// than left as a self-loop with no way out: a self-loop from this state has no reachable IAC/SE
    /// transition of its own, so a bad command byte would otherwise wedge the connection for its entire
    /// remaining lifetime, not just this subnegotiation.
    /// </summary>
    [Transition(From = typeof(XDisplayLocation), To = typeof(SubNegotiating)), OnAny]
    public static void IgnoreMalformed()
    {
    }

    [Transition(From = typeof(XDisplayLocationSend)), On(IAC)]
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
    public static void MarkSend(ref XDisplayLocationSend self) => self.Escaping = !self.Escaping;

    /// <summary>
    /// Anything but IAC here is malformed. Must clear <see cref="XDisplayLocationSend.Escaping"/>, not
    /// just self-loop: otherwise a stray byte between a genuine IAC and an unrelated later SE would still
    /// satisfy <see cref="RequestedEnded"/>'s guard and report a request without an adjacent IAC SE.
    /// </summary>
    [Transition(From = typeof(XDisplayLocationSend)), OnAny]
    public static void IgnoreMalformedSend(ref XDisplayLocationSend self) => self.Escaping = false;

    [Transition(From = typeof(XDisplayLocationSend), To = typeof(Idle)), On(SE)]
    public static class RequestedEnded
    {
        public static bool Guard(in XDisplayLocationSend self) => self.Escaping;

        public static void Transform()
        {
        }

        public static ValueTask CompletedAsync(TelnetCoreContext context) => context.XDisplayLocationRequestedAsync();
    }

    [Transition(From = typeof(XDisplayLocation), To = typeof(XDisplayLocationValue)), On(IS)]
    public static void Reporting()
    {
    }

    [Transition(From = typeof(XDisplayLocationValue)), OnAny, Run]
    public static void Capture(ref XDisplayLocationValue self, System.ReadOnlySpan<byte> run)
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

    [Transition(From = typeof(XDisplayLocationValue)), On(IAC)]
    public static void Mark(ref XDisplayLocationValue self)
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

    [Transition(From = typeof(XDisplayLocationValue), To = typeof(Idle)), On(SE)]
    public static class Ended
    {
        public static bool Guard(in XDisplayLocationValue self) => self.Escaping;

        public static void Transform()
        {
        }

        public static ValueTask CompletedAsync(TelnetCoreContext context, in XDisplayLocationValue from) =>
            from.Overflowed ? default : context.XDisplayLocationAsync(from.Text?.ToArray() ?? []);
    }
}
