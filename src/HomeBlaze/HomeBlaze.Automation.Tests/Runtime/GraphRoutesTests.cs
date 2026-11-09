using System.Collections.Immutable;
using HomeBlaze.Abstractions;
using HomeBlaze.Automation.Expressions;
using HomeBlaze.Automation.Runtime;
using HomeBlaze.Automation.Tests.Testing;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using static HomeBlaze.Automation.Tests.Testing.RuleBuilder;

namespace HomeBlaze.Automation.Tests.Runtime;

public class GraphRoutesTests
{
    // The root has no property Room, so the route also has the property that, added at run time, would take precedence
    // over the child of [InlinePaths], and the configuration of Gust, which would hide it once added as a secret, on the
    // room, which holds the sensor.
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
        Assert.Equal(
            [fixture.RoomName, fixture.RootChildren, new PropertyReference(fixture.Room, holder), new PropertyReference(fixture.Room, GustConfiguration)],
            route.Properties.ToArray());
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

    // The path reaches the folder through none of its parents, so the configuration of Level is on the folder.
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
            [
                new PropertyReference(fixture.Automation, nameof(Automation.Rules)),
                new PropertyReference(fixture.Folder, nameof(TestFolder.Children)),
                new PropertyReference(fixture.Folder, "Level@HomeBlaze:Configuration")
            ],
            route.Properties.ToArray());
        Assert.False(route.IsUnbounded);
    }

    // The configuration of Gust, which would hide it once added as a secret, is on the route on the room, which holds the
    // sensor.
    [Fact]
    public async Task WhenTheSubjectAPathEndsAtIsReplaced_ThenTheRouteStaysTheSameInstanceAndNothingIsPublished()
    {
        // Arrange
        await using var fixture = await RouteFixture.CreateAsync();
        var (_, route) = fixture.ResolveProperty("/Room/Sensor/Gust");
        fixture.Routes.Publish();
        fixture.Room.Sensor = new TestSensor();

        // Act
        var (property, routeAfterReplace) = fixture.ResolveProperty("/Room/Sensor/Gust");
        var hasPublished = fixture.Routes.Publish();

        // Assert
        Assert.Same(fixture.Room.Sensor, property?.Subject);
        Assert.Same(route, routeAfterReplace);
        Assert.False(hasPublished);
    }

    // Another automation resolves the path first, so that the resolver and the registry allocated their caches for the
    // replaced sensor, and the allocations of the same resolution without a route are subtracted; the smallest object
    // takes 24 bytes, so less than 8 bytes a resolution leaves room only for allocations of the runtime that happen once.
    [Fact]
    public async Task WhenTheSubjectAPathEndsAtIsReplaced_ThenRecordingTheSameRouteAgainAllocatesNothing()
    {
        // Arrange
        const int resolutions = 1000;
        await using var fixture = await RouteFixture.CreateAsync();
        var other = new GraphRoutes(fixture.Automation, changes: new AutomationChangeReceiver(new AutomationInputQueue()));
        TestSensor[] sensors = [new(), new()];
        for (var index = 0; index < 100; index++)
        {
            ReplaceAndResolve(fixture, other, sensors[index % 2]);
        }

        // Act
        var allocated = 0L;
        for (var index = 0; index < resolutions; index++)
        {
            allocated += ReplaceAndResolve(fixture, other, sensors[index % 2]);
        }

        // Assert
        Assert.True(allocated < resolutions * 8, $"{allocated} bytes for {resolutions} resolutions");
    }

    // As above, less than 8 bytes a resolution leaves room only for allocations of the runtime that happen once.
    [Theory]
    [InlineData("/Room/Sensor/Gust")]
    [InlineData("Name")]
    public async Task WhenTheSamePathIsResolvedAgain_ThenResolvingItsTargetAllocatesNothing(string path)
    {
        // Arrange
        const int resolutions = 1000;
        await using var fixture = await RouteFixture.CreateAsync();
        var resolver = fixture.Host.Graph.Resolver;
        Assert.NotNull(AutomationPaths.ResolveTarget(resolver, path, fixture.Rule));

        // Act
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < resolutions; index++)
        {
            AutomationPaths.ResolveTarget(resolver, path, fixture.Rule);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert
        Assert.True(allocated < resolutions * 8, $"{allocated} bytes for {resolutions} resolutions");
    }

    private static long ReplaceAndResolve(RouteFixture fixture, GraphRoutes other, TestSensor sensor)
    {
        const string path = "/Room/Sensor/Gust";
        var resolver = fixture.Host.Graph.Resolver;
        fixture.Room.Sensor = sensor;
        AutomationPaths.ResolveProperty(resolver, path, fixture.Rule, other.BeginRoute());

        var before = GC.GetAllocatedBytesForCurrentThread();
        AutomationPaths.ResolveProperty(resolver, path, fixture.Rule);
        var withoutRoute = GC.GetAllocatedBytesForCurrentThread() - before;

        before = GC.GetAllocatedBytesForCurrentThread();
        fixture.ResolveProperty(path);
        fixture.Routes.Publish();
        return GC.GetAllocatedBytesForCurrentThread() - before - withoutRoute;
    }

    // Removing the rule from the automation and putting it back is a write of the Rules property, which detaches and
    // attaches the subject the path starts at.
    [Theory]
    [InlineData("Name")]
    [InlineData("./Name")]
    public async Task WhenAPathStartsAtTheRule_ThenTheRouteHasTheHolderOfTheRuleAndIsBounded(string path)
    {
        // Arrange
        await using var fixture = await RouteFixture.CreateAsync();

        // Act
        var (property, route) = fixture.ResolveProperty(path);

        // Assert
        Assert.NotNull(property);
        Assert.Contains(new PropertyReference(fixture.Automation, nameof(Automation.Rules)), route.Properties.ToArray());
        Assert.False(route.IsUnbounded);
    }

    [Fact]
    public async Task WhenTheSubjectOfAnEventTriggerIsTheRule_ThenTheRouteHasTheHolderOfTheRuleAndIsBounded()
    {
        // Arrange
        await using var fixture = await RouteFixture.CreateAsync();
        var slot = new GraphRouteSlot();

        // Act
        var subject = AutomationPaths.ResolveSubject(fixture.Host.Graph.Resolver, ".", fixture.Rule, fixture.Routes.BeginRoute());
        fixture.Routes.EndRoute(slot);

        // Assert
        Assert.Same(fixture.Rule, subject);
        Assert.Equal([new PropertyReference(fixture.Automation, nameof(Automation.Rules))], slot.Route.Properties.ToArray());
        Assert.False(slot.Route.IsUnbounded);
    }

    [Fact]
    public async Task WhenAPathStartsAtARuleThatIsNotAttached_ThenTheRouteIsUnbounded()
    {
        // Arrange
        await using var fixture = await RouteFixture.CreateAsync();
        var detachedRule = On("Detached", When("/Weather/Gust > 8"), Invoke("/Blinds/Close"));
        var slot = new GraphRouteSlot();

        // Act
        AutomationPaths.ResolveSubject(fixture.Host.Graph.Resolver, ".", detachedRule, fixture.Routes.BeginRoute());
        fixture.Routes.EndRoute(slot);

        // Assert
        Assert.True(slot.Route.IsUnbounded);
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

    // The other host is no automation that tells the worker when the rule gains a parent.
    [Fact]
    public async Task WhenAPathLeadsUpFromARuleThatOnlyAnotherSubjectHolds_ThenTheRouteIsUnbounded()
    {
        // Arrange
        await using var fixture = await RouteFixture.CreateAsync();
        var otherHost = new TestRuleHost();
        var hostedRule = On("Hosted", When("/Weather/Gust > 8"), Invoke("/Blinds/Close"));
        fixture.Host.Graph.Add("OtherHost", otherHost);
        otherHost.Rules = new Dictionary<string, AutomationRule> { ["hosted"] = hostedRule };
        var slot = new GraphRouteSlot();

        // Act
        var property = AutomationPaths.ResolveProperty(fixture.Host.Graph.Resolver, "../Rules", hostedRule, fixture.Routes.BeginRoute());
        fixture.Routes.EndRoute(slot);

        // Assert
        Assert.NotNull(property);
        Assert.Contains(new PropertyReference(otherHost, nameof(TestRuleHost.Rules)), slot.Route.Properties.ToArray());
        Assert.True(slot.Route.IsUnbounded);
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

    // Detaching an ancestor of one of the folders gives the automation a single parent without a change of a holder.
    [Fact]
    public async Task WhenAPathLeadsUpFromAnAutomationWithSeveralParents_ThenTheRouteIsUnbounded()
    {
        // Arrange
        await using var fixture = await RouteFixture.CreateAsync();
        var other = new TestFolder();
        fixture.Host.Graph.Add("Other", other);
        other.Add("Automation", fixture.Automation);

        // Act
        var (property, route) = fixture.ResolveProperty("../../Level");

        // Assert
        Assert.Null(property);
        Assert.Contains(new PropertyReference(other, nameof(TestFolder.Children)), route.Properties.ToArray());
        Assert.True(route.IsUnbounded);
    }

    // A getter that fails may succeed later without a change, so a path whose resolution fails depends on any change.
    [Fact]
    public async Task WhenTheResolutionOfAPathFails_ThenTheRouteIsUnbounded()
    {
        // Arrange
        await using var fixture = await RouteFixture.CreateAsync();
        var fails = false;
        ((IInterceptorSubject)fixture.Room).TryGetRegisteredSubject()!.AddProperty(
            "Broken", typeof(TestSensor), _ => fails ? throw new InvalidOperationException("The getter fails.") : null, null);
        fails = true;

        // Act
        var (property, route) = fixture.ResolveProperty("/Room/Broken/Gust");

        // Assert
        Assert.Null(property);
        Assert.True(route.IsUnbounded);
    }

    [Fact]
    public void WhenARouteBecomesUnboundedWithTheSameProperties_ThenTheBuilderGivesANewRoute()
    {
        // Arrange
        var builder = new GraphRouteBuilder(automation: new TestSensor());
        var property = new PropertyReference(new TestSensor(), nameof(TestSensor.Gust));
        builder.AddProperty(property);
        var bounded = builder.ToRoute(GraphRoute.Empty);

        // Act
        builder.Clear();
        builder.AddProperty(property);
        builder.MarkUnbounded();
        var unbounded = builder.ToRoute(bounded);

        // Assert
        Assert.False(bounded.IsUnbounded);
        Assert.True(unbounded.IsUnbounded);
        Assert.Equal([property], unbounded.Properties.ToArray());
    }

    // The other slot keeps the property in the union, so only the unbounded count tells that the union changed.
    [Fact]
    public async Task WhenAStoredRouteBecomesUnboundedWithPropertiesThatStayInTheUnion_ThenTheUnboundedUnionIsPublished()
    {
        // Arrange
        await using var fixture = await RouteFixture.CreateAsync();
        var dispatcher = new AutomationChangeDispatcher();
        var changes = new AutomationChangeReceiver(new AutomationInputQueue());
        using var registration = changes.Register(dispatcher, fixture.Host.Graph.Context);
        var routes = new GraphRoutes(fixture.Automation, changes);
        var first = new GraphRouteSlot();
        var second = new GraphRouteSlot();
        var properties = ImmutableArray.Create(new PropertyReference(fixture.Room, nameof(TestRoom.Sensor)));
        first.Store(routes, new GraphRoute(properties, isUnbounded: false));
        second.Store(routes, new GraphRoute(properties, isUnbounded: false));
        routes.Publish();
        var isUnboundedBefore = changes.Registration!.IsUnbounded;

        // Act
        first.Store(routes, new GraphRoute(properties, isUnbounded: true));
        var hasPublished = routes.Publish();

        // Assert
        Assert.False(isUnboundedBefore);
        Assert.True(hasPublished);
        Assert.True(changes.Registration.IsUnbounded);
    }

    [Fact]
    public async Task WhenNoResolutionIsStored_ThenTheFirstPublicationPublishesAnEmptyBoundedRoute()
    {
        // Arrange
        await using var fixture = await RouteFixture.CreateAsync();
        var dispatcher = new AutomationChangeDispatcher();
        var changes = new AutomationChangeReceiver(new AutomationInputQueue());
        using var registration = changes.Register(dispatcher, fixture.Host.Graph.Context);
        var routes = new GraphRoutes(fixture.Automation, changes);
        var isUnboundedBefore = changes.Registration!.IsUnbounded;

        // Act
        var hasPublished = routes.Publish();

        // Assert
        Assert.True(isUnboundedBefore);
        Assert.True(hasPublished);
        Assert.False(changes.Registration.IsUnbounded);
        Assert.Empty(changes.Registration.Route);
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
    public async Task WhenRoutesAreStoredAndCleared_ThenTheUnionHasThePropertiesOfTheStoredRoutes()
    {
        // Arrange
        await using var fixture = await RouteFixture.CreateAsync();
        var routes = fixture.Routes;
        var sensor = new GraphRouteSlot();
        var items = new GraphRouteSlot();
        var sensorHolder = new PropertyReference(fixture.Room, nameof(TestRoom.Sensor));
        var itemsHolder = new PropertyReference(fixture.Room, nameof(TestRoom.Items));

        // Act
        Resolve(fixture, "/Room/Sensor/Gust", sensor);
        Resolve(fixture, "/Room/Items[0]/Gust", items);
        var unionOfBoth = routes.Union.ToHashSet();
        items.Clear();
        var unionAfterClear = routes.Union.ToHashSet();
        Resolve(fixture, "../../../Level", sensor);
        var isUnboundedAfterTheStepAbove = routes.IsUnbounded;
        sensor.Clear();

        // Assert
        Assert.Contains(sensorHolder, unionOfBoth);
        Assert.Contains(itemsHolder, unionOfBoth);
        Assert.Contains(fixture.RootChildren, unionAfterClear);
        Assert.Contains(sensorHolder, unionAfterClear);
        Assert.DoesNotContain(itemsHolder, unionAfterClear);
        Assert.True(isUnboundedAfterTheStepAbove);
        Assert.Empty(routes.Union);
        Assert.False(routes.IsUnbounded);
    }

    [Fact]
    public async Task WhenASlotStoresTheRouteOfAnotherResolution_ThenTheRouteItHeldLeavesTheUnion()
    {
        // Arrange
        await using var fixture = await RouteFixture.CreateAsync();
        var slot = new GraphRouteSlot();
        Resolve(fixture, "/Room/Sensor/Gust", slot);

        // Act
        Resolve(fixture, "/Room/Items[0]/Gust", slot);

        // Assert
        Assert.Contains(new PropertyReference(fixture.Room, nameof(TestRoom.Items)), fixture.Routes.Union);
        Assert.DoesNotContain(new PropertyReference(fixture.Room, nameof(TestRoom.Sensor)), fixture.Routes.Union);
    }

    [Fact]
    public async Task WhenAResolutionBegins_ThenTheAutomationReceivesEveryGraphChangeUntilThePublication()
    {
        // Arrange
        await using var fixture = await RouteFixture.CreateAsync();
        var dispatcher = new AutomationChangeDispatcher();
        var changes = new AutomationChangeReceiver(new AutomationInputQueue());
        using var registration = changes.Register(dispatcher, fixture.Host.Graph.Context);
        var routes = new GraphRoutes(fixture.Automation, changes);
        var slot = new GraphRouteSlot();
        routes.Publish();
        var isResolvingBefore = changes.Registration!.IsResolving;

        // Act
        AutomationPaths.ResolveProperty(fixture.Host.Graph.Resolver, "/Room/Sensor/Gust", fixture.Rule, routes.BeginRoute());
        routes.EndRoute(slot);
        var isResolvingWhileResolving = changes.Registration.IsResolving;
        var hasPublished = routes.Publish();
        var isResolvingAfterThePublication = changes.Registration.IsResolving;
        AutomationPaths.ResolveProperty(fixture.Host.Graph.Resolver, "/Room/Sensor/Gust", fixture.Rule, routes.BeginRoute());
        routes.EndRoute(slot);
        var hasPublishedTheSameUnion = routes.Publish();

        // Assert
        Assert.False(isResolvingBefore);
        Assert.True(isResolvingWhileResolving);
        Assert.True(hasPublished);
        Assert.False(isResolvingAfterThePublication);
        Assert.False(hasPublishedTheSameUnion);
        Assert.False(changes.Registration.IsResolving);
    }

    private const string GustConfiguration = "Gust@HomeBlaze:Configuration";

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
            Routes = new GraphRoutes(Automation, changes: new AutomationChangeReceiver(new AutomationInputQueue()));
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
