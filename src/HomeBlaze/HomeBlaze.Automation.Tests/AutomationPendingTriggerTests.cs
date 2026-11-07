using HomeBlaze.Automation.Tests.Testing;
using Namotion.Interceptor;
using static HomeBlaze.Automation.Tests.Testing.RuleBuilder;

namespace HomeBlaze.Automation.Tests;

public class AutomationPendingTriggerTests
{
    private const string Cooldown = "./LastTriggeredAt == null or now - ./LastTriggeredAt > 10m";

    private static Action<Automation> ConveyorGuard(string guard = Cooldown) => automation => automation.Rules = Keyed(
        ("overheat", Create(
            "Slow down when hot",
            AutomationRuleMode.On,
            [When("/Weather/Power > 40", TimeSpan.FromSeconds(30), rearm: "/Weather/Power < 38")],
            [Invoke("/Blinds/Close")],
            guard)));

    private static async Task SetAsync(AutomationTestHost host, double power)
    {
        host.Weather.Power = power;
        await host.FlushAsync();
    }

    private static async Task AdvanceAsync(AutomationTestHost host, TimeSpan time)
    {
        host.Time.Advance(time);
        await host.FlushAsync();
    }

    private static async Task RunOnceAndTriggerWithinTheCooldownAsync(AutomationTestHost host)
    {
        await SetAsync(host, 41);
        await AdvanceAsync(host, TimeSpan.FromSeconds(30));
        await SetAsync(host, 37);
        await SetAsync(host, 41);
        await AdvanceAsync(host, TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task WhenTheCooldownRejectsATriggerThatStaysTrue_ThenTheRuleRunsOnceWhenTheCooldownEnds()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(ConveyorGuard());
        var rule = host.Rule("overheat");
        await RunOnceAndTriggerWithinTheCooldownAsync(host);
        var statusWhileWaiting = rule.Status;

        // Act
        await AdvanceAsync(host, TimeSpan.FromSeconds(9 * 60 + 30));
        var runsBeforeTheCooldownEnds = host.Blinds.CloseCount;
        await AdvanceAsync(host, TimeSpan.FromSeconds(1));
        var runsWhenTheCooldownEnded = host.Blinds.CloseCount;
        await AdvanceAsync(host, TimeSpan.FromMinutes(15));

        // Assert
        Assert.Equal("On: Waiting since 12:01:00: Only if is false", statusWhileWaiting);
        Assert.Equal(1, runsBeforeTheCooldownEnds);
        Assert.Equal(2, runsWhenTheCooldownEnded);
        Assert.Equal(2, host.Blinds.CloseCount);
        Assert.Equal(2L, rule.RunCount);
        Assert.Equal(AutomationTestHost.Start.AddMinutes(10).AddSeconds(31), rule.LastTriggeredAt);
        Assert.Equal("On: Triggered, waiting to rearm", rule.Status);
    }

    [Fact]
    public async Task WhenAWaitingTriggerTurnsFalse_ThenItStopsWaitingAndDoesNotRun()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(ConveyorGuard());
        await RunOnceAndTriggerWithinTheCooldownAsync(host);

        // Act
        await SetAsync(host, 39);
        await AdvanceAsync(host, TimeSpan.FromMinutes(15));

        // Assert
        Assert.Equal(1, host.Blinds.CloseCount);
        Assert.Equal("On: Armed", host.Rule("overheat").Status);
    }

    [Fact]
    public async Task WhenAPropertyOfOnlyIfChanges_ThenAWaitingTriggerRunsWithoutANewHold()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(ConveyorGuard("not /Blinds/IsMoving"));
        host.Blinds.IsMoving = true;
        await SetAsync(host, 41);
        await AdvanceAsync(host, TimeSpan.FromSeconds(30));
        var runsWhileMoving = host.Blinds.CloseCount;

        // Act
        host.Blinds.IsMoving = false;
        await host.FlushAsync();

