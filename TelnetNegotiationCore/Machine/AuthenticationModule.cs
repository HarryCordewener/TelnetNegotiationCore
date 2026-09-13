using System.Collections.Generic;
using System.Threading.Tasks;
using StateAlchemist;

namespace TelnetNegotiationCore.Machine;

// AUTHENTICATION (RFC 2941, option 37): SEND asks the other end which authentication types it offers; IS
// carries its response. Neither carries a doubled IAC for a literal 255 — IAC always ends the capture here,
// matching the original, which has no escape step for this option either.

/// <summary>Reading which of SEND or IS this subnegotiation is.</summary>
public struct Authentication : IState<SubNegotiation>
{
}

/// <summary>Reading the auth type/modifier bytes that follow.</summary>
public struct AuthenticationValue : IState<SubNegotiation>
{
    /// <summary>Whether this is SEND (false) or IS (true).</summary>
    public bool IsReport;

    public List<byte>? Data;

    /// <summary>
    /// True once the peer has sent more than <see cref="AuthenticationModule.MaxDataBytes"/> bytes. An
    /// auth type list or a credential is at most a few hundred bytes even for exotic mechanisms, so this
    /// is far above any legitimate value and exists only to bound a peer that never sends IAC SE.
    /// </summary>
    public bool Overflowed;
}

/// <summary>
/// An IAC was read; the only thing that can legally follow it here is SE. Carries the same fields
/// <see cref="AuthenticationValue"/> did, because leaving that state for this one clears it.
/// </summary>
public struct AuthenticationEnding : IState<SubNegotiation>
{
    public bool IsReport;

    public List<byte>? Data;

    public bool Overflowed;
}

public abstract partial class TelnetCoreContext
{
    /// <summary>SEND: the auth type/modifier pairs the peer offers, as they arrived.</summary>
    public abstract ValueTask AuthenticationSendAsync(byte[] data);

    /// <summary>IS: the auth data the peer answered with.</summary>
    public abstract ValueTask AuthenticationIsAsync(byte[] data);
}

[Module]
public static class AuthenticationModule
{
    private const byte SE = 240;
    private const byte IAC = 255;
    private const byte Is = 0;
    private const byte Send = 1;
    private const byte Option = 37;

    /// <summary>See <see cref="AuthenticationValue.Overflowed"/>.</summary>
    public const int MaxDataBytes = 8192;

    [Transition(From = typeof(ReadingOption), To = typeof(Authentication)), On(Option)]
    public static void Begin(ref SubNegotiation parent) => parent.Option = Option;

    /// <summary>
    /// Anything but SEND or IS here is malformed. Discarded through the core's own IAC-SE skipper rather
    /// than left as a self-loop with no way out: a self-loop from this state has no reachable IAC/SE
    /// transition of its own, so a genuinely malformed command byte would otherwise wedge the connection
    /// for its entire remaining lifetime, not just this subnegotiation.
    /// </summary>
    [Transition(From = typeof(Authentication), To = typeof(SubNegotiating)), OnAny]
    public static void IgnoreMalformed()
    {
    }

    [Transition(From = typeof(Authentication), To = typeof(AuthenticationValue)), On(Send)]
    public static void Sending(ref AuthenticationValue to) => to.IsReport = false;

    [Transition(From = typeof(Authentication), To = typeof(AuthenticationValue)), On(Is)]
    public static void Reporting(ref AuthenticationValue to) => to.IsReport = true;

    [Transition(From = typeof(AuthenticationValue)), OnAny, Run]
    public static void Capture(ref AuthenticationValue self, System.ReadOnlySpan<byte> run)
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

    [Transition(From = typeof(AuthenticationValue), To = typeof(AuthenticationEnding)), On(IAC)]
    public static void Mark(in AuthenticationValue from, ref AuthenticationEnding to)
    {
        to.IsReport = from.IsReport;
        to.Data = from.Data;
        to.Overflowed = from.Overflowed;
    }

    /// <summary>Anything but SE here is malformed; ignored rather than left unhandled.</summary>
    [Transition(From = typeof(AuthenticationEnding)), OnAny]
    public static void IgnoreMalformedEnding()
    {
    }

    /// <summary>
    /// A second IAC: the first one stood for a literal 0xFF in the payload rather than the
    /// terminator. Credentials are whatever the mechanism produced, so that is one byte in 256.
    /// </summary>
    [Transition(From = typeof(AuthenticationEnding), To = typeof(AuthenticationValue)), On(IAC)]
    public static void Escaped(in AuthenticationEnding from, ref AuthenticationValue to)
    {
        to.IsReport = from.IsReport;
        to.Data = from.Data;
        to.Overflowed = from.Overflowed;
        Capture(ref to, stackalloc byte[] { IAC });
    }

    [Transition(From = typeof(AuthenticationEnding), To = typeof(Idle)), On(SE)]
    public static class Ended
    {
        public static void Transform()
        {
        }

        public static ValueTask CompletedAsync(TelnetCoreContext context, in AuthenticationEnding from)
        {
            if (from.Overflowed)
            {
                return default;
            }

            var data = from.Data?.ToArray() ?? [];
            return from.IsReport ? context.AuthenticationIsAsync(data) : context.AuthenticationSendAsync(data);
        }
    }
}
