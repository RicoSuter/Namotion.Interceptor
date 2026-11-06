using HomeBlaze.Automation.Tests.Testing;
using Microsoft.Extensions.Hosting;
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
}
