# Property-Based Telnet Regression Testing Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Seven invariants over generated telnet input, asserted against the StateAlchemist engine, so the unreleased delta has regression evidence that hand-written examples cannot provide.

**Architecture:** Everything lives in `TelnetNegotiationCore.UnitTests` beside the existing 899 tests, with fixed seeds and bounded iteration counts so each property gates every pull request. Input is generated from weighted telnet *tokens* whose frame grammars come from the RFCs, not from the code under test. Failures shrink to a minimal byte array which is then committed as an ordinary named test with the bytes inlined.

**Tech Stack:** C# (LangVersion `latest` on all targets), TUnit 1.x, `net8.0`/`net10.0`/`net11.0`, StateAlchemist 1.2.0. No new package references.

**Spec:** `docs/superpowers/specs/2026-09-12-telnet-fuzz-regression-design.md`

## Global Constraints

- **No new package references.** The test project carries TUnit and Serilog only. Scorecard already docks the repository for unpinned NuGet dependencies.
- **`TreatWarningsAsErrors` is on in both Debug and Release.** Any warning fails the build.
- **All three target frameworks must pass:** `net8.0`, `net10.0`, `net11.0`.
- **`dotnet test` does not work on this repository** under the .NET 11 SDK. The only working invocation is `dotnet run --project TelnetNegotiationCore.UnitTests -c Release --framework <tfm>`. Task 13 fixes this; until then, use `dotnet run`.
- **Determinism is mandatory.** Every generator is seeded from a named constant. A test that fails once must fail every time on the same commit.
- **Total added runtime budget: under 10 seconds** across all properties, against the present 17s suite.
- **Tabs, not spaces**, in files that already use tabs (`BaseTest.cs`, most older tests). Newer files under `Machine/` and the migration-era tests use four spaces. Match the file you are in.
- **Every property must be validated against a deliberate mutant** before it is trusted. A property that passes against its own mutant is not finished.

---

### Task 1: Recorder instrumentation

`RecordingTelnetContext` can currently only be compared field by field, and it hides
unsubmitted text entirely. Every property that compares two runs needs both fixed.

**Files:**
- Modify: `TelnetNegotiationCore.UnitTests/RecordingTelnetContext.cs`
- Test: `TelnetNegotiationCore.UnitTests/RecorderSnapshotTests.cs` (create)

**Interfaces:**
- Consumes: nothing.
- Produces: `RecordingTelnetContext.PendingText` (`string`, the unsubmitted line) and
  `RecordingTelnetContext.Snapshot()` (`string`, a deterministic rendering of all 36
  recorded properties plus `PendingText`).

- [ ] **Step 1: Write the failing test**

Create `TelnetNegotiationCore.UnitTests/RecorderSnapshotTests.cs`:

```csharp
using System.Text;
using System.Threading.Tasks;
using TelnetNegotiationCore.Machine;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// The snapshot is the oracle every generated property compares against, so it has to be
/// sensitive to everything that matters and insensitive to everything that does not.
/// </summary>
public class RecorderSnapshotTests
{
    private const byte SE = 240;
    private const byte SB = 250;
    private const byte WILL = 251;
    private const byte IAC = 255;

    private static async Task<RecordingTelnetContext> Run(params byte[] bytes)
    {
        var recorder = new RecordingTelnetContext();
        await using var machine = new TelnetCoreMachine(recorder);
        await machine.StartAsync();
        await machine.FireAsync(bytes);
        return recorder;
    }

    [Test]
    public async Task PendingTextExposesALineThatHasNotBeenSubmitted()
    {
        var recorder = await Run(Encoding.ASCII.GetBytes("partial"));

        await Assert.That(recorder.Lines).IsEmpty();
        await Assert.That(recorder.PendingText).IsEqualTo("partial");
    }

    [Test]
    public async Task SnapshotDistinguishesDroppedTrailingTextFromDeliveredText()
    {
        var withText = await Run(Encoding.ASCII.GetBytes("abc"));
        var withoutText = await Run();

        await Assert.That(withText.Snapshot()).IsNotEqualTo(withoutText.Snapshot());
    }

    [Test]
    public async Task SnapshotIsEqualForTwoIdenticalRuns()
    {
        var first = await Run([IAC, WILL, 31, .. "hi\r\n"u8]);
        var second = await Run([IAC, WILL, 31, .. "hi\r\n"u8]);

        await Assert.That(first.Snapshot()).IsEqualTo(second.Snapshot());
    }

    [Test]
    public async Task SnapshotSeesASubnegotiationAndANegotiationSeparately()
    {
        var negotiated = await Run([IAC, WILL, 70]);
        var subnegotiated = await Run([IAC, SB, 70, IAC, SE]);

        await Assert.That(negotiated.Snapshot()).IsNotEqualTo(subnegotiated.Snapshot());
    }

    /// <summary>
    /// Chunking a stream changes how byte runs batch into Write calls but must not change what
    /// the snapshot says, or the fragmentation property would fail on every case for a reason
    /// nobody cares about. This pins that intent.
    /// </summary>
    [Test]
    public async Task SnapshotIsBlindToHowWriteCallsWereBatched()
    {
        var whole = new RecordingTelnetContext();
        await using (var machine = new TelnetCoreMachine(whole))
        {
            await machine.StartAsync();
            await machine.FireAsync(Encoding.ASCII.GetBytes("hello\r\n"));
        }

        var split = new RecordingTelnetContext();
        await using (var machine = new TelnetCoreMachine(split))
        {
            await machine.StartAsync();
            foreach (var b in Encoding.ASCII.GetBytes("hello\r\n"))
            {
                await machine.FireAsync([b]);
            }
        }

        await Assert.That(whole.Snapshot()).IsEqualTo(split.Snapshot());
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet run --project TelnetNegotiationCore.UnitTests -c Debug --framework net10.0 -- --treenode-filter "/*/*/RecorderSnapshotTests/*"`

Expected: build failure — `PendingText` and `Snapshot` do not exist on `RecordingTelnetContext`.

- [ ] **Step 3: Add the two members**

In `RecordingTelnetContext.cs`, immediately after the `_line` field declaration, add:

```csharp
    /// <summary>
    /// The line being accumulated that has not been submitted yet. A stream whose trailing text
    /// never receives its newline is otherwise indistinguishable from one that dropped the text,
    /// which is a difference every generated property needs to see.
    /// </summary>
    public string PendingText => _line.ToString();
```

At the end of the class, add:

```csharp
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
        Strings(sb, "EnvironEvents", EnvironEvents);
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
        Strings(sb, "MsspEvents", MsspEvents);
        Count(sb, "MxpStarts", MxpStarts);
        Strings(sb, "Negotiations", Negotiations);
        Strings(sb, "NewEnvironEvents", NewEnvironEvents);
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
    private static string Hex(byte[]? value)
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
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet run --project TelnetNegotiationCore.UnitTests -c Debug --framework net10.0 -- --treenode-filter "/*/*/RecorderSnapshotTests/*"`

Expected: 5 passed.

- [ ] **Step 5: Validate the snapshot against a mutant**

Temporarily delete the `Strings(sb, "PendingText", [PendingText]);` line. Re-run. Expected:
`SnapshotDistinguishesDroppedTrailingTextFromDeliveredText` **fails**. Restore the line and
confirm it passes again. A snapshot that cannot see dropped text is not an oracle.

- [ ] **Step 6: Run the whole suite to confirm nothing regressed**

`RecordingTelnetContext` is shared by roughly twenty test classes, so the additive change
must be confirmed harmless.

Run: `dotnet run --project TelnetNegotiationCore.UnitTests -c Debug --framework net10.0`

Expected: 904 passed, 0 failed.

- [ ] **Step 7: Commit**

```bash
git add TelnetNegotiationCore.UnitTests/RecordingTelnetContext.cs TelnetNegotiationCore.UnitTests/RecorderSnapshotTests.cs
git commit -m "test(recorder): expose pending text and a deterministic snapshot"
```

---

### Task 2: Verify the resync token against all 28 options

Properties 1 and 3 append a probe line to arbitrary garbage and assert it still arrives. That
needs a byte sequence returning the machine to `Idle` from any state. Reading
`TelnetCoreModule`'s transition table says `IAC SE IAC SE` does it for all thirteen core
states, and that `IAC SE` alone does not — from `ReadingOption` the `IAC` binds as the option
byte, and from `EndSubNegotiation` it is consumed as an escaped literal.

The sixteen protocol modules contribute their own substates beneath `SubNegotiation`, some of
which handle `IAC` themselves. **This task establishes the resync token empirically before
anything is built on it.** If some module is not resynchronised, that is a finding, and the
token becomes whatever the evidence supports.

**Files:**
- Test: `TelnetNegotiationCore.UnitTests/ResyncTests.cs` (create)

**Interfaces:**
- Consumes: `RecordingTelnetContext.Snapshot()` from Task 1 (not strictly needed here, but the file shares its `Run` helper shape).
- Produces: `TelnetProbe.Resync` (`byte[]`), `TelnetProbe.ProbeLine` (`byte[]`), and
  `TelnetProbe.Recovered(RecordingTelnetContext)` (`bool`) — the liveness oracle every later
  property reuses.

- [ ] **Step 1: Write the failing test**

Create `TelnetNegotiationCore.UnitTests/ResyncTests.cs`:

