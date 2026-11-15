using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.ConnectorTester.Configuration;
using Namotion.Interceptor.ConnectorTester.Model;
using Namotion.Interceptor.OpcUa;
using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.OpcUa.Server;
using Opc.Ua;
using Opc.Ua.Bindings;

namespace Namotion.Interceptor.ConnectorTester.Connectors.OpcUa;

public sealed class OpcUaConnectorFactory : IConnectorFactory
{
    private readonly OpcUaConfiguration _configuration;
    private readonly string _certificateStoreRootPath;

    /// <param name="configuration">The OPC UA settings of the run.</param>
    /// <param name="certificateStoreRootPath">Directory holding one certificate store per participant when secured.</param>
    public OpcUaConnectorFactory(OpcUaConfiguration configuration, string certificateStoreRootPath)
    {
        _configuration = configuration;
        _certificateStoreRootPath = certificateStoreRootPath;
    }

    public ConnectorKind Kind => ConnectorKind.OpcUa;
    public int DefaultPort => 4840;

    public void RegisterServer(IServiceCollection services, TestNode root, ParticipantConfiguration participantConfiguration, int port)
    {
        services.AddSingleton(root);
        services.AddOpcUaSubjectServer(
            _ => root,
            serviceProvider => CreateServerConfiguration(
                participantConfiguration, serviceProvider.GetRequiredService<ILoggerFactory>()));
    }

    public void RegisterClient(IServiceCollection services, TestNode root, ParticipantConfiguration participantConfiguration, int port)
    {
        services.AddKeyedOpcUaSubjectClientSource(
            participantConfiguration.Name,
            _ => root,
            serviceProvider => CreateClientConfiguration(
                participantConfiguration, port, serviceProvider.GetRequiredService<ILoggerFactory>()));
    }

    public OpcUaServerConfiguration CreateServerConfiguration(ParticipantConfiguration participantConfiguration, ILoggerFactory loggerFactory)
    {
        var configuration = new OpcUaServerConfiguration
        {
            RootName = "Root",
            ValueConverter = new OpcUaValueConverter(),
            TelemetryContext = CreateTelemetryContext(loggerFactory),
            AutoAcceptUntrustedCertificates = true,
            CleanCertificateStore = false,
        };

        if (_configuration.IsSecured)
        {
            configuration.CertificateStoreBasePath = GetCertificateStoreBasePath(participantConfiguration);
        }

        return configuration;
    }

    public OpcUaClientConfiguration CreateClientConfiguration(ParticipantConfiguration participantConfiguration, int port, ILoggerFactory loggerFactory)
    {
        var telemetryContext = CreateTelemetryContext(loggerFactory);
        var configuration = new ConnectorTesterOpcUaClientConfiguration
        {
            ServerUrl = $"opc.tcp://localhost:{port}",
            RootPath = ["Root"],
            TypeResolver = new OpcUaTypeResolver(loggerFactory.CreateLogger<OpcUaTypeResolver>()),
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

        if (_configuration.IsSecured)
        {
            configuration.MinimumSecurityMode = _configuration.MinimumSecurityMode;
            configuration.AutoAcceptUntrustedCertificates = true;
            configuration.CertificateStoreBasePath = GetCertificateStoreBasePath(participantConfiguration);
            // The library default of one hour outlives every channel under chaos, so no token would ever be renewed.
            configuration.SecurityTokenLifetime = TimeSpan.FromMilliseconds(TcpMessageLimits.MinSecurityTokenLifeTime);
            configuration.SessionFactory = new EndpointLoggingSessionFactory(
                telemetryContext, loggerFactory.CreateLogger<EndpointLoggingSessionFactory>());
        }

        return configuration;
    }

    private string GetCertificateStoreBasePath(ParticipantConfiguration participantConfiguration)
        => Path.Combine(_certificateStoreRootPath, participantConfiguration.Name);

    private static ITelemetryContext CreateTelemetryContext(ILoggerFactory loggerFactory)
        => DefaultTelemetry.Create(builder => builder.Services.AddSingleton(loggerFactory));
}
