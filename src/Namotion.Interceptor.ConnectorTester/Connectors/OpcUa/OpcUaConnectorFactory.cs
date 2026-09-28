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

public sealed class OpcUaConnectorFactory : IConnectorFactory
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
                    // Tester-only override: shorten from the library default of 120s so chaos-killed
                    // sessions get reaped by the server's session-timeout monitor within a single
                    // 80s cycle, keeping the server's in-flight session working set bounded under
                    // sustained chaos. Real apps should keep 120s for resilience to transient
                    // network outages.
                    SessionTimeout = TimeSpan.FromSeconds(30),
                };
            });
    }
}
