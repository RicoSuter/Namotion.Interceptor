using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.OpcUa.Client.Connection;
using Namotion.Interceptor.OpcUa.Client.Polling;
using Namotion.Interceptor.OpcUa.Client.ReadAfterWrite;
using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Opc.Ua;
using Opc.Ua.Client;
using static Namotion.Interceptor.OpcUa.Tests.Client.ClientSourceTestFactory;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

public class SubscriptionManagerDiagnosticsTests
{
    [Fact]
    public async Task WhenTrackedStateMutates_ThenCachedCountsStayCurrent()
    {
        // Arrange
        var configuration = CreateConfiguration();
        await using var source = CreateClientSource(configuration: configuration);
        var propertyWriter = new SubjectPropertyWriter(source, NullLogger.Instance);
        await using var manager = CreateManager(
            source,
            propertyWriter,
            configuration,
            new ConcurrentQueue<Exception>());
        var property = new RegisteredSubject((TestRoot)source.RootSubject)
            .TryGetProperty(nameof(TestRoot.Name))!;
        var monitoredItem = new MonitoredItem(NullTelemetryContext.Instance)
        {
            StartNodeId = new NodeId("Name", 2),
            AttributeId = Opc.Ua.Attributes.Value,
            Handle = property
        };

        // Act & Assert
        Assert.Equal(
            (Subscriptions: 0, MonitoredItems: 0),
            (Subscriptions: manager.SubscriptionCount, MonitoredItems: manager.MonitoredItemCount));
        manager.TrackMonitoredItem(monitoredItem);
        Assert.Equal(1, manager.MonitoredItemCount);
        manager.TrackMonitoredItem(monitoredItem);
        Assert.Equal(1, manager.MonitoredItemCount);
        manager.RemoveItemsForSubject(source.RootSubject);
        Assert.Equal(0, manager.MonitoredItemCount);
        manager.TrackMonitoredItem(monitoredItem);
        Assert.Equal(1, manager.MonitoredItemCount);

        await manager.DisposeAsync();
        Assert.Equal(
            (Subscriptions: 0, MonitoredItems: 0),
            (Subscriptions: manager.SubscriptionCount, MonitoredItems: manager.MonitoredItemCount));
    }

    [Fact]
    public async Task WhenANotificationIsApplied_ThenTheSubscriptionsApplyLockIsHeld()
    {
        // Arrange - read-after-write registers this lock, so it must be the one notifications apply under.
        var configuration = CreateConfiguration();
        await using var source = CreateClientSource(configuration: configuration);
        var propertyWriter = new SubjectPropertyWriter(source, NullLogger.Instance);
        await using var manager = CreateManager(source, propertyWriter, configuration, new ConcurrentQueue<Exception>());
        var clientHandle = TrackNameProperty(manager, source);
        await propertyWriter.LoadInitialStateAndResumeAsync(CancellationToken.None);
        var subscription = new Subscription(NullTelemetryContext.Instance, new SubscriptionOptions());
        var applyLock = manager.GetApplyLock(subscription);
        bool? lockHeldDuringApply = null;
        using var changes = source.RootSubject.Context
            .GetPropertyChangeObservable(System.Reactive.Concurrency.ImmediateScheduler.Instance)
            .Subscribe(_ => lockHeldDuringApply = applyLock.IsHeldByCurrentThread);

        // Act
        manager.OnFastDataChange(subscription, CreateNotification(clientHandle, "value"), []);

        // Assert
        Assert.True(lockHeldDuringApply);
    }