        // Assert
        Assert.Equal(0, runsWhileMoving);
        Assert.Equal(1, host.Blinds.CloseCount);
        Assert.Equal(1L, host.Rule("overheat").RunCount);
    }

    [Fact]
    public async Task WhenNoTriggerWaits_ThenThePropertiesOfOnlyIfAreNotWatched()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(ConveyorGuard("not /Blinds/IsMoving"));
        var isMoving = new PropertyReference(host.Blinds, nameof(TestBlinds.IsMoving));
        var dispatcher = host.ChangeDispatcher;
        host.Blinds.IsMoving = true;
        var watchersWhileIdle = dispatcher.CountWatchers(host.Graph.Context, isMoving);
        await SetAsync(host, 41);
        await AdvanceAsync(host, TimeSpan.FromSeconds(30));
        var watchersWhileWaiting = dispatcher.CountWatchers(host.Graph.Context, isMoving);

        // Act
        await SetAsync(host, 30);

        // Assert
        Assert.Equal(0, watchersWhileIdle);
        Assert.Equal(1, watchersWhileWaiting);
        Assert.Equal(0, dispatcher.CountWatchers(host.Graph.Context, isMoving));
    }

    [Fact]
    public async Task WhenOnlyIfReadsTheTimeOfDay_ThenAWaitingTriggerRunsOnceItIsTrue()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(ConveyorGuard("time > 12h5m"));
        await SetAsync(host, 41);
        await AdvanceAsync(host, TimeSpan.FromSeconds(30));
        var runsBefore = host.Blinds.CloseCount;

        // Act
        for (var second = 0; second < 5 * 60; second++)
        {
            await AdvanceAsync(host, TimeSpan.FromSeconds(1));
        }

        // Assert
        Assert.Equal(0, runsBefore);
        Assert.Equal(1, host.Blinds.CloseCount);
    }

    [Fact]
    public async Task WhenARuleWithoutOnlyIfIsSkippedByAStateChange_ThenItRunsAfterTheStateChange()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation =>
        {
            automation.States = ["Idle", "Running"];
            automation.InitialState = "Idle";
            automation.RuleOrder = ["started", "anyState"];
            automation.Rules = Keyed(
                ("started", Create("Started", AutomationRuleMode.On, [When("/Weather/Power > 500")], [Enter("Running")], inState: "Idle")),
                ("anyState", Create("Any state", AutomationRuleMode.On, [When("/Weather/Power > 500")], [Invoke("/Blinds/Close")])));
        });

        // Act
        await SetAsync(host, 600);

        // Assert
        Assert.Equal("Running", host.Automation.CurrentState);
        Assert.Equal(1, host.Blinds.CloseCount);
        Assert.Equal(1L, host.Rule("anyState").RunCount);
    }

    [Fact]
    public async Task WhenTwoTriggersOfARuleFireForOneInput_ThenTheRuleRunsOnceAndNotAgainAfterTheCooldown()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(("storm", Create(
            "Storm", AutomationRuleMode.On, [When("/Weather/Power > 40"), When("/Weather/Power > 45")], [Invoke("/Blinds/Close")], Cooldown))));

        // Act
        await SetAsync(host, 50);
        var status = host.Rule("storm").Status;
        await AdvanceAsync(host, TimeSpan.FromMinutes(11));

        // Assert
        Assert.Equal("On: Triggered, waiting to rearm", status);
        Assert.Equal(1, host.Blinds.CloseCount);
        Assert.Equal(1L, host.Rule("storm").RunCount);
    }

    [Fact]
    public async Task WhenTwoWaitingTriggersOfARuleMayRun_ThenTheRuleRunsOnce()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(("storm", Create(
            "Storm", AutomationRuleMode.On, [When("/Weather/Power > 40"), When("/Weather/Rain > 0")], [Invoke("/Blinds/Close")],
            "not /Blinds/IsMoving"))));
        host.Blinds.IsMoving = true;
        await SetAsync(host, 50);
        host.Weather.Rain = 1;
        await host.FlushAsync();
        var runsWhileMoving = host.Blinds.CloseCount;

        // Act
        host.Blinds.IsMoving = false;
        await host.FlushAsync();
        await AdvanceAsync(host, TimeSpan.FromMinutes(11));

        // Assert
        Assert.Equal(0, runsWhileMoving);
        Assert.Equal(1, host.Blinds.CloseCount);
        Assert.Equal("On: Triggered, waiting to rearm", host.Rule("storm").Status);
    }

    [Fact]
    public async Task WhenTheWaitEndsBecauseTheTriggerTurnsFalse_ThenTheReasonIsClearedAndANewRejectionIsLoggedAgain()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(ConveyorGuard());
        await RunOnceAndTriggerWithinTheCooldownAsync(host);
        var statusWhileWaiting = host.Rule("overheat").Status;

        // Act
        await SetAsync(host, 37);
        var statusAfterTheWait = host.Rule("overheat").Status;
        await SetAsync(host, 41);
        await AdvanceAsync(host, TimeSpan.FromSeconds(30));

        // Assert
        Assert.Equal("On: Waiting since 12:01:00: Only if is false", statusWhileWaiting);
        Assert.Equal("On: Armed", statusAfterTheWait);
        Assert.Equal("On: Waiting since 12:01:30: Only if is false", host.Rule("overheat").Status);
        Assert.Equal(2, host.Logger.Messages.Count(message => message.EndsWith("did not run: Only if is false.", StringComparison.Ordinal)));
    }
}
