# Authentication Implementation Guide

This guide demonstrates how to implement various authentication mechanisms using the TelnetNegotiationCore `AuthenticationProtocol` plugin with external cryptographic libraries.

## Table of Contents

- [Overview](#overview)
- [Kerberos V4 Authentication](#kerberos-v4-authentication)
- [Kerberos V5 Authentication](#kerberos-v5-authentication)
- [SRP (Secure Remote Password)](#srp-secure-remote-password)
- [RSA Authentication](#rsa-authentication)
- [SSL/TLS Authentication](#ssltls-authentication)
- [Security Considerations](#security-considerations)

## Overview

The `AuthenticationProtocol` provides a flexible callback-based system for implementing any RFC 2941-compliant authentication mechanism. This guide shows how to integrate external cryptographic libraries to implement specific authentication types.

### Authentication Type Constants

```csharp
public static class AuthType
{
    public const byte NULL = 0;
    public const byte KERBEROS_V4 = 1;
    public const byte KERBEROS_V5 = 2;
    public const byte SPX = 3;
    public const byte MINK = 4;
    public const byte SRP = 5;
    public const byte RSA = 6;
    public const byte SSL = 7;
    public const byte LOKI = 10;
    public const byte SSA = 11;
    public const byte KEA_SJ = 12;
    public const byte KEA_SJ_INTEG = 13;
    public const byte DSS = 14;
    public const byte NTLM = 15;
}

public static class AuthModifiers
{
    // WHO
    public const byte CLIENT_TO_SERVER = 0x00;
    public const byte SERVER_TO_CLIENT = 0x01;
    public const byte AUTH_WHO_MASK = 0x01;
    
    // HOW
    public const byte ONE_WAY = 0x00;
    public const byte MUTUAL = 0x02;
    public const byte AUTH_HOW_MASK = 0x02;
    
    // ENCRYPT
    public const byte ENCRYPT_OFF = 0x00;
    public const byte ENCRYPT_USING_TELOPT = 0x04;
    public const byte ENCRYPT_AFTER_EXCHANGE = 0x10;
    public const byte ENCRYPT_RESERVED = 0x14;
    public const byte ENCRYPT_MASK = 0x14;
    
    // CREDENTIALS
    public const byte INI_CRED_FWD_OFF = 0x00;
    public const byte INI_CRED_FWD_ON = 0x08;
    public const byte INI_CRED_FWD_MASK = 0x08;
}
```

## Kerberos V4 Authentication

**RFC**: [RFC 2942](https://datatracker.ietf.org/doc/html/rfc2942) - Telnet Authentication: Kerberos Version 4

**NuGet Package**: `Kerberos.NET` or platform-specific Kerberos libraries

### Server Implementation

```csharp
using Kerberos.NET;
using Kerberos.NET.Crypto;
using Kerberos.NET.Entities;

public class KerberosV4AuthenticationHandler
{
    private readonly KerberosAuthenticator _authenticator;
    
    public KerberosV4AuthenticationHandler(string realm, byte[] serviceKey)
    {
        _authenticator = new KerberosAuthenticator(new KerberosValidator(serviceKey));
    }
    
    public async Task<byte[]> HandleKerberosV4RequestAsync(byte[] authData)
    {
        try
        {
            var authType = authData[0]; // Should be 1 (KERBEROS_V4)
            var modifiers = authData[1];
            var ticket = authData.Skip(2).ToArray();
            
            // Validate the Kerberos ticket
            var identity = await _authenticator.Authenticate(ticket);
            
            if (identity.Name != null)
            {
                // Authentication successful
                // Return ACCEPT status
                return new byte[] { authType, modifiers, 0x00 }; // 0x00 = ACCEPT
            }
            else
            {
                // Authentication failed
                return new byte[] { authType, modifiers, 0x01 }; // 0x01 = REJECT
            }
        }
        catch (Exception ex)
        {
            // Log error and return rejection
            return new byte[] { authData[0], authData[1], 0x01 };
        }
    }
}

// Usage in TelnetNegotiationCore
var kerberosHandler = new KerberosV4AuthenticationHandler("REALM", serviceKey);

var telnet = await new TelnetInterpreterBuilder()
    .UseMode(TelnetInterpreter.TelnetMode.Server)
    .AddPlugin<AuthenticationProtocol>()
        .WithAuthenticationTypes(async () => new List<(byte, byte)>
        {
            (AuthType.KERBEROS_V4, AuthModifiers.CLIENT_TO_SERVER | AuthModifiers.MUTUAL)
        })
        .OnAuthenticationResponse(async (authData) =>
        {
            var authType = authData[0];
            if (authType == AuthType.KERBEROS_V4)
            {
                var replyData = await kerberosHandler.HandleKerberosV4RequestAsync(authData);
                var authPlugin = telnet.PluginManager!.GetPlugin<AuthenticationProtocol>();
                await authPlugin!.SendAuthenticationReplyAsync(replyData);
            }
        })
    .BuildAsync();
```

### Client Implementation

```csharp
using Kerberos.NET.Client;
using Kerberos.NET.Credentials;

public class KerberosV4Client
{
    private readonly KerberosClient _client;
    
    public KerberosV4Client(string realm)
    {
        _client = new KerberosClient();
    }
    
    public async Task<byte[]> GetKerberosTicketAsync(string username, string password, string servicePrincipal)
    {
        var credential = new KerberosPasswordCredential(username, password);
        var ticket = await _client.GetServiceTicket(servicePrincipal, credential);
        
        return ticket.EncodeApplication().ToArray();
    }
}

// Usage in TelnetNegotiationCore
var kerberosClient = new KerberosV4Client("REALM");

var telnet = await new TelnetInterpreterBuilder()
    .UseMode(TelnetInterpreter.TelnetMode.Client)
    .AddPlugin<AuthenticationProtocol>()
        .OnAuthenticationRequest(async (authTypePairs) =>
        {
            // Check if Kerberos V4 is offered
            for (int i = 0; i < authTypePairs.Length; i += 2)
            {
                if (authTypePairs[i] == AuthType.KERBEROS_V4)
                {
                    var modifiers = authTypePairs[i + 1];
                    var ticket = await kerberosClient.GetKerberosTicketAsync(
                        "username", "password", "telnet/server@REALM");
                    
                    var response = new List<byte> { AuthType.KERBEROS_V4, modifiers };
                    response.AddRange(ticket);
                    return response.ToArray();
                }
            }
            return null; // No supported auth type
        })
    .BuildAsync();
```

## Kerberos V5 Authentication

**RFC**: [RFC 2942](https://datatracker.ietf.org/doc/html/rfc2942) - Telnet Authentication: Kerberos Version 5

**NuGet Package**: `Kerberos.NET`

### Server Implementation

```csharp
using Kerberos.NET;
using Kerberos.NET.Server;

public class KerberosV5AuthenticationHandler
{
    private readonly KerberosAuthenticator _authenticator;
    
    public KerberosV5AuthenticationHandler(byte[] serviceKey)
    {
        var validator = new KerberosValidator(serviceKey);
        _authenticator = new KerberosAuthenticator(validator);
    }
    
    public async Task<byte[]> HandleKerberosV5RequestAsync(byte[] authData)
    {
        try
        {
            var authType = authData[0]; // Should be 2 (KERBEROS_V5)
            var modifiers = authData[1];
            var apReq = authData.Skip(2).ToArray();
            
            // Authenticate the AP-REQ
            var authenticator = await _authenticator.Authenticate(apReq);
            
            if (authenticator != null)
            {
                // Generate AP-REP for mutual authentication if required
                if ((modifiers & AuthModifiers.MUTUAL) != 0)
                {
                    var apRep = authenticator.GenerateReply();
                    var response = new List<byte> { authType, modifiers, 0x00 }; // ACCEPT
                    response.AddRange(apRep);
                    return response.ToArray();
                }
                else
                {
                    return new byte[] { authType, modifiers, 0x00 }; // ACCEPT
                }
            }
            else
            {
                return new byte[] { authType, modifiers, 0x01 }; // REJECT
            }
        }
        catch
        {
            return new byte[] { authData[0], authData[1], 0x01 }; // REJECT
        }
    }
}

// Usage similar to Kerberos V4 but with AuthType.KERBEROS_V5
```

## SRP (Secure Remote Password)

**RFC**: [RFC 2944](https://datatracker.ietf.org/doc/html/rfc2944) - Telnet Authentication: SRP

**NuGet Package**: `SecureRemotePassword` or custom SRP implementation

### Server Implementation

```csharp
using SecureRemotePassword;

public class SRPAuthenticationHandler
{
    private readonly SrpServer _srpServer;
    private readonly Dictionary<string, string> _userDatabase; // username -> verifier
    private Dictionary<string, SrpEphemeral> _sessions = new();
    
    public SRPAuthenticationHandler()
    {
        _srpServer = new SrpServer();
        _userDatabase = new Dictionary<string, string>();
    }
    
    public void RegisterUser(string username, string verifier, string salt)
    {
        _userDatabase[$"{username}:{salt}"] = verifier;
    }
    
    public async Task<byte[]> HandleSRPRequestAsync(byte[] authData, string sessionId)
    {
        var authType = authData[0]; // Should be 5 (SRP)
        var modifiers = authData[1];
        var command = authData[2]; // SRP sub-command
        
        switch (command)
        {
            case 0: // SRP_AUTH (client username)
                var username = Encoding.UTF8.GetString(authData.Skip(3).ToArray());
                
                // Generate server ephemeral
                var salt = GetSaltForUser(username);
                var verifier = _userDatabase[$"{username}:{salt}"];
                var serverEphemeral = _srpServer.GenerateEphemeral(verifier);
                _sessions[sessionId] = serverEphemeral;
                
                // Send challenge with salt and server public key
                var response = new List<byte> { authType, modifiers, 1 }; // SRP_CHALLENGE
                response.AddRange(Encoding.UTF8.GetBytes(salt));
                response.Add(0); // separator
                response.AddRange(Convert.FromBase64String(serverEphemeral.Public));
                
                return response.ToArray();
                
            case 2: // SRP_CLIENT_AUTH (client proof)
                var clientPublic = Convert.ToBase64String(authData.Skip(3).Take(256).ToArray());
                var clientProof = Convert.ToBase64String(authData.Skip(259).ToArray());
                
                var session = _sessions[sessionId];
                var serverSession = _srpServer.DeriveSession(
                    session.Secret, clientPublic, salt, username, verifier);
                
                if (serverSession.Proof == clientProof)
                {
                    // Authentication successful
                    var acceptResponse = new List<byte> { authType, modifiers, 3 }; // SRP_ACCEPT
                    acceptResponse.AddRange(Convert.FromBase64String(serverSession.Proof));
                    return acceptResponse.ToArray();
                }
                else
                {
                    return new byte[] { authType, modifiers, 4 }; // SRP_REJECT
                }
                
            default:
                return new byte[] { authType, modifiers, 4 }; // SRP_REJECT
        }
    }
    
    private string GetSaltForUser(string username)
    {
        // Retrieve salt from user database
        return "user_salt_here";
    }
}

// Usage with multi-round authentication
var srpHandler = new SRPAuthenticationHandler();

var telnet = await new TelnetInterpreterBuilder()
    .UseMode(TelnetInterpreter.TelnetMode.Server)
    .AddPlugin<AuthenticationProtocol>()
        .WithAuthenticationTypes(async () => new List<(byte, byte)>
        {
            (AuthType.SRP, AuthModifiers.CLIENT_TO_SERVER | AuthModifiers.MUTUAL)
        })
        .OnAuthenticationResponse(async (authData) =>
        {
            if (authData[0] == AuthType.SRP)
            {
                var sessionId = telnet.GetHashCode().ToString();
                var replyData = await srpHandler.HandleSRPRequestAsync(authData, sessionId);
                
                var authPlugin = telnet.PluginManager!.GetPlugin<AuthenticationProtocol>();
                await authPlugin!.SendAuthenticationReplyAsync(replyData);
            }
        })
    .BuildAsync();
```

### Client Implementation

```csharp
using SecureRemotePassword;

public class SRPClient
{
    private readonly SrpClient _srpClient;
    private SrpEphemeral? _clientEphemeral;
    
    public SRPClient()
    {
        _srpClient = new SrpClient();
    }
    
    public byte[] InitiateAuthentication(string username)
    {
        var response = new List<byte> { AuthType.SRP, 0, 0 }; // SRP_AUTH
        response.AddRange(Encoding.UTF8.GetBytes(username));
        return response.ToArray();
    }
    
    public byte[] RespondToChallenge(byte[] challengeData, string username, string password)
    {
        // Parse challenge (salt and server public key)
        var data = challengeData.Skip(3).ToArray();
        var separatorIndex = Array.IndexOf(data, (byte)0);
        var salt = Encoding.UTF8.GetString(data.Take(separatorIndex).ToArray());
        var serverPublic = Convert.ToBase64String(data.Skip(separatorIndex + 1).ToArray());
        
        // Generate private key from password
        var privateKey = _srpClient.DerivePrivateKey(salt, username, password);
        
        // Generate ephemeral
        _clientEphemeral = _srpClient.GenerateEphemeral(privateKey);
        
        // Derive session
        var clientSession = _srpClient.DeriveSession(
            _clientEphemeral.Secret, serverPublic, salt, username, privateKey);
        
        // Send client public key and proof
        var response = new List<byte> { AuthType.SRP, 0, 2 }; // SRP_CLIENT_AUTH
        response.AddRange(Convert.FromBase64String(_clientEphemeral.Public));
        response.AddRange(Convert.FromBase64String(clientSession.Proof));
        
        return response.ToArray();
    }
}
```

## RSA Authentication

**NuGet Package**: `System.Security.Cryptography` (built-in)

### Server Implementation

```csharp
using System.Security.Cryptography;

public class RSAAuthenticationHandler
{
    private readonly RSA _rsa;
    private readonly Dictionary<string, byte[]> _challenges = new();
    
    public RSAAuthenticationHandler()
    {
        _rsa = RSA.Create(2048);
    }
    
    public byte[] GetPublicKey()
    {
        return _rsa.ExportRSAPublicKey();
    }
    
    public byte[] CreateChallenge(string sessionId)
    {
        var challenge = new byte[32];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(challenge);
        }
        _challenges[sessionId] = challenge;
        return challenge;
    }
    
    public async Task<byte[]> HandleRSAResponseAsync(byte[] authData, string sessionId)
    {
        var authType = authData[0]; // Should be 6 (RSA)
        var modifiers = authData[1];
        var signedChallenge = authData.Skip(2).ToArray();
        
        try
        {
            // Verify the signature
            var challenge = _challenges[sessionId];
            var isValid = _rsa.VerifyData(
                challenge, 
                signedChallenge, 
                HashAlgorithmName.SHA256, 
                RSASignaturePadding.Pkcs1);
            
            if (isValid)
            {
                return new byte[] { authType, modifiers, 0x00 }; // ACCEPT
            }
            else
            {
                return new byte[] { authType, modifiers, 0x01 }; // REJECT
            }
        }
        catch
        {
            return new byte[] { authType, modifiers, 0x01 }; // REJECT
        }
    }
}

// Usage
var rsaHandler = new RSAAuthenticationHandler();

var telnet = await new TelnetInterpreterBuilder()
    .UseMode(TelnetInterpreter.TelnetMode.Server)
    .AddPlugin<AuthenticationProtocol>()
        .WithAuthenticationTypes(async () => new List<(byte, byte)>
        {
            (AuthType.RSA, AuthModifiers.CLIENT_TO_SERVER | AuthModifiers.ONE_WAY)
        })
        .OnAuthenticationResponse(async (authData) =>
        {
            if (authData[0] == AuthType.RSA)
            {
                var sessionId = telnet.GetHashCode().ToString();
                
                // First, send challenge
                if (authData.Length == 2) // Initial request
                {
                    var challenge = rsaHandler.CreateChallenge(sessionId);
                    var challengeMsg = new List<byte> { AuthType.RSA, authData[1], 0x02 }; // CHALLENGE
                    challengeMsg.AddRange(challenge);
                    
                    var authPlugin = telnet.PluginManager!.GetPlugin<AuthenticationProtocol>();
                    await authPlugin!.SendAuthenticationReplyAsync(challengeMsg.ToArray());
                }
                else // Response to challenge
                {
                    var replyData = await rsaHandler.HandleRSAResponseAsync(authData, sessionId);
                    var authPlugin = telnet.PluginManager!.GetPlugin<AuthenticationProtocol>();
                    await authPlugin!.SendAuthenticationReplyAsync(replyData);
                }
            }
        })
    .BuildAsync();
```

### Client Implementation

```csharp
using System.Security.Cryptography;

public class RSAClientAuth
{
    private readonly RSA _clientRsa;
    
    public RSAClientAuth(string privateKeyPem)
    {
        _clientRsa = RSA.Create();
        _clientRsa.ImportFromPem(privateKeyPem);
    }
    
    public byte[] SignChallenge(byte[] challenge)
    {
        return _clientRsa.SignData(challenge, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }
}

// Usage
var rsaClient = new RSAClientAuth(privateKeyPem);
byte[] pendingChallenge = null;

var telnet = await new TelnetInterpreterBuilder()
    .UseMode(TelnetInterpreter.TelnetMode.Client)
    .AddPlugin<AuthenticationProtocol>()
        .OnAuthenticationRequest(async (authTypePairs) =>
        {
            for (int i = 0; i < authTypePairs.Length; i += 2)
            {
                if (authTypePairs[i] == AuthType.RSA)
                {
                    var modifiers = authTypePairs[i + 1];
                    
                    if (pendingChallenge != null)
                    {
                        // Respond to challenge
                        var signature = rsaClient.SignChallenge(pendingChallenge);
                        var response = new List<byte> { AuthType.RSA, modifiers };
                        response.AddRange(signature);
                        pendingChallenge = null;
                        return response.ToArray();
                    }
                    else
                    {
                        // Initial request
                        return new byte[] { AuthType.RSA, modifiers };
                    }
                }
            }
            return null;
        })
    .BuildAsync();
```

## SSL/TLS Authentication

**RFC**: [RFC 2946](https://datatracker.ietf.org/doc/html/rfc2946) - Telnet Data Encryption Option

**NuGet Package**: `System.Net.Security` (built-in)

### Server Implementation

```csharp
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

public class SSLAuthenticationHandler
{
    private readonly X509Certificate2 _serverCertificate;
    private SslStream? _sslStream;
    
    public SSLAuthenticationHandler(string certPath, string certPassword)
    {
        _serverCertificate = new X509Certificate2(certPath, certPassword);
    }
    
    public async Task<bool> NegotiateSSLAsync(Stream networkStream)
    {
        try
        {
            _sslStream = new SslStream(networkStream, false);
            
            await _sslStream.AuthenticateAsServerAsync(
                _serverCertificate,
                clientCertificateRequired: true,
                enabledSslProtocols: SslProtocols.Tls12 | SslProtocols.Tls13,
                checkCertificateRevocation: true);
            
            return _sslStream.IsAuthenticated && _sslStream.IsEncrypted;
        }
        catch
        {
            return false;
        }
    }
    
    public X509Certificate2? GetClientCertificate()
    {
        return _sslStream?.RemoteCertificate as X509Certificate2;
    }
}

// Usage
var sslHandler = new SSLAuthenticationHandler("server.pfx", "password");

var telnet = await new TelnetInterpreterBuilder()
    .UseMode(TelnetInterpreter.TelnetMode.Server)
    .AddPlugin<AuthenticationProtocol>()
        .WithAuthenticationTypes(async () => new List<(byte, byte)>
        {
            (AuthType.SSL, AuthModifiers.CLIENT_TO_SERVER | AuthModifiers.MUTUAL | 
                          AuthModifiers.ENCRYPT_AFTER_EXCHANGE)
        })
        .OnAuthenticationResponse(async (authData) =>
        {
            if (authData[0] == AuthType.SSL)
            {
                // SSL negotiation happens at the transport level
                // After successful SSL handshake, send ACCEPT
                var authPlugin = telnet.PluginManager!.GetPlugin<AuthenticationProtocol>();
                await authPlugin!.SendAuthenticationReplyAsync(
                    new byte[] { AuthType.SSL, authData[1], 0x00 }); // ACCEPT
            }
        })
    .BuildAsync();

// Note: SSL/TLS typically requires wrapping the entire connection
// The telnet authentication is used to signal the SSL upgrade
```

### Client Implementation

```csharp
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

public class SSLClientAuth
{
    private readonly X509Certificate2 _clientCertificate;
    private SslStream? _sslStream;
    
    public SSLClientAuth(string certPath, string certPassword)
    {
        _clientCertificate = new X509Certificate2(certPath, certPassword);
    }
    
    public async Task<bool> NegotiateSSLAsync(Stream networkStream, string targetHost)
    {
        try
        {
            _sslStream = new SslStream(
                networkStream, 
                false,
                ValidateServerCertificate);
            
            var clientCertificates = new X509Certificate2Collection(_clientCertificate);
            
            await _sslStream.AuthenticateAsClientAsync(
                targetHost,
                clientCertificates,
                SslProtocols.Tls12 | SslProtocols.Tls13,
                checkCertificateRevocation: true);
            
            return _sslStream.IsAuthenticated && _sslStream.IsEncrypted;
        }
        catch
        {
            return false;
        }
    }
    
    private bool ValidateServerCertificate(
        object sender,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors sslPolicyErrors)
    {
        // Implement certificate validation logic
        return sslPolicyErrors == SslPolicyErrors.None;
    }
}
```

## Security Considerations

### General Best Practices

1. **Key Management**
   - Never hardcode credentials or keys in source code
   - Use secure key storage (Azure Key Vault, AWS KMS, etc.)
   - Rotate keys regularly
   - Use strong key generation (minimum 2048-bit RSA, 256-bit symmetric)

2. **Transport Security**
   - Always use TLS/SSL for the underlying connection when possible
   - Validate certificates properly
   - Use certificate pinning for critical applications

3. **Credential Storage**
   - Store password hashes, never plaintext passwords
   - Use appropriate hashing algorithms (Argon2, PBKDF2, bcrypt)
   - Add salt to password hashes
   - Use secure random number generators

4. **Session Management**
   - Implement proper session timeouts
   - Invalidate sessions after authentication failure
   - Use secure session identifiers
   - Protect against replay attacks

5. **Error Handling**
   - Don't leak information in error messages
   - Log authentication failures securely
   - Implement rate limiting and account lockout
   - Monitor for suspicious authentication patterns

### Authentication-Specific Considerations

**Kerberos**
- Ensure clock synchronization between client and server
- Protect service keys
- Use appropriate encryption types
- Implement ticket lifetime limits

**SRP**
- Use strong password requirements
- Protect verifier database
- Implement proper session key derivation
- Use appropriate group parameters (minimum 2048-bit)

**RSA**
- Use minimum 2048-bit keys (3072-bit recommended)
- Implement proper padding (OAEP for encryption, PSS for signatures)
- Protect private keys
- Use secure random number generation for challenges

**SSL/TLS**
- Use TLS 1.2 or higher
- Disable weak cipher suites
- Implement proper certificate validation
- Use certificate transparency and OCSP stapling

### Compliance

Ensure your implementation complies with relevant standards and regulations:
- FIPS 140-2/3 for cryptographic modules
- PCI DSS for payment applications
- HIPAA for healthcare applications
- GDPR for data protection
- SOC 2 for service organizations

### Testing

Always test your authentication implementation:
- Unit tests for cryptographic operations
- Integration tests for authentication flows
- Penetration testing
- Security audits
- Fuzz testing
- Performance testing under load

### Resources

- [NIST Cryptographic Standards](https://csrc.nist.gov/projects/cryptographic-standards-and-guidelines)
- [OWASP Authentication Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/Authentication_Cheat_Sheet.html)
- [RFC 2941 - Telnet Authentication Option](https://datatracker.ietf.org/doc/html/rfc2941)
- [RFC 2942 - Telnet Authentication: Kerberos](https://datatracker.ietf.org/doc/html/rfc2942)
- [RFC 2944 - Telnet Authentication: SRP](https://datatracker.ietf.org/doc/html/rfc2944)
- [RFC 2946 - Telnet Data Encryption Option](https://datatracker.ietf.org/doc/html/rfc2946)

## Conclusion

The TelnetNegotiationCore `AuthenticationProtocol` provides a flexible framework for implementing any RFC 2941-compliant authentication mechanism. By using the callback system with appropriate external cryptographic libraries, you can implement secure authentication while keeping the core library lightweight and focused on protocol negotiation.

Remember to always:
- Use well-tested cryptographic libraries
- Follow security best practices
- Implement proper error handling
- Test thoroughly
- Get security reviews for production deployments