    [Fact]
    public async Task WhenTheNotificationRankingIsReset_ThenReadAfterWriteKeepsApplyingUnderTheNotificationsLock()
    {
        // Arrange - a new server session resets the ranking, while the read-after-write registration
        // made before it stays.
        var configuration = CreateConfiguration();
        configuration.DefaultSamplingInterval = 0;
        await using var source = CreateClientSource(configuration: configuration);
        var propertyWriter = new SubjectPropertyWriter(source, NullLogger.Instance);
        await using var readAfterWriteManager = CreateReadAfterWriteManager(source, configuration);
        await using var manager = CreateManager(
            source, propertyWriter, configuration, new ConcurrentQueue<Exception>(), readAfterWriteManager: readAfterWriteManager);
        await propertyWriter.LoadInitialStateAndResumeAsync(CancellationToken.None);
        var root = (TestRoot)source.RootSubject;
        var item = CreateNameItem(root);
        var subscription = new Subscription(NullTelemetryContext.Instance, new SubscriptionOptions());
        subscription.AddItem(item);
        manager.TrackMonitoredItem(item);
        manager.TryAddSubscription(subscription);
        MarkCreated(item, revisedSamplingInterval: 100);
        manager.RegisterRecoveredItems(subscription);
        var registeredLock = manager.GetApplyLock(subscription);

        // Act
        manager.ResetNotificationRanking();
        bool? lockHeldDuringNotification = null;
        using (source.RootSubject.Context
            .GetPropertyChangeObservable(System.Reactive.Concurrency.ImmediateScheduler.Instance)
            .Subscribe(_ => lockHeldDuringNotification = registeredLock.IsHeldByCurrentThread))
        {
            manager.OnFastDataChange(subscription, CreateNotification(item.ClientHandle, "value"), []);
        }

        bool? lockHeldDuringRead = null;
        using (source.RootSubject.Context
            .GetPropertyChangeObservable(System.Reactive.Concurrency.ImmediateScheduler.Instance)
            .Subscribe(_ => lockHeldDuringRead = registeredLock.IsHeldByCurrentThread))
        {
            readAfterWriteManager.OnPropertyWritten(item.StartNodeId);
            await readAfterWriteManager.ProcessDueReadsAsync(DateTime.MaxValue);
        }

        // Assert
        Assert.True(lockHeldDuringNotification);
        Assert.Equal("read", root.Name);
        Assert.True(lockHeldDuringRead);
    }

    [Fact]
    public async Task WhenAnItemOfATrackedSubscriptionIsHealed_ThenReadAfterWriteAppliesUnderThatSubscriptionsLock()
    {
        // Arrange
        var configuration = CreateConfiguration();
        configuration.DefaultSamplingInterval = 0;
        await using var source = CreateClientSource(configuration: configuration);
        await using var readAfterWriteManager = CreateReadAfterWriteManager(source, configuration);
        await using var manager = CreateManager(
            source, new SubjectPropertyWriter(source, NullLogger.Instance), configuration, new ConcurrentQueue<Exception>(),
            readAfterWriteManager: readAfterWriteManager);
        var root = (TestRoot)source.RootSubject;
        var item = CreateNameItem(root);
        var subscription = new Subscription(NullTelemetryContext.Instance, new SubscriptionOptions());
        subscription.AddItem(item);
        manager.TrackMonitoredItem(item);
        manager.TryAddSubscription(subscription);
        MarkCreated(item, revisedSamplingInterval: 100);
        var applyLock = manager.GetApplyLock(subscription);
        bool? lockHeldDuringApply = null;
        using var changes = source.RootSubject.Context
            .GetPropertyChangeObservable(System.Reactive.Concurrency.ImmediateScheduler.Instance)
            .Subscribe(_ => lockHeldDuringApply = applyLock.IsHeldByCurrentThread);

        // Act
        manager.RegisterRecoveredItems(subscription);
        readAfterWriteManager.OnPropertyWritten(item.StartNodeId);
        await readAfterWriteManager.ProcessDueReadsAsync(DateTime.MaxValue);

        // Assert
        Assert.Equal("read", root.Name);
        Assert.True(lockHeldDuringApply);
    }

