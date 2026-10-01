using System.Threading.Channels;
using Namotion.Interceptor.Connectors;
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
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.History.Sqlite.Tests;

/// <summary>
/// Drives <see cref="SqliteHistoryStoreSubject"/> against a real graph wired with the real
/// <see cref="SubjectPathResolver"/>, mutates [State] properties, and asserts that changes are
/// recorded under their canonical property paths and persisted to the SQLite partition files.
/// Because the store flushes on an interval, the harness forces a flush through the internal
/// <see cref="SqliteHistoryStoreSubject.FlushNowAsync"/> test hook before each query, so the wait is
/// deterministic rather than timing-dependent.
/// </summary>
public class SqliteHistoryStoreRecordingTests
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

    private static (SqliteHistoryStoreSubject Store, string DatabasePath) CreateStore(IInterceptorSubjectContext sharedContext)
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "hb-sqlite-hist-" + Guid.NewGuid().ToString("N"));

        var store = new SqliteHistoryStoreSubject(NullLogger<SqliteHistoryStoreSubject>.Instance)
        {
            DatabasePath = databasePath,
            // A short buffer time keeps the change-queue batch small so the warm-up loop converges quickly.
            BufferTimeMilliseconds = 50,
            FlushIntervalSeconds = 1
        };

        // Share the graph: the store's ChangeQueueProcessor subscription and path resolver are
        // resolved through this fallback, so it observes the whole graph (like an attached subject).
        ((IInterceptorSubject)store).Context.AddFallbackContext(sharedContext);

        return (store, databasePath);
    }

    /// <summary>
    /// Mutates <paramref name="propertyPath"/> to <paramref name="targetValue"/> and waits until a
    /// point with exactly that value is persisted under the canonical path. A warm-up phase re-applies
    /// a distinct sentinel value until any point appears, which waits without a fixed sleep until the
    /// store's execution has built the engine (queries return an empty series before that). It then
    /// applies the target value and polls until it lands. Every poll forces a flush through the internal
    /// test hook so queued samples become queryable immediately instead of waiting for the interval flush.
    /// </summary>
    private static async Task<HistorySeries> RecordAndWaitForValueAsync(
        SqliteHistoryStoreSubject store, string propertyPath, Action<double> mutate, double targetValue)
    {
        var warmupValue = -1000.0;
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                warmupValue -= 1.0;
                mutate(warmupValue);
                store.FlushNowAsync().GetAwaiter().GetResult();
                var warmup = QuerySeries(store, propertyPath);
                return warmup.Points.Length > 0;
            },
            message: $"Store never started recording under '{propertyPath}'.");

        // Now the engine is recording; apply the asserted value and wait for it specifically.
        mutate(targetValue);
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                store.FlushNowAsync().GetAwaiter().GetResult();
                return QuerySeries(store, propertyPath).Points.Any(point => point.Number == targetValue);
            },
            message: $"Value {targetValue} not recorded under '{propertyPath}'.");

        return QuerySeries(store, propertyPath);
    }

    private static HistorySeries QuerySeries(SqliteHistoryStoreSubject store, string propertyPath) =>
        store.QueryAsync(
            new HistoryQuery(propertyPath, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1)),
            CancellationToken.None).GetAwaiter().GetResult();

    private static void DeleteDirectory(string databasePath)
    {
        try
        {
            if (Directory.Exists(databasePath))
            {
                Directory.Delete(databasePath, recursive: true);
            }
        }
        catch
        {
            // best effort temp cleanup
        }
    }

    [Fact]
    public void WhenConstructed_ThenTitleIsRenderedForSQLiteHistory()
    {
        // Arrange
        var store = new SqliteHistoryStoreSubject(NullLogger<SqliteHistoryStoreSubject>.Instance);

        // Act
        var title = store.Title;

        // Assert
        Assert.Equal("SQLite History", title);
    }

    [Fact]
    public async Task WhenRootStatePropertyMutated_ThenRecordedUnderCanonicalPath()
    {
        // Arrange
        var (context, root, _) = CreateGraph();
        var (store, databasePath) = CreateStore(context);
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
            DeleteDirectory(databasePath);
        }
    }

    [Fact]
    public async Task WhenChildStatePropertyMutated_ThenRecordedUnderChildCanonicalPath()
    {
        // Arrange
        var (context, root, _) = CreateGraph();
        var child = new TestChild(context);
        root.Child = child;

        var (store, databasePath) = CreateStore(context);
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
            DeleteDirectory(databasePath);
        }
    }

    [Fact]
    public async Task WhenChildReparentedToNewSlot_ThenPreMoveAndPostMoveSamplesQueryableUnderNewPath()
    {
        // Arrange
        var (context, root, _) = CreateGraph();
        var child = new TestChild(context);
        root.Child = child;

        var (store, databasePath) = CreateStore(context);
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
            await store.FlushNowAsync();
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
            DeleteDirectory(databasePath);
        }
    }

    [Fact]
    public async Task WhenChildWithTwoHistoryPropertiesReparented_ThenSiblingPropertyKeepsPreMoveHistory()
    {
        // Arrange
        var (context, root, _) = CreateGraph();
        var child = new TestChild(context);
        root.Child = child;

        var (store, databasePath) = CreateStore(context);
        var hostedService = (IHostedService)store;
        await hostedService.StartAsync(CancellationToken.None);
        try
        {
            await RecordAndWaitForValueAsync(
                store, "/Child/Pressure", value => child.Pressure = value, 3.3);
            await RecordAndWaitForValueAsync(
                store, "/Child/Humidity", value => child.Humidity = value, 4.4);

            root.SecondChild = child;
            root.Child = null;

            await RecordAndWaitForValueAsync(
                store, "/SecondChild/Pressure", value => child.Pressure = value, 7.7);
            await RecordAndWaitForValueAsync(
                store, "/SecondChild/Humidity", value => child.Humidity = value, 8.8);

            // Act
            await store.FlushNowAsync();
            var series = await store.QueryAsync(
                new HistoryQuery(
                    "/SecondChild/Humidity",
                    DateTimeOffset.UtcNow.AddMinutes(-1),
                    DateTimeOffset.UtcNow.AddMinutes(1)),
                CancellationToken.None);

            // Assert
            Assert.Contains(series.Points, point => point.Number == 4.4);
            Assert.Contains(series.Points, point => point.Number == 8.8);
        }
        finally
        {
            await hostedService.StopAsync(CancellationToken.None);
            DeleteDirectory(databasePath);
        }
    }

    [Fact]
    public async Task WhenRunning_ThenCoverageToAdvancesAndPriorityIsFifty()
    {
        // Arrange
        var (context, root, _) = CreateGraph();
        var (store, databasePath) = CreateStore(context);
        var hostedService = (IHostedService)store;
        await hostedService.StartAsync(CancellationToken.None);
        try
        {
            // Act
            var beforeRecording = DateTimeOffset.UtcNow;
            var series = await RecordAndWaitForValueAsync(store, "/Temperature", value => root.Temperature = value, 12.5);

            // Assert
            Assert.NotEmpty(series.Points);
            Assert.Equal(50, store.Priority);

            // Against a mark taken before the write, not a minute-wide window around "now", which
            // the coverage end would have to be badly stale to fall outside of.
            Assert.True(
                Assert.Single(store.CoverageRanges).To >= beforeRecording,
                "coverage did not advance past the instant before the recorded change");
        }
        finally
        {
            await hostedService.StopAsync(CancellationToken.None);
            DeleteDirectory(databasePath);
        }
    }

    [Fact]
    public async Task WhenDatabasePathChangesAndConfigurationIsApplied_ThenNewSamplesGoToTheNewDirectory()
    {
        // Arrange
        var (context, root, _) = CreateGraph();
        var (store, databasePath) = CreateStore(context);
        var newDatabasePath = Path.Combine(Path.GetTempPath(), "hb-sqlite-hist-" + Guid.NewGuid().ToString("N"));
        var hostedService = (IHostedService)store;
        await hostedService.StartAsync(CancellationToken.None);
        try
        {
            await RecordAndWaitForValueAsync(store, "/Temperature", value => root.Temperature = value, 1);

            // Act
            store.DatabasePath = newDatabasePath;
            await store.ApplyConfigurationAsync(CancellationToken.None);
            await AsyncTestHelpers.WaitUntilAsync(
                () => Directory.Exists(newDatabasePath) && Directory.EnumerateFiles(newDatabasePath).Any(),
                message: "Store never opened the new database directory after the configuration was applied.");

            await RecordAndWaitForValueAsync(store, "/Temperature", value => root.Temperature = value, 2);
            await store.FlushNowAsync();

            // Assert
            Assert.Contains(
                Directory.EnumerateFiles(newDatabasePath, "*.db"),
                file => Path.GetFileName(file) != "metadata.db");
            var series = QuerySeries(store, "/Temperature");
            Assert.Contains(series.Points, point => point.Number == 2);
            Assert.DoesNotContain(series.Points, point => point.Number == 1);
        }
        finally
        {
            await hostedService.StopAsync(CancellationToken.None);
            DeleteDirectory(databasePath);
            DeleteDirectory(newDatabasePath);
        }
    }

    [Fact]
    public async Task WhenRestarted_ThenSamplesBeforeTheRestartArePersisted()
    {
        // Arrange
        var (context, root, _) = CreateGraph();
        var (store, databasePath) = CreateStore(context);

        // Long enough that no periodic flush persists value 1 before the restart does.
        store.FlushIntervalSeconds = (int)TimeSpan.FromDays(1).TotalSeconds;

        var hostedService = (IHostedService)store;
        await hostedService.StartAsync(CancellationToken.None);
        try
        {
            await RecordAndWaitForValueAsync(store, "/Temperature", value => root.Temperature = value, 0);
            root.Temperature = 1;
            await AsyncTestHelpers.WaitUntilAsync(
                () => store.PendingSampleCount > 0,
                message: "Value 1 never reached the engine.");

            // Act
            var appliedAt = DateTimeOffset.UtcNow;
            store.MaxJsonSize += 1;
            await store.ApplyConfigurationAsync(CancellationToken.None);

            // No forced flush while waiting: before the restart it would persist value 1 itself.
            await WaitForCoverageToReachAsync(store, appliedAt);

            // Assert: polled, since the ending session may still be the one serving when its final flush shows
            // and the next one takes a moment to open the files.
            await AsyncTestHelpers.WaitUntilAsync(
                () => QuerySeries(store, "/Temperature").Points.Any(point => point.Number == 1),
                message: "Value 1 was not persisted by the restart.");
        }
        finally
        {
            await hostedService.StopAsync(CancellationToken.None);
            DeleteDirectory(databasePath);
        }
    }

    [Fact]
    public async Task WhenRestarted_ThenCoverageContinuesAcrossTheRestart()
    {
        // Arrange: the subscription spans the restart and the files keep the earlier samples, so nothing is
        // missing between the two sessions.
        var (context, root, _) = CreateGraph();
        var (store, databasePath) = CreateStore(context);
        var hostedService = (IHostedService)store;
        await hostedService.StartAsync(CancellationToken.None);
        try
        {
            await RecordAndWaitForValueAsync(store, "/Temperature", value => root.Temperature = value, 1);
            var coverageFrom = Assert.Single(store.CoverageRanges).From;

            // Act
            var appliedAt = DateTimeOffset.UtcNow;
            store.MaxJsonSize += 1;
            await store.ApplyConfigurationAsync(CancellationToken.None);
            await WaitForCoverageToReachAsync(store, appliedAt);
            await RecordAndWaitForValueAsync(store, "/Temperature", value => root.Temperature = value, 2);

            // Assert
            var coverage = Assert.Single(store.CoverageRanges);
            Assert.Equal(coverageFrom, coverage.From);
            Assert.Contains(QuerySeries(store, "/Temperature").Points, point => point.Number == 1);
        }
        finally
        {
            await hostedService.StopAsync(CancellationToken.None);
            DeleteDirectory(databasePath);
        }
    }

    [Fact]
    public async Task WhenIsEnabledIsClearedAndApplied_ThenTheStoreServesNoHistory()
    {
        // Arrange
        var (context, root, _) = CreateGraph();
        var (store, databasePath) = CreateStore(context);
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
            Assert.Empty(store.CoverageRanges);
            Assert.Empty(QuerySeries(store, "/Temperature").Points);
            Assert.Equal(0, store.RecordedCount);
        }
        finally
        {
            await hostedService.StopAsync(CancellationToken.None);
            DeleteDirectory(databasePath);
        }
    }

    [Fact]
    public async Task WhenOnlyPriorityAndFlushIntervalChange_ThenTheStoreDoesNotRestart()
    {
        // Arrange: a restart shows as a second processor; the files and the durable coverage survive one.
        var (context, root, _) = CreateGraph();
        using var store = new GatedHistoryStore();
        ((IInterceptorSubject)store).Context.AddFallbackContext(context);
        var databasePath = Path.Combine(Path.GetTempPath(), "hb-sqlite-hist-" + Guid.NewGuid().ToString("N"));
        store.DatabasePath = databasePath;
        store.BufferTimeMilliseconds = 50;
        store.FlushIntervalSeconds = 1;
        await store.StartAsync(CancellationToken.None);
        try
        {
            await RecordAndWaitForValueAsync(store, "/Temperature", value => root.Temperature = value, 11);
            var coverageFrom = Assert.Single(store.CoverageRanges).From;

            // Act
            store.Priority = 7;
            store.FlushIntervalSeconds = 2;
            await store.ApplyConfigurationAsync(CancellationToken.None);
            await RecordAndWaitForValueAsync(store, "/Temperature", value => root.Temperature = value, 22);

            // A restarted session creates its processor before its flush loop publishes anything, so a flush
            // instant from after the second value orders the count below after any restart.
            var recordedAt = DateTimeOffset.UtcNow;
            await AsyncTestHelpers.WaitUntilAsync(
                () => store.LastFlushUtc > recordedAt,
                message: "The flush loop never published a flush after the second value.");

            // Assert
            Assert.Equal(1, store.Creations);
            var series = QuerySeries(store, "/Temperature");
            Assert.Contains(series.Points, point => point.Number == 11);
            Assert.Contains(series.Points, point => point.Number == 22);
            Assert.Equal(coverageFrom, Assert.Single(store.CoverageRanges).From);
        }
        finally
        {
            await store.StopAsync(CancellationToken.None);
            DeleteDirectory(databasePath);
        }
    }

    /// <summary>
    /// Waits until the store's coverage reaches past <paramref name="instant"/>, which after a restart requested
    /// at that instant is first done by the ending session's final flush.
    /// </summary>
    private static Task WaitForCoverageToReachAsync(SqliteHistoryStoreSubject store, DateTimeOffset instant) =>
        AsyncTestHelpers.WaitUntilAsync(
            () => store.CoverageRanges is [.., var last] && last.To > instant,
            message: "Store coverage never reached past the restart.");

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task WhenWrittenBeforeProcessingStarts_ThenHistoryQueriesIncludeTheSample(bool restart, bool heldValue)
    {
        // Arrange
        var (context, root, _) = CreateGraph();
        using var store = new GatedHistoryStore { HoldSessions = true };
        ((IInterceptorSubject)store).Context.AddFallbackContext(context);
        var databasePath = Path.Combine(Path.GetTempPath(), "hb-sqlite-hist-" + Guid.NewGuid().ToString("N"));
        store.DatabasePath = databasePath;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var queryFrom = DateTimeOffset.UtcNow;
        await store.StartAsync(CancellationToken.None);
        try
        {
            var release = await store.Sessions.Reader.ReadAsync(timeout.Token);
            if (restart)
            {
                release.SetResult();
                await RecordAndWaitForValueAsync(store, "/Temperature", value => root.Temperature = value, 1);
                store.MaxJsonSize++;
                await store.ApplyConfigurationAsync(timeout.Token);
                release = await store.Sessions.Reader.ReadAsync(timeout.Token);
            }

            // Act
            root.Temperature = 21.5;
            release.SetResult();
            await AsyncTestHelpers.WaitUntilAsync(() =>
            {
                store.FlushNowAsync().GetAwaiter().GetResult();
                return QuerySeries(store, "/Temperature").Points.Any(point => point.Number == 21.5);
            });

            // Assert
            if (heldValue)
            {
                var recorded = Assert.Single(QuerySeries(store, "/Temperature").Points, point => point.Number == 21.5);
                var sample = await store.GetSampleAtOrBeforeAsync("/Temperature", recorded.Timestamp, timeout.Token);
                Assert.NotNull(sample);
                Assert.Equal(21.5, sample.Number);
            }
            else
            {
                var query = new HistoryQuery("/Temperature", queryFrom, DateTimeOffset.UtcNow);
                var series = await new IHistoryStore[] { store }.QueryHistoryAsync(query, timeout.Token);
                Assert.Contains(series.Points, point => point.Number == 21.5);
            }
        }
        finally
        {
            await store.StopAsync(CancellationToken.None);
            DeleteDirectory(databasePath);
        }
    }

    [Fact]
    public async Task WhenProcessingFaults_ThenTheSessionEndsAndTheRetriedSessionRecords()
    {
        // Arrange: the first processor's filter throws on the first change, which faults its ProcessAsync.
        var (context, root, _) = CreateGraph();
        using var store = new GatedHistoryStore { FaultFirstProcessor = true };
        ((IInterceptorSubject)store).Context.AddFallbackContext(context);
        var databasePath = Path.Combine(Path.GetTempPath(), "hb-sqlite-hist-" + Guid.NewGuid().ToString("N"));
        store.DatabasePath = databasePath;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await store.StartAsync(CancellationToken.None);
        try
        {
            // Act
            root.Temperature = 1;
            var (statusAtRetry, lastErrorAtRetry) = await store.StateAtRetry.Task.WaitAsync(timeout.Token);
            var series = await RecordAndWaitForValueAsync(store, "/Temperature", value => root.Temperature = value, 21.5);

            // Assert
            Assert.Equal("Error", statusAtRetry);
            Assert.Equal("Filter failed.", lastErrorAtRetry);
            Assert.Contains(series.Points, point => point.Number == 21.5);
            Assert.Equal("Running", store.Status);
            Assert.Null(store.LastError);
        }
        finally
        {
            await store.StopAsync(CancellationToken.None);
            DeleteDirectory(databasePath);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenWrittenWhileTheNextProcessorIsBeingCreated_ThenMergedQueriesIncludeTheSample(bool afterFault)
    {
        // Arrange: the second CreateProcessor blocks before it runs, so the write lands after the first processor
        // is gone and before the next session captures anything.
        var (context, root, _) = CreateGraph();
        using var gate = new ManualResetEventSlim();
        using var store = new GatedHistoryStore { HoldSecondCreate = gate, FaultFirstProcessor = afterFault };
        ((IInterceptorSubject)store).Context.AddFallbackContext(context);
        var databasePath = Path.Combine(Path.GetTempPath(), "hb-sqlite-hist-" + Guid.NewGuid().ToString("N"));
        store.DatabasePath = databasePath;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var queryFrom = DateTimeOffset.UtcNow;
        await store.StartAsync(CancellationToken.None);
        try
        {
            if (!afterFault)
            {
                await RecordAndWaitForValueAsync(store, "/Temperature", value => root.Temperature = value, 1);
            }

            if (afterFault)
            {
                root.Temperature = 1;
            }
            else
            {
                store.MaxJsonSize++;
                await store.ApplyConfigurationAsync(timeout.Token);
            }
            await store.SecondCreateEntered.Task.WaitAsync(timeout.Token);

            // Act
            root.Temperature = 21.5;
            gate.Set();

            // Assert: the new session serves the sample, and one continuous range covers both sessions, so a
            // sample persisted by the first is served too.
            var query = new HistoryQuery("/Temperature", queryFrom, DateTimeOffset.UtcNow.AddMinutes(1));
            var series = default(HistorySeries)!;
            await AsyncTestHelpers.WaitUntilAsync(
                () =>
                {
                    store.FlushNowAsync().GetAwaiter().GetResult();
                    series = new IHistoryStore[] { store }.QueryHistoryAsync(query, timeout.Token).GetAwaiter().GetResult();
                    return series.Points.Any(point => point.Number == 21.5);
                },
                timeout: TimeSpan.FromSeconds(10),
                message: "The sample written between two processors was not served.");
            Assert.Single(store.CoverageRanges);
            Assert.Equal(!afterFault, series.Points.Any(point => point.Number == 1));
        }
        finally
        {
            gate.Set();
            await store.StopAsync(CancellationToken.None);
            DeleteDirectory(databasePath);
        }
    }

    private sealed class GatedHistoryStore() : SqliteHistoryStoreSubject(NullLogger<SqliteHistoryStoreSubject>.Instance)
    {
        private int _creations;

        public bool HoldSessions { get; init; }
        public bool FaultFirstProcessor { get; init; }
        public ManualResetEventSlim? HoldSecondCreate { get; init; }
        public int Creations => Volatile.Read(ref _creations);
        public Channel<TaskCompletionSource> Sessions { get; } = Channel.CreateUnbounded<TaskCompletionSource>();
        public TaskCompletionSource SecondCreateEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<(string Status, string? LastError)> StateAtRetry { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override ChangeQueueProcessor CreateProcessor(PropertyChangeQueueSubscription subscription)
        {
            if (++_creations == 2 && HoldSecondCreate is not null)
            {
                SecondCreateEntered.SetResult();
                HoldSecondCreate.Wait();
            }

            var processor = base.CreateProcessor(subscription);
            if (_creations > 1 || !FaultFirstProcessor)
            {
                return processor;
            }

            // Replaced by one whose filter throws, after the base captured the session's settings from it, so
            // the retried session runs on the real processor.
            processor.Dispose();
            return new ChangeQueueProcessor(
                this, subscription, _ => throw new InvalidOperationException("Filter failed."),
                (_, _) => ValueTask.CompletedTask, ChangeDeliveryRule.SourceValuesMayBeStale,
                bufferTime: null, maxQueueDepth: null, NullLogger.Instance);
        }

        protected override TimeSpan GetRetryDelay(Exception exception)
        {
            StateAtRetry.TrySetResult((Status, LastError));
            return TimeSpan.Zero;
        }

        protected override async Task ProcessAsync(ChangeQueueProcessor processor, CancellationToken stoppingToken)
        {
            if (HoldSessions)
            {
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Sessions.Writer.TryWrite(release);
                await release.Task.WaitAsync(stoppingToken);
            }

            await base.ProcessAsync(processor, stoppingToken);
        }
    }
}
