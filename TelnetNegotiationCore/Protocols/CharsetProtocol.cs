using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Stateless;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Plugins;

namespace TelnetNegotiationCore.Protocols;

/// <summary>
/// Charset protocol plugin - RFC 2066
/// http://www.faqs.org/rfcs/rfc2066.html
/// </summary>
public class CharsetProtocol : TelnetProtocolPluginBase
{
    private byte[] _charsetByteState = [];
    private int _charsetByteIndex = 0;

    /// <summary>
    /// Sets the CharacterSet Order for negotiation priority
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">codepage is less than zero or greater than 65535.</exception>
    /// <exception cref="ArgumentException">codepage is not supported by the underlying platform.</exception>
    /// <exception cref="NotSupportedException">codepage is not supported by the underlying platform.</exception>
    public IEnumerable<Encoding>? CharsetOrder { get; set; }

    /// <summary>
    /// Function to get allowed encodings (defaults to all system encodings)
    /// </summary>
    public Func<IEnumerable<EncodingInfo>> AllowedEncodings { get; set; } = Encoding.GetEncodings;

    /// <summary>
    /// Currently selected encoding (defaults to UTF8)
    /// </summary>
    public Encoding CurrentEncoding { get; private set; } = Encoding.UTF8;

    /// <inheritdoc />
    public override Type ProtocolType => typeof(CharsetProtocol);

    /// <inheritdoc />
    public override string ProtocolName => "Charset (RFC 2066)";

    /// <inheritdoc />
    public override IReadOnlyCollection<Type> Dependencies => Array.Empty<Type>();

    /// <inheritdoc />
    public override void ConfigureStateMachine(StateMachine<State, Trigger> stateMachine, IProtocolContext context)
    {
        context.Logger.LogInformation("Configuring Charset state machine");
    }

    /// <inheritdoc />
    protected override ValueTask OnInitializeAsync()
    {
        Context.Logger.LogInformation("Charset Protocol initialized");
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolEnabledAsync()
    {
        Context.Logger.LogInformation("Charset Protocol enabled");
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolDisabledAsync()
    {
        Context.Logger.LogInformation("Charset Protocol disabled");
        _charsetByteState = [];
        _charsetByteIndex = 0;
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Processes a charset byte
    /// </summary>
    public void ProcessCharsetByte(byte value)
    {
        if (!IsEnabled)
            return;

        if (_charsetByteIndex >= _charsetByteState.Length)
        {
            Array.Resize(ref _charsetByteState, _charsetByteIndex + 1);
        }

        _charsetByteState[_charsetByteIndex++] = value;
    }

    /// <summary>
    /// Gets the list of supported character sets
    /// </summary>
    public byte[] GetSupportedCharacterSets()
    {
        if (!IsEnabled)
            return [];

        var charsets = AllowedEncodings()
            .Select(x => x.GetEncoding())
            .OrderBy(x => x.EncodingName)
            .Select(x => x.WebName)
            .ToList();

        var result = string.Join(" ", charsets);
        return Encoding.ASCII.GetBytes(result);
    }

    /// <summary>
    /// Processes charset negotiation and selects appropriate encoding
    /// </summary>
    public async ValueTask ProcessCharsetNegotiationAsync()
    {
        if (!IsEnabled || _charsetByteState.Length == 0)
            return;

        try
        {
            var requestedCharsets = Encoding.ASCII.GetString(_charsetByteState, 0, _charsetByteIndex)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);

            var availableEncodings = AllowedEncodings()
                .Select(x => x.GetEncoding())
                .ToList();

            // Find first matching charset
            foreach (var charset in requestedCharsets)
            {
                var matchingEncoding = availableEncodings
                    .FirstOrDefault(e => e.WebName.Equals(charset, StringComparison.OrdinalIgnoreCase));

                if (matchingEncoding != null)
                {
                    CurrentEncoding = matchingEncoding;
                    Context.Logger.LogInformation("Charset negotiated: {Charset}", matchingEncoding.EncodingName);

                    // Trigger callback if registered
                    if (Context.TryGetSharedState<Func<Encoding, ValueTask>>("Charset_Callback", out var callback) && callback != null)
                    {
                        await callback(CurrentEncoding);
                    }

                    break;
                }
            }
        }
        finally
        {
            _charsetByteState = [];
            _charsetByteIndex = 0;
        }
    }

    /// <inheritdoc />
    protected override ValueTask OnDisposeAsync()
    {
        _charsetByteState = [];
        return ValueTask.CompletedTask;
    }
}
