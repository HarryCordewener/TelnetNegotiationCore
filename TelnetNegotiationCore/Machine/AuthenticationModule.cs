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
}

/// <summary>
/// An IAC was read; the only thing that can legally follow it here is SE. Carries the same fields
/// <see cref="AuthenticationValue"/> did, because leaving that state for this one clears it.
/// </summary>
public struct AuthenticationEnding : IState<SubNegotiation>
{
    public bool IsReport;

    public List<byte>? Data;
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

    [Transition(From = typeof(ReadingOption), To = typeof(Authentication)), On(Option)]
    public static void Begin(ref SubNegotiation parent) => parent.Option = Option;

    /// <summary>Anything but SEND or IS here is malformed; ignored rather than left unhandled.</summary>
    [Transition(From = typeof(Authentication)), OnAny]
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
        self.Data ??= [];
        self.Data.AddRange(run.ToArray());
    }

    [Transition(From = typeof(AuthenticationValue), To = typeof(AuthenticationEnding)), On(IAC)]
    public static void Mark(in AuthenticationValue from, ref AuthenticationEnding to)
    {
        to.IsReport = from.IsReport;
        to.Data = from.Data;
    }

    /// <summary>Anything but SE here is malformed; ignored rather than left unhandled.</summary>
    [Transition(From = typeof(AuthenticationEnding)), OnAny]
    public static void IgnoreMalformedEnding()
    {
    }

    [Transition(From = typeof(AuthenticationEnding), To = typeof(Idle)), On(SE)]
    public static class Ended
    {
        public static void Transform()
        {
        }

        public static ValueTask CompletedAsync(TelnetCoreContext context, in AuthenticationEnding from)
        {
            var data = from.Data?.ToArray() ?? [];
            return from.IsReport ? context.AuthenticationIsAsync(data) : context.AuthenticationSendAsync(data);
        }
    }
}
