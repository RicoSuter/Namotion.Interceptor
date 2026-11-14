using Namotion.Interceptor.OpcUa.Client;
using Opc.Ua.Configuration;

namespace Namotion.Interceptor.ConnectorTester.Connectors.OpcUa;

/// <summary>OPC UA client configuration with tester-only overrides of the SDK application configuration.</summary>
public class ConnectorTesterOpcUaClientConfiguration : OpcUaClientConfiguration
{
    /// <summary>Lifetime the client requests for secure channel security tokens, or null for the library default.</summary>
    public TimeSpan? SecurityTokenLifetime { get; set; }

    public override async Task<ApplicationInstance> CreateApplicationInstanceAsync()
    {
        var application = await base.CreateApplicationInstanceAsync().ConfigureAwait(false);
        if (SecurityTokenLifetime is { } securityTokenLifetime)
        {
            application.ApplicationConfiguration.TransportQuotas.SecurityTokenLifetime = (int)securityTokenLifetime.TotalMilliseconds;
        }

        return application;
    }
}
