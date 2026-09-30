using HomeBlaze.History.Abstractions;
using HomeBlaze.Services;
using HomeBlaze.Services.Lifecycle;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.History.InMemory.Tests;

/// <summary>
/// Drives <see cref="InMemoryHistoryStoreSubject"/> against a real graph wired with the real
/// <see cref="SubjectPathResolver"/>, mutates [State] properties, and asserts that changes are
/// recorded under their canonical property paths.
/// </summary>
public class InMemoryHistoryStoreRecordingTests
{
    private static (IInterceptorSubjectContext Context, TestRoot Root, RootManager RootManager) CreateGraph()
    {
        var typeProvider = new TypeProvider();
        var typeRegistry = new SubjectTypeRegistry(typeProvider);

        var services = new ServiceCollection();
        var serviceProvider = services.BuildServiceProvider();
        var serializer = new ConfigurableSubjectSerializer(typeProvider, serviceProvider);

        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        // The resolver reads the root lazily, so it can be built before the manager that owns it,
        // and RootManager registers it into the context.
        RootManager? rootManager = null;
        var pathResolver = new SubjectPathResolver(() => rootManager!.Root);
        rootManager = new RootManager(typeRegistry, serializer, context, pathResolver, null);
        context.WithService(() => rootManager);

        // PropertyAttributeInitializer turns the C# [State] attribute into the KnownAttributes.State
        // registry attribute that HasHistory() checks. Without it, HasHistory() is always false.
        context.WithService<IPropertyLifecycleHandler>(
            () => new PropertyAttributeInitializer(),
            handler => handler is PropertyAttributeInitializer);

        var root = new TestRoot(context);

        // RootManager.Root has an internal setter (accessible only to HomeBlaze.Services.Tests); set it
        // through reflection so the resolver treats TestRoot as the canonical root ("/").
        typeof(RootManager).GetProperty(nameof(RootManager.Root))!
            .SetValue(rootManager, root);

        return (context, root, rootManager);
    }

    private static InMemoryHistoryStoreSubject CreateStore(IInterceptorSubjectContext sharedContext)
    {
        var store = new InMemoryHistoryStoreSubject(NullLogger<InMemoryHistoryStoreSubject>.Instance);

        // Share the graph: the store's ChangeQueueProcessor subscription and path resolver are
        // resolved through this fallback, so it observes the whole graph (like an attached subject).
        ((IInterceptorSubject)store).Context.AddFallbackContext(sharedContext);

        return store;
    }

