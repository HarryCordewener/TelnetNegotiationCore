# CHARSET and GMCP validation

Scope: SharpMUSH issue #1798 exposed a mismatch between advertised CHARSET choices and its renderer.
TNC 4.5.0 (`0ad3558f2b69d1455948ac0c1e8359b3c734c700`) and main
`84e6958834cc73c8d60e0259f28906429ecab76b` both accepted any Encoding.GetEncoding name without
checking an outstanding request. The accepted charset was committed before the consumer callback.
GMCP receive and string-send paths also used that terminal encoding.

Implementation follows the existing public AllowedEncodings configuration. Every outgoing request
captures one ordered set, writes that exact set, then retains its code pages for one response.
ACCEPTED resolves aliases by code page and consumes that snapshot; rejection, withdrawal, disable,
disposal, and a failed replacement request clear it. Invalid acceptance sends REJECTED without
changing either CharsetProtocol.CurrentEncoding or TelnetInterpreter.CurrentEncoding. No public
API expansion, private-field access, raw-wire shadow parser, or new package dependency is needed.
GMCP independently encodes and decodes UTF-8. Native MSDP retains its existing text-encoding policy.

## Reproduction and regression evidence

Before production edits, CharsetOfferValidationTests had seven expected failures out of ten cases:
unsupported UTF-16 (whole and one-byte fragments), no offer, rejected offer, withdrawn offer,
consumed offer replay, and replacement of the cached first request. The alias cases and unknown-name
rejection already passed. Both GmcpCharsetIndependenceTests cases failed (Latin-1 and ASCII).

The added tests now cover those failures, canonical alias identity, snapshots surviving later
configuration changes, empty/failed sends, text coalesced behind rejected ACCEPTED, and GMCP UTF-8
send/receive after Latin-1/ASCII negotiation. All 1,384 unit tests pass on each of .NET 8, .NET 10 and .NET 11; the whole library
also builds for netstandard2.0, net8.0, net10.0 and net11.0 without warnings.

Validation used .NET SDK 11.0.100-rc.2.26504.105. SDK-generated ILLink lockfile changes were reverted;
the repository SDK pin and committed dependency lockfiles are unchanged. Run the suite with:

```bash
dotnet build TelnetNegotiationCore.UnitTests -m:2 -nr:false -p:GeneratePackageOnBuild=false
dotnet run --no-build --project TelnetNegotiationCore.UnitTests -f net11.0
```

No package was published. Ordinary PR CI only builds/tests; the release workflow requires an explicit
dispatch or version tag. A consuming application needs a reviewed, released package before it can
rely on these guarantees with a production package pin.
