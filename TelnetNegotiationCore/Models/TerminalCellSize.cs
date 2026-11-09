namespace TelnetNegotiationCore.Models;

/// <summary>One terminal character cell in physical pixels, not display-independent units.</summary>
/// <param name="Width">The measured cell width.</param>
/// <param name="Height">The measured cell height.</param>
public sealed record TerminalCellSize(int Width, int Height)
{
    /// <summary>Whether both dimensions are positive and fit an unsigned 16-bit pixel count.</summary>
    public bool IsValid => Width is > 0 and <= 65535 && Height is > 0 and <= 65535;
}
