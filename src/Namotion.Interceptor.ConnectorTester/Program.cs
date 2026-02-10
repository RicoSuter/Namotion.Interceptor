using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MQTTnet.Protocol;
using Namotion.Interceptor;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Hosting;
using Namotion.Interceptor.Mqtt.Client;
using Namotion.Interceptor.Mqtt.Server;
using Namotion.Interceptor.OpcUa;
using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.OpcUa.Server;
using Opc.Ua;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.ConnectorTester.Configuration;
using Namotion.Interceptor.ConnectorTester.Engine;
using Namotion.Interceptor.ConnectorTester.Logging;
using Namotion.Interceptor.ConnectorTester.Model;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Validation;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

// Add cycle logger provider (created first so it can be shared with sharedLoggerFactory)
var cycleLoggerProvider = new CycleLoggerProvider();
builder.Services.AddSingleton(cycleLoggerProvider);
builder.Logging.AddProvider(cycleLoggerProvider);

// Shared logger factory for engines created before DI host build.
// Includes both console output and cycle logger so engine events appear in per-cycle log files.
using var sharedLoggerFactory = LoggerFactory.Create(b =>
{
    b.AddConsole();
    b.AddProvider(cycleLoggerProvider);
});

// Bind configuration
builder.Services.Configure<ConnectorTesterConfiguration>(
    builder.Configuration.GetSection("ConnectorTester"));

// Shared coordinator for all engines
var coordinator = new TestCycleCoordinator();
builder.Services.AddSingleton(coordinator);

// Will be populated during setup
var participants = new List<(string Name, TestNode Root)>();
var mutationEngines = new List<MutationEngine>();
var chaosEngines = new List<ChaosEngine>();

// Read configuration
var configuration = builder.Configuration
    .GetSection("ConnectorTester")
    .Get<ConnectorTesterConfiguration>() ?? new ConnectorTesterConfiguration();

// Determine server port based on connector type
int serverPort = configuration.Connector.ToLowerInvariant() switch
{
    "opcua" => 4840,
    "mqtt" => 1883,
    "websocket" => 8080,
    _ => 4840
};

// --- Server Setup ---
var serverContext = InterceptorSubjectContext
    .Create()
    .WithFullPropertyTracking()
    .WithRegistry()
    .WithParents()
    .WithLifecycle()
    .WithDataAnnotationValidation()
    .WithHostedServices(builder.Services);

var serverRoot = TestNode.CreateWithGraph(serverContext);
participants.Add((configuration.Server.Name, serverRoot));

var serverMutationEngine = new MutationEngine(
    serverRoot, configuration.Server, coordinator,
    sharedLoggerFactory.CreateLogger($"MutationEngine.{configuration.Server.Name}"));

mutationEngines.Add(serverMutationEngine);

builder.Services.AddSingleton<IHostedService>(serverMutationEngine);

