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
using Namotion.Interceptor.ResilienceTest.Chaos;
using Namotion.Interceptor.ResilienceTest.Configuration;
using Namotion.Interceptor.ResilienceTest.Engine;
using Namotion.Interceptor.ResilienceTest.Logging;
using Namotion.Interceptor.ResilienceTest.Model;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Validation;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

// Bind configuration
builder.Services.Configure<ResilienceTestConfiguration>(
    builder.Configuration.GetSection("ResilienceTest"));

// Add cycle logger provider
var cycleLoggerProvider = new CycleLoggerProvider();
builder.Services.AddSingleton(cycleLoggerProvider);
builder.Logging.AddProvider(cycleLoggerProvider);

// Shared coordinator for all engines
var coordinator = new TestCycleCoordinator();
builder.Services.AddSingleton(coordinator);

// Will be populated during setup
var participants = new List<(string Name, TestNode Root)>();
var mutationEngines = new List<MutationEngine>();
var chaosEngines = new List<ChaosEngine>();
var proxies = new List<TcpProxy>();

// Read configuration
var configuration = builder.Configuration
    .GetSection("ResilienceTest")
    .Get<ResilienceTestConfiguration>() ?? new ResilienceTestConfiguration();

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
    LoggerFactory.Create(b => b.AddConsole()).CreateLogger($"MutationEngine.{configuration.Server.Name}"));
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
                    BufferTime = TimeSpan.FromMilliseconds(100)
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

// Server chaos engine (if configured)
if (configuration.Server.Chaos != null)
{
    var serverChaosEngine = new ChaosEngine(
        configuration.Server.Name,
        configuration.Server.Chaos,
        coordinator,
        proxy: null, // server has no proxy
        connectorService: null, // resolved after build for lifecycle chaos
        LoggerFactory.Create(b => b.AddConsole()).CreateLogger($"ChaosEngine.{configuration.Server.Name}"));
    chaosEngines.Add(serverChaosEngine);
    builder.Services.AddSingleton<IHostedService>(serverChaosEngine);
}

// --- Client Setup ---
for (var clientIndex = 0; clientIndex < configuration.Clients.Count; clientIndex++)
{
    var clientConfig = configuration.Clients[clientIndex];
    var proxyPort = serverPort + 1 + clientIndex;

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

    // Create TCP proxy for this client
    var proxy = new TcpProxy(
        proxyPort, serverPort,
        LoggerFactory.Create(b => b.AddConsole()).CreateLogger($"TcpProxy.{clientConfig.Name}"));
    proxies.Add(proxy);

    // Client mutation engine
    var clientMutationEngine = new MutationEngine(
        clientRoot, clientConfig, coordinator,
        LoggerFactory.Create(b => b.AddConsole()).CreateLogger($"MutationEngine.{clientConfig.Name}"));
    mutationEngines.Add(clientMutationEngine);
    builder.Services.AddSingleton<IHostedService>(clientMutationEngine);

    // Client connector wiring
    switch (configuration.Connector.ToLowerInvariant())
    {
        case "opcua":
            var capturedProxyPort = proxyPort;
            builder.Services.AddOpcUaSubjectClientSource(
                _ => clientRoot,
                sp =>
                {
                    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
                    var telemetryContext = DefaultTelemetry.Create(b =>
                        b.Services.AddSingleton(loggerFactory));

                    return new OpcUaClientConfiguration
                    {
                        ServerUrl = $"opc.tcp://localhost:{capturedProxyPort}",
                        RootName = "Root",
                        TypeResolver = new OpcUaTypeResolver(sp.GetRequiredService<ILogger<OpcUaTypeResolver>>()),
                        ValueConverter = new OpcUaValueConverter(),
                        SubjectFactory = new OpcUaSubjectFactory(DefaultSubjectFactory.Instance),
                        TelemetryContext = telemetryContext,
                        BufferTime = TimeSpan.FromMilliseconds(100)
                    };
                });
            break;

        case "mqtt":
            builder.Services.AddMqttSubjectClientSource(
                _ => clientRoot,
                _ => new MqttClientConfiguration
                {
                    BrokerHost = "localhost",
                    BrokerPort = proxyPort,
                    PathProvider = new AttributeBasedPathProvider("mqtt", '/'),
                    DefaultQualityOfService = MqttQualityOfServiceLevel.AtLeastOnce,
                    UseRetainedMessages = true,
                    SourceTimestampSerializer = static ts => ts.UtcTicks.ToString(),
                    SourceTimestampDeserializer = static s => long.TryParse(s, out var ticks)
                        ? new DateTimeOffset(ticks, TimeSpan.Zero) : null
                });
            break;
    }

    // Client chaos engine (if configured)
    if (clientConfig.Chaos != null)
    {
        var clientChaosEngine = new ChaosEngine(
            clientConfig.Name,
            clientConfig.Chaos,
            coordinator,
            proxy,
            connectorService: null,
            LoggerFactory.Create(b => b.AddConsole()).CreateLogger($"ChaosEngine.{clientConfig.Name}"));
        chaosEngines.Add(clientChaosEngine);
        builder.Services.AddSingleton<IHostedService>(clientChaosEngine);
    }
}

// --- Verification Engine ---
builder.Services.AddSingleton<IHostedService>(sp =>
{
    return new VerificationEngine(
        configuration,
        coordinator,
        participants,
        mutationEngines,
        chaosEngines,
        cycleLoggerProvider,
        sp.GetRequiredService<ILogger<VerificationEngine>>());
});

// Build and start proxies
var host = builder.Build();

foreach (var proxy in proxies)
{
    await proxy.StartAsync(CancellationToken.None);
}

// Run the host (blocks until shutdown)
try
{
    await host.RunAsync();
}
finally
{
    foreach (var proxy in proxies)
    {
        await proxy.DisposeAsync();
    }
}
