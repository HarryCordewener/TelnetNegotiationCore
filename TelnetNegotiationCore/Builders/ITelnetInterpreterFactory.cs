namespace TelnetNegotiationCore.Builders;

/// <summary>
/// Factory for creating pre-configured <see cref="TelnetInterpreterBuilder"/> instances.
/// Register with dependency injection using <c>AddTelnetServer</c> or <c>AddTelnetClient</c>
/// extension methods, then inject this factory where telnet interpreters are needed.
/// Each call to <see cref="CreateBuilder"/> returns a fresh builder with shared settings
/// (mode, logger, plugins) already applied. Add per-connection callbacks
/// (e.g. <see cref="TelnetInterpreterBuilder.OnSubmit"/>) before calling
/// <see cref="TelnetInterpreterBuilder.BuildAsync"/> or
/// <see cref="TelnetInterpreterBuilder.BuildAndStartAsync(System.IO.Pipelines.IDuplexPipe, System.Threading.CancellationToken)"/>.
/// </summary>
public interface ITelnetInterpreterFactory
{
    /// <summary>
    /// Creates a new <see cref="TelnetInterpreterBuilder"/> pre-configured with the
    /// registered mode, logger, and any shared plugin configuration.
    /// The caller should add per-connection callbacks (OnSubmit, etc.) before building.
    /// </summary>
    /// <returns>A new pre-configured <see cref="TelnetInterpreterBuilder"/></returns>
    TelnetInterpreterBuilder CreateBuilder();
}
