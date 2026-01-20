using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OneOf;
using Stateless;
using TelnetNegotiationCore.Attributes;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Plugins;

namespace TelnetNegotiationCore.Protocols;

/// <summary>
/// GMCP (Generic MUD Communication Protocol) plugin implementation.
/// This demonstrates the plugin architecture pattern.
/// </summary>
/// <remarks>
/// This protocol optionally accepts configuration. Call <see cref="OnGMCPMessage"/> to set up
/// the callback that will handle GMCP messages if you need to be notified of client messages.
/// </remarks>
[RequiredMethod("OnGMCPMessage", Description = "Configure the callback to handle GMCP messages (optional but recommended)")]
public class GMCPProtocol : TelnetProtocolPluginBase
{
    private Channel<byte> _gmcpByteChannel = Channel.CreateBounded<byte>(new BoundedChannelOptions(8192)
    {
        FullMode = BoundedChannelFullMode.DropWrite  // Drop bytes if message too large (DOS protection)
    });

    private Func<(string Package, string Info), ValueTask>? _onGMCPReceived;

    /// <summary>
    /// Sets the callback that is invoked when a GMCP message is received.
    /// </summary>
    /// <param name="callback">The callback to handle GMCP messages</param>
    /// <returns>This instance for fluent chaining</returns>
    public GMCPProtocol OnGMCPMessage(Func<(string Package, string Info), ValueTask>? callback)
    {
        _onGMCPReceived = callback;
        return this;
    }



    /// <inheritdoc />
    public override Type ProtocolType => typeof(GMCPProtocol);

    /// <inheritdoc />
    public override string ProtocolName => "GMCP (Generic MUD Communication Protocol)";

    /// <inheritdoc />
    public override IReadOnlyCollection<Type> Dependencies => Array.Empty<Type>();
    // Note: In the original code, GMCP had a dependency on MSDP for message forwarding
    // This can be expressed as: new[] { typeof(MSDPProtocol) }

    /// <inheritdoc />
    public override void ConfigureStateMachine(StateMachine<State, Trigger> stateMachine, IProtocolContext context)
    {
        context.Logger.LogInformation("Configuring GMCP state machine");
        
        // Register GMCP protocol handlers with the context
        context.SetSharedState("GMCP_Protocol", this);
        
        if (context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Server)
        {
            stateMachine.Configure(State.Do)
                .Permit(Trigger.GMCP, State.DoGMCP);

            stateMachine.Configure(State.Dont)
                .Permit(Trigger.GMCP, State.DontGMCP);

            stateMachine.Configure(State.DoGMCP)
                .SubstateOf(State.Accepting)
                .OnEntry(() => context.Logger.LogDebug("Connection: {ConnectionState}", "Client will do GMCP"));

            stateMachine.Configure(State.DontGMCP)
                .SubstateOf(State.Accepting)
                .OnEntry(() => context.Logger.LogDebug("Connection: {ConnectionState}", "Client will not GMCP"));

            context.RegisterInitialNegotiation(async () => await WillGMCPAsync(context));
        }
        else if (context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Client)
        {
            stateMachine.Configure(State.Willing)
                .Permit(Trigger.GMCP, State.WillGMCP);

            stateMachine.Configure(State.Refusing)
                .Permit(Trigger.GMCP, State.WontGMCP);

            stateMachine.Configure(State.WillGMCP)
                .SubstateOf(State.Accepting)
                .OnEntryAsync(async x => await DoGMCPAsync(x, context));

            stateMachine.Configure(State.WontGMCP)
                .SubstateOf(State.Accepting)
                .OnEntry(() => context.Logger.LogDebug("Connection: {ConnectionState}", "Client will GMCP"));
        }

        stateMachine.Configure(State.SubNegotiation)
            .Permit(Trigger.GMCP, State.AlmostNegotiatingGMCP);

        stateMachine.Configure(State.AlmostNegotiatingGMCP)
            .Permit(Trigger.IAC, State.EscapingGMCPValue)
            .OnEntry(() => {
                // Reset channel for new message
                _gmcpByteChannel = Channel.CreateBounded<byte>(new BoundedChannelOptions(8192)
                {
                    FullMode = BoundedChannelFullMode.DropWrite
                });
            });

        TriggerHelper.ForAllTriggersButIAC(t => stateMachine
                .Configure(State.EvaluatingGMCPValue)
                .PermitReentry(t)
                .OnEntryFrom(context.Interpreter.ParameterizedTrigger(t), RegisterGMCPValue));

        TriggerHelper.ForAllTriggersButIAC(t => stateMachine
                .Configure(State.AlmostNegotiatingGMCP)
                .Permit(t, State.EvaluatingGMCPValue));

        stateMachine.Configure(State.EvaluatingGMCPValue)
            .Permit(Trigger.IAC, State.EscapingGMCPValue);

        stateMachine.Configure(State.EscapingGMCPValue)
            .Permit(Trigger.IAC, State.EvaluatingGMCPValue)
            .Permit(Trigger.SE, State.CompletingGMCPValue);

        stateMachine.Configure(State.CompletingGMCPValue)
            .SubstateOf(State.Accepting)
            .OnEntryAsync(async x => await CompleteGMCPNegotiation(x, context));
    }

