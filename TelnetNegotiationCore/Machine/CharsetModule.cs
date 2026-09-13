using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using StateAlchemist;

namespace TelnetNegotiationCore.Machine;

// CHARSET (RFC 2066, option 42): REQUEST offers a separator-delimited list of charsets; ACCEPTED or REJECTED
// answers it. TTABLE_IS carries a translation table, the older RFC 2066 mechanism this option also folds in;
// TTABLE_REJECTED/ACK/NAK answer it and carry nothing of their own.

/// <summary>Reading which of the seven CHARSET/TTABLE sub-commands this subnegotiation is.</summary>
public struct Charset : IState<SubNegotiation>
{
}

/// <summary>REQUEST's separator-delimited charset list, or ACCEPTED's text.</summary>
public struct CharsetValue : IState<SubNegotiation>
{
    /// <summary>Which of REQUEST or ACCEPTED this text is for.</summary>
    public byte Kind;

    public List<byte>? Text;

    public bool Escaping;
}

/// <summary>
/// Reading TTABLE_IS's bytes, streamed to the context rather than buffered here -- the same shape
/// GMCP/MSDP/MSSP use, so the application's maximum-message-size policy (see
/// <see cref="Protocols.CharsetProtocol.MaxTTableSize"/>) still lives in one place and can reject
/// mid-stream instead of after an unbounded table has already been read into memory.
/// </summary>
public struct CharsetTTable : IState<SubNegotiation>
{
    /// <summary>An IAC has been read; the next byte says whether this ends the subnegotiation.</summary>
    public bool Escaping;
}

/// <summary>REJECTED, TTABLE_REJECTED, TTABLE_ACK or TTABLE_NAK: nothing of its own, just the closing IAC SE.</summary>
public struct CharsetEnding : IState<SubNegotiation>
{
    public byte Kind;

    public bool Escaping;
}

public abstract partial class TelnetCoreContext
{
    /// <summary>REQUEST: the separator-delimited list of charsets the peer offers, as it arrived.</summary>
    public abstract ValueTask CharsetRequestAsync(byte[] text);

    /// <summary>ACCEPTED: the charset name the peer agreed to.</summary>
    public abstract ValueTask CharsetAcceptedAsync(byte[] text);

    /// <summary>REJECTED: the peer accepted none of what was offered.</summary>
    public abstract ValueTask CharsetRejectedAsync();

    /// <summary>TTABLE_IS began; anything buffered from a previous, incomplete one should be dropped.</summary>
    public abstract ValueTask CharsetTTableStartedAsync();

    /// <summary>A stretch of TTABLE_IS's bytes arrived. Called as many times as it takes.</summary>
    public abstract ValueTask CharsetTTableDataAsync(ReadOnlyMemory<byte> data);

    /// <summary>TTABLE_IS is complete.</summary>
    public abstract ValueTask CharsetTTableEndedAsync();

    /// <summary>TTABLE_REJECTED.</summary>
    public abstract ValueTask CharsetTTableRejectedAsync();

    /// <summary>TTABLE_ACK.</summary>
    public abstract ValueTask CharsetTTableAckAsync();

    /// <summary>TTABLE_NAK.</summary>
    public abstract ValueTask CharsetTTableNakAsync();
}

[Module]
public static class CharsetModule
{
    private const byte SE = 240;
    private const byte IAC = 255;
    private const byte Request = 1;
    private const byte Accepted = 2;
    private const byte Rejected = 3;
    private const byte TTableIs = 4;
    private const byte TTableRejected = 5;
    private const byte TTableAck = 6;
    private const byte TTableNak = 7;
    private const byte Option = 42;

    [Transition(From = typeof(ReadingOption), To = typeof(Charset)), On(Option)]
    public static void Begin(ref SubNegotiation parent) => parent.Option = Option;

    /// <summary>
    /// Anything but one of the seven sub-commands is malformed. Discarded through the core's own IAC-SE
    /// skipper rather than left as a self-loop with no way out: a self-loop from this state has no
    /// reachable IAC/SE transition of its own, so a bad sub-command byte would otherwise wedge the
    /// connection for its entire remaining lifetime, not just this subnegotiation.
    /// </summary>
    [Transition(From = typeof(Charset), To = typeof(SubNegotiating)), OnAny]
    public static void IgnoreMalformed()
    {
    }

    [Transition(From = typeof(Charset), To = typeof(CharsetValue)), On(Request)]
    public static void Requesting(ref CharsetValue to) => to.Kind = Request;

    [Transition(From = typeof(Charset), To = typeof(CharsetValue)), On(Accepted)]
    public static void Accepting(ref CharsetValue to) => to.Kind = Accepted;