```csharp
using System.Collections.Generic;
using System.Threading.Tasks;
using TelnetNegotiationCore.Machine;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// The resynchronisation token that the liveness and fragmentation properties are built on, and
/// the evidence that it actually works from every state any of the 28 options can leave the
/// machine in.
/// </summary>
/// <remarks>
/// <c>IAC SE</c> alone is not enough, and the two tests at the bottom of this file pin why: from
/// <c>ReadingOption</c> the <c>IAC</c> is bound as the subnegotiation's option byte and the
/// <c>SE</c> becomes payload, and from <c>EndSubNegotiation</c> the <c>IAC</c> is consumed by
/// <c>EscapedInPayload</c> as a literal 255. Both leave the machine inside <c>SubNegotiating</c>.
/// </remarks>
public class ResyncTests
{
    private const byte SE = 240;
    private const byte SB = 250;
    private const byte WILL = 251;
    private const byte WONT = 252;
    private const byte DO = 253;
    private const byte DONT = 254;
    private const byte IAC = 255;

    /// <summary>Every option this library has a module or a protocol for.</summary>
    public static IEnumerable<byte> AllOptions =>
    [
        1,   // ECHO, RFC 857
        3,   // SUPPRESS-GO-AHEAD, RFC 858
        24,  // TERMINAL-TYPE, RFC 1091
        25,  // END-OF-RECORD, RFC 885
        31,  // NAWS, RFC 1073
        32,  // TERMINAL-SPEED, RFC 1079
        33,  // TOGGLE-FLOW-CONTROL, RFC 1372
        34,  // LINEMODE, RFC 1184
        35,  // X-DISPLAY-LOCATION, RFC 1096
        36,  // ENVIRON, RFC 1408
        37,  // AUTHENTICATION, RFC 2941
        38,  // ENCRYPT, RFC 2946
        39,  // NEW-ENVIRON, RFC 1572
        42,  // CHARSET, RFC 2066
        69,  // MSDP
        70,  // MSSP
        85,  // MCCP1
        86,  // MCCP2
        87,  // MCCP3
        91,  // MXP
        201, // GMCP
    ];

    /// <summary>
    /// The sub-command bytes worth stopping part-way through, one per option that has a command
    /// byte after the option byte. Stopping here is what leaves a module in its own substate.
    /// </summary>
    public static IEnumerable<(byte Option, byte Command)> OptionCommands =>
    [
        (24, 0), (24, 1),                       // TTYPE IS, SEND
        (32, 0), (32, 1),                       // TSPEED IS, SEND
        (33, 0), (33, 1), (33, 2), (33, 3),     // FLOWCONTROL OFF, ON, RESTART-ANY, RESTART-XON
        (34, 1), (34, 2), (34, 3),              // LINEMODE MODE, FORWARDMASK, SLC
        (35, 0), (35, 1),                       // XDISPLOC IS, SEND
        (36, 0), (36, 1), (36, 2),              // ENVIRON IS, SEND, INFO
        (37, 0), (37, 1), (37, 2), (37, 3),     // AUTH IS, SEND, REPLY, NAME
        (38, 0), (38, 1), (38, 3), (38, 4),     // ENCRYPT IS, SUPPORT, START, END
        (39, 0), (39, 1), (39, 2),              // NEW-ENVIRON IS, SEND, INFO
        (42, 1), (42, 2), (42, 3), (42, 4),     // CHARSET REQUEST, ACCEPTED, REJECTED, TTABLE-IS
    ];

    private static async Task<RecordingTelnetContext> Run(byte[] prefix)
    {
        var recorder = new RecordingTelnetContext();
        await using var machine = new TelnetCoreMachine(recorder);
        await machine.StartAsync();
        await machine.FireAsync(prefix);
        await machine.FireAsync(TelnetProbe.Resync);
        await machine.FireAsync(TelnetProbe.ProbeLine);
        return recorder;
    }

    [Test]
    [MethodDataSource(nameof(AllOptions))]
    public async Task AnOptionLeftMidSubnegotiationResynchronises(byte option)
    {
        var recorder = await Run([IAC, SB, option]);

        await Assert.That(TelnetProbe.Recovered(recorder)).IsTrue();
    }

    [Test]
    [MethodDataSource(nameof(AllOptions))]
    public async Task AnOptionLeftAfterAnIacInItsPayloadResynchronises(byte option)
    {
        var recorder = await Run([IAC, SB, option, 1, 2, 3, IAC]);

        await Assert.That(TelnetProbe.Recovered(recorder)).IsTrue();
    }

    [Test]
    [MethodDataSource(nameof(OptionCommands))]
    public async Task AnOptionLeftMidCommandResynchronises((byte Option, byte Command) pair)
    {
        var recorder = await Run([IAC, SB, pair.Option, pair.Command]);

        await Assert.That(TelnetProbe.Recovered(recorder)).IsTrue();
    }

    [Test]
    [MethodDataSource(nameof(OptionCommands))]
    public async Task AnOptionLeftMidCommandWithAPartialPayloadResynchronises((byte Option, byte Command) pair)
    {
        var recorder = await Run([IAC, SB, pair.Option, pair.Command, 65, 66, 67]);

        await Assert.That(TelnetProbe.Recovered(recorder)).IsTrue();
    }

    [Test]
    [MethodDataSource(nameof(AllOptions))]
    public async Task APendingVerbResynchronises(byte option)
    {
        foreach (var verb in new[] { WILL, WONT, DO, DONT })
        {
            var recorder = await Run([IAC, verb]);

            await Assert.That(TelnetProbe.Recovered(recorder)).IsTrue();
        }
    }

    [Test]
    public async Task ABareIacResynchronises()
    {
        var recorder = await Run([IAC]);

        await Assert.That(TelnetProbe.Recovered(recorder)).IsTrue();
    }

    [Test]
    public async Task APartialLineResynchronisesAndDoesNotContaminateTheProbe()
    {
        var recorder = await Run([.. "leftover"u8]);

        await Assert.That(TelnetProbe.Recovered(recorder)).IsTrue();
    }

    // -------------------------------------------------------------------------------------------
    // Why the token is two pairs and not one. These pin the reasoning so that a later shortening
    // of Resync fails here rather than silently weakening every property built on it.
    // -------------------------------------------------------------------------------------------

    [Test]
    public async Task OneIacSeDoesNotResynchroniseFromReadingOption()
    {
        var recorder = new RecordingTelnetContext();
        await using var machine = new TelnetCoreMachine(recorder);
        await machine.StartAsync();
        await machine.FireAsync([IAC, SB]);
        await machine.FireAsync([IAC, SE]);
        await machine.FireAsync(TelnetProbe.ProbeLine);

        await Assert.That(TelnetProbe.Recovered(recorder)).IsFalse();
    }

    [Test]
    public async Task OneIacSeDoesNotResynchroniseFromEndSubNegotiation()
    {
        var recorder = new RecordingTelnetContext();
        await using var machine = new TelnetCoreMachine(recorder);
        await machine.StartAsync();
        await machine.FireAsync([IAC, SB, 70, 1, IAC]);
        await machine.FireAsync([IAC, SE]);
        await machine.FireAsync(TelnetProbe.ProbeLine);

        await Assert.That(TelnetProbe.Recovered(recorder)).IsFalse();
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet run --project TelnetNegotiationCore.UnitTests -c Debug --framework net10.0 -- --treenode-filter "/*/*/ResyncTests/*"`

Expected: build failure — `TelnetProbe` does not exist.

- [ ] **Step 3: Write the probe**

Create `TelnetNegotiationCore.UnitTests/TelnetProbe.cs`:

```csharp
using System.Linq;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// The liveness oracle the generated properties share: a byte sequence that returns the machine to
/// <c>Idle</c> from any state, a probe line to send afterwards, and the check that it arrived.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Resync"/> is <c>IAC SE IAC SE</c>. One pair is not enough: from
/// <c>ReadingOption</c> the <c>IAC</c> is bound as the subnegotiation's option byte and the
/// <c>SE</c> becomes payload; from <c>EndSubNegotiation</c> the <c>IAC</c> is consumed as an
/// escaped literal 255 and the <c>SE</c> is payload again. Both leave the machine inside
/// <c>SubNegotiating</c>, which the second pair then ends. <c>Idle</c> is closed under the
/// sequence, because <c>IAC</c> moves to <c>StartNegotiation</c> and <c>SE</c> is not a command
/// there, so <c>UnknownCommand</c> returns to <c>Idle</c>.
/// </para>
/// <para>
/// The trailing <c>CR LF</c> flushes whatever partial line the preceding garbage accumulated, so
/// that <see cref="ProbeLine"/> is compared exactly rather than by suffix. Without it a stream
/// ending in <c>"leftover"</c> would submit <c>"leftoverREGRESSION_PROBE"</c>.
/// </para>
/// <para>
/// <c>ResyncTests</c> is the evidence that this works for all 28 options, and pins the two cases
/// that make a single pair insufficient. Shorten this and those tests fail.
/// </para>
/// </remarks>
internal static class TelnetProbe
{
    private const byte SE = 240;
    private const byte IAC = 255;

    /// <summary>Returns the machine to <c>Idle</c> from any state, then flushes the partial line.</summary>
    public static byte[] Resync { get; } = [IAC, SE, IAC, SE, (byte)'\r', (byte)'\n'];

    /// <summary>
    /// A line that no generator emits and no protocol payload contains, so that seeing it proves
    /// the machine is parsing text again rather than that a coincidence occurred.
    /// </summary>
    public static byte[] ProbeLine { get; } = [.. "REGRESSION_PROBE\r\n"u8];

    /// <summary>The text <see cref="ProbeLine"/> submits when the machine has recovered.</summary>
    public const string ProbeText = "REGRESSION_PROBE";

    /// <summary>Whether the probe line arrived as its own submitted line.</summary>
    public static bool Recovered(RecordingTelnetContext recorder) =>
        recorder.Lines.Any(line => line == ProbeText);
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet run --project TelnetNegotiationCore.UnitTests -c Debug --framework net10.0 -- --treenode-filter "/*/*/ResyncTests/*"`

Expected: all passed.

**If any case fails, stop and record it.** A module that `IAC SE IAC SE` does not resynchronise
is a wedge bug of exactly the class `MalformedSubnegotiationRecoveryTests` documents, and it is a
finding to report before continuing. Do not weaken the test to make it pass; either fix the
module or widen `Resync` with the reason written into its remarks.

- [ ] **Step 5: Validate against a mutant**

Temporarily change `Resync` to `[IAC, SE, (byte)'\r', (byte)'\n']` — a single pair. Re-run.
Expected: `AnOptionLeftMidSubnegotiationResynchronises` **fails** for every option, confirming
the test can actually tell the difference. Restore the two-pair token.

- [ ] **Step 6: Commit**

```bash
git add TelnetNegotiationCore.UnitTests/TelnetProbe.cs TelnetNegotiationCore.UnitTests/ResyncTests.cs
git commit -m "test(resync): establish the resynchronisation token for all 28 options"
```

---

### Task 3: Seeded randomness and shrinking

**Files:**
- Create: `TelnetNegotiationCore.UnitTests/Fuzzing/Rng.cs`
- Create: `TelnetNegotiationCore.UnitTests/Fuzzing/Shrink.cs`
- Test: `TelnetNegotiationCore.UnitTests/Fuzzing/RngTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `Rng(ulong seed)` with `Next(int exclusiveBound)`, `NextByte()`, `Bool(int percent)`,
  `Pick<T>(IReadOnlyList<T>)`; and `Shrink.Sequence<T>(IReadOnlyList<T> input, Func<IReadOnlyList<T>, bool> stillFails)`
  returning the smallest still-failing subsequence.

- [ ] **Step 1: Write the failing test**

Create `TelnetNegotiationCore.UnitTests/Fuzzing/RngTests.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests.Fuzzing;

public class RngTests
{
    [Test]
    public async Task TheSameSeedProducesTheSameSequence()
    {
        var first = new Rng(12345);
        var second = new Rng(12345);

        for (var i = 0; i < 100; i++)
        {
            await Assert.That(first.Next(1000)).IsEqualTo(second.Next(1000));
        }
    }

