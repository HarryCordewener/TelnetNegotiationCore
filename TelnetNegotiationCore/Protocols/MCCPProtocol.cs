using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Stateless;
using TelnetNegotiationCore.Attributes;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Plugins;

namespace TelnetNegotiationCore.Protocols;

/// <summary>
/// MCCP (Mud Client Compression Protocol) protocol plugin - MCCP2 and MCCP3
/// Implements https://tintin.mudhalla.net/protocols/mccp
/// Uses zlib compression (RFC 1950) via System.IO.Compression.ZLibStream
/// </summary>
/// <remarks>
/// MCCP2 provides server-to-client compression, reducing bandwidth by 75-90%.
/// MCCP3 provides client-to-server compression for security and bandwidth reduction.
/// This protocol optionally accepts configuration via callbacks.
/// 
/// RFC 1950 Compliance:
/// - Uses DEFLATE compression algorithm (compression method 8)
/// - Includes standard zlib header with checksum validation
/// - Includes ADLER-32 checksum for data integrity
/// - Full compliance verified via System.IO.Compression.ZLibStream
/// - See https://tintin.mudhalla.net/rfc/rfc1950 for specification
/// </remarks>
[RequiredMethod("OnCompressionEnabled", Description = "Configure the callback to handle compression state changes (optional)")]
public class MCCPProtocol : TelnetProtocolPluginBase
{
    private bool _mccp2Enabled = false;
    private bool _mccp3Enabled = false;
    private ZLibStream? _compressionStream;
    private MemoryStream? _compressionBuffer;

    private Func<int, bool, ValueTask>? _onCompressionEnabled;

    /// <summary>
    /// Sets the callback that is invoked when compression state changes.
    /// </summary>
    /// <param name="callback">The callback to handle compression changes (version: 2 or 3, enabled: true/false)</param>
    /// <returns>This instance for fluent chaining</returns>
    public MCCPProtocol OnCompressionEnabled(Func<int, bool, ValueTask>? callback)
    {
        _onCompressionEnabled = callback;
        return this;
    }

    /// <summary>
    /// Indicates whether MCCP2 (server-to-client) compression is enabled
    /// </summary>
    public bool IsMCCP2Enabled => _mccp2Enabled;

    /// <summary>
    /// Indicates whether MCCP3 (client-to-server) compression is enabled
    /// </summary>
    public bool IsMCCP3Enabled => _mccp3Enabled;

    /// <inheritdoc />
    public override Type ProtocolType => typeof(MCCPProtocol);

    /// <inheritdoc />
    public override string ProtocolName => "MCCP (Mud Client Compression Protocol)";

    /// <inheritdoc />
    public override IReadOnlyCollection<Type> Dependencies => Array.Empty<Type>();

