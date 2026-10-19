using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.OpcUa.Client.Connection;
using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Registry.Abstractions;
using Opc.Ua;
using Opc.Ua.Client;
using static Namotion.Interceptor.OpcUa.Tests.Client.ClientSourceTestFactory;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

public class SubscriptionNotificationOrderingTests
{
    [Fact]
    public async Task WhenAnOlderMessageArrivesAfterANewerOne_ThenTheNewerValueIsKept()
    {
        // Arrange
        await using var harness = await Harness.CreateAsync();
        var name = harness.Track(nameof(TestRoot.Name));

        // Act
        harness.Notify(sequenceNumber: 5, (name, "Newer"));
        harness.Notify(sequenceNumber: 4, (name, "Older"));

        // Assert
        Assert.Empty(harness.Errors);
        Assert.Equal("Newer", harness.Root.Name);
    }

    [Fact]
    public async Task WhenOutOfOrderMessagesAreBufferedDuringTheInitialLoad_ThenTheNewerValueIsKept()
    {
        // Arrange
        await using var harness = await Harness.CreateAsync(loadInitialState: false);
        var name = harness.Track(nameof(TestRoot.Name));
        harness.Notify(sequenceNumber: 5, (name, "Newer"));
        harness.Notify(sequenceNumber: 4, (name, "Older"));

        // Act
        await harness.LoadInitialStateAsync();

        // Assert
        Assert.Empty(harness.Errors);
        Assert.Equal("Newer", harness.Root.Name);
    }

    [Fact]
    public async Task WhenAMessageCarriesAnItemTwice_ThenTheLastValueIsKept()
    {
        // Arrange
        await using var harness = await Harness.CreateAsync();
        var name = harness.Track(nameof(TestRoot.Name));

        // Act
        harness.Notify(sequenceNumber: 7, (name, "First"), (name, "Second"));

        // Assert
        Assert.Equal("Second", harness.Root.Name);
    }

    [Fact]
    public async Task WhenAnotherItemWasUpdatedByANewerMessage_ThenAnOlderMessageStillUpdatesThisItem()
    {
        // Arrange
        await using var harness = await Harness.CreateAsync();
        var name = harness.Track(nameof(TestRoot.Name));
        var unit = harness.Track(nameof(TestRoot.Number_Unit));

        // Act
        harness.Notify(sequenceNumber: 10, (name, "Newer"));
        harness.Notify(sequenceNumber: 9, (unit, "items"));

        // Assert
        Assert.Equal("Newer", harness.Root.Name);
        Assert.Equal("items", harness.Root.Number_Unit);
    }

    [Fact]
    public async Task WhenTheSequenceNumberWraps_ThenTheMessageAfterTheWrapIsApplied()
    {
        // Arrange
        await using var harness = await Harness.CreateAsync();
        var name = harness.Track(nameof(TestRoot.Name));

        // Act
        harness.Notify(sequenceNumber: uint.MaxValue, (name, "BeforeWrap"));
        harness.Notify(sequenceNumber: 1, (name, "AfterWrap"));
        harness.Notify(sequenceNumber: uint.MaxValue - 1, (name, "Stale"));

        // Assert
        Assert.Equal("AfterWrap", harness.Root.Name);
    }

    [Fact]
    public async Task WhenAnItemIsIdleForMoreThanHalfTheSequenceRange_ThenItsNextValueIsApplied()
    {
        // Arrange
        await using var harness = await Harness.CreateAsync();
        var name = harness.Track(nameof(TestRoot.Name));
        var unit = harness.Track(nameof(TestRoot.Number_Unit));
        harness.Notify(sequenceNumber: 2, (name, "Early"));

        // Act
        // The other item keeps the subscription publishing past the point where comparing 32-bit
        // numbers would rank the next message as older than "Early".
        harness.Notify(sequenceNumber: 1u << 30, (unit, "a"));
        harness.Notify(sequenceNumber: 2u << 30, (unit, "b"));
        harness.Notify(sequenceNumber: 3u << 30, (unit, "c"));
        harness.Notify(sequenceNumber: (3u << 30) + 1, (name, "Late"));

        // Assert
        Assert.Equal("Late", harness.Root.Name);
    }

