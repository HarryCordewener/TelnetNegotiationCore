using System;
using System.Threading.Tasks;
using StateAlchemist;

namespace TelnetNegotiationCore.Machine;

// GMCP (option 201): a package name, a space, and a JSON payload, of no fixed length and possibly large. The
// module streams captured bytes to the context rather than buffering them itself — the context is where a
// maximum message size is enforced, exactly as it is today, and that policy belongs to the application, not
// to the state that merely frames the bytes.

/// <summary>Reading a GMCP message, byte by byte until IAC SE.</summary>
public struct Gmcp : IState<SubNegotiation>
{
    /// <summary>An IAC has been read; the next byte says whether this ends the subnegotiation.</summary>
    public bool Escaping;
}

public abstract partial class TelnetCoreContext
{
    /// <summary>A stretch of the GMCP message's bytes arrived. Called as many times as it takes.</summary>
    public abstract ValueTask GmcpDataAsync(ReadOnlyMemory<byte> data);

    /// <summary>The GMCP message is complete.</summary>
    public abstract ValueTask GmcpEndedAsync();
}

[Module]
public static class GmcpModule
{
    private const byte SE = 240;
    private const byte IAC = 255;
    private const byte Option = 201;

    [Transition(From = typeof(ReadingOption), To = typeof(Gmcp)), On(Option)]
    public static void Begin(ref SubNegotiation parent) => parent.Option = Option;

    [Transition(From = typeof(Gmcp)), OnAny, Run]
    public static class Capture
    {
        public static void Transform(ref Gmcp self, ReadOnlySpan<byte> run) => self.Escaping = false;

        public static ValueTask CompletedAsync(TelnetCoreContext context, ReadOnlyMemory<byte> run) =>
            context.GmcpDataAsync(run);
    }

    /// <summary>
    /// The first IAC of a pair is a wait-and-see: it might double into one literal 255 of data (RFC 854), or it
    /// might be followed by SE and end the message. The second flips <see cref="Gmcp.Escaping"/> back off, which
    /// is the signal to emit the literal here and now.
    /// </summary>
    [Transition(From = typeof(Gmcp)), On(IAC)]
    public static class Mark
    {
        public static void Transform(ref Gmcp self) => self.Escaping = !self.Escaping;

        public static ValueTask CompletedAsync(TelnetCoreContext context, in Gmcp self) =>
            self.Escaping ? default : context.GmcpDataAsync(new byte[] { IAC });
    }

    [Transition(From = typeof(Gmcp), To = typeof(Idle)), On(SE)]
    public static class Ended
    {
        public static bool Guard(in Gmcp self) => self.Escaping;

        public static void Transform()
        {
        }

        public static ValueTask CompletedAsync(TelnetCoreContext context) => context.GmcpEndedAsync();
    }
}