    /// <inheritdoc />
    public override void ConfigureStateMachine(StateMachine<State, Trigger> stateMachine, IProtocolContext context)
    {
        context.Logger.LogInformation("Configuring MCCP state machine");

        // Register MCCP protocol handlers with the context
        context.SetSharedState("MCCP_Protocol", this);

        // Configure state machine transitions for MCCP protocol
        if (context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Server)
        {
            // Server mode: Handle MCCP2 (server compresses output to client)
            stateMachine.Configure(State.Do)
                .Permit(Trigger.MCCP2, State.DoMCCP2);

            stateMachine.Configure(State.Dont)
                .Permit(Trigger.MCCP2, State.DontMCCP2);

            stateMachine.Configure(State.DoMCCP2)
                .SubstateOf(State.Accepting)
                .OnEntryAsync(async () => await OnDoMCCP2Async(context));

            stateMachine.Configure(State.DontMCCP2)
                .SubstateOf(State.Accepting)
                .OnEntryAsync(async () => await OnDontMCCP2Async(context));

            // Server mode: Handle MCCP3 (client compresses input to server)
            stateMachine.Configure(State.Do)
                .Permit(Trigger.MCCP3, State.DoMCCP3);

            stateMachine.Configure(State.Dont)
                .Permit(Trigger.MCCP3, State.DontMCCP3);

            stateMachine.Configure(State.DoMCCP3)
                .SubstateOf(State.Accepting)
                .OnEntryAsync(async () => await OnDoMCCP3Async(context));

            stateMachine.Configure(State.DontMCCP3)
                .SubstateOf(State.Accepting)
                .OnEntryAsync(async () => await OnDontMCCP3Async(context));

            // Server initiates MCCP on connection
            context.RegisterInitialNegotiation(async () => await InitiateMCCPServerAsync(context));
        }
        else
        {
            // Client mode: Handle MCCP2 (receive compressed data from server)
            stateMachine.Configure(State.Willing)
                .Permit(Trigger.MCCP2, State.WillMCCP2);

            stateMachine.Configure(State.Refusing)
                .Permit(Trigger.MCCP2, State.WontMCCP2);

            stateMachine.Configure(State.WillMCCP2)
                .SubstateOf(State.Accepting)
                .OnEntryAsync(async () => await OnWillMCCP2Async(context));

            stateMachine.Configure(State.WontMCCP2)
                .SubstateOf(State.Accepting)
                .OnEntryAsync(async () => await OnWontMCCP2Async(context));

            // Client mode: Handle MCCP3 (compress output to server)
            stateMachine.Configure(State.Willing)
                .Permit(Trigger.MCCP3, State.WillMCCP3);

            stateMachine.Configure(State.Refusing)
                .Permit(Trigger.MCCP3, State.WontMCCP3);

            stateMachine.Configure(State.WillMCCP3)
                .SubstateOf(State.Accepting)
                .OnEntryAsync(async () => await OnWillMCCP3Async(context));

            stateMachine.Configure(State.WontMCCP3)
                .SubstateOf(State.Accepting)
                .OnEntryAsync(async () => await OnWontMCCP3Async(context));
        }

        // Sub-negotiation for MCCP2
        stateMachine.Configure(State.SubNegotiation)
            .Permit(Trigger.MCCP2, State.NegotiatingMCCP2);

        stateMachine.Configure(State.NegotiatingMCCP2)
            .Permit(Trigger.IAC, State.CompletingMCCP2);

        stateMachine.Configure(State.CompletingMCCP2)
            .SubstateOf(State.EndSubNegotiation)
            .OnEntryAsync(async () => await CompleteMCCP2NegotiationAsync(context));

        // Sub-negotiation for MCCP3
        stateMachine.Configure(State.SubNegotiation)
            .Permit(Trigger.MCCP3, State.NegotiatingMCCP3);

        stateMachine.Configure(State.NegotiatingMCCP3)
            .Permit(Trigger.IAC, State.CompletingMCCP3);

        stateMachine.Configure(State.CompletingMCCP3)
            .SubstateOf(State.EndSubNegotiation)
            .OnEntryAsync(async () => await CompleteMCCP3NegotiationAsync(context));
    }

