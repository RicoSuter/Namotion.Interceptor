using HomeBlaze.Abstractions;
using HomeBlaze.Automation.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace HomeBlaze.Automation.Tests.Testing;

/// <summary>
/// An automation under <c>/{key}</c> next to a sensor at <c>/Weather</c>, blinds at <c>/Blinds</c>, a notifier at
/// <c>/Notifier</c> and a button at <c>/Button</c>, with a fake clock and a data directory of its own.
/// </summary>
internal sealed class AutomationTestHost : IAsyncDisposable
{
    public static readonly DateTimeOffset Start = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    private AutomationTestHost(TestGraph graph, FakeTimeProvider time, string dataDirectory, IAutomationAuthorizer authorizer)
    {
        Graph = graph;
        Time = time;
        Clock = new TestClock(time);
        DataDirectory = dataDirectory;
        Authorizer = authorizer;
        ServiceProvider = new ServiceCollection().AddSingleton<AutomationChangeDispatcher>().BuildServiceProvider();
    }

    public TestGraph Graph { get; }

    /// <summary>The fake time, which drives the timers of the automations.</summary>
    public FakeTimeProvider Time { get; }

    /// <summary>The clock of the automations: the fake time, with a wall clock that a test can set apart and counted wake-ups.</summary>
    public TestClock Clock { get; }

    public string DataDirectory { get; }

    public IAutomationAuthorizer Authorizer { get; }

    /// <summary>The logger of every automation of the host.</summary>
    public MessageLogger Logger { get; } = new();

    /// <summary>The services of every automation of the host, which share one change dispatcher.</summary>
    public IServiceProvider ServiceProvider { get; }

    public AutomationChangeDispatcher ChangeDispatcher => ServiceProvider.GetRequiredService<AutomationChangeDispatcher>();

    public TestSensor Weather { get; } = new();

    public TestBlinds Blinds { get; } = new();

    public TestNotifier Notifier { get; } = new();

    public TestButton Button { get; } = new();

    private readonly List<Automation> _automations = [];

    public Automation Automation { get; private set; } = null!;

    public string Key { get; private set; } = string.Empty;

    public string CheckpointPath => Path.Combine(DataDirectory, "AutomationState", Key + ".json");

    public static async Task<AutomationTestHost> StartAsync(
        Action<Automation> configure, DateTimeOffset? start = null, TimeZoneInfo? timeZone = null, IAutomationAuthorizer? authorizer = null)
    {
        var time = new FakeTimeProvider(start ?? Start);
        time.SetLocalTimeZone(timeZone ?? TimeZoneInfo.Utc);
        var graph = new TestGraph();
        var dataDirectory = Directory.CreateTempSubdirectory("automation-test").FullName;
        graph.Context.AddService<IDataDirectoryProvider>(new TestDataDirectory(dataDirectory));
        var host = new AutomationTestHost(graph, time, dataDirectory, authorizer ?? new AllowAllAutomationAuthorizer());
        graph.Add("Weather", host.Weather);
        graph.Add("Blinds", host.Blinds);
        graph.Add("Notifier", host.Notifier);
        graph.Add("Button", host.Button);
        await host.AddAutomationAsync("Storm", configure);
        return host;
    }

    public AutomationRule Rule(string key) => Automation.Rules[key];

    /// <summary>Creates an automation with the services of the host, not attached yet.</summary>
    public Automation CreateAutomation(string name) =>
        new(Graph.Resolver, Clock, Authorizer, ServiceProvider, Logger) { Name = name };

    /// <summary>Attaches a new automation under <c>/key</c>, starts it and waits until it processed its activation.</summary>
    public async Task<Automation> AddAutomationAsync(string key, Action<Automation> configure)
    {
        var automation = CreateAutomation(key);
        configure(automation);
        Graph.Add(key, automation);
        _automations.Add(automation);
        Automation = automation;
        Key = key;
        await ((IHostedService)automation).StartAsync(CancellationToken.None);
        await automation.FlushAsync();
        return automation;
    }

    public Task FlushAsync() => Automation.FlushAsync();

    public Task StopAsync() => ((IHostedService)Automation).StopAsync(CancellationToken.None);

    /// <summary>
    /// Stops the automation and starts it again as a new process finds it: no runtime state in memory and no readings
    /// of the devices.
    /// </summary>
    public async Task RestartAsync()
    {
        await StopAsync();
        Automation.CurrentState = null;
        Automation.StateEnteredAt = null;
        foreach (var rule in Automation.Rules.Values)
        {
            rule.Status = null;
            rule.LastTriggeredAt = null;
            rule.LastRunOutcome = null;
            rule.LastRunMessage = null;
            rule.RunCount = null;
        }

        Weather.Gust = null;
        Weather.Rain = null;
        Weather.Power = null;
        Weather.Door = null;
        await ((IHostedService)Automation).StartAsync(CancellationToken.None);
        await Automation.FlushAsync();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var automation in _automations)
        {
            await ((IHostedService)automation).StopAsync(CancellationToken.None);
        }

        Directory.Delete(DataDirectory, recursive: true);
    }

    private sealed class TestDataDirectory(string dataDirectory) : IDataDirectoryProvider
    {
        public string DataDirectory { get; } = dataDirectory;
    }
}
