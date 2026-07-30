# Change Log
All notable changes to this project will be documented in this file.

## [2.6.5] - 2026-07-30

### Fixed
- **Silent truncation of large subnegotiation payloads (GMCP, MSDP, CHARSET TTABLE)** — GMCP messages were capped at 8192 bytes across the whole payload (package name, separator and data together), and everything past that was dropped. There was no error, no exception and no signal the consumer could observe: the callback fired with a shortened string, which for any JSON payload means invalid JSON that cannot be told apart from a malformed server. Measured with a client-mode interpreter: `"Ab " + 8189` bytes arrived whole, `"Ab " + 8190` arrived one byte short, and a 15-character package name left 8176 bytes for the data. The `Warning` that was logged did not help — it fired at exactly 8192 bytes whether or not anything had been lost.
  - MSDP shared the ceiling (its own 8192-byte constant, with a warning logged *per dropped byte*), as did the CHARSET TTABLE buffer (a fixed 8192-byte array); a table for a 16-bit character set legitimately exceeds it. MXP, MSSP, NAWS, TERMINAL-TYPE, ENVIRON/NEW-ENVIRON and the other subnegotiations do not accumulate an unbounded payload and were unaffected.
  - All three now share one bounded accumulator (`SubnegotiationBuffer`), which records that it overflowed instead of quietly discarding bytes. Neither the GMCP nor the MSDP specification defines a maximum message size, so the ceiling is a library policy: **1 MiB per message by default**, configurable via `GMCPProtocol.MaxMessageSize` / `MSDPProtocol.MaxMessageSize` / `CharsetProtocol.MaxTTableSize` and the matching `.WithMaxMessageSize(bytes)` / `.WithMaxTTableSize(bytes)` fluent methods.
  - At the ceiling the message is **dropped rather than truncated**, and said out loud: an `Error` level log naming the package and byte count, plus the new `.OnGMCPMessageTooLarge(...)` / `.OnMSDPMessageTooLarge(...)` callbacks for consumers that want to observe it. An oversized TTABLE is answered with `TTABLE-REJECTED` (RFC 2066). The connection is unaffected and the next message is processed normally.
  - Small messages, the overwhelmingly common case, allocate *less*: the per-message `Channel<byte>` (allocated fresh for every GMCP and MSDP subnegotiation) and the copy out of it are gone, replaced by a reused `List<byte>` that releases its backing array only after an unusually large message.
  - Removed the unreachable duplicates of the GMCP and MSDP receive paths in `Interpreters/TelnetGMCPInterpreter.cs` and `Interpreters/TelnetMSDPInterpreter.cs`, which carried their own copies of the 8192-byte limit but were never wired into the state machine. `SendGMCPCommand` is unchanged.

## [2.6.3] - 2026-07-30

### Fixed
- **MSSP multi-value variables, booleans and unknown variables** — the MSSP reader could not represent what the protocol sends, and destroyed the data before any consumer saw it.
  - Every `MSSP_VAL` under one `MSSP_VAR` was accumulated into a single byte buffer with no separator, so `PORT "80" "23" "4201"` arrived as the integer `80234201`. The specification says "It's also possible to attach several values to a single variable by using MSSP_VAL more than once, with the default value reported last."
  - The same variable repeated (`MSSP_VAR "PORT" MSSP_VAL "80" MSSP_VAR "PORT" MSSP_VAL "23"`) kept only whichever value happened to be bound last, and paired names to values by index, so one malformed field misaligned the whole report.
  - `REFERRAL` — a list by definition, and the variable a crawler runs on — came out `null`: the run-together string could not bind to its list-typed property and was dropped.
  - Booleans (`ANSI`, `UTF-8`, `PAY TO PLAY`, …) were parsed with `bool.TryParse`, which rejects MSSP's `1`/`0`, so every one of them was dropped.
  - Variables with no property on `MSSPConfig` were discarded rather than collected, and `MSSPConfig.Extended` was never populated on receive.
  - Variable names were matched case-sensitively after `ToUpper()` (culture-sensitive) and without the specification's recommended underscore-for-space substitution, so a server sending `CRAWL_DELAY` or `MINIMUM_AGE` was ignored.
  - A payload whose last field was a variable name with no value (`… MSSP_VAR "FOO" IAC SE`) hit an unhandled trigger and left the MSSP state machine wedged for the rest of the connection.
