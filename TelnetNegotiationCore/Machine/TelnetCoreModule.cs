using System;
using System.Threading.Tasks;
using StateAlchemist;

namespace TelnetNegotiationCore.Machine;

/// <summary>
/// What the core machine talks to. The interpreter supplies one of these; a test supplies its own and reads back
/// what the machine did.
/// </summary>
public abstract partial class TelnetCoreContext
// Partial so each protocol module's file can add its own callback method here without every module
// competing to edit one shared file — the same reason each module gets its own states.
{
    /// <summary>Ordinary input, in whatever chunks it arrived. Carriage returns are already gone.</summary>
    public abstract void Write(ReadOnlySpan<byte> text);

    /// <summary>A line ended.</summary>
    public abstract ValueTask SubmitAsync();

    /// <summary>IAC WILL, WONT, DO or DONT arrived with its option byte.</summary>
    public abstract ValueTask NegotiateAsync(byte verb, byte option);

    /// <summary>A subnegotiation ended: its option, and everything between the option byte and IAC SE.</summary>
    public abstract ValueTask SubNegotiatedAsync(byte option, ReadOnlyMemory<byte> payload);

}

/// <summary>
/// Telnet framing: text, IAC, the four negotiation verbs and subnegotiation. Every transition a protocol does not
/// own, which is what <c>SetupStandardProtocol</c> configures at runtime today.
/// </summary>
[Module]
public static class TelnetCoreModule
{
    private const byte SE = 240;
    private const byte NOP = 241;
    private const byte GA = 249;
    private const byte SB = 250;
    private const byte WILL = 251;
    private const byte WONT = 252;
    private const byte DO = 253;
    private const byte DONT = 254;
    private const byte IAC = 255;
    private const byte Newline = 10;
    private const byte CarriageReturn = 13;

    /// <summary>
    /// The first byte of a line, wherever text can start. A run has to be a stay — the bytes after the first are
    /// only the same trigger if the state has not changed — so starting the line is its own transition.
    /// </summary>
    [Transition(From = typeof(Accepting), To = typeof(ReadingCharacters)), OnAny]
    public static void BeginLine(TelnetCoreContext context, byte value) => context.Write(Single(value));

    /// <summary>
    /// The rest of it, a stretch at a time. The stop set is worked out from the other transitions out of this
    /// state, so it cannot drift from them the way the hand-written fast path could.
    /// </summary>
    [Transition(From = typeof(ReadingCharacters)), OnAny, Run]
    public static void MoreText(ref ReadingCharacters self, ReadOnlySpan<byte> run, TelnetCoreContext context) => context.Write(run);

    /// <summary>A carriage return is not part of the line, wherever it arrives.</summary>
    [Transition(From = typeof(Accepting)), On(CarriageReturn)]
    public static void DropReturn()
    {
    }

    /// <summary>
    /// And in the middle of a line. Declared again rather than inherited: a state's <c>[OnAny]</c> shadows what
    /// its ancestors do with values, so the state that takes a run of text has to name what stops that run.
    /// </summary>
    [Transition(From = typeof(ReadingCharacters)), On(CarriageReturn)]
    public static void DropReturnInLine()
    {
    }

    /// <summary>The line ends. Back to waiting for the next one.</summary>
    [Transition(From = typeof(Accepting), To = typeof(Idle)), On(Newline)]
    public static class EndOfLine
    {
        public static void Transform()
        {
        }

        public static ValueTask CompletedAsync(TelnetCoreContext context) => context.SubmitAsync();
    }

    /// <summary>The same, from the middle of a line, which is where it usually arrives.</summary>
    [Transition(From = typeof(ReadingCharacters), To = typeof(Idle)), On(Newline)]
    public static class EndOfLineInLine
    {
        public static void Transform()
        {
        }

        public static ValueTask CompletedAsync(TelnetCoreContext context) => context.SubmitAsync();
    }

    /// <summary>IAC: what follows is a command, not text.</summary>
    [Transition(From = typeof(Accepting), To = typeof(StartNegotiation)), On(IAC)]
    public static void Command()
    {
    }

    /// <summary>A command can interrupt a line, and the line carries on after it.</summary>
    [Transition(From = typeof(ReadingCharacters), To = typeof(StartNegotiation)), On(IAC)]
    public static void CommandInLine()
    {
    }

    /// <summary>IAC IAC is a literal 255 in the text.</summary>
    [Transition(From = typeof(StartNegotiation), To = typeof(ReadingCharacters)), On(IAC)]
    public static void EscapedIac(TelnetCoreContext context) => context.Write(Single(IAC));

    [Transition(From = typeof(StartNegotiation), To = typeof(Willing)), On(WILL)]
    public static void Will()
    {
    }

    [Transition(From = typeof(StartNegotiation), To = typeof(Refusing)), On(WONT)]
    public static void Wont()
    {
    }

    [Transition(From = typeof(StartNegotiation), To = typeof(Do)), On(DO)]
    public static void DoOption()
    {
    }

    [Transition(From = typeof(StartNegotiation), To = typeof(Dont)), On(DONT)]
    public static void DontOption()
    {
    }

    [Transition(From = typeof(StartNegotiation), To = typeof(SubNegotiation)), On(SB)]
    public static void Begin()
    {
    }

    [Transition(From = typeof(StartNegotiation), To = typeof(DoNothing)), On(NOP)]
    public static void Nop()
    {
    }

