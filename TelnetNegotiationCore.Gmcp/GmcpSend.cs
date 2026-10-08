using System.Threading.Tasks;

namespace TelnetNegotiationCore.Gmcp;

/// <summary>
/// Sends one GMCP message: the package name, and its data as JSON text, empty for a message with
/// no data.
/// </summary>
/// <remarks>
/// With TelnetNegotiationCore this is <c>TelnetInterpreter.SendGMCPCommand</c>, which omits the
/// separating space when the data is empty, as GMCP asks.
/// </remarks>
/// <param name="package">The package name, such as <c>Core.Hello</c>.</param>
/// <param name="data">The JSON data, or an empty string for none.</param>
public delegate ValueTask GmcpSend(string package, string data);
