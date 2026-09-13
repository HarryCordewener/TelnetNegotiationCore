using StateAlchemist;
using System.Threading.Tasks;

namespace TelnetNegotiationCore.Machine;

// FLOWCONTROL (TELNET-SPEED, option 33): a subnegotiation carrying exactly one command byte —
// off, on, restart-any or restart-xon.

/// <summary>Reading the one command byte a FLOWCONTROL subnegotiation carries.</summary>
public struct FlowControl : IState<SubNegotiation>
{
    /// <summary>The last command byte seen. Overwritten by each byte that arrives before IAC, as the
    /// original did — a peer that repeats itself is answered with whichever command it sent last.</summary>
    public byte Command;

    /// <summary>Whether any command byte has arrived yet. An empty subnegotiation names none.</summary>
    public bool Received;

    /// <summary>An IAC has been read; the next byte says whether this ends the subnegotiation.</summary>
    public bool Escaping;
}

public abstract partial class TelnetCoreContext
{
    /// <summary>The peer sent a FLOWCONTROL command: 0 off, 1 on, 2 restart-any, 3 restart-xon.</summary>
    public abstract ValueTask FlowControlAsync(byte command);
}

[Module]
public static class FlowControlModule
{
    private const byte SE = 240;
    private const byte IAC = 255;
    private const byte Option = 33;

    [Transition(From = typeof(ReadingOption), To = typeof(FlowControl)), On(Option)]
    public static void Begin(ref SubNegotiation parent) => parent.Option = Option;

    /// <summary>A stay: more than one command byte before IAC keeps only the last, as the original did.</summary>
    [Transition(From = typeof(FlowControl)), OnAny]
    public static void Capture(ref FlowControl self, byte value)
    {
        self.Escaping = false;
        self.Command = value;
        self.Received = true;
    }

    [Transition(From = typeof(FlowControl)), On(IAC)]
    public static void Mark(ref FlowControl self) => self.Escaping = true;

    [Transition(From = typeof(FlowControl), To = typeof(Idle)), On(SE)]
    public static class Ended
    {
        public static bool Guard(in FlowControl self) => self.Escaping;

        public static void Transform()
        {
        }

        public static ValueTask CompletedAsync(TelnetCoreContext context, in FlowControl from) =>
            from.Received ? context.FlowControlAsync(from.Command) : default;
    }
}
