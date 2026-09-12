using System.Collections.Generic;
using System.Threading.Tasks;
using StateAlchemist;

namespace TelnetNegotiationCore.Machine;

// TTYPE (RFC 1091, option 24): the same SEND/IS shape as TSPEED and XDISPLOC. A client may answer several
// SENDs in a row with different names — cycling through them, and repeating the last once exhausted, is the
// client's own bookkeeping, unaffected by this module.

/// <summary>Reading which of SEND or IS this subnegotiation is.</summary>
public struct TerminalType : IState<SubNegotiation>
{
}

public struct TerminalTypeSend : IState<SubNegotiation>
{
    public bool Escaping;
}

/// <summary>Reading the terminal type name text an IS carries, such as <c>xterm</c> or <c>MTTS 137</c>.</summary>
public struct TerminalTypeValue : IState<SubNegotiation>
{
    public List<byte>? Text;

    public bool Escaping;
}

public abstract partial class TelnetCoreContext
{
    /// <summary>The peer asked to be sent our terminal type.</summary>
    public abstract ValueTask TerminalTypeRequestedAsync();

    /// <summary>The peer reported a terminal type name, as the ASCII text it sent.</summary>
    public abstract ValueTask TerminalTypeAsync(byte[] text);
}

[Module]
public static class TerminalTypeModule
{
    private const byte SE = 240;
    private const byte IAC = 255;
    private const byte IS = 0;
    private const byte SendCommand = 1;
    private const byte Option = 24;

    [Transition(From = typeof(ReadingOption), To = typeof(TerminalType)), On(Option)]
    public static void Begin(ref SubNegotiation parent) => parent.Option = Option;

    [Transition(From = typeof(TerminalType), To = typeof(TerminalTypeSend)), On(SendCommand)]
    public static void Requested()
    {
    }

    /// <summary>Anything but SEND or IS here is malformed; ignored rather than left unhandled.</summary>
    [Transition(From = typeof(TerminalType)), OnAny]
    public static void IgnoreMalformed()
    {
    }

    [Transition(From = typeof(TerminalTypeSend)), On(IAC)]
    public static void MarkSend(ref TerminalTypeSend self) => self.Escaping = true;

    /// <summary>Anything but IAC here is malformed; ignored rather than left unhandled.</summary>
    [Transition(From = typeof(TerminalTypeSend)), OnAny]
    public static void IgnoreMalformedSend()
    {
    }

    [Transition(From = typeof(TerminalTypeSend), To = typeof(Idle)), On(SE)]
    public static class RequestedEnded
    {
        public static bool Guard(in TerminalTypeSend self) => self.Escaping;

        public static void Transform()
        {
        }

        public static ValueTask CompletedAsync(TelnetCoreContext context) => context.TerminalTypeRequestedAsync();
    }

    [Transition(From = typeof(TerminalType), To = typeof(TerminalTypeValue)), On(IS)]
    public static void Reporting()
    {
    }

    [Transition(From = typeof(TerminalTypeValue)), OnAny, Run]
    public static void Capture(ref TerminalTypeValue self, System.ReadOnlySpan<byte> run)
    {
        self.Escaping = false;
        self.Text ??= [];
        self.Text.AddRange(run.ToArray());
    }

    [Transition(From = typeof(TerminalTypeValue)), On(IAC)]
    public static void Mark(ref TerminalTypeValue self)
    {
        if (self.Escaping)
        {
            self.Escaping = false;
            self.Text ??= [];
            self.Text.Add(IAC);
            return;
        }

        self.Escaping = true;
    }

    [Transition(From = typeof(TerminalTypeValue), To = typeof(Idle)), On(SE)]
    public static class Ended
    {
        public static bool Guard(in TerminalTypeValue self) => self.Escaping;

        public static void Transform()
        {
        }

        public static ValueTask CompletedAsync(TelnetCoreContext context, in TerminalTypeValue from) =>
            context.TerminalTypeAsync(from.Text?.ToArray() ?? []);
    }
}
