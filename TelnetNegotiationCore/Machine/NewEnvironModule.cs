using System;
using System.Threading.Tasks;
using StateAlchemist;

namespace TelnetNegotiationCore.Machine;

// NEW-ENVIRON (RFC 1572, option 39): one outer command — IS or INFO from the side reporting variables, SEND
// from the side asking for names — followed by repeating VAR/USERVAR name markers, each optionally followed by
// a VALUE marker and its text. A SEND carries names with no values; that falls out of this shape rather than
// needing its own path, because a VALUE marker that never arrives is simply a VALUE marker that never arrives.
//
// The outer command and the field markers share their byte values (IS is 0, and so is VAR) but never their
// position — IS/SEND/INFO can only be the first byte, VAR/VALUE/USERVAR only after it — so they need two states,
// not one, the same way the original's AlmostNegotiatingNEWENVIRON and NegotiatingNEWENVIRON are two states.

/// <summary>Reading which outer command — IS, SEND or INFO — this subnegotiation is.</summary>
public struct NewEnviron : IState<SubNegotiation>
{
}

/// <summary>Reading the repeating VAR/USERVAR/VALUE fields that follow the outer command.</summary>
public struct NewEnvironField : IState<SubNegotiation>
{
    /// <summary>An IAC has been read; the next byte says whether this ends the subnegotiation.</summary>
    public bool Escaping;
}

public abstract partial class TelnetCoreContext
{
    /// <summary>The subnegotiation began. <paramref name="command"/> is IS (0), SEND (1) or INFO (2).</summary>
    public abstract ValueTask NewEnvironStartedAsync(byte command);

    /// <summary>A VAR marker: what follows, until the next marker, is an ordinary variable's name.</summary>
    public abstract ValueTask NewEnvironVarAsync();

    /// <summary>A USERVAR marker: what follows, until the next marker, is a user variable's name.</summary>
    public abstract ValueTask NewEnvironUserVarAsync();

    /// <summary>A VALUE marker: what follows, until the next marker, is the current name's value.</summary>
    public abstract ValueTask NewEnvironValueAsync();

    /// <summary>A stretch of a name's or a value's bytes arrived. Called as many times as it takes.</summary>
    public abstract ValueTask NewEnvironDataAsync(ReadOnlyMemory<byte> data);

    /// <summary>The subnegotiation is complete.</summary>
    public abstract ValueTask NewEnvironEndedAsync();
}

[Module]
public static class NewEnvironModule
{
    private const byte SE = 240;
    private const byte IAC = 255;
    private const byte Is = 0;
    private const byte Send = 1;
    private const byte Info = 2;
    private const byte Var = 0;
    private const byte Value = 1;
    private const byte UserVar = 3;
    private const byte Option = 39;

    [Transition(From = typeof(ReadingOption), To = typeof(NewEnviron)), On(Option)]
    public static void Begin(ref SubNegotiation parent) => parent.Option = Option;

    [Transition(From = typeof(NewEnviron), To = typeof(NewEnvironField)), On(Is)]
    public static class StartedIs
    {
        public static void Transform()
        {
        }

        public static ValueTask CompletedAsync(TelnetCoreContext context) => context.NewEnvironStartedAsync(Is);
    }

    [Transition(From = typeof(NewEnviron), To = typeof(NewEnvironField)), On(Send)]
    public static class StartedSend
    {
        public static void Transform()
        {
        }

        public static ValueTask CompletedAsync(TelnetCoreContext context) => context.NewEnvironStartedAsync(Send);
    }

    [Transition(From = typeof(NewEnviron), To = typeof(NewEnvironField)), On(Info)]
    public static class StartedInfo
    {
        public static void Transform()
        {
        }

        public static ValueTask CompletedAsync(TelnetCoreContext context) => context.NewEnvironStartedAsync(Info);
    }

    /// <summary>
    /// Anything but IS, SEND or INFO here is malformed. Discarded through the core's own IAC-SE skipper
    /// rather than left as a self-loop with no way out: a self-loop from this state has no reachable
    /// IAC/SE transition of its own, so a bad command byte would otherwise wedge the connection for its
    /// entire remaining lifetime, not just this subnegotiation.
    /// </summary>
    [Transition(From = typeof(NewEnviron), To = typeof(SubNegotiating)), OnAny]
    public static void IgnoreMalformed()
    {
    }

    [Transition(From = typeof(NewEnvironField)), On(Var)]
    public static class VarMarker
    {
        public static void Transform(ref NewEnvironField self) => self.Escaping = false;

        public static ValueTask CompletedAsync(TelnetCoreContext context) => context.NewEnvironVarAsync();
    }

    [Transition(From = typeof(NewEnvironField)), On(UserVar)]
    public static class UserVarMarker
    {
        public static void Transform(ref NewEnvironField self) => self.Escaping = false;

        public static ValueTask CompletedAsync(TelnetCoreContext context) => context.NewEnvironUserVarAsync();
    }

    [Transition(From = typeof(NewEnvironField)), On(Value)]
    public static class ValueMarker
    {
        public static void Transform(ref NewEnvironField self) => self.Escaping = false;

        public static ValueTask CompletedAsync(TelnetCoreContext context) => context.NewEnvironValueAsync();
    }

    [Transition(From = typeof(NewEnvironField)), OnAny, Run]
    public static class Capture
    {
        public static void Transform(ref NewEnvironField self, ReadOnlySpan<byte> run) => self.Escaping = false;

        public static ValueTask CompletedAsync(TelnetCoreContext context, ReadOnlyMemory<byte> run) =>
            context.NewEnvironDataAsync(run);
    }

    /// <summary>The first IAC of a pair waits to see whether it doubles into data or is followed by SE.</summary>
    [Transition(From = typeof(NewEnvironField)), On(IAC)]
    public static class Mark
    {
        public static void Transform(ref NewEnvironField self) => self.Escaping = !self.Escaping;

        public static ValueTask CompletedAsync(TelnetCoreContext context, in NewEnvironField self) =>
            self.Escaping ? default : context.NewEnvironDataAsync(new byte[] { IAC });
    }

    [Transition(From = typeof(NewEnvironField), To = typeof(Idle)), On(SE)]
    public static class Ended
    {
        public static bool Guard(in NewEnvironField self) => self.Escaping;

        public static void Transform()
        {
        }

        public static ValueTask CompletedAsync(TelnetCoreContext context) => context.NewEnvironEndedAsync();
    }
}
