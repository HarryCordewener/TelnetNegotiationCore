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


public class TTypeTests: BaseTest
{
	private TelnetInterpreter _server_ti;
	private TelnetInterpreter _client_ti;
	private byte[] _negotiationOutput;

	private ValueTask WriteBackToOutput(byte[] arg1, Encoding arg2, TelnetInterpreter t) => throw new NotImplementedException();

	private ValueTask WriteBackToNegotiate(byte[] arg1) { _negotiationOutput = arg1; return ValueTask.CompletedTask; }

	private ValueTask WriteBackToGMCP((string Package, string Info) tuple) => throw new NotImplementedException();

	[Before(Test)]
	public async Task Setup()
	{
		_server_ti = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(WriteBackToOutput)
			.OnNegotiation(WriteBackToNegotiate)
			.AddPlugin<TerminalTypeProtocol>()
			.BuildAsync();

		_client_ti = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(WriteBackToOutput)
			.OnNegotiation(WriteBackToNegotiate)
			.AddPlugin<TerminalTypeProtocol>()
			.BuildAsync();
	}

	[After(Test)]
	public async Task TearDown()
	{
		if (_server_ti != null)
			await _server_ti.DisposeAsync();
		if (_client_ti != null)
			await _client_ti.DisposeAsync();
	}

	[Test]
	[MethodDataSource(nameof(ServerTTypeSequences))]
	public async Task ServerEvaluationCheck(IEnumerable<byte[]> clientSends, IEnumerable<byte[]> serverShouldRespondWith, IEnumerable<string[]> RegisteredTTypes)
	{
		if (clientSends.Count() != serverShouldRespondWith.Count())
			throw new Exception("Invalid Testcase.");

		foreach (var (clientSend, serverShouldRespond, shouldHaveTTypeList) in clientSends.Zip(serverShouldRespondWith, RegisteredTTypes))
		{
			_negotiationOutput = null;
			foreach (var x in clientSend ?? Enumerable.Empty<byte>())
			{
				await _server_ti.InterpretAsync(x);
			}
			await _server_ti.WaitForProcessingAsync();
			await Assert.That(_server_ti.TerminalTypes).IsEquivalentTo(shouldHaveTTypeList ?? Enumerable.Empty<string>());
			await Assert.That(_negotiationOutput).IsEquivalentTo(serverShouldRespond);
		}
	}

	[Test]
	[MethodDataSource(nameof(ClientTTypeSequences))]
	public async Task ClientEvaluationCheck(IEnumerable<byte[]> serverSends, IEnumerable<byte[]> serverShouldRespondWith)
	{
		if (serverSends.Count() != serverShouldRespondWith.Count())
			throw new Exception("Invalid Testcase.");

		foreach (var (serverSend, clientShouldRespond) in serverSends.Zip(serverShouldRespondWith))
		{
			_negotiationOutput = null;
			foreach (var x in serverSend ?? Enumerable.Empty<byte>())
			{
				await _client_ti.InterpretAsync(x);
			}
			await _client_ti.WaitForProcessingAsync();
			await Task.Delay(50);
			await Assert.That(_negotiationOutput).IsEquivalentTo(clientShouldRespond);
		}
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