    [Test]
    public async Task DifferentSeedsDiverge()
    {
        var first = new Rng(1);
        var second = new Rng(2);

        var a = Enumerable.Range(0, 50).Select(_ => first.Next(1000)).ToArray();
        var b = Enumerable.Range(0, 50).Select(_ => second.Next(1000)).ToArray();

        await Assert.That(a.SequenceEqual(b)).IsFalse();
    }

    [Test]
    public async Task NextStaysInsideItsBound()
    {
        var rng = new Rng(99);

        for (var i = 0; i < 10_000; i++)
        {
            var value = rng.Next(7);
            await Assert.That(value).IsGreaterThanOrEqualTo(0);
            await Assert.That(value).IsLessThan(7);
        }
    }

    [Test]
    public async Task EveryByteValueIsReachable()
    {
        var rng = new Rng(7);
        var seen = new HashSet<byte>();

        for (var i = 0; i < 100_000; i++)
        {
            seen.Add(rng.NextByte());
        }

        await Assert.That(seen.Count).IsEqualTo(256);
    }

    [Test]
    public async Task ShrinkFindsTheSingleOffendingElement()
    {
        var input = Enumerable.Range(0, 40).ToArray();

        // "Still fails" means the sequence contains 17. The minimum such sequence is [17].
        var shrunk = Shrink.Sequence<int>(input, candidate => candidate.Contains(17));

        await Assert.That(shrunk.Count).IsEqualTo(1);
        await Assert.That(shrunk[0]).IsEqualTo(17);
    }

    [Test]
    public async Task ShrinkKeepsAPairThatMustTravelTogether()
    {
        var input = Enumerable.Range(0, 40).ToArray();

        var shrunk = Shrink.Sequence<int>(input, candidate => candidate.Contains(3) && candidate.Contains(9));

        await Assert.That(shrunk.Count).IsEqualTo(2);
        await Assert.That(shrunk.Contains(3)).IsTrue();
        await Assert.That(shrunk.Contains(9)).IsTrue();
    }

    [Test]
    public async Task ShrinkReturnsTheInputWhenNothingCanBeRemoved()
    {
        var shrunk = Shrink.Sequence<int>([1, 2], _ => false);

        await Assert.That(shrunk.Count).IsEqualTo(2);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet run --project TelnetNegotiationCore.UnitTests -c Debug --framework net10.0 -- --treenode-filter "/*/*/RngTests/*"`

Expected: build failure — `Rng` and `Shrink` do not exist.

- [ ] **Step 3: Write the implementation**

Create `TelnetNegotiationCore.UnitTests/Fuzzing/Rng.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace TelnetNegotiationCore.UnitTests.Fuzzing;

/// <summary>
/// A seeded xorshift64* generator. <see cref="System.Random"/> is not used because its sequence is
/// not contractually stable across runtimes, and these tests run on three of them: a case that
/// fails on net8.0 must be reproducible on net11.0 from the same seed, or a failure cannot be
/// investigated.
/// </summary>
internal sealed class Rng(ulong seed)
{
    // Any non-zero state will do; xorshift is undefined at zero, and a caller passing seed 0 is
    // asking for a default sequence rather than for undefined behaviour.
    private ulong _state = seed == 0 ? 0x9E3779B97F4A7C15 : seed;

    private ulong NextUInt64()
    {
        var x = _state;
        x ^= x >> 12;
        x ^= x << 25;
        x ^= x >> 27;
        _state = x;
        return x * 0x2545F4914F6CDD1D;
    }

    /// <summary>A value in <c>[0, exclusiveBound)</c>.</summary>
    public int Next(int exclusiveBound)
    {
        if (exclusiveBound <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(exclusiveBound));
        }

        // The modulo bias here is under one part in 2^55 for any bound these tests use, which is
        // far below the point where it could shape a result.
        return (int)(NextUInt64() % (ulong)exclusiveBound);
    }

    public byte NextByte() => (byte)(NextUInt64() >> 56);

    /// <summary>True with the given percent probability.</summary>
    public bool Bool(int percent) => Next(100) < percent;

    public T Pick<T>(IReadOnlyList<T> values) => values[Next(values.Count)];
}
```

Create `TelnetNegotiationCore.UnitTests/Fuzzing/Shrink.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;

namespace TelnetNegotiationCore.UnitTests.Fuzzing;

/// <summary>
/// Reduces a failing sequence to a minimal still-failing one, so that a counterexample is small
/// enough to read and to paste into a named regression test.
/// </summary>
internal static class Shrink
{
    /// <summary>
    /// Delta debugging by halves: try removing progressively smaller contiguous runs, and keep any
    /// removal that leaves the sequence still failing. Terminates because the granularity strictly
    /// decreases and the sequence never grows.
    /// </summary>
    /// <param name="input">A sequence known to fail.</param>
    /// <param name="stillFails">Whether a candidate subsequence still fails.</param>
    /// <returns>The smallest subsequence found that still fails, or <paramref name="input"/> if none is smaller.</returns>
    public static IReadOnlyList<T> Sequence<T>(IReadOnlyList<T> input, Func<IReadOnlyList<T>, bool> stillFails)
    {
        var current = input.ToList();
        var granularity = current.Count;

        while (granularity >= 1)
        {
            var removedSomething = false;

            for (var start = 0; start + granularity <= current.Count;)
            {
                var candidate = new List<T>(current.Count - granularity);
                candidate.AddRange(current.Take(start));
                candidate.AddRange(current.Skip(start + granularity));

                if (candidate.Count > 0 && stillFails(candidate))
                {
                    current = candidate;
                    removedSomething = true;

                    // Do not advance: the window now covers fresh elements.
                    continue;
                }

                start++;
            }

            if (!removedSomething)
            {
                granularity /= 2;
            }
        }

        return current;
    }
}
```

Add `using System;` to `Shrink.cs` for `Func<>`.

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet run --project TelnetNegotiationCore.UnitTests -c Debug --framework net10.0 -- --treenode-filter "/*/*/RngTests/*"`

Expected: 7 passed.

- [ ] **Step 5: Commit**

```bash
git add TelnetNegotiationCore.UnitTests/Fuzzing/
git commit -m "test(fuzz): add a seeded generator and a delta-debugging shrinker"
```

---

### Task 4: The token generator

Frames are built from the grammars in the spec's appendix, read from the RFCs. Do not read a
grammar out of the implementation: a generator that agrees with the code under test cannot
disagree with its bugs.

**Files:**
- Create: `TelnetNegotiationCore.UnitTests/Fuzzing/TelnetTokens.cs`
- Test: `TelnetNegotiationCore.UnitTests/Fuzzing/TelnetTokensTests.cs`

**Interfaces:**
- Consumes: `Rng` from Task 3.
- Produces: `TelnetTokens.Token` (a `sealed record` carrying `string Kind` and `byte[] Bytes`),
  `TelnetTokens.NextToken(Rng)` returning one `Token`, and
  `TelnetTokens.Stream(Rng, int maxTokens)` returning `List<Token>`. Callers flatten with
  `TelnetTokens.Flatten(IReadOnlyList<Token>)` to `byte[]`.

- [ ] **Step 1: Write the failing test**

Create `TelnetNegotiationCore.UnitTests/Fuzzing/TelnetTokensTests.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests.Fuzzing;

public class TelnetTokensTests
{
    [Test]
    public async Task TheSameSeedProducesTheSameStream()
    {
        var a = TelnetTokens.Flatten(TelnetTokens.Stream(new Rng(4242), 20));
        var b = TelnetTokens.Flatten(TelnetTokens.Stream(new Rng(4242), 20));

        await Assert.That(a.SequenceEqual(b)).IsTrue();
    }

    [Test]
    public async Task AStreamIsNeverEmptyAndRespectsItsTokenCeiling()
    {
        for (ulong seed = 1; seed <= 200; seed++)
        {
            var tokens = TelnetTokens.Stream(new Rng(seed), 12);

            await Assert.That(tokens.Count).IsGreaterThan(0);
            await Assert.That(tokens.Count).IsLessThanOrEqualTo(12);
        }
    }

    /// <summary>
    /// The whole point of tokens rather than random bytes: a meaningful share of generated streams
    /// must contain a well-formed frame, because that is where the state machine lives. Uniform
    /// noise would essentially never produce one.
    /// </summary>
    [Test]
    public async Task WellFormedFramesAreCommon()
    {
        var wellFormed = 0;

        for (ulong seed = 1; seed <= 1000; seed++)
        {
            if (TelnetTokens.Stream(new Rng(seed), 8).Any(t => t.Kind == "frame"))
            {
                wellFormed++;
            }
        }

        await Assert.That(wellFormed).IsGreaterThan(800);
    }

    [Test]
    public async Task EveryKindOfTokenIsReachable()
    {
        var kinds = new HashSet<string>();
        var rng = new Rng(31337);

        for (var i = 0; i < 20_000; i++)
        {
            kinds.Add(TelnetTokens.NextToken(rng).Kind);
        }

        await Assert.That(kinds).Contains("frame");
        await Assert.That(kinds).Contains("text");
        await Assert.That(kinds).Contains("mutated");
        await Assert.That(kinds).Contains("verb");
        await Assert.That(kinds).Contains("adversarial");
    }

    [Test]
    public async Task AWellFormedFrameIsProperlyDelimited()
    {
        var rng = new Rng(555);

        for (var i = 0; i < 2000; i++)
        {
            var token = TelnetTokens.NextToken(rng);
            if (token.Kind != "frame")
            {
                continue;
            }

            await Assert.That(token.Bytes[0]).IsEqualTo((byte)255);
            await Assert.That(token.Bytes[1]).IsEqualTo((byte)250);
            await Assert.That(token.Bytes[^2]).IsEqualTo((byte)255);
            await Assert.That(token.Bytes[^1]).IsEqualTo((byte)240);
        }
    }

    /// <summary>
    /// A well-formed frame's payload must not contain an unescaped 255, or the frame would end
    /// early and the generator would be producing something other than what it claims.
    /// </summary>
    [Test]
    public async Task AWellFormedFramePayloadHasNoUnescapedIac()
    {
        var rng = new Rng(888);

        for (var i = 0; i < 2000; i++)
        {
            var token = TelnetTokens.NextToken(rng);
            if (token.Kind != "frame")
            {
                continue;
            }

            // Payload sits between IAC SB <option> and the trailing IAC SE.
            var payload = token.Bytes[3..^2];
            var index = 0;
            while (index < payload.Length)
            {
                if (payload[index] == 255)
                {
                    await Assert.That(index + 1).IsLessThan(payload.Length);
                    await Assert.That(payload[index + 1]).IsEqualTo((byte)255);
                    index += 2;
                    continue;
                }

                index++;
            }
        }
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet run --project TelnetNegotiationCore.UnitTests -c Debug --framework net10.0 -- --treenode-filter "/*/*/TelnetTokensTests/*"`

Expected: build failure — `TelnetTokens` does not exist.

- [ ] **Step 3: Write the generator**

Create `TelnetNegotiationCore.UnitTests/Fuzzing/TelnetTokens.cs`. Grammars are from the RFCs
named in each comment; see the spec appendix for the full citation list.

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

namespace TelnetNegotiationCore.UnitTests.Fuzzing;

/// <summary>
/// Builds telnet streams out of weighted tokens rather than random bytes.
/// </summary>
/// <remarks>
/// <para>
/// Uniform noise essentially never produces a well-formed <c>IAC SB &lt;option&gt; … IAC SE</c>
/// frame, and frame handling is where the state machine lives, so a byte-level fuzzer would spend
/// its whole budget in the text path. Tokens put the generator where the states are.
/// </para>
/// <para>
/// Every payload grammar here comes from the option's RFC, not from this library's
/// implementation of it. A generator that read its grammar out of the code under test would
/// agree with whatever that code gets wrong.
/// </para>
/// </remarks>
internal static class TelnetTokens
{
    private const byte SE = 240;
    private const byte NOP = 241;
    private const byte GA = 249;
    private const byte SB = 250;
    private const byte WILL = 251;
    private const byte WONT = 252;
    private const byte DO = 253;
    private const byte DONT = 254;
    private const byte IAC = 255;
    private const byte EOR = 239;

