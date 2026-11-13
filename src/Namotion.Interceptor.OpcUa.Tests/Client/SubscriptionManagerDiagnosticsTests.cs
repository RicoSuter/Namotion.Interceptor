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
using Opc.Ua;
using Opc.Ua.Client.Subscriptions;
using static Namotion.Interceptor.OpcUa.Tests.Client.ClientSourceTestFactory;
using static Namotion.Interceptor.OpcUa.Tests.Client.SubscriptionTestFactory;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

public class SubscriptionManagerDiagnosticsTests
{
    [Fact]
    public async Task WhenTrackedStateMutates_ThenCachedCountsStayCurrent()
    {
        // Arrange
        var configuration = CreateConfiguration();
        configuration.MaxItemsPerSubscription = 1;
        await using var source = CreateClientSource(configuration: configuration);
        var propertyWriter = new SubjectPropertyWriter(source, NullLogger.Instance);
        await using var manager = CreateManager(
            source,
            propertyWriter,
            configuration,
            new ConcurrentQueue<Exception>());
        var session = CreateSession();
        var nameRequest = CreateRequest(source, nameof(TestRoot.Name));
        var unitRequest = CreateRequest(source, nameof(TestRoot.Number_Unit));
        var monitoredItem = CreateMonitoredItem();

        // Act & Assert
        Assert.Equal(
            (Subscriptions: 0, MonitoredItems: 0),
            (Subscriptions: manager.SubscriptionCount, MonitoredItems: manager.MonitoredItemCount));
        await manager.CreateBatchedSubscriptionsAsync([nameRequest, unitRequest], session, CancellationToken.None);
        Assert.Equal(
            (Subscriptions: 2, MonitoredItems: 2),
            (Subscriptions: manager.SubscriptionCount, MonitoredItems: manager.MonitoredItemCount));
        await manager.CreateBatchedSubscriptionsAsync([nameRequest], session, CancellationToken.None);
        Assert.Equal(
            (Subscriptions: 1, MonitoredItems: 1),
            (Subscriptions: manager.SubscriptionCount, MonitoredItems: manager.MonitoredItemCount));

        manager.TrackMonitoredItem(monitoredItem, unitRequest);
        Assert.Equal(2, manager.MonitoredItemCount);
        manager.TrackMonitoredItem(monitoredItem, unitRequest);
        Assert.Equal(2, manager.MonitoredItemCount);
        manager.RemoveItemsForSubject(source.RootSubject);
        Assert.Equal(0, manager.MonitoredItemCount);
        manager.TrackMonitoredItem(monitoredItem, unitRequest);
        Assert.Equal(1, manager.MonitoredItemCount);

        await manager.DisposeAsync();
        Assert.Equal(
            (Subscriptions: 0, MonitoredItems: 0),
            (Subscriptions: manager.SubscriptionCount, MonitoredItems: manager.MonitoredItemCount));
    }

    [Fact]
    public async Task WhenATrackedSubscriptionStopsPublishing_ThenHasStoppedPublishingIsTrue()
    {
        // Arrange
        var configuration = CreateConfiguration();
        await using var source = CreateClientSource(configuration: configuration);
        var propertyWriter = new SubjectPropertyWriter(source, NullLogger.Instance);
        await using var manager = CreateManager(source, propertyWriter, configuration, new ConcurrentQueue<Exception>());
        var subscription = await CreateTrackedSubscriptionAsync(manager, source, CreateSession());
        var stoppedBefore = manager.HasStoppedPublishing;

        // Act
        await manager.OnSubscriptionStateChangedAsync(
            subscription, SubscriptionState.Modified, PublishState.Stopped | PublishState.Timeout);

        // Assert
        Assert.False(stoppedBefore);
        Assert.True(manager.HasStoppedPublishing);
    }

