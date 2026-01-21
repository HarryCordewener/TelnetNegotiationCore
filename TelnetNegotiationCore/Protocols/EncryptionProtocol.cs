using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OneOf;
using Stateless;
using TelnetNegotiationCore.Attributes;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Plugins;

namespace TelnetNegotiationCore.Protocols;

/// <summary>
/// Encryption protocol plugin (RFC 2946)
/// https://datatracker.ietf.org/doc/html/rfc2946
/// </summary>
/// <remarks>
/// <para>
/// This implementation provides a flexible framework for telnet data encryption negotiation.
/// By default, it rejects all encryption types by responding with NULL, allowing
/// sessions to proceed without encryption. This maintains backward compatibility
/// while providing the protocol negotiation framework.
/// </para>
/// <para>
/// Consumers can inject custom encryption behavior via callbacks to implement
/// specific encryption algorithms such as DES_CFB64, DES_OFB64, DES3_CFB64, or others.
/// </para>
/// <para><strong>Server-Side Usage:</strong></para>
/// <code>
/// var telnet = await new TelnetInterpreterBuilder()
///     .UseMode(TelnetInterpreter.TelnetMode.Server)
///     .AddPlugin&lt;EncryptionProtocol&gt;()
///         .WithEncryptionTypes(async () => new List&lt;byte&gt; { 1, 3 })  // DES_CFB64, DES3_CFB64
///         .OnEncryptionRequest(async (encType, data) => InitializeEncryption(encType, data))
///     .BuildAsync();
/// </code>
/// <para><strong>Client-Side Usage:</strong></para>
/// <code>
/// var telnet = await new TelnetInterpreterBuilder()
///     .UseMode(TelnetInterpreter.TelnetMode.Client)
///     .AddPlugin&lt;EncryptionProtocol&gt;()
///         .OnEncryptionSupport(async (types) => SelectEncryptionType(types))
///     .BuildAsync();
/// </code>
/// <para><strong>Encryption Types:</strong></para>
/// <list type="bullet">
/// <item><description>0 - NULL (no encryption)</description></item>
/// <item><description>1 - DES_CFB64</description></item>
/// <item><description>2 - DES_OFB64</description></item>
/// <item><description>3 - DES3_CFB64</description></item>
/// <item><description>4 - DES3_OFB64</description></item>
/// <item><description>8 - CAST5_40_CFB64</description></item>
/// <item><description>9 - CAST5_40_OFB64</description></item>
/// <item><description>10 - CAST128_CFB64</description></item>
/// <item><description>11 - CAST128_OFB64</description></item>
/// </list>
/// <para><strong>Encryption Commands:</strong></para>
/// <list type="bullet">
/// <item><description>0 - IS (sent by WILL side to initialize encryption)</description></item>
/// <item><description>1 - SUPPORT (sent by DO side with supported types)</description></item>
/// <item><description>2 - REPLY (sent by DO side to continue initialization)</description></item>
/// <item><description>3 - START (sent by WILL side to begin encryption)</description></item>
/// <item><description>4 - END (sent by WILL side to stop encryption)</description></item>
/// <item><description>5 - REQUEST-START (sent by DO side to request encryption)</description></item>
/// <item><description>6 - REQUEST-END (sent by DO side to stop encryption)</description></item>
/// <item><description>7 - ENC_KEYID (verify encryption keyid)</description></item>
/// <item><description>8 - DEC_KEYID (verify decryption keyid)</description></item>
/// </list>
/// </remarks>
public class EncryptionProtocol : TelnetProtocolPluginBase
{
    /// <summary>
    /// Encryption type NULL - used to indicate no encryption types are supported
    /// </summary>
    private const byte ENC_NULL = 0;

    /// <summary>
    /// Encryption command IS - sent by WILL side to initialize encryption
    /// </summary>
    private const byte ENC_IS = 0;

    /// <summary>
    /// Encryption command SUPPORT - sent by DO side with supported types
    /// </summary>
    private const byte ENC_SUPPORT = 1;

