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
        var resolutions = 0;
        automation.Runtime!.Engine.Routes.BeforePublication = () => resolutions++;

        // Act
        holder.Sensor = new TestSensor { Gust = 9 };
        await automation.FlushAsync();
        var (graphChangesOffTheRoute, resolutionsOffTheRoute) = (listener.GraphChanges, resolutions);
        host.Graph.Add("Weather", new TestSensor { Gust = 9 });
        await automation.FlushAsync();

        // Assert
        Assert.Equal(0, graphChangesOffTheRoute);
        Assert.Equal(0, resolutionsOffTheRoute);
        Assert.False(Registration(automation).IsUnbounded);
        Assert.NotEqual(0, listener.GraphChanges);
        Assert.NotEqual(0, resolutions);
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
        var resolutions = 0;
        host.Automation.Runtime!.Engine.Routes.BeforePublication = () => resolutions++;

        // Act
        host.Weather.Gust = 9;
        await host.FlushAsync();
        host.Weather.Gust = 0;
        await host.FlushAsync();

        // Assert
        Assert.Equal(1, host.Blinds.CloseCount);
        Assert.Equal(0, resolutions);
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

    [Fact]
    public async Task WhenAPropertyOnTheNewRouteChangesBeforeTheRouteIsPublished_ThenThePathsAreResolvedAgain()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(Rule("/Room/Sensor/Gust > 8"));
        host.Graph.Add("Room", new TestRoom { Sensor = new TestSensor { Gust = 3 } });
        await host.FlushAsync();
        var replacement = new TestRoom { Sensor = new TestSensor { Gust = 4 } };
        var windy = new TestSensor { Gust = 9 };
        var routes = host.Automation.Runtime!.Engine.Routes;
        routes.BeforePublication = () =>
        {
            routes.BeforePublication = null;
            replacement.Sensor = windy;
        };

        // Act
        host.Graph.Add("Room", replacement);
        await host.FlushAsync();

        // Assert
        var trigger = host.Automation.Runtime.Engine.Rules[0].PropertyTriggers[0];
        Assert.Equal([new PropertyReference(windy, nameof(TestSensor.Gust))], trigger.Dependencies);
        Assert.Equal(1, host.Blinds.CloseCount);
    }

    // The writes run on the worker, between the input that resolved the paths and its publication, so that the change of
    // the watched property is processed before the graph change: the resolution of that input is the first to see the
    // new sensor.
    [Fact]
    public async Task WhenAStructuralChangeIsFirstSeenWhileATriggerStopsPending_ThenTheTriggerItLeadsElsewhereIsEvaluated()
    {
        // Arrange
        var holder = new TestSensorHolder { Sensor = new TestSensor { Gust = 1 } };
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(
            ("gusts", On("Gusts", When("/Holder/Sensor/Gust > 8"), Invoke("/Blinds/Close"))),
            ("rain", Create("Rain", AutomationRuleMode.On, [When("/Weather/Rain > 1")], [Invoke("/Blinds/Open")], onlyIf: "/Weather/Power > 100"))));
        host.Graph.Add("Holder", holder);
        await host.FlushAsync();
        host.Weather.Rain = 2;
        await host.FlushAsync();
        var routes = host.Automation.Runtime!.Engine.Routes;
        routes.BeforePublication = () =>
        {
            routes.BeforePublication = null;
            host.Weather.Rain = 0;
            holder.Sensor = new TestSensor { Gust = 9 };
        };

        // Act
        host.Graph.Add("Other", new TestSensor());
        await host.FlushAsync();

        // Assert
        Assert.Equal(1, host.Blinds.CloseCount);
        Assert.Equal(0, host.Blinds.OpenCount);
    }

    // A trigger that becomes pending has every pending run requested again; one that stops pending does not, so only the
    // resolution of that input sees the new sensor of Only if.
    [Fact]
    public async Task WhenAStructuralChangeIsFirstSeenWhileATriggerStopsPending_ThenAPendingRuleWhoseOnlyIfLeadsElsewhereRunsAgain()
    {
        // Arrange
        var holder = new TestSensorHolder { Sensor = new TestSensor { Power = 0 } };
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(
            ("gusts", Create("Gusts", AutomationRuleMode.On, [When("/Weather/Gust > 1")], [Invoke("/Blinds/Close")], onlyIf: "/Holder/Sensor/Power > 5")),
            ("rain", Create("Rain", AutomationRuleMode.On, [When("/Weather/Rain > 1")], [Invoke("/Blinds/Open")], onlyIf: "/Weather/Power > 100"))));
        host.Graph.Add("Holder", holder);
        await host.FlushAsync();
        host.Weather.Gust = 2;
        host.Weather.Rain = 2;
        await host.FlushAsync();
        var routes = host.Automation.Runtime!.Engine.Routes;
        routes.BeforePublication = () =>
        {
            routes.BeforePublication = null;
            host.Weather.Rain = 0;
            holder.Sensor = new TestSensor { Power = 9 };
        };

        // Act
        host.Graph.Add("Other", new TestSensor());
        await host.FlushAsync();

        // Assert
        Assert.Equal(1, host.Blinds.CloseCount);
        Assert.Equal(0, host.Blinds.OpenCount);
    }

    [Fact]
    public async Task WhenAStructuralChangeIsFirstSeenWhileAStateChangeActivatesRules_ThenAnActiveTriggerItLeadsElsewhereIsEvaluated()
    {
        // Arrange
        var holder = new TestSensorHolder { Sensor = new TestSensor { Gust = 1 } };
        await using var host = await AutomationTestHost.StartAsync(automation =>
        {
            automation.States = ["Day", "Night"];
            automation.InitialState = "Day";
            automation.Rules = Keyed(
                ("gusts", On("Gusts", When("/Holder/Sensor/Gust > 8"), Invoke("/Blinds/Close"))),
                ("toNight", Create("To night", AutomationRuleMode.On, [When("/Weather/Rain > 1")], [Enter("Night")], inState: "Day")),
                ("night", Create("Night", AutomationRuleMode.On, [When("/Weather/Power > 0")], [Invoke("/Blinds/Open")], inState: "Night")));
        });
        host.Graph.Add("Holder", holder);
        await host.FlushAsync();
        var routes = host.Automation.Runtime!.Engine.Routes;
        routes.BeforePublication = () =>
        {
            routes.BeforePublication = null;
            host.Weather.Rain = 2;
            holder.Sensor = new TestSensor { Gust = 9 };
        };

        // Act
        host.Graph.Add("Other", new TestSensor());
        await host.FlushAsync();

        // Assert
        Assert.Equal("Night", host.Automation.CurrentState);
        Assert.Equal(1, host.Blinds.CloseCount);
        Assert.Equal(0, host.Blinds.OpenCount);
    }

    // The route published before the resolution has every property that can change its result, so a change off the route
    // during the resolution needs no second resolution.
    [Fact]
    public async Task WhenAStructuralChangeOffTheRouteCommitsWhileThePathsAreResolved_ThenThePathsAreNotResolvedAgain()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(Rule("/Weather/Gust > 8"));
        var holder = new TestSensorHolder();
        host.Graph.Add("Holder", holder);
        await host.FlushAsync();
        var routes = host.Automation.Runtime!.Engine.Routes;
        var resolutions = 0;
        routes.BeforePublication = () =>
        {
            if (++resolutions == 1)
            {
                holder.Sensor = new TestSensor();
            }
        };

        // Act
        host.Graph.Add("Other", new TestSensor());
        await host.FlushAsync();

        // Assert
        Assert.Equal(1, resolutions);
    }

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

        public int GraphChanges => _graphChanges.Count;

        public void Reset() => _graphChanges.Clear();

        public void OnWatchedPropertyChanged(PropertyChangedInput input) => inner.OnWatchedPropertyChanged(input);

        public void OnGraphChanged(int chainDepth)
        {
            _graphChanges.Enqueue(chainDepth);
            inner.OnGraphChanged(chainDepth);
        }
    }
}
