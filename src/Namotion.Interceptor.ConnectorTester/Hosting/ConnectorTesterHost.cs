using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor.ConnectorTester.Configuration;
using Namotion.Interceptor.ConnectorTester.Connectors;
using Namotion.Interceptor.ConnectorTester.Engine;
using Namotion.Interceptor.ConnectorTester.Engine.Chaos;
using Namotion.Interceptor.ConnectorTester.Engine.Mutation;
using Namotion.Interceptor.ConnectorTester.Engine.Verification;
using Namotion.Interceptor.ConnectorTester.Logging;
using Namotion.Interceptor.ConnectorTester.Model;
using Namotion.Interceptor.ConnectorTester.Performance;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Hosting;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.ConnectorTester.Hosting;

/// <summary>
/// Builds the connector tester host: registers connectors, mutation engines, chaos engines,
/// the optional verification engine, and creates performance profilers. The Build method
/// returns a composite carrying the host plus the engines/profilers that need to live for
/// the lifetime of the process.
/// </summary>
public sealed class ConnectorTesterHost
{
    public IHost Host { get; }
    public IReadOnlyList<PerformanceProfiler> Profilers { get; }
    public VerificationEngine? VerificationEngine { get; }
    public RunModeSelection RunModeSelection { get; }

    private ConnectorTesterHost(
        IHost host,
        IReadOnlyList<PerformanceProfiler> profilers,
        VerificationEngine? verificationEngine,
        RunModeSelection runModeSelection)
    {
        Host = host;
        Profilers = profilers;
        VerificationEngine = verificationEngine;
        RunModeSelection = runModeSelection;
    }