- **Bodyless GMCP messages were dropped** — a message with no data section, such as `Core.Ping`, never reached `OnGMCPMessage`. The parser required a space separator unconditionally, so the one form the specification prescribes for a command without data was the one form that was discarded: *"The `<data>` field is optional and should be separated from the package field with a space. When sending a command without a data section the space should be omitted."* The same message *with* a trailing space was delivered fine. Such messages are now delivered with `Package = "Core.Ping"` and `Info = ""` — an empty string rather than a fabricated `"{}"`, so the callback reports what was actually on the wire (Mudlet substitutes `{}` because its Lua API has no way to express "no data section"; this one does).
- **A GMCP message whose data ran into the package name was dropped without naming it** — `Char.Vitals{"hp":1}` is malformed by that same sentence, and was discarded with a log line that did not say which package it had been. A server spelling it that way was indistinguishable from a server with no GMCP at all. A package name cannot contain `{`, so the message is now split at the first character that cannot belong to a package name, delivered, and logged as a `Warning` naming the package — tolerated, but never in silence. A payload with no package name at all (`{"hp":1}` on its own) is still discarded, now with a warning quoting what was thrown away; previously it was delivered with an empty package name.
- **MSDP over GMCP delivered the package name instead of the message** — a MoG message (`IAC SB GMCP 'MSDP {"LIST" : "COMMANDS"}' IAC SE`) had its data section discarded, and the *package name bytes* were handed to the MSDP byte-encoding scanner instead. Every MoG message, whatever it carried, reached `OnMSDPMessage` as the four characters `"MSDP"`; the body never arrived at all. The GMCP specification carries MSDP as JSON, not as MSDP's own encoding — *"The data field must use the JSON data syntax with keywords being case sensitive using UTF-8 encoding"* — and that is the same shape `OnMSDPMessage` already receives from a native `IAC SB MSDP` subnegotiation (MSDP tables are JSON objects, MSDP arrays are JSON arrays), so the data section is now forwarded verbatim. No callback signature changes.
  - The package name is matched case sensitively, as the specification requires: *"When using MoG (MSDP over GMCP) the package name is considered case sensitive and MSDP must be fully capitalized."* A `msdp` package is an ordinary GMCP package.
  - A data section that is not valid JSON is discarded with an `Error` log naming the payload, rather than forwarded to a callback whose contract is JSON. Parsing happens inside the protocol, so a malformed payload cannot throw onto the read loop, and the connection continues with the next message.
  - A MoG message received with no `MSDPProtocol` plugin registered is now delivered to `OnGMCPMessage` with `Package = "MSDP"`, instead of being dropped silently.

