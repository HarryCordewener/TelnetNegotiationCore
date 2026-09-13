using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TelnetNegotiationCore.Machine;
using TelnetNegotiationCore.Plugins;

namespace TelnetNegotiationCore.Interpreters;

/// <summary>
/// The StateAlchemist-generated machine that now drives every protocol's negotiation and subnegotiation.
/// Every interpreter built through <see cref="Builders.TelnetInterpreterBuilder"/> uses it, because that
/// builder always sets <see cref="UseGeneratedMachine"/> true regardless of the bare property's own
/// <see langword="false"/> default; the Stateless machine and the flag itself are a migration-era seam
/// kept only until the last of it is deleted.
/// </summary>
/// <remarks>
/// <c>ConfigureStateMachine</c> still runs for every plugin either way -- it is also where a plugin
/// registers its initial negotiation offer, which has nothing to do with which machine reads bytes off
/// the wire. Only <see cref="FireGeneratedByteAsync"/> decides that.
/// </remarks>
public partial class TelnetInterpreter
{
    /// <summary>
    /// Drive the generated machine instead of leaving it unused. Defaults to <see langword="false"/> on
    /// this bare property; <see cref="Builders.TelnetInterpreterBuilder"/> always sets it
    /// <see langword="true"/>, which is why every interpreter built the normal way uses the generated
    /// machine regardless of this default.
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

        // The catch-all for a subnegotiation this class has no dedicated handler for: an option the
        // library structurally parses but does not otherwise act on, or one of the "malformed command
        // byte" recoveries (see the various IgnoreMalformed transitions across Machine/*.cs) that route
        // into the shared SubNegotiating/EndSubNegotiation discard states rather than wedging the
        // connection. Nothing to do in either case.
        public override ValueTask SubNegotiatedAsync(byte option, ReadOnlyMemory<byte> payload) => default;

        // Same discriminator-byte restoration AuthenticationSendAsync/AuthenticationIsAsync need --
        // ENCRYPT shares AUTHENTICATION's exact Stateless capture shape (RFC 2946 mirrors RFC 2941's).
        public override ValueTask EncryptionSendAsync(byte[] data)
        {
            if (TryGetEnabledPlugin<Protocols.EncryptionProtocol>(out var encryption))
            {
                return encryption.ProcessEncryptionSupportFromBytesAsync(PrependAuthCommand(1, data), Context());
            }

            return default;
        }

        public override ValueTask EncryptionIsAsync(byte[] data)
        {
            if (TryGetEnabledPlugin<Protocols.EncryptionProtocol>(out var encryption))
            {
                return encryption.ProcessEncryptionIsFromBytesAsync(PrependAuthCommand(0, data), Context());
            }

            return default;
        }

        public override ValueTask EncryptionStartAsync(byte[] keyId)
        {
            if (TryGetEnabledPlugin<Protocols.EncryptionProtocol>(out var encryption))
            {
                return encryption.ProcessEncryptionStartFromBytesAsync(keyId, Context());
            }

            return default;
        }

        public override ValueTask EncryptionEndAsync()
        {
            if (TryGetEnabledPlugin<Protocols.EncryptionProtocol>(out var encryption))
            {
                return encryption.ProcessEncryptionEndFromBytesAsync(Context());
            }

            return default;
        }

        public override ValueTask MsdpStartedAsync()
        {
            if (TryGetEnabledPlugin<Protocols.MSDPProtocol>(out var msdp))
            {
                msdp.StartMsdpMessage();
            }

            return default;
        }

        public override ValueTask MsdpDataAsync(ReadOnlyMemory<byte> data)
        {
            if (TryGetEnabledPlugin<Protocols.MSDPProtocol>(out var msdp))
            {
                msdp.AppendMsdpBytes(data);
            }

            return default;
        }

