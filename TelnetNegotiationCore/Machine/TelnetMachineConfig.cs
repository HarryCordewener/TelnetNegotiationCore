using TelnetNegotiationCore.Models;

namespace TelnetNegotiationCore.Machine;

/// <summary>Immutable choices that determine how one telnet stream is parsed.</summary>
public readonly record struct TelnetMachineConfig(CarriageReturnMode CarriageReturnMode)
{
    public static TelnetMachineConfig Default => new(CarriageReturnMode.Drop);
}
