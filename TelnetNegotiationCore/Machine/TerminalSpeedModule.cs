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

    [Transition(From = typeof(ReadingOption), To = typeof(Tspeed)), On(Option)]
    public static void Begin(ref SubNegotiation parent) => parent.Option = Option;

    [Transition(From = typeof(Tspeed), To = typeof(TspeedSend)), On(SendCommand)]
    public static void Requested()
    {
    }

    /// <summary>Anything but SEND or IS here is malformed; ignored rather than left unhandled.</summary>
    [Transition(From = typeof(Tspeed)), OnAny]
    public static void IgnoreMalformed()
    {
    }

    [Transition(From = typeof(TspeedSend)), On(IAC)]
    public static void MarkSend(ref TspeedSend self) => self.Escaping = true;

    /// <summary>Anything but IAC here is malformed; ignored rather than left unhandled.</summary>
    [Transition(From = typeof(TspeedSend)), OnAny]
    public static void IgnoreMalformedSend()
    {
    }

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
        self.Text ??= [];
        self.Text.AddRange(run.ToArray());
    }

    [Transition(From = typeof(TspeedValue)), On(IAC)]
    public static void Mark(ref TspeedValue self)
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

    [Transition(From = typeof(TspeedValue), To = typeof(Idle)), On(SE)]
    public static class Ended
    {
        public static bool Guard(in TspeedValue self) => self.Escaping;

        public static void Transform()
        {
        }

        public static ValueTask CompletedAsync(TelnetCoreContext context, in TspeedValue from) =>
            context.TerminalSpeedAsync(from.Text?.ToArray() ?? []);
    }
}
