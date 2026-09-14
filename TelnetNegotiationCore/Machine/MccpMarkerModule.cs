using System.Threading.Tasks;
using StateAlchemist;

namespace TelnetNegotiationCore.Machine;

// MCCP: only the start marker is a negotiation concern — IAC SB MCCP2/MCCP3 IAC SE for v2 and v3, and v1's own
// odd IAC SB COMPRESS WILL SE (no doubled IAC at all, a historical quirk this codebase already had to fix once:
// without recognising WILL SE specifically, the marker fell to the generic "skip until IAC SE" reader, which
// then read through the zlib stream behind it looking for a coincidental IAC SE). Whether the marker's arrival
// starts inflating, or is consumed and ignored, depends on which side received it and which version compresses
// which direction — that decision is the context's, using the mode it already knows; the module only recognises
// the three shapes of marker.

public struct Mccp2 : IState<SubNegotiation>
{
    public bool Escaping;
}

public struct Mccp3 : IState<SubNegotiation>
{
    public bool Escaping;
}

/// <summary>MCCP1's marker has no escape step: the byte after the option is WILL, then SE, nothing doubled.</summary>
public struct Mccp1 : IState<SubNegotiation>
{
}

public struct Mccp1AfterWill : IState<SubNegotiation>
{
}

public abstract partial class TelnetCoreContext
{
    /// <summary>The MCCP2 start marker arrived.</summary>
    public abstract ValueTask Mccp2MarkerAsync();

    /// <summary>The MCCP3 start marker arrived.</summary>
    public abstract ValueTask Mccp3MarkerAsync();

    /// <summary>MCCP1's start marker arrived.</summary>
    public abstract ValueTask Mccp1MarkerAsync();
}

[Module]
public static class MccpMarkerModule
{
    private const byte SE = 240;
    private const byte WILL = 251;
    private const byte IAC = 255;
    private const byte Mccp1Option = 85;
    private const byte Mccp2Option = 86;
    private const byte Mccp3Option = 87;

    [Transition(From = typeof(ReadingOption), To = typeof(Mccp2)), On(Mccp2Option)]
    public static void BeginMccp2(ref SubNegotiation parent) => parent.Option = Mccp2Option;

    [Transition(From = typeof(Mccp2)), On(IAC)]
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
    public static void MarkMccp2(ref Mccp2 self) => self.Escaping = !self.Escaping;

    /// <summary>
    /// Anything but SE here is malformed. Must clear <see cref="Mccp2.Escaping"/>, not just self-loop:
    /// otherwise a stray byte between a genuine IAC and an unrelated later SE would still satisfy
    /// <see cref="Mccp2Ended"/>'s guard and start inflation at the wrong stream position.
    /// </summary>
    [Transition(From = typeof(Mccp2)), OnAny]
    public static void IgnoreMalformedMccp2(ref Mccp2 self) => self.Escaping = false;

    [Transition(From = typeof(Mccp2), To = typeof(Idle)), On(SE)]
    public static class Mccp2Ended
    {
        public static bool Guard(in Mccp2 self) => self.Escaping;

        public static void Transform()
        {
        }

        public static ValueTask CompletedAsync(TelnetCoreContext context) => context.Mccp2MarkerAsync();
    }

    [Transition(From = typeof(ReadingOption), To = typeof(Mccp3)), On(Mccp3Option)]
    public static void BeginMccp3(ref SubNegotiation parent) => parent.Option = Mccp3Option;

    [Transition(From = typeof(Mccp3)), On(IAC)]
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
    public static void MarkMccp3(ref Mccp3 self) => self.Escaping = !self.Escaping;

    /// <summary>
    /// Anything but SE here is malformed. Must clear <see cref="Mccp3.Escaping"/>, not just self-loop:
    /// otherwise a stray byte between a genuine IAC and an unrelated later SE would still satisfy
    /// <see cref="Mccp3Ended"/>'s guard and start inflation at the wrong stream position.
    /// </summary>
    [Transition(From = typeof(Mccp3)), OnAny]
    public static void IgnoreMalformedMccp3(ref Mccp3 self) => self.Escaping = false;

    [Transition(From = typeof(Mccp3), To = typeof(Idle)), On(SE)]
    public static class Mccp3Ended
    {
        public static bool Guard(in Mccp3 self) => self.Escaping;

        public static void Transform()
        {
        }

        public static ValueTask CompletedAsync(TelnetCoreContext context) => context.Mccp3MarkerAsync();
    }

    [Transition(From = typeof(ReadingOption), To = typeof(Mccp1)), On(Mccp1Option)]
    public static void BeginMccp1(ref SubNegotiation parent) => parent.Option = Mccp1Option;

    [Transition(From = typeof(Mccp1), To = typeof(Mccp1AfterWill)), On(WILL)]
    public static void MarkMccp1()
    {
    }

    /// <summary>
    /// Anything but WILL here is malformed. Discarded through the core's own IAC-SE skipper rather than
    /// left as a self-loop with no way out: <see cref="Mccp1"/> has no reachable IAC/SE transition of its
    /// own, so a stray byte here would otherwise wedge the connection for its entire remaining lifetime,
    /// not just this subnegotiation.
    /// </summary>
    [Transition(From = typeof(Mccp1), To = typeof(SubNegotiating)), OnAny]
    public static void IgnoreMalformedMccp1()
    {
    }

    [Transition(From = typeof(Mccp1AfterWill), To = typeof(Idle)), On(SE)]
    public static class Mccp1Ended
    {
        public static void Transform()
        {
        }

        public static ValueTask CompletedAsync(TelnetCoreContext context) => context.Mccp1MarkerAsync();
    }

    /// <summary>
    /// Anything but SE here means this was not the marker after all. Re-arms back to <see cref="Mccp1"/>
    /// rather than self-looping in <see cref="Mccp1AfterWill"/>: a self-loop would let a later, unrelated
    /// SE still complete the marker even though a stray byte came between it and the WILL that preceded
    /// it, the same class of bug as MCCP2/MCCP3's guard.
    /// </summary>
    [Transition(From = typeof(Mccp1AfterWill), To = typeof(Mccp1)), OnAny]
    public static void IgnoreMalformedMccp1AfterWill()
    {
    }
}