    /// <summary>One piece of a generated stream, tagged so tests can reason about the mix.</summary>
    internal sealed record Token(string Kind, byte[] Bytes);

    private static readonly byte[] Options =
    [
        1, 3, 24, 25, 31, 32, 33, 34, 35, 36, 37, 38, 39, 42, 69, 70, 85, 86, 87, 91, 201,
    ];

    private static readonly byte[] Verbs = [WILL, WONT, DO, DONT];

    public static List<Token> Stream(Rng rng, int maxTokens)
    {
        var count = 1 + rng.Next(maxTokens);
        var tokens = new List<Token>(count);
        for (var i = 0; i < count; i++)
        {
            tokens.Add(NextToken(rng));
        }

        return tokens;
    }

    public static byte[] Flatten(IReadOnlyList<Token> tokens) =>
        tokens.SelectMany(t => t.Bytes).ToArray();

    public static Token NextToken(Rng rng)
    {
        var roll = rng.Next(100);
        return roll switch
        {
            < 35 => new Token("frame", Frame(rng, rng.Pick(Options))),
            < 60 => new Token("text", Text(rng)),
            < 80 => new Token("mutated", Mutated(rng)),
            < 90 => new Token("verb", Verb(rng)),
            _ => new Token("adversarial", Adversarial(rng)),
        };
    }

    /// <summary>A well-formed frame: IAC SB option, an escaped payload, IAC SE.</summary>
    private static byte[] Frame(Rng rng, byte option)
    {
        var body = new List<byte> { IAC, SB, option };
        body.AddRange(Escaped(Payload(rng, option)));
        body.Add(IAC);
        body.Add(SE);
        return [.. body];
    }

    /// <summary>Doubles every 255, which every one of these grammars requires of its payload.</summary>
    private static List<byte> Escaped(IEnumerable<byte> payload)
    {
        var escaped = new List<byte>();
        foreach (var b in payload)
        {
            escaped.Add(b);
            if (b == IAC)
            {
                escaped.Add(IAC);
            }
        }

        return escaped;
    }

    /// <summary>
    /// The payload for one option, from its RFC. Unescaped: <see cref="Escaped"/> handles 255.
    /// </summary>
    private static List<byte> Payload(Rng rng, byte option) => option switch
    {
        // NAWS, RFC 1073: width and height, two bytes each, network byte order. Zero means
        // "not being sent", which is a distinct case from a genuine zero.
        31 => [.. Sixteen(rng), .. Sixteen(rng)],

        // TERMINAL-TYPE, RFC 1091: IS name, or SEND. Name is NVT ASCII, max 40 characters.
        24 => rng.Bool(50) ? [0, .. Ascii(rng, 40)] : [1],

        // TERMINAL-SPEED, RFC 1079: IS "<transmit>,<receive>" as decimal ASCII, or SEND.
        32 => rng.Bool(50)
            ? [0, .. Digits(rng), (byte)',', .. Digits(rng)]
            : [1],

        // TOGGLE-FLOW-CONTROL, RFC 1372: one sub-command byte, no further payload. Codes other
        // than 0-3 must be silently ignored, so generate beyond the range too.
        33 => [(byte)rng.Next(6)],

        // LINEMODE, RFC 1184: MODE mask, FORWARDMASK, or SLC triplets.
        34 => rng.Next(3) switch
        {
            0 => [1, (byte)rng.Next(32)],                       // MODE + mask (EDIT|TRAPSIG|ACK|SOFT_TAB|LIT_ECHO)
            1 => [2, .. RandomBytes(rng, 1 + rng.Next(32))],    // FORWARDMASK
            _ => [3, .. SlcTriplets(rng)],                       // SLC
        },

        // X-DISPLAY-LOCATION, RFC 1096: IS "<host>:<dispnum>[.<screennum>]", or SEND.
        35 => rng.Bool(50)
            ? [0, .. Ascii(rng, 20), (byte)':', .. Digits(rng)]
            : [1],

        // ENVIRON (36, RFC 1408) and NEW-ENVIRON (39, RFC 1572) share their codes:
        // IS=0 SEND=1 INFO=2, VAR=0 VALUE=1 ESC=2 USERVAR=3, with ESC escaping the four type bytes.
        36 or 39 => EnvironPayload(rng),

        // AUTHENTICATION, RFC 2941: IS/SEND/REPLY/NAME, then two-octet type pairs.
        37 => [(byte)rng.Next(4), .. AuthPairs(rng)],

        // ENCRYPT, RFC 2946: IS/SUPPORT/REPLY/START/END/REQUEST-START/REQUEST-END/ENC_KEYID/DEC_KEYID.
        // A START keyid is at least one byte, most significant first; zero means the default key.
        38 => rng.Next(9) switch
        {
            3 => [3, .. RandomBytes(rng, 1 + rng.Next(8))],     // START + keyid
            4 => [4],                                            // END
            var command => [(byte)command, .. RandomBytes(rng, rng.Next(4))],
        },

        // CHARSET, RFC 2066: REQUEST optionally prefixed "[TTABLE]<version>", then a charset list
        // whose separator is chosen by the sender and may be any octet except IAC. That the
        // separator is peer-controlled is the sharpest target in the set.
        42 => CharsetPayload(rng),

        // MSDP: VAR=1 VAL=2 TABLE_OPEN=3 TABLE_CLOSE=4 ARRAY_OPEN=5 ARRAY_CLOSE=6, nestable, so
        // unbalanced and deeply nested payloads are the cases that matter.
        69 => MsdpPayload(rng, 0),

        // MSSP: VAR=1 VAL=2 in alternating pairs.
        70 => MsspPayload(rng),

        // GMCP: a package name, a space, then JSON.
        201 => [.. Ascii(rng, 12), (byte)' ', .. GmcpJson(rng)],

        // MCCP2 (86) and MCCP3 (87) markers carry no payload; MCCP1 (85) is IAC SB 85 WILL SE.
        85 => [WILL],
        86 or 87 => [],

        // MXP (91) and everything else: a short opaque payload.
        _ => RandomBytes(rng, rng.Next(8)),
    };

    private static List<byte> EnvironPayload(Rng rng)
    {
        const byte Var = 0;
        const byte Value = 1;
        const byte Esc = 2;
        const byte UserVar = 3;

        var command = (byte)rng.Next(3);           // IS, SEND, INFO
        var payload = new List<byte> { command };

        var pairs = rng.Next(3);
        for (var i = 0; i < pairs; i++)
        {
            payload.Add(rng.Bool(50) ? Var : UserVar);
            payload.AddRange(EnvironEscaped(rng, Ascii(rng, 8)));

            if (rng.Bool(70))
            {
                payload.Add(Value);
                payload.AddRange(EnvironEscaped(rng, Ascii(rng, 8)));
            }
        }

        // An ESC as the final byte escapes nothing. The RFC does not define it, so the only
        // defensible contract is that it must not wedge.
        if (rng.Bool(10))
        {
            payload.Add(Esc);
        }

        return payload;

        static List<byte> EnvironEscaped(Rng rng, IEnumerable<byte> name)
        {
            var escaped = new List<byte>();
            foreach (var b in name)
            {
                // RFC 1572: a VAR, VALUE, USERVAR or ESC inside a name or value is preceded by ESC.
                if (b is Var or Value or Esc or UserVar)
                {
                    escaped.Add(Esc);
                }

                escaped.Add(b);
            }

            return escaped;
        }
    }

    private static List<byte> AuthPairs(Rng rng)
    {
        var pairs = new List<byte>();
        var count = rng.Next(4);
        for (var i = 0; i < count; i++)
        {
            pairs.Add((byte)rng.Next(16));              // authentication type, 0-15 defined
            pairs.Add((byte)rng.Next(32));              // WHO|HOW|ENCRYPT|INI_CRED_FWD modifier bits
        }

        // An odd-length list of pairs is malformed, which is exactly why it is generated.
        if (rng.Bool(15))
        {
            pairs.Add((byte)rng.Next(16));
        }

        return pairs;
    }

    private static List<byte> CharsetPayload(Rng rng)
    {
        var command = rng.Next(7) + 1;                  // REQUEST..TTABLE-NAK
        if (command != 1)
        {
            return [(byte)command, .. Ascii(rng, 10)];
        }

        var payload = new List<byte> { 1 };

        if (rng.Bool(25))
        {
            payload.AddRange("[TTABLE]"u8);
            payload.Add((byte)(1 + rng.Next(3)));       // version, non-zero
        }

        // Any octet except IAC may be the separator, and the peer picks it. A digit, a space, or
        // a byte that also occurs inside a charset name are all legal and all interesting.
        var separators = new byte[] { (byte)';', (byte)' ', (byte)',', (byte)'A', (byte)'0', 0 };
        var separator = rng.Pick(separators);

        var names = 1 + rng.Next(3);
        for (var i = 0; i < names; i++)
        {
            payload.Add(separator);
            payload.AddRange(Ascii(rng, 8));
        }

        return payload;
    }

    private static List<byte> MsdpPayload(Rng rng, int depth)
    {
        const byte Var = 1;
        const byte Val = 2;
        const byte TableOpen = 3;
        const byte TableClose = 4;
        const byte ArrayOpen = 5;
        const byte ArrayClose = 6;

        var payload = new List<byte> { Var };
        payload.AddRange(Ascii(rng, 8));
        payload.Add(Val);

        // Depth is capped so the generator cannot recurse itself to death; unbalanced payloads
        // come from the 20% chance of omitting the close, not from unbounded nesting.
        if (depth < 3 && rng.Bool(30))
        {
            var open = rng.Bool(50) ? TableOpen : ArrayOpen;
            payload.Add(open);
            payload.AddRange(MsdpPayload(rng, depth + 1));

            if (rng.Bool(80))
            {
                payload.Add(open == TableOpen ? TableClose : ArrayClose);
            }

            return payload;
        }

        payload.AddRange(Ascii(rng, 8));
        return payload;
    }