    [Fact]
    public async Task WhenAnItemOfAnUntrackedSubscriptionIsHealed_ThenItIsNotRegistered()
    {
        // Arrange - a heal of a subscription a recreate has just replaced must not register its lock.
        var configuration = CreateConfiguration();
        configuration.DefaultSamplingInterval = 0;
        await using var source = CreateClientSource(configuration: configuration);
        await using var readAfterWriteManager = CreateReadAfterWriteManager(source, configuration);
        await using var manager = CreateManager(
            source, new SubjectPropertyWriter(source, NullLogger.Instance), configuration, new ConcurrentQueue<Exception>(),
            readAfterWriteManager: readAfterWriteManager);
        var item = CreateNameItem((TestRoot)source.RootSubject);
        var replacedSubscription = new Subscription(NullTelemetryContext.Instance, new SubscriptionOptions());
        replacedSubscription.AddItem(item);
        manager.TrackMonitoredItem(item);
        MarkCreated(item, revisedSamplingInterval: 100);

        // Act
        manager.RegisterRecoveredItems(replacedSubscription);
        readAfterWriteManager.OnPropertyWritten(item.StartNodeId);

        // Assert
        Assert.Equal(0, readAfterWriteManager.PendingReadCount);
    }

    [Fact]
    public async Task WhenASubscriptionIsRegisteredAgainAfterASubjectWasReleased_ThenItsPropertyIsNotRegistered()
    {
        // Arrange - a released property's SDK item stays in the subscription.
        var configuration = CreateConfiguration();
        configuration.DefaultSamplingInterval = 0;
        await using var source = CreateClientSource(configuration: configuration);
        await using var readAfterWriteManager = CreateReadAfterWriteManager(source, configuration);
        await using var manager = CreateManager(
            source, new SubjectPropertyWriter(source, NullLogger.Instance), configuration, new ConcurrentQueue<Exception>(),
            readAfterWriteManager: readAfterWriteManager);
        var item = CreateNameItem((TestRoot)source.RootSubject);
        var subscription = new Subscription(NullTelemetryContext.Instance, new SubscriptionOptions());
        subscription.AddItem(item);
        manager.TrackMonitoredItem(item);
        manager.TryAddSubscription(subscription);
        MarkCreated(item, revisedSamplingInterval: 100);
        manager.RemoveItemsForSubject(source.RootSubject);

        // Act
        manager.RegisterRecoveredItems(subscription);
        readAfterWriteManager.OnPropertyWritten(item.StartNodeId);

        // Assert
        Assert.Equal(0, readAfterWriteManager.PendingReadCount);
    }

    [Fact]
    public async Task WhenAnItemFallsBackToPolling_ThenItIsUnregisteredFromReadAfterWrite()
    {
        // Arrange
        var configuration = CreateConfiguration();
        await using var source = CreateClientSource(configuration: configuration);
        await using var readAfterWriteManager = CreateReadAfterWriteManager(source, configuration);
        await using var manager = CreateManager(
            source, new SubjectPropertyWriter(source, NullLogger.Instance), configuration, new ConcurrentQueue<Exception>(),
            applyChangesAsync: static (_, _) => Task.CompletedTask,
            readAfterWriteManager: readAfterWriteManager);
        var item = CreateNameItem((TestRoot)source.RootSubject);
        var subscription = new Subscription(NullTelemetryContext.Instance, new SubscriptionOptions());
        subscription.AddItem(item);
        readAfterWriteManager.RegisterProperty(item.StartNodeId, (RegisteredSubjectProperty)item.Handle!, 0, TimeSpan.FromMinutes(1), new Lock());

        // Act
        await manager.RemoveAndFallBackToPollingAsync(subscription, [item], [item], CancellationToken.None);
        readAfterWriteManager.OnPropertyWritten(item.StartNodeId);

        // Assert
        Assert.Equal(0, readAfterWriteManager.PendingReadCount);
    }

    [Fact]
    public async Task WhenConversionThrows_ThenSourceReportsExactFailureOnceAndSdkCallbackStillThrows()
    {
        // Arrange
        var error = new InvalidOperationException("conversion failed");
        var configuration = CreateConfiguration();
        configuration.ValueConverter = new ThrowingConverter(error);
        await using var source = CreateClientSource(configuration: configuration);
        var reportedErrors = new ConcurrentQueue<Exception>();
        var propertyWriter = new SubjectPropertyWriter(source, NullLogger.Instance);
        await using var manager = CreateManager(source, propertyWriter, configuration, reportedErrors);
        var clientHandle = TrackNameProperty(manager, source);
        var notification = CreateNotification(clientHandle, "value");

        // Act & Assert
        var thrown = Assert.Throws<InvalidOperationException>(() =>
            manager.OnFastDataChange(
                new Subscription(NullTelemetryContext.Instance, new SubscriptionOptions()),
                notification,
                []));

        Assert.Same(error, thrown);
        var reported = Assert.Single(reportedErrors);
        Assert.Same(error, reported);
        Assert.Same(error, source.Diagnostics.LastError);

        source.NotifySessionHealthy();
        Assert.Same(error, source.Diagnostics.LastError);
    }