    [Fact]
    public async Task WhenAStoppedSubscriptionPublishesAgain_ThenHasStoppedPublishingIsFalse()
    {
        // Arrange
        var configuration = CreateConfiguration();
        await using var source = CreateClientSource(configuration: configuration);
        var propertyWriter = new SubjectPropertyWriter(source, NullLogger.Instance);
        await using var manager = CreateManager(source, propertyWriter, configuration, new ConcurrentQueue<Exception>());
        var subscription = await CreateTrackedSubscriptionAsync(manager, source, CreateSession());
        await manager.OnSubscriptionStateChangedAsync(subscription, SubscriptionState.Modified, PublishState.Stopped);

        // Act
        await manager.OnSubscriptionStateChangedAsync(subscription, SubscriptionState.Modified, PublishState.Recovered);

        // Assert
        Assert.False(manager.HasStoppedPublishing);
    }

    [Fact]
    public async Task WhenAnUntrackedSubscriptionStopsPublishing_ThenHasStoppedPublishingIsFalse()
    {
        // Arrange
        var configuration = CreateConfiguration();
        await using var source = CreateClientSource(configuration: configuration);
        var propertyWriter = new SubjectPropertyWriter(source, NullLogger.Instance);
        await using var manager = CreateManager(source, propertyWriter, configuration, new ConcurrentQueue<Exception>());
        await CreateTrackedSubscriptionAsync(manager, source, CreateSession());

        // Act
        await manager.OnSubscriptionStateChangedAsync(
            CreateSubscription(), SubscriptionState.Modified, PublishState.Stopped);

        // Assert
        Assert.False(manager.HasStoppedPublishing);
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
            manager.OnDataChange(CreateSubscription(), sequenceNumber: 0, notification));

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

        manager.OnDataChange(CreateSubscription(), sequenceNumber: 0, CreateNotification(clientHandle, "value"));

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

