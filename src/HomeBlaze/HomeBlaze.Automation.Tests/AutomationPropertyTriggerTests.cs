using System.Collections.Concurrent;
using HomeBlaze.Automation.Runtime;
using HomeBlaze.Automation.Tests.Testing;
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using static HomeBlaze.Automation.Tests.Testing.RuleBuilder;

namespace HomeBlaze.Automation.Tests;

public class AutomationPropertyTriggerTests
{
    private const string StormOnlyIf = "not /Blinds/IsMoving and (./LastTriggeredAt == null or now - ./LastTriggeredAt > 30m)";

    private static Action<Automation> Storm(string? onlyIf = null) => automation => automation.Rules = Keyed(
        ("retract", Create(
            "Retract blinds",
            AutomationRuleMode.On,
            [When("/Weather/Gust > 8", TimeSpan.FromSeconds(20), rearm: "/Weather/Gust < 6")],
            [Invoke("/Blinds/Close")],
            onlyIf)));

    private static async Task HoldGustAsync(AutomationTestHost host, double gust)
    {
        host.Weather.Gust = gust;
        await host.FlushAsync();
        await host.AdvanceAsync(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task WhenTheValueHoldsForTheHold_ThenTheRuleRunsOnce()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(Storm());
        var rule = host.Rule("retract");
        host.Weather.Gust = 9;
        await host.FlushAsync();

        // Act
        await host.AdvanceAsync(TimeSpan.FromSeconds(19));
        var runsBeforeTheHold = rule.RunCount;
        await host.AdvanceAsync(TimeSpan.FromSeconds(1));

        // Assert
        Assert.Equal(0L, runsBeforeTheHold);
        Assert.Equal(1L, rule.RunCount);
        Assert.Equal(1, host.Blinds.CloseCount);
        Assert.Equal(AutomationRunOutcome.Succeeded, rule.LastRunOutcome);
        Assert.Equal("Ran /Blinds/Close()", rule.LastRunMessage);
        Assert.Equal(AutomationTestHost.Start.AddSeconds(20), rule.LastTriggeredAt);
        Assert.Equal("On: Triggered, waiting to rearm", rule.Status);
    }

    [Fact]
    public async Task WhenGustsMoveAroundTheThreshold_ThenOnlyTheRearmExpressionAllowsAnotherRun()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(Storm());
        var rule = host.Rule("retract");
        await HoldGustAsync(host, 9);

        // Act
        host.Weather.Gust = 7;
        await HoldGustAsync(host, 9);
        var runsAroundTheThreshold = rule.RunCount;
        host.Weather.Gust = 5;
        await HoldGustAsync(host, 10);

        // Assert
        Assert.Equal(1L, runsAroundTheThreshold);
        Assert.Equal(2L, rule.RunCount);
        Assert.Equal(2, host.Blinds.CloseCount);
    }