// Server-side connector wiring
switch (configuration.Connector.ToLowerInvariant())
{
    case "opcua":
        builder.Services.AddSingleton(serverRoot);
        builder.Services.AddOpcUaSubjectServer(
            _ => serverRoot,
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
        break;

    case "mqtt":
        builder.Services.AddSingleton(serverRoot);
        builder.Services.AddMqttSubjectServer(
            _ => serverRoot,
            _ => new MqttServerConfiguration
            {
                BrokerPort = 1883,
                PathProvider = new AttributeBasedPathProvider("mqtt", '/'),
                DefaultQualityOfService = MqttQualityOfServiceLevel.AtLeastOnce,
                UseRetainedMessages = true,
                SourceTimestampSerializer = static ts => ts.UtcTicks.ToString(),
                SourceTimestampDeserializer = static s => long.TryParse(s, out var ticks)
                    ? new DateTimeOffset(ticks, TimeSpan.Zero) : null
            });
        break;
}

// --- Client Setup ---
for (var clientIndex = 0; clientIndex < configuration.Clients.Count; clientIndex++)
{
    var clientConfig = configuration.Clients[clientIndex];

    var clientContext = InterceptorSubjectContext
        .Create()
        .WithFullPropertyTracking()
        .WithRegistry()
        .WithParents()
        .WithLifecycle()
        .WithDataAnnotationValidation()
        .WithSourceTransactions()
        .WithHostedServices(builder.Services);

    var clientRoot = TestNode.CreateWithGraph(clientContext);
    participants.Add((clientConfig.Name, clientRoot));

    // Client mutation engine
    var clientMutationEngine = new MutationEngine(
        clientRoot, clientConfig, coordinator,
        sharedLoggerFactory.CreateLogger($"MutationEngine.{clientConfig.Name}"));
    mutationEngines.Add(clientMutationEngine);
    builder.Services.AddSingleton<IHostedService>(clientMutationEngine);

    // Client connector wiring (connect directly to server)
    switch (configuration.Connector.ToLowerInvariant())
    {
        case "opcua":
            builder.Services.AddOpcUaSubjectClientSource(
                _ => clientRoot,
                sp =>
                {
                    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
                    var telemetryContext = DefaultTelemetry.Create(b =>
                        b.Services.AddSingleton(loggerFactory));

                    return new OpcUaClientConfiguration
                    {
                        ServerUrl = $"opc.tcp://localhost:{serverPort}",
                        RootName = "Root",
                        TypeResolver = new OpcUaTypeResolver(sp.GetRequiredService<ILogger<OpcUaTypeResolver>>()),
                        ValueConverter = new OpcUaValueConverter(),
                        SubjectFactory = new OpcUaSubjectFactory(DefaultSubjectFactory.Instance),
                        TelemetryContext = telemetryContext,
                        BufferTime = TimeSpan.FromMilliseconds(100),
                    };
                });
            break;

        case "mqtt":
            builder.Services.AddMqttSubjectClientSource(
                _ => clientRoot,
                _ => new MqttClientConfiguration
                {
                    BrokerHost = "localhost",
                    BrokerPort = serverPort,
                    PathProvider = new AttributeBasedPathProvider("mqtt", '/'),
                    DefaultQualityOfService = MqttQualityOfServiceLevel.AtLeastOnce,
                    UseRetainedMessages = true,
                    SourceTimestampSerializer = static ts => ts.UtcTicks.ToString(),
                    SourceTimestampDeserializer = static s => long.TryParse(s, out var ticks)
                        ? new DateTimeOffset(ticks, TimeSpan.Zero) : null,
                    ReconnectDelay = TimeSpan.FromSeconds(1),
                    MaximumReconnectDelay = TimeSpan.FromSeconds(10),
                    HealthCheckInterval = TimeSpan.FromSeconds(5),
                    CircuitBreakerFailureThreshold = 3,
                    CircuitBreakerCooldown = TimeSpan.FromSeconds(10),
                });
            break;
    }

    // Client chaos engine (if configured) - connector resolved after build
    if (clientConfig.Chaos != null)
    {
        var capturedClientRoot = clientRoot;
        var clientChaosEngine = new ChaosEngine(
            clientConfig.Name,
            clientConfig.Chaos,
            coordinator,
            target: null, // resolved after build
            sharedLoggerFactory.CreateLogger($"ChaosEngine.{clientConfig.Name}"));

        chaosEngines.Add(clientChaosEngine);
        builder.Services.AddSingleton<IHostedService>(clientChaosEngine);
    }
}

// Server chaos engine (if configured)
ChaosEngine? serverChaosEngine = null;
if (configuration.Server.Chaos != null)
{
    serverChaosEngine = new ChaosEngine(
        configuration.Server.Name,
        configuration.Server.Chaos,
        coordinator,
        target: null, // resolved after build
        sharedLoggerFactory.CreateLogger($"ChaosEngine.{configuration.Server.Name}"));

    chaosEngines.Add(serverChaosEngine);
    builder.Services.AddSingleton<IHostedService>(serverChaosEngine);
}

// --- Verification Engine ---
builder.Services.AddSingleton<IHostedService>(sp => new VerificationEngine(
    configuration,
    coordinator,
    participants,
    mutationEngines,
    chaosEngines,
    cycleLoggerProvider,
    sp.GetRequiredService<ILogger<VerificationEngine>>()));

// Build and wire up connectors for chaos engines
var host = builder.Build();

var allConnectors = host.Services.GetServices<IHostedService>()
    .OfType<ISubjectConnector>()
    .ToList();

foreach (var chaosEngine in chaosEngines)
{
    // Find the connector whose root subject matches one of the participants, then cast to IChaosTarget
    var participant = participants.FirstOrDefault(p => p.Name == chaosEngine.TargetName);
    var connector = allConnectors.FirstOrDefault(c => c.RootSubject == participant.Root);
    if (connector is IChaosTarget chaosTarget)
    {
        chaosEngine.SetTarget(chaosTarget);
    }
}

await host.RunAsync();