    /// <summary>
    /// Encryption command REPLY - sent by DO side to continue initialization
    /// </summary>
    private const byte ENC_REPLY = 2;

    /// <summary>
    /// Encryption command START - sent by WILL side to begin encryption
    /// </summary>
    private const byte ENC_START = 3;

    /// <summary>
    /// Encryption command END - sent by WILL side to stop encryption
    /// </summary>
    private const byte ENC_END = 4;

    /// <summary>
    /// Encryption command REQUEST-START - sent by DO side to request encryption
    /// </summary>
    private const byte ENC_REQUEST_START = 5;

    /// <summary>
    /// Encryption command REQUEST-END - sent by DO side to stop encryption
    /// </summary>
    private const byte ENC_REQUEST_END = 6;

    /// <summary>
    /// Encryption command ENC_KEYID - verify encryption keyid
    /// </summary>
    private const byte ENC_ENC_KEYID = 7;

    /// <summary>
    /// Encryption command DEC_KEYID - verify decryption keyid
    /// </summary>
    private const byte ENC_DEC_KEYID = 8;

    // Callbacks for encryption behavior injection
    private Func<byte[], ValueTask<byte[]?>>? _onEncryptionSupport;
    private Func<byte[], ValueTask>? _onEncryptionRequest;
    private Func<ValueTask<List<byte>>>? _encryptionTypesProvider;
    private Func<byte[], ValueTask>? _onEncryptionStart;
    private Func<ValueTask>? _onEncryptionEnd;
    
    // State for capturing encryption data during subnegotiation
    private List<byte> _encryptionData = new();
    private bool _isEncrypting = false;

    /// <inheritdoc />
    public override Type ProtocolType => typeof(EncryptionProtocol);

    /// <inheritdoc />
    public override string ProtocolName => "Encryption (RFC 2946)";

    /// <inheritdoc />
    public override IReadOnlyCollection<Type> Dependencies => Array.Empty<Type>();

    /// <summary>
    /// Indicates whether encryption is currently active
    /// </summary>
    public bool IsEncrypting => _isEncrypting;

    /// <summary>
    /// Sets the callback invoked when the client receives encryption SUPPORT message from the server.
    /// </summary>
    /// <param name="callback">
    /// Callback to handle encryption support. The callback receives a byte array containing
    /// the list of supported encryption types offered by the server.
    /// Should return initialization data for selected encryption type (type byte + init data),
    /// or null to reject all encryption types with NULL response.
    /// </param>
    /// <returns>This instance for fluent chaining</returns>
    /// <remarks>
    /// <para><strong>Client-side only.</strong> This callback is invoked when the server sends a SUPPORT subnegotiation
    /// with a list of supported encryption types.</para>
    /// <example>
    /// <code>
    /// .OnEncryptionSupport(async (supportedTypes) =>
    /// {
    ///     // supportedTypes format: [type1, type2, ...]
    ///     if (supportedTypes.Contains(1)) // DES_CFB64
    ///     {
    ///         var initData = await GetEncryptionInitData(1);
    ///         return new byte[] { 1 }.Concat(initData).ToArray();
    ///     }
    ///     return null; // Reject all types
    /// })
    /// </code>
    /// </example>
    /// </remarks>
    public EncryptionProtocol OnEncryptionSupport(Func<byte[], ValueTask<byte[]?>>? callback)
    {
        _onEncryptionSupport = callback;
        return this;
    }

    /// <summary>
    /// Sets the callback invoked when the server receives an encryption IS message from the client.
    /// </summary>
    /// <param name="callback">
    /// Callback to handle encryption initialization. The callback receives a byte array containing
    /// the encryption type and initialization data from the client.
    /// </param>
    /// <returns>This instance for fluent chaining</returns>
    /// <remarks>
    /// <para><strong>Server-side only.</strong> This callback is invoked when the client sends an IS subnegotiation
    /// with encryption initialization data.</para>
    /// <example>
    /// <code>
    /// .OnEncryptionRequest(async (encData) =>
    /// {
    ///     var encType = encData[0];
    ///     var initData = encData.Skip(1).ToArray();
    ///     await InitializeDecryption(encType, initData);
    /// })
    /// </code>
    /// </example>
    /// </remarks>
    public EncryptionProtocol OnEncryptionRequest(Func<byte[], ValueTask>? callback)
    {
        _onEncryptionRequest = callback;
        return this;
    }

