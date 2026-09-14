using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using StateAlchemist;
using TelnetNegotiationCore.Machine;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

public class GeneratedMachineDefinitionTests
{
    [Test]
    public async Task ParsingIsStrictlyPure()
    {
        await Assert.That(TelnetCoreMachine.Definition.Transitions.Where(transition => transition.UsesContext)).IsEmpty();
    }

    [Test]
    public async Task EveryLeafHasAValuePathForEveryByte()
    {
        var definition = TelnetCoreMachine.Definition;
        var parents = definition.States.Select(state => state.Parent).ToArray();
        var leaves = definition.States.Where(state => definition.ChildrenOf(state).Count == 0);

        foreach (var leaf in leaves)
        {
            for (var value = 0; value <= byte.MaxValue; value++)
            {
                var state = leaf.Index;
                var handled = false;
                while (state >= 0 && !handled)
                {
                    handled = definition.Transitions.Any(transition =>
                        transition.Source == state && transition.Trigger.Matches(value));
                    state = parents[state];
                }

                await Assert.That(handled).IsTrue();
            }
        }
    }

    [Test]
    public async Task EachSubnegotiationOptionHasOneOwner()
    {
        var definition = TelnetCoreMachine.Definition;
        var readingOption = definition.IndexOf(typeof(ReadingOption));
        var duplicate = definition.Transitions
            .Where(transition => transition.Source == readingOption && transition.Trigger.Kind == TriggerKind.Value)
            .GroupBy(transition => transition.Trigger.Low)
            .FirstOrDefault(group => group.Count() > 1);

        await Assert.That(duplicate).IsNull();
    }

    [Test]
    public async Task EachNegotiationDirectionHasOneFallbackOwner()
    {
        var definition = TelnetCoreMachine.Definition;
        var directions = new[] { typeof(Willing), typeof(Refusing), typeof(Do), typeof(Dont) };

        foreach (var direction in directions)
        {
            var source = definition.IndexOf(direction);
            var fallbacks = definition.Transitions.Where(transition =>
                transition.Source == source && transition.Trigger.Kind == TriggerKind.Any);

            await Assert.That(fallbacks.Count()).IsEqualTo(1);
        }
    }

    [Test]
    public async Task EveryDeclaredStateIsReachable()
    {
        var definition = TelnetCoreMachine.Definition;
        var reachable = new HashSet<int>();

        var changed = false;
        AddStateAndInitialDescendants(definition.Root.Index);
        while (changed)
        {
            changed = false;
            foreach (var transition in definition.Transitions.Where(transition => reachable.Contains(transition.Source)))
            {
                if (transition.Target >= 0)
                {
                    AddStateAndInitialDescendants(transition.Target);
                }

                foreach (var outcome in transition.Outcomes)
                {
                    AddStateAndInitialDescendants(outcome.Target);
                }
            }
        }

        var unreachable = definition.States.Where(state => !reachable.Contains(state.Index)).Select(state => state.Name);
        await Assert.That(unreachable).IsEmpty();
        return;

        void AddStateAndInitialDescendants(int index)
        {
            if (index < 0)
            {
                return;
            }

            for (var state = index; state >= 0; state = definition.States[state].Parent)
            {
                changed |= reachable.Add(state);
            }

            var initial = definition.ChildrenOf(definition.States[index]).SingleOrDefault(state => state.IsInitial);
            if (initial is not null)
            {
                AddStateAndInitialDescendants(initial.Index);
            }
        }
    }

    [Test]
    public async Task EveryRunHasAValueTransitionThatStopsIt()
    {
        var definition = TelnetCoreMachine.Definition;
        foreach (var run in definition.Transitions.Where(transition => transition.IsRun))
        {
            var stops = definition.Transitions.Where(transition =>
                transition.Source == run.Source && !transition.IsRun &&
                Enumerable.Range(0, 256).Any(value => transition.Trigger.Matches(value)));

            await Assert.That(stops).IsNotEmpty();
        }
    }

    [Test]
    public async Task MalformedCommandsAndSubnegotiationsPlanBackToRecoveryStates()
    {
        await using var machine = new TelnetCoreMachine(new RecordingTelnetContext(), TelnetMachineConfig.Default);
        await machine.StartAsync();

        await machine.FireAsync((byte)255);
        await Assert.That(machine.Plan((byte)1).Target).IsEqualTo(typeof(Idle));

        await machine.FireAsync((byte)250);
        await machine.FireAsync((byte)200);
        await machine.FireAsync((byte)255);
        await Assert.That(machine.Plan((byte)1).Target).IsEqualTo(typeof(SubNegotiating));
    }

    [Test]
    public async Task CheckedInDiagramsMatchTheGeneratedMachine()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../"));
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(root, "docs/generated/telnet-core-machine.mmd")))
            .IsEqualTo(TelnetCoreMachine.Mermaid + "\n");
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(root, "docs/generated/telnet-core-machine.dot")))
            .IsEqualTo(TelnetCoreMachine.Dot + "\n");
    }
}
