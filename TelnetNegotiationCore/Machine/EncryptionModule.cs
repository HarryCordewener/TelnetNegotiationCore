using System.Collections.Generic;
using System.Threading.Tasks;
using StateAlchemist;

namespace TelnetNegotiationCore.Machine;

// ENCRYPT (RFC 2946, option 38): the same shape as AUTHENTICATION — SEND asks which types are supported, IS
// carries the initialization data, and an IAC is either the terminator or, doubled, a literal 0xFF in the
// payload — an initialization blob or a keyid is arbitrary bytes, so that is one value in 256. START and END are the
// WILL side's own markers -- "I am now/no longer encrypting what I send" -- rather than part of the
// SEND/IS exchange. START carries a keyid payload, captured the same way SEND/IS are; END carries none at
// all, so one boolean field does the job the way MCCP2/MCCP3's markers already do, rather than a second
// state for a byte that is never there.

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

    /// <summary>
    /// True once the peer has sent more than <see cref="EncryptionModule.MaxDataBytes"/> bytes. An
    /// encryption type list or its initialization data is at most a few hundred bytes even for exotic
    /// mechanisms, so this is far above any legitimate value and exists only to bound a peer that never
    /// sends IAC SE.
    /// </summary>
    public bool Overflowed;
}

/// <summary>
/// An IAC was read; the only thing that can legally follow it here is SE. Carries the same fields
/// <see cref="EncryptionValue"/> did, because leaving that state for this one clears it.
/// </summary>
public struct EncryptionEnding : IState<SubNegotiation>
{
    public bool IsReport;

    public List<byte>? Data;

    public bool Overflowed;
}

/// <summary>Reading the keyid bytes that follow START.</summary>
public struct EncryptionStart : IState<SubNegotiation>
{
    public List<byte>? Data;

    /// <summary>See <see cref="EncryptionValue.Overflowed"/>; the keyid is bounded the same way.</summary>
    public bool Overflowed;
}

/// <summary>
/// An IAC was read while reading START's keyid; the only thing that can legally follow it here is SE.
/// Carries the same field <see cref="EncryptionStart"/> did, because leaving that state for this one
/// clears it.
/// </summary>
public struct EncryptionStartEnding : IState<SubNegotiation>
{
    public List<byte>? Data;

    public bool Overflowed;
}

/// <summary>
/// END carries no payload of its own -- <c>IAC SB ENCRYPT END IAC SE</c> goes straight from the command
/// byte to the marker's own <c>IAC SE</c> -- so <see cref="Escaping"/> alone tracks whether the IAC that
/// must precede SE has been seen yet, the same one-state shape MCCP2/MCCP3's markers use.
/// </summary>
public struct EncryptionEnd : IState<SubNegotiation>
{
    public bool Escaping;
}

public abstract partial class TelnetCoreContext
{
    /// <summary>SEND: the encryption types the peer supports, as they arrived.</summary>
    public abstract ValueTask EncryptionSendAsync(byte[] data);

    /// <summary>IS: the encryption initialization data the peer sent.</summary>
    public abstract ValueTask EncryptionIsAsync(byte[] data);

    /// <summary>START: the peer is now encrypting what it sends, using this keyid.</summary>
    public abstract ValueTask EncryptionStartAsync(byte[] keyId);

    /// <summary>END: the peer has stopped encrypting what it sends.</summary>
    public abstract ValueTask EncryptionEndAsync();
}

[Module]
public static class EncryptionModule
{
    private const byte SE = 240;
    private const byte IAC = 255;
    private const byte Is = 0;
    private const byte Send = 1;
    private const byte Start = 3;
    private const byte End = 4;
    private const byte Option = 38;

    /// <summary>See <see cref="EncryptionValue.Overflowed"/>.</summary>
    public const int MaxDataBytes = 8192;

    [Transition(From = typeof(ReadingOption), To = typeof(Encryption)), On(Option)]
    public static void Begin(ref SubNegotiation parent) => parent.Option = Option;

    /// <summary>
    /// Anything but SEND, IS, START or END here is malformed. Discarded through the core's own IAC-SE
    /// skipper rather than left as a self-loop with no way out: a self-loop from this state has no
    /// reachable IAC/SE transition of its own, so a bad command byte would otherwise wedge the connection
    /// for its entire remaining lifetime, not just this subnegotiation.
    /// </summary>
    [Transition(From = typeof(Encryption), To = typeof(SubNegotiating)), OnAny]
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
        if (self.Overflowed)
        {
            return;
        }

        self.Data ??= [];
        if (self.Data.Count + run.Length > MaxDataBytes)
        {
            self.Overflowed = true;
            return;
        }

