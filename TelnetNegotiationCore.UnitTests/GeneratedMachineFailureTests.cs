using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StateAlchemist;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
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

    [Test]
    public async Task InterpreterLogsStructuredCompletedFailureAndContinues()
    {
        var logger = new CapturingLogger(NullLogger.Instance);
        var fail = true;
        var completed = 0;
        await using var interpreter = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnNegotiation(_ => ValueTask.CompletedTask)
            .OnSubmit((_, _, _) =>
            {
                if (fail)
                {
                    fail = false;
                    throw new InvalidOperationException("submit failed");
                }

                completed++;
                return ValueTask.CompletedTask;
            })
            .BuildAsync();

        await interpreter.InterpretByteArrayAsync("first\nsecond\n"u8.ToArray());
        await interpreter.WaitForProcessingAsync();

        var errors = logger.Entries(LogLevel.Error);
        await Assert.That(errors.Any(message =>
            message.Contains("Completed failure", StringComparison.Ordinal) &&
            message.Contains("TelnetCoreModule.EndOfLineInLine", StringComparison.Ordinal) &&
            message.Contains("ReadingCharacters", StringComparison.Ordinal) &&
            message.Contains("Idle", StringComparison.Ordinal))).IsTrue();
        await Assert.That(completed).IsEqualTo(1);
    }
}
