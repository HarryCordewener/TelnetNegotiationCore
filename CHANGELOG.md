# Change Log
All notable changes to this project will be documented in this file.

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