        self.Data.AddRange(run.ToArray());
    }

    [Transition(From = typeof(EncryptionValue), To = typeof(EncryptionEnding)), On(IAC)]
    public static void Mark(in EncryptionValue from, ref EncryptionEnding to)
    {
        to.IsReport = from.IsReport;
        to.Data = from.Data;
        to.Overflowed = from.Overflowed;
    }

    /// <summary>Anything but SE or a second IAC here is malformed; ignored rather than left unhandled.</summary>
    [Transition(From = typeof(EncryptionEnding)), OnAny]
    public static void IgnoreMalformedEnding()
    {
    }

    /// <summary>
    /// A second IAC: the first one stood for a literal 0xFF in the payload rather than the
    /// terminator, so it goes into the data and the capture resumes.
    /// </summary>
    [Transition(From = typeof(EncryptionEnding), To = typeof(EncryptionValue)), On(IAC)]
    public static void Escaped(in EncryptionEnding from, ref EncryptionValue to)
    {
        to.IsReport = from.IsReport;
        to.Data = from.Data;
        to.Overflowed = from.Overflowed;
        Capture(ref to, stackalloc byte[] { IAC });
    }

    [Transition(From = typeof(EncryptionEnding), To = typeof(Idle)), On(SE)]
    public static class Ended
    {
        public static void Transform()
        {
        }

        public static ValueTask CompletedAsync(TelnetCoreContext context, in EncryptionEnding from)
        {
            if (from.Overflowed)
            {
                return default;
            }

            var data = from.Data?.ToArray() ?? [];
            return from.IsReport ? context.EncryptionIsAsync(data) : context.EncryptionSendAsync(data);
        }
    }

    [Transition(From = typeof(Encryption), To = typeof(EncryptionStart)), On(Start)]
    public static void Starting()
    {
    }

    [Transition(From = typeof(EncryptionStart)), OnAny, Run]
    public static void CaptureStart(ref EncryptionStart self, System.ReadOnlySpan<byte> run)
    {
        if (self.Overflowed)
        {
            return;
        }

        self.Data ??= [];
        if (self.Data.Count + run.Length > MaxDataBytes)
        {
            self.Overflowed = true;
            return;
        }

        self.Data.AddRange(run.ToArray());
    }

    [Transition(From = typeof(EncryptionStart), To = typeof(EncryptionStartEnding)), On(IAC)]
    public static void MarkStart(in EncryptionStart from, ref EncryptionStartEnding to)
    {
        to.Data = from.Data;
        to.Overflowed = from.Overflowed;
    }

    /// <summary>
    /// Anything but SE here is malformed. Discarded through the core's own IAC-SE skipper rather than
    /// left as a self-loop: a self-loop here has no reachable IAC/SE transition of its own (wedging the
    /// connection), and leaving <see cref="EncryptionStartEnding"/> pending would let a later, unrelated
    /// bare SE still satisfy <see cref="StartEnded"/> and misfire <c>EncryptionStartAsync</c>.
    /// </summary>
    [Transition(From = typeof(EncryptionStartEnding), To = typeof(SubNegotiating)), OnAny]
    public static void IgnoreMalformedStartEnding()
    {
    }

    /// <summary>A doubled IAC inside START's keyid, the same as in an IS body.</summary>
    [Transition(From = typeof(EncryptionStartEnding), To = typeof(EncryptionStart)), On(IAC)]
    public static void EscapedStart(in EncryptionStartEnding from, ref EncryptionStart to)
    {
        to.Data = from.Data;
        to.Overflowed = from.Overflowed;
        CaptureStart(ref to, stackalloc byte[] { IAC });
    }

    [Transition(From = typeof(EncryptionStartEnding), To = typeof(Idle)), On(SE)]
    public static class StartEnded
    {
        public static void Transform()
        {
        }

        public static ValueTask CompletedAsync(TelnetCoreContext context, in EncryptionStartEnding from) =>
            from.Overflowed ? default : context.EncryptionStartAsync(from.Data?.ToArray() ?? []);
    }

    [Transition(From = typeof(Encryption), To = typeof(EncryptionEnd)), On(End)]
    public static void Ending()
    {
    }

    [Transition(From = typeof(EncryptionEnd)), On(IAC)]
    public static void MarkEnd(ref EncryptionEnd self) => self.Escaping = true;

    /// <summary>
    /// Anything but the IAC that precedes SE, or SE once escaping, is malformed. Resets
    /// <see cref="EncryptionEnd.Escaping"/> so a later, unrelated bare SE cannot still satisfy
    /// <see cref="EndEnded"/>'s guard and misfire <c>EncryptionEndAsync</c>.
    /// </summary>
    [Transition(From = typeof(EncryptionEnd)), OnAny]
    public static void IgnoreMalformedEnd(ref EncryptionEnd self) => self.Escaping = false;

    [Transition(From = typeof(EncryptionEnd), To = typeof(Idle)), On(SE)]
    public static class EndEnded
    {
        public static bool Guard(in EncryptionEnd self) => self.Escaping;

        public static void Transform()
        {
        }

        public static ValueTask CompletedAsync(TelnetCoreContext context) => context.EncryptionEndAsync();
    }
}
