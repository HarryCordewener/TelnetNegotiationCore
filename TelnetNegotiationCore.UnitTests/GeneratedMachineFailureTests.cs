using System;
using System.Threading.Tasks;
using StateAlchemist;
using TelnetNegotiationCore.Machine;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

public class GeneratedMachineFailureTests
{
    private sealed class ThrowOnceContext(Exception exception) : RecordingTelnetContext
    {
        private bool _throw = true;

        public override void Write(ReadOnlySpan<byte> text)
        {
            if (_throw)
            {
                _throw = false;
                throw exception;
            }

            base.Write(text);
        }
    }

    [Test]
    public async Task CompletedFailureIsReportedAndFollowingInputContinues()
    {
        var context = new ThrowOnceContext(new InvalidOperationException("callback failed"));
        TransitionInfo<byte>? failure = null;
        await using var machine = new TelnetCoreMachine(context, TelnetMachineConfig.Default)
        {
            TransitionFailed = (_, transition) => failure = transition,
        };
        await machine.StartAsync();

        await machine.FireAsync((byte)'a');
        await machine.FireAsync((byte)'b');

        await Assert.That(failure.HasValue).IsTrue();
        await Assert.That(failure!.Value.Phase).IsEqualTo(Phase.Completed);
        await Assert.That(failure.Value.Transition).IsEqualTo("TelnetCoreModule.BeginLine");
        await Assert.That(context.PendingText).IsEqualTo("b");
    }

    [Test]
    public async Task CancellationStillPropagates()
    {
        var context = new ThrowOnceContext(new OperationCanceledException("shutdown"));
        await using var machine = new TelnetCoreMachine(context, TelnetMachineConfig.Default);
        await machine.StartAsync();

        await Assert.That(async () => await machine.FireAsync((byte)'a')).Throws<OperationCanceledException>();
    }
}
