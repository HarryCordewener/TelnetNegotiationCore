using Microsoft.Extensions.Logging;
using TUnit.Core;
using System;
using System.Text;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Protocols;

namespace TelnetNegotiationCore.UnitTests;

public class AuthenticationTests : BaseTest
{
    private TelnetInterpreter _server_ti;
    private TelnetInterpreter _client_ti;
    private byte[] _negotiationOutput;

    private ValueTask WriteBackToOutput(byte[] arg1, Encoding arg2, TelnetInterpreter t) => ValueTask.CompletedTask;

    private ValueTask WriteBackToNegotiate(byte[] arg1)
    {
        _negotiationOutput = arg1;
        return ValueTask.CompletedTask;
    }

    [Before(Test)]
    public async Task Setup()
    {
        _negotiationOutput = null;

        _server_ti = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(WriteBackToOutput)
            .OnNegotiation(WriteBackToNegotiate)
            .AddPlugin<AuthenticationProtocol>()
            .BuildAsync();

        _client_ti = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit(WriteBackToOutput)
            .OnNegotiation(WriteBackToNegotiate)
            .AddPlugin<AuthenticationProtocol>()
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
    public async Task ServerSendsDoAuthentication()
    {
        // Arrange
        _negotiationOutput = null;

        // Act - Server initialization should send DO AUTHENTICATION
        await Task.Delay(100); // Allow initialization to complete

        // Assert - Server should send DO AUTHENTICATION
        await Assert.That(_negotiationOutput).IsNotNull();
        await Assert.That(_negotiationOutput).IsEquivalentTo(new byte[] 
        { 
            (byte)Trigger.IAC, 
            (byte)Trigger.DO, 
            (byte)Trigger.AUTHENTICATION 
        });
    }

    [Test]
    public async Task ClientRespondsWithWillToServerDo()
    {
        // Arrange
        _negotiationOutput = null;

        // Act - Client receives DO AUTHENTICATION from server
        await _client_ti.InterpretByteArrayAsync(new byte[] 
        { 
            (byte)Trigger.IAC, 
            (byte)Trigger.DO, 
            (byte)Trigger.AUTHENTICATION 
        });
        await _client_ti.WaitForProcessingAsync();

        // Assert - Client should respond with WILL AUTHENTICATION
        await Assert.That(_negotiationOutput).IsNotNull();
        await Assert.That(_negotiationOutput).IsEquivalentTo(new byte[] 
        { 
            (byte)Trigger.IAC, 
            (byte)Trigger.WILL, 
            (byte)Trigger.AUTHENTICATION 
        });
    }

    [Test]
    public async Task ServerSendsSendSubnegotiationAfterClientWill()
    {
        // Arrange
        _negotiationOutput = null;

        // Act - Server receives WILL AUTHENTICATION from client
        await _server_ti.InterpretByteArrayAsync(new byte[] 
        { 
            (byte)Trigger.IAC, 
            (byte)Trigger.WILL, 
            (byte)Trigger.AUTHENTICATION 
        });
        await _server_ti.WaitForProcessingAsync();

        // Assert - Server should send SEND subnegotiation (empty list)
        await Assert.That(_negotiationOutput).IsNotNull();
        await Assert.That(_negotiationOutput).IsEquivalentTo(new byte[]
        {
            (byte)Trigger.IAC,
            (byte)Trigger.SB,
            (byte)Trigger.AUTHENTICATION,
            1, // SEND command
            (byte)Trigger.IAC,
            (byte)Trigger.SE
        });
    }

    [Test]
    public async Task ClientRespondsWithIsNullToServerSend()
    {
        // Arrange
        _negotiationOutput = null;

        // First establish WILL/DO
        await _client_ti.InterpretByteArrayAsync(new byte[] 
        { 
            (byte)Trigger.IAC, 
            (byte)Trigger.DO, 
            (byte)Trigger.AUTHENTICATION 
        });
        await _client_ti.WaitForProcessingAsync();
        
        _negotiationOutput = null;

        // Act - Client receives SEND subnegotiation
        await _client_ti.InterpretByteArrayAsync(new byte[]
        {
            (byte)Trigger.IAC,
            (byte)Trigger.SB,
            (byte)Trigger.AUTHENTICATION,
            1, // SEND command
            (byte)Trigger.IAC,
            (byte)Trigger.SE
        });
        await _client_ti.WaitForProcessingAsync();

        // Assert - Client should respond with IS NULL
        await Assert.That(_negotiationOutput).IsNotNull();
        await Assert.That(_negotiationOutput.Length).IsGreaterThanOrEqualTo(7);
        await Assert.That(_negotiationOutput[0]).IsEqualTo((byte)Trigger.IAC);
        await Assert.That(_negotiationOutput[1]).IsEqualTo((byte)Trigger.SB);
        await Assert.That(_negotiationOutput[2]).IsEqualTo((byte)Trigger.AUTHENTICATION);
        await Assert.That(_negotiationOutput[3]).IsEqualTo((byte)0); // IS command
        await Assert.That(_negotiationOutput[4]).IsEqualTo((byte)0); // NULL type
    }

    [Test]
    public async Task ServerRejectsClientWontAuthentication()
    {
        // Arrange
        _negotiationOutput = null;

        // Act - Server receives WONT AUTHENTICATION from client
        await _server_ti.InterpretByteArrayAsync(new byte[] 
        { 
            (byte)Trigger.IAC, 
            (byte)Trigger.WONT, 
            (byte)Trigger.AUTHENTICATION 
        });
        await _server_ti.WaitForProcessingAsync();

        // Assert - Server should accept WONT without additional response
        // The protocol logs but doesn't send a response for WONT
        // Verify that no negotiation output was sent (null or empty)
        var isNullOrEmpty = _negotiationOutput == null || _negotiationOutput.Length == 0;
        await Assert.That(isNullOrEmpty).IsTrue();
    }

    [Test]
    public async Task ClientRejectsServerDontAuthentication()
    {
        // Arrange
        _negotiationOutput = null;

        // Act - Client receives DONT AUTHENTICATION from server
        await _client_ti.InterpretByteArrayAsync(new byte[] 
        { 
            (byte)Trigger.IAC, 
            (byte)Trigger.DONT, 
            (byte)Trigger.AUTHENTICATION 
        });
        await _client_ti.WaitForProcessingAsync();

        // Assert - Client should accept DONT without additional response
        // Verify that no negotiation output was sent (null or empty)
        var isNullOrEmpty = _negotiationOutput == null || _negotiationOutput.Length == 0;
        await Assert.That(isNullOrEmpty).IsTrue();
    }

    [Test]
    public async Task FullNegotiationSequence()
    {
        // This test simulates a complete authentication negotiation that results in rejection
        
        // Step 1: Server sends DO AUTHENTICATION
        await _server_ti.WaitForProcessingAsync();
        var serverMessage = _negotiationOutput;
        await Assert.That(serverMessage).IsNotNull();
        
        _negotiationOutput = null;

        // Step 2: Client receives DO and responds with WILL
        await _client_ti.InterpretByteArrayAsync(serverMessage);
        await _client_ti.WaitForProcessingAsync();
        var clientWill = _negotiationOutput;
        await Assert.That(clientWill).IsNotNull();
        
        _negotiationOutput = null;

        // Step 3: Server receives WILL and sends SEND
        await _server_ti.InterpretByteArrayAsync(clientWill);
        await _server_ti.WaitForProcessingAsync();
        var serverSend = _negotiationOutput;
        await Assert.That(serverSend).IsNotNull();
        
        _negotiationOutput = null;

        // Step 4: Client receives SEND and responds with IS NULL
        await _client_ti.InterpretByteArrayAsync(serverSend);
        await _client_ti.WaitForProcessingAsync();
        var clientIsNull = _negotiationOutput;
        await Assert.That(clientIsNull).IsNotNull();

        // Verify the IS NULL response format
        await Assert.That(clientIsNull[0]).IsEqualTo((byte)Trigger.IAC);
        await Assert.That(clientIsNull[1]).IsEqualTo((byte)Trigger.SB);
        await Assert.That(clientIsNull[2]).IsEqualTo((byte)Trigger.AUTHENTICATION);
        await Assert.That(clientIsNull[3]).IsEqualTo((byte)0); // IS
        await Assert.That(clientIsNull[4]).IsEqualTo((byte)0); // NULL type
    }

    [Test]
    public async Task ServerCanProvideCustomAuthenticationTypes()
    {
        // Arrange - Server with custom auth types
        var authTypesCalled = false;
        await _server_ti.DisposeAsync();
        _server_ti = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(WriteBackToOutput)
            .OnNegotiation(WriteBackToNegotiate)
            .AddPlugin<AuthenticationProtocol>()
                .WithAuthenticationTypes(async () =>
                {
                    authTypesCalled = true;
                    return new System.Collections.Generic.List<(byte, byte)>
                    {
                        (5, 0), // SRP with no modifiers
                        (6, 2)  // RSA with AUTH_HOW_MUTUAL
                    };
                })
            .BuildAsync();

        _negotiationOutput = null;

        // Act - Client sends WILL
        await _server_ti.InterpretByteArrayAsync(new byte[] 
        { 
            (byte)Trigger.IAC, 
            (byte)Trigger.WILL, 
            (byte)Trigger.AUTHENTICATION 
        });
        await _server_ti.WaitForProcessingAsync();

        // Assert - Server should send SEND with auth types
        await Assert.That(authTypesCalled).IsTrue();
        await Assert.That(_negotiationOutput).IsNotNull();
        await Assert.That(_negotiationOutput[0]).IsEqualTo((byte)Trigger.IAC);
        await Assert.That(_negotiationOutput[1]).IsEqualTo((byte)Trigger.SB);
        await Assert.That(_negotiationOutput[2]).IsEqualTo((byte)Trigger.AUTHENTICATION);
        await Assert.That(_negotiationOutput[3]).IsEqualTo((byte)1); // SEND
        await Assert.That(_negotiationOutput[4]).IsEqualTo((byte)5); // SRP
        await Assert.That(_negotiationOutput[5]).IsEqualTo((byte)0); // No modifiers
        await Assert.That(_negotiationOutput[6]).IsEqualTo((byte)6); // RSA
        await Assert.That(_negotiationOutput[7]).IsEqualTo((byte)2); // AUTH_HOW_MUTUAL
    }

    [Test]
    public async Task ClientCanProvideCustomAuthenticationResponse()
    {
        // Arrange - Client with custom auth response handler
        byte[] receivedRequest = null;
        await _client_ti.DisposeAsync();
        _client_ti = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit(WriteBackToOutput)
            .OnNegotiation(WriteBackToNegotiate)
            .AddPlugin<AuthenticationProtocol>()
                .OnAuthenticationRequest(async (authTypePairs) =>
                {
                    receivedRequest = authTypePairs;
                    // Return a custom auth response (e.g., SRP with some data)
                    return new byte[] { 5, 0, 0x01, 0x02, 0x03 }; // SRP, no modifiers, some data
                })
            .BuildAsync();

        // Establish WILL/DO first
        await _client_ti.InterpretByteArrayAsync(new byte[] 
        { 
            (byte)Trigger.IAC, 
            (byte)Trigger.DO, 
            (byte)Trigger.AUTHENTICATION 
        });
        await _client_ti.WaitForProcessingAsync();
        
        _negotiationOutput = null;

        // Act - Client receives SEND with auth types
        await _client_ti.InterpretByteArrayAsync(new byte[]
        {
            (byte)Trigger.IAC,
            (byte)Trigger.SB,
            (byte)Trigger.AUTHENTICATION,
            1, // SEND
            5, 0, // SRP, no modifiers
            6, 2, // RSA, AUTH_HOW_MUTUAL
            (byte)Trigger.IAC,
            (byte)Trigger.SE
        });
        await _client_ti.WaitForProcessingAsync();

        // Assert - Client should send custom IS response
        await Assert.That(receivedRequest).IsNotNull();
        await Assert.That(receivedRequest.Length).IsEqualTo(4); // Two auth type pairs
        await Assert.That(_negotiationOutput).IsNotNull();
        await Assert.That(_negotiationOutput[0]).IsEqualTo((byte)Trigger.IAC);
        await Assert.That(_negotiationOutput[1]).IsEqualTo((byte)Trigger.SB);
        await Assert.That(_negotiationOutput[2]).IsEqualTo((byte)Trigger.AUTHENTICATION);
        await Assert.That(_negotiationOutput[3]).IsEqualTo((byte)0); // IS
        await Assert.That(_negotiationOutput[4]).IsEqualTo((byte)5); // SRP
        await Assert.That(_negotiationOutput[5]).IsEqualTo((byte)0); // No modifiers
        await Assert.That(_negotiationOutput[6]).IsEqualTo((byte)0x01); // Custom data
        await Assert.That(_negotiationOutput[7]).IsEqualTo((byte)0x02);
        await Assert.That(_negotiationOutput[8]).IsEqualTo((byte)0x03);
    }

    [Test]
    public async Task ServerCanReceiveAndProcessAuthenticationResponse()
    {
        // Arrange - Server with auth response handler
        byte[] receivedAuthData = null;
        await _server_ti.DisposeAsync();
        _server_ti = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(WriteBackToOutput)
            .OnNegotiation(WriteBackToNegotiate)
            .AddPlugin<AuthenticationProtocol>()
                .OnAuthenticationResponse(async (authData) =>
                {
                    receivedAuthData = authData;
                    await ValueTask.CompletedTask;
                })
            .BuildAsync();

        // Establish DO/WILL first
        await _server_ti.InterpretByteArrayAsync(new byte[] 
        { 
            (byte)Trigger.IAC, 
            (byte)Trigger.WILL, 
            (byte)Trigger.AUTHENTICATION 
        });
        await _server_ti.WaitForProcessingAsync();

        // Act - Server receives IS with auth data from client
        await _server_ti.InterpretByteArrayAsync(new byte[]
        {
            (byte)Trigger.IAC,
            (byte)Trigger.SB,
            (byte)Trigger.AUTHENTICATION,
            0, // IS
            5, 0, // SRP, no modifiers
            0x01, 0x02, 0x03, // Some auth data
            (byte)Trigger.IAC,
            (byte)Trigger.SE
        });
        await _server_ti.WaitForProcessingAsync();

        // Assert - Server should have received the auth data
        await Assert.That(receivedAuthData).IsNotNull();
        await Assert.That(receivedAuthData.Length).IsEqualTo(5); // Auth type, modifiers, and 3 bytes of data
        await Assert.That(receivedAuthData[0]).IsEqualTo((byte)5); // SRP
        await Assert.That(receivedAuthData[1]).IsEqualTo((byte)0); // No modifiers
        await Assert.That(receivedAuthData[2]).IsEqualTo((byte)0x01);
        await Assert.That(receivedAuthData[3]).IsEqualTo((byte)0x02);
        await Assert.That(receivedAuthData[4]).IsEqualTo((byte)0x03);
    }
}
