using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.ConnectorTester.Configuration;
using Namotion.Interceptor.ConnectorTester.Model;
using Namotion.Interceptor.OpcUa;
using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.OpcUa.Server;
using Opc.Ua;

namespace Namotion.Interceptor.ConnectorTester.Connectors.OpcUa;

public sealed class OpcUaConnectorBindings : IConnectorBindings
{
    public ConnectorKind Kind => ConnectorKind.OpcUa;
    public int DefaultPort => 4840;

    public void RegisterServer(IServiceCollection services, TestNode root, int port)
    {
        services.AddSingleton(root);
        services.AddOpcUaSubjectServer(
            _ => root,
            sp =>
            {
                var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
                var telemetryContext = DefaultTelemetry.Create(b =>
                    b.Services.AddSingleton(loggerFactory));

                return new OpcUaServerConfiguration
                {
                    RootName = "Root",
                    ValueConverter = new OpcUaValueConverter(),
                    TelemetryContext = telemetryContext,
                    AutoAcceptUntrustedCertificates = true,
                    CleanCertificateStore = false,
                };
            });
    }

    public void RegisterClient(IServiceCollection services, TestNode root, ParticipantConfiguration participantConfiguration, int port)
    {
        services.AddKeyedOpcUaSubjectClientSource(
            participantConfiguration.Name,
            _ => root,
            sp =>
            {
                var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
                var telemetryContext = DefaultTelemetry.Create(b =>
                    b.Services.AddSingleton(loggerFactory));

                return new OpcUaClientConfiguration
                {
                    ServerUrl = $"opc.tcp://localhost:{port}",
                    RootPath = ["Root"],
                    TypeResolver = new OpcUaTypeResolver(sp.GetRequiredService<ILogger<OpcUaTypeResolver>>()),
                    ValueConverter = new OpcUaValueConverter(),
                    SubjectFactory = new OpcUaSubjectFactory(DefaultSubjectFactory.Instance),
                    TelemetryContext = telemetryContext,
                    WriteRetryQueueSize = 10_000,
                };
            });
    }
}
