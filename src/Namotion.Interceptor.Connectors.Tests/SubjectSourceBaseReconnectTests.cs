using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Connectors.Monitoring;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests;

/// <summary>
/// Reloads that happen inside a connector (the MQTT and WebSocket monitors, the OPC UA session manager)
/// call <see cref="SubjectPropertyWriter.StartBuffering"/> and
/// <see cref="SubjectPropertyWriter.LoadInitialStateAndResumeAsync"/> while the source is connected.
/// Every write committed before such a reload completes has to be judged against the reloaded model
/// before it reaches the source, as on the first connect (issue #362).
/// </summary>
public class SubjectSourceBaseReconnectTests
{
    private static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(10);

    // Bound for the two ends to settle. Nothing flushes a parked write without a resynchronization or
    // a new owned change, so a longer bound would only lengthen a failing run.
    private static readonly TimeSpan SettleBound = TimeSpan.FromSeconds(3);

    public enum LoadStyle
    {
        /// <summary>MQTT: the values arrive through the subscription and are replayed after the load.</summary>
        Retained,

        /// <summary>OPC UA: the load reads the values and applies them in its apply action.</summary>
        Read
    }

    [Theory]
    [InlineData(true, 8)]
    [InlineData(false, 8)]
    [InlineData(true, 0)]
    [InlineData(false, 0)]
    public async Task WhenWriteIsParkedBeforeInternalReconnect_ThenItIsDeliveredAndModelHoldsIt(
        bool sourceChangedDuringOutage, int bufferMilliseconds)
    {
        // Arrange
        var (person, source) = await StartConnectedAsync(echoWrites: false, bufferMilliseconds);
        try
        {
            source.TransportUp = false;
            person.FirstName = "L1";
            await WaitForParkedAsync(person, source);
            if (sourceChangedDuringOutage)
            {
                source.SetServerValue(nameof(Person.FirstName), "S1");
            }

            // Act
            source.SignalReconnect();
            await source.ReconnectCompleted.Task.WaitAsync(EventTimeout);

            // Assert
            var settled = await SettlesAsync(() =>
                person.FirstName == "L1" &&
                Equals(source.GetServerValue(nameof(Person.FirstName)), "L1") &&
                source.Diagnostics.OutboundRetries.Depth == 0);
            Assert.True(settled, Describe(person, source));
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
            source.Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenUnrelatedWriteFollowsInternalReconnect_ThenParkedWriteIsNotFlushedUnjudged(bool echoWrites)
    {
        // Arrange
        var (person, source) = await StartConnectedAsync(echoWrites, bufferMilliseconds: 8);
        try
        {
            source.TransportUp = false;
            person.FirstName = "L1";
            await WaitForParkedAsync(person, source);
            source.SetServerValue(nameof(Person.FirstName), "S1");
            source.SignalReconnect();
            await source.ReconnectCompleted.Task.WaitAsync(EventTimeout);

            // Act: a write to another owned property hands the processor a change, whose delivery flushes the
            // retry queue backlog ahead of it.
            person.LastName = "U1";
            await AsyncTestHelpers.WaitUntilAsync(
                () => Equals(source.GetServerValue(nameof(Person.LastName)), "U1"),
                timeout: EventTimeout,
                message: "The unrelated write was not delivered.");

            // Assert
            var settled = await SettlesAsync(() =>
                person.FirstName == "L1" &&
                Equals(source.GetServerValue(nameof(Person.FirstName)), "L1"));
            Assert.True(settled, Describe(person, source));
            Assert.All(source.FirstNameWrites, entry => Assert.Equal("FirstName=L1 (model=L1)", entry));
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
            source.Dispose();
        }
    }

    [Theory]
    [InlineData(true, 8)]
    [InlineData(false, 8)]
    [InlineData(true, 0)]
    public async Task WhenTwoInternalReconnectsFollowEachOther_ThenParkedWriteIsDeliveredExactlyOnce(
        bool echoWrites, int bufferMilliseconds)
    {
        // Arrange
        var (person, source) = await StartConnectedAsync(echoWrites, bufferMilliseconds);
        try
        {
            source.TransportUp = false;
            person.FirstName = "L1";
            await WaitForParkedAsync(person, source);
            source.SetServerValue(nameof(Person.FirstName), "S1");

            // Act: the second reload can land before, during or after the resynchronization of the first.
            source.SignalReconnect();
            source.SignalReconnect();
            await AsyncTestHelpers.WaitUntilAsync(() => source.ReconnectCount == 2,
                timeout: EventTimeout, message: "The two reconnects did not complete.");

            // Assert
            var settled = await SettlesAsync(() =>
                person.FirstName == "L1" &&
                Equals(source.GetServerValue(nameof(Person.FirstName)), "L1") &&
                source.Diagnostics.OutboundRetries.Depth == 0);
            Assert.True(settled, Describe(person, source));
            Assert.Equal(["FirstName=L1 (model=L1)"], source.FirstNameWrites);
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
            source.Dispose();
        }
    }

    [Fact]
    public async Task WhenReloadIsSupersededWhileWriteLands_ThenNothingIsSentBeforeNewerReloadIsReconciled()
    {
        // Arrange
        var (person, source) = await StartConnectedAsync(echoWrites: true, bufferMilliseconds: 8);
        try
        {
            source.TransportUp = false;
            person.FirstName = "L1";
            await WaitForParkedAsync(person, source);
            source.SetServerValue(nameof(Person.FirstName), "S1");

            // Act: the reconnect's load stalls, the connection drops again (a newer generation), and a
            // local write lands while the transport is up but nothing has been reconciled.
            var loadGate = source.BlockNextLoad();
            source.SignalReconnect();
            await source.LoadBlocked.Task.WaitAsync(EventTimeout);
            source.StartBufferingAgain();
            person.LastName = "W2";
            var parkedBoth = await SettlesAsync(() => source.Diagnostics.OutboundRetries.Depth == 2, EventTimeout);
            loadGate.SetResult();

            source.SignalReconnect();
            await AsyncTestHelpers.WaitUntilAsync(() => source.ReconnectCount == 2,
                timeout: EventTimeout, message: "The newer reconnect did not complete.");

            // Assert
            Assert.True(parkedBoth, "The write made before the newer reload was not parked. " + Describe(person, source));
            var settled = await SettlesAsync(() =>
                person.FirstName == "L1" && person.LastName == "W2" &&
                Equals(source.GetServerValue(nameof(Person.FirstName)), "L1") &&
                Equals(source.GetServerValue(nameof(Person.LastName)), "W2") &&
                source.Diagnostics.OutboundRetries.Depth == 0);
            Assert.True(settled, Describe(person, source));
            Assert.All(source.FirstNameWrites, entry => Assert.Equal("FirstName=L1 (model=L1)", entry));
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
            source.Dispose();
        }
    }

    [Theory]
    [InlineData(8)]
    [InlineData(250)]
    [InlineData(0)]
    public async Task WhenWriteCommitsDuringReloadBeforeLoadApplies_ThenModelAndSourceEndAtWrite(int bufferMilliseconds)
    {
        // Arrange
        var (person, source) = await StartConnectedAsync(echoWrites: false, bufferMilliseconds);
        try
        {
            source.SetServerValue(nameof(Person.FirstName), "S1");
            source.BeforeLoadReturns = () => person.FirstName = "W";

            // Act
            source.SignalReconnect();
            await source.ReconnectCompleted.Task.WaitAsync(EventTimeout);

            // Assert
            var settled = await SettlesAsync(() =>
                person.FirstName == "W" &&
                Equals(source.GetServerValue(nameof(Person.FirstName)), "W") &&
                source.Diagnostics.OutboundRetries.Depth == 0);
            Assert.True(settled, Describe(person, source));
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
            source.Dispose();
        }
    }

    [Theory]
    [InlineData(8)]
    [InlineData(250)]
    [InlineData(0)]
    public async Task WhenWriteCommitsAfterLoadAppliedBeforeReconcile_ThenModelAndSourceEndAtWrite(int bufferMilliseconds)
    {
        // Arrange
        var (person, source) = await StartConnectedAsync(echoWrites: false, bufferMilliseconds, LoadStyle.Read);
        try
        {
            source.SetServerValue(nameof(Person.FirstName), "S1");
            source.AfterLoadApplied = () => person.FirstName = "W";

            // Act
            source.SignalReconnect();
            await source.ReconnectCompleted.Task.WaitAsync(EventTimeout);

            // Assert
            var settled = await SettlesAsync(() =>
                person.FirstName == "W" &&
                Equals(source.GetServerValue(nameof(Person.FirstName)), "W") &&
                source.Diagnostics.OutboundRetries.Depth == 0);
            Assert.True(settled, Describe(person, source));
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
            source.Dispose();
        }
    }

    [Theory]
    [InlineData(8)]
    [InlineData(250)]
    public async Task WhenWriteCommitsJustBeforeReload_ThenItIsParkedAndJudged(int bufferMilliseconds)
    {
        // Arrange
        var (person, source) = await StartConnectedAsync(echoWrites: false, bufferMilliseconds);
        try
        {
            source.SetServerValue(nameof(Person.FirstName), "S1");

            // Act: the write is still in the processor's buffer when the reload starts and completes.
            person.FirstName = "W";
            source.SignalReconnect();
            await source.ReconnectCompleted.Task.WaitAsync(EventTimeout);

            // Assert: the write reached the source after the reconcile restored it, never while the model
            // held the loaded value.
            var settled = await SettlesAsync(() =>
                person.FirstName == "W" &&
                Equals(source.GetServerValue(nameof(Person.FirstName)), "W") &&
                source.Diagnostics.OutboundRetries.Depth == 0);
            Assert.True(settled, Describe(person, source));
            Assert.Equal(["FirstName=W (model=W)"], source.FirstNameWrites);
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
            source.Dispose();
        }
    }

    [Theory]
    [InlineData(LoadStyle.Read)]
    [InlineData(LoadStyle.Retained)]
    public async Task WhenWriteLandsBeforeOwnershipIsEstablishedOnFirstConnect_ThenItIsKeptAndReconciled(LoadStyle loadStyle)
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry().WithFullPropertyTracking();
        var person = new Person(context);
        var source = new ReconnectingSource(person, context, echoWrites: false, TimeSpan.FromMilliseconds(8), loadStyle);
        source.SetServerValue(nameof(Person.FirstName), "S0");
        source.SetServerValue(nameof(Person.LastName), "S0");
        var ownershipEstablished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.FirstListenHook = () =>
        {
            // The browse: the write commits before the source claims the property.
            person.FirstName = "W";
            new PropertyReference(person, nameof(Person.FirstName)).SetSource(source);
            new PropertyReference(person, nameof(Person.LastName)).SetSource(source);
            ownershipEstablished.TrySetResult();
            return Task.CompletedTask;
        };

        try
        {
            // Act
            await source.StartAsync(CancellationToken.None);
            await ownershipEstablished.Task.WaitAsync(EventTimeout);
            await AsyncTestHelpers.WaitUntilAsync(() => source.State == SourceState.Synchronized,
                timeout: EventTimeout, message: "The first connect did not complete.");

            // Assert
            var settled = await SettlesAsync(() =>
                person.FirstName == "W" &&
                Equals(source.GetServerValue(nameof(Person.FirstName)), "W"));
            Assert.True(settled, Describe(person, source));
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
            source.Dispose();
        }
    }

    [Fact]
    public async Task WhenSourceStopsWhileResyncIsPending_ThenParkedWriteIsCountedAsDroppedAndNotSentUnjudged()
    {
        // Arrange
        var (person, source) = await StartConnectedAsync(echoWrites: true, bufferMilliseconds: 8);
        source.TransportUp = false;
        person.FirstName = "L1";
        await WaitForParkedAsync(person, source);
        source.SetServerValue(nameof(Person.FirstName), "S1");

        source.BlockNextLoad();
        source.SignalReconnect();
        await source.LoadBlocked.Task.WaitAsync(EventTimeout);
        var droppedBefore = source.Diagnostics.OutboundRetries.TotalDropped;

        // Act
        await source.StopAsync(CancellationToken.None);

        // Assert
        Assert.Empty(source.FirstNameWrites);
        Assert.True(source.Diagnostics.OutboundRetries.TotalDropped > droppedBefore,
            "The parked write was neither delivered nor counted as dropped. " + Describe(person, source));
        source.Dispose();
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(6500)]
    public async Task WhenTransportWriteStallsAcrossInternalReconnect_ThenBufferedWriteIsReconciled(int stallMilliseconds)
    {
        // Arrange: a write to one property stalls in the transport while a reload runs and a write to another
        // property commits behind it in the processor's buffer.
        var (person, source) = await StartConnectedAsync(echoWrites: false, bufferMilliseconds: 8);
        try
        {
            var acknowledgement = source.StallWrite(nameof(Person.LastName), "X");
            person.LastName = "X";
            await source.WriteStalled.Task.WaitAsync(EventTimeout);

            source.SetServerValue(nameof(Person.FirstName), "S1");
            var loadReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            source.BeforeLoadReturns = () =>
            {
                person.FirstName = "W";
                loadReturned.TrySetResult();
            };

            // Act
            source.SignalReconnect();
            await loadReturned.Task.WaitAsync(EventTimeout);

            // The stall's length is the scenario: 6.5 s outlasts the processor's teardown bound, which must not
            // apply to a reload. Only elapsed time can show that the buffered write is not given up on.
            await Task.Delay(stallMilliseconds);
            acknowledgement.SetResult();
            await source.ReconnectCompleted.Task.WaitAsync(EventTimeout);

            // Assert
            var settled = await SettlesAsync(() =>
                person.FirstName == "W" &&
                Equals(source.GetServerValue(nameof(Person.FirstName)), "W") &&
                person.LastName == "X" &&
                Equals(source.GetServerValue(nameof(Person.LastName)), "X") &&
                source.Diagnostics.OutboundRetries.Depth == 0);
            Assert.True(settled, Describe(person, source));
            Assert.Equal(0, source.Diagnostics.OutboundChanges.TotalDropped);
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
            source.Dispose();
        }
    }

    [Fact]
    public async Task WhenReloadIsInvalidatedByConnectionLossThenRetried_ThenParkedWriteIsDeliveredOnce()
    {
        // Arrange
        var (person, source) = await StartConnectedAsync(echoWrites: true, bufferMilliseconds: 8);
        try
        {
            source.TransportUp = false;
            person.FirstName = "L1";
            await WaitForParkedAsync(person, source);
            source.SetServerValue(nameof(Person.FirstName), "S1");

            var loadGate = source.BlockNextLoad();
            source.SignalReconnect();
            await source.LoadBlocked.Task.WaitAsync(EventTimeout);

            // Act: the connection drops before the stalled load applies, so that load is discarded, then the
            // next reconnect succeeds.
            source.LoseConnection();
            loadGate.SetResult();
            source.SignalReconnect();
            await AsyncTestHelpers.WaitUntilAsync(() => source.ReconnectCount == 2,
                timeout: EventTimeout, message: "The reconnects did not complete.");

            // Assert
            var settled = await SettlesAsync(() =>
                person.FirstName == "L1" &&
                Equals(source.GetServerValue(nameof(Person.FirstName)), "L1") &&
                source.Diagnostics.OutboundRetries.Depth == 0);
            Assert.True(settled, Describe(person, source));
            Assert.Equal(["FirstName=L1 (model=L1)"], source.FirstNameWrites);
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
            source.Dispose();
        }
    }

    [Fact]
    public async Task WhenUnownedWritesBurstDuringPendingReload_ThenParkedOwnedWriteSurvives()
    {
        // Arrange: writes to subjects this source does not own never belonged in its bounded retry queue.
        var (person, source) = await StartConnectedAsync(echoWrites: true, bufferMilliseconds: 8, writeRetryQueueSize: 5);
        var unowned = Enumerable.Range(0, 20).Select(_ => new Person(((IInterceptorSubject)person).Context)).ToArray();
        try
        {
            source.TransportUp = false;
            person.FirstName = "L1";
            await WaitForParkedAsync(person, source);
            source.SetServerValue(nameof(Person.FirstName), "S1");

            var loadGate = source.BlockNextLoad();
            source.SignalReconnect();
            await source.LoadBlocked.Task.WaitAsync(EventTimeout);

            // Act
            for (var i = 0; i < unowned.Length; i++)
            {
                unowned[i].FirstName = "U" + i;
            }

            loadGate.SetResult();
            await source.ReconnectCompleted.Task.WaitAsync(EventTimeout);

            // Assert
            var settled = await SettlesAsync(() =>
                person.FirstName == "L1" &&
                Equals(source.GetServerValue(nameof(Person.FirstName)), "L1") &&
                source.Diagnostics.OutboundRetries.Depth == 0);
            Assert.True(settled, Describe(person, source));
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
            source.Dispose();
        }
    }

    [Fact]
    public async Task WhenOwnedPropertyIsWrittenRepeatedlyDuringPendingReload_ThenOtherParkedWritesAreNotEvicted()
    {
        // Arrange: a property written repeatedly while a reload is pending has to cost one slot of the bounded
        // retry queue, or it evicts the parked writes of every other property before the reconcile sees them.
        // Immediate mode parks each write as it is dequeued, so the pacing below makes every write a park.
        var (person, source) = await StartConnectedAsync(echoWrites: false, bufferMilliseconds: 0, writeRetryQueueSize: 5);
        try
        {
            source.TransportUp = false;
            person.FirstName = "L1";
            await WaitForParkedAsync(person, source);
            source.SetServerValue(nameof(Person.FirstName), "S1");

            var loadGate = source.BlockNextLoad();
            source.SignalReconnect();
            await source.LoadBlocked.Task.WaitAsync(EventTimeout);

            // Act
            for (var i = 0; i < 50; i++)
            {
                person.LastName = "H" + i;
                await Task.Delay(1);
            }

            loadGate.SetResult();
            await source.ReconnectCompleted.Task.WaitAsync(EventTimeout);

            // Assert
            var settled = await SettlesAsync(() =>
                person.FirstName == "L1" &&
                Equals(source.GetServerValue(nameof(Person.FirstName)), "L1") &&
                person.LastName == "H49" &&
                Equals(source.GetServerValue(nameof(Person.LastName)), "H49") &&
                source.Diagnostics.OutboundRetries.Depth == 0);
            Assert.True(settled, Describe(person, source));
            Assert.Equal(0, source.Diagnostics.OutboundRetries.TotalDropped);
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
            source.Dispose();
        }
    }

    private static string Describe(Person person, ReconnectingSource source) =>
        $"model=({person.FirstName}, {person.LastName}), " +
        $"source=({source.GetServerValue(nameof(Person.FirstName))}, {source.GetServerValue(nameof(Person.LastName))}), " +
        $"retryDepth={source.Diagnostics.OutboundRetries.Depth}, retryDropped={source.Diagnostics.OutboundRetries.TotalDropped}, " +
        $"state={source.State}, writesToSource=[{string.Join(", ", source.WriteLog)}]";

    private static async Task<(Person Person, ReconnectingSource Source)> StartConnectedAsync(
        bool echoWrites, int bufferMilliseconds, LoadStyle loadStyle = LoadStyle.Retained, int writeRetryQueueSize = 1000)
    {
        var context = InterceptorSubjectContext.Create().WithRegistry().WithFullPropertyTracking();
        var person = new Person(context);
        var source = new ReconnectingSource(
            person, context, echoWrites, TimeSpan.FromMilliseconds(bufferMilliseconds), loadStyle, writeRetryQueueSize);
        source.SetServerValue(nameof(Person.FirstName), "S0");
        source.SetServerValue(nameof(Person.LastName), "S0");

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(source);
        new PropertyReference(person, nameof(Person.LastName)).SetSource(source);

        await source.StartAsync(CancellationToken.None);

        // Connected once a probe write reaches the source, and quiet once the last probe has as well.
        var probe = 0;
        await AsyncTestHelpers.WaitUntilAsync(() =>
        {
            person.LastName = "P" + probe++;
            return source.WriteLog.Any(entry => entry.StartsWith("LastName=P", StringComparison.Ordinal));
        }, timeout: EventTimeout, message: "The change processor did not start.");

        await AsyncTestHelpers.WaitUntilAsync(
            () => person.FirstName == "S0" && source.State == SourceState.Synchronized &&
                  Equals(source.GetServerValue(nameof(Person.LastName)), person.LastName),
            timeout: EventTimeout,
            message: "The initial connect did not settle.");

        return (person, source);
    }

    private static async Task WaitForParkedAsync(Person person, ReconnectingSource source)
    {
        var parked = await SettlesAsync(() => source.Diagnostics.OutboundRetries.Depth == 1, EventTimeout);
        Assert.True(parked, "The failed write was not parked. " + Describe(person, source));
    }

    /// <summary>
    /// Waits for <paramref name="condition"/> within <paramref name="bound"/> and reports whether it held, so
    /// the caller can fail with the state at that moment.
    /// </summary>
    private static async Task<bool> SettlesAsync(Func<bool> condition, TimeSpan? bound = null)
    {
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(condition, timeout: bound ?? SettleBound);
            return true;
        }
        catch (TimeoutException)
        {
            return condition();
        }
    }

