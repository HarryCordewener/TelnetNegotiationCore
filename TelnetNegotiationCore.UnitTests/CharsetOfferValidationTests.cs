using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Protocols;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

public class CharsetOfferValidationTests : BaseTest
{
    private static byte[] Accepted(string name) => [255, 250, 42, 2, .. Encoding.ASCII.GetBytes(name), 255, 240];

    [Test]
    [Arguments("unoffered", false)]
    [Arguments("unsupported", false)]
    [Arguments("unsupported", true)]
    [Arguments("rejected", false)]
    [Arguments("withdrawn", false)]
    [Arguments("replayed", false)]
    [Arguments("unknown", false)]
    public async Task InvalidAcceptancePreservesDecoderAndBufferedInput(string scenario, bool fragmented)
    {
        var output = new List<byte>();
        var lines = new List<string>();
        var changed = new List<string>();
        var charset = new CharsetProtocol
        {
            AllowedEncodings = () => Encoding.GetEncodings().Where(e => e.CodePage is 65001 or 28591 or 20127)
        }.OnCharsetChange(e => { changed.Add(e.WebName); return default; });
        await using var server = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server).UseLogger(logger)
            .OnSubmit((bytes, encoding, _) => { lines.Add(encoding.GetString(bytes)); return default; })
            .OnNegotiation(bytes => { output.AddRange(bytes.ToArray()); return default; }).AddPlugin(charset));
        if (scenario != "unoffered") await InterpretAndWaitAsync(server, [255, 253, 42]);
        if (scenario == "rejected") await InterpretAndWaitAsync(server, [255, 250, 42, 3, 255, 240]);
        if (scenario == "withdrawn") await InterpretAndWaitAsync(server, [255, 254, 42]);
        if (scenario == "replayed") await InterpretAndWaitAsync(server, Accepted("utf-8"));
        output.Clear();
        changed.Clear();
        var frame = Accepted(scenario == "unsupported" ? "utf-16" : scenario == "unknown" ? "not-a-charset" : "iso-8859-1");
        if (fragmented)
        {
            foreach (var b in frame) await InterpretAndWaitAsync(server, [b]);
            await InterpretAndWaitAsync(server, Encoding.UTF8.GetBytes("café雪\r\n"));
        }
        else await InterpretAndWaitAsync(server, [.. frame, .. Encoding.UTF8.GetBytes("café雪\r\n")]);
        await Assert.That(server.CurrentEncoding.WebName).IsEqualTo("utf-8");
        await Assert.That(charset.CurrentEncoding.WebName).IsEqualTo("utf-8");
        await Assert.That(changed.Count).IsEqualTo(0);
        await Assert.That(lines.Single()).IsEqualTo("café雪");
        await AssertByteArraysEqual(output.ToArray(), [255, 250, 42, 3, 255, 240]);
    }

    [Test]
    [Arguments("ISO-8859-1")]
    [Arguments("iso-ir-100")]
    public async Task AcceptanceUsesActualOfferSnapshotAndCanonicalAliases(string alias)
    {
        var allowed = new[] { 28591 };
        var output = new List<byte>();
        var charset = new CharsetProtocol { AllowedEncodings = () => Encoding.GetEncodings().Where(e => allowed.Contains(e.CodePage)) };
        await using var server = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server).UseLogger(logger).OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(bytes => { output.AddRange(bytes.ToArray()); return default; }).AddPlugin(charset));
        await InterpretAndWaitAsync(server, [255, 253, 42]);
        // Configuration changes after the request must not invent a different outstanding offer.
        allowed = [65001];
        await InterpretAndWaitAsync(server, Accepted(alias));
        await Assert.That(server.CurrentEncoding.WebName).IsEqualTo("iso-8859-1");
    }

    [Test]
    public async Task RepeatedOfferReflectsNewAllowedSetAndInvalidatesPreviousOffer()
    {
        var allowed = new[] { 28591 };
        var output = new List<byte>();
        var charset = new CharsetProtocol { AllowedEncodings = () => Encoding.GetEncodings().Where(e => allowed.Contains(e.CodePage)) };
        await using var server = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server).UseLogger(logger).OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(bytes => { output.AddRange(bytes.ToArray()); return default; }).AddPlugin(charset));
        await InterpretAndWaitAsync(server, [255, 253, 42]);
        allowed = [65001];
        output.Clear();
        await InterpretAndWaitAsync(server, [255, 253, 42]);
        await AssertByteArraysEqual(output.ToArray(), [255, 250, 42, 1, .. ";utf-8"u8.ToArray(), 255, 240]);
        await InterpretAndWaitAsync(server, Accepted("iso-8859-1"));
        await Assert.That(server.CurrentEncoding.WebName).IsEqualTo("utf-8");
    }
    /// <summary>
    /// Axmud refuses a server's WILL CHARSET and takes only its DO, then waits for the server's REQUEST.
    /// The server asks that way once, and the client's WILL is the agreement, not a fresh offer to answer.
    /// </summary>
    [Test]
    public async Task RefusedOfferIsAskedOnceTheOtherWayRound()
    {
        var output = new List<byte>();
        var charset = new CharsetProtocol
        {
            AllowedEncodings = () => Encoding.GetEncodings().Where(e => e.CodePage is 65001 or 28591),
            CharsetOrder = [Encoding.UTF8, Encoding.Latin1]
        };
        await using var server = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server).UseLogger(logger).OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(bytes => { output.AddRange(bytes.ToArray()); return default; }).AddPlugin(charset));
        output.Clear();

        await InterpretAndWaitAsync(server, [255, 254, 42]);
        await AssertByteArraysEqual(output.ToArray(), [255, 253, 42]);
        await Assert.That(charset.IsNegotiated).IsFalse();

        output.Clear();
        await InterpretAndWaitAsync(server, [255, 251, 42]);
        await AssertByteArraysEqual(output.ToArray(), [255, 250, 42, 1, .. ";utf-8;iso-8859-1"u8.ToArray(), 255, 240]);
        await Assert.That(charset.IsNegotiated).IsTrue();

        await InterpretAndWaitAsync(server, Accepted("utf-8"));
        await Assert.That(server.CurrentEncoding.WebName).IsEqualTo("utf-8");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task OfferIsAskedTheOtherWayRoundAtMostOnce(bool agreedFirst)
    {
        var output = new List<byte>();
        var charset = new CharsetProtocol { AllowedEncodings = () => Encoding.GetEncodings().Where(e => e.CodePage is 65001) };
        await using var server = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server).UseLogger(logger).OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(bytes => { output.AddRange(bytes.ToArray()); return default; }).AddPlugin(charset));
        if (agreedFirst)
        {
            // A client that took the WILL and later withdraws has answered; it is not asked again.
            await InterpretAndWaitAsync(server, [255, 253, 42]);
        }
        else
        {
            await InterpretAndWaitAsync(server, [255, 254, 42]);
            await InterpretAndWaitAsync(server, [255, 252, 42]);
        }

        output.Clear();
        await InterpretAndWaitAsync(server, [255, 254, 42]);
        await AssertByteArraysEqual(output.ToArray(), []);
        await Assert.That(charset.IsNegotiated).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task EmptyOrFailedOfferCannotAuthorizeAcceptance(bool failWrite)
    {
        var output = new List<byte>();
        var fail = false;
        var charset = new CharsetProtocol
        {
            AllowedEncodings = () => failWrite ? Encoding.GetEncodings() : []
        };
        await using var server = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server).UseLogger(logger).OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(bytes =>
            {
                if (fail) throw new System.IO.IOException("test failed offer write");
                output.AddRange(bytes.ToArray());
                return default;
            }).AddPlugin(charset));
        fail = failWrite;
        await InterpretAndWaitAsync(server, [255, 253, 42]);
        fail = false;
        output.Clear();
        await InterpretAndWaitAsync(server, Accepted("iso-8859-1"));
        await Assert.That(server.CurrentEncoding.WebName).IsEqualTo("utf-8");
        await AssertByteArraysEqual(output.ToArray(), [255, 250, 42, 3, 255, 240]);
    }

}
