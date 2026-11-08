using HomeBlaze.Abstractions;
using HomeBlaze.Automation.Expressions;
using HomeBlaze.Automation.Runtime;
using HomeBlaze.Automation.Tests.Testing;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry.Abstractions;
using static HomeBlaze.Automation.Tests.Testing.RuleBuilder;

namespace HomeBlaze.Automation.Tests.Runtime;

public class GraphRoutesTests
{
    // The root has no property Room, so the route also has the property that, added at run time, would take precedence
    // over the child of [InlinePaths].
    [Theory]
    [InlineData("/Room/Devices[gauge]/Gust", nameof(TestRoom.Devices))]
    [InlineData("/Room/Items[0]/Gust", nameof(TestRoom.Items))]
    [InlineData("/Room/Sensor/Gust", nameof(TestRoom.Sensor))]
    public async Task WhenAPathPassesPropertiesThatHoldSubjects_ThenTheRouteHasEachOfThem(string path, string holder)
    {
        // Arrange
        await using var fixture = await RouteFixture.CreateAsync();

        // Act
        var (property, route) = fixture.ResolveProperty(path);

        // Assert
        Assert.NotNull(property);
        Assert.Equal([fixture.RoomName, fixture.RootChildren, new PropertyReference(fixture.Room, holder)], route.Properties.ToArray());
        Assert.False(route.IsUnbounded);
    }

    [Fact]
    public async Task WhenTheEntryOfAPathIsMissing_ThenTheRouteHasThePropertyThatLacksIt()
    {
        // Arrange
        await using var fixture = await RouteFixture.CreateAsync();

        // Act
        var (property, route) = fixture.ResolveProperty("/Room/Devices[missing]/Gust");

        // Assert
        Assert.Null(property);
        Assert.Equal([fixture.RoomName, fixture.RootChildren, new PropertyReference(fixture.Room, nameof(TestRoom.Devices))], route.Properties.ToArray());
    }

    [Fact]
    public async Task WhenAPropertyOfAPathIsMissing_ThenTheRouteHasThePropertyThatAddingItChanges()
    {
        // Arrange
        await using var fixture = await RouteFixture.CreateAsync();

        // Act
        var (property, route) = fixture.ResolveProperty("/Room/Sensor/Wind");

        // Assert
        Assert.Null(property);
        Assert.Contains(new PropertyReference(fixture.Room.Sensor!, "Wind"), route.Properties.ToArray());
        Assert.False(route.IsUnbounded);
    }

    [Fact]
    public async Task WhenAPathEndsAtASecretProperty_ThenTheRouteHasOnlyItsHolders()
    {
        // Arrange
        await using var fixture = await RouteFixture.CreateAsync();

        // Act
        var (property, route) = fixture.ResolveProperty("/Room/Sensor/ApiKey");

        // Assert
        Assert.Null(property);
        Assert.Equal([fixture.RoomName, fixture.RootChildren, new PropertyReference(fixture.Room, nameof(TestRoom.Sensor))], route.Properties.ToArray());
        Assert.False(route.IsUnbounded);
    }

    [Fact]
    public async Task WhenAPathLeadsUpFromTheRuleAndTheAutomation_ThenTheRouteHasTheirHoldersAndIsBounded()
    {
        // Arrange
        await using var fixture = await RouteFixture.CreateAsync();

        // Act
        var (property, route) = fixture.ResolveProperty("../../Level");

        // Assert
        Assert.NotNull(property);
        Assert.Equal(
            [new PropertyReference(fixture.Automation, nameof(Automation.Rules)), new PropertyReference(fixture.Folder, nameof(TestFolder.Children))],
            route.Properties.ToArray());
        Assert.False(route.IsUnbounded);
    }

    [Theory]
    [InlineData("../../../Level")]
    [InlineData("../../../Room/Sensor/Gust")]
    public async Task WhenAPathLeadsUpFromASubjectThatDoesNotTellItsParentGains_ThenTheRouteIsUnbounded(string path)
    {
        // Arrange
        await using var fixture = await RouteFixture.CreateAsync();

        // Act
        var (_, route) = fixture.ResolveProperty(path);

        // Assert
        Assert.True(route.IsUnbounded);
    }

    // Detaching the other host above its Rules property gives the rule a single parent without a change of a holder.
    [Fact]
    public async Task WhenAPathLeadsUpFromARuleWithSeveralParents_ThenTheRouteHasItsHoldersAndIsUnbounded()
    {
        // Arrange
        await using var fixture = await RouteFixture.CreateAsync();
        var otherHost = new TestRuleHost();
        fixture.Host.Graph.Add("OtherHost", otherHost);
        otherHost.Rules = new Dictionary<string, AutomationRule> { ["copy"] = fixture.Rule };

        // Act
        var (property, route) = fixture.ResolveProperty("../Enabled");

        // Assert
        Assert.Null(property);
        Assert.Equal(
            [new PropertyReference(fixture.Automation, nameof(Automation.Rules)), new PropertyReference(otherHost, nameof(TestRuleHost.Rules))],
            route.Properties.ToArray());
        Assert.True(route.IsUnbounded);
    }

