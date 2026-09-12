using System;
using System.Threading.Tasks;
using StateAlchemist;

namespace TelnetNegotiationCore.Machine;

// NAWS (RFC 1073): the client's window size, four bytes of it, inside a subnegotiation. The first protocol
// declared as a module rather than configured at runtime — what a plugin's ConfigureStateMachine does today.

/// <summary>
/// Reading a window size: width high, width low, height high, height low.
/// </summary>
/// <remarks>
/// There is no separate state for an escape, which is what <c>EscapingNAWSValue</c> is today. Leaving a state
/// clears it, so stepping aside for one byte would throw away the bytes already read — and a run transition may
/// only write its own state, so the reading could not be kept on the subnegotiation either. An IAC half-read is
/// what it sounds like: a byte of this state's data, not a place to be.
/// </remarks>
public struct Naws : IState<SubNegotiation>
{
    /// <summary>Width high, width low, height high, height low, as far as they have arrived.</summary>
    public byte[]? Bytes;

    /// <summary>How many of them there are.</summary>
    public int Index;

    /// <summary>An IAC has been read and what it means depends on the next byte.</summary>
    public bool Escaping;
}

public abstract partial class TelnetCoreContext
{
    /// <summary>The client reported its window size. In TNC this context is the interpreter itself.</summary>
    public abstract ValueTask WindowSizeAsync(int width, int height);
}

[Module]
public static class NawsModule
{
    private const byte SE = 240;
    private const byte IAC = 255;
    private const byte Option = 31;

    /// <summary>
    /// The option byte says this subnegotiation is NAWS. More specific than the core's <c>[OnAny]</c> for the
    /// option byte, which is how a protocol claims one without the core knowing it exists.
    /// </summary>
    [Transition(From = typeof(ReadingOption), To = typeof(Naws)), On(Option)]
    public static void Begin(ref SubNegotiation parent, ref Naws to)
    {
        parent.Option = Option;
        to.Bytes = new byte[4];
    }

    /// <summary>
    /// The value, taken a stretch at a time. IAC and SE are declared below, so the run stops at both rather than
    /// swallowing them — which <c>SALCH0702</c> would say if they were not.
    /// </summary>
    [Transition(From = typeof(Naws)), OnAny, Run]
    public static void Capture(ref Naws self, ReadOnlySpan<byte> run)
    {
        self.Escaping = false;
        Append(ref self, run);
    }

    /// <summary>IAC: either the end is coming, or this is a literal 255, which a 255-column window needs.</summary>
    [Transition(From = typeof(Naws)), On(IAC)]
    public static void Mark(ref Naws self)
    {
        if (self.Escaping)
        {
            self.Escaping = false;
            Append(ref self, IAC);
            return;
        }

        self.Escaping = true;
    }

    /// <summary>IAC SE: the window size is complete.</summary>
    [Transition(From = typeof(Naws), To = typeof(Idle)), On(SE)]
    public static class Ended
    {
        /// <summary>Only after an IAC. A bare 240 is a byte of the value — a 240-column window is legal.</summary>
        public static bool Guard(in Naws self) => self.Escaping;

        /// <summary>Reads the state being left, which is still readable here and cleared straight after.</summary>
        public static void Transform(in Naws from, ref Connected connection)
        {
            if (from.Bytes is null || from.Index < 4)
            {
                return;
            }

            connection.Width = (from.Bytes[0] << 8) | from.Bytes[1];
            connection.Height = (from.Bytes[2] << 8) | from.Bytes[3];
        }

        /// <summary>
        /// Only when <see cref="Transform"/> actually updated the window size: a peer that sends fewer
        /// than four bytes before IAC SE has reported nothing, not the previous size (or 0x0 the first
        /// time) all over again.
        /// </summary>
        public static ValueTask CompletedAsync(TelnetCoreContext context, in Naws from, Connected connection) =>
            from.Bytes is not null && from.Index >= 4
                ? context.WindowSizeAsync(connection.Width, connection.Height)
                : default;
    }

    /// <summary>An SE that no IAC preceded is part of the value.</summary>
    [Transition(From = typeof(Naws)), On(SE)]
    public static void LiteralSe(ref Naws self)
    {
        self.Escaping = false;
        Append(ref self, SE);
    }

    /// <summary>Four bytes is all RFC 1073 defines; a peer that sends more has the rest dropped, not the connection.</summary>
    private static void Append(ref Naws self, byte value)
    {
        self.Bytes ??= new byte[4];
        if (self.Index < 4)
        {
            self.Bytes[self.Index] = value;
            self.Index++;
        }
    }

    private static void Append(ref Naws self, ReadOnlySpan<byte> run)
    {
        self.Bytes ??= new byte[4];
        for (var i = 0; i < run.Length && self.Index < 4; i++)
        {
            self.Bytes[self.Index] = run[i];
            self.Index++;
        }
    }
}