    /// <summary>
    /// Sets the provider function that returns the list of supported encryption types.
    /// </summary>
    /// <param name="provider">
    /// Function that provides a list of encryption types. Types should be ordered by preference
    /// (most preferred first). If not set, the server will send an empty list (rejecting encryption).
    /// </param>
    /// <returns>This instance for fluent chaining</returns>
    /// <remarks>
    /// <para><strong>Server-side only.</strong> Used by the server to offer encryption options to the client.</para>
    /// <para><strong>Common Encryption Types:</strong></para>
    /// <list type="bullet">
    /// <item><description>0 - NULL (no encryption)</description></item>
    /// <item><description>1 - DES_CFB64</description></item>
    /// <item><description>2 - DES_OFB64</description></item>
    /// <item><description>3 - DES3_CFB64</description></item>
    /// <item><description>4 - DES3_OFB64</description></item>
    /// </list>
    /// <example>
    /// <code>
    /// .WithEncryptionTypes(async () => new List&lt;byte&gt;
    /// {
    ///     1,  // DES_CFB64
    ///     3   // DES3_CFB64
    /// })
    /// </code>
    /// </example>
    /// </remarks>
    public EncryptionProtocol WithEncryptionTypes(Func<ValueTask<List<byte>>>? provider)
    {
        _encryptionTypesProvider = provider;
        return this;
    }

    /// <summary>
    /// Sets the callback invoked when encryption starts.
    /// </summary>
    /// <param name="callback">Callback to handle encryption start event. Receives keyid data.</param>
    /// <returns>This instance for fluent chaining</returns>
    public EncryptionProtocol OnEncryptionStart(Func<byte[], ValueTask>? callback)
    {
        _onEncryptionStart = callback;
        return this;
    }

    /// <summary>
    /// Sets the callback invoked when encryption ends.
    /// </summary>
    /// <param name="callback">Callback to handle encryption end event</param>
    /// <returns>This instance for fluent chaining</returns>
    public EncryptionProtocol OnEncryptionEnd(Func<ValueTask>? callback)
    {
        _onEncryptionEnd = callback;
        return this;
    }

    /// <summary>
    /// Sends a SUPPORT message with the list of supported encryption types.
    /// </summary>
    /// <param name="encryptionTypes">List of supported encryption types (ordered by preference)</param>
    /// <returns>A task representing the asynchronous operation</returns>
    /// <remarks>
    /// <para><strong>Server-side.</strong> Used by servers to announce supported encryption types.</para>
    /// <para>This sends an IAC SB ENCRYPT SUPPORT [types] IAC SE message.</para>
    /// </remarks>
    public async ValueTask SendEncryptionSupportAsync(List<byte> encryptionTypes)
    {
        if (!IsEnabled)
            return;

        var bytes = new List<byte>
        {
            (byte)Trigger.IAC,
            (byte)Trigger.SB,
            (byte)Trigger.ENCRYPT,
            ENC_SUPPORT
        };

        bytes.AddRange(encryptionTypes);
        bytes.Add((byte)Trigger.IAC);
        bytes.Add((byte)Trigger.SE);

        await Context.SendNegotiationAsync(bytes.ToArray());
    }

    /// <summary>
    /// Sends an IS message to initialize encryption.
    /// </summary>
    /// <param name="encryptionData">
    /// The encryption initialization data including type and init data.
    /// Format: [encType, ...init data]
    /// </param>
    /// <returns>A task representing the asynchronous operation</returns>
    /// <remarks>
    /// <para><strong>Client-side.</strong> Used by clients to initialize encryption.</para>
    /// <para>This sends an IAC SB ENCRYPT IS [enc data] IAC SE message.</para>
    /// </remarks>
    public async ValueTask SendEncryptionIsAsync(byte[] encryptionData)
    {
        if (!IsEnabled)
            return;

        var bytes = new List<byte>
        {
            (byte)Trigger.IAC,
            (byte)Trigger.SB,
            (byte)Trigger.ENCRYPT,
            ENC_IS
        };

        bytes.AddRange(encryptionData);
        bytes.Add((byte)Trigger.IAC);
        bytes.Add((byte)Trigger.SE);

        await Context.SendNegotiationAsync(bytes.ToArray());
    }