    [Fact]
    public async Task WhenTheRoutesAreForNoAutomation_ThenAPathThatLeadsUpHasAnUnboundedRoute()
    {
        // Arrange
        await using var fixture = await RouteFixture.CreateAsync();
        var routes = new GraphRoutes(automation: null);
        var slot = new GraphRouteSlot();

        // Act
        AutomationPaths.ResolveProperty(fixture.Host.Graph.Resolver, "../Enabled", fixture.Rule, routes.BeginRoute());
        routes.EndRoute(slot);

        // Assert
        Assert.True(slot.Route.IsUnbounded);
    }

    [Fact]
    public async Task WhenTheLocationIsResolved_ThenTheRouteHasTheHoldersOfTheAutomationAndOfItsAncestors()
    {
        // Arrange
        await using var fixture = await RouteFixture.CreateAsync();
        var location = CheckpointLocation.Of(fixture.Automation, fixture.Host.Logger);

        // Act
        var hasMoved = location.HasMoved(fixture.Automation, fixture.Routes.BeginRoute());
        fixture.Routes.EndRoute(fixture.Routes.Location);

        // Assert
        Assert.False(hasMoved);
        Assert.Equal("/Folder/Automation", location.AutomationPath);
        Assert.Equal([new PropertyReference(fixture.Folder, nameof(TestFolder.Children)), fixture.RootChildren], fixture.Routes.Location.Route.Properties.ToArray());
        Assert.False(fixture.Routes.Location.Route.IsUnbounded);
    }

    [Fact]
    public async Task WhenAResolutionRecordsTheSameRouteAgain_ThenTheSlotKeepsTheStoredInstance()
    {
        // Arrange
        await using var fixture = await RouteFixture.CreateAsync();
        var (_, first) = fixture.ResolveProperty("/Room/Sensor/Gust");

        // Act
        var (_, second) = fixture.ResolveProperty("/Room/Sensor/Gust");

        // Assert
        Assert.Same(first, second);
    }

    [Fact]
    public async Task WhenRoutesAreStoredClearedAndMoved_ThenTheUnionHasThePropertiesOfTheStoredRoutes()
    {
        // Arrange
        await using var fixture = await RouteFixture.CreateAsync();
        var routes = fixture.Routes;
        var sensor = new GraphRouteSlot();
        var items = new GraphRouteSlot();
        var moved = new GraphRouteSlot();
        var sensorHolder = new PropertyReference(fixture.Room, nameof(TestRoom.Sensor));
        var itemsHolder = new PropertyReference(fixture.Room, nameof(TestRoom.Items));

        // Act
        Resolve(fixture, "/Room/Sensor/Gust", sensor);
        Resolve(fixture, "/Room/Items[0]/Gust", items);
        var unionOfBoth = routes.Union.ToHashSet();
        items.Clear();
        var unionAfterClear = routes.Union.ToHashSet();
        sensor.MoveTo(moved);
        var unionAfterMove = routes.Union.ToHashSet();
        Resolve(fixture, "../../../Level", moved);
        var isUnboundedAfterTheStepAbove = routes.IsUnbounded;
        moved.Clear();

        // Assert
        Assert.Contains(sensorHolder, unionOfBoth);
        Assert.Contains(itemsHolder, unionOfBoth);
        Assert.Contains(fixture.RootChildren, unionAfterClear);
        Assert.DoesNotContain(itemsHolder, unionAfterClear);
        Assert.Equal(unionAfterClear, unionAfterMove);
        Assert.Same(GraphRoute.Empty, sensor.Route);
        Assert.True(isUnboundedAfterTheStepAbove);
        Assert.Empty(routes.Union);
        Assert.False(routes.IsUnbounded);
    }

    [Theory]
    [InlineData(4, true)]
    [InlineData(2, false)]
    public async Task WhenAChangedUnionIsPublished_ThenAGraphChangeIsQueuedOnlyWhenTheStructureMovedSinceTheResolution(long versionAtPublication, bool isQueued)
    {
        // Arrange
        await using var fixture = await RouteFixture.CreateAsync();
        var version = 2L;
        var queue = new AutomationInputQueue();
        var routes = new GraphRoutes(fixture.Automation, new AutomationChangeReceiver(queue), () => version);
        var slot = new GraphRouteSlot();
        AutomationPaths.ResolveProperty(fixture.Host.Graph.Resolver, "/Room/Sensor/Gust", fixture.Rule, routes.BeginRoute());
        routes.EndRoute(slot);
        version = versionAtPublication;

        // Act
        var hasPublished = routes.Publish(chainDepth: 3);

        // Assert
        Assert.True(hasPublished);
        Assert.Equal(isQueued, queue.HasWork);
        if (isQueued)
        {
            Assert.Equal(3, Assert.IsType<GraphChangedInput>(await queue.ReadAsync(CancellationToken.None)).ChainDepth);
        }
    }