    [Fact]
    public async Task WhenTheSubscriptionIsReplaced_ThenItsMessagesAreNotRankedAgainstTheOldOne()
    {
        // Arrange
        await using var harness = await Harness.CreateAsync();
        var name = harness.Track(nameof(TestRoot.Name));
        harness.Notify(sequenceNumber: 500, (name, "OldSubscription"));

        // Act
        // A reconnect to a restarted server recreates the subscription, which numbers from 1 again.
        harness.ReplaceSubscription();
        harness.Notify(sequenceNumber: 1, (name, "NewSubscription"));

        // Assert
        Assert.Equal("NewSubscription", harness.Root.Name);
    }

    [Fact]
    public async Task WhenTheReplacedSubscriptionDeliversLate_ThenTheNewSubscriptionStillApplies()
    {
        // Arrange
        await using var harness = await Harness.CreateAsync();
        var name = harness.Track(nameof(TestRoot.Name));
        var oldSubscription = harness.ReplaceSubscription();
        harness.Notify(sequenceNumber: 1, (name, "New1"));

        // Act
        harness.Notify(oldSubscription, sequenceNumber: 900, (name, "OldLate"));
        harness.Notify(sequenceNumber: 2, (name, "New2"));

        // Assert
        Assert.Equal("New2", harness.Root.Name);
    }

    [Fact]
    public async Task WhenTheNewestValueFailsToApply_ThenAnOlderMessageThatIsNewerThanTheShownValueIsApplied()
    {
        // Arrange
        var configuration = CreateConfiguration();
        configuration.ValueConverter = new RejectingConverter("Unassignable");
        await using var harness = await Harness.CreateAsync(configuration: configuration);
        var name = harness.Track(nameof(TestRoot.Name));
        harness.Notify(sequenceNumber: 5, (name, "Old"));

        // Act
        harness.Notify(sequenceNumber: 10, (name, "Unassignable"));
        harness.Notify(sequenceNumber: 9, (name, "Fresher"));

        // Assert
        Assert.Single(harness.Errors);
        Assert.Equal("Fresher", harness.Root.Name);
    }

    [Fact]
    public async Task WhenAMessageCarriesNoSequenceNumber_ThenItIsApplied()
    {
        // Arrange
        await using var harness = await Harness.CreateAsync();
        var name = harness.Track(nameof(TestRoot.Name));
        harness.Notify(sequenceNumber: 5, (name, "Numbered"));

        // Act
        harness.Notify(sequenceNumber: 0, (name, "Unnumbered"));

        // Assert
        Assert.Equal("Unnumbered", harness.Root.Name);
    }

    [Fact]
    public async Task WhenMessagesAreDeliveredConcurrently_ThenTheHighestSequenceNumberWins()
    {
        // Arrange
        await using var harness = await Harness.CreateAsync();
        var name = harness.Track(nameof(TestRoot.Name));
        const int messageCount = 2000;

        // Act
        Parallel.For(1, messageCount + 1, new ParallelOptions { MaxDegreeOfParallelism = 8 },
            sequenceNumber => harness.Notify((uint)sequenceNumber, (name, sequenceNumber.ToString())));

        // Assert
        Assert.Empty(harness.Errors);
        Assert.Equal(messageCount.ToString(), harness.Root.Name);
    }

    /// <summary>
    /// Converts <paramref name="unassignableValue"/> to a number, which the string property's setter rejects.
    /// </summary>
    private sealed class RejectingConverter(string unassignableValue) : OpcUaValueConverter
    {
        public override object? ConvertToPropertyValue(object? nodeValue, RegisteredSubjectProperty property) =>
            Equals(nodeValue, unassignableValue) ? 0 : base.ConvertToPropertyValue(nodeValue, property);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly OpcUaSubjectClientSource _source;
        private readonly SubscriptionManager _manager;
        private readonly SubjectPropertyWriter _propertyWriter;
        private readonly List<Subscription> _replacedSubscriptions = [];
        private Subscription _subscription;

