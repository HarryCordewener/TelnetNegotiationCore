using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
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

    // What this side actually put in its SUPPORT, kept so the peer's IS can be held to it. Null
    // until a SUPPORT built from a configured provider has gone out.
    private List<byte>? _offeredEncryptionTypes;
    private Func<byte[], ValueTask>? _onEncryptionStart;
    private Func<ValueTask>? _onEncryptionEnd;
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
    ///     // supportedTypes[0] is the SUPPORT command byte (1); the offered types start at index 1.
    ///     // format: [1, type1, type2, ...]
    ///     if (supportedTypes.Skip(1).Contains((byte)1)) // DES_CFB64
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
    ///     // encData[0] is the IS command byte (0); the type and init data start at index 1.
    ///     var encType = encData[1];
    ///     var initData = encData.Skip(2).ToArray();
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
    /// Sets the callback invoked when a genuine START marker arrives from the peer -- it is now
    /// encrypting what it sends. <see cref="IsEncrypting"/> is already true by the time this runs.
    /// </summary>
    /// <param name="callback">Callback to handle encryption start event. Receives the keyid bytes,
    /// or an empty array if the peer sent none.</param>
    /// <returns>This instance for fluent chaining</returns>
    public EncryptionProtocol OnEncryptionStart(Func<byte[], ValueTask>? callback)
    {
        _onEncryptionStart = callback;
        return this;
    }

    /// <summary>
    /// Sets the callback invoked when a genuine END marker arrives from the peer -- it has stopped
    /// encrypting what it sends. <see cref="IsEncrypting"/> is already false by the time this runs.
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
    public ValueTask SendEncryptionSupportAsync(List<byte> encryptionTypes)
        => SendEncryptionSupportAsync(encryptionTypes, recordAsOffer: true);

    /// <param name="recordAsOffer">
    /// Whether this list becomes the advertisement the peer's <c>IS</c> is held to, once it is sent. True for every
    /// deliberate offer; false only for the empty <c>SUPPORT</c> the plugin emits when nothing is
    /// configured, which is a refusal rather than an advertisement.
    /// </param>
    /// <inheritdoc cref="SendEncryptionSupportAsync(List{byte})"/>
    private async ValueTask SendEncryptionSupportAsync(List<byte> encryptionTypes, bool recordAsOffer)
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

        // Only once it is on the wire — see the note on the authentication side.
        if (recordAsOffer)
        {
            _offeredEncryptionTypes = [.. encryptionTypes];
        }
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
    /// <remarks>
    /// Negotiation acceptance and the subnegotiation are wired to the generated machine (see
    /// <see cref="OnPeerNegotiatedAsync"/>, <see cref="ProcessEncryptionSupportFromBytesAsync"/>/
    /// <see cref="ProcessEncryptionIsFromBytesAsync"/>, and <see cref="ProcessEncryptionStartFromBytesAsync"/>/
    /// <see cref="ProcessEncryptionEndFromBytesAsync"/>); this hook survives only to register the
    /// server's initial offer, a cross-cutting mechanism independent of which machine drives byte
    /// processing.
    /// </remarks>
    public override void ConfigureStateMachine(IProtocolContext context)
    {
        if (context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Server)
        {
            context.RegisterInitialNegotiation(async () => await SendDoEncryptAsync(context));
        }
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

    /// <summary>
    /// Whether an inbound <c>IS</c> names a type this side put in its <c>SUPPORT</c>. Always true
    /// when no encryption types were configured: there is no advertisement to hold the peer to.
    /// </summary>
    /// <param name="message">The subnegotiation body, command byte first.</param>
    private bool IsOfferedEncryptionType(byte[] message)
    {
        if (_offeredEncryptionTypes is null)
        {
            return true;
        }

        return message.Length >= 2 && _offeredEncryptionTypes.Contains(message[1]);
    }

    private async ValueTask OnClientWillEncryptAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Client willing to encrypt - sending encryption types");
        await OnNegotiatedAsync(true);

        // Get the list of encryption types from the provider (or empty list to reject)
        var encTypes = new List<byte>();
        if (_encryptionTypesProvider != null)
        {
            encTypes = await _encryptionTypesProvider();
        }

        // Send SUPPORT subnegotiation with encryption types
        await SendEncryptionSupportAsync(encTypes, recordAsOffer: _encryptionTypesProvider != null);
    }

    private async ValueTask OnServerRequestsEncryptionAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Server requests encryption (DO ENCRYPT) - responding with WILL");
        await OnNegotiatedAsync(true);
        await context.SendNegotiationAsync(new byte[]
        { 
            (byte)Trigger.IAC, 
            (byte)Trigger.WILL, 
            (byte)Trigger.ENCRYPT 
        });
    }

    internal async ValueTask ProcessEncryptionSupportFromBytesAsync(byte[] data, IProtocolContext context)
    {
        context.Logger.LogDebug("Processing encryption SUPPORT");

        byte[]? responseData = null;

        // If callback is provided, let it handle the encryption type selection
        if (_onEncryptionSupport != null)
        {
            responseData = await _onEncryptionSupport(data);
        }

        // If no response data or callback not set, send NULL rejection
        if (responseData == null)
        {
            context.Logger.LogDebug("Sending IS NULL response - rejecting all encryption types");
            responseData = new byte[] { ENC_NULL };
        }

        await SendEncryptionIsAsync(responseData);
    }

    internal async ValueTask ProcessEncryptionIsFromBytesAsync(byte[] data, IProtocolContext context)
    {
        context.Logger.LogDebug("Processing encryption IS from client");

        if (!IsOfferedEncryptionType(data))
        {
            // Refused before the callback, which is the point: OnEncryptionRequest is where a
            // consumer initialises decryption, and initialising it for an algorithm this side
            // never offered is exactly what this stops.
            context.Logger.LogWarning(
                "Client answered ENCRYPT with type {EncryptionType}, which was not offered. Ignoring.",
                data.Length > 1 ? data[1] : -1);
            return;
        }

        // Invoke callback if provided
        if (_onEncryptionRequest != null)
        {
            await _onEncryptionRequest(data);
        }
        else
        {
            context.Logger.LogDebug("No encryption request handler configured - encryption data ignored");
        }
    }

    /// <summary>
    /// A genuine START marker arrived: the peer is now encrypting what it sends, using <paramref name="keyId"/>.
    /// </summary>
    internal async ValueTask ProcessEncryptionStartFromBytesAsync(byte[] keyId, IProtocolContext context)
    {
        context.Logger.LogDebug("Encryption started by peer");
        _isEncrypting = true;

        if (_onEncryptionStart != null)
        {
            await _onEncryptionStart(keyId);
        }
        else
        {
            context.Logger.LogDebug("No encryption start handler configured - START marker ignored");
        }
    }

    /// <summary>A genuine END marker arrived: the peer has stopped encrypting what it sends.</summary>
    internal async ValueTask ProcessEncryptionEndFromBytesAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Encryption ended by peer");
        _isEncrypting = false;

        if (_onEncryptionEnd != null)
        {
            await _onEncryptionEnd();
        }
        else
        {
            context.Logger.LogDebug("No encryption end handler configured - END marker ignored");
        }
    }

    private ValueTask OnWontEncryptAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Client won't encrypt");
        return OnNegotiatedAsync(false);
    }

    private ValueTask OnDontEncryptAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Server doesn't want encryption");
        return OnNegotiatedAsync(false);
    }

    /// <summary>A server only ever configured WILL/WONT for this option, a client only ever
    /// configured DO/DONT -- the same split AuthenticationProtocol needed.</summary>
    internal async ValueTask OnPeerNegotiatedAsync(byte verb, IProtocolContext context)
    {
        var server = context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Server;
        switch (verb)
        {
            case (byte)Trigger.WILL when server:
                await OnClientWillEncryptAsync(context);
                break;
            case (byte)Trigger.WONT when server:
                await OnWontEncryptAsync(context);
                break;
            case (byte)Trigger.DO when !server:
                await OnServerRequestsEncryptionAsync(context);
                break;
            case (byte)Trigger.DONT when !server:
                await OnDontEncryptAsync(context);
                break;
        }
    }

    #endregion
}
