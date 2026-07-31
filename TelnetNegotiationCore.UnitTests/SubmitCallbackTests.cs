using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// A NEWLINE only ever reaches <c>WriteToOutput</c> via <c>State.Act</c>, and nothing else enters that
/// state — so every call is a genuine line submission, including a blank one. A blank line (the second
/// CRLF of a "paragraph break" double newline) used to be swallowed before the submit callback ever saw
/// it, because <c>WriteToOutput</c> returned early whenever nothing had been buffered since the last
/// submission. That is indistinguishable, from a caller's side, from the newline never having arrived at
/// all: no event, no empty line, nothing. MU*/MUD servers lean on exactly this for paragraph breaks.
/// </summary>
public class SubmitCallbackTests : BaseTest
{
    [Test]
    public async Task ADoubleNewlineSubmitsAnEmptyLineBetweenTheTwoRealOnes()
    {
        var submitted = new List<byte[]>();

        ValueTask CaptureSubmit(byte[] data, Encoding encoding, TelnetInterpreter ti)
        {
            submitted.Add(data);
            return ValueTask.CompletedTask;
        }

        var ti = await BuildAndWaitAsync(
            new TelnetInterpreterBuilder()
                .UseMode(TelnetInterpreter.TelnetMode.Client)
                .UseLogger(logger)
                .OnSubmit(CaptureSubmit)
                .OnNegotiation(_ => ValueTask.CompletedTask));

        await InterpretAndWaitAsync(ti, Encoding.ASCII.GetBytes("Hello\r\n\r\nWorld\r\n"));

        await Assert.That(submitted.Count).IsEqualTo(3);
        await Assert.That(Encoding.ASCII.GetString(submitted[0])).IsEqualTo("Hello");
        await Assert.That(submitted[1]).IsNotNull();
        await Assert.That(submitted[1].Length).IsEqualTo(0);
        await Assert.That(Encoding.ASCII.GetString(submitted[2])).IsEqualTo("World");

        await ti.DisposeAsync();
    }

    [Test]
    public async Task ALeadingBlankLineSubmitsBeforeAnythingElseHasBeenBuffered()
    {
        var submitted = new List<byte[]>();

        ValueTask CaptureSubmit(byte[] data, Encoding encoding, TelnetInterpreter ti)
        {
            submitted.Add(data);
            return ValueTask.CompletedTask;
        }

        var ti = await BuildAndWaitAsync(
            new TelnetInterpreterBuilder()
                .UseMode(TelnetInterpreter.TelnetMode.Client)
                .UseLogger(logger)
                .OnSubmit(CaptureSubmit)
                .OnNegotiation(_ => ValueTask.CompletedTask));

        // A blank line as the very first thing on the wire: the internal buffer has never been
        // allocated yet, which is the case the fix's null-buffer path has to handle.
        await InterpretAndWaitAsync(ti, Encoding.ASCII.GetBytes("\r\nHello\r\n"));

        await Assert.That(submitted.Count).IsEqualTo(2);
        await Assert.That(submitted[0].Length).IsEqualTo(0);
        await Assert.That(Encoding.ASCII.GetString(submitted[1])).IsEqualTo("Hello");

        await ti.DisposeAsync();
    }
}