### Added
- **`MSSPConfig.Variables`** — an ordered, canonicalized variable name → value **list** map holding everything a peer reported, which the strongly typed properties are projected from. Scalar properties take the last value (the specification's default); list-typed properties take all of them. `Default`, `Flag` and `Integer` read the default value in the shape a caller usually wants, and `OfficialNames` / `UnofficialNames` partition a report against the specification's tables.
- **`MSSPVariables`** — `Canonicalize` (upper case, underscores folded to spaces, whitespace runs collapsed), `IsOfficial`, `IsKnown`, and the official name list.
- **`MSSPConfig.Charset` and `MSSPConfig.Discord`** — official Generic variables that were missing from the model. `CHARSET` is array-capable.
- **`MSSPConfigAccessor.TrySetValues`** — binds every value of a variable at once. `TrySetProperty` is unchanged in signature and now defers to it.
- When a received `MSSPConfig` is sent back out, `Variables` is written verbatim — arrays and unknown variables included — and the typed properties and `Extended` supply only names it does not mention. A configuration built by hand has an empty map and sends exactly what it always did.

### Changed
- `MSSPConfig.MSP` is now marked `[Official(false)]`. `MSP` is not in the specification's official variable tables.

## [2.6.0] - 2026-07-29

### Added
- **Keep-alive** — opt-in idle keep-alive that stops NAT tables, load balancers and idle timers from dropping a quiet connection:
  - `.WithKeepAlive(TimeSpan? interval = null, Func<TelnetInterpreter, CancellationToken, ValueTask>? sendAsync = null)` on `TelnetInterpreterBuilder` (and on a plugin configuration chain). Off unless called, so upgrading changes nothing for existing consumers.
  - The interval is an **idle** window, not a fixed heartbeat: it is restarted by every outbound write through `WriteToNetworkAsync`, so a connection that is already sending data sends nothing extra. Defaults to 30 seconds (`TelnetInterpreter.DefaultKeepAliveInterval`).
  - The interval is bounded: at least 1 second (`TelnetInterpreter.MinimumKeepAliveInterval`) and at most 24 hours (`TelnetInterpreter.MaximumKeepAliveInterval`), inclusive. Out-of-range values throw `ArgumentOutOfRangeException` naming both bounds rather than being clamped, and the check applies both to `.WithKeepAlive(...)` and to `BuildAsync()` for anyone assigning the `KeepAliveInterval` init property directly. Under a second a keep-alive is a flood — no idle timeout it defends against is that short — and over a day it cannot keep anything alive, besides approaching the interval at which `Task.Delay` itself throws (over `int.MaxValue` ms on .NET Framework, reachable through the `netstandard2.0` target).
  - The payload is overridable via `sendAsync`; the default is `IAC NOP` (RFC 854), also available on its own as `TelnetInterpreter.SendKeepAliveAsync()`.
  - Works in both client and server mode. A failing send (typically a vanished peer) is logged as a warning and stops the keep-alive for that connection; it is never rethrown onto the host application and does not disturb the byte-processing loop.
  - **Note:** a NOP keep-alive is not peer-liveness detection. A successful send only proves the local write succeeded, not that anyone is still listening — the peer is not required to answer. Verifying the peer is alive needs a round trip it must answer, such as TIMING-MARK (RFC 860, option 6), which this library does not implement.

## [2.5.3]

### Fixed
- **NAWS negotiation direction (RFC 1073)** — the client offered `DO NAWS` (asking the server for a window it has no concept of) and then sent an unsolicited `SB NAWS` because `SendNAWS`'s guard was a no-op. A strict server answered the stray `DO NAWS` with `WONT`, and the unsolicited subnegotiation then desynced its parser and swallowed the following line, making typed logins intermittently bounce back to the login screen. The client now offers `WILL NAWS` and only reports its size once the server enables it with `DO NAWS`.

## [2.5.2]

### Fixed
- **CHARSET initiation (RFC 2066)** — the CHARSET plugin registered its proactive `WILL CHARSET` offer in both client and server mode. Two peers both offering `WILL CHARSET` collided and never resolved, leaving a stuck CHARSET state that discarded the client's first line. The initial offer is now gated to server mode, matching how GMCP and MCCP gate theirs.

## [2.5.1]

### Fixed
- **Missing `FSharp.Core` dependency** — the package bundles the F# assembly `TelnetNegotiationCore.Functional.dll` (MSDP support) but its `FSharp.Core` dependency was not declared in the nuspec (the F# project is referenced with `PrivateAssets="all"`). Consumers therefore never restored `FSharp.Core`, and the first MSDP negotiation threw `Could not load file or assembly 'FSharp.Core'` — a hard native `SIGSEGV` under Mono / .NET-Android. `FSharp.Core` is now declared as an explicit package dependency (pinned to 10.1.203 to match the bundled assembly).

## [2.5.0]

### Added
- **MXP Protocol** — `MXPProtocol` plugin implementing MUD eXtension Protocol (telnet option 91) handshake negotiation. Server sends `IAC WILL MXP`, client responds `IAC DO MXP`. Included in `AddDefaultMUDProtocols()`.
- `.OnMXPEnabled(() => ...)` fluent callback for MXP negotiation success
- `MXPProtocol.IsMXPActive` property to check negotiation state

## [2.4.2]

### Added
- **Automatic pipe/stream wiring** — `BuildAndStartAsync` overloads on `TelnetInterpreterBuilder` and `PluginConfigurationContext<T>` eliminate boilerplate read loops and `OnNegotiation` wiring:
  - `BuildAndStartAsync(IDuplexPipe, CancellationToken)` — wires negotiation output to `pipe.Output`, starts read loop; returns `(TelnetInterpreter, Task readTask)`
  - `BuildAndStartAsync(Stream, CancellationToken)` — wraps any `Stream` (e.g. `NetworkStream`, `SslStream`) using `PipeReader.Create` / `PipeWriter.Create`
  - `BuildAndStartAsync(TcpClient, CancellationToken)` — convenience overload; delegates to the `Stream` overload via `client.GetStream()`
  - `UsePipe(IDuplexPipe)` — wires `OnNegotiation` to `pipe.Output`; leaves read loop to the caller
  - `UseStream(Stream)` — wires `OnNegotiation` via `PipeWriter.Create(stream)`; leaves read loop to the caller
  - `ReadFromPipeAsync(TelnetInterpreter, PipeReader, CancellationToken)` — static helper that drives the standard read loop
- Added `System.IO.Pipelines` as an explicit package reference for `net8.0` and `netstandard2.0` targets
- **Dependency injection integration** — `AddTelnetServer()` and `AddTelnetClient()` extension methods on `IServiceCollection` register an `ITelnetInterpreterFactory` that creates pre-configured `TelnetInterpreterBuilder` instances with mode and logger resolved from DI:
  - `ITelnetInterpreterFactory` — factory interface; call `CreateBuilder()` to get a fresh builder per connection
  - `TelnetServiceCollectionExtensions.AddTelnetServer(Action<TelnetInterpreterBuilder>?)` — registers the factory in server mode
  - `TelnetServiceCollectionExtensions.AddTelnetClient(Action<TelnetInterpreterBuilder>?)` — registers the factory in client mode
  - Added `Microsoft.Extensions.DependencyInjection.Abstractions` as a package dependency
- **Modernized test projects** — TestServer updated from legacy `WebHost.CreateDefaultBuilder` + `Startup` class to `WebApplication.CreateBuilder()` minimal hosting; TestClient updated from `Host.CreateDefaultBuilder` to `Host.CreateApplicationBuilder()`; both now use `ITelnetInterpreterFactory` from DI

## [2.3.0] - 2026-02-13

### Performance Improvements
- **GMCP Protocol**: Optimized message parsing using `CollectionsMarshal.AsSpan()` for .NET 5+ to eliminate 2 `ToArray()` allocations per message
- **MSSP Protocol**: Optimized string encoding operations using `CollectionsMarshal.AsSpan()` to avoid intermediate array allocations
- **NAWS Protocol**: Replaced `BitConverter.GetBytes()` with `BinaryPrimitives.WriteInt16BigEndian()` and `stackalloc` for explicit big-endian encoding and improved performance on .NET 5+
- **TelnetStandardInterpreter**: Simplified `WriteToOutput()` method by removing unnecessary ArrayPool pattern
- **Documentation**: Added inline comments explaining design decisions for performance-critical code paths

## [2.0.0] - 2026-01-19

### Added
- **Plugin Architecture**: Class-based plugin system for protocol management
  - `ITelnetProtocolPlugin` interface for type-safe protocol contracts
  - `TelnetProtocolPluginBase` abstract base class
  - `ProtocolPluginManager` for dependency resolution and lifecycle management
  - `IProtocolContext` for plugin-to-plugin communication
  - `TelnetInterpreterBuilder` fluent API for construction
- **System.Threading.Channels Integration**: High-performance async byte processing
  - Bounded channel with 10,000 byte capacity for automatic backpressure
  - Non-blocking `InterpretAsync()` and `InterpretByteArrayAsync()` operations
  - Background processing with graceful shutdown via `IAsyncDisposable`
- **DOS Protection**: 8KB message size limits for GMCP and MSDP protocols
- **Protocol Plugins**: All 8 protocols migrated to plugin architecture
  - `GMCPProtocol` - Generic MUD Communication Protocol
  - `MSDPProtocol` - MUD Server Data Protocol
  - `NAWSProtocol` - Negotiate About Window Size (RFC 1073)
  - `TerminalTypeProtocol` - Terminal Type (RFC 1091 + MTTS)
  - `CharsetProtocol` - Character encoding (RFC 2066)
  - `MSSPProtocol` - MUD Server Status Protocol
  - `EORProtocol` - End of Record
  - `SuppressGoAheadProtocol` - Suppress Go-Ahead
- **Configurable Buffer**: `MaxBufferSize` property for line buffer (default 5MB)
- **Fluent Configuration Extensions**: Inline protocol configuration methods
  - `WithCharsetOrder()` - Configure charset order fluently on CharsetProtocol
  - `WithMSSPConfig()` - Configure MSSP settings fluently on MSSPProtocol
  - `AddDefaultMUDProtocols()` overload with optional parameters for inline configuration of all protocol callbacks and settings (onNAWS, onGMCPMessage, onMSSP, msspConfig, onMSDPMessage, onPrompt, charsetOrder)

### Changed
- Library architecture modernized with plugin-based design patterns
- Improved performance with non-blocking async operations
- Enhanced testability with independent protocol implementations

### Security
- Added comprehensive input validation and size limits
- Implemented automatic backpressure to prevent memory bloat
- DOS protection for protocol message buffers

**Note**: The legacy API remains fully supported for backward compatibility. All existing code will continue to work without modifications.

## [1.1.1] - 2024-12-30

### Fixed
- Fixed GMCP message receiving bug where the package name was incorrectly duplicated instead of the JSON message content being parsed.

### Added
- Added comprehensive test suite for GMCP functionality covering both client and server send/receive operations.

## [1.1.0] - 2025-03-16

### Changed
- Use nullable language feature for better null checks.
- Mark required items are required to assist with Validation.
- Adjusted F# code to use language features and more constants.
- Added caching for Byte -> Trigger mapping for faster performance.

## [1.0.9] - 2024-11-17

### Changed
- Fix a bug in 1.0.8 by downgrading the Stateless package.

## [1.0.8] - 2024-11-17

### Changed
- Use ValueTasks instead of Tasks for improved performance.

## [1.0.7] - 2024-03-19

### Changed
- Get NuGet to play nice about dependencies.

## [1.0.6] - 2024-01-09

### Changed
- Replaces 1.0.5, which was an invalid package update.
- Removed MoreLINQ dependency by making a copy of the function I needed and keep dependencies lower. License retained in the source file - to abide by Apache2 License. 

## [1.0.5] - 2024-01-09

### Changed
- Removed MoreLINQ dependency by making a copy of the function I needed and keep dependencies lower. License retained in the source file - to abide by Apache2 License. 

## [1.0.4] - 2024-01-09
  
### Changed
- Removed Serilog dependency in favor of Microsoft.Extensions.Logging.Abstractions, which allows one to inject the preferred logger.
 
## [1.0.3] - 2024-01-08
  
### Fixed
- Ensure that the Project Dependency on TelnetNegotiationCore.Functional is added as a DLL.
 
## [1.0.2] - 2024-01-07
  
### Added
- Add MSDP support.
- Added a helper function to convert strings to safe byte arrays.

### Changed
- Altered EOR functionality.

## [1.0.1] - 2024-01-03
  
### Added
- Add callback function for MSSP.
 
### Changed
- Target .NET 8.0.
- Change Methods to be properly async.
- Modernized TestClient example to use Pipes.
- Modernized TestServer example to use Pipes and Kestrel.
 
## [1.0.0] - 2024-01-03
  
Initial version.
 
### Added
- Initial support for RFC855 (TELOPT)
- Initial support for RFC858 (GOAHEAD)
- Initial support for RFC1091 (TTERM)
- Initial support for MTTS
- Initial support for RFC885 (EOR)
- Initial support for RFC1073 (NAWS)
- Initial support for RFC2066 (CHARSET)
- Initial support for MSSP
- Initial support for GMCP