    [Fact]
    public async Task WhenPropertyApplicationThrows_ThenSourceReportsExactFailureOnceAndKeepsItAfterRecovery()
    {
        // Arrange
        var configuration = CreateConfiguration();
        configuration.ValueConverter = new WrongTypeConverter();
        await using var source = CreateClientSource(configuration: configuration);
        var reportedErrors = new ConcurrentQueue<Exception>();
        var propertyWriter = new SubjectPropertyWriter(source, NullLogger.Instance);
        await using var manager = CreateManager(source, propertyWriter, configuration, reportedErrors);
        var clientHandle = TrackNameProperty(manager, source);

        manager.OnFastDataChange(
            new Subscription(NullTelemetryContext.Instance, new SubscriptionOptions()),
            CreateNotification(clientHandle, "value"),
            []);

        // Act
        await propertyWriter.LoadInitialStateAndResumeAsync(CancellationToken.None);

        // Assert
        var error = Assert.Single(reportedErrors);
        Assert.IsType<InvalidCastException>(error);
        Assert.Same(error, source.Diagnostics.LastError);

        source.NotifySessionHealthy();
        Assert.Same(error, source.Diagnostics.LastError);
    }

    [Fact]
    public async Task WhenDisposalWinsDuringConversion_ThenFailureIsRethrownWithoutReporting()
    {
        // Arrange
        using var converter = new GatedThrowingConverter();
        var configuration = CreateConfiguration();
        configuration.ValueConverter = converter;
        await using var source = CreateClientSource(configuration: configuration);
        var reportedErrors = new ConcurrentQueue<Exception>();
        var propertyWriter = new SubjectPropertyWriter(source, NullLogger.Instance);
        await using var manager = CreateManager(source, propertyWriter, configuration, reportedErrors);
        var clientHandle = TrackNameProperty(manager, source);

        var callback = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() => manager.OnFastDataChange(
            new Subscription(NullTelemetryContext.Instance, new SubscriptionOptions()),
            CreateNotification(clientHandle, "value"),
            []));
        await converter.Entered.Task.WaitAsync(TestTimeouts.Default);

        // Act
        await manager.DisposeAsync();
        converter.Release();