        public override ValueTask MsdpEndedAsync()
        {
            if (TryGetEnabledPlugin<Protocols.MSDPProtocol>(out var msdp))
            {
                return msdp.CompleteMsdpAsync(Context());
            }

            return default;
        }
        public override ValueTask MsspStartedAsync()
        {
            if (TryGetEnabledPlugin<Protocols.MSSPProtocol>(out var mssp))
            {
                mssp.StartMsspMessage();
            }

            return default;
        }

        public override ValueTask MsspVariableMarkerAsync()
        {
            if (TryGetEnabledPlugin<Protocols.MSSPProtocol>(out var mssp))
            {
                mssp.OnMsspVariableMarker(Context());
            }

            return default;
        }

        public override ValueTask MsspValueMarkerAsync()
        {
            if (TryGetEnabledPlugin<Protocols.MSSPProtocol>(out var mssp))
            {
                mssp.OnMsspValueMarker(Context());
            }

            return default;
        }

        public override ValueTask MsspDataAsync(ReadOnlyMemory<byte> data)
        {
            if (TryGetEnabledPlugin<Protocols.MSSPProtocol>(out var mssp))
            {
                mssp.AppendMsspBytes(data);
            }

            return default;
        }

        public override ValueTask MsspEndedAsync()
        {
            if (TryGetEnabledPlugin<Protocols.MSSPProtocol>(out var mssp))
            {
                return mssp.CompleteMsspAsync(Context());
            }

            return default;
        }
        public override ValueTask FlowControlAsync(byte command)
        {
            if (TryGetEnabledPlugin<Protocols.FlowControlProtocol>(out var flowControl))
            {
                return flowControl.OnFlowControlCommandAsync(command, Context());
            }

            return default;
        }
        public override ValueTask LineModeAsync(byte kind, byte[] data)
        {
            if (TryGetEnabledPlugin<Protocols.LineModeProtocol>(out var lineMode))
            {
                return lineMode.CompleteLineModeFromBytesAsync(kind, data, Context());
            }

            return default;
        }
        public override ValueTask GmcpStartedAsync()
        {
            if (TryGetEnabledPlugin<Protocols.GMCPProtocol>(out var gmcp))
            {
                gmcp.StartGmcpMessage();
            }

            return default;
        }

        public override ValueTask GmcpDataAsync(ReadOnlyMemory<byte> data)
        {
            if (TryGetEnabledPlugin<Protocols.GMCPProtocol>(out var gmcp))
            {
                gmcp.AppendGmcpBytes(data);
            }

            return default;
        }

        public override ValueTask GmcpEndedAsync()
        {
            if (TryGetEnabledPlugin<Protocols.GMCPProtocol>(out var gmcp))
            {
                return gmcp.CompleteGmcpAsync(Context());
            }

            return default;
        }
        public override ValueTask CharsetRequestAsync(byte[] text)
        {
            if (TryGetEnabledPlugin<Protocols.CharsetProtocol>(out var charset))
            {
                return charset.CompleteCharsetRequestFromBytesAsync(text, Context());
            }

            return default;
        }

        public override ValueTask CharsetAcceptedAsync(byte[] text)
        {
            if (TryGetEnabledPlugin<Protocols.CharsetProtocol>(out var charset))
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
            if (TryGetEnabledPlugin<Protocols.CharsetProtocol>(out var charset))
            {
                charset.StartTTableMessage();
            }

            return default;
        }

        public override ValueTask CharsetTTableDataAsync(ReadOnlyMemory<byte> data)
        {
            if (TryGetEnabledPlugin<Protocols.CharsetProtocol>(out var charset))
            {
                charset.AppendTTableBytes(data);
            }

            return default;
        }

        public override ValueTask CharsetTTableEndedAsync()
        {
            if (TryGetEnabledPlugin<Protocols.CharsetProtocol>(out var charset))
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
            if (TryGetEnabledPlugin<Protocols.AuthenticationProtocol>(out var authentication))
            {
                return authentication.RespondToAuthenticationSendFromBytesAsync(PrependAuthCommand(1, data), Context());
            }

            return default;
        }

