using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Plugins;

namespace TelnetNegotiationCore.Protocols;

public partial class TerminalQueryProtocol
{
    private Func<TerminalCellSize?>? _cellSizeProvider;
    private ClientCellSizeResponder? _clientCellSizeResponder;

    /// <summary>Opts a client into answering <c>CSI 16 t</c> with measured physical cell pixels.</summary>
    /// <remarks>
    /// Configure before building. No response is enabled by default. Return null when the terminal's
    /// actual geometry is not available; nonpositive or greater-than-65535 dimensions are also ignored.
    /// The provider runs on the receive loop or the caller of <see cref="ReportCellSizeAsync"/>, so
    /// return a thread-safe snapshot rather than accessing UI controls directly. It must not wait on
    /// input processing or plugin disposal. Input remains unchanged, including the recognized query.
    /// </remarks>
    /// <param name="provider">The current cell size in physical pixels, or null when unknown.</param>
    /// <returns>This plugin for fluent configuration.</returns>
    /// <exception cref="ArgumentNullException">The provider is null.</exception>
    /// <exception cref="InvalidOperationException">The plugin has already been configured or initialized.</exception>
    public TerminalQueryProtocol WithCellSizeProvider(Func<TerminalCellSize?> provider)
    {
        if (provider is null) throw new ArgumentNullException(nameof(provider));
        if (IsInitialized || _clientCellSizeResponder is not null)
            throw new InvalidOperationException("Configure the cell-size provider before building the interpreter.");
        _cellSizeProvider = provider;
        return this;
    }

    /// <summary>Reports changed cell pixels to a server that has already asked for them.</summary>
    /// <remarks>
    /// Call after font, zoom, DPI or host-terminal metrics change, or become available. The provider is
    /// read under a per-connection send gate so concurrent refreshes cannot leave an older size on the
    /// wire. Unchanged sizes are not repeated; each explicit peer query is answered even if unchanged.
    /// Does nothing without a provider, before the first query, while disabled, or after disposal.
    /// Disable or dispose the plugin to stop reports. Already-started writes may finish; shutdown waits
    /// for them and suppresses queued reports. Re-enabling requires a fresh query from the peer.
    /// </remarks>
    public ValueTask ReportCellSizeAsync() => _clientCellSizeResponder?.ReportAsync() ?? default;

    private bool ConfigureClientCellSizeResponder(IProtocolContext context)
    {
        if (_cellSizeProvider is null) return false;
        if (context.Mode != Interpreters.TelnetInterpreter.TelnetMode.Client)
            throw new InvalidOperationException("A cell-size responder requires client mode.");
        _clientCellSizeResponder = new ClientCellSizeResponder(this, context, _cellSizeProvider);
        context.Interpreter.RegisterInputByteObserver(
            _clientCellSizeResponder.ObserveAsync, _clientCellSizeResponder.FinishBoundary);
        return true;
    }

    private sealed class ClientCellSizeResponder(
        TerminalQueryProtocol owner, IProtocolContext context, Func<TerminalCellSize?> measure)
    {
        private enum CellInputState { Ground, Escape, Csi, String, StringEscape }
        private CellInputState _state;
        private bool _osc;
        private int _queryLength;
        private bool _candidate;
        private int _codePage = -1;
        private bool _asciiCompatible;
        private volatile bool _asked;
        private volatile bool _stopped;
        private TerminalCellSize? _sent;
        private readonly SemaphoreSlim _sendGate = new(1, 1);
        private static readonly byte[] s_controlSyntax = Encoding.ASCII.GetBytes("\u001b[16t]P_^X\\\a");

        private static bool HasCompatibleControlSyntax(Encoding encoding) =>
            encoding.GetBytes("\u001b[16t]P_^X\\\a").SequenceEqual(s_controlSyntax);

        private bool IsActive => owner.IsEnabled && !owner._disposed && !_stopped
            && !context.Interpreter.ProcessingToken.IsCancellationRequested;

        internal void FinishBoundary() => _state = CellInputState.Ground;

        internal void Enable()
        {
            _asked = false;
            _sent = null;
            _state = CellInputState.Ground;
            _stopped = false;
        }

        internal async ValueTask StopAsync()
        {
            _stopped = true;
            await _sendGate.WaitAsync().ConfigureAwait(false);
            try
            {
                _asked = false;
                _sent = null;
                _state = CellInputState.Ground;
            }
            finally { _sendGate.Release(); }
        }

        internal ValueTask ObserveAsync(byte value, Encoding encoding)
        {
            if (_codePage != encoding.CodePage)
            {
                _codePage = encoding.CodePage;
                _asciiCompatible = HasCompatibleControlSyntax(encoding);
                _state = CellInputState.Ground;
            }

            if (!IsActive || !_asciiCompatible)
            {
                _state = CellInputState.Ground;
                return default;
            }

            // The line-rendering paths abandon incomplete controls on LF. A CR inside OSC/DCS
            // remains in that string (including CR NUL), but aborts an incomplete CSI below.
            if (value is 0x18 or 0x1a or 10)
            {
                _state = CellInputState.Ground;
                return default;
            }

            if (_state is CellInputState.String or CellInputState.StringEscape)
            {
                if ((_osc && value == 7) || (_state == CellInputState.StringEscape && value == '\\'))
                    _state = CellInputState.Ground;
                else
                    _state = value == 27 ? CellInputState.StringEscape : CellInputState.String;
                return default;
            }

            if (value == 27)
            {
                _state = CellInputState.Escape;
                return default;
            }

            switch (_state)
            {
                case CellInputState.Escape:
                    _state = value switch
                    {
                        (byte)'[' => CellInputState.Csi,
                        (byte)']' or (byte)'P' or (byte)'_' or (byte)'^' or (byte)'X' => CellInputState.String,
                        _ => CellInputState.Ground,
                    };
                    _osc = value == ']';
                    _queryLength = 0;
                    _candidate = true;
                    break;
                case CellInputState.Csi:
                    if (value is >= 0x40 and <= 0x7e)
                    {
                        _state = CellInputState.Ground;
                        if (_candidate && _queryLength == 2 && value == 't')
                        {
                            _asked = true;
                            return ReportAsync(force: true);
                        }
                    }
                    else if (value is >= 0x20 and <= 0x3f)
                    {
                        _candidate &= _queryLength switch { 0 => value == '1', 1 => value == '6', _ => false };
                        _queryLength = Math.Min(3, _queryLength + 1);
                    }
                    else _state = CellInputState.Ground;
                    break;
            }
            return default;
        }

        internal async ValueTask ReportAsync(bool force = false)
        {
            if (!IsActive || !_asked) return;
            var cancellation = context.Interpreter.ProcessingToken;
            try { await _sendGate.WaitAsync(cancellation).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return; }
            try
            {
                // Re-read after waiting: A -> B -> A must finish with A, even while B is sending.
                if (!IsActive || !HasCompatibleControlSyntax(context.CurrentEncoding)
                    || measure() is not { IsValid: true } current || (!force && current == _sent)) return;
                var reply = string.Format(CultureInfo.InvariantCulture, "\u001b[6;{0};{1}t", current.Height, current.Width);
                await context.Interpreter.WriteToNetworkAsync(Encoding.ASCII.GetBytes(reply), cancellation).ConfigureAwait(false);
                _sent = current;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            finally { _sendGate.Release(); }
        }
    }
}