    [Transition(From = typeof(CharsetValue)), OnAny, Run]
    public static void Capture(ref CharsetValue self, System.ReadOnlySpan<byte> run)
    {
        self.Escaping = false;
        self.Text ??= [];
        self.Text.AddRange(run.ToArray());
    }

    [Transition(From = typeof(CharsetValue)), On(IAC)]
    public static void Mark(ref CharsetValue self)
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

    [Transition(From = typeof(CharsetValue), To = typeof(Idle)), On(SE)]
    public static class ValueEnded
    {
        public static bool Guard(in CharsetValue self) => self.Escaping;

        public static void Transform()
        {
        }

        public static ValueTask CompletedAsync(TelnetCoreContext context, in CharsetValue from)
        {
            var text = from.Text?.ToArray() ?? [];
            return from.Kind == Request ? context.CharsetRequestAsync(text) : context.CharsetAcceptedAsync(text);
        }
    }

    [Transition(From = typeof(Charset), To = typeof(CharsetTTable)), On(TTableIs)]
    public static class ReadingTTable
    {
        public static void Transform()
        {
        }

        public static ValueTask CompletedAsync(TelnetCoreContext context) => context.CharsetTTableStartedAsync();
    }

    [Transition(From = typeof(CharsetTTable)), OnAny, Run]
    public static class CaptureTTable
    {
        public static void Transform(ref CharsetTTable self, System.ReadOnlySpan<byte> run) => self.Escaping = false;

        public static ValueTask CompletedAsync(TelnetCoreContext context, ReadOnlyMemory<byte> run) =>
            context.CharsetTTableDataAsync(run);
    }

    /// <summary>The first IAC of a pair waits to see whether it doubles into data or is followed by SE.</summary>
    [Transition(From = typeof(CharsetTTable)), On(IAC)]
    public static class MarkTTable
    {
        public static void Transform(ref CharsetTTable self) => self.Escaping = !self.Escaping;

        public static ValueTask CompletedAsync(TelnetCoreContext context, in CharsetTTable self) =>
            self.Escaping ? default : context.CharsetTTableDataAsync(new byte[] { IAC });
    }

    [Transition(From = typeof(CharsetTTable), To = typeof(Idle)), On(SE)]
    public static class TTableEnded
    {
        public static bool Guard(in CharsetTTable self) => self.Escaping;

        public static void Transform()
        {
        }

        public static ValueTask CompletedAsync(TelnetCoreContext context) => context.CharsetTTableEndedAsync();
    }

    [Transition(From = typeof(Charset), To = typeof(CharsetEnding)), On(Rejected)]
    public static void Rejecting(ref CharsetEnding to) => to.Kind = Rejected;

    [Transition(From = typeof(Charset), To = typeof(CharsetEnding)), On(TTableRejected)]
    public static void RejectingTTable(ref CharsetEnding to) => to.Kind = TTableRejected;

    [Transition(From = typeof(Charset), To = typeof(CharsetEnding)), On(TTableAck)]
    public static void AckingTTable(ref CharsetEnding to) => to.Kind = TTableAck;

    [Transition(From = typeof(Charset), To = typeof(CharsetEnding)), On(TTableNak)]
    public static void NakingTTable(ref CharsetEnding to) => to.Kind = TTableNak;

    [Transition(From = typeof(CharsetEnding)), On(IAC)]
    public static void MarkEnding(ref CharsetEnding self) => self.Escaping = true;

    /// <summary>
    /// Anything but IAC here is malformed. Must clear <see cref="CharsetEnding.Escaping"/>, not just
    /// self-loop: otherwise a stray byte between a genuine IAC and an unrelated later SE would still
    /// satisfy <see cref="Ended"/>'s guard and complete with the wrong <see cref="CharsetEnding.Kind"/>
    /// pairing -- the same class of bug MCCP2/MCCP3's guard has.
    /// </summary>
    [Transition(From = typeof(CharsetEnding)), OnAny]
    public static void IgnoreMalformedEnding(ref CharsetEnding self) => self.Escaping = false;

    [Transition(From = typeof(CharsetEnding), To = typeof(Idle)), On(SE)]
    public static class Ended
    {
        public static bool Guard(in CharsetEnding self) => self.Escaping;

        public static void Transform()
        {
        }

        public static ValueTask CompletedAsync(TelnetCoreContext context, in CharsetEnding from) => from.Kind switch
        {
            Rejected => context.CharsetRejectedAsync(),
            TTableRejected => context.CharsetTTableRejectedAsync(),
            TTableAck => context.CharsetTTableAckAsync(),
            _ => context.CharsetTTableNakAsync(),
        };
    }
}