        public override ValueTask AuthenticationIsAsync(byte[] data)
        {
            if (TryGetEnabledPlugin<Protocols.AuthenticationProtocol>(out var authentication))
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
            if (TryGetEnabledPlugin<Protocols.MXPProtocol>(out var mxp))
            {
                return mxp.StartMxpModeAsync(Context());
            }

            return default;
        }

        public override ValueTask GoAheadAsync()
        {
            if (TryGetEnabledPlugin<Protocols.SuppressGoAheadProtocol>(out var sga))
            {
                return sga.OnBareGoAheadAsync(Context());
            }

            return default;
        }

        public override ValueTask EorAsync()
        {
            if (TryGetEnabledPlugin<Protocols.EORProtocol>(out var eor))
            {
                return eor.OnBareEorAsync();
            }

            return default;
        }
        public override ValueTask TerminalTypeRequestedAsync()
        {
            if (TryGetEnabledPlugin<Protocols.TerminalTypeProtocol>(out var ttype))
            {
                return ttype.OnRequestedAsync(Context());
            }

            return default;
        }

        public override ValueTask TerminalTypeAsync(byte[] text)
        {
            if (TryGetEnabledPlugin<Protocols.TerminalTypeProtocol>(out var ttype))
            {
                return ttype.CompleteTerminalTypeFromBytesAsync(text, Context());
            }

            return default;
        }
        public override ValueTask NewEnvironStartedAsync(byte command)
        {
            if (TryGetEnabledPlugin<Protocols.NewEnvironProtocol>(out var newEnviron))
            {
                return newEnviron.OnNewEnvironStartedAsync(command, Context());
            }

            return default;
        }

        public override ValueTask NewEnvironVarAsync()
        {
            if (TryGetEnabledPlugin<Protocols.NewEnvironProtocol>(out var newEnviron))
            {
                return newEnviron.OnNewEnvironVarMarkerAsync(Context());
            }

            return default;
        }

        public override ValueTask NewEnvironUserVarAsync()
        {
            if (TryGetEnabledPlugin<Protocols.NewEnvironProtocol>(out var newEnviron))
            {
                return newEnviron.OnNewEnvironUserVarMarkerAsync(Context());
            }

            return default;
        }

        public override ValueTask NewEnvironValueAsync()
        {
            if (TryGetEnabledPlugin<Protocols.NewEnvironProtocol>(out var newEnviron))
            {
                return newEnviron.OnNewEnvironValueMarkerAsync(Context());
            }

            return default;
        }

        public override ValueTask NewEnvironDataAsync(ReadOnlyMemory<byte> data)
        {
            if (TryGetEnabledPlugin<Protocols.NewEnvironProtocol>(out var newEnviron))
            {
                return newEnviron.OnNewEnvironDataAsync(data, Context());
            }

            return default;
        }

        public override ValueTask NewEnvironEndedAsync()
        {
            if (TryGetEnabledPlugin<Protocols.NewEnvironProtocol>(out var newEnviron))
            {
                return newEnviron.OnNewEnvironEndedAsync(Context());
            }

            return default;
        }
        public override ValueTask XDisplayLocationRequestedAsync()
        {
            if (TryGetEnabledPlugin<Protocols.XDisplayProtocol>(out var xdisploc))
            {
                return xdisploc.OnRequestedAsync(Context());
            }

            return default;
        }

        public override ValueTask XDisplayLocationAsync(byte[] text)
        {
            if (TryGetEnabledPlugin<Protocols.XDisplayProtocol>(out var xdisploc))
            {
                return xdisploc.CompleteXDisplayLocationFromBytesAsync(text, Context());
            }

            return default;
        }
        public override ValueTask Mccp2MarkerAsync()
        {
            if (TryGetEnabledPlugin<Protocols.MCCPProtocol>(out var mccp))
            {
                return mccp.OnMccp2MarkerAsync(Context());
            }

            return default;
        }

