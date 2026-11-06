using HomeBlaze.Automation.Tests.Testing;
using Microsoft.Extensions.Hosting;
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
    public async Task WhenTheAutomationMovesToAnotherPath_ThenItStartsFromItsInitialState()
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
        Assert.False(File.Exists(Path.Combine(host.DataDirectory, "Automations", "Storm.json")));
        Assert.True(File.Exists(Path.Combine(host.DataDirectory, "Automations", "Laundry.json")));
    }
}
