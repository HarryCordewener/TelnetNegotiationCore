# Change Log
All notable changes to this project will be documented in this file.

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