    /// <summary>
    /// Mirrors the MQTT client source: writes fail while the transport is down, and a monitor task reconnects
    /// internally with StartBuffering, connect, redeliver retained values and LoadInitialStateAndResumeAsync.
    /// With <see cref="LoadStyle.Read"/> the load applies the values itself, as the OPC UA client does.
    /// </summary>
    private sealed class ReconnectingSource : SubjectSourceBase
    {
        private readonly Person _subject;
        private readonly bool _echoWrites;
        private readonly LoadStyle _loadStyle;
        private readonly Lock _gate = new();
        private readonly Dictionary<string, object?> _server = [];
        private readonly List<string> _writeLog = [];
        private readonly SemaphoreSlim _reconnectSignal = new(0);
        private SubjectPropertyWriter? _propertyWriter;
        private volatile bool _transportUp;
        private int _reconnectCount;
        private int _listenCount;
        private TaskCompletionSource? _loadGate;
        private (string Property, object? Value, TaskCompletionSource Acknowledgement)? _stall;

        public ReconnectingSource(
            Person subject,
            IInterceptorSubjectContext context,
            bool echoWrites,
            TimeSpan bufferTime,
            LoadStyle loadStyle,
            int writeRetryQueueSize = 1000)
            : base(context, NullLogger.Instance, bufferTime: bufferTime, writeRetryQueueSize: writeRetryQueueSize)
        {
            _subject = subject;
            _echoWrites = echoWrites;
            _loadStyle = loadStyle;
        }

