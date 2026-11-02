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

/// <summary>
/// GMCP's data field is UTF-8 JSON whatever CHARSET agreed for ordinary text
/// (https://tintin.mudhalla.net/protocols/gmcp/). Issue #155.
/// </summary>
public class GmcpCharsetIndependenceTests : BaseTest
{
    // Accented Latin-1 text and a character outside Latin-1, unescaped, so a serializer's \uXXXX
    // escaping cannot hide a wrong encoding.
    private const string Json = "{\"name\":\"café雪\"}";

    private static readonly byte[] DoCharset = [255, 253, 42];

    private static readonly byte[] DoGmcp = [255, 253, 201];

    private static byte[] AcceptCharset(string name) => [255, 250, 42, 2, .. Encoding.ASCII.GetBytes(name), 255, 240];

    private static byte[] GmcpWire(string text) => [255, 250, 201, .. Encoding.UTF8.GetBytes(text), 255, 240];

    /// <summary>Agrees <paramref name="charset"/> for text and GMCP, then forgets what was sent.</summary>
    private static async Task NegotiateAsync(TelnetInterpreter server, string charset, List<byte> output)
    {
        await InterpretAndWaitAsync(server, DoCharset);
        await InterpretAndWaitAsync(server, [.. AcceptCharset(charset), .. DoGmcp]);
        await Assert.That(server.CurrentEncoding.WebName).IsEqualTo(charset);
        output.Clear();
    }

    [Test]
    [Arguments("iso-8859-1")]
    [Arguments("us-ascii")]
    public async Task SendAndReceiveUseUtf8RegardlessOfTextCharset(string name)
    {
        var output = new List<byte>();
        var received = new List<string>();
        await using var server = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server).UseLogger(logger).OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(bytes => { output.AddRange(bytes.ToArray()); return default; })
            .AddPlugin<CharsetProtocol>()
            .AddPlugin<GMCPProtocol>().OnGMCPMessage(data => { received.Add(data.Info); return default; }));
        await NegotiateAsync(server, name, output);
        var wire = GmcpWire("Test.Message " + Json);
        await server.SendGMCPCommand("Test.Message", Json);
        await AssertByteArraysEqual(output.ToArray(), wire);
        await InterpretAndWaitAsync(server, wire);
        await Assert.That(received[0]).IsEqualTo(Json);
    }

    /// <summary>
    /// The message arrives a byte at a time, so the two bytes of é, and IAC and SE, land in
    /// different reads. Decoding happens once the frame is whole, so the split changes nothing.
    /// </summary>
    [Test]
    public async Task AMessageSplitInsideACharacterAndTheFrameStillDecodesAsUtf8()
    {
        var output = new List<byte>();
        var received = new List<(string Package, string Info)>();
        await using var server = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server).UseLogger(logger).OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(bytes => { output.AddRange(bytes.ToArray()); return default; })
            .AddPlugin<CharsetProtocol>()
            .AddPlugin<GMCPProtocol>().OnGMCPMessage(data => { received.Add(data); return default; }));
        await NegotiateAsync(server, "iso-8859-1", output);

        foreach (var b in GmcpWire("Café.Message " + Json))
        {
            await server.InterpretByteArrayAsync(new[] { b });
        }
        await server.WaitForProcessingAsync();

        await Assert.That(await PollUntilAsync(() => received.Count > 0)).IsTrue();
        await Assert.That(received[0].Package).IsEqualTo("Café.Message");
        await Assert.That(received[0].Info).IsEqualTo(Json);
    }

    /// <summary>
    /// MSDP over GMCP is GMCP, so it is UTF-8 too, both on the way in to the MSDP callback and on
    /// the way out.
    /// </summary>
    [Test]
    public async Task MsdpOverGmcpIsUtf8UnderLatin1()
    {
        var output = new List<byte>();
        string msdp = null;
        await using var server = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server).UseLogger(logger).OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(bytes => { output.AddRange(bytes.ToArray()); return default; })
            .AddPlugin<CharsetProtocol>()
            .AddPlugin<MSDPProtocol>().OnMSDPMessage((_, json) => { msdp = json; return default; })
            .AddPlugin<GMCPProtocol>());
        await NegotiateAsync(server, "iso-8859-1", output);

        await InterpretAndWaitAsync(server, GmcpWire("MSDP " + Json));
        await Assert.That(await PollUntilAsync(() => msdp != null)).IsTrue();
        await Assert.That(msdp).IsEqualTo(Json);

        await server.SendGMCPCommand("MSDP", Json);
        await AssertByteArraysEqual(output.ToArray(), GmcpWire("MSDP " + Json));
    }

    /// <summary>The Core session attached by UseGmcpServerSession reads the same UTF-8.</summary>
    [Test]
    public async Task TheCoreSessionReadsUtf8UnderLatin1()
    {
        var output = new List<byte>();
        await using var server = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server).UseLogger(logger).OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(bytes => { output.AddRange(bytes.ToArray()); return default; })
            .AddPlugin<CharsetProtocol>()
            .AddPlugin<GMCPProtocol>().UseGmcpServerSession(out var gmcp));
        await NegotiateAsync(server, "iso-8859-1", output);

        await InterpretAndWaitAsync(server, GmcpWire("""Core.Hello {"client":"Mûdlet雪","version":"1.0"}"""));

        await Assert.That(await PollUntilAsync(() => gmcp.ClientName != null)).IsTrue();
        await Assert.That(gmcp.ClientName).IsEqualTo("Mûdlet雪");
    }

    /// <summary>
    /// Only GMCP is held to UTF-8: ordinary text still arrives under the negotiated charset, and a
    /// bodyless message keeps its frame with no separator.
    /// </summary>
    [Test]
    public async Task TextKeepsTheNegotiatedCharsetAndBodylessGmcpKeepsItsFrame()
    {
        var output = new List<byte>();
        var lines = new List<(byte[] Bytes, Encoding Encoding)>();
        await using var server = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server).UseLogger(logger)
            .OnSubmit((bytes, encoding, _) => { lines.Add((bytes, encoding)); return default; })
            .OnNegotiation(bytes => { output.AddRange(bytes.ToArray()); return default; })
            .AddPlugin<CharsetProtocol>()
            .AddPlugin<GMCPProtocol>());
        await NegotiateAsync(server, "iso-8859-1", output);

        var latin1 = Encoding.GetEncoding("iso-8859-1");
        await InterpretAndWaitAsync(server, [.. latin1.GetBytes("café"), (byte)'\r', (byte)'\n']);
        await Assert.That(await PollUntilAsync(() => lines.Count > 0)).IsTrue();
        await Assert.That(lines[0].Encoding.WebName).IsEqualTo("iso-8859-1");
        await Assert.That(lines[0].Encoding.GetString(lines[0].Bytes)).IsEqualTo("café");

        await server.SendGMCPCommand("Core.Ping", "");
        await AssertByteArraysEqual(output.ToArray(), GmcpWire("Core.Ping"));
    }
}