        public override ValueTask Mccp3MarkerAsync()
        {
            if (TryGetEnabledPlugin<Protocols.MCCPProtocol>(out var mccp))
            {
                return mccp.OnMccp3MarkerAsync(Context());
            }

            return default;
        }

        public override ValueTask Mccp1MarkerAsync()
        {
            if (TryGetEnabledPlugin<Protocols.MCCPProtocol>(out var mccp))
            {
                return mccp.OnMccp1MarkerAsync(Context());
            }

            return default;
        }
        public override ValueTask EnvironStartedAsync(byte command)
        {
            if (TryGetEnabledPlugin<Protocols.EnvironProtocol>(out var environ))
            {
                return environ.OnEnvironStartedAsync(command, Context());
            }

            return default;
        }

        public override ValueTask EnvironVarAsync()
        {
            if (TryGetEnabledPlugin<Protocols.EnvironProtocol>(out var environ))
            {
                return environ.OnEnvironVarMarkerAsync(Context());
            }

            return default;
        }

        public override ValueTask EnvironValueAsync()
        {
            if (TryGetEnabledPlugin<Protocols.EnvironProtocol>(out var environ))
            {
                return environ.OnEnvironValueMarkerAsync(Context());
            }

            return default;
        }

        public override ValueTask EnvironDataAsync(ReadOnlyMemory<byte> data)
        {
            if (TryGetEnabledPlugin<Protocols.EnvironProtocol>(out var environ))
            {
                return environ.OnEnvironDataAsync(data, Context());
            }

            return default;
        }

        public override ValueTask EnvironEndedAsync()
        {
            if (TryGetEnabledPlugin<Protocols.EnvironProtocol>(out var environ))
            {
                return environ.OnEnvironEndedAsync(Context());
            }

            return default;
        }
        public override ValueTask TerminalSpeedRequestedAsync()
        {
            if (TryGetEnabledPlugin<Protocols.TerminalSpeedProtocol>(out var tspeed))
            {
                return tspeed.OnRequestedAsync(Context());
            }

            return default;
        }

        public override ValueTask TerminalSpeedAsync(byte[] text)
        {
            if (TryGetEnabledPlugin<Protocols.TerminalSpeedProtocol>(out var tspeed))
            {
                return tspeed.CompleteTerminalSpeedFromBytesAsync(text, Context());
            }

            return default;
        }

        public override ValueTask WindowSizeAsync(int width, int height)
        {
            if (TryGetEnabledPlugin<Protocols.NAWSProtocol>(out var naws))
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

        /// <summary>
        /// Every dispatch in this class goes through this rather than a bare <c>GetPlugin</c>: a disabled
        /// plugin is still registered (<c>GetPlugin</c> would find it) because
        /// <c>ProtocolPluginManager.DisablePluginAsync</c> never tells the peer to stop negotiating --
        /// it only flips <c>IsEnabled</c>. Without this gate, a peer that negotiated an option before
        /// this side disabled the plugin could keep sending subnegotiation data and have it delivered
        /// as if nothing had changed, undoing what disabling the plugin was meant to do. Matches
        /// <see cref="NegotiateAsync"/>'s own <c>IsPluginEnabled</c> gate above.
        /// </summary>
        private bool TryGetEnabledPlugin<T>(out T plugin) where T : class, ITelnetProtocolPlugin
        {
            if (owner.PluginManager?.IsPluginEnabled(typeof(T)) == true &&
                owner.PluginManager.GetPlugin(typeof(T)) is T found)
            {
                plugin = found;
                return true;
            }

            plugin = null!;
            return false;
        }
    }

    /// <summary>
    /// Set once by <see cref="Builders.TelnetInterpreterBuilder.BuildAsync"/>, before
    /// <see cref="StartGeneratedMachineAsync"/> runs, to the same <see cref="Plugins.ProtocolContext"/>
    /// every plugin was configured and initialized against.
    /// </summary>
    internal IProtocolContext? SharedProtocolContext { get; set; }
}