    [Transition(From = typeof(StartNegotiation), To = typeof(GoAhead)), On(GA)]
    public static void GoingAhead()
    {
    }

    /// <summary>A command nothing claims. The byte is dropped and parsing carries on from the next one.</summary>
    [Transition(From = typeof(StartNegotiation), To = typeof(Idle)), OnAny]
    public static void UnknownCommand()
    {
    }

    /// <summary>
    /// The option byte each verb was waiting for. Four transitions rather than one because the verb is which
    /// state the machine is in — the state is the parameter.
    /// </summary>
    [Transition(From = typeof(Willing), To = typeof(Idle)), OnAny]
    public static class WilledOption
    {
        public static void Transform()
        {
        }

        public static ValueTask CompletedAsync(TelnetCoreContext context, byte value) => context.NegotiateAsync(WILL, value);
    }

    [Transition(From = typeof(Refusing), To = typeof(Idle)), OnAny]
    public static class RefusedOption
    {
        public static void Transform()
        {
        }

        public static ValueTask CompletedAsync(TelnetCoreContext context, byte value) => context.NegotiateAsync(WONT, value);
    }

    [Transition(From = typeof(Do), To = typeof(Idle)), OnAny]
    public static class DoneOption
    {
        public static void Transform()
        {
        }

        public static ValueTask CompletedAsync(TelnetCoreContext context, byte value) => context.NegotiateAsync(DO, value);
    }

    [Transition(From = typeof(Dont), To = typeof(Idle)), OnAny]
    public static class DontOptionByte
    {
        public static void Transform()
        {
        }

        public static ValueTask CompletedAsync(TelnetCoreContext context, byte value) => context.NegotiateAsync(DONT, value);
    }

    /// <summary>
    /// A fresh IAC where an option byte was expected abandons the incomplete negotiation and starts the new
    /// command where it actually begins. Peers do this; see the comment on Willing in the old interpreter.
    /// </summary>
    [Transition(From = typeof(Willing), To = typeof(StartNegotiation), Order = -1), On(IAC)]
    public static void WillInterrupted()
    {
    }

    [Transition(From = typeof(Refusing), To = typeof(StartNegotiation), Order = -1), On(IAC)]
    public static void WontInterrupted()
    {
    }

    [Transition(From = typeof(Do), To = typeof(StartNegotiation), Order = -1), On(IAC)]
    public static void DoInterrupted()
    {
    }

    [Transition(From = typeof(Dont), To = typeof(StartNegotiation), Order = -1), On(IAC)]
    public static void DontInterrupted()
    {
    }

    /// <summary>The subnegotiation's option byte, which lasts as long as the subnegotiation does.</summary>
    [Transition(From = typeof(ReadingOption), To = typeof(SubNegotiating)), OnAny]
    public static void Option(ref SubNegotiation self, byte value) => self.Option = value;

    /// <summary>IAC inside a subnegotiation: either it ends here, or it was an escaped 255.</summary>
    [Transition(From = typeof(SubNegotiating), To = typeof(EndSubNegotiation)), On(IAC)]
    public static void MaybeEnd()
    {
    }

    /// <summary>IAC SE: the subnegotiation is over, and its option byte goes with it.</summary>
    [Transition(From = typeof(EndSubNegotiation), To = typeof(Idle)), On(SE)]
    public static class Ended
    {
        public static void Transform()
        {
        }

        public static ValueTask CompletedAsync(TelnetCoreContext context, in SubNegotiation from) =>
            context.SubNegotiatedAsync(from.Option, default);
    }

    /// <summary>IAC IAC inside a subnegotiation is a literal 255 of payload.</summary>
    [Transition(From = typeof(EndSubNegotiation), To = typeof(SubNegotiating)), On(IAC)]
    public static void EscapedInPayload()
    {
    }

    /// <summary>Anything else after an IAC inside a subnegotiation: the IAC meant nothing.</summary>
    [Transition(From = typeof(EndSubNegotiation), To = typeof(SubNegotiating)), OnAny]
    public static void NotEnded()
    {
    }

    /// <summary>The payload itself, which the core does not read: a protocol's own module does.</summary>
    [Transition(From = typeof(SubNegotiating)), OnAny, Run]
    public static void Payload(ref SubNegotiating self, ReadOnlySpan<byte> run)
    {
    }

    /// <summary>One byte as a span, without allocating: the run path takes spans, and so does the context.</summary>
    private static ReadOnlySpan<byte> Single(byte value) => Bytes.AsSpan(value, 1);

    /// <summary>Every byte value, once, so that a single byte can be handed on as a span of the static array.</summary>
    private static readonly byte[] Bytes = CreateBytes();

    private static byte[] CreateBytes()
    {
        var bytes = new byte[256];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)i;
        }

        return bytes;
    }
}

/// <summary>The core machine, with no protocols in it: what TNC's own interpreter drives.</summary>
[Machine(Root = typeof(Connected), Value = typeof(byte), Context = typeof(TelnetCoreContext))]
[Include(typeof(TelnetCoreModule)), Include(typeof(NawsModule)), Include(typeof(FlowControlModule)), Include(typeof(TerminalSpeedModule)), Include(typeof(XDisplayModule)), Include(typeof(GmcpModule)), Include(typeof(MsdpModule))]
public sealed partial class TelnetCoreMachine;