    private static List<byte> MsspPayload(Rng rng)
    {
        var payload = new List<byte>();
        var pairs = 1 + rng.Next(4);
        for (var i = 0; i < pairs; i++)
        {
            payload.Add(1);                             // MSSP_VAR
            payload.AddRange(Ascii(rng, 10));
            payload.Add(2);                             // MSSP_VAL
            payload.AddRange(Ascii(rng, 10));
        }

        return payload;
    }

    private static List<byte> GmcpJson(Rng rng) => rng.Next(4) switch
    {
        0 => [.. "{}"u8],
        1 => [.. "{\"a\":1}"u8],
        2 => [.. "[1,2,3]"u8],
        _ => [.. "{\"broken\":"u8],                     // deliberately invalid JSON
    };

    private static List<byte> SlcTriplets(Rng rng)
    {
        var triplets = new List<byte>();
        var count = 1 + rng.Next(4);
        for (var i = 0; i < count; i++)
        {
            triplets.Add((byte)(1 + rng.Next(30)));     // SLC_SYNCH..SLC_EEOL
            triplets.Add((byte)rng.Next(256));          // level in bits 0-1, ACK|FLUSHIN|FLUSHOUT above
            triplets.Add(rng.NextByte());               // the character
        }

        // A truncated triplet is undefined by the RFC, so "must not wedge" is the only contract.
        if (rng.Bool(20))
        {
            triplets.Add((byte)(1 + rng.Next(30)));
            if (rng.Bool(50))
            {
                triplets.Add((byte)rng.Next(256));
            }
        }

        return triplets;
    }

    /// <summary>Plain text, weighted towards the line-ending cases that the core machine branches on.</summary>
    private static byte[] Text(Rng rng) => rng.Next(10) switch
    {
        0 => [.. "hello\r\n"u8],
        1 => [.. "bare-lf\n"u8],
        2 => [.. "bare-cr\r"u8],
        3 => [(byte)'c', (byte)'r', (byte)'-', (byte)'n', (byte)'u', (byte)'l', (byte)'\r', 0],
        4 => [.. "\r\n"u8],
        5 => [(byte)'\n'],
        6 => [(byte)'\r'],
        7 => [.. RandomHighBytes(rng, 1 + rng.Next(8))],
        8 => [],
        _ => [.. Ascii(rng, 1 + rng.Next(16))],
    };

    /// <summary>A mutation of a well-formed frame: the 20% slice where most bugs should live.</summary>
    private static byte[] Mutated(Rng rng)
    {
        var frame = Frame(rng, rng.Pick(Options));

        return rng.Next(6) switch
        {
            // Truncated at an arbitrary offset.
            0 => frame[..(1 + rng.Next(frame.Length))],

            // Missing its terminating SE.
            1 => frame[..^1],

            // Option byte replaced, including with an option nothing claims.
            2 => Replace(frame, 2, rng.NextByte()),

            // A stray IAC injected somewhere in the payload.
            3 => Insert(frame, 3 + rng.Next(Math.Max(1, frame.Length - 4)), IAC),

            // An SB nested inside, which no grammar permits.
            4 => Insert(frame, 3 + rng.Next(Math.Max(1, frame.Length - 4)), SB),

            // A random byte flipped anywhere.
            _ => Replace(frame, rng.Next(frame.Length), rng.NextByte()),
        };

        static byte[] Replace(byte[] source, int index, byte value)
        {
            var copy = source.ToArray();
            copy[index] = value;
            return copy;
        }

        static byte[] Insert(byte[] source, int index, byte value)
        {
            var copy = new List<byte>(source);
            copy.Insert(Math.Min(index, copy.Count), value);
            return [.. copy];
        }
    }

    /// <summary>
    /// A bare verb sequence, including the case <c>WillInterrupted</c> exists for: a fresh IAC
    /// where an option byte was expected abandons the pending negotiation.
    /// </summary>
    private static byte[] Verb(Rng rng) => rng.Next(5) switch
    {
        0 => [IAC, rng.Pick(Verbs), rng.Pick(Options)],
        1 => [IAC, rng.Pick(Verbs), rng.NextByte()],
        2 => [IAC, rng.Pick(Verbs)],                                    // pending option byte
        3 => [IAC, rng.Pick(Verbs), IAC, rng.Pick(Verbs), rng.Pick(Options)],  // interrupted
        _ => [IAC, rng.Pick<byte>([NOP, GA, EOR])],
    };

    private static byte[] Adversarial(Rng rng) => rng.Next(8) switch
    {
        0 => [IAC],
        1 => [IAC, IAC],
        2 => [IAC, SE],
        3 => [IAC, SB],
        4 => [IAC, SB, IAC, SE],
        5 => [IAC, rng.NextByte()],
        6 => [.. RandomBytes(rng, 1 + rng.Next(16))],
        _ => [IAC, SB, rng.Pick(Options), IAC, SB, rng.Pick(Options), IAC, SE],
    };

    private static byte[] Sixteen(Rng rng) => rng.Next(5) switch
    {
        0 => [0, 0],                                    // "not being sent"
        1 => [255, 255],                                 // 65535, and two IACs to escape
        2 => [0, 80],
        3 => [0, 24],
        _ => [rng.NextByte(), rng.NextByte()],
    };

    private static byte[] Ascii(Rng rng, int maxLength)
    {
        var length = rng.Next(maxLength + 1);
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            bytes[i] = (byte)(33 + rng.Next(94));       // printable, no space
        }

        return bytes;
    }

    private static byte[] Digits(Rng rng)
    {
        var length = 1 + rng.Next(5);
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            bytes[i] = (byte)((byte)'0' + rng.Next(10));
        }

        return bytes;
    }

    private static byte[] RandomBytes(Rng rng, int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            bytes[i] = rng.NextByte();
        }

        return bytes;
    }

    private static byte[] RandomHighBytes(Rng rng, int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            bytes[i] = (byte)(128 + rng.Next(127));     // high, but never 255
        }

        return bytes;
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet run --project TelnetNegotiationCore.UnitTests -c Debug --framework net10.0 -- --treenode-filter "/*/*/TelnetTokensTests/*"`

Expected: 6 passed. If `WellFormedFramesAreCommon` fails, the weights are wrong, not the test.

- [ ] **Step 5: Commit**

```bash
git add TelnetNegotiationCore.UnitTests/Fuzzing/TelnetTokens.cs TelnetNegotiationCore.UnitTests/Fuzzing/TelnetTokensTests.cs
git commit -m "test(fuzz): generate telnet streams from RFC-grounded frame grammars"
```

---

### Task 5: Properties 2 and 3 — no-throw and no-wedge

**Files:**
- Create: `TelnetNegotiationCore.UnitTests/Fuzzing/EngineProperties.cs`
- Create: `TelnetNegotiationCore.UnitTests/Fuzzing/PropertyRunner.cs`

**Interfaces:**
- Consumes: `Rng`, `Shrink`, `TelnetTokens`, `TelnetProbe`, `RecordingTelnetContext.Snapshot()`.
- Produces: `PropertyRunner.ForEachStream(ulong seed, int cases, int maxTokens, Func<byte[], Task<string?>> check)`
  which returns `null` on success or a formatted failure report naming the shrunk counterexample
  as a C# literal.

- [ ] **Step 1: Write the property runner**

Create `TelnetNegotiationCore.UnitTests/Fuzzing/PropertyRunner.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TelnetNegotiationCore.UnitTests.Fuzzing;

/// <summary>
/// Runs one property over a generated corpus, and on failure shrinks the offending stream to a
/// minimal one and formats it as a C# literal.
/// </summary>
/// <remarks>
/// The literal matters more than the seed. A seed reproduces a failure only while the generator is
/// unchanged; a byte array reproduces it forever. Every counterexample this prints is meant to be
/// pasted into a named test of its own, which is where it keeps its value.
/// </remarks>
internal static class PropertyRunner
{
    /// <summary>
    /// Checks <paramref name="check"/> against <paramref name="cases"/> generated streams.
    /// </summary>
    /// <param name="check">Returns null when the stream satisfies the property, or a reason when it does not.</param>
    /// <returns>Null when every case passed, or a report naming the smallest failing stream.</returns>
    public static async Task<string?> ForEachStream(
        ulong seed,
        int cases,
        int maxTokens,
        Func<byte[], Task<string?>> check)
    {
        for (var i = 0; i < cases; i++)
        {
            // Each case gets its own generator, derived from the run's seed, so that case 900 is
            // reproducible without replaying the 899 before it.
            var rng = new Rng(seed + (ulong)i * 0x9E3779B97F4A7C15);
            var tokens = TelnetTokens.Stream(rng, maxTokens);
            var bytes = TelnetTokens.Flatten(tokens);

            var reason = await check(bytes);
            if (reason is null)
            {
                continue;
            }

            var shrunk = await ShrinkTokens(tokens, check);
            return Report(seed, i, reason, shrunk);
        }

        return null;
    }

    private static async Task<byte[]> ShrinkTokens(
        IReadOnlyList<TelnetTokens.Token> tokens,
        Func<byte[], Task<string?>> check)
    {
        // Shrink at token granularity first, which keeps frames intact and so keeps the
        // counterexample readable, then at byte granularity for the final squeeze.
        var byToken = Shrink.Sequence(tokens, candidate =>
            check(TelnetTokens.Flatten(candidate)).GetAwaiter().GetResult() is not null);

        var bytes = TelnetTokens.Flatten(byToken);

        var byByte = Shrink.Sequence<byte>(bytes, candidate =>
            check(candidate.ToArray()).GetAwaiter().GetResult() is not null);

        return byByte.ToArray();
    }

    private static string Report(ulong seed, int caseIndex, string reason, byte[] shrunk)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Property failed on case {caseIndex} of seed {seed}.");
        sb.AppendLine($"Reason: {reason}");
        sb.AppendLine($"Shrunk to {shrunk.Length} bytes. Paste this into a named regression test:");
        sb.Append("    byte[] bytes = [");
        sb.Append(string.Join(", ", shrunk.Select(b => b.ToString())));
        sb.AppendLine("];");
        return sb.ToString();
    }
}
```

- [ ] **Step 2: Write the two properties**

Create `TelnetNegotiationCore.UnitTests/Fuzzing/EngineProperties.cs`:

```csharp
using System;
using System.Threading.Tasks;
using TelnetNegotiationCore.Machine;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests.Fuzzing;