    [Fact]
    public async Task WhenTheRearmExpressionIsTrueWhileTheExpressionStaysTrue_ThenTheRuleRunsOnce()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(
            ("retract", On("Retract", When("/Weather/Gust > 8", rearm: "/Weather/Rain < 1"), Invoke("/Blinds/Close")))));
        host.Weather.Rain = 0;
        host.Weather.Gust = 10;
        await host.FlushAsync();

        // Act
        host.Weather.Rain = 0.1;
        await host.FlushAsync();
        host.Weather.Rain = 0.2;
        await host.FlushAsync();
        var runsWhileTheGustStays = host.Blinds.CloseCount;
        host.Weather.Gust = 7;
        await host.FlushAsync();
        host.Weather.Gust = 9;
        await host.FlushAsync();

        // Assert
        Assert.Equal(1, runsWhileTheGustStays);
        Assert.Equal(2, host.Blinds.CloseCount);
    }

    [Fact]
    public async Task WhenATriggerReadsTheTimeAndItsRearmExpressionStaysTrue_ThenTheRuleRunsOnce()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(
            ("retract", On("Retract", When("time > 1h and /Weather/Gust > 8", rearm: "/Weather/Rain < 1"), Invoke("/Blinds/Close")))));
        host.Weather.Rain = 0;
        host.Weather.Gust = 10;
        await host.FlushAsync();

        // Act
        for (var second = 0; second < 5; second++)
        {
            await host.AdvanceAsync(TimeSpan.FromSeconds(1));
        }

        // Assert
        Assert.Equal(1, host.Blinds.CloseCount);
    }

    [Fact]
    public async Task WhenTheTriggerStartsTrueWithRearm_ThenOnlyTheRearmExpressionAllowsTheFirstRun()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation =>
        {
            automation.Rules = Keyed(
                ("retract", Create(
                    "Retract blinds",
                    AutomationRuleMode.On,
                    [When("/Weather/Gust > 8", rearm: "/Weather/Gust < 6", activation: TriggerActivation.WaitForFalseToTrue)],
                    [Invoke("/Blinds/Close")])));
        });
        var rule = host.Rule("retract");
        host.Weather.Gust = 9;
        await host.FlushAsync();
        await host.StopAsync();
        await ((IHostedService)host.Automation).StartAsync(CancellationToken.None);
        await host.FlushAsync();

        // Act
        host.Weather.Gust = 7;
        await host.FlushAsync();
        host.Weather.Gust = 9;
        await host.FlushAsync();
        var runsWithoutRearm = rule.RunCount;
        host.Weather.Gust = 5;
        await host.FlushAsync();
        host.Weather.Gust = 9;
        await host.FlushAsync();

        // Assert
        Assert.Equal(0L, runsWithoutRearm);
        Assert.Equal(1L, rule.RunCount);
    }

    [Fact]
    public async Task WhenOnlyIfRejects_ThenNoRunIsRecordedAndTheStatusSaysWhy()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(Storm(StormOnlyIf));
        var rule = host.Rule("retract");
        host.Blinds.IsMoving = true;

        // Act
        await HoldGustAsync(host, 9);

        // Assert
        Assert.Equal(0L, rule.RunCount);
        Assert.Null(rule.LastRunOutcome);
        Assert.Null(rule.LastRunMessage);
        Assert.Null(rule.LastTriggeredAt);
        Assert.Equal(0, host.Blinds.CloseCount);
        Assert.Equal("On: Waiting since 2026-11-06 12:00:20: Only if is false", rule.Status);
        Assert.DoesNotContain("retract", File.Exists(host.CheckpointPath) ? await File.ReadAllTextAsync(host.CheckpointPath) : string.Empty);
    }

    [Fact]
    public async Task WhenTheRuleTriggersAgainWithinTheCooldown_ThenOnlyIfRejectsIt()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(Storm(StormOnlyIf));
        var rule = host.Rule("retract");
        await HoldGustAsync(host, 9);

        // Act
        host.Weather.Gust = 5;
        await HoldGustAsync(host, 9);

        // Assert
        Assert.Equal(1L, rule.RunCount);
        Assert.Equal(AutomationRunOutcome.Succeeded, rule.LastRunOutcome);
        Assert.Equal(1, host.Blinds.CloseCount);
    }

    [Fact]
    public async Task WhenAValueIsMissing_ThenTheRuleShowsTheMissingPathAndWaitsForIt()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation =>
            automation.Rules = Keyed(("retract", On("Retract", When("/Weather/Gust > 8"), Invoke("/Blinds/Close")))));
        var rule = host.Rule("retract");
        var statusWhileUnknown = rule.Status;

        // Act
        host.Weather.Gust = 9;
        await host.FlushAsync();

        // Assert
        Assert.Equal("On: Unknown since 2026-11-06 12:00:00, missing /Weather/Gust", statusWhileUnknown);
        Assert.Equal(1, host.Blinds.CloseCount);
        Assert.Equal("On: Triggered, waiting to rearm", rule.Status);
    }

    [Fact]
    public async Task WhenAReadingStaysEmptyForTheHold_ThenTheOutageIsReported()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(
            ("silent", On(
                "Rain gauge silent",
                When("/Weather/Rain == null", TimeSpan.FromMinutes(15)),
                Invoke("/Notifier/SendNotification", ("notification.Message", "The rain gauge has not reported for 15 minutes"))))));

        // Act
        await host.AdvanceAsync(TimeSpan.FromMinutes(15));

        // Assert
        Assert.Equal("The rain gauge has not reported for 15 minutes", Assert.Single(host.Notifier.Sent).Message);
    }

    [Fact]
    public async Task WhenAPulseIsObserved_ThenTheRuleRunsAlthoughTheValueIsBackAlready()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation =>
            automation.Rules = Keyed(("pulse", On("Pulse", When("/Weather/Power > 500"), Invoke("/Blinds/Close")))));

        // Act
        host.Weather.Power = 600;
        host.Weather.Power = 0;
        await host.FlushAsync();

        // Assert
        Assert.Equal(1, host.Blinds.CloseCount);
    }

    [Fact]
    public async Task WhenATriggerReadsNow_ThenItFiresWithinOneSecond()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation =>
            automation.Rules = Keyed(("stale", On("Stale", When("now - /Weather/LastUpdated > 15m"), Invoke("/Blinds/Close")))));
        host.Weather.LastUpdated = AutomationTestHost.Start;
        await host.FlushAsync();

        // Act
        await host.AdvanceAsync(TimeSpan.FromMinutes(15));
        var runsAtFifteenMinutes = host.Blinds.CloseCount;
        await host.AdvanceAsync(TimeSpan.FromSeconds(1));

        // Assert
        Assert.Equal(0, runsAtFifteenMinutes);
        Assert.Equal(1, host.Blinds.CloseCount);
    }

    [Fact]
    public async Task WhenAWatchedPropertyThatHoldsASubjectIsReplaced_ThenTheTriggerFollowsTheNewSubject()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(
            ("retract", On("Retract", When("/Holder/Sensor != null and /Holder/Sensor/Gust > 8"), Invoke("/Blinds/Close")))));
        var holder = new TestSensorHolder { Sensor = new TestSensor { Gust = 3 } };
        host.Graph.Add("Holder", holder);
        await host.FlushAsync();
        var replacement = new TestSensor { Gust = 4 };

        // Act
        holder.Sensor = replacement;
        await host.FlushAsync();
        replacement.Gust = 9;
        await host.FlushAsync();

        // Assert
        Assert.Equal(1, host.Blinds.CloseCount);
    }

    [Fact]
    public async Task WhenASubjectIsReplacedWhileTheWorkerIsBusy_ThenTheTriggerReadsTheCurrentValueOfTheNewSubject()
    {
        // Arrange
        var gate = new TestGate { BlockOnCall = 1 };
        var holder = new TestSensorHolder { Sensor = new TestSensor() };
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(
            ("retract", On("Retract", When("/Holder/Sensor/Gust > 8 and /Weather/Rain > 0"), Invoke("/Blinds/Close"))),
            ("step", On("Step", When("/Weather/Rain > 0"), Invoke("/Gate/Step"))),
            ("wait", On("Wait", When("/Weather/Power > 0"), Invoke("/Blinds/Wait")))));
        host.Graph.Add("Holder", holder);
        host.Graph.Add("Gate", gate);
        await host.FlushAsync();
        host.Weather.Power = 1;
        await AsyncTestHelpers.WaitUntilAsync(() => host.Blinds.IsWaiting);
        host.Weather.Rain = 1;
        var replacement = new TestSensor { Gust = 0 };
        holder.Sensor = replacement;
        host.Blinds.Release();
        await AsyncTestHelpers.WaitUntilAsync(() => gate.IsWaiting);

        // Act
        replacement.Gust = 10;
        gate.Release();
        await host.FlushAsync();

        // Assert
        Assert.Equal(1, host.Blinds.CloseCount);
    }

    [Fact]
    public async Task WhenTwoAutomationsWatchDifferentProperties_ThenAChangeIsRoutedOnlyToTheAutomationThatWatchesIt()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(_ => { });
        var listeners = new List<CountingListener>();
        host.ChangeDispatcher.ListenerDecorator = listener =>
        {
            var counting = new CountingListener(listener);
            listeners.Add(counting);
            return counting;
        };
        var gusts = await host.AddAutomationAsync("Gusts", automation => automation.Rules = Keyed(
            ("retract", On("Retract", When("/Weather/Gust > 8"), Invoke("/Blinds/Close")))));
        var gustsListener = Assert.Single(listeners);
        var showers = await host.AddAutomationAsync("Showers", automation => automation.Rules = Keyed(
            ("open", On("Open", When("/Weather/Rain > 5"), Invoke("/Blinds/Open")))));
        var showersListener = listeners[1];

        // Act
        host.Weather.Gust = 9;
        await gusts.FlushAsync();
        await showers.FlushAsync();

        // Assert
        Assert.Equal(nameof(TestSensor.Gust), Assert.Single(gustsListener.WatchedChanges).Name);
        Assert.Empty(showersListener.WatchedChanges);
        Assert.Equal(1L, gusts.Rules["retract"].RunCount);
        Assert.Equal(0L, showers.Rules["open"].RunCount);
        Assert.Equal(1, host.ChangeDispatcher.SourceCount);
    }

    [Fact]
    public async Task WhenTheLastAutomationOfAGraphStops_ThenTheDispatcherReleasesTheGraph()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(Storm());
        var storm = host.Automation;
        var shower = await host.AddAutomationAsync("Shower", automation => automation.Rules = Keyed(
            ("open", On("Open", When("/Weather/Rain > 5"), Invoke("/Blinds/Open")))));
        var dispatcher = host.ChangeDispatcher;

        // Act
        await ((IHostedService)storm).StopAsync(CancellationToken.None);
        var sourcesWithOneAutomation = dispatcher.SourceCount;
        var watchersOfGust = dispatcher.CountWatchers(host.Graph.Context, new PropertyReference(host.Weather, nameof(TestSensor.Gust)));
        await ((IHostedService)shower).StopAsync(CancellationToken.None);

        // Assert
        Assert.Equal(1, sourcesWithOneAutomation);
        Assert.Equal(0, watchersOfGust);
        Assert.Equal(0, dispatcher.SourceCount);
    }

    [Fact]
    public async Task WhenAPropertyThatARuleReadsIsAddedWithAValue_ThenTheRuleRuns()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(
            ("retract", On("Retract", When("/Weather/Wind > 5"), Invoke("/Blinds/Close")))));
        var statusWhileMissing = host.Rule("retract").Status;

        // Act
        host.Weather.TryGetRegisteredSubject()!.AddProperty<double?>("Wind", _ => 10);
        await host.FlushAsync();

        // Assert
        Assert.Equal("On: Unknown since 2026-11-06 12:00:00, missing /Weather/Wind", statusWhileMissing);
        Assert.Equal(1, host.Blinds.CloseCount);
    }

    [Fact]
    public async Task WhenAPropertyThatARuleReadsIsAddedWithoutAValueAndWrittenLater_ThenTheRuleRuns()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(
            ("retract", On("Retract", When("/Weather/Wind > 5"), Invoke("/Blinds/Close")))));
        object? wind = null;
        var property = host.Weather.TryGetRegisteredSubject()!.AddProperty(
            "Wind", typeof(double?), _ => Volatile.Read(ref wind), (_, value) => Volatile.Write(ref wind, value));
        await host.FlushAsync();
        var runsWhileNull = host.Blinds.CloseCount;

        // Act
        property.SetValue(10.0);
        await host.FlushAsync();

        // Assert
        Assert.Equal(0, runsWhileNull);
        Assert.Equal(1, host.Blinds.CloseCount);
    }

    [Fact]
    public async Task WhenAnAttributeThatARuleReadsIsAdded_ThenTheRuleRuns()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(
            ("retract", On("Retract", When("/Weather/Gust@Maximum > 5"), Invoke("/Blinds/Close")))));
        var statusWhileMissing = host.Rule("retract").Status;

        // Act
        host.Weather.TryGetRegisteredProperty(nameof(TestSensor.Gust))!.AddAttribute<double?>("Maximum", _ => 10);
        await host.FlushAsync();

        // Assert
        Assert.Equal("On: Unknown since 2026-11-06 12:00:00, missing /Weather/Gust@Maximum", statusWhileMissing);
        Assert.Equal(1, host.Blinds.CloseCount);
    }

    [Fact]
    public async Task WhenAPropertyIsAddedWithTheNameOfAChildThatAPathLedThrough_ThenThePathLeadsToTheProperty()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(
            ("retract", On("Retract", When("/Weather/Gust > 8"), Invoke("/Blinds/Close")))));
        host.Weather.Gust = 3;
        await host.FlushAsync();
        var statusThroughTheChild = host.Rule("retract").Status;

        // Act
        host.Graph.Root.TryGetRegisteredSubject()!.AddProperty<TestSensor>("Weather", _ => null);
        await host.FlushAsync();

        // Assert
        Assert.Equal("On: Armed", statusThroughTheChild);
        Assert.Equal("On: Unknown since 2026-11-06 12:00:00, missing /Weather/Gust", host.Rule("retract").Status);
    }

    private sealed class CountingListener(IAutomationChangeListener inner) : IAutomationChangeListener
    {
        private readonly ConcurrentQueue<PropertyReference> _watchedChanges = new();

        public IReadOnlyList<PropertyReference> WatchedChanges => [.. _watchedChanges];

        public void OnWatchedPropertyChanged(PropertyChangedInput input)
        {
            _watchedChanges.Enqueue(input.Property);
            inner.OnWatchedPropertyChanged(input);
        }

        public void OnGraphChanged(int chainDepth) => inner.OnGraphChanged(chainDepth);
    }
}
