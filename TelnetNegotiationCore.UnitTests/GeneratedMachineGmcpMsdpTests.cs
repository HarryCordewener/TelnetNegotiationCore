using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Protocols;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>GMCP and MSDP through the generated machine, streaming through the real protocol's own buffer.</summary>
public class GeneratedMachineGmcpMsdpTests : BaseTest
{
    [Test]
    public async Task ServerCanReceiveGMCPMessage()
    {
        (string Package, string Info)? receivedGMCP = null;
        ValueTask WriteBackToGMCP((string Package, string Info) tuple) { receivedGMCP = tuple; return ValueTask.CompletedTask; }

        var server = await new TelnetInterpreterBuilder()
            .UseGeneratedMachine()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit((a, e, t) => ValueTask.CompletedTask)
            .OnNegotiation(_ => ValueTask.CompletedTask)
            .AddPlugin<GMCPProtocol>()
                .OnGMCPMessage(WriteBackToGMCP)
            .BuildAsync();

        await server.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.GMCP });
        await server.WaitForProcessingAsync();

        var package = "Core.Hello";
        var message = "{\"client\":\"TestClient\",\"version\":\"1.0\"}";
        var gmcpBytes = new List<byte> { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.GMCP };
        gmcpBytes.AddRange(Encoding.ASCII.GetBytes(package));
        gmcpBytes.Add((byte)' ');
        gmcpBytes.AddRange(Encoding.ASCII.GetBytes(message));
        gmcpBytes.Add((byte)Trigger.IAC);
        gmcpBytes.Add((byte)Trigger.SE);

        await server.InterpretByteArrayAsync(gmcpBytes.ToArray());
        await server.WaitForProcessingAsync();

        await Assert.That(await PollUntilAsync(() => receivedGMCP != null)).IsTrue();
        await Assert.That(receivedGMCP!.Value.Package).IsEqualTo(package);
        await Assert.That(receivedGMCP.Value.Info).IsEqualTo(message);

        await server.DisposeAsync();
    }

    /// <summary>The same maximum-message-size enforcement the plugin always had, unaffected by which machine streams to it.</summary>
    [Test]
    public async Task AnOversizedGMCPMessageIsDroppedNotTruncated()
    {
        (string Package, long ReceivedBytes, int MaxMessageSize)? tooLarge = null;
        (string Package, string Info)? receivedGMCP = null;

        var server = await new TelnetInterpreterBuilder()
            .UseGeneratedMachine()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit((a, e, t) => ValueTask.CompletedTask)
            .OnNegotiation(_ => ValueTask.CompletedTask)
            .AddPlugin<GMCPProtocol>()
                .OnGMCPMessage(tuple => { receivedGMCP = tuple; return ValueTask.CompletedTask; })
                .OnGMCPMessageTooLarge(info => { tooLarge = info; return ValueTask.CompletedTask; })
                .WithMaxMessageSize(16)
            .BuildAsync();

        await server.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.GMCP });
        await server.WaitForProcessingAsync();

        var gmcpBytes = new List<byte> { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.GMCP };
        gmcpBytes.AddRange(Encoding.ASCII.GetBytes("Core.Hello a-payload-well-past-sixteen-bytes"));
        gmcpBytes.Add((byte)Trigger.IAC);
        gmcpBytes.Add((byte)Trigger.SE);

        await server.InterpretByteArrayAsync(gmcpBytes.ToArray());
        await server.WaitForProcessingAsync();

        await Assert.That(await PollUntilAsync(() => tooLarge != null)).IsTrue();
        await Assert.That(receivedGMCP).IsNull();

        await server.DisposeAsync();
    }

    [Test]
    public async Task ServerCanReceiveMSDPMessage()
    {
        string receivedJson = null;
        ValueTask OnMsdp(TelnetInterpreter t, string json) { receivedJson = json; return ValueTask.CompletedTask; }

        var server = await new TelnetInterpreterBuilder()
            .UseGeneratedMachine()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit((a, e, t) => ValueTask.CompletedTask)
            .OnNegotiation(_ => ValueTask.CompletedTask)
            .AddPlugin<MSDPProtocol>()
                .OnMSDPMessage(OnMsdp)
            .BuildAsync();

        await server.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MSDP });
        await server.WaitForProcessingAsync();

        var msdpBytes = new List<byte> { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.MSDP, (byte)Trigger.MSDP_VAR };
        msdpBytes.AddRange(Encoding.ASCII.GetBytes("NAME"));
        msdpBytes.Add((byte)Trigger.MSDP_VAL);
        msdpBytes.AddRange(Encoding.ASCII.GetBytes("Server"));
        msdpBytes.Add((byte)Trigger.IAC);
        msdpBytes.Add((byte)Trigger.SE);

        await server.InterpretByteArrayAsync(msdpBytes.ToArray());
        await server.WaitForProcessingAsync();

        await Assert.That(await PollUntilAsync(() => receivedJson != null)).IsTrue();
        await Assert.That(receivedJson).Contains("NAME");
        await Assert.That(receivedJson).Contains("Server");

        await server.DisposeAsync();
    }
}
