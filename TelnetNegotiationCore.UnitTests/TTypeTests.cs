using TUnit.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Protocols;

namespace TelnetNegotiationCore.UnitTests;

public class TTypeTests : BaseTest
{
	[Test]
	[MethodDataSource(nameof(ServerTTypeSequences))]
	public async Task ServerEvaluationCheck(IEnumerable<byte[]> clientSends, IEnumerable<byte[]> serverShouldRespondWith, IEnumerable<string[]> RegisteredTTypes)
	{
		byte[] negotiationOutput = null;
		
		ValueTask CaptureNegotiation(byte[] data)
		{
			negotiationOutput = data;
			return ValueTask.CompletedTask;
		}
		
		var server_ti = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit((data, enc, ti) => throw new NotImplementedException())
			.OnNegotiation(CaptureNegotiation)
			.AddPlugin<TerminalTypeProtocol>()
			.BuildAsync();

		if (clientSends.Count() != serverShouldRespondWith.Count())
			throw new Exception("Invalid Testcase.");

		foreach (var (clientSend, serverShouldRespond, shouldHaveTTypeList) in clientSends.Zip(serverShouldRespondWith, RegisteredTTypes))
		{
			negotiationOutput = null;
			foreach (var x in clientSend ?? Enumerable.Empty<byte>())
			{
				await server_ti.InterpretAsync(x);
			}
			await server_ti.WaitForProcessingAsync();
			await Assert.That(server_ti.TerminalTypes).IsEquivalentTo(shouldHaveTTypeList ?? Enumerable.Empty<string>());
			
			if (serverShouldRespond == null)
			{
				await Assert.That(negotiationOutput).IsNull();
			}
			else
			{
				await Assert.That(negotiationOutput).IsEquivalentTo(serverShouldRespond);
			}
		}
		
		await server_ti.DisposeAsync();
	}

	[Test]
	[MethodDataSource(nameof(ClientTTypeSequences))]
	public async Task ClientEvaluationCheck(IEnumerable<byte[]> serverSends, IEnumerable<byte[]> serverShouldRespondWith)
	{
		byte[] negotiationOutput = null;
		
		ValueTask CaptureNegotiation(byte[] data)
		{
			negotiationOutput = data;
			return ValueTask.CompletedTask;
		}
		
		var client_ti = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit((data, enc, ti) => throw new NotImplementedException())
			.OnNegotiation(CaptureNegotiation)
			.AddPlugin<TerminalTypeProtocol>()
			.BuildAsync();

		if (serverSends.Count() != serverShouldRespondWith.Count())
			throw new Exception("Invalid Testcase.");

		foreach (var (serverSend, clientShouldRespond) in serverSends.Zip(serverShouldRespondWith))
		{
			negotiationOutput = null;
			foreach (var x in serverSend ?? Enumerable.Empty<byte>())
			{
				await client_ti.InterpretAsync(x);
			}
			await client_ti.WaitForProcessingAsync();
			await Task.Delay(50);
			await Assert.That(negotiationOutput).IsEquivalentTo(clientShouldRespond);
		}
		
		await client_ti.DisposeAsync();
	}

	public static IEnumerable<(IEnumerable<byte[]>, IEnumerable<byte[]>)> ClientTTypeSequences()
	{
		yield return (
			new[]
			{
				new [] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.TTYPE }
			},
			new[]
			{
				new [] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.TTYPE }
			});
		yield return (
			new byte[][]
			{
				[(byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.TTYPE],
				[(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TTYPE, (byte)Trigger.SEND, (byte)Trigger.IAC, (byte)Trigger.SE],
				[(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TTYPE, (byte)Trigger.SEND, (byte)Trigger.IAC, (byte)Trigger.SE],
				[(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TTYPE, (byte)Trigger.SEND, (byte)Trigger.IAC, (byte)Trigger.SE],
				[(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TTYPE, (byte)Trigger.SEND, (byte)Trigger.IAC, (byte)Trigger.SE],
				[(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TTYPE, (byte)Trigger.SEND, (byte)Trigger.IAC, (byte)Trigger.SE]
			},
			new byte[][]
			{
				[(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.TTYPE],
				[(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TTYPE, (byte)Trigger.IS, (byte)'T', (byte)'N', (byte)'C', (byte)Trigger.IAC, (byte)Trigger.SE],
				[(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TTYPE, (byte)Trigger.IS, (byte)'X', (byte)'T', (byte)'E', (byte)'R', (byte)'M', (byte)Trigger.IAC, (byte)Trigger.SE],
				[(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TTYPE, (byte)Trigger.IS, (byte)'M', (byte)'T', (byte)'T', (byte)'S', (byte)' ', (byte)'3', (byte)'8', (byte)'5', (byte)'3', (byte)Trigger.IAC, (byte)Trigger.SE],
				[(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TTYPE, (byte)Trigger.IS, (byte)'M', (byte)'T', (byte)'T', (byte)'S', (byte)' ', (byte)'3', (byte)'8', (byte)'5', (byte)'3', (byte)Trigger.IAC, (byte)Trigger.SE],
				[(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TTYPE, (byte)Trigger.IS, (byte)'T', (byte)'N', (byte)'C', (byte)Trigger.IAC, (byte)Trigger.SE]
			});
	}

	public static IEnumerable<(IEnumerable<byte[]>, IEnumerable<byte[]>, IEnumerable<string[]>)> ServerTTypeSequences()
	{
		yield return (
			new[] { // Client Sends
				new[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.TTYPE },
			},
			new[] { // Server Should Respond With
				new[] { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TTYPE, (byte)Trigger.SEND, (byte)Trigger.IAC, (byte)Trigger.SE },
			},
			new[] // Registered TType List After Negotiation
			{
				Array.Empty<string>()
			});
		yield return (
			new byte[][] { // Client Sends
				[(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.TTYPE],
				[(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TTYPE, (byte)Trigger.IS, (byte)'A', (byte)'N', (byte)'S', (byte)'I', (byte)Trigger.IAC, (byte)Trigger.SE],
				[(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TTYPE, (byte)Trigger.IS, (byte)'V', (byte)'T', (byte)'1', (byte)'0', (byte)'0', (byte)Trigger.IAC, (byte)Trigger.SE],
				[(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TTYPE, (byte)Trigger.IS, (byte)'V', (byte)'T', (byte)'1', (byte)'0', (byte)'0', (byte)Trigger.IAC, (byte)Trigger.SE]
			},
			new byte[][] { // Server Should Respond With
				[(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TTYPE, (byte)Trigger.SEND, (byte)Trigger.IAC, (byte)Trigger.SE],
				[(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TTYPE, (byte)Trigger.SEND, (byte)Trigger.IAC, (byte)Trigger.SE],
				[(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TTYPE, (byte)Trigger.SEND, (byte)Trigger.IAC, (byte)Trigger.SE],
				null
			},
			new[] // Registered TType List After Negotiation
			{
				Array.Empty<string>(),
				["ANSI"],
				["ANSI", "VT100"],
				["ANSI", "VT100"]
			});
	}
}