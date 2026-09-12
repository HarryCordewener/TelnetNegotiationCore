using System;
using System.Threading.Tasks;
using StateAlchemist;

namespace TelnetNegotiationCore.Machine;

// MSDP (option 69): the same shape as GMCP — a payload of no fixed length, streamed to the context rather than
// buffered here, so the application's maximum-message-size policy still lives in one place.

/// <summary>Reading an MSDP message, byte by byte until IAC SE.</summary>
public struct Msdp : IState<SubNegotiation>
{
    /// <summary>An IAC has been read; the next byte says whether this ends the subnegotiation.</summary>
    public bool Escaping;
}

public abstract partial class TelnetCoreContext
{
    /// <summary>A stretch of the MSDP message's bytes arrived. Called as many times as it takes.</summary>
    public abstract ValueTask MsdpDataAsync(ReadOnlyMemory<byte> data);

    /// <summary>The MSDP message is complete.</summary>
    public abstract ValueTask MsdpEndedAsync();
}

[Module]
public static class MsdpModule
{
    private const byte SE = 240;
    private const byte IAC = 255;
    private const byte Option = 69;

    [Transition(From = typeof(ReadingOption), To = typeof(Msdp)), On(Option)]
    public static void Begin(ref SubNegotiation parent) => parent.Option = Option;

    [Transition(From = typeof(Msdp)), OnAny, Run]
    public static class Capture
    {
        public static void Transform(ref Msdp self, ReadOnlySpan<byte> run) => self.Escaping = false;

        public static ValueTask CompletedAsync(TelnetCoreContext context, ReadOnlyMemory<byte> run) =>
            context.MsdpDataAsync(run);
    }

    /// <summary>The first IAC of a pair waits to see whether it doubles into data or is followed by SE.</summary>
    [Transition(From = typeof(Msdp)), On(IAC)]
    public static class Mark
    {
        public static void Transform(ref Msdp self) => self.Escaping = !self.Escaping;

        public static ValueTask CompletedAsync(TelnetCoreContext context, in Msdp self) =>
            self.Escaping ? default : context.MsdpDataAsync(new byte[] { IAC });
    }

    [Transition(From = typeof(Msdp), To = typeof(Idle)), On(SE)]
    public static class Ended
    {
        public static bool Guard(in Msdp self) => self.Escaping;

        public static void Transform()
        {
        }

        public static ValueTask CompletedAsync(TelnetCoreContext context) => context.MsdpEndedAsync();
    }
}