        public override IInterceptorSubject RootSubject => _subject;

        public TaskCompletionSource ReconnectCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource LoadBlocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource WriteStalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Runs inside the first <see cref="StartListeningAsync"/>, before ownership is established.</summary>
        public Func<Task>? FirstListenHook { get; set; }

        /// <summary>Runs inside the next load, before it returns.</summary>
        public Action? BeforeLoadReturns { get; set; }

        /// <summary>Runs inside the next load's apply action, after the loaded values were applied.</summary>
        public Action? AfterLoadApplied { get; set; }

        public bool TransportUp
        {
            get => _transportUp;
            set => _transportUp = value;
        }

        public int ReconnectCount => Volatile.Read(ref _reconnectCount);

        public IReadOnlyList<string> WriteLog
        {
            get
            {
                lock (_gate)
                {
                    return _writeLog.ToArray();
                }
            }
        }

        public string[] FirstNameWrites =>
            WriteLog.Where(entry => entry.StartsWith("FirstName=", StringComparison.Ordinal)).ToArray();

        public void SetServerValue(string propertyName, object? value)
        {
            lock (_gate)
            {
                _server[propertyName] = value;
            }
        }

        public object? GetServerValue(string propertyName)
        {
            lock (_gate)
            {
                return _server.GetValueOrDefault(propertyName);
            }
        }

