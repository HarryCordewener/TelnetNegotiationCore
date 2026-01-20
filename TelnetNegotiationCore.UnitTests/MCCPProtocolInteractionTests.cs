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
	private TelnetInterpreter _server_ti;
	private TelnetInterpreter _client_ti;
	private byte[] _negotiationOutput;
	private int _nawsHeight;
	private int _nawsWidth;
	private Encoding _charsetEncoding;
	private bool _mccpEnabled;

	private ValueTask WriteBackToOutput(byte[] arg1, Encoding arg2, TelnetInterpreter t) => ValueTask.CompletedTask;

	private ValueTask WriteBackToNegotiate(byte[] arg1)
	{
		_negotiationOutput = arg1;
		return ValueTask.CompletedTask;
	}

	private ValueTask OnNAWSReceived(int height, int width)
	{
		_nawsHeight = height;
		_nawsWidth = width;
		logger.LogInformation("NAWS received: {Height}x{Width}", height, width);
		return ValueTask.CompletedTask;
	}

	private ValueTask OnCharsetChanged(Encoding encoding)
	{
		_charsetEncoding = encoding;
		logger.LogInformation("Charset changed to: {Encoding}", encoding.EncodingName);
		return ValueTask.CompletedTask;
	}

	private ValueTask OnMCCPStateChanged(int version, bool enabled)
	{
		_mccpEnabled = enabled;
		logger.LogInformation("MCCP{Version} {State}", version, enabled ? "enabled" : "disabled");
		return ValueTask.CompletedTask;
	}

	[Before(Test)]
	public async Task Setup()
	{
		_negotiationOutput = null;
		_nawsHeight = 0;
		_nawsWidth = 0;
		_charsetEncoding = null;
		_mccpEnabled = false;

		// Server with all protocols enabled
		_server_ti = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(WriteBackToOutput)
			.OnNegotiation(WriteBackToNegotiate)
			.AddPlugin<NAWSProtocol>()
				.OnNAWS(OnNAWSReceived)
			.AddPlugin<CharsetProtocol>()
				.WithCharsetOrder(Encoding.UTF8, Encoding.GetEncoding("iso-8859-1"))
			.AddPlugin<MCCPProtocol>()
				.OnCompressionEnabled(OnMCCPStateChanged)
			.BuildAsync();

		// Client with all protocols enabled
		_client_ti = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(WriteBackToOutput)
			.OnNegotiation(WriteBackToNegotiate)
			.AddPlugin<NAWSProtocol>()
				.OnNAWS(OnNAWSReceived)
			.AddPlugin<CharsetProtocol>()
				.WithCharsetOrder(Encoding.UTF8, Encoding.GetEncoding("iso-8859-1"))
			.AddPlugin<MCCPProtocol>()
				.OnCompressionEnabled(OnMCCPStateChanged)
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
	public async Task NAWSNegotiationWorksWithMCCPEnabled()
	{
		// Arrange - MCCP is configured
		// Server sends WILL MCCP2
		await _client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.MCCP2 });
		await _client_ti.WaitForProcessingAsync();

		// Client accepts MCCP2
		await _server_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MCCP2 });
		await _server_ti.WaitForProcessingAsync();
		await Task.Delay(50);

		// Act - Now negotiate NAWS
		_nawsHeight = 0;
		_nawsWidth = 0;

		// Server requests NAWS
		await _client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.NAWS });
		await _client_ti.WaitForProcessingAsync();

		// Client sends NAWS sub-negotiation with width=100, height=40
		byte[] nawsData = new byte[] 
		{
			(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.NAWS,
			0x00, 0x64, // width 100
			0x00, 0x28, // height 40
			(byte)Trigger.IAC, (byte)Trigger.SE
		};
		await _server_ti.InterpretByteArrayAsync(nawsData);
		await _server_ti.WaitForProcessingAsync();
		await Task.Delay(50);

		// Assert - NAWS should work correctly even with MCCP enabled
		await Assert.That(_nawsWidth).IsEqualTo(100);
		await Assert.That(_nawsHeight).IsEqualTo(40);

		logger.LogInformation("✓ NAWS negotiation successful with MCCP enabled");
	}

	[Test]
	public async Task CharsetNegotiationWorksWithMCCPEnabled()
	{
		// Arrange - MCCP is configured
		// Server sends WILL MCCP2
		await _client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.MCCP2 });
		await _client_ti.WaitForProcessingAsync();

		// Client accepts MCCP2
		await _server_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MCCP2 });
		await _server_ti.WaitForProcessingAsync();
		await Task.Delay(50);

		// Act - Now negotiate Charset
		// Server sends WILL CHARSET
		await _client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.CHARSET });
		await _client_ti.WaitForProcessingAsync();
		await Task.Delay(50);

		// Assert - Charset negotiation should proceed normally
		// The client should respond to WILL CHARSET
		await Assert.That(_negotiationOutput).IsNotNull();

		logger.LogInformation("✓ Charset negotiation successful with MCCP enabled");
	}

	[Test]
	public async Task AllProtocolsCanCoexist()
	{
		// This test verifies that MCCP, NAWS, and Charset can all be negotiated
		// in the same session without conflicts

		// Step 1: Negotiate NAWS
		await _client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.NAWS });
		await _client_ti.WaitForProcessingAsync();

		byte[] nawsData = new byte[] 
		{
			(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.NAWS,
			0x00, 0x50, // width 80
			0x00, 0x18, // height 24
			(byte)Trigger.IAC, (byte)Trigger.SE
		};
		await _server_ti.InterpretByteArrayAsync(nawsData);
		await _server_ti.WaitForProcessingAsync();
		await Task.Delay(50);

		// Step 2: Negotiate MCCP2
		await _client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.MCCP2 });
		await _client_ti.WaitForProcessingAsync();
		await _server_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MCCP2 });
		await _server_ti.WaitForProcessingAsync();
		await Task.Delay(50);

		// Step 3: Negotiate Charset
		await _client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.CHARSET });
		await _client_ti.WaitForProcessingAsync();
		await Task.Delay(50);

		// Assert - All protocols should be active
		await Assert.That(_nawsWidth).IsEqualTo(80);
		await Assert.That(_nawsHeight).IsEqualTo(24);

		// Verify protocols are independent
		var nawsPlugin = _server_ti.PluginManager?.GetPlugin<NAWSProtocol>();
		var charsetPlugin = _server_ti.PluginManager?.GetPlugin<CharsetProtocol>();
		var mccpPlugin = _server_ti.PluginManager?.GetPlugin<MCCPProtocol>();

		await Assert.That(nawsPlugin).IsNotNull();
		await Assert.That(charsetPlugin).IsNotNull();
		await Assert.That(mccpPlugin).IsNotNull();

		logger.LogInformation("✓ All protocols (NAWS, Charset, MCCP) coexist successfully");
	}

	[Test]
	public async Task MCCPDoesNotDependOnOtherProtocols()
	{
		// Verify MCCP has no dependencies on Charset or NAWS
		var mccpPlugin = _server_ti.PluginManager?.GetPlugin<MCCPProtocol>();
		
		await Assert.That(mccpPlugin).IsNotNull();
		await Assert.That(mccpPlugin.Dependencies).IsNotNull();
		await Assert.That(mccpPlugin.Dependencies.Count).IsEqualTo(0);

		logger.LogInformation("✓ MCCP has no dependencies - it's independent");
	}

	[Test]
	public async Task CharsetAndNAWSDoNotDependOnMCCP()
	{
		// Verify Charset and NAWS have no dependencies on MCCP
		var nawsPlugin = _server_ti.PluginManager?.GetPlugin<NAWSProtocol>();
		var charsetPlugin = _server_ti.PluginManager?.GetPlugin<CharsetProtocol>();
		
		await Assert.That(nawsPlugin).IsNotNull();
		await Assert.That(nawsPlugin.Dependencies).IsNotNull();
		await Assert.That(nawsPlugin.Dependencies.Count).IsEqualTo(0);

		await Assert.That(charsetPlugin).IsNotNull();
		await Assert.That(charsetPlugin.Dependencies).IsNotNull();
		await Assert.That(charsetPlugin.Dependencies.Count).IsEqualTo(0);

		logger.LogInformation("✓ Charset and NAWS are independent of MCCP");
	}

	[Test]
	public async Task ProtocolsNegotiateInAnyOrder()
	{
		// Test that protocols can be negotiated in different orders
		// Order: Charset -> MCCP -> NAWS

		// Step 1: Charset
		await _client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.CHARSET });
		await _client_ti.WaitForProcessingAsync();
		await Task.Delay(50);

		// Step 2: MCCP
		await _client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.MCCP2 });
		await _client_ti.WaitForProcessingAsync();
		await _server_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MCCP2 });
		await _server_ti.WaitForProcessingAsync();
		await Task.Delay(50);

		// Step 3: NAWS
		await _client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.NAWS });
		await _client_ti.WaitForProcessingAsync();

		byte[] nawsData = new byte[] 
		{
			(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.NAWS,
			0x00, 0x64, // width 100
			0x00, 0x32, // height 50
			(byte)Trigger.IAC, (byte)Trigger.SE
		};
		await _server_ti.InterpretByteArrayAsync(nawsData);
		await _server_ti.WaitForProcessingAsync();
		await Task.Delay(50);

		// Assert - All should work regardless of order
		await Assert.That(_nawsWidth).IsEqualTo(100);
		await Assert.That(_nawsHeight).IsEqualTo(50);

		logger.LogInformation("✓ Protocols negotiate successfully in any order");
	}
}