    [Fact]
    public async Task WhenAChangeIsInProgressAtTheResolution_ThenThePublicationQueuesAGraphChange()
    {
        // Arrange
        await using var fixture = await RouteFixture.CreateAsync();
        var version = 3L;
        var queue = new AutomationInputQueue();
        var routes = new GraphRoutes(fixture.Automation, new AutomationChangeReceiver(queue), () => version);
        AutomationPaths.ResolveProperty(fixture.Host.Graph.Resolver, "/Room/Sensor/Gust", fixture.Rule, routes.BeginRoute());
        routes.EndRoute(new GraphRouteSlot());
        version = 4;

        // Act
        routes.Publish(chainDepth: 0);

        // Assert
        Assert.True(queue.HasWork);
    }

    // The union published before has every property that can change the stored results, so a change during the
    // resolution is routed by it.
    [Fact]
    public async Task WhenTheUnionDidNotChange_ThenNeitherIsItPublishedNorAGraphChangeQueuedAfterTheStructureMoved()
    {
        // Arrange
        await using var fixture = await RouteFixture.CreateAsync();
        var version = 2L;
        var queue = new AutomationInputQueue();
        var routes = new GraphRoutes(fixture.Automation, new AutomationChangeReceiver(queue), () => version);
        var slot = new GraphRouteSlot();
        AutomationPaths.ResolveProperty(fixture.Host.Graph.Resolver, "/Room/Sensor/Gust", fixture.Rule, routes.BeginRoute());
        routes.EndRoute(slot);
        routes.Publish(chainDepth: 0);
        AutomationPaths.ResolveProperty(fixture.Host.Graph.Resolver, "/Room/Sensor/Gust", fixture.Rule, routes.BeginRoute());
        routes.EndRoute(slot);
        version = 6;

        // Act
        var hasPublished = routes.Publish(chainDepth: 0);

        // Assert
        Assert.False(hasPublished);
        Assert.False(queue.HasWork);
    }

    private static void Resolve(RouteFixture fixture, string path, GraphRouteSlot slot)
    {
        AutomationPaths.ResolveProperty(fixture.Host.Graph.Resolver, path, fixture.Rule, fixture.Routes.BeginRoute());
        fixture.Routes.EndRoute(slot);
    }

    /// <summary>
    /// A room at <c>/Room</c> with a sensor by key, by position and by reference, and a not started automation at
    /// <c>/Folder/Automation</c> with the rule <c>r1</c>, inside a folder of level 5.
    /// </summary>
    private sealed class RouteFixture : IAsyncDisposable
    {
        private readonly GraphRouteSlot _slot = new();

        private RouteFixture(AutomationTestHost host)
        {
            Host = host;
            Room = new TestRoom
            {
                Devices = new Dictionary<string, TestSensor> { ["gauge"] = new() },
                Items = [new TestSensor()],
                Sensor = new TestSensor()
            };
            Folder = new TestFolder { Level = 5 };
            Automation = host.CreateAutomation("Automation");
            Automation.Rules = Keyed(("r1", On("Rule", When("/Weather/Gust > 8"), Invoke("/Blinds/Close"))));
            host.Graph.Add("Room", Room);
            host.Graph.Add("Folder", Folder);
            Folder.Add("Automation", Automation);
            Routes = new GraphRoutes(Automation);
        }

        public AutomationTestHost Host { get; }

        public TestRoom Room { get; }

        public TestFolder Folder { get; }

        public Automation Automation { get; }

        public AutomationRule Rule => Automation.Rules["r1"];

        public GraphRoutes Routes { get; }

        public PropertyReference RootChildren => new(Host.Graph.Root, nameof(TestRoot.Children));

        public PropertyReference RoomName => new(Host.Graph.Root, "Room");

        public static async Task<RouteFixture> CreateAsync() => new(await AutomationTestHost.StartAsync(_ => { }));

        public (RegisteredSubjectProperty? Property, GraphRoute Route) ResolveProperty(string path)
        {
            var property = AutomationPaths.ResolveProperty(Host.Graph.Resolver, path, Rule, Routes.BeginRoute());
            Routes.EndRoute(_slot);
            return (property, _slot.Route);
        }

        public ValueTask DisposeAsync() => Host.DisposeAsync();
    }
}