    /// <summary>
    /// Sends a REPLY message to continue encryption initialization.
    /// </summary>
    /// <param name="replyData">Reply data for encryption initialization</param>
    /// <returns>A task representing the asynchronous operation</returns>
    public async ValueTask SendEncryptionReplyAsync(byte[] replyData)
    {
        if (!IsEnabled)
            return;

        var bytes = new List<byte>
        {
            (byte)Trigger.IAC,
            (byte)Trigger.SB,
            (byte)Trigger.ENCRYPT,
            ENC_REPLY
        };

        bytes.AddRange(replyData);
        bytes.Add((byte)Trigger.IAC);
        bytes.Add((byte)Trigger.SE);

        await Context.SendNegotiationAsync(bytes.ToArray());
    }

    /// <summary>
    /// Sends a START message to begin encryption.
    /// </summary>
    /// <param name="keyId">Optional key identifier (default is 0)</param>
    /// <returns>A task representing the asynchronous operation</returns>
    public async ValueTask SendEncryptionStartAsync(byte[]? keyId = null)
    {
        if (!IsEnabled)
            return;

        var bytes = new List<byte>
        {
            (byte)Trigger.IAC,
            (byte)Trigger.SB,
            (byte)Trigger.ENCRYPT,
            ENC_START
        };

        if (keyId != null && keyId.Length > 0)
            bytes.AddRange(keyId);
        else
            bytes.Add(0); // Default keyid

        bytes.Add((byte)Trigger.IAC);
        bytes.Add((byte)Trigger.SE);

        await Context.SendNegotiationAsync(bytes.ToArray());
    }

    /// <summary>
    /// Sends an END message to stop encryption.
    /// </summary>
    /// <returns>A task representing the asynchronous operation</returns>
    public async ValueTask SendEncryptionEndAsync()
    {
        if (!IsEnabled)
            return;

        var bytes = new List<byte>
        {
            (byte)Trigger.IAC,
            (byte)Trigger.SB,
            (byte)Trigger.ENCRYPT,
            ENC_END,
            (byte)Trigger.IAC,
            (byte)Trigger.SE
        };

        await Context.SendNegotiationAsync(bytes.ToArray());
    }

    /// <summary>
    /// Sends a REQUEST-START message to request encryption.
    /// </summary>
    /// <param name="keyId">Optional key identifier</param>
    /// <returns>A task representing the asynchronous operation</returns>
    public async ValueTask SendEncryptionRequestStartAsync(byte[]? keyId = null)
    {
        if (!IsEnabled)
            return;

        var bytes = new List<byte>
        {
            (byte)Trigger.IAC,
            (byte)Trigger.SB,
            (byte)Trigger.ENCRYPT,
            ENC_REQUEST_START
        };

        if (keyId != null && keyId.Length > 0)
            bytes.AddRange(keyId);

        bytes.Add((byte)Trigger.IAC);
        bytes.Add((byte)Trigger.SE);

        await Context.SendNegotiationAsync(bytes.ToArray());
    }

    /// <summary>
    /// Sends a REQUEST-END message to request stopping encryption.
    /// </summary>
    /// <returns>A task representing the asynchronous operation</returns>
    public async ValueTask SendEncryptionRequestEndAsync()
    {
        if (!IsEnabled)
            return;

        var bytes = new List<byte>
        {
            (byte)Trigger.IAC,
            (byte)Trigger.SB,
            (byte)Trigger.ENCRYPT,
            ENC_REQUEST_END,
            (byte)Trigger.IAC,
            (byte)Trigger.SE
        };

        await Context.SendNegotiationAsync(bytes.ToArray());
    }

