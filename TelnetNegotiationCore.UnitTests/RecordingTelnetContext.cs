using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using TelnetNegotiationCore.Machine;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// One implementation of <see cref="TelnetCoreContext"/> that every module's own tests share, recording what it
/// was called with rather than doing anything. Every protocol module adds an abstract member to the context, and
/// a separate fake per test class would need updating for each one; this is updated once, here, per module.
/// </summary>
public class RecordingTelnetContext : TelnetCoreContext
{
    private readonly StringBuilder _line = new();

    public List<string> Lines { get; } = [];

    public List<string> Negotiations { get; } = [];

    public List<byte> SubNegotiations { get; } = [];

    public List<(int Width, int Height)> Windows { get; } = [];

    public List<byte> FlowControlCommands { get; } = [];

    public int TerminalSpeedRequests { get; private set; }

    public List<byte[]> TerminalSpeedReports { get; } = [];

    public int XDisplayLocationRequests { get; private set; }

    public List<byte[]> XDisplayLocationReports { get; } = [];

    private readonly List<byte> _gmcp = [];

    public List<byte[]> GmcpMessages { get; } = [];

    private readonly List<byte> _msdp = [];

    public List<byte[]> MsdpMessages { get; } = [];

    public List<string> MsspEvents { get; } = [];

    public int TerminalTypeRequests { get; private set; }

    public List<byte[]> TerminalTypeReports { get; } = [];

    public List<byte[]> CharsetRequests { get; } = [];

    public List<byte[]> CharsetAccepted { get; } = [];

    public int CharsetRejections { get; private set; }

    public List<byte[]> CharsetTTables { get; } = [];

    public int CharsetTTableRejections { get; private set; }

    public int CharsetTTableAcks { get; private set; }

    public int CharsetTTableNaks { get; private set; }

    public override void Write(ReadOnlySpan<byte> text)
    {
        foreach (var b in text)
        {
            _line.Append((char)b);
        }
    }

    public override ValueTask SubmitAsync()
    {
        Lines.Add(_line.ToString());
        _line.Clear();
        return default;
    }

    public override ValueTask NegotiateAsync(byte verb, byte option)
    {
        Negotiations.Add($"{Verb(verb)} {option}");
        return default;
    }

    private static string Verb(byte verb) => verb switch
    {
        251 => "WILL",
        252 => "WONT",
        253 => "DO",
        254 => "DONT",
        _ => verb.ToString(),
    };

    public override ValueTask SubNegotiatedAsync(byte option, ReadOnlyMemory<byte> payload)
    {
        SubNegotiations.Add(option);
        return default;
    }

    public override ValueTask WindowSizeAsync(int width, int height)
    {
        Windows.Add((width, height));
        return default;
    }

    public override ValueTask FlowControlAsync(byte command)
    {
        FlowControlCommands.Add(command);
        return default;
    }

    public override ValueTask TerminalSpeedRequestedAsync()
    {
        TerminalSpeedRequests++;
        return default;
    }

    public override ValueTask TerminalSpeedAsync(byte[] text)
    {
        TerminalSpeedReports.Add(text);
        return default;
    }

    public override ValueTask XDisplayLocationRequestedAsync()
    {
        XDisplayLocationRequests++;
        return default;
    }

    public override ValueTask XDisplayLocationAsync(byte[] text)
    {
        XDisplayLocationReports.Add(text);
        return default;
    }

    public override ValueTask GmcpDataAsync(ReadOnlyMemory<byte> data)
    {
        _gmcp.AddRange(data.ToArray());
        return default;
    }

    public override ValueTask GmcpEndedAsync()
    {
        GmcpMessages.Add([.. _gmcp]);
        _gmcp.Clear();
        return default;
    }

    public override ValueTask MsdpDataAsync(ReadOnlyMemory<byte> data)
    {
        _msdp.AddRange(data.ToArray());
        return default;
    }

    public override ValueTask MsdpEndedAsync()
    {
        MsdpMessages.Add([.. _msdp]);
        _msdp.Clear();
        return default;
    }

    public override ValueTask MsspStartedAsync()
    {
        MsspEvents.Add("started");
        return default;
    }

    public override ValueTask MsspVariableMarkerAsync()
    {
        MsspEvents.Add("VAR");
        return default;
    }

    public override ValueTask MsspValueMarkerAsync()
    {
        MsspEvents.Add("VAL");
        return default;
    }

    public override ValueTask MsspDataAsync(ReadOnlyMemory<byte> data)
    {
        MsspEvents.Add(Encoding.ASCII.GetString(data.Span));
        return default;
    }

    public override ValueTask MsspEndedAsync()
    {
        MsspEvents.Add("ended");
        return default;
    }

    public override ValueTask TerminalTypeRequestedAsync()
    {
        TerminalTypeRequests++;
        return default;
    }

    public override ValueTask TerminalTypeAsync(byte[] text)
    {
        TerminalTypeReports.Add(text);
        return default;
    }

    public override ValueTask CharsetRequestAsync(byte[] text)
    {
        CharsetRequests.Add(text);
        return default;
    }

    public override ValueTask CharsetAcceptedAsync(byte[] text)
    {
        CharsetAccepted.Add(text);
        return default;
    }

    public override ValueTask CharsetRejectedAsync()
    {
        CharsetRejections++;
        return default;
    }

    public override ValueTask CharsetTTableAsync(byte[] text)
    {
        CharsetTTables.Add(text);
        return default;
    }

    public override ValueTask CharsetTTableRejectedAsync()
    {
        CharsetTTableRejections++;
        return default;
    }

    public override ValueTask CharsetTTableAckAsync()
    {
        CharsetTTableAcks++;
        return default;
    }

    public override ValueTask CharsetTTableNakAsync()
    {
        CharsetTTableNaks++;
        return default;
    }
}
