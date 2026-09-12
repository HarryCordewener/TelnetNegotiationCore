using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TelnetNegotiationCore.Machine;
using TelnetNegotiationCore.Plugins;

namespace TelnetNegotiationCore.Interpreters;

/// <summary>
/// The seam the migration design calls for: the generated machine, switched on behind a flag, running
/// alongside the Stateless one rather than instead of it. <see cref="UseGeneratedMachine"/> defaults to
/// false, so nothing here changes any existing behaviour unless a caller opts in.
/// </summary>
/// <remarks>
/// <c>ConfigureStateMachine</c> still runs for every plugin either way — it is also where a plugin registers
/// its initial negotiation and its shared state, which have nothing to do with which machine reads bytes off
/// the wire. Only <see cref="FireByteAsync"/> decides that, and only for the connections this is turned on for.
/// <para>
/// This is a first slice, proven end to end against the interpreter's own tests rather than the sample
/// harness: the core framing and NAWS. Every other option this library structurally parses is still routed
/// through <see cref="GeneratedContext"/>'s parser, correctly, but negotiation <em>acceptance</em> for those
/// is not yet wired to their real logic, so <see cref="GeneratedContext.NegotiateAsync"/> refuses them by
/// number rather than silently doing nothing — the same answer an unregistered plugin gets today.
/// </para>
/// </remarks>
public partial class TelnetInterpreter
{
    /// <summary>Drive the generated machine instead of <see cref="TelnetStateMachine"/>. Off by default.</summary>
    internal bool UseGeneratedMachine { get; init; }

    private TelnetCoreMachine? _generatedMachine;

    /// <summary>Option bytes whose negotiation acceptance is wired to their real protocol logic so far.</summary>
    private static readonly Dictionary<byte, Type> s_wiredOptions = new()
    {
        [31] = typeof(Protocols.NAWSProtocol),
    };

    /// <summary>Builds and starts the generated machine. Called once, after plugins have configured themselves.</summary>
    internal async ValueTask StartGeneratedMachineAsync()
    {
        _generatedMachine = new TelnetCoreMachine(new GeneratedContext(this));
        await _generatedMachine.StartAsync();
    }

    /// <summary>Fires one byte into the generated machine. Errors are handled the same way <see cref="FireByteAsync"/> handles Stateless's.</summary>
    private async ValueTask FireGeneratedByteAsync(byte bt)
    {
        try
        {
            await _generatedMachine!.FireAsync(bt);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Dropping a byte that could not be processed by the generated machine. Connection continues.");
        }
    }

    /// <summary>
    /// Bridges the generated machine to this interpreter's own buffer, callbacks and plugins — the same
    /// things <see cref="Plugins.ProtocolContext"/> bridges Stateless's handlers to, reused rather than
    /// duplicated. Nested so it can reach this interpreter's private members the way any other part of it
    /// can, the same reason <c>TelnetSafeInterpreter.cs</c> and its neighbours are partial classes of it
    /// rather than separate collaborators.
    /// </summary>
    private sealed class GeneratedContext(TelnetInterpreter owner) : TelnetCoreContext
    {
        public override void Write(ReadOnlySpan<byte> text)
        {
            foreach (var b in text)
            {
                if (owner.WriteToBufferAndAdvance(b) && owner.CallbackOnByteAsync is { } callback)
                {
                    // Write is synchronous by design -- text is the hot path -- so a callback that
                    // does not complete synchronously is waited for rather than reordered.
                    callback(b, owner.CurrentEncoding).GetAwaiter().GetResult();
                }
            }
        }

        public override ValueTask SubmitAsync() => owner.WriteToOutput();

        public override async ValueTask NegotiateAsync(byte verb, byte option)
        {
            if (s_wiredOptions.TryGetValue(option, out var type) && owner.PluginManager?.IsPluginEnabled(type) == true)
            {
                var plugin = owner.PluginManager.GetPlugin(type);
                if (plugin is Protocols.NAWSProtocol naws)
                {
                    await naws.OnPeerNegotiatedAsync(verb, Context());
                    return;
                }
            }

            await RefuseAsync(verb, option);
        }

        /// <summary>Refuses an option by its own number, the same answer an unclaimed one gets today.</summary>
        private async ValueTask RefuseAsync(byte verb, byte option)
        {
            const byte will = 251, wont = 252, doVerb = 253;
            if (verb != will && verb != doVerb)
            {
                // WONT and DONT need no answer; refusing a refusal is not a telnet exchange.
                return;
            }

            var refusal = verb == doVerb ? wont : (byte)254;
            owner._logger.LogDebug("Connection: refusing option {Option} with {Refusal}.", option, refusal);
            await owner.WriteToNetworkAsync((byte[])[255, refusal, option]);
        }

