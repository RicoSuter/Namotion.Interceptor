using HomeBlaze.Abstractions;
using HomeBlaze.Automation.Persistence;
using HomeBlaze.Automation.Tests.Testing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor;
using static HomeBlaze.Automation.Tests.Testing.RuleBuilder;

namespace HomeBlaze.Automation.Tests;

public class AutomationCheckpointLifecycleTests
{
    private static Action<Automation> Washer() => automation =>
    {
        automation.States = ["Idle", "Running"];
        automation.InitialState = "Idle";
        automation.Rules = Keyed(("started", Create("Started", AutomationRuleMode.On, [When("/Weather/Power > 500")], [Enter("Running")], inState: "Idle")));
    };

    [Fact]
    public async Task WhenTheAutomationIsRemoved_ThenItsCheckpointIsDeleted()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(Washer());
        var existedBefore = File.Exists(host.CheckpointPath);

        // Act
        host.Graph.Remove("Storm");
        await host.StopAsync();

        // Assert
        Assert.True(existedBefore);
        Assert.False(File.Exists(host.CheckpointPath));
    }

    [Fact]
    public async Task WhenTheAutomationMovesToAnotherFolder_ThenItStartsFromItsInitialStateAtTheNewPath()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(Washer());
        host.Weather.Power = 600;
        await host.FlushAsync();
        host.Weather.Power = 0;
        var automation = host.Automation;
        var stateBeforeMove = automation.CurrentState;
        var basement = new TestRoot();
        host.Graph.Add("Basement", basement);

        // Act
        // As storage moves a file: the same instance is added to its new folder before it is removed from the old one.
        basement.Children = new Dictionary<string, IInterceptorSubject> { ["Storm"] = automation };
        host.Graph.Remove("Storm");
        await automation.FlushAsync();
        var stateAfterMove = automation.CurrentState;
        host.Weather.Power = 600;
        await automation.FlushAsync();

        // Assert
        Assert.Equal("Running", stateBeforeMove);
        Assert.Equal("Idle", stateAfterMove);
        Assert.Equal(1L, host.Rule("started").RunCount);
        Assert.False(File.Exists(Path.Combine(host.DataDirectory, "Automations", "Storm.json")));
        Assert.Contains("\"Running\"", await File.ReadAllTextAsync(Path.Combine(host.DataDirectory, "Automations", "Basement", "Storm.json")));
    }

    [Fact]
    public async Task WhenTheAutomationIsInBothFoldersWhileItMoves_ThenItsRunsAreNotTakenAlong()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(Washer());
        host.Weather.Power = 600;
        await host.FlushAsync();
        host.Weather.Power = 0;
        var automation = host.Automation;
        var basement = new TestRoot();
        host.Graph.Add("Basement", basement);

        // Act
        basement.Children = new Dictionary<string, IInterceptorSubject> { ["Storm"] = automation };
        await automation.FlushAsync();
        host.Graph.Remove("Storm");
        await automation.FlushAsync();

        // Assert
        Assert.Null(host.Rule("started").LastTriggeredAt);
        Assert.Null(host.Rule("started").LastRunOutcome);
        Assert.Equal(0L, host.Rule("started").RunCount);
        Assert.Equal("Idle", automation.CurrentState);
        Assert.False(File.Exists(Path.Combine(host.DataDirectory, "Automations", "Storm.json")));
        Assert.True(File.Exists(Path.Combine(host.DataDirectory, "Automations", "Basement", "Storm.json")));
    }

    [Fact]
    public async Task WhenTheAutomationIsRemovedAndAddedAtAnotherPath_ThenItStartsFromItsInitialStateWithoutItsRuns()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(Washer());
        host.Weather.Power = 600;
        await host.FlushAsync();
        host.Weather.Power = 0;
        var automation = host.Automation;
        var stateBeforeMove = automation.CurrentState;

        // Act
        host.Graph.Remove("Storm");
        await host.StopAsync();
        host.Graph.Add("Laundry", automation);
        await ((IHostedService)automation).StartAsync(CancellationToken.None);
        await automation.FlushAsync();

        // Assert
        Assert.Equal("Running", stateBeforeMove);
        Assert.Equal("Idle", automation.CurrentState);
        Assert.Null(host.Rule("started").LastTriggeredAt);
        Assert.Equal(0L, host.Rule("started").RunCount);
        Assert.False(File.Exists(Path.Combine(host.DataDirectory, "Automations", "Storm.json")));
        Assert.True(File.Exists(Path.Combine(host.DataDirectory, "Automations", "Laundry.json")));
    }

    [Fact]
    public async Task WhenTheAutomationHadAnErrorBeforeItMoved_ThenItRunsWithoutTheError()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(
            ("jam", On("Jam", When("/Weather/Rain > 0"), Invoke("/Blinds/Jam")))));
        host.Weather.Rain = 1;
        await host.FlushAsync();
        host.Weather.Rain = 0;
        var automation = host.Automation;
        var statusBeforeMove = automation.Status;
        var basement = new TestRoot();
        host.Graph.Add("Basement", basement);

        // Act
        basement.Children = new Dictionary<string, IInterceptorSubject> { ["Storm"] = automation };
        host.Graph.Remove("Storm");
        await automation.FlushAsync();

        // Assert
        Assert.Equal(ServiceStatus.Error, statusBeforeMove);
        Assert.Equal(ServiceStatus.Running, automation.Status);
        Assert.Null(automation.StatusMessage);
    }

    [Fact]
    public async Task WhenTheAutomationStartedBeforeItHadAPath_ThenItLoadsTheCheckpointOfItsFirstPath()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation => { });
        var checkpointPath = Path.Combine(host.DataDirectory, "Automations", "Basement", "Washer.json");
        new AutomationCheckpointStore(checkpointPath).Write(new AutomationCheckpoint
        {
            CurrentState = "Running",
            StateEnteredAt = AutomationTestHost.Start.AddHours(-1),
            Rules = new Dictionary<string, RuleCheckpoint> { ["started"] = new() { RunCount = 3, LastTriggeredAt = AutomationTestHost.Start.AddHours(-1) } }
        });
        var washer = new Automation(host.Services, NullLogger<Automation>.Instance) { Name = "Washer" };
        Washer()(washer);

        // In the context of the graph as a root of its own, which has no path.
        ((IInterceptorSubject)washer).Context.AddFallbackContext(host.Graph.Context);
        await ((IHostedService)washer).StartAsync(CancellationToken.None);
        await washer.FlushAsync();
        var pathAtStart = host.Services.PathResolver.GetPath(washer, PathStyle.Canonical);
        var stateWithoutPath = washer.CurrentState;
        var basement = new TestRoot();
        basement.Children = new Dictionary<string, IInterceptorSubject> { ["Washer"] = washer };

        // Act
        host.Graph.Add("Basement", basement);
        await washer.FlushAsync();
        await ((IHostedService)washer).StopAsync(CancellationToken.None);

        // Assert
        Assert.Null(pathAtStart);
        Assert.Equal("Idle", stateWithoutPath);
        Assert.Equal("Running", washer.CurrentState);
        Assert.Equal(3L, washer.Rules["started"].RunCount);
        Assert.Contains("\"Running\"", await File.ReadAllTextAsync(checkpointPath));
    }
}
