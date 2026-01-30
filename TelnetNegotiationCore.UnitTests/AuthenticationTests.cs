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
    [Test]
    public async Task ServerSendsDoAuthentication()
    {
        // Arrange - Create local variable for capturing output
        byte[] negotiationOutput = null;
        
        ValueTask CaptureNegotiation(byte[] data)
        {
            negotiationOutput = data;
            return ValueTask.CompletedTask;
        }
        
        var server = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit((data, enc, ti) => ValueTask.CompletedTask)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<AuthenticationProtocol>()
            .BuildAsync();

        // Act - Server initialization should send DO AUTHENTICATION
        await Task.Delay(100); // Allow initialization to complete

        // Assert - Server should send DO AUTHENTICATION
        await Assert.That(negotiationOutput).IsNotNull();
        await Assert.That(negotiationOutput).IsEquivalentTo(new byte[] 
        { 
            (byte)Trigger.IAC, 
            (byte)Trigger.DO, 
            (byte)Trigger.AUTHENTICATION 
        });
        
        await server.DisposeAsync();
    }

    [Test]
    public async Task ClientRespondsWithWillToServerDo()
    {
        // Arrange - Create local variable for capturing output
        byte[] negotiationOutput = null;
        
        ValueTask CaptureNegotiation(byte[] data)
        {
            negotiationOutput = data;
            return ValueTask.CompletedTask;
        }
        
        var client = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit((data, enc, ti) => ValueTask.CompletedTask)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<AuthenticationProtocol>()
            .BuildAsync();

        // Act - Client receives DO AUTHENTICATION from server
        await client.InterpretByteArrayAsync(new byte[] 
        { 
            (byte)Trigger.IAC, 
            (byte)Trigger.DO, 
            (byte)Trigger.AUTHENTICATION 
        });
        await client.WaitForProcessingAsync();

        // Assert - Client should respond with WILL AUTHENTICATION
        await Assert.That(negotiationOutput).IsNotNull();
        await Assert.That(negotiationOutput).IsEquivalentTo(new byte[] 
        { 
            (byte)Trigger.IAC, 
            (byte)Trigger.WILL, 
            (byte)Trigger.AUTHENTICATION 
        });
        
        await client.DisposeAsync();
    }

    [Test]
    public async Task ServerSendsSendSubnegotiationAfterClientWill()
    {
        // Arrange - Create local variables
        byte[] negotiationOutput = null;
        
        ValueTask CaptureNegotiation(byte[] data)
        {
            negotiationOutput = data;
            return ValueTask.CompletedTask;
        }
        
        var server = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit((data, enc, ti) => ValueTask.CompletedTask)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<AuthenticationProtocol>()
            .BuildAsync();

        // Act - Server receives WILL AUTHENTICATION from client
        await server.InterpretByteArrayAsync(new byte[] 
        { 
            (byte)Trigger.IAC, 
            (byte)Trigger.WILL, 
            (byte)Trigger.AUTHENTICATION 
        });
        await server.WaitForProcessingAsync();

        // Assert - Server should send SEND subnegotiation (empty list)
        await Assert.That(negotiationOutput).IsNotNull();
        await Assert.That(negotiationOutput).IsEquivalentTo(new byte[]
        {
            (byte)Trigger.IAC,
            (byte)Trigger.SB,
            (byte)Trigger.AUTHENTICATION,
            1, // SEND command
            (byte)Trigger.IAC,
            (byte)Trigger.SE
        });
        
        await server.DisposeAsync();
    }

    [Test]
    public async Task ClientRespondsWithIsNullToServerSend()
    {
        // Arrange - Create local variables
        byte[] negotiationOutput = null;
        
        ValueTask CaptureNegotiation(byte[] data)
        {
            negotiationOutput = data;
            return ValueTask.CompletedTask;
        }
        
        var client = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit((data, enc, ti) => ValueTask.CompletedTask)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<AuthenticationProtocol>()
            .BuildAsync();

        // First establish WILL/DO
        await client.InterpretByteArrayAsync(new byte[] 
        { 
            (byte)Trigger.IAC, 
            (byte)Trigger.DO, 
            (byte)Trigger.AUTHENTICATION 
        });
        await client.WaitForProcessingAsync();
        
        negotiationOutput = null;

        // Act - Client receives SEND subnegotiation
        await client.InterpretByteArrayAsync(new byte[]
        {
            (byte)Trigger.IAC,
            (byte)Trigger.SB,
            (byte)Trigger.AUTHENTICATION,
            1, // SEND command
            (byte)Trigger.IAC,
            (byte)Trigger.SE
        });
        await client.WaitForProcessingAsync();

        // Assert - Client should respond with IS NULL
        await Assert.That(negotiationOutput).IsNotNull();
        await Assert.That(negotiationOutput.Length).IsGreaterThanOrEqualTo(7);
        await Assert.That(negotiationOutput[0]).IsEqualTo((byte)Trigger.IAC);
        await Assert.That(negotiationOutput[1]).IsEqualTo((byte)Trigger.SB);
        await Assert.That(negotiationOutput[2]).IsEqualTo((byte)Trigger.AUTHENTICATION);
        await Assert.That(negotiationOutput[3]).IsEqualTo((byte)0); // IS command
        await Assert.That(negotiationOutput[4]).IsEqualTo((byte)0); // NULL type
        
        await client.DisposeAsync();
    }

    [Test]
    public async Task ServerRejectsClientWontAuthentication()
    {
        // Arrange - Create local variables
        byte[] negotiationOutput = null;
        
        ValueTask CaptureNegotiation(byte[] data)
        {
            negotiationOutput = data;
            return ValueTask.CompletedTask;
        }
        
        var server = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit((data, enc, ti) => ValueTask.CompletedTask)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<AuthenticationProtocol>()
            .BuildAsync();

        // Act - Server receives WONT AUTHENTICATION from client
        await server.InterpretByteArrayAsync(new byte[] 
        { 
            (byte)Trigger.IAC, 
            (byte)Trigger.WONT, 
            (byte)Trigger.AUTHENTICATION 
        });
        await server.WaitForProcessingAsync();

        // Assert - Server should accept WONT without additional response
        var isNullOrEmpty = negotiationOutput == null || negotiationOutput.Length == 0;
        await Assert.That(isNullOrEmpty).IsTrue();
        
        await server.DisposeAsync();
    }

    [Test]
    public async Task ClientRejectsServerDontAuthentication()
    {
        // Arrange - Create local variables
        byte[] negotiationOutput = null;
        
        ValueTask CaptureNegotiation(byte[] data)
        {
            negotiationOutput = data;
            return ValueTask.CompletedTask;
        }
        
        var client = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit((data, enc, ti) => ValueTask.CompletedTask)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<AuthenticationProtocol>()
            .BuildAsync();

        // Act - Client receives DONT AUTHENTICATION from server
        await client.InterpretByteArrayAsync(new byte[] 
        { 
            (byte)Trigger.IAC, 
            (byte)Trigger.DONT, 
            (byte)Trigger.AUTHENTICATION 
        });
        await client.WaitForProcessingAsync();

        // Assert - Client should accept DONT without additional response
        var isNullOrEmpty = negotiationOutput == null || negotiationOutput.Length == 0;
        await Assert.That(isNullOrEmpty).IsTrue();
        
        await client.DisposeAsync();
    }

    [Test]
    public async Task FullNegotiationSequence()
    {
        // Create local variables for capturing
        byte[] serverNegOutput = null;
        byte[] clientNegOutput = null;
        
        ValueTask CaptureServerNegotiation(byte[] data)
        {
            serverNegOutput = data;
            return ValueTask.CompletedTask;
        }
        
        ValueTask CaptureClientNegotiation(byte[] data)
        {
            clientNegOutput = data;
            return ValueTask.CompletedTask;
        }
        
        var server = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit((data, enc, ti) => ValueTask.CompletedTask)
            .OnNegotiation(CaptureServerNegotiation)
            .AddPlugin<AuthenticationProtocol>()
            .BuildAsync();
            
        var client = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit((data, enc, ti) => ValueTask.CompletedTask)
            .OnNegotiation(CaptureClientNegotiation)
            .AddPlugin<AuthenticationProtocol>()
            .BuildAsync();

        // Step 1: Server sends DO AUTHENTICATION
        await server.WaitForProcessingAsync();
        var serverMessage = serverNegOutput;
        await Assert.That(serverMessage).IsNotNull();
        
        serverNegOutput = null;

        // Step 2: Client receives DO and responds with WILL
        await client.InterpretByteArrayAsync(serverMessage);
        await client.WaitForProcessingAsync();
        var clientWill = clientNegOutput;
        await Assert.That(clientWill).IsNotNull();
        
        clientNegOutput = null;

        // Step 3: Server receives WILL and sends SEND
        await server.InterpretByteArrayAsync(clientWill);
        await server.WaitForProcessingAsync();
        var serverSend = serverNegOutput;
        await Assert.That(serverSend).IsNotNull();
        
        serverNegOutput = null;

        // Step 4: Client receives SEND and responds with IS NULL
        await client.InterpretByteArrayAsync(serverSend);
        await client.WaitForProcessingAsync();
        var clientIsNull = clientNegOutput;
        await Assert.That(clientIsNull).IsNotNull();

        // Verify the IS NULL response format
        await Assert.That(clientIsNull[0]).IsEqualTo((byte)Trigger.IAC);
        await Assert.That(clientIsNull[1]).IsEqualTo((byte)Trigger.SB);
        await Assert.That(clientIsNull[2]).IsEqualTo((byte)Trigger.AUTHENTICATION);
        await Assert.That(clientIsNull[3]).IsEqualTo((byte)0); // IS
        await Assert.That(clientIsNull[4]).IsEqualTo((byte)0); // NULL type
        
        await server.DisposeAsync();
        await client.DisposeAsync();
    }

    [Test]
    public async Task ServerCanProvideCustomAuthenticationTypes()
    {
        // Arrange - Server with custom auth types
        var authTypesCalled = false;
        byte[] negotiationOutput = null;
        
        ValueTask CaptureNegotiation(byte[] data)
        {
            negotiationOutput = data;
            return ValueTask.CompletedTask;
        }
        
        var server = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit((data, enc, ti) => ValueTask.CompletedTask)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<AuthenticationProtocol>()
                .WithAuthenticationTypes(() =>
                {
                    authTypesCalled = true;
                    return ValueTask.FromResult(new System.Collections.Generic.List<(byte, byte)>
                    {
                        (5, 0), // SRP with no modifiers
                        (6, 2)  // RSA with AUTH_HOW_MUTUAL
                    });
                })
            .BuildAsync();

        // Act - Client sends WILL
        await server.InterpretByteArrayAsync(new byte[] 
        { 
            (byte)Trigger.IAC, 
            (byte)Trigger.WILL, 
            (byte)Trigger.AUTHENTICATION 
        });
        await server.WaitForProcessingAsync();

        // Assert - Server should send SEND with auth types
        await Assert.That(authTypesCalled).IsTrue();
        await Assert.That(negotiationOutput).IsNotNull();
        await Assert.That(negotiationOutput[0]).IsEqualTo((byte)Trigger.IAC);
        await Assert.That(negotiationOutput[1]).IsEqualTo((byte)Trigger.SB);
        await Assert.That(negotiationOutput[2]).IsEqualTo((byte)Trigger.AUTHENTICATION);
        await Assert.That(negotiationOutput[3]).IsEqualTo((byte)1); // SEND
        await Assert.That(negotiationOutput[4]).IsEqualTo((byte)5); // SRP
        await Assert.That(negotiationOutput[5]).IsEqualTo((byte)0); // No modifiers
        await Assert.That(negotiationOutput[6]).IsEqualTo((byte)6); // RSA
        await Assert.That(negotiationOutput[7]).IsEqualTo((byte)2); // AUTH_HOW_MUTUAL
        
        await server.DisposeAsync();
    }

    [Test]
    public async Task ClientCanProvideCustomAuthenticationResponse()
    {
        // Arrange - Client with custom auth response handler
        byte[] receivedRequest = null;
        byte[] negotiationOutput = null;
        
        ValueTask CaptureNegotiation(byte[] data)
        {
            negotiationOutput = data;
            return ValueTask.CompletedTask;
        }
        
        var client = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit((data, enc, ti) => ValueTask.CompletedTask)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<AuthenticationProtocol>()
                .OnAuthenticationRequest((authTypePairs) =>
                {
                    receivedRequest = authTypePairs;
                    return ValueTask.FromResult((byte[])new byte[] { 5, 0, 0x01, 0x02, 0x03 });
                })
            .BuildAsync();

        // Establish WILL/DO first
        await client.InterpretByteArrayAsync(new byte[] 
        { 
            (byte)Trigger.IAC, 
            (byte)Trigger.DO, 
            (byte)Trigger.AUTHENTICATION 
        });
        await client.WaitForProcessingAsync();
        
        negotiationOutput = null;

        // Act - Client receives SEND with auth types
        await client.InterpretByteArrayAsync(new byte[]
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
        await client.WaitForProcessingAsync();

        // Assert - Client should send custom IS response
        await Assert.That(receivedRequest).IsNotNull();
        await Assert.That(receivedRequest.Length).IsEqualTo(4);
        await Assert.That(negotiationOutput).IsNotNull();
        await Assert.That(negotiationOutput[0]).IsEqualTo((byte)Trigger.IAC);
        await Assert.That(negotiationOutput[1]).IsEqualTo((byte)Trigger.SB);
        await Assert.That(negotiationOutput[2]).IsEqualTo((byte)Trigger.AUTHENTICATION);
        await Assert.That(negotiationOutput[3]).IsEqualTo((byte)0); // IS
        await Assert.That(negotiationOutput[4]).IsEqualTo((byte)5); // SRP
        await Assert.That(negotiationOutput[5]).IsEqualTo((byte)0); // No modifiers
        await Assert.That(negotiationOutput[6]).IsEqualTo((byte)0x01);
        await Assert.That(negotiationOutput[7]).IsEqualTo((byte)0x02);
        await Assert.That(negotiationOutput[8]).IsEqualTo((byte)0x03);
        
        await client.DisposeAsync();
    }

    [Test]
    public async Task ServerCanReceiveAndProcessAuthenticationResponse()
    {
        // Arrange - Server with auth response handler
        byte[] receivedAuthData = null;
        byte[] negotiationOutput = null;
        
        ValueTask CaptureNegotiation(byte[] data)
        {
            negotiationOutput = data;
            return ValueTask.CompletedTask;
        }
        
        var server = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit((data, enc, ti) => ValueTask.CompletedTask)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<AuthenticationProtocol>()
                .OnAuthenticationResponse(async (authData) =>
                {
                    receivedAuthData = authData;
                    await ValueTask.CompletedTask;
                })
            .BuildAsync();

        // Establish DO/WILL first
        await server.InterpretByteArrayAsync(new byte[] 
        { 
            (byte)Trigger.IAC, 
            (byte)Trigger.WILL, 
            (byte)Trigger.AUTHENTICATION 
        });
        await server.WaitForProcessingAsync();

        // Act - Server receives IS with auth data from client
        await server.InterpretByteArrayAsync(new byte[]
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
        await server.WaitForProcessingAsync();

        // Assert - Server should have received the auth data
        await Assert.That(receivedAuthData).IsNotNull();
        await Assert.That(receivedAuthData.Length).IsEqualTo(5);
        await Assert.That(receivedAuthData[0]).IsEqualTo((byte)5); // SRP
        await Assert.That(receivedAuthData[1]).IsEqualTo((byte)0); // No modifiers
        await Assert.That(receivedAuthData[2]).IsEqualTo((byte)0x01);
        await Assert.That(receivedAuthData[3]).IsEqualTo((byte)0x02);
        await Assert.That(receivedAuthData[4]).IsEqualTo((byte)0x03);
        
        await server.DisposeAsync();
    }
}