        private Harness(
            OpcUaSubjectClientSource source,
            SubjectPropertyWriter propertyWriter,
            SubscriptionManager manager,
            List<Exception> errors)
        {
            _source = source;
            _propertyWriter = propertyWriter;
            _manager = manager;
            Errors = errors;
            _subscription = new Subscription(NullTelemetryContext.Instance, new SubscriptionOptions());
        }

        public TestRoot Root => (TestRoot)_source.RootSubject;

        public List<Exception> Errors { get; }

        /// <param name="loadInitialState">
        /// <c>false</c> leaves the writer buffering notifications until <see cref="LoadInitialStateAsync"/>.
        /// </param>
        /// <param name="configuration">The client configuration, or <c>null</c> for the default one.</param>
        public static async Task<Harness> CreateAsync(
            bool loadInitialState = true, OpcUaClientConfiguration? configuration = null)
        {
            configuration ??= CreateConfiguration();
            var source = CreateClientSource(configuration: configuration);
            var propertyWriter = new SubjectPropertyWriter(source, NullLogger.Instance);
            if (loadInitialState)
            {
                await propertyWriter.LoadInitialStateAndResumeAsync(CancellationToken.None);
            }

            var errors = new List<Exception>();
            var manager = new SubscriptionManager(
                source, propertyWriter, pollingManager: null, readAfterWriteManager: null,
                configuration, error => { lock (errors) { errors.Add(error); } }, NullLogger.Instance);
            return new Harness(source, propertyWriter, manager, errors);
        }

        public Task LoadInitialStateAsync() => _propertyWriter.LoadInitialStateAndResumeAsync(CancellationToken.None);

        public uint Track(string propertyName)
        {
            var property = new RegisteredSubject(Root).TryGetProperty(propertyName)!;
            var monitoredItem = new MonitoredItem(NullTelemetryContext.Instance)
            {
                StartNodeId = new NodeId(propertyName, 2),
                AttributeId = Opc.Ua.Attributes.Value,
                Handle = property
            };
            _subscription.AddItem(monitoredItem);
            _manager.TrackMonitoredItem(monitoredItem);
            return monitoredItem.ClientHandle;
        }

        /// <summary>
        /// Switches later notifications to a new subscription object and returns the previous one.
        /// </summary>
        public Subscription ReplaceSubscription()
        {
            var previous = _subscription;
            _subscription = new Subscription(NullTelemetryContext.Instance, new SubscriptionOptions());
            _replacedSubscriptions.Add(previous);
            return previous;
        }

        public void Notify(uint sequenceNumber, params (uint ClientHandle, string Value)[] values)
            => Notify(_subscription, sequenceNumber, values);

        public void Notify(Subscription subscription, uint sequenceNumber, params (uint ClientHandle, string Value)[] values)
        {
            var notification = new DataChangeNotification
            {
                MonitoredItems = [.. values.Select(value => new MonitoredItemNotification
                {
                    ClientHandle = value.ClientHandle,
                    Value = new DataValue
                    {
                        Value = value.Value,
                        SourceTimestamp = DateTime.UtcNow,
                        StatusCode = StatusCodes.Good
                    }
                })],
                DiagnosticInfos = [],
                SequenceNumber = sequenceNumber
            };

            _manager.OnFastDataChange(subscription, notification, []);
        }

        public async ValueTask DisposeAsync()
        {
            await _manager.DisposeAsync();
            _subscription.Dispose();
            foreach (var subscription in _replacedSubscriptions)
            {
                subscription.Dispose();
            }
            await _source.DisposeAsync();
        }
    }
}