    /// <inheritdoc />
    public override void ConfigureStateMachine(StateMachine<State, Trigger> stateMachine, IProtocolContext context)
    {
        context.Logger.LogInformation("Configuring Encryption state machine");
        
        // Register Encryption protocol handlers with the context
        context.SetSharedState("Encryption_Protocol", this);
        
        // Configure state machine transitions for Encryption protocol
        if (context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Server)
        {
            ConfigureAsServer(stateMachine, context);
        }
        else
        {
            ConfigureAsClient(stateMachine, context);
        }
    }

    private void ConfigureAsServer(StateMachine<State, Trigger> stateMachine, IProtocolContext context)
    {
        // Server side: Receives WILL ENCRYPT from client
        stateMachine.Configure(State.Willing)
            .Permit(Trigger.ENCRYPT, State.WillEncryption);

        stateMachine.Configure(State.Refusing)
            .Permit(Trigger.ENCRYPT, State.WontEncryption);

        stateMachine.Configure(State.WillEncryption)
            .SubstateOf(State.Accepting)
            .OnEntryAsync(async () => await OnClientWillEncryptAsync(context));

        stateMachine.Configure(State.WontEncryption)
            .SubstateOf(State.Accepting)
            .OnEntry(() => context.Logger.LogDebug("Client won't encrypt"));

        // Server handles IS subnegotiation (encryption initialization from client)
        stateMachine.Configure(State.SubNegotiation)
            .Permit(Trigger.ENCRYPT, State.AlmostNegotiatingEncryption);

        stateMachine.Configure(State.AlmostNegotiatingEncryption)
            .Permit(Trigger.IS, State.NegotiatingEncryptionIs)
            .OnEntry(() =>
            {
                context.Logger.LogDebug("Receiving encryption IS from client");
                _encryptionData = new List<byte>();
            });

        // Handle the IS command - capture all encryption data until IAC
        stateMachine.Configure(State.NegotiatingEncryptionIs)
            .Permit(Trigger.IAC, State.CompletingEncryptionNegotiation);
        
        // Capture all other triggers as encryption data
        TriggerHelper.ForAllTriggersButIAC(t => 
            stateMachine.Configure(State.NegotiatingEncryptionIs)
                .OnEntryFrom(context.Interpreter.ParameterizedTrigger(t), (OneOf<byte, Trigger> b) => _encryptionData.Add(b.AsT0))
                .PermitReentry(t));

        stateMachine.Configure(State.CompletingEncryptionNegotiation)
            .Permit(Trigger.SE, State.ProcessingEncryptionIs)
            .OnEntry(() => context.Logger.LogDebug("Received end of IS subnegotiation"));

        stateMachine.Configure(State.ProcessingEncryptionIs)
            .SubstateOf(State.Accepting)
            .OnEntryAsync(async () => await ProcessEncryptionIsAsync(context));

        // Server initiates encryption negotiation
        context.RegisterInitialNegotiation(async () => await SendDoEncryptAsync(context));
    }