    /// <inheritdoc />
    protected override ValueTask OnInitializeAsync()
    {
        Context.Logger.LogInformation("GMCP Protocol initialized");
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolEnabledAsync()
    {
        Context.Logger.LogInformation("GMCP Protocol enabled");
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolDisabledAsync()
    {
        Context.Logger.LogInformation("GMCP Protocol disabled");
        _gmcpByteChannel = Channel.CreateBounded<byte>(new BoundedChannelOptions(8192)
        {
            FullMode = BoundedChannelFullMode.DropWrite
        });
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Called by the interpreter when a GMCP message is received.
    /// Internal method that invokes the callback.
    /// </summary>
    internal async ValueTask OnGMCPMessageAsync((string Package, string Info) message)
    {
        if (!IsEnabled)
            return;

        Context.Logger.LogDebug("Received GMCP message: Package={Package}", message.Package);
        
        if (_onGMCPReceived != null)
            await _onGMCPReceived(message).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override ValueTask OnDisposeAsync()
    {
        _gmcpByteChannel = Channel.CreateBounded<byte>(new BoundedChannelOptions(8192)
        {
            FullMode = BoundedChannelFullMode.DropWrite
        });
        return ValueTask.CompletedTask;
    }

    #region State Machine Handlers

    private void RegisterGMCPValue(OneOf<byte, Trigger> b)
    {
        // Try to write to channel; if full (>8KB), byte is dropped (DOS protection)
        _gmcpByteChannel.Writer.TryWrite(b.AsT0);
    }

    private async ValueTask CompleteGMCPNegotiation(StateMachine<State, Trigger>.Transition _, IProtocolContext context)
    {
        // Read all bytes from channel into list
        var gmcpBytes = new List<byte>(256);
        while (_gmcpByteChannel.Reader.TryRead(out var bt))
        {
            gmcpBytes.Add(bt);
            if (gmcpBytes.Count >= 8192)
            {
                context.Logger.LogWarning("GMCP message too large (>8KB), truncating");
                break;
            }
        }

        if (gmcpBytes.Count == 0)
        {
            context.Logger.LogWarning("Empty GMCP message received");
            return;
        }

        const byte space = (byte)' ';  // Literal instead of GetBytes(" ").First()
        var firstSpace = gmcpBytes.FindIndex(x => x == space);
        
        if (firstSpace < 0)
        {
            context.Logger.LogWarning("Invalid GMCP message format (no space separator)");
            return;
        }

        // Use CollectionsMarshal.AsSpan with slicing for zero-copy access
        var gmcpSpan = CollectionsMarshal.AsSpan(gmcpBytes);
        var packageBytes = gmcpSpan[..firstSpace].ToArray();
        var rest = gmcpSpan[(firstSpace + 1)..].ToArray();
        var package = context.CurrentEncoding.GetString(gmcpSpan[..firstSpace]);

        if(package == "MSDP")
        {
            // Call MSDP plugin if available
            var msdpPlugin = context.GetPlugin<MSDPProtocol>();
            if (msdpPlugin != null && msdpPlugin.IsEnabled)
            {
                await msdpPlugin.OnMSDPMessageAsync(context.Interpreter, JsonSerializer.Serialize(Functional.MSDPLibrary.MSDPScan(packageBytes, context.CurrentEncoding)));
            }
        }
        else
        {
            // Call GMCP plugin callback
            if (_onGMCPReceived != null)
            {
                await _onGMCPReceived((Package: package, Info: context.CurrentEncoding.GetString(rest)));
            }
        }
    }

    private async ValueTask WillGMCPAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Connection: {ConnectionState}", "Announcing the server will GMCP");

        await context.SendNegotiationAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.GMCP });
    }

    private async ValueTask DoGMCPAsync(StateMachine<State, Trigger>.Transition _, IProtocolContext context)
    {
        context.Logger.LogDebug("Connection: {ConnectionState}", "Announcing the client can do GMCP");

        await context.SendNegotiationAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.GMCP });
    }

    #endregion
}