    public static ConnectorTesterHost Build(string[] args, string runDirectory)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory
        });

        // Cycle logger provider doubles as ILoggerProvider and ICycleLifecycleNotifier.
        var cycleLoggerProvider = new CycleLoggerProvider(runDirectory);
        builder.Services.AddSingleton(cycleLoggerProvider);
        builder.Services.AddSingleton<ICycleLifecycleNotifier>(cycleLoggerProvider);
        builder.Logging.AddProvider(cycleLoggerProvider);

        // Shared logger factory for engines created before DI host build.
        // Includes both console output and cycle logger so engine events appear in per-cycle log files.
        var sharedLoggerFactory = LoggerFactory.Create(b =>
        {
            b.AddConsole();
            b.AddProvider(cycleLoggerProvider);
        });

        builder.Services.Configure<ConnectorTesterConfiguration>(
            builder.Configuration.GetSection("ConnectorTester"));

        var coordinator = new TestCycleCoordinator();
        builder.Services.AddSingleton(coordinator);

        var configuration = builder.Configuration
            .GetSection("ConnectorTester")
            .Get<ConnectorTesterConfiguration>() ?? new ConnectorTesterConfiguration();

        // Assign stable participant indices from config position.
        configuration.Server.Index = 0;
        for (var i = 0; i < configuration.Clients.Count; i++)
        {
            configuration.Clients[i].Index = i + 1;
        }

        configuration.Server.Chaos?.Validate();
        foreach (var client in configuration.Clients)
        {
            client.Chaos?.Validate();
        }

        var runModeSelection = RunModeSelector.Select(args, configuration);
        var bindings = ConnectorBindingsRegistry.Resolve(configuration.ConnectorKind);

        var participants = new Dictionary<string, TestNode>();
        var mutationEngines = new List<MutationEngineHost>();
        var chaosEngines = new List<ChaosEngine>();

        var participantConfigurations = EnumerateActiveParticipants(configuration, runModeSelection).ToList();

        foreach (var (participantConfiguration, isServer) in participantConfigurations)
        {
            var context = InterceptorSubjectContext
                .Create()
                .WithFullPropertyTracking()
                .WithRegistry()
                .WithParents()
                .WithLifecycle()
                .WithTransactions()
                .WithSourceTransactions()
                .WithHostedServices(builder.Services);

            var root = TestNode.CreateWithGraph(context, configuration.CollectionCount, configuration.DictionaryCount);
            participants[participantConfiguration.Name] = root;

            var mutationLogger = sharedLoggerFactory.CreateLogger($"MutationEngine.{participantConfiguration.Name}");
            var mutationEngine = configuration.NumberOfBatches > 0
                ? MutationEngineHost.CreateBatch(root, participantConfiguration, coordinator, mutationLogger,
                    configuration.NumberOfBatches, participantConfiguration.Index)
                : MutationEngineHost.CreateRandom(root, participantConfiguration, coordinator, mutationLogger);
            mutationEngines.Add(mutationEngine);
            builder.Services.AddSingleton<IHostedService>(mutationEngine);

            if (isServer)
            {
                bindings.RegisterServer(builder.Services, root, bindings.DefaultPort);
            }
            else
            {
                bindings.RegisterClient(builder.Services, root, participantConfiguration, bindings.DefaultPort);
            }
        }

        builder.Services.AddSingleton<IFaultTargetResolver>(serviceProvider =>
        {
            var connectors = serviceProvider.GetServices<IHostedService>().OfType<ISubjectConnector>();
            return new FaultTargetResolver(participants, connectors);
        });

        foreach (var (participantConfiguration, _) in participantConfigurations)
        {
            if (participantConfiguration.Chaos == null)
            {
                continue;
            }

            var chaosLogger = sharedLoggerFactory.CreateLogger($"ChaosEngine.{participantConfiguration.Name}");
            var capturedConfiguration = participantConfiguration;
            builder.Services.AddSingleton<IHostedService>(serviceProvider =>
            {
                var resolver = serviceProvider.GetRequiredService<IFaultTargetResolver>();
                var engine = new ChaosEngine(
                    capturedConfiguration.Name,
                    capturedConfiguration.Chaos!,
                    coordinator,
                    resolver,
                    chaosLogger);
                chaosEngines.Add(engine);
                return engine;
            });
        }

        VerificationEngine? verificationEngine = null;
        if (runModeSelection.Mode == RunMode.Verify)
        {
            builder.Services.AddSingleton(serviceProvider =>
            {
                verificationEngine = new VerificationEngine(
                    configuration,
                    coordinator,
                    participants,
                    mutationEngines,
                    chaosEngines,
                    cycleLoggerProvider,
                    serviceProvider.GetRequiredService<IHostApplicationLifetime>(),
                    serviceProvider.GetRequiredService<ILogger<VerificationEngine>>(),
                    runDirectory);
                return verificationEngine;
            });
            builder.Services.AddSingleton<IHostedService>(serviceProvider => serviceProvider.GetRequiredService<VerificationEngine>());
        }

        var host = builder.Build();

        if (runModeSelection.Mode == RunMode.Verify)
        {
            verificationEngine = host.Services.GetRequiredService<VerificationEngine>();
        }

        var profilers = participants
            .Select(participant => new PerformanceProfiler(
                ((IInterceptorSubject)participant.Value).Context,
                participant.Key,
                configuration.MetricsReportingInterval,
                runDirectory,
                coordinator))
            .ToList();

        return new ConnectorTesterHost(host, profilers, verificationEngine, runModeSelection);
    }

    private static IEnumerable<(ParticipantConfiguration Configuration, bool IsServer)> EnumerateActiveParticipants(
        ConnectorTesterConfiguration configuration,
        RunModeSelection runMode)
    {
        if (runMode.Mode == RunMode.Verify || runMode.ParticipantName == configuration.Server.Name)
        {
            yield return (configuration.Server, IsServer: true);
        }

        foreach (var client in configuration.Clients)
        {
            if (runMode.Mode == RunMode.Verify || runMode.ParticipantName == client.Name)
            {
                yield return (client, IsServer: false);
            }
        }
    }
}
