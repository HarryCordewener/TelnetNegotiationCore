using System;
using System.Threading.Tasks;
using StateAlchemist;

namespace TelnetNegotiationCore.Machine;

// ENVIRON (RFC 1408, option 36): NEW-ENVIRON's predecessor and the same shape without USERVAR or INFO — one
// outer command, IS or SEND, followed by repeating VAR name markers each optionally followed by a VALUE marker
// and its text. See NewEnvironModule for why the outer command and the field markers need two states.

/// <summary>Reading which outer command — IS or SEND — this subnegotiation is.</summary>
public struct Environ : IState<SubNegotiation>
{
}

/// <summary>Reading the repeating VAR/VALUE fields that follow the outer command.</summary>
public struct EnvironField : IState<SubNegotiation>
{
    /// <summary>An IAC has been read; the next byte says whether this ends the subnegotiation.</summary>
    public bool Escaping;

    /// <summary>
    /// An <c>ESC</c> has been read, so the next byte is data even if it is a type byte.
    /// </summary>
    /// <remarks>
    /// RFC 1408 escapes the four type bytes inside a name or a value: a literal <c>VAR</c> is sent as
    /// <c>ESC VAR</c>, and likewise for <c>VALUE</c>, <c>USERVAR</c> and <c>ESC</c> itself. Distinct
    /// from <see cref="Escaping"/>, which is about <c>IAC</c> doubling.
    /// </remarks>
    public bool TypeEscaped;
}

public abstract partial class TelnetCoreContext
{
    /// <summary>The subnegotiation began. <paramref name="command"/> is IS (0) or SEND (1).</summary>
    public abstract ValueTask EnvironStartedAsync(byte command);

    /// <summary>A VAR marker: what follows, until the next marker, is a variable's name.</summary>
    public abstract ValueTask EnvironVarAsync();

    /// <summary>A VALUE marker: what follows, until the next marker, is the current name's value.</summary>
    public abstract ValueTask EnvironValueAsync();

    /// <summary>A stretch of a name's or a value's bytes arrived. Called as many times as it takes.</summary>
    public abstract ValueTask EnvironDataAsync(ReadOnlyMemory<byte> data);

    /// <summary>The subnegotiation is complete.</summary>
    public abstract ValueTask EnvironEndedAsync();
}

[Module]
public static class EnvironModule
{
    private const byte SE = 240;
    private const byte IAC = 255;
    private const byte Is = 0;
    private const byte Send = 1;
    private const byte Var = 0;
    private const byte Value = 1;
    private const byte Esc = 2;
    private const byte Option = 36;

    [Transition(From = typeof(ReadingOption), To = typeof(Environ)), On(Option)]
    public static void Begin(ref SubNegotiation parent) => parent.Option = Option;

    [Transition(From = typeof(Environ), To = typeof(EnvironField)), On(Is)]
    public static class StartedIs
    {
        public static void Transform()
        {
        }

        public static ValueTask CompletedAsync(TelnetCoreContext context) => context.EnvironStartedAsync(Is);
    }

    [Transition(From = typeof(Environ), To = typeof(EnvironField)), On(Send)]
    public static class StartedSend
    {
        public static void Transform()
        {
        }

        public static ValueTask CompletedAsync(TelnetCoreContext context) => context.EnvironStartedAsync(Send);
    }

    /// <summary>
    /// Anything but IS or SEND here is malformed. Discarded through the core's own IAC-SE skipper rather
    /// than left as a self-loop with no way out: a self-loop from this state has no reachable IAC/SE
    /// transition of its own, so a bad command byte would otherwise wedge the connection for its entire
    /// remaining lifetime, not just this subnegotiation.
    /// </summary>
    [Transition(From = typeof(Environ), To = typeof(SubNegotiating)), OnAny]
    public static void IgnoreMalformed()
    {
    }

    [Transition(From = typeof(EnvironField)), On(Var)]
    public static class VarMarker
    {
        /// <summary>
        /// An escaped type byte is data, not structure. The run's stop set is computed at compile time
        /// from which transitions exist rather than from what their guards return, so this trigger still
        /// stops the run; declining here falls through to <c>Capture</c>, which takes the byte as data.
        /// </summary>
        public static bool Guard(in EnvironField self) => !self.TypeEscaped;

        public static void Transform(ref EnvironField self) => self.Escaping = false;

        public static ValueTask CompletedAsync(TelnetCoreContext context) => context.EnvironVarAsync();
    }

    [Transition(From = typeof(EnvironField)), On(Value)]
    public static class ValueMarker
    {
        /// <summary>
        /// An escaped type byte is data, not structure. The run's stop set is computed at compile time
        /// from which transitions exist rather than from what their guards return, so this trigger still
        /// stops the run; declining here falls through to <c>Capture</c>, which takes the byte as data.
        /// </summary>
        public static bool Guard(in EnvironField self) => !self.TypeEscaped;

        public static void Transform(ref EnvironField self) => self.Escaping = false;

        public static ValueTask CompletedAsync(TelnetCoreContext context) => context.EnvironValueAsync();
    }

    [Transition(From = typeof(EnvironField)), OnAny, Run]
    public static class Capture
    {
        public static void Transform(ref EnvironField self, ReadOnlySpan<byte> run)
        {
            self.Escaping = false;

            // Whatever the run covered, any pending type escape has been spent on its first byte.
            self.TypeEscaped = false;
        }

        public static ValueTask CompletedAsync(TelnetCoreContext context, ReadOnlyMemory<byte> run) =>
            context.EnvironDataAsync(run);
    }

    /// <summary>
    /// RFC 1408's <c>ESC</c>: the next byte is data even if it is a type byte. <c>ESC ESC</c> is one
    /// literal <c>ESC</c> of data, which is why this toggles rather than sets.
    /// </summary>
    /// <remarks>
    /// An <c>ESC</c> before a byte the RFC does not list as escapable is still consumed and the byte
    /// delivered literally, and a trailing <c>ESC</c> before <c>IAC SE</c> escapes nothing and is
    /// consumed. Both match libtelnet, which skips the <c>ESC</c> unconditionally.
    /// </remarks>
    [Transition(From = typeof(EnvironField)), On(Esc)]
    public static class TypeEscape
    {
        public static void Transform(ref EnvironField self) => self.TypeEscaped = !self.TypeEscaped;

        public static ValueTask CompletedAsync(TelnetCoreContext context, in EnvironField self) =>
            self.TypeEscaped ? default : context.EnvironDataAsync(new byte[] { Esc });
    }

    /// <summary>The first IAC of a pair waits to see whether it doubles into data or is followed by SE.</summary>
    [Transition(From = typeof(EnvironField)), On(IAC)]
    public static class Mark
    {
        public static void Transform(ref EnvironField self) => self.Escaping = !self.Escaping;

        public static ValueTask CompletedAsync(TelnetCoreContext context, in EnvironField self) =>
            self.Escaping ? default : context.EnvironDataAsync(new byte[] { IAC });
    }

    [Transition(From = typeof(EnvironField), To = typeof(Idle)), On(SE)]
    public static class Ended
    {
        public static bool Guard(in EnvironField self) => self.Escaping;

        public static void Transform()
        {
        }

        public static ValueTask CompletedAsync(TelnetCoreContext context) => context.EnvironEndedAsync();
    }
}
