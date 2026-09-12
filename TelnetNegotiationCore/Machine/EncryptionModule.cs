using System.Collections.Generic;
using System.Threading.Tasks;
using StateAlchemist;

namespace TelnetNegotiationCore.Machine;

// ENCRYPT (RFC 2946, option 38): the same shape as AUTHENTICATION — SEND asks which types are supported, IS
// carries the initialization data, and IAC always ends the capture with no doubling.

/// <summary>Reading which of SEND or IS this subnegotiation is.</summary>
public struct Encryption : IState<SubNegotiation>
{
}

/// <summary>Reading the encryption type/init bytes that follow.</summary>
public struct EncryptionValue : IState<SubNegotiation>
{
    /// <summary>Whether this is SEND (false) or IS (true).</summary>
    public bool IsReport;

    public List<byte>? Data;
}

/// <summary>
/// An IAC was read; the only thing that can legally follow it here is SE. Carries the same fields
/// <see cref="EncryptionValue"/> did, because leaving that state for this one clears it.
/// </summary>
public struct EncryptionEnding : IState<SubNegotiation>
{
    public bool IsReport;

    public List<byte>? Data;
}

public abstract partial class TelnetCoreContext
{
    /// <summary>SEND: the encryption types the peer supports, as they arrived.</summary>
    public abstract ValueTask EncryptionSendAsync(byte[] data);

    /// <summary>IS: the encryption initialization data the peer sent.</summary>
    public abstract ValueTask EncryptionIsAsync(byte[] data);
}

[Module]
public static class EncryptionModule
{
    private const byte SE = 240;
    private const byte IAC = 255;
    private const byte Is = 0;
    private const byte Send = 1;
    private const byte Option = 38;

    [Transition(From = typeof(ReadingOption), To = typeof(Encryption)), On(Option)]
    public static void Begin(ref SubNegotiation parent) => parent.Option = Option;

    /// <summary>Anything but SEND or IS here is malformed; ignored rather than left unhandled.</summary>
    [Transition(From = typeof(Encryption)), OnAny]
    public static void IgnoreMalformed()
    {
    }

    [Transition(From = typeof(Encryption), To = typeof(EncryptionValue)), On(Send)]
    public static void Sending(ref EncryptionValue to) => to.IsReport = false;

    [Transition(From = typeof(Encryption), To = typeof(EncryptionValue)), On(Is)]
    public static void Reporting(ref EncryptionValue to) => to.IsReport = true;

    [Transition(From = typeof(EncryptionValue)), OnAny, Run]
    public static void Capture(ref EncryptionValue self, System.ReadOnlySpan<byte> run)
    {
        self.Data ??= [];
        self.Data.AddRange(run.ToArray());
    }

    [Transition(From = typeof(EncryptionValue), To = typeof(EncryptionEnding)), On(IAC)]
    public static void Mark(in EncryptionValue from, ref EncryptionEnding to)
    {
        to.IsReport = from.IsReport;
        to.Data = from.Data;
    }

    /// <summary>Anything but SE here is malformed; ignored rather than left unhandled.</summary>
    [Transition(From = typeof(EncryptionEnding)), OnAny]
    public static void IgnoreMalformedEnding()
    {
    }

    [Transition(From = typeof(EncryptionEnding), To = typeof(Idle)), On(SE)]
    public static class Ended
    {
        public static void Transform()
        {
        }

        public static ValueTask CompletedAsync(TelnetCoreContext context, in EncryptionEnding from)
        {
            var data = from.Data?.ToArray() ?? [];
            return from.IsReport ? context.EncryptionIsAsync(data) : context.EncryptionSendAsync(data);
        }
    }
}