/// <summary>
/// MSDP (MUD Server Data Protocol) plugin implementation.
/// This demonstrates the plugin architecture pattern.
/// </summary>
/// <remarks>
/// This protocol optionally accepts configuration. Call <see cref="OnMSDPMessage"/> to set up
/// the callback that will handle MSDP messages if you need to be notified of client messages.
/// </remarks>
[RequiredMethod("OnMSDPMessage", Description = "Configure the callback to handle MSDP messages (optional but recommended)")]
public class MSDPProtocol : TelnetProtocolPluginBase
{
    private const int MaxMessageSize = 8192; // 8KB DOS protection
    private readonly List<byte> _msdpBytes = new();

    private Func<Interpreters.TelnetInterpreter, string, ValueTask>? _onMSDPReceived;

    /// <summary>
    /// Sets the callback that is invoked when an MSDP message is received.
    /// </summary>
    /// <param name="callback">The callback to handle MSDP messages</param>
    /// <returns>This instance for fluent chaining</returns>
    public MSDPProtocol OnMSDPMessage(Func<Interpreters.TelnetInterpreter, string, ValueTask>? callback)
    {
        _onMSDPReceived = callback;
        return this;
    }



    /// <inheritdoc />
    public override Type ProtocolType => typeof(MSDPProtocol);

    /// <inheritdoc />
    public override string ProtocolName => "MSDP (MUD Server Data Protocol)";

    /// <inheritdoc />
    public override IReadOnlyCollection<Type> Dependencies => Array.Empty<Type>();

    /// <inheritdoc />
    public override void ConfigureStateMachine(StateMachine<State, Trigger> stateMachine, IProtocolContext context)
    {
        context.Logger.LogInformation("Configuring MSDP state machine");
        
        // Register MSDP protocol handlers with the context
        context.SetSharedState("MSDP_Protocol", this);
        
        if (context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Server)
        {
            // Server-side MSDP negotiation
            stateMachine.Configure(State.Do)
                .Permit(Trigger.MSDP, State.DoMSDP);

            stateMachine.Configure(State.Dont)
                .Permit(Trigger.MSDP, State.DontMSDP);

            stateMachine.Configure(State.DoMSDP)
                .SubstateOf(State.Accepting)
                .OnEntry(() => context.Logger.LogDebug("Connection: {ConnectionState}", "Client will do MSDP"));

            stateMachine.Configure(State.DontMSDP)
                .SubstateOf(State.Accepting)
                .OnEntry(() => context.Logger.LogDebug("Connection: {ConnectionState}", "Client will not MSDP"));

            context.RegisterInitialNegotiation(async () => await WillMSDPAsync(context));
        }
        else if (context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Client)
        {
            // Client-side MSDP negotiation
            stateMachine.Configure(State.Willing)
                .Permit(Trigger.MSDP, State.WillMSDP);

            stateMachine.Configure(State.Refusing)
                .Permit(Trigger.MSDP, State.WontMSDP);

            stateMachine.Configure(State.WillMSDP)
                .SubstateOf(State.Accepting)
                .OnEntryAsync(async x => await DoMSDPAsync(x, context));

            stateMachine.Configure(State.WontMSDP)
                .SubstateOf(State.Accepting)
                .OnEntry(() => context.Logger.LogDebug("Connection: {ConnectionState}", "Server will not MSDP"));
        }

        // Sub-negotiation states (common to both server and client)
        stateMachine.Configure(State.SubNegotiation)
            .Permit(Trigger.MSDP, State.AlmostNegotiatingMSDP);

        stateMachine.Configure(State.AlmostNegotiatingMSDP)
            .Permit(Trigger.IAC, State.EscapingMSDP)
            .OnEntry(() => {
                // Reset byte collection for new message
                _msdpBytes.Clear();
            });

        // Configure transitions for all non-IAC triggers to capture MSDP values
        TriggerHelper.ForAllTriggersButIAC(t => stateMachine
            .Configure(State.EvaluatingMSDP)
            .PermitReentry(t)
            .OnEntryFrom(context.Interpreter.ParameterizedTrigger(t), CaptureMSDPByte));

        TriggerHelper.ForAllTriggersButIAC(t => stateMachine
            .Configure(State.AlmostNegotiatingMSDP)
            .Permit(t, State.EvaluatingMSDP));

        stateMachine.Configure(State.EvaluatingMSDP)
            .Permit(Trigger.IAC, State.EscapingMSDP);

        stateMachine.Configure(State.EscapingMSDP)
            .Permit(Trigger.IAC, State.EvaluatingMSDP)
            .Permit(Trigger.SE, State.CompletingMSDP);

        stateMachine.Configure(State.CompletingMSDP)
            .SubstateOf(State.Accepting)
            .OnEntryAsync(async x => await CompleteMSDPNegotiation(x, context));
    }

