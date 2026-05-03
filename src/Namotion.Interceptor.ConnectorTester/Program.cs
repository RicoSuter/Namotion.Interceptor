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
using Namotion.Interceptor.WebSocket;

// Tick-precision timestamp serializers (not default Unix milliseconds) to ensure
// exact timestamp convergence in snapshot comparison.
static byte[] SerializeTickTimestamp(DateTimeOffset ts)
{
    Span<byte> buffer = stackalloc byte[20];
    System.Buffers.Text.Utf8Formatter.TryFormat(ts.UtcTicks, buffer, out var bytesWritten);
    return buffer[..bytesWritten].ToArray();
}

static DateTimeOffset? DeserializeTickTimestamp(ReadOnlyMemory<byte> value)
{
    return System.Buffers.Text.Utf8Parser.TryParse(value.Span, out long ticks, out int _)
        ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;
}

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

// Add a cycle logger provider (created first so it can be shared with sharedLoggerFactory)
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
var participants = new Dictionary<string, TestNode>();
var mutationEngines = new List<MutationEngine>();
var chaosEngines = new List<ChaosEngine>();

// Read configuration
var configuration = builder.Configuration
    .GetSection("ConnectorTester")
    .Get<ConnectorTesterConfiguration>() ?? new ConnectorTesterConfiguration();

// Parse --participant CLI arg for multi-process mode
string? participantFilter = null;
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--participant" && i + 1 < args.Length)
    {
        participantFilter = args[i + 1];
        break;
    }
}

MutationEngine CreateMutationEngine(TestNode root, ParticipantConfiguration config, int participantIndex, string logCategory)
{
    var logger = sharedLoggerFactory.CreateLogger(logCategory);

    if (configuration.NumberOfBatches > 0)
    {
        return new BatchMutationEngine(
            root, config, coordinator, logger,
            configuration.NumberOfBatches, participantIndex);
    }

    return new RandomMutationEngine(root, config, coordinator, logger);
}

// Determine server port based on connector type
int serverPort = configuration.Connector.ToLowerInvariant() switch
{
    "opcua" => 4840,
    "mqtt" => 1883,
    "websocket" => 8080,
    _ => 4840
};

// --- Server Setup ---
var skipServer = participantFilter != null && participantFilter != configuration.Server.Name;

if (!skipServer)
{
    var serverContext = InterceptorSubjectContext
        .Create()
        .WithFullPropertyTracking()
        .WithRegistry()
        .WithParents()
        .WithLifecycle()
        .WithHostedServices(builder.Services);

    var serverRoot = TestNode.CreateWithGraph(serverContext, configuration.ObjectCount);
    participants[configuration.Server.Name] = serverRoot;

    var serverMutationEngine = CreateMutationEngine(
        serverRoot, configuration.Server, 0, $"MutationEngine.{configuration.Server.Name}");
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
                    SourceTimestampSerializer = SerializeTickTimestamp,
                    SourceTimestampDeserializer = DeserializeTickTimestamp
                });
            break;

        case "websocket":
            builder.Services.AddSingleton(serverRoot);
            builder.Services.AddWebSocketSubjectServer(
                _ => serverRoot,
                config =>
                {
                    config.Port = serverPort;
                    config.PathProvider = new AttributeBasedPathProvider("ws");
                });
            break;
    }

    // Server chaos engine (if configured)
    if (configuration.Server.Chaos != null)
    {
        var serverChaosEngine = new ChaosEngine(
            configuration.Server.Name,
            configuration.Server.Chaos,
            coordinator,
            target: null, // resolved after build
            sharedLoggerFactory.CreateLogger($"ChaosEngine.{configuration.Server.Name}"));

        chaosEngines.Add(serverChaosEngine);
        builder.Services.AddSingleton<IHostedService>(serverChaosEngine);
    }
}

