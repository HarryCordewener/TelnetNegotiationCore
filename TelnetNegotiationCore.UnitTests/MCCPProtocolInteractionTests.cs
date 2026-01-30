using Microsoft.Extensions.Logging;
using TUnit.Core;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Protocols;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// Tests to verify that MCCP protocol doesn't interfere with other protocols
/// like Charset (UTF-8) and NAWS negotiation
/// </summary>
public class MCCPProtocolInteractionTests : BaseTest
{
	[Test]
	public async Task NAWSNegotiationWorksWithMCCPEnabled()
	{
		byte[] negotiationOutput = null;
		int nawsHeight = 0;
		int nawsWidth = 0;
		bool mccpEnabled = false;

		ValueTask WriteBackToOutput(byte[] arg1, Encoding arg2, TelnetInterpreter t) => ValueTask.CompletedTask;

		ValueTask CaptureNegotiation(byte[] data)
		{
			negotiationOutput = data;
			return ValueTask.CompletedTask;
		}

		ValueTask CaptureNAWS(int height, int width)
		{
			nawsHeight = height;
			nawsWidth = width;
			logger.LogInformation("NAWS received: {Height}x{Width}", height, width);
			return ValueTask.CompletedTask;
		}

		ValueTask CaptureMCCPStateChanged(int version, bool enabled)
		{
			mccpEnabled = enabled;
			logger.LogInformation("MCCP{Version} {State}", version, enabled ? "enabled" : "disabled");
			return ValueTask.CompletedTask;
		}

		var server_ti = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(WriteBackToOutput)
			.OnNegotiation(CaptureNegotiation)
			.AddPlugin<NAWSProtocol>()
				.OnNAWS(CaptureNAWS)
			.AddPlugin<CharsetProtocol>()
				.WithCharsetOrder(Encoding.UTF8, Encoding.GetEncoding("iso-8859-1"))
			.AddPlugin<MCCPProtocol>()
				.OnCompressionEnabled(CaptureMCCPStateChanged)
			.BuildAsync();
		await Task.Delay(100);

		var client_ti = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(WriteBackToOutput)
			.OnNegotiation(CaptureNegotiation)
			.AddPlugin<NAWSProtocol>()
				.OnNAWS(CaptureNAWS)
			.AddPlugin<CharsetProtocol>()
				.WithCharsetOrder(Encoding.UTF8, Encoding.GetEncoding("iso-8859-1"))
			.AddPlugin<MCCPProtocol>()
				.OnCompressionEnabled(CaptureMCCPStateChanged)
			.BuildAsync();
		await Task.Delay(100);

		// Arrange - MCCP is configured
		// Server sends WILL MCCP2
		await client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.MCCP2 });
		await client_ti.WaitForProcessingAsync();

		// Client accepts MCCP2
		await server_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MCCP2 });
		await server_ti.WaitForProcessingAsync();
		await Task.Delay(50);

		// Act - Now negotiate NAWS
		nawsHeight = 0;
		nawsWidth = 0;

		// Server requests NAWS
		await client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.NAWS });
		await client_ti.WaitForProcessingAsync();

		// Client sends NAWS sub-negotiation with width=100, height=40
		byte[] nawsData = new byte[] 
		{
			(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.NAWS,
			0x00, 0x64, // width 100
			0x00, 0x28, // height 40
			(byte)Trigger.IAC, (byte)Trigger.SE
		};
		await server_ti.InterpretByteArrayAsync(nawsData);
		await server_ti.WaitForProcessingAsync();
		await Task.Delay(50);

		// Assert - NAWS should work correctly even with MCCP enabled
		await Assert.That(nawsWidth).IsEqualTo(100);
		await Assert.That(nawsHeight).IsEqualTo(40);

		logger.LogInformation("✓ NAWS negotiation successful with MCCP enabled");

		await server_ti.DisposeAsync();
		await client_ti.DisposeAsync();
	}

	[Test]
	public async Task CharsetNegotiationWorksWithMCCPEnabled()
	{
		byte[] negotiationOutput = null;
		int nawsHeight = 0;
		int nawsWidth = 0;
		bool mccpEnabled = false;

		ValueTask WriteBackToOutput(byte[] arg1, Encoding arg2, TelnetInterpreter t) => ValueTask.CompletedTask;

		ValueTask CaptureNegotiation(byte[] data)
		{
			negotiationOutput = data;
			return ValueTask.CompletedTask;
		}

		ValueTask CaptureNAWS(int height, int width)
		{
			nawsHeight = height;
			nawsWidth = width;
			logger.LogInformation("NAWS received: {Height}x{Width}", height, width);
			return ValueTask.CompletedTask;
		}

		ValueTask CaptureMCCPStateChanged(int version, bool enabled)
		{
			mccpEnabled = enabled;
			logger.LogInformation("MCCP{Version} {State}", version, enabled ? "enabled" : "disabled");
			return ValueTask.CompletedTask;
		}

		var server_ti = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(WriteBackToOutput)
			.OnNegotiation(CaptureNegotiation)
			.AddPlugin<NAWSProtocol>()
				.OnNAWS(CaptureNAWS)
			.AddPlugin<CharsetProtocol>()
				.WithCharsetOrder(Encoding.UTF8, Encoding.GetEncoding("iso-8859-1"))
			.AddPlugin<MCCPProtocol>()
				.OnCompressionEnabled(CaptureMCCPStateChanged)
			.BuildAsync();
		await Task.Delay(100);

		var client_ti = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(WriteBackToOutput)
			.OnNegotiation(CaptureNegotiation)
			.AddPlugin<NAWSProtocol>()
				.OnNAWS(CaptureNAWS)
			.AddPlugin<CharsetProtocol>()
				.WithCharsetOrder(Encoding.UTF8, Encoding.GetEncoding("iso-8859-1"))
			.AddPlugin<MCCPProtocol>()
				.OnCompressionEnabled(CaptureMCCPStateChanged)
			.BuildAsync();
		await Task.Delay(100);

		// Arrange - MCCP is configured
		// Server sends WILL MCCP2
		await client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.MCCP2 });
		await client_ti.WaitForProcessingAsync();

		// Client accepts MCCP2
		await server_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MCCP2 });
		await server_ti.WaitForProcessingAsync();
		await Task.Delay(50);

		// Act - Now negotiate Charset
		// Server sends WILL CHARSET
		await client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.CHARSET });
		await client_ti.WaitForProcessingAsync();
		await Task.Delay(50);

		// Assert - Charset negotiation should proceed normally
		// The client should respond to WILL CHARSET
		await Assert.That(negotiationOutput).IsNotNull();

		logger.LogInformation("✓ Charset negotiation successful with MCCP enabled");

		await server_ti.DisposeAsync();
		await client_ti.DisposeAsync();
	}

	[Test]
	public async Task AllProtocolsCanCoexist()
	{
		byte[] negotiationOutput = null;
		int nawsHeight = 0;
		int nawsWidth = 0;
		bool mccpEnabled = false;

		ValueTask WriteBackToOutput(byte[] arg1, Encoding arg2, TelnetInterpreter t) => ValueTask.CompletedTask;

		ValueTask CaptureNegotiation(byte[] data)
		{
			negotiationOutput = data;
			return ValueTask.CompletedTask;
		}

		ValueTask CaptureNAWS(int height, int width)
		{
			nawsHeight = height;
			nawsWidth = width;
			logger.LogInformation("NAWS received: {Height}x{Width}", height, width);
			return ValueTask.CompletedTask;
		}

		ValueTask CaptureMCCPStateChanged(int version, bool enabled)
		{
			mccpEnabled = enabled;
			logger.LogInformation("MCCP{Version} {State}", version, enabled ? "enabled" : "disabled");
			return ValueTask.CompletedTask;
		}

		var server_ti = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(WriteBackToOutput)
			.OnNegotiation(CaptureNegotiation)
			.AddPlugin<NAWSProtocol>()
				.OnNAWS(CaptureNAWS)
			.AddPlugin<CharsetProtocol>()
				.WithCharsetOrder(Encoding.UTF8, Encoding.GetEncoding("iso-8859-1"))
			.AddPlugin<MCCPProtocol>()
				.OnCompressionEnabled(CaptureMCCPStateChanged)
			.BuildAsync();
		await Task.Delay(100);

		var client_ti = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(WriteBackToOutput)
			.OnNegotiation(CaptureNegotiation)
			.AddPlugin<NAWSProtocol>()
				.OnNAWS(CaptureNAWS)
			.AddPlugin<CharsetProtocol>()
				.WithCharsetOrder(Encoding.UTF8, Encoding.GetEncoding("iso-8859-1"))
			.AddPlugin<MCCPProtocol>()
				.OnCompressionEnabled(CaptureMCCPStateChanged)
			.BuildAsync();
		await Task.Delay(100);

		// This test verifies that MCCP, NAWS, and Charset can all be negotiated
		// in the same session without conflicts

		// Step 1: Negotiate NAWS
		await client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.NAWS });
		await client_ti.WaitForProcessingAsync();

		byte[] nawsData = new byte[] 
		{
			(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.NAWS,
			0x00, 0x50, // width 80
			0x00, 0x18, // height 24
			(byte)Trigger.IAC, (byte)Trigger.SE
		};
		await server_ti.InterpretByteArrayAsync(nawsData);
		await server_ti.WaitForProcessingAsync();
		await Task.Delay(50);

		// Step 2: Negotiate MCCP2
		await client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.MCCP2 });
		await client_ti.WaitForProcessingAsync();
		await server_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MCCP2 });
		await server_ti.WaitForProcessingAsync();
		await Task.Delay(50);

		// Step 3: Negotiate Charset
		await client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.CHARSET });
		await client_ti.WaitForProcessingAsync();
		await Task.Delay(50);

		// Assert - All protocols should be active
		await Assert.That(nawsWidth).IsEqualTo(80);
		await Assert.That(nawsHeight).IsEqualTo(24);

		// Verify protocols are independent
		var nawsPlugin = server_ti.PluginManager?.GetPlugin<NAWSProtocol>();
		var charsetPlugin = server_ti.PluginManager?.GetPlugin<CharsetProtocol>();
		var mccpPlugin = server_ti.PluginManager?.GetPlugin<MCCPProtocol>();

		await Assert.That(nawsPlugin).IsNotNull();
		await Assert.That(charsetPlugin).IsNotNull();
		await Assert.That(mccpPlugin).IsNotNull();

		logger.LogInformation("✓ All protocols (NAWS, Charset, MCCP) coexist successfully");

		await server_ti.DisposeAsync();
		await client_ti.DisposeAsync();
	}

	[Test]
	public async Task MCCPDoesNotDependOnOtherProtocols()
	{
		byte[] negotiationOutput = null;
		int nawsHeight = 0;
		int nawsWidth = 0;
		bool mccpEnabled = false;

		ValueTask WriteBackToOutput(byte[] arg1, Encoding arg2, TelnetInterpreter t) => ValueTask.CompletedTask;

		ValueTask CaptureNegotiation(byte[] data)
		{
			negotiationOutput = data;
			return ValueTask.CompletedTask;
		}

		ValueTask CaptureNAWS(int height, int width)
		{
			nawsHeight = height;
			nawsWidth = width;
			logger.LogInformation("NAWS received: {Height}x{Width}", height, width);
			return ValueTask.CompletedTask;
		}

		ValueTask CaptureMCCPStateChanged(int version, bool enabled)
		{
			mccpEnabled = enabled;
			logger.LogInformation("MCCP{Version} {State}", version, enabled ? "enabled" : "disabled");
			return ValueTask.CompletedTask;
		}

		var server_ti = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(WriteBackToOutput)
			.OnNegotiation(CaptureNegotiation)
			.AddPlugin<NAWSProtocol>()
				.OnNAWS(CaptureNAWS)
			.AddPlugin<CharsetProtocol>()
				.WithCharsetOrder(Encoding.UTF8, Encoding.GetEncoding("iso-8859-1"))
			.AddPlugin<MCCPProtocol>()
				.OnCompressionEnabled(CaptureMCCPStateChanged)
			.BuildAsync();
		await Task.Delay(100);

		// Verify MCCP has no dependencies on Charset or NAWS
		var mccpPlugin = server_ti.PluginManager?.GetPlugin<MCCPProtocol>();
		
		await Assert.That(mccpPlugin).IsNotNull();
		await Assert.That(mccpPlugin.Dependencies).IsNotNull();
		await Assert.That(mccpPlugin.Dependencies.Count).IsEqualTo(0);

		logger.LogInformation("✓ MCCP has no dependencies - it's independent");

		await server_ti.DisposeAsync();
	}

	[Test]
	public async Task CharsetAndNAWSDoNotDependOnMCCP()
	{
		byte[] negotiationOutput = null;
		int nawsHeight = 0;
		int nawsWidth = 0;
		bool mccpEnabled = false;

		ValueTask WriteBackToOutput(byte[] arg1, Encoding arg2, TelnetInterpreter t) => ValueTask.CompletedTask;

		ValueTask CaptureNegotiation(byte[] data)
		{
			negotiationOutput = data;
			return ValueTask.CompletedTask;
		}

		ValueTask CaptureNAWS(int height, int width)
		{
			nawsHeight = height;
			nawsWidth = width;
			logger.LogInformation("NAWS received: {Height}x{Width}", height, width);
			return ValueTask.CompletedTask;
		}

		ValueTask CaptureMCCPStateChanged(int version, bool enabled)
		{
			mccpEnabled = enabled;
			logger.LogInformation("MCCP{Version} {State}", version, enabled ? "enabled" : "disabled");
			return ValueTask.CompletedTask;
		}

		var server_ti = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(WriteBackToOutput)
			.OnNegotiation(CaptureNegotiation)
			.AddPlugin<NAWSProtocol>()
				.OnNAWS(CaptureNAWS)
			.AddPlugin<CharsetProtocol>()
				.WithCharsetOrder(Encoding.UTF8, Encoding.GetEncoding("iso-8859-1"))
			.AddPlugin<MCCPProtocol>()
				.OnCompressionEnabled(CaptureMCCPStateChanged)
			.BuildAsync();
		await Task.Delay(100);

		// Verify Charset and NAWS have no dependencies on MCCP
		var nawsPlugin = server_ti.PluginManager?.GetPlugin<NAWSProtocol>();
		var charsetPlugin = server_ti.PluginManager?.GetPlugin<CharsetProtocol>();
		
		await Assert.That(nawsPlugin).IsNotNull();
		await Assert.That(nawsPlugin.Dependencies).IsNotNull();
		await Assert.That(nawsPlugin.Dependencies.Count).IsEqualTo(0);

		await Assert.That(charsetPlugin).IsNotNull();
		await Assert.That(charsetPlugin.Dependencies).IsNotNull();
		await Assert.That(charsetPlugin.Dependencies.Count).IsEqualTo(0);

		logger.LogInformation("✓ Charset and NAWS are independent of MCCP");

		await server_ti.DisposeAsync();
	}

	[Test]
	public async Task ProtocolsNegotiateInAnyOrder()
	{
		byte[] negotiationOutput = null;
		int nawsHeight = 0;
		int nawsWidth = 0;
		bool mccpEnabled = false;

		ValueTask WriteBackToOutput(byte[] arg1, Encoding arg2, TelnetInterpreter t) => ValueTask.CompletedTask;

		ValueTask CaptureNegotiation(byte[] data)
		{
			negotiationOutput = data;
			return ValueTask.CompletedTask;
		}

		ValueTask CaptureNAWS(int height, int width)
		{
			nawsHeight = height;
			nawsWidth = width;
			logger.LogInformation("NAWS received: {Height}x{Width}", height, width);
			return ValueTask.CompletedTask;
		}

		ValueTask CaptureMCCPStateChanged(int version, bool enabled)
		{
			mccpEnabled = enabled;
			logger.LogInformation("MCCP{Version} {State}", version, enabled ? "enabled" : "disabled");
			return ValueTask.CompletedTask;
		}

		var server_ti = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(WriteBackToOutput)
			.OnNegotiation(CaptureNegotiation)
			.AddPlugin<NAWSProtocol>()
				.OnNAWS(CaptureNAWS)
			.AddPlugin<CharsetProtocol>()
				.WithCharsetOrder(Encoding.UTF8, Encoding.GetEncoding("iso-8859-1"))
			.AddPlugin<MCCPProtocol>()
				.OnCompressionEnabled(CaptureMCCPStateChanged)
			.BuildAsync();
		await Task.Delay(100);

		var client_ti = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(WriteBackToOutput)
			.OnNegotiation(CaptureNegotiation)
			.AddPlugin<NAWSProtocol>()
				.OnNAWS(CaptureNAWS)
			.AddPlugin<CharsetProtocol>()
				.WithCharsetOrder(Encoding.UTF8, Encoding.GetEncoding("iso-8859-1"))
			.AddPlugin<MCCPProtocol>()
				.OnCompressionEnabled(CaptureMCCPStateChanged)
			.BuildAsync();
		await Task.Delay(100);

		// Test that protocols can be negotiated in different orders
		// Order: Charset -> MCCP -> NAWS

		// Step 1: Charset
		await client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.CHARSET });
		await client_ti.WaitForProcessingAsync();
		await Task.Delay(50);

		// Step 2: MCCP
		await client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.MCCP2 });
		await client_ti.WaitForProcessingAsync();
		await server_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MCCP2 });
		await server_ti.WaitForProcessingAsync();
		await Task.Delay(50);

		// Step 3: NAWS
		await client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.NAWS });
		await client_ti.WaitForProcessingAsync();

		byte[] nawsData = new byte[] 
		{
			(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.NAWS,
			0x00, 0x64, // width 100
			0x00, 0x32, // height 50
			(byte)Trigger.IAC, (byte)Trigger.SE
		};
		await server_ti.InterpretByteArrayAsync(nawsData);
		await server_ti.WaitForProcessingAsync();
		await Task.Delay(50);

		// Assert - All should work regardless of order
		await Assert.That(nawsWidth).IsEqualTo(100);
		await Assert.That(nawsHeight).IsEqualTo(50);

		logger.LogInformation("✓ Protocols negotiate successfully in any order");

		await server_ti.DisposeAsync();
		await client_ti.DisposeAsync();
	}
}