        // Assert
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => callback);
        Assert.Same(converter.Error, thrown);
        Assert.Empty(reportedErrors);
        Assert.Null(source.Diagnostics.LastError);
    }

    [Fact]
    public async Task WhenPrimaryApplyChangesThrows_ThenSourceReportsExactFailureOnceAndFallsBack()
    {
        // Arrange
        var error = new ServiceResultException(StatusCodes.BadUnexpectedError, "primary apply failed");
        var configuration = CreateConfiguration();
        await using var source = CreateClientSource(configuration: configuration);
        var reportedErrors = new ConcurrentQueue<Exception>();
        var propertyWriter = new SubjectPropertyWriter(source, NullLogger.Instance);
        await using var pollingManager = new PollingManager(
            source,
            sessionProvider: () => null,
            propertyWriter,
            configuration,
            source.PollingMetrics,
            source.ReportBackgroundError,
            NullLogger.Instance);
        var applyAttempts = 0;
        await using var manager = CreateManager(
            source,
            propertyWriter,
            configuration,
            reportedErrors,
            (_, _) => Interlocked.Increment(ref applyAttempts) == 1
                ? Task.FromException(error)
                : Task.CompletedTask,
            pollingManager);
        var subscription = new Subscription(NullTelemetryContext.Instance, new SubscriptionOptions());
        var monitoredItem = CreateFailedNameItem(source, StatusCodes.BadNotSupported);
        subscription.AddItem(monitoredItem);
        manager.TrackMonitoredItem(monitoredItem);

        // Act
        await manager.ApplyChangesAndFilterFailedMonitoredItemsAsync(subscription, CancellationToken.None);

        // Assert
        var reported = Assert.Single(reportedErrors);
        Assert.Same(error, reported);
        Assert.Same(error, source.Diagnostics.LastError);
        Assert.Equal(2, applyAttempts);
        Assert.Empty(subscription.MonitoredItems);
        Assert.Equal(1, pollingManager.PollingItemCount);
    }

    [Fact]
    public async Task WhenPrimaryApplyChangesTokenIsCancelled_ThenFailureIsNotReportedAndFilteringContinues()
    {
        // Arrange
        using var cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.Cancel();
        var error = new ServiceResultException(StatusCodes.BadRequestCancelledByClient, "cancelled primary apply failed");
        var configuration = CreateConfiguration();
        configuration.EnablePollingFallback = false;
        await using var source = CreateClientSource(configuration: configuration);
        var reportedErrors = new ConcurrentQueue<Exception>();
        var propertyWriter = new SubjectPropertyWriter(source, NullLogger.Instance);
        var applyAttempts = 0;
        await using var manager = CreateManager(
            source,
            propertyWriter,
            configuration,
            reportedErrors,
            (_, _) => Interlocked.Increment(ref applyAttempts) == 1
                ? Task.FromException(error)
                : Task.CompletedTask);
        var subscription = new Subscription(NullTelemetryContext.Instance, new SubscriptionOptions());
        subscription.AddItem(CreateFailedNameItem(source, StatusCodes.BadAttributeIdInvalid));

        // Act
        await manager.ApplyChangesAndFilterFailedMonitoredItemsAsync(subscription, cancellationTokenSource.Token);

        // Assert
        Assert.Empty(reportedErrors);
        Assert.Null(source.Diagnostics.LastError);
        Assert.Equal(2, applyAttempts);
        Assert.Empty(subscription.MonitoredItems);
    }

    [Fact]
    public async Task WhenDisposalWinsDuringPrimaryApplyChanges_ThenFailureIsNotReportedAndFilteringContinues()
    {
        // Arrange
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var error = new ServiceResultException(StatusCodes.BadSessionClosed, "shutdown primary apply failed");
        var configuration = CreateConfiguration();
        configuration.EnablePollingFallback = false;
        await using var source = CreateClientSource(configuration: configuration);
        var reportedErrors = new ConcurrentQueue<Exception>();
        var propertyWriter = new SubjectPropertyWriter(source, NullLogger.Instance);
        var applyAttempts = 0;
        await using var manager = CreateManager(
            source,
            propertyWriter,
            configuration,
            reportedErrors,
            async (_, _) =>
            {
                if (Interlocked.Increment(ref applyAttempts) == 1)
                {
                    entered.TrySetResult();
                    await release.Task;
                    throw error;
                }
            });
        var subscription = new Subscription(NullTelemetryContext.Instance, new SubscriptionOptions());
        subscription.AddItem(CreateFailedNameItem(source, StatusCodes.BadAttributeIdInvalid));

        var applyChanges = manager.ApplyChangesAndFilterFailedMonitoredItemsAsync(
            subscription,
            CancellationToken.None);
        await entered.Task.WaitAsync(TestTimeouts.Default);

        // Act
        await manager.DisposeAsync();
        release.TrySetResult();
        await applyChanges;

        // Assert
        Assert.Empty(reportedErrors);
        Assert.Null(source.Diagnostics.LastError);
        Assert.Equal(2, applyAttempts);
        Assert.Empty(subscription.MonitoredItems);
    }

    [Fact]
    public async Task WhenFallbackApplyChangesThrows_ThenSourceReportsExactFailureOnceAndContinues()
    {
        // Arrange
        var error = new InvalidOperationException("fallback apply failed");
        var configuration = CreateConfiguration();
        await using var source = CreateClientSource(configuration: configuration);
        var reportedErrors = new ConcurrentQueue<Exception>();
        var propertyWriter = new SubjectPropertyWriter(source, NullLogger.Instance);
        await using var manager = CreateManager(
            source,
            propertyWriter,
            configuration,
            reportedErrors,
            (_, _) => Task.FromException(error));

        // Act
        await manager.RemoveAndFallBackToPollingAsync(
            new Subscription(NullTelemetryContext.Instance, new SubscriptionOptions()),
            [],
            [],
            CancellationToken.None);

        // Assert
        var reported = Assert.Single(reportedErrors);
        Assert.Same(error, reported);
        Assert.Same(error, source.Diagnostics.LastError);
    }

    [Fact]
    public async Task WhenFallbackApplyChangesIsCancelledByWorkToken_ThenFailureIsNotReportedAndContinues()
    {
        // Arrange
        using var cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.Cancel();
        var error = new OperationCanceledException(cancellationTokenSource.Token);
        var configuration = CreateConfiguration();
        await using var source = CreateClientSource(configuration: configuration);
        var reportedErrors = new ConcurrentQueue<Exception>();
        var propertyWriter = new SubjectPropertyWriter(source, NullLogger.Instance);
        await using var manager = CreateManager(
            source,
            propertyWriter,
            configuration,
            reportedErrors,
            (_, _) => Task.FromException(error));

        // Act
        await manager.RemoveAndFallBackToPollingAsync(
            new Subscription(NullTelemetryContext.Instance, new SubscriptionOptions()),
            [],
            [],
            cancellationTokenSource.Token);

        // Assert
        Assert.Empty(reportedErrors);
        Assert.Null(source.Diagnostics.LastError);
    }

    [Fact]
    public async Task WhenDisposalWinsDuringFallbackApplyChanges_ThenFailureIsNotReportedAndContinues()
    {
        // Arrange
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var error = new InvalidOperationException("shutdown fallback apply failed");
        var configuration = CreateConfiguration();
        await using var source = CreateClientSource(configuration: configuration);
        var reportedErrors = new ConcurrentQueue<Exception>();
        var propertyWriter = new SubjectPropertyWriter(source, NullLogger.Instance);
        await using var manager = CreateManager(
            source,
            propertyWriter,
            configuration,
            reportedErrors,
            async (_, _) =>
            {
                entered.TrySetResult();
                await release.Task;
                throw error;
            });

        var applyChanges = manager.RemoveAndFallBackToPollingAsync(
            new Subscription(NullTelemetryContext.Instance, new SubscriptionOptions()),
            [],
            [],
            CancellationToken.None);
        await entered.Task.WaitAsync(TestTimeouts.Default);

        // Act
        await manager.DisposeAsync();
        release.TrySetResult();
        await applyChanges;

        // Assert
        Assert.Empty(reportedErrors);
        Assert.Null(source.Diagnostics.LastError);
    }

    private static SubscriptionManager CreateManager(
        OpcUaSubjectClientSource source,
        SubjectPropertyWriter propertyWriter,
        OpcUaClientConfiguration configuration,
        ConcurrentQueue<Exception> reportedErrors,
        Func<Subscription, CancellationToken, Task>? applyChangesAsync = null,
        PollingManager? pollingManager = null,
        ReadAfterWriteManager? readAfterWriteManager = null) =>
        new(
            source,
            propertyWriter,
            pollingManager,
            readAfterWriteManager,
            configuration,
            error =>
            {
                reportedErrors.Enqueue(error);
                source.ReportBackgroundError(error);
            },
            NullLogger.Instance,
            applyChangesAsync);

    private static ReadAfterWriteManager CreateReadAfterWriteManager(
        OpcUaSubjectClientSource source,
        OpcUaClientConfiguration configuration)
    {
        var session = new Mock<ISession>();
        session.SetupGet(value => value.Connected).Returns(true);
        session
            .Setup(value => value.ReadAsync(
                It.IsAny<RequestHeader>(),
                It.IsAny<double>(),
                It.IsAny<TimestampsToReturn>(),
                It.IsAny<ArrayOf<ReadValueId>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReadResponse
            {
                ResponseHeader = new ResponseHeader(),
                Results = new[] { new DataValue("read", StatusCodes.Good, DateTime.UtcNow.AddMinutes(1)) }.ToArrayOf(),
                DiagnosticInfos = []
            });

        return new ReadAfterWriteManager(
            () => session.Object, source, configuration, new ReadAfterWriteMetrics(), static _ => { }, NullLogger.Instance);
    }

    private static MonitoredItem CreateNameItem(TestRoot root) =>
        new(NullTelemetryContext.Instance)
        {
            StartNodeId = new NodeId("Name", 2),
            AttributeId = Opc.Ua.Attributes.Value,
            Handle = new RegisteredSubject(root).TryGetProperty(nameof(TestRoot.Name))!
        };

    private static void MarkCreated(MonitoredItem item, double revisedSamplingInterval)
    {
        // Setting ServerId makes Status.Id != 0, which flips Created to true. The SDK sets the revised sampling
        // interval only from a server response, so a test has to set it through reflection.
        item.ServerId = 1;
        typeof(MonitoredItemStatus)
            .GetProperty(nameof(MonitoredItemStatus.SamplingInterval))!
            .SetValue(item.Status, revisedSamplingInterval);
    }

    private static MonitoredItem CreateFailedNameItem(OpcUaSubjectClientSource source, StatusCode statusCode)
    {
        var property = new RegisteredSubject((TestRoot)source.RootSubject)
            .TryGetProperty(nameof(TestRoot.Name))!;
        var monitoredItem = new MonitoredItem(NullTelemetryContext.Instance)
        {
            StartNodeId = new NodeId("Name", 2),
            AttributeId = Opc.Ua.Attributes.Value,
            DisplayName = nameof(TestRoot.Name),
            Handle = property
        };
        monitoredItem.SetError(new ServiceResult(statusCode));
        return monitoredItem;
    }

    private static uint TrackNameProperty(SubscriptionManager manager, OpcUaSubjectClientSource source)
    {
        var property = new RegisteredSubject((TestRoot)source.RootSubject)
            .TryGetProperty(nameof(TestRoot.Name))!;
        var monitoredItem = new MonitoredItem(NullTelemetryContext.Instance)
        {
            StartNodeId = new NodeId("Name", 2),
            AttributeId = Opc.Ua.Attributes.Value,
            Handle = property
        };
        var subscription = new Subscription(NullTelemetryContext.Instance, new SubscriptionOptions());
        subscription.AddItem(monitoredItem);
        manager.TrackMonitoredItem(monitoredItem);
        return monitoredItem.ClientHandle;
    }

    private static DataChangeNotification CreateNotification(uint clientHandle, object value) =>
        new()
        {
            MonitoredItems =
            [
                new MonitoredItemNotification
                {
                    ClientHandle = clientHandle,
                    Value = new DataValue(VariantHelper.CastFromWithReflectionFallback(value), StatusCodes.Good, DateTime.UtcNow)
                }
            ],
            DiagnosticInfos = []
        };

    private sealed class ThrowingConverter(Exception error) : OpcUaValueConverter
    {
        public override object? ConvertToPropertyValue(object? nodeValue, RegisteredSubjectProperty property) =>
            throw error;
    }

    private sealed class WrongTypeConverter : OpcUaValueConverter
    {
        public override object? ConvertToPropertyValue(object? nodeValue, RegisteredSubjectProperty property) => 0;
    }

    private sealed class GatedThrowingConverter : OpcUaValueConverter, IDisposable
    {
        private readonly ManualResetEventSlim _release = new(false);

        internal TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal InvalidOperationException Error { get; } = new("shutdown conversion failed");

        public override object? ConvertToPropertyValue(object? nodeValue, RegisteredSubjectProperty property)
        {
            Entered.TrySetResult();
            if (!_release.Wait(TestTimeouts.Default))
            {
                throw new TimeoutException("The test did not release the gated converter.");
            }

            throw Error;
        }

        internal void Release() => _release.Set();

        public void Dispose() => _release.Dispose();
    }
}