        public void SignalReconnect() => _reconnectSignal.Release();

        public void LoseConnection() => ReportConnectionLost();

        /// <summary>A second outage detected while the first reconnect is still loading.</summary>
        public void StartBufferingAgain() => _propertyWriter!.StartBuffering();

        public TaskCompletionSource BlockNextLoad()
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _loadGate, gate);
            return gate;
        }

        /// <summary>
        /// Makes the next transport write of <paramref name="value"/> to <paramref name="propertyName"/> store the
        /// value and then wait for the returned acknowledgement, ignoring cancellation.
        /// </summary>
        public TaskCompletionSource StallWrite(string propertyName, object? value)
        {
            var acknowledgement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                _stall = (propertyName, value, acknowledgement);
            }

            return acknowledgement;
        }

        protected override async Task<IAsyncDisposable?> StartListeningAsync(
            SubjectPropertyWriter propertyWriter, CancellationToken cancellationToken)
        {
            _propertyWriter = propertyWriter;
            if (Interlocked.Increment(ref _listenCount) == 1 && FirstListenHook is { } hook)
            {
                await hook().ConfigureAwait(false);
            }

            _transportUp = true;
            if (_loadStyle == LoadStyle.Retained)
            {
                DeliverRetainedValues();
            }

            return BackgroundTaskLifetime.Start(cancellationToken, NullLogger.Instance, MonitorAsync);
        }

        private async Task MonitorAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await _reconnectSignal.WaitAsync(cancellationToken).ConfigureAwait(false);

                _propertyWriter!.StartBuffering();
                _transportUp = true;
                if (_loadStyle == LoadStyle.Retained)
                {
                    DeliverRetainedValues();
                }

                await _propertyWriter.LoadInitialStateAndResumeAsync(cancellationToken).ConfigureAwait(false);

                Interlocked.Increment(ref _reconnectCount);
                ReconnectCompleted.TrySetResult();
            }
        }

        private void DeliverRetainedValues()
        {
            KeyValuePair<string, object?>[] retained;
            lock (_gate)
            {
                retained = _server.ToArray();
            }

            foreach (var (propertyName, value) in retained)
            {
                Apply(propertyName, value);
            }
        }

        private void Apply(string propertyName, object? value)
        {
            _propertyWriter!.Write(
                (Source: this, Property: new PropertyReference(_subject, propertyName), Value: value),
                static state => state.Property.SetValueFromSource(state.Source, null, null, state.Value));
        }

        public override async Task<Action?> LoadInitialStateAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _loadGate, null) is { } gate)
            {
                LoadBlocked.TrySetResult();
                await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            KeyValuePair<string, object?>[] snapshot;
            lock (_gate)
            {
                snapshot = _server.ToArray();
            }

            var beforeReturn = BeforeLoadReturns;
            BeforeLoadReturns = null;
            beforeReturn?.Invoke();

            if (_loadStyle == LoadStyle.Retained)
            {
                return null;
            }

            var afterApplied = AfterLoadApplied;
            AfterLoadApplied = null;
            return () =>
            {
                foreach (var (propertyName, value) in snapshot)
                {
                    new PropertyReference(_subject, propertyName).SetValueFromSource(this, null, null, value);
                }

                afterApplied?.Invoke();
            };
        }

        public override async ValueTask<WriteResult> WriteChangesAsync(
            ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken)
        {
            if (!_transportUp)
            {
                return WriteResult.Failure(changes, new InvalidOperationException("Not connected."));
            }

            var written = changes.ToArray();
            TaskCompletionSource? acknowledgement = null;
            foreach (var change in written)
            {
                var propertyName = change.Property.Name;
                var value = change.GetNewValue<object?>();
                var modelValueAtSend = change.Property.Metadata.GetValue?.Invoke(change.Property.Subject);
                lock (_gate)
                {
                    _server[propertyName] = value;
                    _writeLog.Add($"{propertyName}={value} (model={modelValueAtSend})");
                    if (_stall is { } stall && stall.Property == propertyName && Equals(stall.Value, value))
                    {
                        _stall = null;
                        acknowledgement = stall.Acknowledgement;
                    }
                }
            }

            if (acknowledgement is not null)
            {
                WriteStalled.TrySetResult();
                await acknowledgement.Task.ConfigureAwait(false);
            }

            if (_echoWrites)
            {
                foreach (var change in written)
                {
                    Apply(change.Property.Name, change.GetNewValue<object?>());
                }
            }

            return WriteResult.Success;
        }
    }
}
