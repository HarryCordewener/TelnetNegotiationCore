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
    public static void MarkMccp2(ref Mccp2 self) => self.Escaping = true;

    [Transition(From = typeof(Mccp2)), OnAny]
    public static void IgnoreMalformedMccp2()
    {
    }

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
    public static void MarkMccp3(ref Mccp3 self) => self.Escaping = true;

    [Transition(From = typeof(Mccp3)), OnAny]
    public static void IgnoreMalformedMccp3()
    {
    }

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

    [Transition(From = typeof(Mccp1)), OnAny]
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

    /// <summary>Anything but SE here means this was not the marker after all; ignored rather than left unhandled.</summary>
    [Transition(From = typeof(Mccp1AfterWill)), OnAny]
    public static void IgnoreMalformedMccp1AfterWill()
    {
    }
}