    /// <inheritdoc />
    protected override ValueTask OnInitializeAsync()
    {
        Context.Logger.LogInformation("MSDP Protocol initialized");
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolEnabledAsync()
    {
        Context.Logger.LogInformation("MSDP Protocol enabled");
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolDisabledAsync()
    {
        Context.Logger.LogInformation("MSDP Protocol disabled");
        _msdpBytes.Clear();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Called by the interpreter when an MSDP message is received.
    /// Internal method that invokes the callback.
    /// </summary>
    internal async ValueTask OnMSDPMessageAsync(Interpreters.TelnetInterpreter interpreter, string message)
    {
        if (!IsEnabled)
            return;

        Context.Logger.LogDebug("Received MSDP message");
        
        if (_onMSDPReceived != null)
            await _onMSDPReceived(interpreter, message).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override ValueTask OnDisposeAsync()
    {
        _msdpBytes.Clear();
        return ValueTask.CompletedTask;
    }

    #region State Machine Handlers

    private void CaptureMSDPByte(OneOf<byte, Trigger> b)
    {
        if (!IsEnabled)
            return;

        // DOS protection: enforce max message size
        if (_msdpBytes.Count >= MaxMessageSize)
        {
            Context.Logger.LogWarning("MSDP message exceeded max size of {MaxSize} bytes, truncating", MaxMessageSize);
            return;
        }

        _msdpBytes.Add(b.AsT0);
    }

    private async ValueTask CompleteMSDPNegotiation(StateMachine<State, Trigger>.Transition _, IProtocolContext context)
    {
        if (_msdpBytes.Count == 0)
        {
            context.Logger.LogWarning("Empty MSDP message received");
            return;
        }

        try
        {
            context.Logger.LogDebug("Processing MSDP message with {ByteCount} bytes", _msdpBytes.Count);

            // Parse MSDP bytes using the F# library
            var parsedData = Functional.MSDPLibrary.MSDPScan(_msdpBytes, context.CurrentEncoding);
            var jsonString = JsonSerializer.Serialize(parsedData);

            // Invoke the callback if registered
            if (_onMSDPReceived != null)
            {
                await _onMSDPReceived(context.Interpreter, jsonString);
            }
        }
        catch (Exception ex)
        {
            context.Logger.LogError(ex, "Error processing MSDP message");
        }
        finally
        {
            _msdpBytes.Clear();
        }
    }

    private async ValueTask WillMSDPAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Connection: {ConnectionState}", "Announcing the server will MSDP");
        await context.SendNegotiationAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.MSDP });
    }

    private async ValueTask DoMSDPAsync(StateMachine<State, Trigger>.Transition _, IProtocolContext context)
    {
        context.Logger.LogDebug("Connection: {ConnectionState}", "Announcing the client can do MSDP");
        await context.SendNegotiationAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MSDP });
    }

    #endregion
}