// --- Client Setup ---
for (var clientIndex = 0; clientIndex < configuration.Clients.Count; clientIndex++)
{
    var clientConfig = configuration.Clients[clientIndex];
    var skipClient = participantFilter != null && participantFilter != clientConfig.Name;

    if (skipClient)
    {
        continue;
    }

    var clientContext = InterceptorSubjectContext
        .Create()
        .WithFullPropertyTracking()
        .WithRegistry()
        .WithParents()
        .WithLifecycle()
            .WithSourceTransactions()
        .WithHostedServices(builder.Services);

    var clientRoot = TestNode.CreateWithGraph(clientContext, configuration.ObjectCount);
    participants[clientConfig.Name] = clientRoot;

    // Client mutation engine (participantIndex: server=0, clients=1,2,...)
    var clientMutationEngine = CreateMutationEngine(
        clientRoot, clientConfig, clientIndex + 1, $"MutationEngine.{clientConfig.Name}");
    mutationEngines.Add(clientMutationEngine);
    builder.Services.AddSingleton<IHostedService>(clientMutationEngine);

    // Client connector wiring (connect directly to server)
    switch (configuration.Connector.ToLowerInvariant())
    {
        case "opcua":
            builder.Services.AddKeyedOpcUaSubjectClientSource(
                clientConfig.Name,
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
                    SourceTimestampSerializer = static ts =>
                    {
                        Span<byte> buffer = stackalloc byte[20];
                        System.Buffers.Text.Utf8Formatter.TryFormat(ts.UtcTicks, buffer, out var bytesWritten);
                        return buffer[..bytesWritten].ToArray();
                    },
                    SourceTimestampDeserializer = static value => System.Buffers.Text.Utf8Parser.TryParse(value.Span, out long ticks, out int _bytesConsumed)
                        ? new DateTimeOffset(ticks, TimeSpan.Zero) : null,
                    ReconnectDelay = TimeSpan.FromSeconds(1),
                    MaximumReconnectDelay = TimeSpan.FromSeconds(10),
                    HealthCheckInterval = TimeSpan.FromSeconds(5),
                    CircuitBreakerFailureThreshold = 3,
                    CircuitBreakerCooldown = TimeSpan.FromSeconds(10),
                });
            break;

        case "websocket":
            builder.Services.AddWebSocketSubjectClientSource(
                _ => clientRoot,
                config =>
                {
                    config.ServerUri = new Uri($"ws://localhost:{serverPort}/ws");
                    config.ReconnectDelay = TimeSpan.FromSeconds(1);
                    config.MaxReconnectDelay = TimeSpan.FromSeconds(10);
                    config.PathProvider = new AttributeBasedPathProvider("ws");
                });
            break;
    }

    // Client chaos engine (if configured) - connector resolved after build
    if (clientConfig.Chaos != null)
    {
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

// --- Verification Engine (single-process only) ---
if (participantFilter == null)
{
    builder.Services.AddSingleton(sp => new VerificationEngine(
        configuration,
        coordinator,
        participants,
        mutationEngines,
        chaosEngines,
        cycleLoggerProvider,
        sp.GetRequiredService<IHostApplicationLifetime>(),
        sp.GetRequiredService<ILogger<VerificationEngine>>()));

    builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<VerificationEngine>());
}

// Build and wire up connectors for chaos engines
var host = builder.Build();

var allConnectors = host.Services.GetServices<IHostedService>()
    .OfType<ISubjectConnector>()
    .ToList();

var startupLogger = host.Services.GetRequiredService<ILoggerFactory>()
    .CreateLogger("Startup");

foreach (var chaosEngine in chaosEngines)
{
    // Find the connector whose root subject matches one of the participants, then cast to IFaultInjectable
    participants.TryGetValue(chaosEngine.TargetName, out var participantRoot);
    var connector = allConnectors.FirstOrDefault(c => c.RootSubject == participantRoot);
    if (connector is IFaultInjectable faultInjectable)
    {
        chaosEngine.SetTarget(faultInjectable);
    }
    else
    {
        startupLogger.LogWarning(
            "ChaosEngine [{Target}] could not be wired to a connector. Chaos will be skipped for this participant.",
            chaosEngine.TargetName);
    }
}

// Create performance profilers for all participants
var profilers = new List<PerformanceProfiler>();
foreach (var (name, root) in participants)
{
    var profiler = new PerformanceProfiler(
        ((IInterceptorSubject)root).Context,
        name,
        configuration.MetricsReportingInterval);
    profilers.Add(profiler);
}

await host.RunAsync();

// Dispose profilers on shutdown
foreach (var profiler in profilers)
{
    profiler.Dispose();
}

// Set non-zero exit code on convergence failure (single-process only)
if (participantFilter == null)
{
    var verificationEngine = host.Services.GetRequiredService<VerificationEngine>();
    if (verificationEngine.Failed)
    {
        Environment.ExitCode = 1;
    }
}
