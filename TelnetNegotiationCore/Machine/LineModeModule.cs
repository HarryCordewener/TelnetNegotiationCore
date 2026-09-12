using System.Collections.Generic;
using System.Threading.Tasks;
using StateAlchemist;

namespace TelnetNegotiationCore.Machine;

// LINEMODE (RFC 1184, option 34): one sub-command — MODE, FORWARDMASK or SLC — followed by its data, of no
// fixed length, until IAC SE. Interpreting the mode bits, the forward mask or the SLC triples stays exactly
// where it lives today, called through the context with the sub-command and the raw bytes.

/// <summary>Reading which of MODE, FORWARDMASK or SLC this subnegotiation is.</summary>
public struct LineMode : IState<SubNegotiation>
{
}

/// <summary>Reading the sub-command's data.</summary>
public struct LineModeValue : IState<SubNegotiation>
{
    /// <summary>Which sub-command this data belongs to: MODE (1), FORWARDMASK (2) or SLC (3).</summary>
    public byte Kind;

    public List<byte>? Data;

    /// <summary>An IAC has been read; the next byte says whether this ends the subnegotiation.</summary>
    public bool Escaping;

    /// <summary>
    /// True once the peer has sent more than <see cref="LineModeModule.MaxDataBytes"/> bytes. A MODE
    /// byte, a forward mask or an SLC triple set is at most a few dozen bytes even for an elaborate
    /// configuration, so this is far above any legitimate value and exists only to bound a peer that
    /// never sends IAC SE.
    /// </summary>
    public bool Overflowed;
}

public abstract partial class TelnetCoreContext
{
    /// <summary>MODE, FORWARDMASK or SLC arrived with its data, once IAC SE closes it.</summary>
    public abstract ValueTask LineModeAsync(byte kind, byte[] data);
}

[Module]
public static class LineModeModule
{
    private const byte SE = 240;
    private const byte IAC = 255;
    private const byte Mode = 1;
    private const byte ForwardMask = 2;
    private const byte Slc = 3;
    private const byte Option = 34;

    /// <summary>See <see cref="LineModeValue.Overflowed"/>.</summary>
    public const int MaxDataBytes = 8192;

    [Transition(From = typeof(ReadingOption), To = typeof(LineMode)), On(Option)]
    public static void Begin(ref SubNegotiation parent) => parent.Option = Option;

    /// <summary>
    /// Anything but MODE, FORWARDMASK or SLC here is malformed. Discarded through the core's own IAC-SE
    /// skipper rather than left as a self-loop with no way out: a self-loop from this state has no
    /// reachable IAC/SE transition of its own, so a bad sub-command byte would otherwise wedge the
    /// connection for its entire remaining lifetime, not just this subnegotiation.
    /// </summary>
    [Transition(From = typeof(LineMode), To = typeof(SubNegotiating)), OnAny]
    public static void IgnoreMalformed()
    {
    }

    [Transition(From = typeof(LineMode), To = typeof(LineModeValue)), On(Mode)]
    public static void ReadingMode(ref LineModeValue to) => to.Kind = Mode;

    [Transition(From = typeof(LineMode), To = typeof(LineModeValue)), On(ForwardMask)]
    public static void ReadingForwardMask(ref LineModeValue to) => to.Kind = ForwardMask;

    [Transition(From = typeof(LineMode), To = typeof(LineModeValue)), On(Slc)]
    public static void ReadingSlc(ref LineModeValue to) => to.Kind = Slc;

    [Transition(From = typeof(LineModeValue)), OnAny, Run]
    public static void Capture(ref LineModeValue self, System.ReadOnlySpan<byte> run)
    {
        self.Escaping = false;
        if (self.Overflowed)
        {
            return;
        }

        self.Data ??= [];
        if (self.Data.Count + run.Length > MaxDataBytes)
        {
            self.Overflowed = true;
            return;
        }

        self.Data.AddRange(run.ToArray());
    }

    [Transition(From = typeof(LineModeValue)), On(IAC)]
    public static void Mark(ref LineModeValue self)
    {
        if (self.Escaping)
        {
            self.Escaping = false;
            if (self.Overflowed)
            {
                return;
            }

            self.Data ??= [];
            if (self.Data.Count + 1 > MaxDataBytes)
            {
                self.Overflowed = true;
                return;
            }

            self.Data.Add(IAC);
            return;
        }

        self.Escaping = true;
    }

    [Transition(From = typeof(LineModeValue), To = typeof(Idle)), On(SE)]
    public static class Ended
    {
        public static bool Guard(in LineModeValue self) => self.Escaping;

        public static void Transform()
        {
        }

        public static ValueTask CompletedAsync(TelnetCoreContext context, in LineModeValue from) =>
            from.Overflowed ? default : context.LineModeAsync(from.Kind, from.Data?.ToArray() ?? []);
    }
}
