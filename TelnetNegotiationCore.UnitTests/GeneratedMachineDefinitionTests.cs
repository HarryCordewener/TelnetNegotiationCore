using System;
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
    public async Task CheckedInDiagramsMatchTheGeneratedMachine()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../"));
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(root, "docs/generated/telnet-core-machine.mmd")))
            .IsEqualTo(TelnetCoreMachine.Mermaid + "\n");
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(root, "docs/generated/telnet-core-machine.dot")))
            .IsEqualTo(TelnetCoreMachine.Dot + "\n");
    }
}