        public override ValueTask SubNegotiatedAsync(byte option, ReadOnlyMemory<byte> payload) => default;

        // Structural parsing for these is in place and tested against the sample harness; negotiation
        // acceptance for them is not wired to real protocol logic yet, so NegotiateAsync refuses their
        // option before any of these are ever reached from a real peer -- matching an unregistered
        // plugin's answer today. Kept as explicit no-ops rather than left unimplemented so the class
        // compiles as what it honestly is: a context that speaks for every option this library parses,
        // with only NAWS's answer live so far.
        public override ValueTask EncryptionSendAsync(byte[] data) => default;
        public override ValueTask EncryptionIsAsync(byte[] data) => default;
        public override ValueTask MsdpDataAsync(ReadOnlyMemory<byte> data) => default;
        public override ValueTask MsdpEndedAsync() => default;
        public override ValueTask MsspStartedAsync() => default;
        public override ValueTask MsspVariableMarkerAsync() => default;
        public override ValueTask MsspValueMarkerAsync() => default;
        public override ValueTask MsspDataAsync(ReadOnlyMemory<byte> data) => default;
        public override ValueTask MsspEndedAsync() => default;
        public override ValueTask FlowControlAsync(byte command) => default;
        public override ValueTask LineModeAsync(byte kind, byte[] data) => default;
        public override ValueTask GmcpDataAsync(ReadOnlyMemory<byte> data) => default;
        public override ValueTask GmcpEndedAsync() => default;
        public override ValueTask CharsetRequestAsync(byte[] text) => default;
        public override ValueTask CharsetAcceptedAsync(byte[] text) => default;
        public override ValueTask CharsetRejectedAsync() => default;
        public override ValueTask CharsetTTableAsync(byte[] text) => default;
        public override ValueTask CharsetTTableRejectedAsync() => default;
        public override ValueTask CharsetTTableAckAsync() => default;
        public override ValueTask CharsetTTableNakAsync() => default;
        public override ValueTask AuthenticationSendAsync(byte[] data) => default;
        public override ValueTask AuthenticationIsAsync(byte[] data) => default;
        public override ValueTask MxpStartedAsync() => default;
        public override ValueTask TerminalTypeRequestedAsync() => default;
        public override ValueTask TerminalTypeAsync(byte[] text) => default;
        public override ValueTask NewEnvironStartedAsync(byte command) => default;
        public override ValueTask NewEnvironVarAsync() => default;
        public override ValueTask NewEnvironUserVarAsync() => default;
        public override ValueTask NewEnvironValueAsync() => default;
        public override ValueTask NewEnvironDataAsync(ReadOnlyMemory<byte> data) => default;
        public override ValueTask NewEnvironEndedAsync() => default;
        public override ValueTask XDisplayLocationRequestedAsync() => default;
        public override ValueTask XDisplayLocationAsync(byte[] text) => default;
        public override ValueTask Mccp2MarkerAsync() => default;
        public override ValueTask Mccp3MarkerAsync() => default;
        public override ValueTask Mccp1MarkerAsync() => default;
        public override ValueTask EnvironStartedAsync(byte command) => default;
        public override ValueTask EnvironVarAsync() => default;
        public override ValueTask EnvironValueAsync() => default;
        public override ValueTask EnvironDataAsync(ReadOnlyMemory<byte> data) => default;
        public override ValueTask EnvironEndedAsync() => default;
        public override ValueTask TerminalSpeedRequestedAsync() => default;
        public override ValueTask TerminalSpeedAsync(byte[] text) => default;

        public override ValueTask WindowSizeAsync(int width, int height)
        {
            if (owner.PluginManager?.GetPlugin(typeof(Protocols.NAWSProtocol)) is Protocols.NAWSProtocol naws)
            {
                return naws.OnWindowSizeAsync(width, height, Context());
            }

            return default;
        }

        /// <summary>A protocol's own methods take <see cref="IProtocolContext"/>; this is the one this interpreter already owns.</summary>
        private IProtocolContext Context() => owner._generatedProtocolContext ??= new ProtocolContext(owner, owner.PluginManager!, owner._logger);
    }

    private IProtocolContext? _generatedProtocolContext;
}
