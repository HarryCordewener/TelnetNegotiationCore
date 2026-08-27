using System.Runtime.CompilerServices;

// The only assembly outside TelnetNegotiationCore that can see internals -- kept here, not folded
// into another file, so that grant is easy to find and audit.
[assembly: InternalsVisibleTo("TelnetNegotiationCore.UnitTests")]
