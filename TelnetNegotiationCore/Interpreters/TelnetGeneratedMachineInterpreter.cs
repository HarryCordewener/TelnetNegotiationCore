using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TelnetNegotiationCore.Machine;
using TelnetNegotiationCore.Plugins;

namespace TelnetNegotiationCore.Interpreters;

/// <summary>
/// The StateAlchemist-generated machine that now drives every protocol's negotiation and subnegotiation.
/// <see cref="UseGeneratedMachine"/> defaults to true; the Stateless machine and the flag itself are a
/// migration-era seam kept only until the last of it is deleted.
/// </summary>
/// <remarks>
/// <c>ConfigureStateMachine</c> still runs for every plugin either way -- it is also where a plugin
/// registers its initial negotiation offer, which has nothing to do with which machine reads bytes off
/// the wire. Only <see cref="FireByteAsync"/> decides that.
/// </remarks>
public partial class TelnetInterpreter
{
    /// <summary>
    /// Drive the generated machine instead of <see cref="TelnetStateMachine"/>. Defaults to false on
    /// this property, but <see cref="Builders.TelnetInterpreterBuilder"/> always sets it true.
    /// </summary>
    internal bool UseGeneratedMachine { get; init; }

    private TelnetCoreMachine? _generatedMachine;

    /// <summary>Option bytes whose negotiation acceptance is wired to their real protocol logic so far.</summary>
    private static readonly Dictionary<byte, Type> s_wiredOptions = new()
    {
        [31] = typeof(Protocols.NAWSProtocol),
        [3] = typeof(Protocols.SuppressGoAheadProtocol),
        [25] = typeof(Protocols.EORProtocol),
        [1] = typeof(Protocols.EchoProtocol),
        [33] = typeof(Protocols.FlowControlProtocol),
        [32] = typeof(Protocols.TerminalSpeedProtocol),
        [35] = typeof(Protocols.XDisplayProtocol),
        [201] = typeof(Protocols.GMCPProtocol),
        [69] = typeof(Protocols.MSDPProtocol),
        [70] = typeof(Protocols.MSSPProtocol),
        [24] = typeof(Protocols.TerminalTypeProtocol),
        [42] = typeof(Protocols.CharsetProtocol),
        [39] = typeof(Protocols.NewEnvironProtocol),
        [36] = typeof(Protocols.EnvironProtocol),
        [91] = typeof(Protocols.MXPProtocol),
        [86] = typeof(Protocols.MCCPProtocol),
        [87] = typeof(Protocols.MCCPProtocol),
        [34] = typeof(Protocols.LineModeProtocol),
        [37] = typeof(Protocols.AuthenticationProtocol),
        [38] = typeof(Protocols.EncryptionProtocol),
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
                switch (owner.PluginManager.GetPlugin(type))
                {
                    case Protocols.NAWSProtocol naws:
                        await naws.OnPeerNegotiatedAsync(verb, Context());
                        return;
                    case Protocols.SuppressGoAheadProtocol sga:
                        await sga.OnPeerNegotiatedAsync(verb, Context());
                        return;
                    case Protocols.EORProtocol eor:
                        await eor.OnPeerNegotiatedAsync(verb, Context());
                        return;
                    case Protocols.EchoProtocol echo:
                        await echo.OnPeerNegotiatedAsync(verb, Context());
                        return;
                    case Protocols.FlowControlProtocol flowControl:
                        await flowControl.OnPeerNegotiatedAsync(verb, Context());
                        return;
                    case Protocols.TerminalSpeedProtocol tspeed:
                        await tspeed.OnPeerNegotiatedAsync(verb, Context());
                        return;
                    case Protocols.XDisplayProtocol xdisploc:
                        await xdisploc.OnPeerNegotiatedAsync(verb, Context());
                        return;
                    case Protocols.GMCPProtocol gmcp:
                        await gmcp.OnPeerNegotiatedAsync(verb, Context());
                        return;
                    case Protocols.MSDPProtocol msdp:
                        await msdp.OnPeerNegotiatedAsync(verb, Context());
                        return;
                    case Protocols.MSSPProtocol mssp:
                        await mssp.OnPeerNegotiatedAsync(verb, Context());
                        return;
                    case Protocols.TerminalTypeProtocol ttype:
                        await ttype.OnPeerNegotiatedAsync(verb, Context());
                        return;
                    case Protocols.CharsetProtocol charset:
                        await charset.OnPeerNegotiatedAsync(verb, Context());
                        return;
                    case Protocols.NewEnvironProtocol newEnviron:
                        await newEnviron.OnPeerNegotiatedAsync(verb, Context());
                        return;
                    case Protocols.EnvironProtocol environ:
                        await environ.OnPeerNegotiatedAsync(verb, Context());
                        return;
                    case Protocols.MXPProtocol mxp:
                        await mxp.OnPeerNegotiatedAsync(verb, Context());
                        return;
                    case Protocols.MCCPProtocol mccp:
                        await mccp.OnPeerNegotiatedAsync(verb, option, Context());
                        return;
                    case Protocols.LineModeProtocol lineMode:
                        await lineMode.OnPeerNegotiatedAsync(verb, Context());
                        return;
                    case Protocols.AuthenticationProtocol authentication:
                        await authentication.OnPeerNegotiatedAsync(verb, Context());
                        return;
                    case Protocols.EncryptionProtocol encryption:
                        await encryption.OnPeerNegotiatedAsync(verb, Context());
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
        // Same discriminator-byte restoration AuthenticationSendAsync/AuthenticationIsAsync need --
        // ENCRYPT shares AUTHENTICATION's exact Stateless capture shape (RFC 2946 mirrors RFC 2941's).
        public override ValueTask EncryptionSendAsync(byte[] data)
        {
            if (owner.PluginManager?.GetPlugin(typeof(Protocols.EncryptionProtocol)) is Protocols.EncryptionProtocol encryption)
            {
                return encryption.ProcessEncryptionSupportFromBytesAsync(PrependAuthCommand(1, data), Context());
            }

            return default;
        }

        public override ValueTask EncryptionIsAsync(byte[] data)
        {
            if (owner.PluginManager?.GetPlugin(typeof(Protocols.EncryptionProtocol)) is Protocols.EncryptionProtocol encryption)
            {
                return encryption.ProcessEncryptionIsFromBytesAsync(PrependAuthCommand(0, data), Context());
            }

            return default;
        }
        public override ValueTask MsdpStartedAsync()
        {
            (owner.PluginManager?.GetPlugin(typeof(Protocols.MSDPProtocol)) as Protocols.MSDPProtocol)?.StartMsdpMessage();
            return default;
        }

        public override ValueTask MsdpDataAsync(ReadOnlyMemory<byte> data)
        {
            (owner.PluginManager?.GetPlugin(typeof(Protocols.MSDPProtocol)) as Protocols.MSDPProtocol)?.AppendMsdpBytes(data);
            return default;
        }

        public override ValueTask MsdpEndedAsync()
        {
            if (owner.PluginManager?.GetPlugin(typeof(Protocols.MSDPProtocol)) is Protocols.MSDPProtocol msdp)
            {
                return msdp.CompleteMsdpAsync(Context());
            }

            return default;
        }
        public override ValueTask MsspStartedAsync()
        {
            (owner.PluginManager?.GetPlugin(typeof(Protocols.MSSPProtocol)) as Protocols.MSSPProtocol)?.StartMsspMessage();
            return default;
        }

        public override ValueTask MsspVariableMarkerAsync()
        {
            (owner.PluginManager?.GetPlugin(typeof(Protocols.MSSPProtocol)) as Protocols.MSSPProtocol)?.OnMsspVariableMarker(Context());
            return default;
        }

        public override ValueTask MsspValueMarkerAsync()
        {
            (owner.PluginManager?.GetPlugin(typeof(Protocols.MSSPProtocol)) as Protocols.MSSPProtocol)?.OnMsspValueMarker(Context());
            return default;
        }

        public override ValueTask MsspDataAsync(ReadOnlyMemory<byte> data)
        {
            (owner.PluginManager?.GetPlugin(typeof(Protocols.MSSPProtocol)) as Protocols.MSSPProtocol)?.AppendMsspBytes(data);
            return default;
        }

        public override ValueTask MsspEndedAsync()
        {
            if (owner.PluginManager?.GetPlugin(typeof(Protocols.MSSPProtocol)) is Protocols.MSSPProtocol mssp)
            {
                return mssp.CompleteMsspAsync(Context());
            }

            return default;
        }
        public override ValueTask FlowControlAsync(byte command)
        {
            if (owner.PluginManager?.GetPlugin(typeof(Protocols.FlowControlProtocol)) is Protocols.FlowControlProtocol flowControl)
            {
                return flowControl.OnFlowControlCommandAsync(command, Context());
            }

            return default;
        }
        public override ValueTask LineModeAsync(byte kind, byte[] data)
        {
            if (owner.PluginManager?.GetPlugin(typeof(Protocols.LineModeProtocol)) is Protocols.LineModeProtocol lineMode)
            {
                return lineMode.CompleteLineModeFromBytesAsync(kind, data, Context());
            }

            return default;
        }
        public override ValueTask GmcpStartedAsync()
        {
            (owner.PluginManager?.GetPlugin(typeof(Protocols.GMCPProtocol)) as Protocols.GMCPProtocol)?.StartGmcpMessage();
            return default;
        }

        public override ValueTask GmcpDataAsync(ReadOnlyMemory<byte> data)
        {
            (owner.PluginManager?.GetPlugin(typeof(Protocols.GMCPProtocol)) as Protocols.GMCPProtocol)?.AppendGmcpBytes(data);
            return default;
        }

        public override ValueTask GmcpEndedAsync()
        {
            if (owner.PluginManager?.GetPlugin(typeof(Protocols.GMCPProtocol)) is Protocols.GMCPProtocol gmcp)
            {
                return gmcp.CompleteGmcpAsync(Context());
            }

            return default;
        }
        public override ValueTask CharsetRequestAsync(byte[] text)
        {
            if (owner.PluginManager?.GetPlugin(typeof(Protocols.CharsetProtocol)) is Protocols.CharsetProtocol charset)
            {
                return charset.CompleteCharsetRequestFromBytesAsync(text, Context());
            }

            return default;
        }

        public override ValueTask CharsetAcceptedAsync(byte[] text)
        {
            if (owner.PluginManager?.GetPlugin(typeof(Protocols.CharsetProtocol)) is Protocols.CharsetProtocol charset)
            {
                return charset.CompleteAcceptedCharsetFromBytesAsync(text, Context());
            }

            return default;
        }

        // RFC 2066's own REJECTED/TTABLE_REJECTED/TTABLE_ACK/TTABLE_NAK carry no payload of their own to
        // process on the receiving end -- the original Stateless configuration has no handler for any of
        // them either, only the state transition that consumes their bytes.
        public override ValueTask CharsetRejectedAsync() => default;

        public override ValueTask CharsetTTableStartedAsync()
        {
            (owner.PluginManager?.GetPlugin(typeof(Protocols.CharsetProtocol)) as Protocols.CharsetProtocol)?.StartTTableMessage();
            return default;
        }

        public override ValueTask CharsetTTableDataAsync(ReadOnlyMemory<byte> data)
        {
            (owner.PluginManager?.GetPlugin(typeof(Protocols.CharsetProtocol)) as Protocols.CharsetProtocol)?.AppendTTableBytes(data);
            return default;
        }

        public override ValueTask CharsetTTableEndedAsync()
        {
            if (owner.PluginManager?.GetPlugin(typeof(Protocols.CharsetProtocol)) is Protocols.CharsetProtocol charset)
            {
                return charset.CompleteTTableFromBufferAsync(Context());
            }

            return default;
        }

        public override ValueTask CharsetTTableRejectedAsync() => default;
        public override ValueTask CharsetTTableAckAsync() => default;
        public override ValueTask CharsetTTableNakAsync() => default;
        // The module hands over the bytes after the IS/SEND discriminator; the plugin's own
        // Stateless-era capture included that discriminator as the callback data's first byte (the
        // byte value doubling as both the Stateless trigger that entered the capturing state and the
        // first trigger OnEntryFrom captured into it) -- AuthenticationTests.cs locks that shape in
        // (e.g. ServerCanReceiveAndProcessAuthenticationResponse expects data[0] to be the IS command),
        // so it is restored here rather than changed out from under existing consumers.
        public override ValueTask AuthenticationSendAsync(byte[] data)
        {
            if (owner.PluginManager?.GetPlugin(typeof(Protocols.AuthenticationProtocol)) is Protocols.AuthenticationProtocol authentication)
            {
                return authentication.RespondToAuthenticationSendFromBytesAsync(PrependAuthCommand(1, data), Context());
            }

            return default;
        }

        public override ValueTask AuthenticationIsAsync(byte[] data)
        {
            if (owner.PluginManager?.GetPlugin(typeof(Protocols.AuthenticationProtocol)) is Protocols.AuthenticationProtocol authentication)
            {
                return authentication.ProcessAuthenticationResponseFromBytesAsync(PrependAuthCommand(0, data), Context());
            }

            return default;
        }

        private static byte[] PrependAuthCommand(byte command, byte[] data)
        {
            var withCommand = new byte[data.Length + 1];
            withCommand[0] = command;
            Array.Copy(data, 0, withCommand, 1, data.Length);
            return withCommand;
        }
        public override ValueTask MxpStartedAsync()
        {
            if (owner.PluginManager?.GetPlugin(typeof(Protocols.MXPProtocol)) is Protocols.MXPProtocol mxp)
            {
                return mxp.StartMxpModeAsync(Context());
            }

            return default;
        }

        public override ValueTask GoAheadAsync()
        {
            if (owner.PluginManager?.GetPlugin(typeof(Protocols.SuppressGoAheadProtocol)) is Protocols.SuppressGoAheadProtocol sga)
            {
                return sga.OnBareGoAheadAsync(Context());
            }

            return default;
        }

        public override ValueTask EorAsync()
        {
            if (owner.PluginManager?.GetPlugin(typeof(Protocols.EORProtocol)) is Protocols.EORProtocol eor)
            {
                return eor.OnBareEorAsync();
            }

            return default;
        }
        public override ValueTask TerminalTypeRequestedAsync()
        {
            if (owner.PluginManager?.GetPlugin(typeof(Protocols.TerminalTypeProtocol)) is Protocols.TerminalTypeProtocol ttype)
            {
                return ttype.OnRequestedAsync(Context());
            }

            return default;
        }

        public override ValueTask TerminalTypeAsync(byte[] text)
        {
            if (owner.PluginManager?.GetPlugin(typeof(Protocols.TerminalTypeProtocol)) is Protocols.TerminalTypeProtocol ttype)
            {
                return ttype.CompleteTerminalTypeFromBytesAsync(text, Context());
            }

            return default;
        }
        public override ValueTask NewEnvironStartedAsync(byte command)
        {
            if (owner.PluginManager?.GetPlugin(typeof(Protocols.NewEnvironProtocol)) is Protocols.NewEnvironProtocol newEnviron)
            {
                return newEnviron.OnNewEnvironStartedAsync(command, Context());
            }

            return default;
        }

        public override ValueTask NewEnvironVarAsync()
        {
            if (owner.PluginManager?.GetPlugin(typeof(Protocols.NewEnvironProtocol)) is Protocols.NewEnvironProtocol newEnviron)
            {
                return newEnviron.OnNewEnvironVarMarkerAsync(Context());
            }

            return default;
        }

        public override ValueTask NewEnvironUserVarAsync()
        {
            if (owner.PluginManager?.GetPlugin(typeof(Protocols.NewEnvironProtocol)) is Protocols.NewEnvironProtocol newEnviron)
            {
                return newEnviron.OnNewEnvironUserVarMarkerAsync(Context());
            }

            return default;
        }

        public override ValueTask NewEnvironValueAsync()
        {
            if (owner.PluginManager?.GetPlugin(typeof(Protocols.NewEnvironProtocol)) is Protocols.NewEnvironProtocol newEnviron)
            {
                return newEnviron.OnNewEnvironValueMarkerAsync(Context());
            }

            return default;
        }

        public override ValueTask NewEnvironDataAsync(ReadOnlyMemory<byte> data)
        {
            if (owner.PluginManager?.GetPlugin(typeof(Protocols.NewEnvironProtocol)) is Protocols.NewEnvironProtocol newEnviron)
            {
                return newEnviron.OnNewEnvironDataAsync(data, Context());
            }

            return default;
        }

        public override ValueTask NewEnvironEndedAsync()
        {
            if (owner.PluginManager?.GetPlugin(typeof(Protocols.NewEnvironProtocol)) is Protocols.NewEnvironProtocol newEnviron)
            {
                return newEnviron.OnNewEnvironEndedAsync(Context());
            }

            return default;
        }
        public override ValueTask XDisplayLocationRequestedAsync()
        {
            if (owner.PluginManager?.GetPlugin(typeof(Protocols.XDisplayProtocol)) is Protocols.XDisplayProtocol xdisploc)
            {
                return xdisploc.OnRequestedAsync(Context());
            }

            return default;
        }

        public override ValueTask XDisplayLocationAsync(byte[] text)
        {
            if (owner.PluginManager?.GetPlugin(typeof(Protocols.XDisplayProtocol)) is Protocols.XDisplayProtocol xdisploc)
            {
                return xdisploc.CompleteXDisplayLocationFromBytesAsync(text, Context());
            }

            return default;
        }
        public override ValueTask Mccp2MarkerAsync()
        {
            if (owner.PluginManager?.GetPlugin(typeof(Protocols.MCCPProtocol)) is Protocols.MCCPProtocol mccp)
            {
                return mccp.OnMccp2MarkerAsync(Context());
            }

            return default;
        }

        public override ValueTask Mccp3MarkerAsync()
        {
            if (owner.PluginManager?.GetPlugin(typeof(Protocols.MCCPProtocol)) is Protocols.MCCPProtocol mccp)
            {
                return mccp.OnMccp3MarkerAsync(Context());
            }

            return default;
        }

        public override ValueTask Mccp1MarkerAsync()
        {
            if (owner.PluginManager?.GetPlugin(typeof(Protocols.MCCPProtocol)) is Protocols.MCCPProtocol mccp)
            {
                return mccp.OnMccp1MarkerAsync(Context());
            }

            return default;
        }
        public override ValueTask EnvironStartedAsync(byte command)
        {
            if (owner.PluginManager?.GetPlugin(typeof(Protocols.EnvironProtocol)) is Protocols.EnvironProtocol environ)
            {
                return environ.OnEnvironStartedAsync(command, Context());
            }

            return default;
        }

        public override ValueTask EnvironVarAsync()
        {
            if (owner.PluginManager?.GetPlugin(typeof(Protocols.EnvironProtocol)) is Protocols.EnvironProtocol environ)
            {
                return environ.OnEnvironVarMarkerAsync(Context());
            }

            return default;
        }

        public override ValueTask EnvironValueAsync()
        {
            if (owner.PluginManager?.GetPlugin(typeof(Protocols.EnvironProtocol)) is Protocols.EnvironProtocol environ)
            {
                return environ.OnEnvironValueMarkerAsync(Context());
            }

            return default;
        }

        public override ValueTask EnvironDataAsync(ReadOnlyMemory<byte> data)
        {
            if (owner.PluginManager?.GetPlugin(typeof(Protocols.EnvironProtocol)) is Protocols.EnvironProtocol environ)
            {
                return environ.OnEnvironDataAsync(data, Context());
            }

            return default;
        }

        public override ValueTask EnvironEndedAsync()
        {
            if (owner.PluginManager?.GetPlugin(typeof(Protocols.EnvironProtocol)) is Protocols.EnvironProtocol environ)
            {
                return environ.OnEnvironEndedAsync(Context());
            }

            return default;
        }
        public override ValueTask TerminalSpeedRequestedAsync()
        {
            if (owner.PluginManager?.GetPlugin(typeof(Protocols.TerminalSpeedProtocol)) is Protocols.TerminalSpeedProtocol tspeed)
            {
                return tspeed.OnRequestedAsync(Context());
            }

            return default;
        }

        public override ValueTask TerminalSpeedAsync(byte[] text)
        {
            if (owner.PluginManager?.GetPlugin(typeof(Protocols.TerminalSpeedProtocol)) is Protocols.TerminalSpeedProtocol tspeed)
            {
                return tspeed.CompleteTerminalSpeedFromBytesAsync(text, Context());
            }

            return default;
        }

        public override ValueTask WindowSizeAsync(int width, int height)
        {
            if (owner.PluginManager?.GetPlugin(typeof(Protocols.NAWSProtocol)) is Protocols.NAWSProtocol naws)
            {
                return naws.OnWindowSizeAsync(width, height, Context());
            }

            return default;
        }

        /// <summary>
        /// A protocol's own methods take <see cref="IProtocolContext"/>; this is the one this
        /// interpreter already owns -- the same instance <see cref="Builders.TelnetInterpreterBuilder.BuildAsync"/>
        /// created and populated (<c>WithClientIdentity</c>'s shared state included) before any plugin
        /// configured itself, not a fresh one. A second instance would carry an empty shared-state
        /// dictionary of its own -- <see cref="Plugins.ProtocolContext"/> keeps it per instance -- so a
        /// protocol reading, say, the client identity through this seam would silently see none.
        /// </summary>
        private IProtocolContext Context() => owner.SharedProtocolContext!;
    }

    /// <summary>
    /// Set once by <see cref="Builders.TelnetInterpreterBuilder.BuildAsync"/>, before
    /// <see cref="StartGeneratedMachineAsync"/> runs, to the same <see cref="Plugins.ProtocolContext"/>
    /// every plugin was configured and initialized against.
    /// </summary>
    internal IProtocolContext? SharedProtocolContext { get; set; }
}
