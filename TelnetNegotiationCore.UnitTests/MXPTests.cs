using Microsoft.Extensions.Logging;
using System;
using System.Text;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Protocols;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

public class MXPTests : BaseTest
{
	private static async Task<bool> PollUntilAsync(Func<bool> condition, int timeoutMs = 2000, int pollIntervalMs = 10)
	{
		var waitedMs = 0;
		while (!condition() && waitedMs < timeoutMs)
		{
			await Task.Delay(pollIntervalMs);
			waitedMs += pollIntervalMs;
		}
		return condition();
	}

	[Test]
	public async Task ServerAnnouncesMXPOnBuild()
	{
		// Arrange
		byte[] negotiationOutput = null;

		ValueTask WriteBackToNegotiate(ReadOnlyMemory<byte> arg1)
		{
			negotiationOutput = arg1.ToArray();
			return ValueTask.CompletedTask;
		}

		var server_ti = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(WriteBackToNegotiate)
			.AddPlugin<MXPProtocol>()
			.BuildAsync();

		// Assert - Server should announce WILL MXP during initialization
		await Assert.That(negotiationOutput).IsNotNull();
		await AssertByteArraysEqual(negotiationOutput, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.MXP });

		await server_ti.DisposeAsync();
	}

	[Test]
	public async Task ClientRespondsDoMXPToServerWill()
	{
		// Arrange
		byte[] negotiationOutput = null;

		ValueTask WriteBackToNegotiate(ReadOnlyMemory<byte> arg1)
		{
			negotiationOutput = arg1.ToArray();
			return ValueTask.CompletedTask;
		}

		var client_ti = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(WriteBackToNegotiate)
			.AddPlugin<MXPProtocol>()
			.BuildAsync();

		// Act - Server sends WILL MXP
		await client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.MXP });
		await client_ti.WaitForProcessingAsync();

		// Assert - Client should respond with DO MXP
		await Assert.That(negotiationOutput).IsNotNull();
		await AssertByteArraysEqual(negotiationOutput, new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MXP });

		await client_ti.DisposeAsync();
	}

	[Test]
	public async Task ServerMXPEnabledOnClientDo()
	{
		// Arrange
		byte[] negotiationOutput = null;
		bool mxpEnabledCallbackFired = false;

		ValueTask WriteBackToNegotiate(ReadOnlyMemory<byte> arg1)
		{
			negotiationOutput = arg1.ToArray();
			return ValueTask.CompletedTask;
		}

		var server_ti = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(WriteBackToNegotiate)
			.AddPlugin<MXPProtocol>()
				.OnMXPEnabled(() =>
				{
					mxpEnabledCallbackFired = true;
					return ValueTask.CompletedTask;
				})
			.BuildAsync();

		var mxpPlugin = server_ti.PluginManager!.GetPlugin<MXPProtocol>();

		// Act - Client sends DO MXP
		await server_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MXP });
		await server_ti.WaitForProcessingAsync();

		var gotCallback = await PollUntilAsync(() => mxpEnabledCallbackFired);

		// Assert
		await Assert.That(gotCallback).IsTrue();
		await Assert.That(mxpPlugin!.IsMXPActive).IsTrue();

		await server_ti.DisposeAsync();
	}

	[Test]
	public async Task ClientMXPEnabledOnServerWill()
	{
		// Arrange
		bool mxpEnabledCallbackFired = false;

		ValueTask WriteBackToNegotiate(ReadOnlyMemory<byte> arg1) => ValueTask.CompletedTask;

		var client_ti = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(WriteBackToNegotiate)
			.AddPlugin<MXPProtocol>()
				.OnMXPEnabled(() =>
				{
					mxpEnabledCallbackFired = true;
					return ValueTask.CompletedTask;
				})
			.BuildAsync();

		var mxpPlugin = client_ti.PluginManager!.GetPlugin<MXPProtocol>();

		// Act - Server sends WILL MXP
		await client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.MXP });
		await client_ti.WaitForProcessingAsync();

		var gotCallback = await PollUntilAsync(() => mxpEnabledCallbackFired);

		// Assert
		await Assert.That(gotCallback).IsTrue();
		await Assert.That(mxpPlugin!.IsMXPActive).IsTrue();

		await client_ti.DisposeAsync();
	}

	[Test]
	public async Task ServerMXPDisabledOnClientDont()
	{
		// Arrange
		ValueTask WriteBackToNegotiate(ReadOnlyMemory<byte> arg1) => ValueTask.CompletedTask;

		var server_ti = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(WriteBackToNegotiate)
			.AddPlugin<MXPProtocol>()
			.BuildAsync();

		var mxpPlugin = server_ti.PluginManager!.GetPlugin<MXPProtocol>();

		// Act - Client sends DONT MXP
		await server_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DONT, (byte)Trigger.MXP });
		await server_ti.WaitForProcessingAsync();
		await Task.Delay(100);

		// Assert
		await Assert.That(mxpPlugin!.IsMXPActive).IsFalse();

		await server_ti.DisposeAsync();
	}

	[Test]
	public async Task ClientMXPDisabledOnServerWont()
	{
		// Arrange
		ValueTask WriteBackToNegotiate(ReadOnlyMemory<byte> arg1) => ValueTask.CompletedTask;

		var client_ti = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(WriteBackToNegotiate)
			.AddPlugin<MXPProtocol>()
			.BuildAsync();

		var mxpPlugin = client_ti.PluginManager!.GetPlugin<MXPProtocol>();

		// Act - Server sends WONT MXP
		await client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WONT, (byte)Trigger.MXP });
		await client_ti.WaitForProcessingAsync();
		await Task.Delay(100);

		// Assert
		await Assert.That(mxpPlugin!.IsMXPActive).IsFalse();

		await client_ti.DisposeAsync();
	}
}
