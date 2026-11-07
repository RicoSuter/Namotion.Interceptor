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
        Assert.Equal([fixture.RoomName, fixture.RootChildren, new PropertyReference(fixture.Room, holder)], route.Properties);
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
        Assert.Equal([fixture.RoomName, fixture.RootChildren, new PropertyReference(fixture.Room, nameof(TestRoom.Devices))], route.Properties);
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
        Assert.Contains(new PropertyReference(fixture.Room.Sensor!, "Wind"), route.Properties);
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
            route.Properties);
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

    [Fact]
    public async Task WhenTheRoutesAreForNoAutomation_ThenAPathThatLeadsUpHasAnUnboundedRoute()
    {
        // Arrange
        await using var fixture = await RouteFixture.CreateAsync();
        var routes = new GraphRoutes(fixture.Host.Graph.Resolver, null, () => -1, null);

        // Act
        var builder = routes.BeginRoute();
        routes.ResolveProperty("../Enabled", fixture.Rule, builder);

        // Assert
        Assert.True(builder.ToRoute(GraphRoute.Empty).IsUnbounded);
    }

    [Fact]
    public async Task WhenTheLocationIsRecorded_ThenTheRouteHasTheHoldersOfTheAutomationAndOfItsAncestors()
    {
        // Arrange
        await using var fixture = await RouteFixture.CreateAsync();

        // Act
        fixture.Routes.RecordLocation(fixture.Automation);

        // Assert
        Assert.Equal([new PropertyReference(fixture.Folder, nameof(TestFolder.Children)), fixture.RootChildren], fixture.Routes.Location.Properties);
        Assert.False(fixture.Routes.Location.IsUnbounded);
    }

    [Fact]
    public async Task WhenARouteIsResolvedAgainUnchanged_ThenItIsTheSameInstance()
    {
        // Arrange
        await using var fixture = await RouteFixture.CreateAsync();
        var (_, first) = fixture.ResolveProperty("/Room/Sensor/Gust");

        // Act
        var builder = fixture.Routes.BeginRoute();
        fixture.Routes.ResolveProperty("/Room/Sensor/Gust", fixture.Rule, builder);
        var second = builder.ToRoute(first);

        // Assert
        Assert.Same(first, second);
    }

    /// <summary>
    /// A room at <c>/Room</c> with a sensor by key, by position and by reference, and a not started automation at
    /// <c>/Folder/Automation</c> with the rule <c>r1</c>, inside a folder of level 5.
    /// </summary>
    private sealed class RouteFixture : IAsyncDisposable
    {
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
            var registry = host.Graph.Context.GetService<ISubjectRegistry>();
            Routes = new GraphRoutes(host.Graph.Resolver, Automation, () => registry.StructureVersion, null);
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
            var builder = Routes.BeginRoute();
            var property = Routes.ResolveProperty(path, Rule, builder);
            return (property, builder.ToRoute(GraphRoute.Empty));
        }

        public ValueTask DisposeAsync() => Host.DisposeAsync();
    }
}
