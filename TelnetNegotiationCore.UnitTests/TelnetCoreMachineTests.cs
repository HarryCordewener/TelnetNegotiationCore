using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using TelnetNegotiationCore.Machine;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// The core telnet framing, driven through the generated machine rather than the configured one. Same wire bytes,
/// same expectations as the interpreter's own tests: this is what says the migration is possible before anything
/// depends on it.
/// </summary>
public class TelnetCoreMachineTests
{
    private const byte SE = 240;
    private const byte NOP = 241;
    private const byte GA = 249;
    private const byte SB = 250;
    private const byte WILL = 251;
    private const byte WONT = 252;
    private const byte DO = 253;
    private const byte DONT = 254;
    private const byte IAC = 255;

    private static async Task<RecordingTelnetContext> Run(params byte[] bytes)
    {
        var recorder = new RecordingTelnetContext();
        await using var machine = new TelnetCoreMachine(recorder);
        await machine.StartAsync();
        await machine.FireAsync(bytes);
        return recorder;
    }

    private static byte[] Wire(string text) => Encoding.ASCII.GetBytes(text);

    [Test]
    public async Task OrdinaryTextIsALineWhenTheNewlineArrives()
    {
        var recorder = await Run(Wire("hello\nworld\n"));

        await Assert.That(recorder.Lines).IsEquivalentTo(new[] { "hello", "world" });
    }

    [Test]
    public async Task ACarriageReturnIsNotPartOfTheLine()
    {
        var recorder = await Run(Wire("hello\r\n"));

        await Assert.That(recorder.Lines).IsEquivalentTo(new[] { "hello" });
    }

    [Test]
    public async Task TheFourVerbsArriveWithTheirOption()
    {
        var recorder = await Run(IAC, WILL, 31, IAC, WONT, 24, IAC, DO, 1, IAC, DONT, 3);

        await Assert.That(recorder.Negotiations).IsEquivalentTo(new[] { "WILL 31", "WONT 24", "DO 1", "DONT 3" });
    }

    [Test]
    public async Task NegotiationInTheMiddleOfALineDoesNotBreakTheLine()
    {
        var recorder = await Run([.. Wire("he"), IAC, WILL, 31, .. Wire("llo\n")]);

        await Assert.That(recorder.Negotiations).IsEquivalentTo(new[] { "WILL 31" });
        await Assert.That(recorder.Lines).IsEquivalentTo(new[] { "hello" });
    }

    [Test]
    public async Task AnEscapedIacIsOneByteOfText()
    {
        var recorder = await Run([.. Wire("a"), IAC, IAC, .. Wire("b\n")]);

        await Assert.That(recorder.Lines).IsEquivalentTo(new[] { "aÿb" });
    }

    /// <summary>Option 198 claims nothing here, so the core frames it generically.</summary>
    [Test]
    public async Task ASubnegotiationEndsAtIacSe()
    {
        var recorder = await Run([IAC, SB, 198, 0, 80, 0, 24, IAC, SE, .. Wire("after\n")]);

        await Assert.That(recorder.SubNegotiations).IsEquivalentTo(new byte[] { 198 });
        await Assert.That(recorder.Lines).IsEquivalentTo(new[] { "after" });
    }

    [Test]
    public async Task AnEscapedIacInsideASubnegotiationDoesNotEndIt()
    {
        var recorder = await Run([IAC, SB, 199, 1, IAC, IAC, 2, IAC, SE, .. Wire("x\n")]);

        await Assert.That(recorder.SubNegotiations).IsEquivalentTo(new byte[] { 199 });
        await Assert.That(recorder.Lines).IsEquivalentTo(new[] { "x" });
    }

    /// <summary>
    /// What the old interpreter's comment on <c>State.Willing</c> describes: a peer that sends IAC where the
    /// option byte belongs. The incomplete negotiation is abandoned and the new command parsed from its start.
    /// </summary>
    [Test]
    public async Task AFreshIacWhereAnOptionBelongsStartsTheNewCommand()
    {
        var recorder = await Run(IAC, WONT, IAC, SB, 86, IAC, SE, IAC, WILL, 86);

        await Assert.That(recorder.SubNegotiations).IsEquivalentTo(new byte[] { 86 });
        await Assert.That(recorder.Negotiations).IsEquivalentTo(new[] { "WILL 86" });
    }

    [Test]
    public async Task NopAndGoAheadAreCommandsThatLeaveTheTextAlone()
    {
        var recorder = await Run([.. Wire("a\n"), IAC, NOP, IAC, GA, .. Wire("b\n")]);

        await Assert.That(recorder.Lines).IsEquivalentTo(new[] { "a", "b" });
    }

    /// <summary>RFC 1073: IAC SB NAWS, width high, width low, height high, height low, IAC SE.</summary>
    [Test]
    public async Task AWindowSizeIsReadFromItsSubnegotiation()
    {
        var recorder = await Run([IAC, SB, 31, 0, 80, 0, 24, IAC, SE, .. Wire("after\n")]);

        await Assert.That(recorder.Windows).IsEquivalentTo(new[] { (80, 24) });
        await Assert.That(recorder.Lines).IsEquivalentTo(new[] { "after" });
    }

    /// <summary>A 255-column window: the 255 arrives escaped, and must not end the subnegotiation.</summary>
    [Test]
    public async Task AnEscapedByteInAWindowSizeIsAValueNotAnEnding()
    {
        var recorder = await Run([IAC, SB, 31, 0, IAC, IAC, 1, 44, IAC, SE]);

        await Assert.That(recorder.Windows).IsEquivalentTo(new[] { (255, 300) });
    }

    /// <summary>A bare 240 is a legal width. Only one that follows an IAC ends the subnegotiation.</summary>
    [Test]
    public async Task AnSeThatNoIacPrecededIsPartOfTheWindowSize()
    {
        var recorder = await Run([IAC, SB, 31, 0, SE, 0, 24, IAC, SE]);

        await Assert.That(recorder.Windows).IsEquivalentTo(new[] { (240, 24) });
    }

    /// <summary>A subnegotiation for an option nothing claims still ends, and the text after it survives.</summary>
    [Test]
    public async Task AnUnclaimedOptionStillFramesCorrectly()
    {
        var recorder = await Run([IAC, SB, 199, 1, 2, 3, IAC, SE, .. Wire("x\n")]);

        await Assert.That(recorder.Windows).IsEmpty();
        await Assert.That(recorder.SubNegotiations).IsEquivalentTo(new byte[] { 199 });
        await Assert.That(recorder.Lines).IsEquivalentTo(new[] { "x" });
    }

    /// <summary>Every byte of a line after the first is taken as a run, so the machine is not fired per byte.</summary>
    [Test]
    public async Task ALongLineIsOneRun()
    {
        var recorder = await Run([.. Wire(new string('a', 4096)), 10]);

        await Assert.That(recorder.Lines[0].Length).IsEqualTo(4096);
    }
}
