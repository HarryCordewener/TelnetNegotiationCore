using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Protocols;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

public class GmcpCharsetIndependenceTests : BaseTest
{
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
        await InterpretAndWaitAsync(server, [255, 253, 42]);
        await InterpretAndWaitAsync(server, [255, 250, 42, 2, .. Encoding.ASCII.GetBytes(name), 255, 240, 255, 253, 201]);
        output.Clear();
        const string json = "{\"name\":\"café雪\"}";
        byte[] wire = [255, 250, 201, .. Encoding.UTF8.GetBytes("Test.Message " + json), 255, 240];
        await server.SendGMCPCommand("Test.Message", json);
        await AssertByteArraysEqual(output.ToArray(), wire);
        await InterpretAndWaitAsync(server, wire);
        await Assert.That(received[0]).IsEqualTo(json);
    }
}