    private void ConfigureAsClient(StateMachine<State, Trigger> stateMachine, IProtocolContext context)
    {
        // Client side: Receives DO ENCRYPT from server
        stateMachine.Configure(State.Do)
            .Permit(Trigger.ENCRYPT, State.DoEncryption);

        stateMachine.Configure(State.Dont)
            .Permit(Trigger.ENCRYPT, State.DontEncryption);

        stateMachine.Configure(State.DoEncryption)
            .SubstateOf(State.Accepting)
            .OnEntryAsync(async () => await OnServerRequestsEncryptionAsync(context));

        stateMachine.Configure(State.DontEncryption)
            .SubstateOf(State.Accepting)
            .OnEntry(() => context.Logger.LogDebug("Server doesn't want encryption"));

        // Client handles SUPPORT subnegotiation
        stateMachine.Configure(State.SubNegotiation)
            .Permit(Trigger.ENCRYPT, State.AlmostNegotiatingEncryption);

        stateMachine.Configure(State.AlmostNegotiatingEncryption)
            .Permit(Trigger.SEND, State.NegotiatingEncryptionSupport)
            .OnEntry(() =>
            {
                context.Logger.LogDebug("Starting encryption subnegotiation");
                _encryptionData = new List<byte>();
            });

        // Handle the SUPPORT command - capture all encryption types until IAC
        stateMachine.Configure(State.NegotiatingEncryptionSupport)
            .Permit(Trigger.IAC, State.CompletingEncryptionNegotiation);
        
        // Capture all other triggers as encryption types
        TriggerHelper.ForAllTriggersButIAC(t => 
            stateMachine.Configure(State.NegotiatingEncryptionSupport)
                .OnEntryFrom(context.Interpreter.ParameterizedTrigger(t), (OneOf<byte, Trigger> b) => _encryptionData.Add(b.AsT0))
                .PermitReentry(t));

        stateMachine.Configure(State.CompletingEncryptionNegotiation)
            .Permit(Trigger.SE, State.ProcessingEncryptionSupport)
            .OnEntry(() => context.Logger.LogDebug("Received end of SUPPORT subnegotiation"));

        stateMachine.Configure(State.ProcessingEncryptionSupport)
            .SubstateOf(State.Accepting)
            .OnEntryAsync(async () => await ProcessEncryptionSupportAsync(context));
    }

    /// <inheritdoc />
    protected override ValueTask OnInitializeAsync()
    {
        Context.Logger.LogInformation("Encryption Protocol initialized");
        return default(ValueTask);
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolEnabledAsync()
    {
        Context.Logger.LogInformation("Encryption Protocol enabled");
        return default(ValueTask);
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolDisabledAsync()
    {
        Context.Logger.LogInformation("Encryption Protocol disabled");
        _isEncrypting = false;
        return default(ValueTask);
    }

    /// <inheritdoc />
    protected override ValueTask OnDisposeAsync()
    {
        _isEncrypting = false;
        return default(ValueTask);
    }

    #region State Machine Handlers

    private async ValueTask SendDoEncryptAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Server requesting client to encrypt (DO ENCRYPT)");
        await context.SendNegotiationAsync(new byte[] 
        { 
            (byte)Trigger.IAC, 
            (byte)Trigger.DO, 
            (byte)Trigger.ENCRYPT 
        });
    }

    private async ValueTask OnClientWillEncryptAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Client willing to encrypt - sending encryption types");
        
        // Get the list of encryption types from the provider (or empty list to reject)
        var encTypes = new List<byte>();
        if (_encryptionTypesProvider != null)
        {
            encTypes = await _encryptionTypesProvider();
        }

        // Send SUPPORT subnegotiation with encryption types
        await SendEncryptionSupportAsync(encTypes);
    }

    private async ValueTask OnServerRequestsEncryptionAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Server requests encryption (DO ENCRYPT) - responding with WILL");
        await context.SendNegotiationAsync(new byte[] 
        { 
            (byte)Trigger.IAC, 
            (byte)Trigger.WILL, 
            (byte)Trigger.ENCRYPT 
        });
    }

    private async ValueTask ProcessEncryptionSupportAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Processing encryption SUPPORT");
        
        byte[]? responseData = null;
        
        // If callback is provided, let it handle the encryption type selection
        if (_onEncryptionSupport != null)
        {
            responseData = await _onEncryptionSupport(_encryptionData.ToArray());
        }
        
        // If no response data or callback not set, send NULL rejection
        if (responseData == null)
        {
            context.Logger.LogDebug("Sending IS NULL response - rejecting all encryption types");
            responseData = new byte[] { ENC_NULL };
        }
        
        await SendEncryptionIsAsync(responseData);
    }

    private async ValueTask ProcessEncryptionIsAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Processing encryption IS from client");
        
        // Invoke callback if provided
        if (_onEncryptionRequest != null)
        {
            await _onEncryptionRequest(_encryptionData.ToArray());
        }
        else
        {
            context.Logger.LogDebug("No encryption request handler configured - encryption data ignored");
        }
    }

    #endregion
}