/// <summary>
/// Invariants the core machine must hold for every input, asserted over a generated corpus rather
/// than over hand-picked examples.
/// </summary>
public class EngineProperties
{
    private const int Cases = 3000;
    private const int MaxTokens = 10;

    [Test]
    public async Task FiringAnyStreamNeverThrows()
    {
        var failure = await PropertyRunner.ForEachStream(0xA11CE, Cases, MaxTokens, async bytes =>
        {
            try
            {
                var recorder = new RecordingTelnetContext();
                await using var machine = new TelnetCoreMachine(recorder);
                await machine.StartAsync();
                await machine.FireAsync(bytes);
                return null;
            }
            catch (Exception ex)
            {
                // A throw here lands on a consumer's read loop, where there is nothing useful to
                // do with it. Any exception at all is the failure.
                return $"{ex.GetType().Name}: {ex.Message}";
            }
        });

        await Assert.That(failure).IsNull();
    }

    [Test]
    public async Task AnyStreamRecoversAndKeepsParsingText()
    {
        var failure = await PropertyRunner.ForEachStream(0xB0B, Cases, MaxTokens, async bytes =>
        {
            var recorder = new RecordingTelnetContext();
            await using var machine = new TelnetCoreMachine(recorder);
            await machine.StartAsync();
            await machine.FireAsync(bytes);
            await machine.FireAsync(TelnetProbe.Resync);
            await machine.FireAsync(TelnetProbe.ProbeLine);

            return TelnetProbe.Recovered(recorder)
                ? null
                : "the probe line did not arrive after the resync: the machine is wedged";
        });

        await Assert.That(failure).IsNull();
    }
}
```

- [ ] **Step 3: Run the two properties**

Run: `dotnet run --project TelnetNegotiationCore.UnitTests -c Debug --framework net10.0 -- --treenode-filter "/*/*/EngineProperties/*"`

Expected: 2 passed. **If either fails, that is a real finding.** Read the shrunk literal from
the failure output, add it as its own named test in the appropriate existing file
(`MalformedSubnegotiationRecoveryTests` for a wedge, a new file for a throw), and investigate the
module. Do not weaken the property.

- [ ] **Step 4: Validate both against a mutant**

Temporarily change `TelnetProbe.Resync` to `[IAC, SE]` — a single pair. Re-run. Expected:
`AnyStreamRecoversAndKeepsParsingText` **fails** and prints a shrunk literal. This confirms the
property can fail and that shrinking works. Restore the token.

- [ ] **Step 5: Check the runtime budget**

Run the full suite and note the duration.

Run: `dotnet run --project TelnetNegotiationCore.UnitTests -c Release --framework net10.0`

Expected: all passed, total duration under 25s. If these two properties alone cost more than
about 4s, reduce `Cases`.

- [ ] **Step 6: Commit**

```bash
git add TelnetNegotiationCore.UnitTests/Fuzzing/PropertyRunner.cs TelnetNegotiationCore.UnitTests/Fuzzing/EngineProperties.cs
git commit -m "test(fuzz): assert the engine never throws and never wedges"
```

---

### Task 6: Property 5 — text transparency

**Files:**
- Modify: `TelnetNegotiationCore.UnitTests/Fuzzing/EngineProperties.cs`

**Interfaces:**
- Consumes: everything from Task 5.
- Produces: nothing new.

- [ ] **Step 1: Write the failing test**

Append to `EngineProperties`:

```csharp
    /// <summary>
    /// For input with no IAC in it, every byte must reach the context exactly once and in order.
    /// The machine is allowed to interpret line endings — that is its job — so the comparison
    /// normalises CR LF, CR NUL and a bare CR to a single line break and then requires equality.
    /// </summary>
    [Test]
    public async Task IacFreeTextArrivesIntactAndInOrder()
    {
        var failure = await PropertyRunner.ForEachStream(0xC0FFEE, Cases, MaxTokens, async bytes =>
        {
            // Only the text path is under test here, so anything containing an IAC is skipped
            // rather than reshaped: reshaping would silently change what is being asserted.
            foreach (var b in bytes)
            {
                if (b == 255)
                {
                    return null;
                }
            }

            var recorder = new RecordingTelnetContext();
            await using var machine = new TelnetCoreMachine(recorder);
            await machine.StartAsync();
            await machine.FireAsync(bytes);

            var delivered = string.Join("\n", recorder.Lines);
            if (recorder.PendingText.Length > 0)
            {
                delivered = recorder.Lines.Count > 0
                    ? delivered + "\n" + recorder.PendingText
                    : recorder.PendingText;
            }

            var expected = Normalise(bytes);

            return delivered == expected
                ? null
                : $"expected {Describe(expected)} but the context received {Describe(delivered)}";
        });

        await Assert.That(failure).IsNull();

        static string Normalise(byte[] bytes)
        {
            var sb = new System.Text.StringBuilder();
            for (var i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] == (byte)'\r')
                {
                    // CR LF and CR NUL are both one line break; a bare CR is too.
                    if (i + 1 < bytes.Length && (bytes[i + 1] == (byte)'\n' || bytes[i + 1] == 0))
                    {
                        i++;
                    }

                    sb.Append('\n');
                    continue;
                }

                sb.Append((char)bytes[i]);
            }

            return sb.ToString();
        }

        static string Describe(string value) =>
            value.Replace("\n", "\\n").Replace("\r", "\\r");
    }
```

- [ ] **Step 2: Run it**

Run: `dotnet run --project TelnetNegotiationCore.UnitTests -c Debug --framework net10.0 -- --treenode-filter "/*/*/EngineProperties/IacFreeTextArrivesIntactAndInOrder"`

Expected: PASS.

**If it fails**, read the counterexample carefully before changing anything. The likeliest cause
is that `Normalise` does not match the engine's actual, defensible line-ending policy — bare `LF`
handling in particular. Establish what the engine does from `TelnetCoreModule`'s
`EndOfLine`/`DropReturn` transitions and RFC 854's NVT rules, then correct whichever side is
wrong and write the reasoning into the test's remarks.

- [ ] **Step 3: Validate against a mutant**

Temporarily make `Normalise` drop every `'a'`. Re-run. Expected: the property **fails**. Restore.

- [ ] **Step 4: Commit**

```bash
git add TelnetNegotiationCore.UnitTests/Fuzzing/EngineProperties.cs
git commit -m "test(fuzz): assert IAC-free text reaches the context intact"
```

---

### Task 7: Property 1 — fragmentation invariance

The highest-value property in the plan. Every `ref self` field in `TelnetStates.cs` is state that
must survive a chunk boundary, and a real socket splits wherever it likes.

**Files:**
- Create: `TelnetNegotiationCore.UnitTests/Fuzzing/FragmentationProperties.cs`

**Interfaces:**
- Consumes: `Rng`, `TelnetTokens`, `PropertyRunner`, `RecordingTelnetContext.Snapshot()`.
- Produces: nothing new.

- [ ] **Step 1: Write the failing test**

```csharp
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TelnetNegotiationCore.Machine;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests.Fuzzing;

/// <summary>
/// A stream fed in chunks must be indistinguishable from the same stream fed whole.
/// </summary>
/// <remarks>
/// <para>
/// This is the property a state-machine rewrite most needs. Every <c>ref self</c> field in
/// <c>TelnetStates.cs</c> — <c>SubNegotiation.Option</c>, <c>Connected.Width</c>,
/// <c>Connected.Height</c>, and each protocol module's own — is state that has to survive a chunk
/// boundary, and a socket splits wherever it likes. A machine that reads a two-byte NAWS dimension
/// correctly in one call and incorrectly across two calls is broken in a way no example-based test
/// in this suite would notice.
/// </para>
/// <para>
/// The comparison is <see cref="RecordingTelnetContext.Snapshot"/>, which is deliberately blind to
/// how byte runs batched into <c>Write</c> calls; see its remarks. Chunking changes that batching
/// legitimately, and a comparison that could see it would fail on every case for no reason.
/// </para>
/// </remarks>
public class FragmentationProperties
{
    // Each case runs the stream several times over, so this gets a smaller budget than the
    // single-pass properties.
    private const int Cases = 1000;
    private const int MaxTokens = 8;

    private static async Task<string> Whole(byte[] bytes)
    {
        var recorder = new RecordingTelnetContext();
        await using var machine = new TelnetCoreMachine(recorder);
        await machine.StartAsync();
        await machine.FireAsync(bytes);
        return recorder.Snapshot();
    }

    private static async Task<string> InChunks(byte[] bytes, IReadOnlyList<int> boundaries)
    {
        var recorder = new RecordingTelnetContext();
        await using var machine = new TelnetCoreMachine(recorder);
        await machine.StartAsync();

        var start = 0;
        foreach (var boundary in boundaries)
        {
            if (boundary <= start || boundary > bytes.Length)
            {
                continue;
            }

            await machine.FireAsync(bytes[start..boundary]);
            start = boundary;
        }

        if (start < bytes.Length)
        {
            await machine.FireAsync(bytes[start..]);
        }

        return recorder.Snapshot();
    }

    [Test]
    public async Task OneByteAtATimeIsTheSameAsAllAtOnce()
    {
        var failure = await PropertyRunner.ForEachStream(0xD15EA5E, Cases, MaxTokens, async bytes =>
        {
            var whole = await Whole(bytes);
            var split = await InChunks(bytes, Enumerable.Range(1, bytes.Length).ToArray());

            return whole == split
                ? null
                : $"whole:   {whole}\n            chunked: {split}";
        });

        await Assert.That(failure).IsNull();
    }

    [Test]
    public async Task AnyChunkingIsTheSameAsAllAtOnce()
    {
        var failure = await PropertyRunner.ForEachStream(0xE1F, Cases, MaxTokens, async bytes =>
        {
            var whole = await Whole(bytes);

            // Several independent chunkings per stream, each derived from the stream's own
            // content so the choice is deterministic without threading another seed through.
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var rng = new Rng((ulong)(bytes.Length + 1) * 0x100000001B3 + (ulong)attempt);
                var boundaries = new List<int>();
                for (var i = 1; i < bytes.Length; i++)
                {
                    if (rng.Bool(30))
                    {
                        boundaries.Add(i);
                    }
                }

                var split = await InChunks(bytes, boundaries);
                if (whole != split)
                {
                    return $"boundaries [{string.Join(", ", boundaries)}]\n            whole:   {whole}\n            chunked: {split}";
                }
            }

            return null;
        });