    /// <inheritdoc />
    protected override ValueTask OnInitializeAsync()
    {
        Context.Logger.LogInformation("MCCP Protocol initialized");
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolEnabledAsync()
    {
        Context.Logger.LogInformation("MCCP Protocol enabled");
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolDisabledAsync()
    {
        Context.Logger.LogInformation("MCCP Protocol disabled");
        DisableCompression();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    protected override ValueTask OnDisposeAsync()
    {
        DisableCompression();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Compresses data using the active compression stream (MCCP2 for server, MCCP3 for client)
    /// Uses RFC 1950 compliant zlib compression via System.IO.Compression.ZLibStream
    /// </summary>
    /// <param name="data">The data to compress</param>
    /// <returns>The compressed data, or original data if compression is not active</returns>
    public byte[] CompressData(byte[] data)
    {
        if (_compressionStream == null || _compressionBuffer == null)
            return data;

        try
        {
            _compressionBuffer.SetLength(0);
            _compressionStream.Write(data, 0, data.Length);
            _compressionStream.Flush();
            return _compressionBuffer.ToArray();
        }
        catch (Exception ex)
        {
            Context.Logger.LogError(ex, "Error compressing data, sending uncompressed");
            return data;
        }
    }

    /// <summary>
    /// Decompresses data using RFC 1950 compliant zlib decompression
    /// Creates a new ZLibStream for each decompression operation to handle stream-based data correctly
    /// </summary>
    /// <param name="data">The compressed data</param>
    /// <returns>The decompressed data</returns>
    public byte[] DecompressData(byte[] data)
    {
        if (data == null || data.Length == 0)
            return Array.Empty<byte>();

        try
        {
            // Create a new memory stream for the compressed input data
            using var compressedStream = new MemoryStream(data);
            using var zlibStream = new ZLibStream(compressedStream, CompressionMode.Decompress);
            using var outputStream = new MemoryStream();
            
            zlibStream.CopyTo(outputStream);
            return outputStream.ToArray();
        }
        catch (Exception ex)
        {
            Context.Logger.LogError(ex, "Error decompressing data");
            // Signal decompression error - fire and forget with error handling
            Task.Run(async () =>
            {
                try
                {
                    await DisableCompressionWithErrorAsync();
                }
                catch (Exception disableEx)
                {
                    Context.Logger.LogError(disableEx, "Error disabling compression after decompression failure");
                }
            });
            return Array.Empty<byte>();
        }
    }

    private void DisableCompression()
    {
        _compressionStream?.Dispose();
        _compressionStream = null;
        _compressionBuffer?.Dispose();
        _compressionBuffer = null;
        _mccp2Enabled = false;
        _mccp3Enabled = false;
    }

    private async ValueTask DisableCompressionWithErrorAsync()
    {
        Context.Logger.LogWarning("Disabling compression due to error");
        DisableCompression();

        // Send DONT MCCP to remote party
        if (Context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Server)
        {
            await Context.SendNegotiationAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WONT, (byte)Trigger.MCCP2 });
            await Context.SendNegotiationAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WONT, (byte)Trigger.MCCP3 });
        }
        else
        {
            await Context.SendNegotiationAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DONT, (byte)Trigger.MCCP2 });
            await Context.SendNegotiationAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DONT, (byte)Trigger.MCCP3 });
        }
    }

    #region State Machine Handlers

    private async ValueTask InitiateMCCPServerAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Server announcing MCCP2 and MCCP3 support");
        await context.SendNegotiationAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.MCCP2 });
        await context.SendNegotiationAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.MCCP3 });
    }

    private async ValueTask OnDoMCCP2Async(IProtocolContext context)
    {
        context.Logger.LogDebug("Client supports MCCP2 - will start compression");
        // Send sub-negotiation to start compression: IAC SB MCCP2 IAC SE
        await context.SendNegotiationAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.MCCP2, (byte)Trigger.IAC, (byte)Trigger.SE });
        // Compression will be enabled in CompleteMCCP2NegotiationAsync
    }

    private ValueTask OnDontMCCP2Async(IProtocolContext context)
    {
        context.Logger.LogDebug("Client doesn't support MCCP2");
        _mccp2Enabled = false;
        return ValueTask.CompletedTask;
    }

    private async ValueTask OnDoMCCP3Async(IProtocolContext context)
    {
        context.Logger.LogDebug("Client will use MCCP3 compression");
        // Client will send IAC SB MCCP3 IAC SE and start compressing
        // Server is ready to decompress (done per-message in DecompressData)
        _mccp3Enabled = true;

        if (_onCompressionEnabled != null)
            await _onCompressionEnabled(3, true);
    }

    private ValueTask OnDontMCCP3Async(IProtocolContext context)
    {
        context.Logger.LogDebug("Client doesn't support MCCP3");
        _mccp3Enabled = false;
        return ValueTask.CompletedTask;
    }

    private async ValueTask OnWillMCCP2Async(IProtocolContext context)
    {
        context.Logger.LogDebug("Server supports MCCP2 - accepting");
        await context.SendNegotiationAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MCCP2 });
    }

    private async ValueTask OnWontMCCP2Async(IProtocolContext context)
    {
        context.Logger.LogDebug("Server doesn't support MCCP2");
        _mccp2Enabled = false;
        if (_onCompressionEnabled != null)
            await _onCompressionEnabled(2, false);
    }

    private async ValueTask OnWillMCCP3Async(IProtocolContext context)
    {
        context.Logger.LogDebug("Server supports MCCP3 - will start compressing output");
        await context.SendNegotiationAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MCCP3 });
        // Send sub-negotiation: IAC SB MCCP3 IAC SE
        await context.SendNegotiationAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.MCCP3, (byte)Trigger.IAC, (byte)Trigger.SE });
        // Start compression
        _compressionBuffer = new MemoryStream();
        _compressionStream = new ZLibStream(_compressionBuffer, CompressionMode.Compress);
        _mccp3Enabled = true;

        if (_onCompressionEnabled != null)
            await _onCompressionEnabled(3, true);
    }

    private async ValueTask OnWontMCCP3Async(IProtocolContext context)
    {
        context.Logger.LogDebug("Server doesn't support MCCP3");
        _mccp3Enabled = false;
        if (_onCompressionEnabled != null)
            await _onCompressionEnabled(3, false);
    }

    private async ValueTask CompleteMCCP2NegotiationAsync(IProtocolContext context)
    {
        context.Logger.LogInformation("MCCP2 compression starting");
        
        // Initialize compression stream for server-to-client compression
        _compressionBuffer = new MemoryStream();
        _compressionStream = new ZLibStream(_compressionBuffer, CompressionMode.Compress);
        _mccp2Enabled = true;

        if (_onCompressionEnabled != null)
            await _onCompressionEnabled(2, true);
    }

    private async ValueTask CompleteMCCP3NegotiationAsync(IProtocolContext context)
    {
        context.Logger.LogInformation("MCCP3 decompression ready");
        
        // Server is ready to decompress client data (done per-message in DecompressData)
        _mccp3Enabled = true;

        if (_onCompressionEnabled != null)
            await _onCompressionEnabled(3, true);
    }

    #endregion
}
