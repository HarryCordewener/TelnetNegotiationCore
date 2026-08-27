using System.Runtime.CompilerServices;

// Grants the unit test project access to internal members. Kept in its own file, rather than folded
// into an existing one, so the one thing it does is easy to find and easy to audit: this is the only
// assembly outside TelnetNegotiationCore itself that can see internals, and it exists purely to test
// seams -- such as PacketPatchProtocol.OnTimerElapsed -- that have no reason to be part of the public
// API a real consumer would ever call.
[assembly: InternalsVisibleTo("TelnetNegotiationCore.UnitTests")]
