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
        var (property, route, isUnbounded) = fixture.ResolveProperty(path);

        // Assert
        Assert.NotNull(property);
        Assert.Equal([fixture.RoomName, fixture.RootChildren, new PropertyReference(fixture.Room, holder)], route);
        Assert.False(isUnbounded);
    }

    [Fact]
    public async Task WhenTheEntryOfAPathIsMissing_ThenTheRouteHasThePropertyThatLacksIt()
    {
        // Arrange
        await using var fixture = await RouteFixture.CreateAsync();

        // Act
        var (property, route, _) = fixture.ResolveProperty("/Room/Devices[missing]/Gust");

        // Assert
        Assert.Null(property);
        Assert.Equal([fixture.RoomName, fixture.RootChildren, new PropertyReference(fixture.Room, nameof(TestRoom.Devices))], route);
    }

    [Fact]
    public async Task WhenAPropertyOfAPathIsMissing_ThenTheRouteHasThePropertyThatAddingItChanges()
    {
        // Arrange
        await using var fixture = await RouteFixture.CreateAsync();

        // Act
        var (property, route, isUnbounded) = fixture.ResolveProperty("/Room/Sensor/Wind");

        // Assert
        Assert.Null(property);
        Assert.Contains(new PropertyReference(fixture.Room.Sensor!, "Wind"), route);
        Assert.False(isUnbounded);
    }

    [Fact]
    public async Task WhenAPathEndsAtASecretProperty_ThenTheRouteHasOnlyItsHolders()
    {
        // Arrange
        await using var fixture = await RouteFixture.CreateAsync();

        // Act
        var (property, route, isUnbounded) = fixture.ResolveProperty("/Room/Sensor/ApiKey");

        // Assert
        Assert.Null(property);
        Assert.Equal([fixture.RoomName, fixture.RootChildren, new PropertyReference(fixture.Room, nameof(TestRoom.Sensor))], route);
        Assert.False(isUnbounded);
    }

    [Fact]
    public async Task WhenAPathLeadsUpFromTheRuleAndTheAutomation_ThenTheRouteHasTheirHoldersAndIsBounded()
    {
        // Arrange
        await using var fixture = await RouteFixture.CreateAsync();

        // Act
        var (property, route, isUnbounded) = fixture.ResolveProperty("../../Level");

        // Assert
        Assert.NotNull(property);
        Assert.Equal(
            [new PropertyReference(fixture.Automation, nameof(Automation.Rules)), new PropertyReference(fixture.Folder, nameof(TestFolder.Children))],
            route);
        Assert.False(isUnbounded);
    }

    [Theory]
    [InlineData("../../../Level")]
    [InlineData("../../../Room/Sensor/Gust")]
    public async Task WhenAPathLeadsUpFromASubjectThatDoesNotTellItsParentGains_ThenTheRouteIsUnbounded(string path)
    {
        // Arrange
        await using var fixture = await RouteFixture.CreateAsync();

        // Act
        var (_, _, isUnbounded) = fixture.ResolveProperty(path);

        // Assert
        Assert.True(isUnbounded);
    }

    [Fact]
    public async Task WhenAPathLeadsUpFromARuleWithSeveralParents_ThenTheRouteHasItsHoldersAndIsBounded()
    {
        // Arrange
        await using var fixture = await RouteFixture.CreateAsync();
        var otherHost = new TestRuleHost();
        fixture.Host.Graph.Add("OtherHost", otherHost);
        otherHost.Rules = new Dictionary<string, AutomationRule> { ["copy"] = fixture.Rule };

        // Act
        var (property, route, isUnbounded) = fixture.ResolveProperty("../Enabled");

        // Assert
        Assert.Null(property);
        Assert.Equal(
            [new PropertyReference(fixture.Automation, nameof(Automation.Rules)), new PropertyReference(otherHost, nameof(TestRuleHost.Rules))],
            route);
        Assert.False(isUnbounded);
    }

    [Fact]
    public async Task WhenTheRoutesAreForNoAutomation_ThenAPathThatLeadsUpHasAnUnboundedRoute()
    {
        // Arrange
        await using var fixture = await RouteFixture.CreateAsync();
        var routes = new GraphRoutes(fixture.Host.Graph.Resolver, null, () => -1, null);

        // Act
        var route = routes.BeginRoute();
        AutomationPaths.ResolveProperty(fixture.Host.Graph.Resolver, "../Enabled", fixture.Rule, route);

        // Assert
        Assert.True(route.IsUnbounded);
    }

    [Fact]
    public async Task WhenTheLocationIsResolved_ThenTheRouteHasTheHoldersOfTheAutomationAndOfItsAncestors()
    {
        // Arrange
        await using var fixture = await RouteFixture.CreateAsync();

        // Act
        var path = fixture.Routes.ResolveLocation(fixture.Automation);

        // Assert
        Assert.Equal("/Folder/Automation", path);
        Assert.Equal([new PropertyReference(fixture.Folder, nameof(TestFolder.Children)), fixture.RootChildren], fixture.Routes.LocationRoute.Properties);
        Assert.False(fixture.Routes.LocationRoute.IsUnbounded);
    }

    [Fact]
    public async Task WhenTheStructureIsAsItWasAtTheLastPublication_ThenNothingNeedsResolving()
    {
        // Arrange
        await using var fixture = await RouteFixture.CreateAsync();
        var needsResolvingBefore = fixture.Routes.NeedsResolving();
        fixture.ResolveProperty("/Room/Sensor/Gust");
        fixture.Routes.Publish(0);
        var needsResolvingAfterThePathsAlone = fixture.Routes.NeedsResolving();
        fixture.Routes.ResolveLocation(fixture.Automation);
        fixture.ResolveProperty("/Room/Sensor/Gust");
        fixture.Routes.Publish(0);
        var needsResolvingAfterThePathsAndTheLocation = fixture.Routes.NeedsResolving();

        // Act
        fixture.Room.Sensor = new TestSensor();

        // Assert
        Assert.True(needsResolvingBefore);
        Assert.True(needsResolvingAfterThePathsAlone);
        Assert.False(needsResolvingAfterThePathsAndTheLocation);
        Assert.True(fixture.Routes.NeedsResolving());
    }

    [Fact]
    public async Task WhenThePathsAloneAreResolvedAgainAtTheSameStructure_ThenTheLocationStaysBound()
    {
        // Arrange
        await using var fixture = await RouteFixture.CreateAsync();
        fixture.Routes.ResolveLocation(fixture.Automation);
        fixture.ResolveProperty("/Room/Sensor/Gust");
        fixture.Routes.Publish(0);

        // Act
        fixture.ResolveProperty("/Room/Items[0]/Gust");
        fixture.Routes.Publish(0);

        // Assert
        Assert.False(fixture.Routes.NeedsResolving());
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

        public (RegisteredSubjectProperty? Property, PropertyReference[] Route, bool IsUnbounded) ResolveProperty(string path)
        {
            var route = Routes.BeginRoute();
            var property = AutomationPaths.ResolveProperty(Host.Graph.Resolver, path, Rule, route);
            return (property, route.Properties.ToArray(), route.IsUnbounded);
        }

        public ValueTask DisposeAsync() => Host.DisposeAsync();
    }
}