        var callback = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() => manager.OnDataChange(
            CreateSubscription(), sequenceNumber: 0, CreateNotification(clientHandle, "value")));
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

    [Theory]
    [InlineData(nameof(StatusCodes.BadTimeout), true, 0)]
    [InlineData(nameof(StatusCodes.BadNotSupported), false, 1)]
    [InlineData(nameof(StatusCodes.BadMonitoredItemFilterUnsupported), false, 1)]
    [InlineData(nameof(StatusCodes.BadNodeIdUnknown), false, 0)]
    public async Task WhenAMonitoredItemFailed_ThenItIsKeptForRetryOrPolledOrRemoved(
        string statusCodeName, bool expectKept, int expectedPollingItemCount)
    {
        // Arrange
        var configuration = CreateConfiguration();
        await using var source = CreateClientSource(configuration: configuration);
        var propertyWriter = new SubjectPropertyWriter(source, NullLogger.Instance);
        await using var pollingManager = CreatePollingManager(source, propertyWriter, configuration);
        var reportedErrors = new ConcurrentQueue<Exception>();
        await using var manager = CreateManager(source, propertyWriter, configuration, reportedErrors, pollingManager);
        var monitoredItem = CreateMonitoredItem(created: false, error: StatusCodeNames.Resolve(statusCodeName));
        var subscription = CreateSubscription([monitoredItem]);
        manager.TrackMonitoredItem(monitoredItem, CreateRequest(source, nameof(TestRoot.Name)));

        // Act
        manager.FilterOutFailedMonitoredItems(subscription);

        // Assert
        Assert.Equal(expectKept ? [monitoredItem] : [], subscription.MonitoredItems.Items);
        Assert.Equal(expectKept ? 1 : 0, manager.MonitoredItemCount);
        Assert.Equal(expectedPollingItemCount, pollingManager.PollingItemCount);
        Assert.Empty(reportedErrors);
    }

    [Fact]
    public async Task WhenAMonitoredItemWasNotAttemptedYet_ThenFilteringLeavesItAlone()
    {
        // Arrange
        var configuration = CreateConfiguration();
        await using var source = CreateClientSource(configuration: configuration);
        var propertyWriter = new SubjectPropertyWriter(source, NullLogger.Instance);
        await using var pollingManager = CreatePollingManager(source, propertyWriter, configuration);
        await using var manager = CreateManager(
            source, propertyWriter, configuration, new ConcurrentQueue<Exception>(), pollingManager);
        var notAttemptedYet = CreateMonitoredItem(created: false);
        var created = CreateMonitoredItem();
        var subscription = CreateSubscription([notAttemptedYet, created]);
        manager.TrackMonitoredItem(notAttemptedYet, CreateRequest(source, nameof(TestRoot.Name)));
        manager.TrackMonitoredItem(created, CreateRequest(source, nameof(TestRoot.Number_Unit)));

        // Act
        manager.FilterOutFailedMonitoredItems(subscription);

        // Assert
        Assert.Equal([notAttemptedYet, created], subscription.MonitoredItems.Items);
        Assert.Equal(2, manager.MonitoredItemCount);
        Assert.Equal(0, pollingManager.PollingItemCount);
    }

    [Fact]
    public async Task WhenARetryableItemKeepsFailing_ThenItIsEscalatedToPollingAfterTheRetryBound()
    {
        // Arrange
        var configuration = CreateConfiguration();
        await using var source = CreateClientSource(configuration: configuration);
        var propertyWriter = new SubjectPropertyWriter(source, NullLogger.Instance);
        await using var pollingManager = CreatePollingManager(source, propertyWriter, configuration);
        await using var manager = CreateManager(
            source, propertyWriter, configuration, new ConcurrentQueue<Exception>(), pollingManager);
        var session = CreateSession(_ => CreateMonitoredItem(created: false, error: StatusCodes.BadTimeout));
        var subscription = await CreateTrackedSubscriptionAsync(manager, source, session);
        for (var i = 1; i < SubscriptionManager.MaxHealAttemptsBeforeEscalation; i++)
        {
            manager.EscalatePersistentlyFailedItems();
        }

        var polledWithinTheBound = pollingManager.PollingItemCount;

        // Act
        manager.EscalatePersistentlyFailedItems();

        // Assert
        Assert.Equal(0, polledWithinTheBound);
        Assert.Equal(1, pollingManager.PollingItemCount);
        Assert.Empty(subscription.MonitoredItems.Items);
        Assert.Equal(0, manager.MonitoredItemCount);
    }

    [Fact]
    public async Task WhenABatchFailsToBeApplied_ThenItsItemsAreNoLongerTracked()
    {
        // Arrange
        var configuration = CreateConfiguration();
        configuration.OperationTimeout = TimeSpan.FromMilliseconds(100);
        await using var source = CreateClientSource(configuration: configuration);
        var propertyWriter = new SubjectPropertyWriter(source, NullLogger.Instance);
        await using var manager = CreateManager(source, propertyWriter, configuration, new ConcurrentQueue<Exception>());
        var session = CreateSession(subscriptionsCreated: false);

        // Act
        await Assert.ThrowsAsync<TimeoutException>(() => manager.CreateBatchedSubscriptionsAsync(
            [CreateRequest(source, nameof(TestRoot.Name)), CreateRequest(source, nameof(TestRoot.Number_Unit))],
            session,
            CancellationToken.None));

        // Assert
        Assert.Equal(
            (Subscriptions: 0, MonitoredItems: 0, NeedsRecreate: true),
            (Subscriptions: manager.SubscriptionCount, MonitoredItems: manager.MonitoredItemCount, manager.NeedsRecreate));
    }

    [Fact]
    public async Task WhenAnItemIsCreatedAfterTheSubscriptionWasSetUp_ThenTheHealthCheckRegistersItForReadAfterWrite()
    {
        // Arrange
        var configuration = CreateConfiguration();
        configuration.DefaultSamplingInterval = 0;
        await using var source = CreateClientSource(configuration: configuration);
        var propertyWriter = new SubjectPropertyWriter(source, NullLogger.Instance);
        await using var readAfterWriteManager = new ReadAfterWriteManager(
            () => null, source, configuration, new ReadAfterWriteMetrics(), _ => { }, NullLogger.Instance);
        await using var manager = CreateManager(
            source, propertyWriter, configuration, new ConcurrentQueue<Exception>(), readAfterWriteManager: readAfterWriteManager);

        var monitoredItem = CreateMonitoredItem(created: false, error: StatusCodes.BadTimeout);
        var request = CreateRequest(source, nameof(TestRoot.Name));
        await manager.CreateBatchedSubscriptionsAsync([request], CreateSession(_ => monitoredItem), CancellationToken.None);

        readAfterWriteManager.OnPropertyWritten(request.NodeId);
        var pendingReadsBeforeCreation = readAfterWriteManager.PendingReadCount;

        var item = Mock.Get(monitoredItem);
        item.SetupGet(i => i.Created).Returns(true);
        item.SetupGet(i => i.Error).Returns(ServiceResult.Good);
        item.SetupGet(i => i.CurrentSamplingInterval).Returns(TimeSpan.FromMinutes(1));

        // Act
        manager.RegisterCreatedItemsWithReadAfterWriteManager();

        // Assert
        readAfterWriteManager.OnPropertyWritten(request.NodeId);
        Assert.Equal(0, pendingReadsBeforeCreation);
        Assert.Equal(1, readAfterWriteManager.PendingReadCount);
    }

    [Fact]
    public async Task WhenAnItemIsCreatedBeforeItsRevisedIntervalIsKnown_ThenALaterHealthCheckRegistersItForReadAfterWrite()
    {
        // Arrange
        var configuration = CreateConfiguration();
        configuration.DefaultSamplingInterval = 0;
        await using var source = CreateClientSource(configuration: configuration);
        var propertyWriter = new SubjectPropertyWriter(source, NullLogger.Instance);
        await using var readAfterWriteManager = new ReadAfterWriteManager(
            () => null, source, configuration, new ReadAfterWriteMetrics(), _ => { }, NullLogger.Instance);
        await using var manager = CreateManager(
            source, propertyWriter, configuration, new ConcurrentQueue<Exception>(), readAfterWriteManager: readAfterWriteManager);

        var monitoredItem = CreateMonitoredItem(created: false, error: StatusCodes.BadTimeout);
        var request = CreateRequest(source, nameof(TestRoot.Name));
        await manager.CreateBatchedSubscriptionsAsync([request], CreateSession(_ => monitoredItem), CancellationToken.None);

        var item = Mock.Get(monitoredItem);
        item.SetupGet(i => i.Created).Returns(true);
        item.SetupGet(i => i.Error).Returns(ServiceResult.Good);
        item.SetupGet(i => i.CurrentSamplingInterval).Returns(TimeSpan.Zero);
        manager.RegisterCreatedItemsWithReadAfterWriteManager();
        item.SetupGet(i => i.CurrentSamplingInterval).Returns(TimeSpan.FromMinutes(1));

        // Act
        manager.RegisterCreatedItemsWithReadAfterWriteManager();

        // Assert
        readAfterWriteManager.OnPropertyWritten(request.NodeId);
        Assert.Equal(1, readAfterWriteManager.PendingReadCount);
    }

    [Fact]
    public async Task WhenABatchFailsToBeApplied_ThenItsSubscriptionIsDisposed()
    {
        // Arrange
        var configuration = CreateConfiguration();
        configuration.OperationTimeout = TimeSpan.FromMilliseconds(100);
        await using var source = CreateClientSource(configuration: configuration);
        var propertyWriter = new SubjectPropertyWriter(source, NullLogger.Instance);
        await using var manager = CreateManager(source, propertyWriter, configuration, new ConcurrentQueue<Exception>());

        var subscription = CreateSubscription(created: false);
        var session = CreateSession(createSubscription: () => subscription);

        // Act
        await Assert.ThrowsAsync<TimeoutException>(() => manager.CreateBatchedSubscriptionsAsync(
            [CreateRequest(source, nameof(TestRoot.Name))], session, CancellationToken.None));

        // Assert
        Mock.Get(subscription).Verify(s => s.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task WhenSubscriptionsAreRecreatedWithoutItems_ThenThePreviousSubscriptionsAreDisposed()
    {
        // Arrange
        var configuration = CreateConfiguration();
        await using var source = CreateClientSource(configuration: configuration);
        var propertyWriter = new SubjectPropertyWriter(source, NullLogger.Instance);
        await using var manager = CreateManager(source, propertyWriter, configuration, new ConcurrentQueue<Exception>());

        var subscription = CreateSubscription();
        var session = CreateSession(createSubscription: () => subscription);
        await manager.CreateBatchedSubscriptionsAsync(
            [CreateRequest(source, nameof(TestRoot.Name))], session, CancellationToken.None);

        // Act
        await manager.CreateBatchedSubscriptionsAsync([], session, CancellationToken.None);

        // Assert
        Mock.Get(subscription).Verify(s => s.DisposeAsync(), Times.Once);
        Assert.Equal(
            (Subscriptions: 0, MonitoredItems: 0, NeedsRecreate: false),
            (Subscriptions: manager.SubscriptionCount, MonitoredItems: manager.MonitoredItemCount, manager.NeedsRecreate));
    }

    [Fact]
    public async Task WhenSubscriptionsAreDisposed_ThenTheirDisposalsRunConcurrently()
    {
        // Arrange
        var configuration = CreateConfiguration();
        configuration.MaxItemsPerSubscription = 1;
        await using var source = CreateClientSource(configuration: configuration);
        var propertyWriter = new SubjectPropertyWriter(source, NullLogger.Instance);
        var manager = CreateManager(source, propertyWriter, configuration, new ConcurrentQueue<Exception>());
        await manager.CreateBatchedSubscriptionsAsync(
            [
                CreateRequest(source, nameof(TestRoot.Name)),
                CreateRequest(source, nameof(TestRoot.Number_Unit)),
                CreateRequest(source, nameof(TestRoot.Connected))
            ],
            CreateSession(),
            CancellationToken.None);

        var subscriptions = manager.Subscriptions.ToArray();
        using var allStarted = new CountdownEvent(subscriptions.Length);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        foreach (var subscription in subscriptions)
        {
            Mock.Get(subscription)
                .Setup(s => s.DisposeAsync())
                .Returns(() =>
                {
                    allStarted.Signal();
                    return new ValueTask(release.Task);
                });
        }

        // Act
        var disposal = manager.DisposeAsync().AsTask();

        // Assert
        Assert.True(allStarted.Wait(TestTimeouts.Default));
        Assert.False(disposal.IsCompleted);
        release.SetResult();
        await disposal;
    }

    private static SubscriptionManager CreateManager(
        OpcUaSubjectClientSource source,
        SubjectPropertyWriter propertyWriter,
        OpcUaClientConfiguration configuration,
        ConcurrentQueue<Exception> reportedErrors,
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
            NullLogger.Instance);

    private static PollingManager CreatePollingManager(
        OpcUaSubjectClientSource source,
        SubjectPropertyWriter propertyWriter,
        OpcUaClientConfiguration configuration) =>
        new(
            source,
            sessionProvider: () => null,
            propertyWriter,
            configuration,
            source.PollingMetrics,
            source.ReportBackgroundError,
            NullLogger.Instance);

    /// <summary>
    /// Creates the one subscription the manager tracks, with a monitored item for the name property.
    /// </summary>
    private static async Task<ISubscription> CreateTrackedSubscriptionAsync(
        SubscriptionManager manager, OpcUaSubjectClientSource source, Opc.Ua.Client.ISession session)
    {
        await manager.CreateBatchedSubscriptionsAsync(
            [CreateRequest(source, nameof(TestRoot.Name))], session, CancellationToken.None);
        return Assert.Single(manager.Subscriptions);
    }

    private static uint TrackNameProperty(SubscriptionManager manager, OpcUaSubjectClientSource source)
    {
        var monitoredItem = CreateMonitoredItem();
        manager.TrackMonitoredItem(monitoredItem, CreateRequest(source, nameof(TestRoot.Name)));
        return monitoredItem.ClientHandle;
    }

    private static DataValueChange[] CreateNotification(uint clientHandle, string value) =>
        [CreateDataChange(clientHandle, value, DateTime.UtcNow)];

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
