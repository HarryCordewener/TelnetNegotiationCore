# Change Log
All notable changes to this project will be documented in this file.

## [Unreleased]

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