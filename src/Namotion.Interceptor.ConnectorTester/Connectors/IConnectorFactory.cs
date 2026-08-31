using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor.ConnectorTester.Configuration;
using Namotion.Interceptor.ConnectorTester.Model;

namespace Namotion.Interceptor.ConnectorTester.Connectors;

/// <summary>
/// Per-connector factory. One implementation per ConnectorKind. Owns the connector's
/// default port and the server-side and client-side service registrations.
/// </summary>
public interface IConnectorFactory
{
    ConnectorKind Kind { get; }
    int DefaultPort { get; }

    void RegisterServer(IServiceCollection services, TestNode root, int port);
    void RegisterClient(IServiceCollection services, TestNode root, ParticipantConfiguration participantConfiguration, int port);
}
