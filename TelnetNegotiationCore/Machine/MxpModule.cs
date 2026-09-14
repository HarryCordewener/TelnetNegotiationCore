using System.Threading.Tasks;
using StateAlchemist;

namespace TelnetNegotiationCore.Machine;

// MXP (option 91): the subnegotiation carries nothing — IAC SB MXP IAC SE is the whole of it, the marker that
// starts MXP mode. Everything MXP actually does (parsing its tags in the ordinary text stream) is unaffected by
// this module; it only recognises the marker that turns the mode on.

/// <summary>Waiting for the marker's closing IAC SE, which carries nothing of its own.</summary>
public struct Mxp : IState<SubNegotiation>
{
    /// <summary>An IAC has been read; the next byte says whether this ends the subnegotiation.</summary>
    public bool Escaping;
}

public abstract partial class TelnetCoreContext
{
    /// <summary>The MXP start marker arrived.</summary>
    public abstract ValueTask MxpStartedAsync();
}

[Module]
public static class MxpModule
{
    private const byte SE = 240;
    private const byte IAC = 255;
    private const byte Option = 91;

    [Transition(From = typeof(ReadingOption), To = typeof(Mxp)), On(Option)]
    public static void Begin(ref SubNegotiation parent) => parent.Option = Option;

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
    [Transition(From = typeof(Mxp)), On(IAC)]
    public static void Mark(ref Mxp self) => self.Escaping = !self.Escaping;

    /// <summary>
    /// Nothing else belongs in this marker. Must clear <see cref="Mxp.Escaping"/>, not just self-loop:
    /// otherwise a stray byte between a genuine IAC and an unrelated later SE would still satisfy
    /// <see cref="Ended"/>'s guard and start MXP mode without an adjacent IAC SE.
    /// </summary>
    [Transition(From = typeof(Mxp)), OnAny]
    public static void IgnoreMalformed(ref Mxp self) => self.Escaping = false;

    [Transition(From = typeof(Mxp), To = typeof(Idle)), On(SE)]
    public static class Ended
    {
        public static bool Guard(in Mxp self) => self.Escaping;

        public static void Transform()
        {
        }

        public static ValueTask CompletedAsync(TelnetCoreContext context) => context.MxpStartedAsync();
    }
}
