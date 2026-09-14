using System;
using StateAlchemist;

namespace TelnetNegotiationCore.Machine;

public sealed partial class TelnetCoreMachine
{
    /// <summary>Creates a machine with the default carriage-return behavior.</summary>
    public TelnetCoreMachine(TelnetCoreContext context) : this(context, TelnetMachineConfig.Default)
    {
    }

    internal Action<Exception, TransitionInfo<byte>>? TransitionFailed { get; set; }
    internal Action<Type, byte>? ValueUnhandled { get; set; }

    partial void OnGuardException(Exception exception, in TransitionInfo<byte> transition, ref ExceptionResolution resolution) =>
        Resolve(exception, transition, ExceptionResolution.Skip, ref resolution);

    partial void OnTransformException(Exception exception, in TransitionInfo<byte> transition, ref ExceptionResolution resolution) =>
        Resolve(exception, transition, ExceptionResolution.Skip, ref resolution);

    partial void OnExitedException(Exception exception, in TransitionInfo<byte> transition, ref ExceptionResolution resolution) =>
        Resolve(exception, transition, ExceptionResolution.Continue, ref resolution);

    partial void OnEnteredException(Exception exception, in TransitionInfo<byte> transition, ref ExceptionResolution resolution) =>
        Resolve(exception, transition, ExceptionResolution.Continue, ref resolution);

    partial void OnCompletedException(Exception exception, in TransitionInfo<byte> transition, ref ExceptionResolution resolution) =>
        Resolve(exception, transition, ExceptionResolution.Continue, ref resolution);

    partial void OnUnhandled(StateId state, byte value) => ValueUnhandled?.Invoke(StateType, value);

    private void Resolve(
        Exception exception,
        in TransitionInfo<byte> transition,
        ExceptionResolution recovery,
        ref ExceptionResolution resolution)
    {
        if (exception is OperationCanceledException)
        {
            return;
        }

        TransitionFailed?.Invoke(exception, transition);
        resolution = recovery;
    }
}
