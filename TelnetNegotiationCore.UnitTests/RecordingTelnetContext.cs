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

    /// <summary>
    /// The line being accumulated that has not been submitted yet. A stream whose trailing text
    /// never receives its newline is otherwise indistinguishable from one that dropped the text,
    /// which is a difference every generated property needs to see.
    /// </summary>
    public string PendingText => _line.ToString();

    /// <summary>
    /// A protocol's structural markers and payload in order, with adjacent payload runs merged.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The streaming protocols — MSSP, ENVIRON and NEW-ENVIRON — deliver payload through a
    /// <c>DataAsync</c> callback that fires once per run of payload bytes, and a run necessarily
    /// stops at a chunk boundary because the span it is handed cannot cross one. So the number of
    /// <c>DataAsync</c> calls is a function of how the peer's bytes happened to arrive, not of what
    /// the peer said, and any consumer has to concatenate consecutive calls to recover a value.
    /// GMCP, MSDP and CHARSET's translation table already accumulate for exactly this reason.
    /// </para>
    /// <para>
    /// The public event lists keep one entry per call, because that is what the existing tests
    /// assert on. This parallel trace merges adjacent payload runs so that
    /// <see cref="Snapshot"/> compares what the peer said and in what order, and stays blind to how
    /// the bytes were split — the same distinction <see cref="Write"/> already relies on.
    /// </para>
    /// </remarks>
    private sealed class CoalescingTrace
    {
        /// <summary>
        /// A marker carries its name; a payload run carries its raw bytes.
        /// </summary>
        /// <remarks>
        /// Payload is kept as bytes rather than as an ASCII-decoded string on purpose. ASCII
        /// decoding maps every byte above 0x7F to the same replacement character, so a
        /// fragmentation-dependent substitution between two high bytes — exactly the defect
        /// <see cref="FragmentationProperties"/> exists to catch — would produce identical snapshots
        /// and pass. The public event lists keep the decoded strings, because that is what the
        /// existing tests assert on.
        /// </remarks>
        private readonly List<(bool IsData, string Marker, List<byte> Data)> _entries = [];

        public void Marker(string name) => _entries.Add((false, name, null));

        public void Data(ReadOnlySpan<byte> data)
        {
            if (_entries.Count > 0 && _entries[^1].IsData)
            {
                foreach (var b in data)
                {
                    _entries[^1].Data.Add(b);
                }

                return;
            }

            var run = new List<byte>(data.Length);
            foreach (var b in data)
            {
                run.Add(b);
            }

            _entries.Add((true, null, run));
        }

        public void Render(StringBuilder sb, string name)
        {
            sb.Append(name).Append('=');
            foreach (var (isData, marker, data) in _entries)
            {
                if (isData)
                {
                    sb.Append('d').Append(data.Count).Append(':');
                    foreach (var b in data)
                    {
                        sb.Append(b.ToString("x2"));
                    }

                    sb.Append(',');
                    continue;
                }

                sb.Append('m').Append(marker.Length).Append(':').Append(marker).Append(',');
            }

            sb.Append(';');
        }
    }

    private readonly CoalescingTrace _msspTrace = new();

    private readonly CoalescingTrace _newEnvironTrace = new();

    private readonly CoalescingTrace _environTrace = new();

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

    public List<string> NewEnvironEvents { get; } = [];

    public List<string> EnvironEvents { get; } = [];

    public int MxpStarts { get; private set; }

    public int Mccp2Markers { get; private set; }

    public int Mccp3Markers { get; private set; }

    public int Mccp1Markers { get; private set; }

    public List<(byte Kind, byte[] Data)> LineModeMessages { get; } = [];

    public List<byte[]> AuthenticationSends { get; } = [];

    public List<byte[]> AuthenticationIsMessages { get; } = [];

    public List<byte[]> EncryptionSends { get; } = [];

    public List<byte[]> EncryptionIsMessages { get; } = [];

    public List<byte[]> EncryptionStarts { get; } = [];

    public int EncryptionEnds { get; private set; }

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

    public override ValueTask GmcpStartedAsync()
    {
        _gmcp.Clear();
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

    public override ValueTask MsdpStartedAsync()
    {
        _msdp.Clear();
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
        _msspTrace.Marker("started");
        return default;
    }

    public override ValueTask MsspVariableMarkerAsync()
    {
        MsspEvents.Add("VAR");
        _msspTrace.Marker("VAR");
        return default;
    }

    public override ValueTask MsspValueMarkerAsync()
    {
        MsspEvents.Add("VAL");
        _msspTrace.Marker("VAL");
        return default;
    }

    public override ValueTask MsspDataAsync(ReadOnlyMemory<byte> data)
    {
        MsspEvents.Add(Encoding.ASCII.GetString(data.Span));
        _msspTrace.Data(data.Span);
        return default;
    }

    public override ValueTask MsspEndedAsync()
    {
        MsspEvents.Add("ended");
        _msspTrace.Marker("ended");
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

    private readonly List<byte> _charsetTTable = [];

    public override ValueTask CharsetTTableStartedAsync()
    {
        _charsetTTable.Clear();
        return default;
    }

    public override ValueTask CharsetTTableDataAsync(ReadOnlyMemory<byte> data)
    {
        _charsetTTable.AddRange(data.ToArray());
        return default;
    }

    public override ValueTask CharsetTTableEndedAsync()
    {
        CharsetTTables.Add([.. _charsetTTable]);
        _charsetTTable.Clear();
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

    public override ValueTask NewEnvironStartedAsync(byte command)
    {
        NewEnvironEvents.Add($"started {command}");
        _newEnvironTrace.Marker($"started {command}");
        return default;
    }

    public override ValueTask NewEnvironVarAsync()
    {
        NewEnvironEvents.Add("VAR");
        _newEnvironTrace.Marker("VAR");
        return default;
    }

    public override ValueTask NewEnvironUserVarAsync()
    {
        NewEnvironEvents.Add("USERVAR");
        _newEnvironTrace.Marker("USERVAR");
        return default;
    }

    public override ValueTask NewEnvironValueAsync()
    {
        NewEnvironEvents.Add("VALUE");
        _newEnvironTrace.Marker("VALUE");
        return default;
    }

    public override ValueTask NewEnvironDataAsync(ReadOnlyMemory<byte> data)
    {
        NewEnvironEvents.Add(Encoding.ASCII.GetString(data.Span));
        _newEnvironTrace.Data(data.Span);
        return default;
    }

    public override ValueTask NewEnvironEndedAsync()
    {
        NewEnvironEvents.Add("ended");
        _newEnvironTrace.Marker("ended");
        return default;
    }

    public override ValueTask EnvironStartedAsync(byte command)
    {
        EnvironEvents.Add($"started {command}");
        _environTrace.Marker($"started {command}");
        return default;
    }

    public override ValueTask EnvironVarAsync()
    {
        EnvironEvents.Add("VAR");
        _environTrace.Marker("VAR");
        return default;
    }

    public override ValueTask EnvironValueAsync()
    {
        EnvironEvents.Add("VALUE");
        _environTrace.Marker("VALUE");
        return default;
    }

    public override ValueTask EnvironDataAsync(ReadOnlyMemory<byte> data)
    {
        EnvironEvents.Add(Encoding.ASCII.GetString(data.Span));
        _environTrace.Data(data.Span);
        return default;
    }

    public override ValueTask EnvironEndedAsync()
    {
        EnvironEvents.Add("ended");
        _environTrace.Marker("ended");
        return default;
    }

    public override ValueTask MxpStartedAsync()
    {
        MxpStarts++;
        return default;
    }

    public override ValueTask Mccp2MarkerAsync()
    {
        Mccp2Markers++;
        return default;
    }

    public override ValueTask Mccp3MarkerAsync()
    {
        Mccp3Markers++;
        return default;
    }

    public override ValueTask Mccp1MarkerAsync()
    {
        Mccp1Markers++;
        return default;
    }

    public override ValueTask LineModeAsync(byte kind, byte[] data)
    {
        LineModeMessages.Add((kind, data));
        return default;
    }

    public override ValueTask AuthenticationSendAsync(byte[] data)
    {
        AuthenticationSends.Add(data);
        return default;
    }

    public override ValueTask AuthenticationIsAsync(byte[] data)
    {
        AuthenticationIsMessages.Add(data);
        return default;
    }

    public override ValueTask EncryptionSendAsync(byte[] data)
    {
        EncryptionSends.Add(data);
        return default;
    }

    public override ValueTask EncryptionIsAsync(byte[] data)
    {
        EncryptionIsMessages.Add(data);
        return default;
    }

    public override ValueTask EncryptionStartAsync(byte[] keyId)
    {
        EncryptionStarts.Add(keyId);
        return default;
    }

    public override ValueTask EncryptionEndAsync()
    {
        EncryptionEnds++;
        return default;
    }

    public int GoAheads { get; private set; }

    public int Eors { get; private set; }

    public override ValueTask GoAheadAsync()
    {
        GoAheads++;
        return default;
    }

    public override ValueTask EorAsync()
    {
        Eors++;
        return default;
    }

    /// <summary>
    /// Every recorded field rendered to one deterministic string, so that two runs compare with a
    /// single equality and a failure prints a readable diff rather than thirty-six assertions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The order is fixed and alphabetical by field name rather than by declaration, so that adding
    /// a field to this class cannot silently reorder an existing snapshot.
    /// </para>
    /// <para>
    /// <see cref="Write"/> call boundaries are deliberately absent. Chunking a stream legitimately
    /// changes how byte runs batch into <see cref="Write"/> calls; a snapshot that could see those
    /// boundaries would make the fragmentation property fail on every case for a reason of no
    /// interest to anyone. The contract is which bytes arrive in which order, not how many calls
    /// delivered them.
    /// </para>
    /// </remarks>
    public string Snapshot()
    {
        var sb = new StringBuilder();

        Bytes(sb, "AuthenticationIsMessages", AuthenticationIsMessages);
        Bytes(sb, "AuthenticationSends", AuthenticationSends);
        Bytes(sb, "CharsetAccepted", CharsetAccepted);
        Count(sb, "CharsetRejections", CharsetRejections);
        Bytes(sb, "CharsetRequests", CharsetRequests);
        Count(sb, "CharsetTTableAcks", CharsetTTableAcks);
        Count(sb, "CharsetTTableNaks", CharsetTTableNaks);
        Count(sb, "CharsetTTableRejections", CharsetTTableRejections);
        Bytes(sb, "CharsetTTables", CharsetTTables);
        Count(sb, "EncryptionEnds", EncryptionEnds);
        Bytes(sb, "EncryptionIsMessages", EncryptionIsMessages);
        Bytes(sb, "EncryptionSends", EncryptionSends);
        Bytes(sb, "EncryptionStarts", EncryptionStarts);
        _environTrace.Render(sb, "EnvironEvents");
        Count(sb, "Eors", Eors);
        Octets(sb, "FlowControlCommands", FlowControlCommands);
        Bytes(sb, "GmcpMessages", GmcpMessages);
        Count(sb, "GoAheads", GoAheads);

        sb.Append("LineModeMessages=");
        foreach (var (kind, data) in LineModeMessages)
        {
            sb.Append(kind).Append(':').Append(Hex(data)).Append(',');
        }

        sb.Append(';');

        Strings(sb, "Lines", Lines);
        Count(sb, "Mccp1Markers", Mccp1Markers);
        Count(sb, "Mccp2Markers", Mccp2Markers);
        Count(sb, "Mccp3Markers", Mccp3Markers);
        Bytes(sb, "MsdpMessages", MsdpMessages);
        _msspTrace.Render(sb, "MsspEvents");
        Count(sb, "MxpStarts", MxpStarts);
        Strings(sb, "Negotiations", Negotiations);
        _newEnvironTrace.Render(sb, "NewEnvironEvents");
        Strings(sb, "PendingText", [PendingText]);
        Octets(sb, "SubNegotiations", SubNegotiations);
        Bytes(sb, "TerminalSpeedReports", TerminalSpeedReports);
        Count(sb, "TerminalSpeedRequests", TerminalSpeedRequests);
        Bytes(sb, "TerminalTypeReports", TerminalTypeReports);
        Count(sb, "TerminalTypeRequests", TerminalTypeRequests);

        sb.Append("Windows=");
        foreach (var (width, height) in Windows)
        {
            sb.Append(width).Append('x').Append(height).Append(',');
        }

        sb.Append(';');

        Bytes(sb, "XDisplayLocationReports", XDisplayLocationReports);
        Count(sb, "XDisplayLocationRequests", XDisplayLocationRequests);

        return sb.ToString();

        static void Count(StringBuilder sb, string name, int value) =>
            sb.Append(name).Append('=').Append(value).Append(';');

        static void Octets(StringBuilder sb, string name, List<byte> values)
        {
            sb.Append(name).Append('=');
            foreach (var value in values)
            {
                sb.Append(value).Append(',');
            }

            sb.Append(';');
        }

        static void Bytes(StringBuilder sb, string name, List<byte[]> values)
        {
            sb.Append(name).Append('=');
            foreach (var value in values)
            {
                sb.Append(Hex(value)).Append(',');
            }

            sb.Append(';');
        }

        static void Strings(StringBuilder sb, string name, IReadOnlyList<string> values)
        {
            sb.Append(name).Append('=');
            foreach (var value in values)
            {
                // Length-prefixed so that ["a", "bc"] and ["ab", "c"] cannot collide.
                sb.Append(value.Length).Append(':').Append(value).Append(',');
            }

            sb.Append(';');
        }
    }

    /// <summary>Lower-case hex, so a snapshot diff points at a byte rather than at a code point.</summary>
    private static string Hex(byte[] value)
    {
        if (value is null)
        {
            return "null";
        }

        var sb = new StringBuilder(value.Length * 2);
        foreach (var b in value)
        {
            sb.Append(b.ToString("x2"));
        }

        return sb.ToString();
    }
}