        await Assert.That(failure).IsNull();
    }

    /// <summary>
    /// An empty chunk is what a socket hands over on a zero-length read, and it must change
    /// nothing at all.
    /// </summary>
    [Test]
    public async Task EmptyChunksChangeNothing()
    {
        var failure = await PropertyRunner.ForEachStream(0xF00D, Cases, MaxTokens, async bytes =>
        {
            var whole = await Whole(bytes);

            var recorder = new RecordingTelnetContext();
            await using var machine = new TelnetCoreMachine(recorder);
            await machine.StartAsync();
            await machine.FireAsync([]);
            foreach (var b in bytes)
            {
                await machine.FireAsync([b]);
                await machine.FireAsync([]);
            }

            return whole == recorder.Snapshot()
                ? null
                : $"whole:   {whole}\n            padded:  {recorder.Snapshot()}";
        });

        await Assert.That(failure).IsNull();
    }
}
```

- [ ] **Step 2: Run it**

Run: `dotnet run --project TelnetNegotiationCore.UnitTests -c Debug --framework net10.0 -- --treenode-filter "/*/*/FragmentationProperties/*"`

Expected: 3 passed — **but expect failures here.** This is the property most likely to find a
real bug in the delta.

For each failure: read the shrunk literal, add it to a new file
`TelnetNegotiationCore.UnitTests/FragmentationRegressionTests.cs` as a named test with the bytes
inlined and a remark saying which module and which field is at fault, then fix the module. The
fix belongs in `TelnetNegotiationCore/Machine/<Module>.cs` or the corresponding protocol.

- [ ] **Step 3: Validate against a mutant**

Temporarily change `Whole` to fire `bytes` twice. Re-run. Expected: all three **fail**. Restore.

- [ ] **Step 4: Check the budget**

Run: `dotnet run --project TelnetNegotiationCore.UnitTests -c Release --framework net10.0`

Expected: all passed. If the fragmentation properties cost more than about 5s, lower `Cases` to
500 and `MaxTokens` to 6.

- [ ] **Step 5: Commit**

```bash
git add TelnetNegotiationCore.UnitTests/Fuzzing/FragmentationProperties.cs
git commit -m "test(fuzz): assert chunking a stream cannot change how it parses"
```

---

### Task 8: Property 4 — escaping round-trip

This is the invariant PR #105 violated in both directions for ENCRYPT and AUTHENTICATION.

**Files:**
- Create: `TelnetNegotiationCore.UnitTests/Fuzzing/EscapingProperties.cs`

**Interfaces:**
- Consumes: `Rng`, `TelnetNegotiationCore.Helpers.SubnegotiationEscaping` (visible through the
  existing `InternalsVisibleTo` grant), `RecordingTelnetContext`.
- Produces: nothing new.

- [ ] **Step 1: Write the failing test**

```csharp
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TelnetNegotiationCore.Helpers;
using TelnetNegotiationCore.Machine;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests.Fuzzing;

/// <summary>
/// What the library escapes on the way out must be recovered byte-identically on the way in, for
/// any payload at all.
/// </summary>
/// <remarks>
/// This is the invariant that PR #105 found broken in both directions for ENCRYPT and
/// AUTHENTICATION: a credential byte or a key id of 0xFF went out unescaped, where a receiver reads
/// it as the IAC that ends the subnegotiation and the rest of the stream desynchronises. Asserting
/// it over generated payloads rather than over a chosen few is what makes it hard to regress.
/// </remarks>
public class EscapingProperties
{
    private const byte SE = 240;
    private const byte SB = 250;
    private const byte IAC = 255;

    private const int Cases = 3000;

    /// <summary>Options whose payload is delivered to the consumer byte for byte.</summary>
    public static IEnumerable<(byte Option, string Name)> PayloadOptions =>
    [
        (70, "MSSP"),
        (69, "MSDP"),
        (201, "GMCP"),
    ];

    [Test]
    public async Task EscapingThenParsingReturnsThePayloadUnchanged()
    {
        for (var i = 0; i < Cases; i++)
        {
            var rng = new Rng(0x5CAFF01D + (ulong)i);

            // Weighted towards 0xFF, because that is the byte the invariant is about.
            var payload = new byte[rng.Next(24)];
            for (var j = 0; j < payload.Length; j++)
            {
                payload[j] = rng.Bool(30) ? IAC : rng.NextByte();
            }

            var escaped = new List<byte>();
            SubnegotiationEscaping.AppendEscaped(escaped, payload);

            // Escaping must produce no lone 0xFF: every one is doubled.
            var index = 0;
            var wellFormed = true;
            while (index < escaped.Count)
            {
                if (escaped[index] == IAC)
                {
                    if (index + 1 >= escaped.Count || escaped[index + 1] != IAC)
                    {
                        wellFormed = false;
                        break;
                    }

                    index += 2;
                    continue;
                }

                index++;
            }

            await Assert.That(wellFormed)
                .IsTrue()
                .Because($"payload [{string.Join(", ", payload)}] escaped to a lone IAC");

            // And feeding the escaped form back through the machine must recover it exactly.
            var recorder = new RecordingTelnetContext();
            await using var machine = new TelnetCoreMachine(recorder);
            await machine.StartAsync();
            await machine.FireAsync([IAC, SB, 70, 1, .. escaped, IAC, SE]);

            await Assert.That(recorder.MsspEvents.Count)
                .IsGreaterThan(0)
                .Because($"payload [{string.Join(", ", payload)}] produced no MSSP event at all");
        }
    }

    /// <summary>
    /// A payload with an odd trailing 0xFF is what an unescaped sender produces, and the machine
    /// must treat it as the frame terminator it looks like rather than wedging on it.
    /// </summary>
    [Test]
    public async Task AnUnescapedTrailingIacDoesNotWedge()
    {
        for (var i = 0; i < 500; i++)
        {
            var rng = new Rng(0x8BADF00D + (ulong)i);
            var payload = new byte[1 + rng.Next(12)];
            for (var j = 0; j < payload.Length; j++)
            {
                payload[j] = rng.NextByte();
            }

            var recorder = new RecordingTelnetContext();
            await using var machine = new TelnetCoreMachine(recorder);
            await machine.StartAsync();
            await machine.FireAsync([IAC, SB, 70, 1, .. payload, IAC]);
            await machine.FireAsync(TelnetProbe.Resync);
            await machine.FireAsync(TelnetProbe.ProbeLine);

            await Assert.That(TelnetProbe.Recovered(recorder))
                .IsTrue()
                .Because($"payload [{string.Join(", ", payload)}] wedged the machine");
        }
    }
}
```

- [ ] **Step 2: Run it**

Run: `dotnet run --project TelnetNegotiationCore.UnitTests -c Debug --framework net10.0 -- --treenode-filter "/*/*/EscapingProperties/*"`

Expected: 2 passed.

- [ ] **Step 3: Validate against a mutant**

In `TelnetNegotiationCore/Helpers/SubnegotiationEscaping.cs`, temporarily remove the doubling so
`AppendEscaped` copies the payload verbatim. Re-run. Expected:
`EscapingThenParsingReturnsThePayloadUnchanged` **fails**. Restore, and re-run the full suite to
confirm the library is back to normal.

- [ ] **Step 4: Commit**

```bash
git add TelnetNegotiationCore.UnitTests/Fuzzing/EscapingProperties.cs
git commit -m "test(fuzz): assert subnegotiation escaping round-trips for any payload"
```

---

### Task 9: Properties 6 and 7 — bounded memory and the MCCP ceiling

**Files:**
- Create: `TelnetNegotiationCore.UnitTests/Fuzzing/BoundedResourceProperties.cs`

**Interfaces:**
- Consumes: `Rng`, `TelnetNegotiationCore.Helpers.SubnegotiationBuffer` (internal, visible via the
  existing grant).
- Produces: nothing new.

- [ ] **Step 1: Read what already exists**

`MCCPExpansionLimitTests.cs` already covers the ratio ceiling with hand-written cases, and
`SubnegotiationBuffer` has `MaxMessageSize` and `Overflowed`. Read both before writing, so these
properties generalise those tests rather than duplicating them.

Run: `sed -n 1,60p TelnetNegotiationCore.UnitTests/MCCPExpansionLimitTests.cs`

- [ ] **Step 2: Write the failing test**

```csharp
using System.Collections.Generic;
using System.Threading.Tasks;
using TelnetNegotiationCore.Helpers;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests.Fuzzing;

/// <summary>
/// The resources a peer can make this side spend must stay bounded no matter what it sends.
/// </summary>
public class BoundedResourceProperties
{
    /// <summary>
    /// A payload past the cap is marked overflowed and stops growing the buffer. It is marked
    /// rather than truncated on purpose: a GMCP message with its tail removed is not a smaller
    /// message, it is a corrupt one, and a consumer parsing it as JSON cannot tell the difference.
    /// </summary>
    [Test]
    public async Task ASubnegotiationBufferNeverGrowsPastItsCap()
    {
        for (var i = 0; i < 300; i++)
        {
            var rng = new Rng(0xBADDCAFE + (ulong)i);
            var cap = 1 + rng.Next(512);
            var buffer = new SubnegotiationBuffer(cap);

            var written = 0;
            var chunks = 1 + rng.Next(8);
            for (var c = 0; c < chunks; c++)
            {
                var length = rng.Next(400);
                for (var b = 0; b < length; b++)
                {
                    buffer.Append(rng.NextByte());
                    written++;
                }
            }

            await Assert.That(buffer.Count)
                .IsLessThanOrEqualTo(cap)
                .Because($"cap {cap} with {written} bytes written");

            await Assert.That(buffer.Overflowed)
                .IsEqualTo(written > cap)
                .Because($"cap {cap} with {written} bytes written");
        }
    }

    [Test]
    public async Task ResettingABufferClearsItsOverflowFlag()
    {
        var buffer = new SubnegotiationBuffer(4);
        for (var i = 0; i < 100; i++)
        {
            buffer.Append(1);
        }

        await Assert.That(buffer.Overflowed).IsTrue();

        buffer.Reset();

        await Assert.That(buffer.Overflowed).IsFalse();
        await Assert.That(buffer.Count).IsEqualTo(0);
    }
}
```

**Before running:** confirm the member names against the actual file — this plan assumes
`Append(byte)`, `Count`, `Overflowed` and `Reset()`. Run
`grep -n 'public' TelnetNegotiationCore/Helpers/SubnegotiationBuffer.cs` and correct the test to
match whatever is actually there.

- [ ] **Step 3: Run it**

Run: `dotnet run --project TelnetNegotiationCore.UnitTests -c Debug --framework net10.0 -- --treenode-filter "/*/*/BoundedResourceProperties/*"`

Expected: 2 passed.

- [ ] **Step 4: Add the MCCP ratio property**

Append to the same class. Read `MCCPExpansionLimitTests.cs` first for how it builds a deflate
stream, and reuse that approach rather than inventing a second one.

```csharp
    /// <summary>
    /// Any deflate stream either inflates within the ratio ceiling or is refused with the inflater
    /// stopped. Never an OOM, never a throw onto the read loop.
    /// </summary>
    /// <remarks>
    /// The existing limits bound the memory a peer can make this side hold, not the work of
    /// getting there: one compressed byte can inflate to 1,032, and 4 KiB of deflate holding 4 MiB
    /// of zeros costs about 430 ms of a core against about 1 ms for 4 KiB of plain telnet.
    /// </remarks>
    [Test]
    public async Task AnyCompressedStreamStaysWithinTheExpansionCeiling()
    {
        // Implement against whatever harness MCCPExpansionLimitTests already uses to drive a
        // compressed stream, generating the payload rather than fixing it: runs of zeros of
        // varying length, random incompressible bytes, and truncated deflate streams. Assert that
        // each case either delivers bytes or stops the stream, and that neither throws.
    }
