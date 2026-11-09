using System.Collections.Concurrent;
using HomeBlaze.Automation.Runtime;
using HomeBlaze.Automation.Tests.Testing;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using static HomeBlaze.Automation.Tests.Testing.RuleBuilder;

namespace HomeBlaze.Automation.Tests;

public class AutomationGraphRouteTests
{
    [Fact]
    public async Task WhenAPropertyThatHoldsSubjectsAndIsNotOnTheRouteChanges_ThenTheAutomationNeitherReceivesAGraphChangeNorResolvesAgain()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(_ => { });
        var holder = new TestSensorHolder();
        host.Graph.Add("Holder", holder);
        var (automation, listener) = await AddCountedAsync(host, "Gusts", Rule("/Weather/Gust > 8"));
        listener.Reset();
        var resolutionsBefore = Routes(automation).ResolutionCount;

        // Act
        holder.Sensor = new TestSensor { Gust = 9 };
        await automation.FlushAsync();
        var (graphChangesOffTheRoute, resolutionsOffTheRoute) = (listener.GraphChanges, Routes(automation).ResolutionCount - resolutionsBefore);
        host.Graph.Add("Weather", new TestSensor { Gust = 9 });
        await automation.FlushAsync();

        // Assert
        Assert.Equal(0, graphChangesOffTheRoute);
        Assert.Equal(0, resolutionsOffTheRoute);
        Assert.False(Registration(automation).IsUnbounded);
        Assert.NotEqual(0, listener.GraphChanges);
        Assert.NotEqual(resolutionsBefore, Routes(automation).ResolutionCount);
        Assert.Equal(1, host.Blinds.CloseCount);
    }

    [Fact]
    public async Task WhenAPropertyOnTheRouteChanges_ThenThePathsAreResolvedAgain()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(_ => { });
        var holder = new TestSensorHolder { Sensor = new TestSensor { Gust = 3 } };
        host.Graph.Add("Holder", holder);
        var (automation, listener) = await AddCountedAsync(host, "Gusts", Rule("/Holder/Sensor/Gust > 8"));
        listener.Reset();

        // Act
        holder.Sensor = new TestSensor { Gust = 9 };
        await automation.FlushAsync();

        // Assert
        Assert.Equal(1, listener.GraphChanges);
        Assert.Equal(1, host.Blinds.CloseCount);
    }

    [Fact]
    public async Task WhenTheMissingEntryOfAnUnresolvedPathIsAdded_ThenThePathResolves()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(Rule("/Garden/Gust > 8"));
        var unknownStatus = host.Rule("rule").Status;

        // Act
        host.Graph.Add("Garden", new TestSensor { Gust = 9 });
        await host.FlushAsync();

        // Assert
        Assert.Contains("missing /Garden/Gust", unknownStatus);
        Assert.Equal(1, host.Blinds.CloseCount);
    }

    [Fact]
    public async Task WhenAPropertyIsAddedAtRunTime_ThenAPathToItResolves()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(Rule("/Weather/Wind > 8"));

        // Act
        ((IInterceptorSubject)host.Weather).TryGetRegisteredSubject()!.AddProperty("Wind", typeof(double), _ => 9.0, null);
        await host.FlushAsync();

        // Assert
        Assert.Equal(1, host.Blinds.CloseCount);
    }

    [Fact]
    public async Task WhenAWatchedPropertyChanges_ThenNoPathIsResolved()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(Rule("/Weather/Gust > 8"));
        var resolutionsBefore = Routes(host.Automation).ResolutionCount;

        // Act
        host.Weather.Gust = 9;
        await host.FlushAsync();
        host.Weather.Gust = 0;
        await host.FlushAsync();

        // Assert
        Assert.Equal(1, host.Blinds.CloseCount);
        Assert.Equal(resolutionsBefore, Routes(host.Automation).ResolutionCount);
    }

    // A property added with a null value publishes no change, so the attach of the property is what routes a graph change.
    [Fact]
    public async Task WhenAPropertyIsAddedAtRunTimeWithANullValue_ThenAPathToItResolvesAndTheValueIsWatched()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(Rule("/Weather/Wind > 8"));
        double? wind = null;
        var registered = ((IInterceptorSubject)host.Weather).TryGetRegisteredSubject()!;

        // Act
        var property = registered.AddProperty("Wind", typeof(double?), _ => wind, (_, value) => wind = (double?)value);
        await host.FlushAsync();
        var isWatchedAfterAdding = Registration(host.Automation).Watched.Contains(property.Reference);
        property.SetValue(9.0);
        await host.FlushAsync();

        // Assert
        Assert.True(isWatchedAfterAdding);
        Assert.Equal(1, host.Blinds.CloseCount);
    }

    [Fact]
    public async Task WhenTheParentOfTheAutomationIsReplacedAtTheSamePath_ThenARelativePathFollowsIt()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(_ => { });
        var calm = new TestFolder { Level = 1 };
        host.Graph.Add("Room", calm);
        var automation = await host.AddAutomationAsync("Levels", Rule("../../Level > 5"), automation => calm.Add("Levels", automation));
        var stormy = new TestFolder { Level = 9, Children = new Dictionary<string, IInterceptorSubject> { ["Levels"] = automation } };
        var pathBefore = automation.Runtime!.Location.AutomationPath;

        // Act
        host.Graph.Add("Room", stormy);
        await automation.FlushAsync();

        // Assert
        Assert.Equal("/Room/Levels", pathBefore);
        Assert.Equal("/Room/Levels", automation.Runtime.Location.AutomationPath);
        Assert.Equal(1, host.Blinds.CloseCount);
    }

    [Fact]
    public async Task WhenTheAutomationGainsASecondParent_ThenARelativePathThroughItBecomesAmbiguous()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(_ => { });
        var room = new TestFolder { Level = 9 };
        var other = new TestFolder();
        host.Graph.Add("Room", room);
        host.Graph.Add("Other", other);
        var automation = await host.AddAutomationAsync("Levels", Rule("../../Level > 5"), automation => room.Add("Levels", automation));
        var statusBefore = automation.Rules["rule"].Status;

        // Act
        other.Add("Levels", automation);
        await automation.FlushAsync();

        // Assert
        Assert.DoesNotContain("missing", statusBefore);
        Assert.Contains("missing ../../Level", automation.Rules["rule"].Status);
    }

    [Fact]
    public async Task WhenARuleGainsASecondParent_ThenARelativePathFromItBecomesAmbiguous()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(
            ("rule", On("Rule", When("../Enabled == true"), Invoke("/Blinds/Close")))));
        var statusBefore = host.Rule("rule").Status;
        var otherHost = new TestRuleHost();
        host.Graph.Add("OtherHost", otherHost);
        await host.FlushAsync();

        // Act
        otherHost.Rules = new Dictionary<string, AutomationRule> { ["copy"] = host.Rule("rule") };
        await host.FlushAsync();

        // Assert
        Assert.DoesNotContain("missing", statusBefore);
        Assert.Contains("missing ../Enabled", host.Rule("rule").Status);
    }

    // The write that gives the rule a second parent also changes a watched property; without routes the watched change
    // of a write is routed before its graph change.
    [Fact]
    public async Task WhenAWriteChangesAWatchedPropertyAndGivesARuleASecondParent_ThenTheWatchedChangeComesBeforeTheGraphChange()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(_ => { });
        var otherHost = new TestRuleHost();
        host.Graph.Add("OtherHost", otherHost);
        var (automation, listener) = await AddCountedAsync(host, "Rules", automation => automation.Rules = Keyed(
            ("rule", On("Rule", When("/OtherHost/Rules != null and ../Enabled == true"), Invoke("/Blinds/Close")))));
        var rulesOfOtherHost = new PropertyReference(otherHost, nameof(TestRuleHost.Rules));
        var isWatched = Registration(automation).Watched.Contains(rulesOfOtherHost);
        var isRouted = Registration(automation).Route.Contains(rulesOfOtherHost);
        listener.Reset();

        // Act
        otherHost.Rules = new Dictionary<string, AutomationRule> { ["copy"] = automation.Rules["rule"] };
        await automation.FlushAsync();

        // Assert
        Assert.True(isWatched);
        Assert.False(isRouted);
        Assert.Equal(["watched", "graph"], listener.Calls);
        Assert.Contains("missing ../Enabled", automation.Rules["rule"].Status);
    }

    [Fact]
    public async Task WhenAnEntryOfADictionaryOrACollectionChanges_ThenThePathsFollowIt()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(
            ("keyed", On("Keyed", When("/Room/Devices[gauge]/Gust > 8"), Invoke("/Blinds/Close"))),
            ("indexed", On("Indexed", When("/Room/Items[1]/Rain > 2"), Invoke("/Blinds/Open")))));
        var room = new TestRoom { Items = [new TestSensor { Rain = 0 }, new TestSensor { Rain = 1 }] };
        host.Graph.Add("Room", room);
        await host.FlushAsync();

        // Act
        room.Devices = new Dictionary<string, TestSensor> { ["gauge"] = new() { Gust = 9 } };
        room.Items = [new TestSensor { Rain = 0 }, new TestSensor { Rain = 5 }, room.Items[1]];
        await host.FlushAsync();

        // Assert
        Assert.Equal(1, host.Blinds.CloseCount);
        Assert.Equal(1, host.Blinds.OpenCount);
    }

    // The registry keeps one parent per property, with the first key, so a second key for a subject the dictionary holds
    // already leaves the structure version as it was, and the paths resolve as before until the next structural change
    // anywhere, as without routes.
    [Fact]
    public async Task WhenADictionaryGainsASecondKeyForASubjectItHolds_ThenThePathsResolveAtTheNextStructuralChangeAnywhere()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(_ => { });
        var sensor = new TestSensor { Gust = 9 };
        var room = new TestRoom { Devices = new Dictionary<string, TestSensor> { ["d1"] = sensor } };
        var holder = new TestSensorHolder();
        host.Graph.Add("Room", room);
        host.Graph.Add("Holder", holder);
        var automation = await host.AddAutomationAsync("Gusts", Rule("/Room/Devices[d2]/Gust > 5"));

        // Act
        room.Devices = new Dictionary<string, TestSensor> { ["d1"] = sensor, ["d2"] = sensor };
        await automation.FlushAsync();
        holder.Sensor = new TestSensor();
        await automation.FlushAsync();

        // Assert
        Assert.Equal(1, host.Blinds.CloseCount);
        Assert.False(Routes(automation).IsUnbounded);
    }

    // The other automation resolves the shared path at the current version after a change on its own route, so the
    // write that the registry does not count leaves a result in the cache of the resolver that both automations read.
    [Fact]
    public async Task WhenAnotherAutomationCachedAPathBeforeAWriteThatTheRegistryDoesNotCount_ThenBothResolveAtTheNextStructuralChangeAnywhere()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(_ => { });
        var sensor = new TestSensor { Gust = 9 };
        var room = new TestRoom { Devices = new Dictionary<string, TestSensor> { ["d1"] = sensor } };
        var other = new TestSensorHolder { Sensor = new TestSensor() };
        var unrelated = new TestSensorHolder();
        host.Graph.Add("Room", room);
        host.Graph.Add("Other", other);
        host.Graph.Add("Unrelated", unrelated);
        var first = await host.AddAutomationAsync("First", Rule("/Room/Devices[d2]/Gust > 5"));
        var second = await host.AddAutomationAsync("Second", automation => automation.Rules = Keyed(
            ("rule", On("Rule", When("/Room/Devices[d2]/Gust > 5"), Invoke("/Blinds/Close"))),
            ("other", On("Other", When("/Other/Sensor/Gust > 5"), Invoke("/Blinds/Open")))));
        other.Sensor = new TestSensor();
        await second.FlushAsync();

        // Act
        room.Devices = new Dictionary<string, TestSensor> { ["d1"] = sensor, ["d2"] = sensor };
        await first.FlushAsync();
        await second.FlushAsync();
        unrelated.Sensor = new TestSensor();
        await first.FlushAsync();
        await second.FlushAsync();

        // Assert
        Assert.Equal(2, host.Blinds.CloseCount);
    }

    [Fact]
    public async Task WhenTheSourceOfAnEventTriggerIsAddedLater_ThenTheTriggerSubscribesToIt()
    {
        // Arrange
        var room = new TestRoom();
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(
            ("press", Create("Press", AutomationRuleMode.On, [OnEvent("/Room/Button", "TestPressEvent")], [Invoke("/Blinds/Close")]))));
        host.Graph.Add("Room", room);
        await host.FlushAsync();
        var statusBefore = host.Rule("press").Status;
        var button = new TestButton();

        // Act
        room.Button = button;
        await host.FlushAsync();
        button.Press("Release");
        await host.FlushAsync();

        // Assert
        Assert.Equal("On: No subject at /Room/Button", statusBefore);
        Assert.Equal(1, host.Blinds.CloseCount);
    }

    // The write runs after the paths were resolved and before the union that has the new sensor holder is published, so
    // that the union before, which does not have that holder, does not route it.
    [Fact]
    public async Task WhenAPropertyOnlyTheNewRouteHasChangesBeforeTheRouteIsPublished_ThenItIsAGraphChangeAtTheChainDepthOfTheWriter()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(_ => { });
        host.Graph.Add("Room", new TestRoom { Sensor = new TestSensor { Gust = 3 } });
        var (automation, listener) = await AddCountedAsync(host, "Gusts", Rule("/Room/Sensor/Gust > 8"));
        var replacement = new TestRoom { Sensor = new TestSensor { Gust = 4 } };
        var windy = new TestSensor { Gust = 9 };
        var routes = Routes(automation);
        var sensorOfReplacement = new PropertyReference(replacement, nameof(TestRoom.Sensor));
        var wasRoutedAtTheWrite = true;
        routes.BeforePublication = () =>
        {
            routes.BeforePublication = null;
            wasRoutedAtTheWrite = Registration(automation).Route.Contains(sensorOfReplacement);
            var writer = new Thread(() =>
            {
                using var run = AutomationInputQueue.EnterRun(5);
                replacement.Sensor = windy;
            });
            writer.Start();
            writer.Join();
        };
        listener.Reset();

        // Act
        host.Graph.Add("Room", replacement);
        await automation.FlushAsync();

        // Assert
        Assert.False(wasRoutedAtTheWrite);
        Assert.Equal([0, 6], listener.ChainDepths);
        var trigger = automation.Runtime!.Engine.Rules[0].PropertyTriggers[0];
        Assert.Equal([new PropertyReference(windy, nameof(TestSensor.Gust))], trigger.Dependencies);
        Assert.Equal(1, host.Blinds.CloseCount);
    }

    // A Save hands the trigger to the new rule instance, which its relative path starts at now; the trigger is evaluated
    // at the next graph change, whatever it changes, so the route is unbounded until then.
    [Fact]
    public async Task WhenASaveLeadsATriggerElsewhere_ThenTheNextGraphChangeOffItsRouteEvaluatesIt()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(
            ("rule", On("Calm", When("./Name == Gusty"), Invoke("/Blinds/Close")))));
        var holder = new TestSensorHolder();
        host.Graph.Add("Holder", holder);
        await host.FlushAsync();
        host.Automation.Rules = Keyed(("rule", On("Gusty", When("./Name == Gusty"), Invoke("/Blinds/Close"))));
        await host.FlushAsync();
        await host.Automation.ApplyConfigurationAsync(CancellationToken.None);
        await host.FlushAsync();
        var closesAfterTheSave = host.Blinds.CloseCount;

        // Act
        holder.Sensor = new TestSensor();
        await host.FlushAsync();

        // Assert
        Assert.Equal(0, closesAfterTheSave);
        Assert.Equal(1, host.Blinds.CloseCount);
        Assert.False(Routes(host.Automation).IsUnbounded);
    }

    [Fact]
    public async Task WhenTheRulesAreDeactivated_ThenTheirRoutesLeaveThePublishedRoute()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(_ => { });
        var holder = new TestSensorHolder { Sensor = new TestSensor() };
        host.Graph.Add("Holder", holder);
        var (automation, listener) = await AddCountedAsync(host, "Gusts", automation => automation.Rules = Keyed(
            ("gusts", On("Gusts", When("/Holder/Sensor/Gust > 8"), Invoke("/Blinds/Close"))),
            ("press", Create("Press", AutomationRuleMode.On, [OnEvent("/Holder/Sensor", "TestPressEvent")], [Invoke("/Blinds/Open")]))));
        var sensorOfHolder = new PropertyReference(holder, nameof(TestSensorHolder.Sensor));
        var wasRouted = Registration(automation).Route.Contains(sensorOfHolder);

        // Act
        automation.Enabled = false;
        await automation.ApplyConfigurationAsync(CancellationToken.None);
        await automation.FlushAsync();
        listener.Reset();
        holder.Sensor = new TestSensor();
        await automation.FlushAsync();

        // Assert
        Assert.True(wasRouted);
        Assert.DoesNotContain(sensorOfHolder, (IReadOnlySet<PropertyReference>)Registration(automation).Route);
        Assert.DoesNotContain(sensorOfHolder, Routes(automation).Union);
        Assert.Equal(0, listener.GraphChanges);
    }

    // No other rule reads the sensor of the room, so only the rearm expression puts its holder on the route.
    [Fact]
    public async Task WhenTheSubjectThatARearmExpressionReadsIsReplaced_ThenTheTriggerRearmsOnIt()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(_ => { });
        var room = new TestRoom { Sensor = new TestSensor { Rain = 5 } };
        host.Graph.Add("Room", room);
        var automation = await host.AddAutomationAsync("Rearm", automation => automation.Rules = Keyed(
            ("rule", On("Rule", When("/Weather/Gust > 8", rearm: "/Room/Sensor/Rain < 3"), Invoke("/Blinds/Close")))));
        host.Weather.Gust = 9;
        await automation.FlushAsync();
        host.Weather.Gust = 0;
        await automation.FlushAsync();

        // Act
        room.Sensor = new TestSensor { Rain = 1 };
        await automation.FlushAsync();
        host.Weather.Gust = 9;
        await automation.FlushAsync();

        // Assert
        Assert.Contains(new PropertyReference(room, nameof(TestRoom.Sensor)), Routes(automation).Union);
        Assert.Equal(2, host.Blinds.CloseCount);
    }

    [Fact]
    public async Task WhenATriggerStopsWaiting_ThenTheRouteOfItsOnlyIfLeavesTheUnion()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(_ => { });
        var room = new TestRoom { Sensor = new TestSensor { Power = 0 } };
        host.Graph.Add("Room", room);
        var automation = await host.AddAutomationAsync("Guarded", automation => automation.Rules = Keyed(
            ("rule", Create("Guarded", AutomationRuleMode.On, [When("/Weather/Gust > 8")], [Invoke("/Blinds/Close")], onlyIf: "/Room/Sensor/Power > 5"))));
        var sensorOfRoom = new PropertyReference(room, nameof(TestRoom.Sensor));
        host.Weather.Gust = 9;
        await automation.FlushAsync();
        var isRoutedWhileWaiting = Routes(automation).Union.Contains(sensorOfRoom);

        // Act
        host.Weather.Gust = 0;
        await automation.FlushAsync();

        // Assert
        Assert.True(isRoutedWhileWaiting);
        Assert.DoesNotContain(sensorOfRoom, Routes(automation).Union);
        Assert.DoesNotContain(sensorOfRoom, (IReadOnlySet<PropertyReference>)Registration(automation).Route);
        Assert.Equal(0, host.Blinds.CloseCount);
    }

    // The Save hands each trigger and its route to the trigger of the new rule instance, which the deactivation clears.
    [Fact]
    public async Task WhenASaveCarriesTheTriggersOverAndTheRulesAreDeactivatedThen_ThenOnlyTheLocationIsInTheUnion()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(_ => { });
        var room = new TestRoom { Sensor = new TestSensor { Gust = 0, Power = 0 }, Button = new TestButton() };
        host.Graph.Add("Room", room);
        var automation = await host.AddAutomationAsync("Carried", automation => automation.Rules = CreateCarriedRules());
        host.Weather.Gust = 9;
        await automation.FlushAsync();
        var unionBefore = Routes(automation).Union.ToHashSet();

        // Act
        await host.SaveAsync(CreateCarriedRules());
        var unionAfterTheSave = Routes(automation).Union.ToHashSet();
        automation.Enabled = false;
        await host.SaveAsync();

        // Assert
        Assert.Contains(new PropertyReference(room, nameof(TestRoom.Sensor)), unionBefore);
        Assert.Contains(new PropertyReference(room, nameof(TestRoom.Button)), unionBefore);
        Assert.Equal(unionBefore, unionAfterTheSave);
        Assert.Equal(Routes(automation).Location.Route.Properties.ToHashSet(), Routes(automation).Union.ToHashSet());
    }

    private static Dictionary<string, AutomationRule> CreateCarriedRules() => Keyed(
        ("gusts", On("Gusts", When("/Room/Sensor/Gust > 8"), Invoke("/Blinds/Close"))),
        ("press", Create("Press", AutomationRuleMode.On, [OnEvent("/Room/Button", "TestPressEvent")], [Invoke("/Blinds/Open")])),
        ("guarded", Create("Guarded", AutomationRuleMode.On, [When("/Weather/Gust > 8")], [Invoke("/Blinds/Close")], onlyIf: "/Room/Sensor/Power > 5")));

    [Fact]
    public async Task WhenAPathLeadsAboveTheAutomationAndItsParent_ThenEveryStructuralChangeReachesTheAutomation()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(_ => { });
        var holder = new TestSensorHolder();
        var building = new TestFolder { Level = 9 };
        var floor = new TestFolder();
        host.Graph.Add("Holder", holder);
        host.Graph.Add("Building", building);
        building.Add("Floor", floor);
        var (automation, listener) = await AddCountedAsync(host, "Levels", Rule("../../../Level > 5"), automation => floor.Add("Levels", automation));
        listener.Reset();

        // Act
        holder.Sensor = new TestSensor();
        await automation.FlushAsync();

        // Assert
        Assert.True(Registration(automation).IsUnbounded);
        Assert.Equal(1, listener.GraphChanges);
        Assert.Equal(1, host.Blinds.CloseCount);
    }

    private static Action<Automation> Rule(string expression) => automation => automation.Rules = Keyed(
        ("rule", On("Rule", When(expression), Invoke("/Blinds/Close"))));

    private static GraphRoutes Routes(Automation automation) => automation.Runtime!.Routes;

    private static AutomationChangeDispatcher.Registration Registration(Automation automation) =>
        automation.Runtime!.Changes.Registration!;

    private static async Task<(Automation Automation, CountingListener Listener)> AddCountedAsync(
        AutomationTestHost host, string key, Action<Automation> configure, Action<Automation>? attach = null)
    {
        CountingListener? counting = null;
        host.ChangeDispatcher.ListenerDecorator = listener => counting = new CountingListener(listener);
        var automation = await host.AddAutomationAsync(key, configure, attach);
        host.ChangeDispatcher.ListenerDecorator = null;
        return (automation, counting!);
    }

    private sealed class CountingListener(IAutomationChangeListener inner) : IAutomationChangeListener
    {
        private readonly ConcurrentQueue<int> _graphChanges = new();
        private readonly ConcurrentQueue<string> _calls = new();

        public int GraphChanges => _graphChanges.Count;

        public int[] ChainDepths => [.. _graphChanges];

        public string[] Calls => [.. _calls];

        public void Reset()
        {
            _graphChanges.Clear();
            _calls.Clear();
        }

        public void OnWatchedPropertyChanged(PropertyChangedInput input)
        {
            _calls.Enqueue("watched");
            inner.OnWatchedPropertyChanged(input);
        }

        public void OnGraphChanged(int chainDepth)
        {
            _graphChanges.Enqueue(chainDepth);
            _calls.Enqueue("graph");
            inner.OnGraphChanged(chainDepth);
        }
    }
}