    /// <summary>
    /// Mutates <paramref name="propertyPath"/> to <paramref name="targetValue"/> and waits until a
    /// point with exactly that value is recorded under the canonical path. A warm-up phase re-applies
    /// a distinct sentinel value until any point appears, which waits without a fixed sleep until the
    /// store's execution has built the engine (queries return an empty series before that). It then
    /// applies the target value and polls until it lands.
    /// </summary>
    private static async Task<HistorySeries> RecordAndWaitForValueAsync(
        InMemoryHistoryStoreSubject store, string propertyPath, Action<double> mutate, double targetValue)
    {
        // Warm-up: wait until the execution has built the engine and it is recording. Each iteration
        // uses a distinct negative value so the equality check never drops it as a no-op repeat. Driving
        // a new mutation on every poll is why this cannot be a plain WaitUntilAsync over a static condition;
        // the wait itself is expressed via WaitUntilAsync over the "any point landed" condition.
        var warmupValue = -1000.0;
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                warmupValue -= 1.0;
                mutate(warmupValue);
                var warmup = QuerySeries(store, propertyPath);
                return warmup.Points.Length > 0;
            },
            message: $"Store never started recording under '{propertyPath}'.");

        // Now the engine is recording; apply the asserted value and wait for it specifically.
        mutate(targetValue);
        await AsyncTestHelpers.WaitUntilAsync(
            () => QuerySeries(store, propertyPath).Points.Any(point => point.Number == targetValue),
            message: $"Value {targetValue} not recorded under '{propertyPath}'.");

        return QuerySeries(store, propertyPath);
    }

    private static HistorySeries QuerySeries(InMemoryHistoryStoreSubject store, string propertyPath) =>
        store.QueryAsync(
            new HistoryQuery(propertyPath, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1)),
            CancellationToken.None).GetAwaiter().GetResult();

    [Fact]
    public void WhenConstructed_ThenTitleIsRenderedForInMemoryHistory()
    {
        // Arrange
        var store = new InMemoryHistoryStoreSubject(NullLogger<InMemoryHistoryStoreSubject>.Instance);

        // Act
        var title = store.Title;

        // Assert
        Assert.Equal("In-Memory History", title);
    }

    [Fact]
    public async Task WhenRootStatePropertyMutated_ThenRecordedUnderCanonicalPath()
    {
        // Arrange
        var (context, root, _) = CreateGraph();
        var store = CreateStore(context);
        var hostedService = (IHostedService)store;
        await hostedService.StartAsync(CancellationToken.None);
        try
        {
            // Act
            var series = await RecordAndWaitForValueAsync(store, "/Temperature", value => root.Temperature = value, 21.5);

            // Assert
            Assert.Contains(series.Points, point => point.Number == 21.5);
        }
        finally
        {
            await hostedService.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenAPropertyIsWrittenRightAfterStartAsync_ThenItIsRecorded()
    {
        // Arrange: the write happens right after the host start returns, typically before the execution has run.
        var (context, root, _) = CreateGraph();
        var store = CreateStore(context);
        var hostedService = (IHostedService)store;

        // Act
        await hostedService.StartAsync(CancellationToken.None);
        root.Temperature = 21.5;

        // Assert
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(
                () => QuerySeries(store, "/Temperature").Points.Any(point => point.Number == 21.5),
                message: "Value written right after StartAsync was not recorded.");
        }
        finally
        {
            await hostedService.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenChildStatePropertyMutated_ThenRecordedUnderChildCanonicalPath()
    {
        // Arrange
        var (context, root, _) = CreateGraph();
        var child = new TestChild(context);
        root.Child = child;

        var store = CreateStore(context);
        var hostedService = (IHostedService)store;
        await hostedService.StartAsync(CancellationToken.None);
        try
        {
            // Act
            var series = await RecordAndWaitForValueAsync(store, "/Child/Pressure", value => child.Pressure = value, 3.3);

            // Assert
            Assert.Contains(series.Points, point => point.Number == 3.3);
        }
        finally
        {
            await hostedService.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenChildReparentedToNewSlot_ThenPreMoveAndPostMoveSamplesQueryableUnderNewPath()
    {
        // Arrange
        var (context, root, _) = CreateGraph();
        var child = new TestChild(context);
        root.Child = child;

        var store = CreateStore(context);
        var hostedService = (IHostedService)store;
        await hostedService.StartAsync(CancellationToken.None);
        try
        {
            // Act
            // 1. Record a pre-move sample at the child's original canonical path (/Child/Pressure). This
            //    also seeds the store's move-detection cache with the subject's old path.
            var beforeMove = await RecordAndWaitForValueAsync(
                store, "/Child/Pressure", value => child.Pressure = value, 3.3);
            Assert.Contains(beforeMove.Points, point => point.Number == 3.3);

            // 2. Reparent the same child subject so its canonical path changes from /Child to /SecondChild.
            //    Add the new reference FIRST (child keeps at least one parent throughout, so no context
            //    detach fires and the move-detection cache entry survives), then clear the old slot so the
            //    resolver returns the new path. Reassigning references fires the lifecycle events that clear
            //    the resolver's path cache, so the next change resolves the new canonical path.
            root.SecondChild = child;
            root.Child = null;

            // 3. Record a post-move sample. The store resolves the new path (/SecondChild/Pressure), sees it
            //    differ from the cached old path, and records a move leg /Child/Pressure -> /SecondChild/Pressure.
            var afterMove = await RecordAndWaitForValueAsync(
                store, "/SecondChild/Pressure", value => child.Pressure = value, 7.7);

            // Assert
            // Querying the new path must return BOTH samples: the post-move one directly, and the pre-move
            // one resolved across the recorded move chain. If the move leg were not recorded, the pre-move
            // sample would remain orphaned under /Child/Pressure and be absent here.
            var series = await store.QueryAsync(
                new HistoryQuery("/SecondChild/Pressure", DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1)),
                CancellationToken.None);

            Assert.Contains(series.Points, point => point.Number == 3.3);
            Assert.Contains(series.Points, point => point.Number == 7.7);
            Assert.Equal(afterMove.PropertyPath, series.PropertyPath);
        }
        finally
        {
            await hostedService.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenChildWithTwoHistoryPropertiesReparented_ThenSiblingPropertyKeepsPreMoveHistory()
    {
        // Arrange
        var (context, root, _) = CreateGraph();
        var child = new TestChild(context);
        root.Child = child;

        var store = CreateStore(context);
        var hostedService = (IHostedService)store;
        await hostedService.StartAsync(CancellationToken.None);
        try
        {
            // Act
            // 1. Record a pre-move sample for BOTH history properties at the child's original path
            //    (/Child/...). This seeds the move-detection cache with the old path for each property.
            var pressureBefore = await RecordAndWaitForValueAsync(
                store, "/Child/Pressure", value => child.Pressure = value, 3.3);
            Assert.Contains(pressureBefore.Points, point => point.Number == 3.3);
            var humidityBefore = await RecordAndWaitForValueAsync(
                store, "/Child/Humidity", value => child.Humidity = value, 4.4);
            Assert.Contains(humidityBefore.Points, point => point.Number == 4.4);

            // 2. Reparent the same child subject so its canonical path changes from /Child to /SecondChild.
            //    Add the new reference first (the child keeps a parent throughout, so no context detach
            //    clears the cache), then clear the old slot so the resolver returns the new path.
            root.SecondChild = child;
            root.Child = null;

            // 3. Record a post-move sample for Pressure FIRST. This is the property that consumes the
            //    rename when moves are tracked per subject, which is exactly what must not starve Humidity.
            var pressureAfter = await RecordAndWaitForValueAsync(
                store, "/SecondChild/Pressure", value => child.Pressure = value, 7.7);
            Assert.Contains(pressureAfter.Points, point => point.Number == 7.7);

            // 4. Then record a post-move sample for the sibling Humidity. Per-property move tracking must
            //    independently detect the rename for Humidity and record its /Child -> /SecondChild leg.
            var humidityAfter = await RecordAndWaitForValueAsync(
                store, "/SecondChild/Humidity", value => child.Humidity = value, 8.8);
            Assert.Contains(humidityAfter.Points, point => point.Number == 8.8);

            // Assert
            // Querying the new Humidity path must return BOTH the pre-move (4.4) and post-move (8.8)
            // samples. With per-subject move tracking, Pressure consumes the rename and Humidity's move
            // leg is never recorded, so its pre-move sample stays orphaned under /Child/Humidity.
            var series = await store.QueryAsync(
                new HistoryQuery("/SecondChild/Humidity", DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1)),
                CancellationToken.None);

            Assert.Contains(series.Points, point => point.Number == 4.4);
            Assert.Contains(series.Points, point => point.Number == 8.8);
        }
        finally
        {
            await hostedService.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenRunning_ThenCoverageToAdvancesAndPriorityIsHundred()
    {
        // Arrange
        var (context, root, _) = CreateGraph();
        var store = CreateStore(context);
        var hostedService = (IHostedService)store;
        await hostedService.StartAsync(CancellationToken.None);
        try
        {
            // Act
            var beforeRecording = DateTimeOffset.UtcNow;
            var series = await RecordAndWaitForValueAsync(store, "/Temperature", value => root.Temperature = value, 12.5);

            // Assert
            Assert.NotEmpty(series.Points);
            Assert.Equal(100, store.Priority);

            // Against a mark taken before the write, not a minute-wide window around "now", which
            // the coverage end would have to be badly stale to fall outside of.
            Assert.True(
                Assert.Single(store.CoverageRanges).To >= beforeRecording,
                "coverage did not advance past the instant before the recorded change");
        }
        finally
        {
            await hostedService.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenMaxPointsPerPropertyChangesAndConfigurationIsApplied_ThenNewSamplesUseTheNewCapacity()
    {
        // Arrange
        var (context, root, _) = CreateGraph();
        var store = CreateStore(context);
        var hostedService = (IHostedService)store;
        await hostedService.StartAsync(CancellationToken.None);
        try
        {
            await RecordAndWaitForValueAsync(store, "/Temperature", value => root.Temperature = value, 1);

            // Act
            store.MaxPointsPerProperty = 2;
            var appliedAt = DateTimeOffset.UtcNow;
            await store.ApplyConfigurationAsync(CancellationToken.None);
            await WaitForCoverageSessionStartedAtOrAfterAsync(store, appliedAt);

            for (var target = 1; target <= 5; target++)
            {
                await RecordAndWaitForValueAsync(store, "/Temperature", value => root.Temperature = value, target);
            }

            // Assert
            var series = QuerySeries(store, "/Temperature");
            Assert.InRange(series.Points.Length, 1, 2);
            Assert.Equal(5, series.Points[^1].Number);
        }
        finally
        {
            await hostedService.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenIsEnabledIsClearedAndApplied_ThenStatusBecomesDisabled()
    {
        // Arrange
        var (context, root, _) = CreateGraph();
        var store = CreateStore(context);
        var hostedService = (IHostedService)store;
        await hostedService.StartAsync(CancellationToken.None);
        try
        {
            await RecordAndWaitForValueAsync(store, "/Temperature", value => root.Temperature = value, 1);

            // Act
            store.IsEnabled = false;
            await store.ApplyConfigurationAsync(CancellationToken.None);
            await AsyncTestHelpers.WaitUntilAsync(
                () => store.Status == "Disabled",
                message: "Store never reported Disabled after IsEnabled was cleared and applied.");

            // Assert
            Assert.Equal("Disabled", store.Status);
            Assert.Empty(store.CoverageRanges);
            Assert.Empty(QuerySeries(store, "/Temperature").Points);
            Assert.Equal(0, store.RecordedCount);

            store.IsEnabled = true;
            await store.ApplyConfigurationAsync(CancellationToken.None);
            await AsyncTestHelpers.WaitUntilAsync(
                () => store.Status == "Running",
                message: "Store never reported Running after IsEnabled was set again and applied.");

            var series = await RecordAndWaitForValueAsync(store, "/Temperature", value => root.Temperature = value, 42);
            Assert.Contains(series.Points, point => point.Number == 42);
        }
        finally
        {
            await hostedService.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenOnlyPriorityChanges_ThenRecordedSamplesAreKept()
    {
        // Arrange
        var (context, root, _) = CreateGraph();
        var store = CreateStore(context);
        var hostedService = (IHostedService)store;
        await hostedService.StartAsync(CancellationToken.None);
        try
        {
            await RecordAndWaitForValueAsync(store, "/Temperature", value => root.Temperature = value, 11);
            var coverageFrom = Assert.Single(store.CoverageRanges).From;

            // Act
            store.Priority = 7;
            await store.ApplyConfigurationAsync(CancellationToken.None);

            // Assert (before a restart could complete)
            Assert.Equal(coverageFrom, Assert.Single(store.CoverageRanges).From);
            Assert.Contains(QuerySeries(store, "/Temperature").Points, point => point.Number == 11);

            await RecordAndWaitForValueAsync(store, "/Temperature", value => root.Temperature = value, 22);

            // Assert
            var series = QuerySeries(store, "/Temperature");
            Assert.Contains(series.Points, point => point.Number == 11);
            Assert.Contains(series.Points, point => point.Number == 22);
            Assert.Equal(coverageFrom, Assert.Single(store.CoverageRanges).From);
        }
        finally
        {
            await hostedService.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Waits until the store reports a coverage session that began at or after <paramref name="instant"/>,
    /// which is how a restart shows: the new engine starts its session once its subscription is live.
    /// </summary>
    private static Task WaitForCoverageSessionStartedAtOrAfterAsync(InMemoryHistoryStoreSubject store, DateTimeOffset instant) =>
        AsyncTestHelpers.WaitUntilAsync(
            () => store.CoverageRanges is [var coverage] && coverage.From >= instant,
            message: "Store never began a new coverage session after the configuration was applied.");
}