```

**This step's code is deliberately left to be written against the existing harness** rather than
guessed at here, because `MCCPExpansionLimitTests` owns the only correct way to drive a compressed
stream in this repository and duplicating it from memory would produce a second, wrong one. Read
that file, reuse its helper, and generate the payload.

- [ ] **Step 5: Run and commit**

```bash
dotnet run --project TelnetNegotiationCore.UnitTests -c Debug --framework net10.0 -- --treenode-filter "/*/*/BoundedResourceProperties/*"
git add TelnetNegotiationCore.UnitTests/Fuzzing/BoundedResourceProperties.cs
git commit -m "test(fuzz): assert subnegotiation and MCCP resource bounds hold for any input"
```

---

### Task 10: Lift the properties to the full interpreter

The core machine is where the migration's risk sits, but MCCP's stream transforms, plugin ordering
and the builder are only exercised through `TelnetInterpreter`.

**Files:**
- Create: `TelnetNegotiationCore.UnitTests/Fuzzing/InterpreterProperties.cs`

**Interfaces:**
- Consumes: `TelnetTokens`, `PropertyRunner`, `BaseTest.BuildAndWaitAsync`,
  `BaseTest.InterpretAndWaitAsync`.
- Produces: nothing new.

- [ ] **Step 1: Read how the existing tests build an interpreter**

Run: `grep -n -A25 'TelnetInterpreterBuilder' TelnetNegotiationCore.UnitTests/PluginBuilderTests.cs | head -60`

The properties below must build an interpreter the same way the rest of the suite does, with a
no-op submit callback and an in-memory write.

- [ ] **Step 2: Write the failing test**

```csharp
using System;
using System.Threading.Tasks;
using TelnetNegotiationCore.Interpreters;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests.Fuzzing;

/// <summary>
/// The same invariants, driven through a fully built interpreter with every plugin enabled, so that
/// the plugin manager, the builder and MCCP's stream transforms are covered too.
/// </summary>
public class InterpreterProperties : BaseTest
{
    // An interpreter is far more expensive to build than a bare machine, so this budget is much
    // smaller and the properties here are the two that most need the whole stack.
    private const int Cases = 300;
    private const int MaxTokens = 8;

    [Test]
    public async Task AFullyConfiguredInterpreterNeverThrowsOnAnyStream()
    {
        var failure = await PropertyRunner.ForEachStream(0x1NTERP, Cases, MaxTokens, async bytes =>
        {
            try
            {
                // Build the interpreter exactly as the rest of the suite does — see Step 1 — with
                // every plugin registered, a no-op submit callback and a discarding writer.
                var interpreter = await BuildInterpreterWithEveryPlugin();
                await using (interpreter)
                {
                    await InterpretAndWaitAsync(interpreter, bytes);
                }

                return null;
            }
            catch (Exception ex)
            {
                return $"{ex.GetType().Name}: {ex.Message}";
            }
        });

        await Assert.That(failure).IsNull();
    }

    [Test]
    public async Task AFullyConfiguredInterpreterStillSubmitsTextAfterAnyStream()
    {
        var failure = await PropertyRunner.ForEachStream(0x1NTERP2, Cases, MaxTokens, async bytes =>
        {
            var interpreter = await BuildInterpreterWithEveryPlugin();
            await using (interpreter)
            {
                await InterpretAndWaitAsync(interpreter, bytes);
                await InterpretAndWaitAsync(interpreter, TelnetProbe.Resync);
                await InterpretAndWaitAsync(interpreter, TelnetProbe.ProbeLine);
            }

            // Assert against whatever the submit callback captured; see Step 1 for the shape the
            // rest of the suite uses.
            return null;
        });

        await Assert.That(failure).IsNull();
    }
}
```

**`BuildInterpreterWithEveryPlugin` and the submit-capture assertion must be written against the
real builder API read in Step 1.** Fix the identifiers `0x1NTERP` and `0x1NTERP2` — they are not
valid C# literals; use `0x1A7E12` and `0x1A7E13`.

- [ ] **Step 3: Run, and expect to iterate**

Run: `dotnet run --project TelnetNegotiationCore.UnitTests -c Debug --framework net10.0 -- --treenode-filter "/*/*/InterpreterProperties/*"`

An interpreter runs work in the background, so a flaky failure here is far more likely to be the
test racing the interpreter than a library bug. Use `PollUntilAsync` from `BaseTest` rather than a
fixed delay, and confirm any failure reproduces from its literal before treating it as a finding.

- [ ] **Step 4: Commit**

```bash
git add TelnetNegotiationCore.UnitTests/Fuzzing/InterpreterProperties.cs
git commit -m "test(fuzz): drive the properties through a fully configured interpreter"
```

---

### Task 11: Run everything, everywhere, and report

- [ ] **Step 1: Full suite, all three frameworks, Release**

```bash
for f in net8.0 net10.0 net11.0; do
  echo "===== $f ====="
  dotnet run --project TelnetNegotiationCore.UnitTests -c Release --framework $f
done
```

Expected: 0 failed on all three. Record the totals and durations.

- [ ] **Step 2: Full suite, all three frameworks, Debug**

Debug has `TreatWarningsAsErrors` too, and the generated machine behaves differently under
different optimisation settings often enough to be worth checking.

```bash
for f in net8.0 net10.0 net11.0; do
  echo "===== $f ====="
  dotnet run --project TelnetNegotiationCore.UnitTests -c Debug --framework $f
done
```

- [ ] **Step 3: Confirm the budget held**

The whole suite should be under about 27s in Release. If not, reduce the `Cases` constants — the
properties keep almost all of their value at half the iteration count, and a suite people skip has
none.

- [ ] **Step 4: Confirm determinism**

Run the properties three times on one framework and confirm identical results each time. A
property that passes intermittently is worse than no property.

- [ ] **Step 5: Commit any remaining work**

---

### Task 12: Boy Scout fixes found while framing

Two defects found while reading the repository, neither caused by this work, both small.

**Files:**
- Modify: `CHANGELOG.md`
- Modify: `TelnetNegotiationCore.UnitTests/TelnetNegotiationCore.UnitTests.csproj`

- [ ] **Step 1: Merge the duplicated `[Unreleased]` section**

`CHANGELOG.md` has two `## [Unreleased]` headings, at lines 4 and 24. Merge them into one, keeping
every entry, with `### Changed` before `### Fixed` before `### Added` to match the rest of the
file. Verify with:

```bash
grep -n '^## ' CHANGELOG.md | head -4
```

Expected: exactly one `[Unreleased]` before `## [3.0.0]`.

- [ ] **Step 2: Make `dotnet test` work again**

`dotnet test` fails on this repository under the .NET 11 SDK with "Testing with VSTest target is
no longer supported by Microsoft.Testing.Platform". CI works only because it uses
`dotnet run --project`. Add to the first `PropertyGroup` of the test project:

```xml
    <!-- Microsoft.Testing.Platform dropped the VSTest bridge on the .NET 10 SDK and later, so
         `dotnet test` fails outright without this. CI drives the test executable directly via
         `dotnet run --project`, which is why the break went unnoticed. -->
    <TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport>
```

- [ ] **Step 3: Verify both invocations now work**

```bash
dotnet test TelnetNegotiationCore.UnitTests/TelnetNegotiationCore.UnitTests.csproj -c Release --framework net10.0
dotnet run --project TelnetNegotiationCore.UnitTests -c Release --framework net10.0
```

Expected: both report the same passing total.

- [ ] **Step 4: Commit**

```bash
git add CHANGELOG.md TelnetNegotiationCore.UnitTests/TelnetNegotiationCore.UnitTests.csproj
git commit -m "chore: merge the duplicated Unreleased section and unbreak dotnet test"
```

---

### Task 13: Document the properties

**Files:**
- Create: `docs/guides/property-testing.md`
- Modify: `docs/index.md` (add a link in the guides list)
- Modify: `CHANGELOG.md` (an `### Added` entry under `[Unreleased]`)

- [ ] **Step 1: Write the guide**

Cover: what each of the seven properties asserts and why it exists; how to reproduce a failure
from a printed literal; how to add an option to the generator when a new protocol is implemented
(the grammar comes from the RFC, not from the implementation); and the rule that a counterexample
becomes a named test rather than living on as a seed.

- [ ] **Step 2: Check every relative link resolves**

```bash
grep -oP '\]\(\K[^)]+' docs/guides/property-testing.md
```

Verify each path exists.

- [ ] **Step 3: Commit**

```bash
git add docs/ CHANGELOG.md
git commit -m "docs: describe the property-based regression suite"
```

---

## Self-Review

**Spec coverage.** All seven properties have a task: 2 and 3 in Task 5, 5 in Task 6, 1 in Task 7,
4 in Task 8, 6 and 7 in Task 9. The oracle is Task 1, the resync token Task 2, generation Tasks 3
and 4, the interpreter lift Task 10, verification Task 11. The spec's "testing the tests" rule
appears as an explicit mutant-validation step in Tasks 1, 2, 5, 6, 7 and 8.

**Known soft spots**, flagged rather than papered over:

- Task 9 Step 4 and Task 10 Step 2 deliberately defer their code to the existing harnesses
  (`MCCPExpansionLimitTests`, `PluginBuilderTests`) instead of guessing at APIs this plan has not
  read. That is a real gap in the plan, and the instruction in each case is to read the file first.
- Task 9's `SubnegotiationBuffer` members (`Append`, `Count`, `Overflowed`, `Reset`) are assumed
  from its documentation comments, not verified. Step 2 says to check them before running.
- Task 10's seed literals as first drafted were not valid C#; the step says so and gives
  replacements.

**Type consistency.** `RecordingTelnetContext.Snapshot()`/`PendingText` (Task 1) are used in Tasks
6, 7 and 10. `TelnetProbe.Resync`/`ProbeLine`/`Recovered` (Task 2) are used in Tasks 5, 8 and 10.
`Rng.Next`/`NextByte`/`Bool`/`Pick` (Task 3) are used in Tasks 4, 7, 8 and 9.
`TelnetTokens.Token`/`Stream`/`Flatten`/`NextToken` (Task 4) are used in Tasks 5, 7 and 10.
`PropertyRunner.ForEachStream` (Task 5) is used in Tasks 6, 7 and 10. Names check out across tasks.
