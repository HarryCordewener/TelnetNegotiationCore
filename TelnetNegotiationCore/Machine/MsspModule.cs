using System;
using System.Threading.Tasks;
using StateAlchemist;

namespace TelnetNegotiationCore.Machine;

// MSSP (option 70): repeating MSSP_VAR <name> MSSP_VAL <value> pairs, several VALs per VAR for an array-valued
// field. The marker bytes (1, 2) always mean marker, never data — MSSP text cannot contain them, the same
// limitation the original configuration has. Building the name/value dictionary out of the marker and data
// events stays exactly where it lives today, in MSSPProtocol, called through the context.

/// <summary>Reading an MSSP subnegotiation: a stream of data punctuated by VAR and VAL markers.</summary>
public struct Mssp : IState<SubNegotiation>
{
    /// <summary>An IAC has been read; the next byte says whether this ends the subnegotiation.</summary>
    public bool Escaping;
}

public abstract partial class TelnetCoreContext
{
    /// <summary>The subnegotiation began: whatever the previous one left buffered should be dropped.</summary>
    public abstract ValueTask MsspStartedAsync();

    /// <summary>MSSP_VAR arrived: what came before was a value (unless nothing has arrived yet) and a new name starts.</summary>
    public abstract ValueTask MsspVariableMarkerAsync();

    /// <summary>MSSP_VAL arrived: what came before was a name and a new value starts.</summary>
    public abstract ValueTask MsspValueMarkerAsync();

    /// <summary>A stretch of a name's or a value's bytes arrived. Called as many times as it takes.</summary>
    public abstract ValueTask MsspDataAsync(ReadOnlyMemory<byte> data);

    /// <summary>The subnegotiation is complete.</summary>
    public abstract ValueTask MsspEndedAsync();
}

[Module]
public static class MsspModule
{
    private const byte SE = 240;
    private const byte IAC = 255;
    private const byte VarMarker = 1;
    private const byte ValMarker = 2;
    private const byte Option = 70;

    [Transition(From = typeof(ReadingOption), To = typeof(Mssp)), On(Option)]
    public static class Begin
    {
        public static void Transform(ref SubNegotiation parent) => parent.Option = Option;

        public static ValueTask CompletedAsync(TelnetCoreContext context) => context.MsspStartedAsync();
    }

    [Transition(From = typeof(Mssp)), On(VarMarker)]
    public static class Var
    {
        public static void Transform(ref Mssp self) => self.Escaping = false;

        public static ValueTask CompletedAsync(TelnetCoreContext context) => context.MsspVariableMarkerAsync();
    }

    [Transition(From = typeof(Mssp)), On(ValMarker)]
    public static class Val
    {
        public static void Transform(ref Mssp self) => self.Escaping = false;

        public static ValueTask CompletedAsync(TelnetCoreContext context) => context.MsspValueMarkerAsync();
    }

    [Transition(From = typeof(Mssp)), OnAny, Run]
    public static class Capture
    {
        public static void Transform(ref Mssp self, ReadOnlySpan<byte> run) => self.Escaping = false;

        public static ValueTask CompletedAsync(TelnetCoreContext context, ReadOnlyMemory<byte> run) =>
            context.MsspDataAsync(run);
    }

    /// <summary>The first IAC of a pair waits to see whether it doubles into data or is followed by SE.</summary>
    [Transition(From = typeof(Mssp)), On(IAC)]
    public static class Mark
    {
        public static void Transform(ref Mssp self) => self.Escaping = !self.Escaping;

        public static ValueTask CompletedAsync(TelnetCoreContext context, in Mssp self) =>
            self.Escaping ? default : context.MsspDataAsync(new byte[] { IAC });
    }

    [Transition(From = typeof(Mssp), To = typeof(Idle)), On(SE)]
    public static class Ended
    {
        public static bool Guard(in Mssp self) => self.Escaping;

        public static void Transform()
        {
        }

        public static ValueTask CompletedAsync(TelnetCoreContext context) => context.MsspEndedAsync();
    }
}
