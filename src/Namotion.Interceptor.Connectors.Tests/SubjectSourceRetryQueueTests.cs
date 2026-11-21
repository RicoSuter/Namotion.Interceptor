using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Connectors.Monitoring;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests;

public class SubjectSourceRetryQueueTests
{
    [Fact]
    public async Task WhenWriteFailsWithoutEnumeratedFailedChanges_ThenChangesAreQueuedAndRetried()
    {
        // Arrange: real context with a running SubjectSourceBase pump; the source fails FirstName
        // writes wholesale (error without enumerated failed changes) while the flag is set. The long retry
        // time leaves the retry to the next write rather than the idle flush.
        var recorder = new RecordingSource(retryTime: TimeSpan.FromMinutes(1));

        await recorder.Source.StartAsync(CancellationToken.None);
        try
        {
            await recorder.WaitForPumpAsync();

            // Act: fail the FirstName write wholesale; the change must land in the retry queue.
            await recorder.ParkFirstNameWriteAsync("John");

            // Recover the source; subsequent outbound writes flush the retry queue first.
            recorder.FailFirstName = false;
            var recoveryValue = 0;
            await AsyncTestHelpers.WaitUntilAsync(() =>
            {
                recorder.Person.LastName = "Recovery" + recoveryValue++;
                return recorder.HasReceived("FirstName=John");
            }, message: "Queued write was not retried after recovery.");
        }
        finally
        {
            await recorder.Source.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenTheSourceIsIdleWithAParkedWrite_ThenTheIntervalSendsItWithoutAFurtherChange()
    {
        // Arrange
        var recorder = new RecordingSource(retryTime: TimeSpan.FromMilliseconds(200));

        await recorder.Source.StartAsync(CancellationToken.None);
        try
        {
            await recorder.WaitForPumpAsync();
            await recorder.ParkFirstNameWriteAsync("John");

            // Act: let writes succeed again and make no further change to any property.
            recorder.FailFirstName = false;

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(
                () => recorder.HasReceived("FirstName=John"),
                message: "The parked write was not sent while the model was idle.");
        }
        finally
        {
            await recorder.Source.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenAReloadInsideTheConnectorMovesTheModelOffAParkedWrite_ThenTheWriteIsRestoredAndSent()
    {
        // Arrange: a peer that holds FirstName and never reads a write back, so only the reconcile can bring the
        // model back to the parked write. The long retry time leaves it to the wake on Synchronized.
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithFullPropertyTracking();

        var person = new Person(context);
        var firstName = new PropertyReference(person, nameof(Person.FirstName));

        var gate = new object();
        var peerIsDown = false;
        var peerFirstName = "Initial";
        var events = new List<string>();
        var probeWriteCount = 0;
        SubjectPropertyWriter? propertyWriter = null;

        TestSubjectSource source = null!;
        source = new TestSubjectSource(person, context, NullLogger.Instance,
            bufferTime: TimeSpan.FromMilliseconds(8),
            retryTime: TimeSpan.FromMinutes(1))
        {
            StartListeningOverride = (writer, _) =>
            {
                propertyWriter = writer;
                return Task.FromResult<IAsyncDisposable?>(null);
            },
            LoadInitialStateOverride = _ =>
            {
                string loadedValue;
                lock (gate)
                {
                    loadedValue = peerFirstName;
                }

                return Task.FromResult<Action?>(() =>
                {
                    firstName.SetValueFromSource(source, null, null, loadedValue);
                    lock (gate)
                    {
                        events.Add("Load=" + loadedValue);
                    }
                });
            },
            WriteChangesOverride = (changes, _) =>
            {
                lock (gate)
                {
                    foreach (var change in changes.Span)
                    {
                        if (change.Property.Name != nameof(Person.FirstName))
                        {
                            probeWriteCount++;
                            continue;
                        }

                        if (peerIsDown)
                        {
                            return ValueTask.FromResult(WriteResult.Failure(changes, new InvalidOperationException("Peer is down")));
                        }

                        peerFirstName = change.GetNewValue<string?>()!;
                        events.Add("Write=" + peerFirstName);
                    }
                }

                return ValueTask.FromResult(WriteResult.Success);
            },
        };

        firstName.SetSource(source);
        new PropertyReference(person, nameof(Person.LastName)).SetSource(source);

        await source.StartAsync(CancellationToken.None);
        try
        {
            // Waits until the pump processes changes, so the write below is parked by a failed outbound write.
            var probeValue = 0;
            await AsyncTestHelpers.WaitUntilAsync(() =>
            {
                person.LastName = "Probe" + probeValue++;
                lock (gate)
                {
                    return probeWriteCount >= 1;
                }
            }, message: "Pump did not start processing changes.");

            lock (gate)
            {
                peerIsDown = true;
            }
            person.FirstName = "Written";
            await AsyncTestHelpers.WaitUntilAsync(
                () => source.Diagnostics.OutboundRetries.Depth > 0,
                message: "The write made while the peer was down was not parked.");

            // Act: the peer comes back and the connector reloads its state, which moves the model off the
            // parked write. No further local write follows.
            lock (gate)
            {
                peerIsDown = false;
            }
            propertyWriter!.StartBuffering();
            await propertyWriter.LoadInitialStateAndResumeAsync(CancellationToken.None);

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(() =>
            {
                lock (gate)
                {
                    return person.FirstName == "Written" && peerFirstName == "Written";
                }
            }, message: "The parked write was not restored locally and sent after the reload.");

            lock (gate)
            {
                var reloadIndex = events.LastIndexOf("Load=Initial");
                Assert.True(reloadIndex > 0, "The reload did not apply the peer's older value.");
                Assert.Equal("Write=Written", events[reloadIndex + 1]);
            }
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenALocalWriteDuringAReloadSupersedesAParkedWrite_ThenTheParkedWriteIsDroppedNotSent()
    {
        // Arrange: the first load's local write is parked when the reconcile at connect fails to send it. The
        // long buffer time holds the reload's local write in the processor. Either the reconcile after the reload
        // or the flush skipping superseded writes drops the parked write; the restore test isolates the reconcile.
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithFullPropertyTracking();

        var person = new Person(context);

        var gate = new object();
        var peerIsDown = true;
        var loadCount = 0;
        var attemptedWrites = new List<string>();
        SubjectPropertyWriter? propertyWriter = null;

        using var source = new TestSubjectSource(person, context, NullLogger.Instance,
            bufferTime: TimeSpan.FromMinutes(5),
            retryTime: TimeSpan.FromMinutes(1))
        {
            StartListeningOverride = (writer, _) =>
            {
                propertyWriter = writer;
                return Task.FromResult<IAsyncDisposable?>(null);
            },
            LoadInitialStateOverride = _ =>
            {
                var value = Interlocked.Increment(ref loadCount) == 1 ? "Parked" : "Newer";
                return Task.FromResult<Action?>(() => person.FirstName = value);
            },
            WriteChangesOverride = (changes, _) =>
            {
                lock (gate)
                {
                    foreach (var change in changes.Span)
                    {
                        attemptedWrites.Add($"{change.Property.Name}={change.GetNewValue<object?>()}");
                    }

                    return ValueTask.FromResult(peerIsDown
                        ? WriteResult.Failure(changes, new InvalidOperationException("Peer is down"))
                        : WriteResult.Success);
                }
            },
        };

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(source);

        await source.StartAsync(CancellationToken.None);
        try
        {
            // Tried and failed by the reconcile at connect, which the idle flush's startup wake does not repeat.
            await AsyncTestHelpers.WaitUntilAsync(() =>
            {
                lock (gate)
                {
                    return attemptedWrites.Count == 1 && source.Diagnostics.OutboundRetries.Depth > 0;
                }
            }, message: "The write made during the first load was not parked.");

            // Act
            lock (gate)
            {
                peerIsDown = false;
            }
            propertyWriter!.StartBuffering();
            await propertyWriter.LoadInitialStateAndResumeAsync(CancellationToken.None);

            await AsyncTestHelpers.WaitUntilAsync(
                () => source.Diagnostics.OutboundRetries.Depth == 0,
                message: "The parked write was not taken out of the queue after the reload.");

            // Takes the flush gate, so it returns only after the reconcile has finished.
            await source.WriteRetryQueue.FlushAsync(source, CancellationToken.None);

            // Assert
            lock (gate)
            {
                Assert.Equal(["FirstName=Parked"], attemptedWrites);
            }
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenIdleFlushesKeepFailing_ThenTheIntervalBacksOffToEightTimesAndResetsOnSuccessAndWake()
    {
        // Arrange: the idle flush's timer is the only timer on the clock.
        var retryTime = TimeSpan.FromSeconds(1);
        var clock = new ManualTeardownClock();
        SubjectPropertyWriter? propertyWriter = null;
        var recorder = new RecordingSource(
            retryTime: retryTime,
            startListening: (writer, _) =>
            {
                propertyWriter = writer;
                return Task.FromResult<IAsyncDisposable?>(null);
            },
            idleFlushTimeProvider: clock);

        await recorder.Source.StartAsync(CancellationToken.None);
        try
        {
            await recorder.WaitForPumpAsync();
            await recorder.ParkFirstNameWriteAsync("John");
            await AsyncTestHelpers.WaitUntilAsync(
                () => clock.NextDueIn == retryTime,
                message: "The idle flush was not armed at the retry time.");

            // Act & Assert: each failed idle flush doubles the interval up to eight times the retry time.
            foreach (var factor in new[] { 2, 4, 8, 8 })
            {
                await FireAndWaitForIdleIntervalAsync(clock, retryTime * factor);
            }

            // A successful flush resets it.
            recorder.FailFirstName = false;
            await FireAndWaitForIdleIntervalAsync(clock, retryTime);
            Assert.True(recorder.HasReceived("FirstName=John"));

            // A wake on Synchronized resets it.
            await recorder.ParkFirstNameWriteAsync("Jane");
            await FireAndWaitForIdleIntervalAsync(clock, retryTime * 2);

            propertyWriter!.StartBuffering();
            await propertyWriter.LoadInitialStateAndResumeAsync(CancellationToken.None);
            await AsyncTestHelpers.WaitUntilAsync(
                () => clock.NextDueIn == retryTime,
                message: "A wake on Synchronized did not reset the interval.");
        }
        finally
        {
            await recorder.Source.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Fires the idle flush's armed timer and waits until the flush that follows has armed it again at
    /// <paramref name="interval"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="ManualTeardownClock.Advance"/> removes the fired timer before it returns, and only the idle
    /// flush arms it again, after its flush, so seeing <paramref name="interval"/> afterwards means that flush ran.
    /// </remarks>
    private static async Task FireAndWaitForIdleIntervalAsync(ManualTeardownClock clock, TimeSpan interval)
    {
        clock.Advance(clock.NextDueIn ?? throw new InvalidOperationException("The idle flush timer is not armed."));
        await AsyncTestHelpers.WaitUntilAsync(
            () => clock.NextDueIn == interval,
            message: $"The idle flush did not arm {interval} after its timer fired.");
    }

    [Fact]
    public async Task WhenRetryTimeIsBelowOneMillisecond_ThenTheIdleFlushArmsNoTimerAndWritesAreStillDelivered()
    {
        // Arrange: the clock never advances, so a timer the idle flush arms stays visible.
        var clock = new ManualTeardownClock();
        var recorder = new RecordingSource(retryTime: TimeSpan.FromMicroseconds(500), idleFlushTimeProvider: clock);

        await recorder.Source.StartAsync(CancellationToken.None);
        try
        {
            await recorder.WaitForPumpAsync();

            // Act
            recorder.Person.FirstName = "John";

            // Assert: delivered by the processor, which starts after the idle flush has set its timer.
            await AsyncTestHelpers.WaitUntilAsync(
                () => recorder.HasReceived("FirstName=John"),
                message: "The write was not delivered.");
            Assert.Null(clock.NextDueIn);
        }
        finally
        {
            await recorder.Source.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenAnOutboundBatchIsTheFirstFlushOfASynchronizedPeriod_ThenItReconcilesTheParkedWritesFirst()
    {
        // Arrange: a parked write that a reload moved the model off, with no idle flush running, so the flush
        // ahead of the next outbound batch is the first one of the new synchronized period.
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithFullPropertyTracking();

        var person = new Person(context);
        var firstName = new PropertyReference(person, nameof(Person.FirstName));
        var sentWrites = new List<string>();

        using var source = new TestSubjectSource(person, context, NullLogger.Instance)
        {
            WriteChangesOverride = (changes, _) =>
            {
                foreach (var change in changes.Span)
                {
                    sentWrites.Add($"{change.Property.Name}={change.GetNewValue<object?>()}");
                }

                return ValueTask.FromResult(WriteResult.Success);
            },
        };

        var parkedWrite = CaptureLocalWrite(context, nameof(Person.FirstName), () => person.FirstName = "Parked");
        var outboundBatch = CaptureLocalWrite(context, nameof(Person.LastName), () => person.LastName = "Batch");

        source.WriteRetryQueue.Enqueue(new[] { parkedWrite });
        firstName.SetValueFromSource(source, null, null, "Loaded");
        source.TransitionStateTo(SourceState.Synchronized);

        // Act
        await source.WriteRetryQueue.WriteAsync(source, new[] { outboundBatch }, CancellationToken.None);

        // Assert: restored locally for the processor to send, rather than sent as it is, which would leave the
        // model on the loaded value for a peer that does not read the write back.
        Assert.Equal("Parked", person.FirstName);
        Assert.Equal(["LastName=Batch"], sentWrites);
        Assert.False(source.IsReconcileDue);
    }

    private static SubjectPropertyChange CaptureLocalWrite(IInterceptorSubjectContext context, string propertyName, Action write)
    {
        using var subscription = context.CreatePropertyChangeQueueSubscription();
        write();
        while (subscription.TryDequeueImmediate(out var change))
        {
            if (change.Property.Name == propertyName)
            {
                return change;
            }
        }

        throw new InvalidOperationException($"The write did not change {propertyName}.");
    }

    /// <summary>
    /// A source owning FirstName and LastName that records each delivered write as "Name=Value", and fails
    /// FirstName writes wholesale while <see cref="FailFirstName"/> is set.
    /// </summary>
    private sealed class RecordingSource
    {
        private readonly object _gate = new();
        private readonly List<string> _receivedWrites = [];
        private bool _failFirstName;

        public RecordingSource(
            TimeSpan? retryTime = null,
            Func<SubjectPropertyWriter, CancellationToken, Task<IAsyncDisposable?>>? startListening = null,
            TimeProvider? idleFlushTimeProvider = null)
        {
            var context = InterceptorSubjectContext
                .Create()
                .WithRegistry()
                .WithFullPropertyTracking();

            Person = new Person(context);
            Source = new TestSubjectSource(Person, context, NullLogger.Instance,
                bufferTime: TimeSpan.FromMilliseconds(8),
                retryTime: retryTime)
            {
                StartListeningOverride = startListening,
                IdleFlushTimeProvider = idleFlushTimeProvider ?? TimeProvider.System,
                WriteChangesOverride = (changes, _) =>
                {
                    lock (_gate)
                    {
                        var batch = changes.ToArray();
                        if (_failFirstName && batch.Any(change => change.Property.Name == nameof(Person.FirstName)))
                        {
                            return ValueTask.FromResult(WriteResult.Failure(
                                ReadOnlyMemory<SubjectPropertyChange>.Empty,
                                new InvalidOperationException("Wholesale boom")));
                        }

                        foreach (var change in batch)
                        {
                            _receivedWrites.Add($"{change.Property.Name}={change.GetNewValue<object?>()}");
                        }
                        return ValueTask.FromResult(WriteResult.Success);
                    }
                },
            };

            new PropertyReference(Person, nameof(Person.FirstName)).SetSource(Source);
            new PropertyReference(Person, nameof(Person.LastName)).SetSource(Source);
        }

        public Person Person { get; }

        public TestSubjectSource Source { get; }

        public bool FailFirstName
        {
            get
            {
                lock (_gate)
                {
                    return _failFirstName;
                }
            }
            set
            {
                lock (_gate)
                {
                    _failFirstName = value;
                }
            }
        }

        public bool HasReceived(string write)
        {
            lock (_gate)
            {
                return _receivedWrites.Contains(write);
            }
        }

        /// <summary>
        /// Waits until the pump delivers a LastName write. The probe is rewritten on each poll, because a write
        /// made before the source's subscription exists is not seen.
        /// </summary>
        public async Task WaitForPumpAsync()
        {
            var probeValue = 0;
            await AsyncTestHelpers.WaitUntilAsync(() =>
            {
                Person.LastName = "Probe" + probeValue++;
                lock (_gate)
                {
                    return _receivedWrites.Exists(write => write.StartsWith(nameof(Person.LastName) + "=", StringComparison.Ordinal));
                }
            }, message: "Pump did not start processing changes.");
        }

        /// <summary>
        /// Fails FirstName writes, writes <paramref name="value"/> to it and waits until the write is parked.
        /// </summary>
        public async Task ParkFirstNameWriteAsync(string value)
        {
            FailFirstName = true;
            Person.FirstName = value;
            await AsyncTestHelpers.WaitUntilAsync(
                () => Source.Diagnostics.OutboundRetries.Depth > 0,
                message: $"The failed write of {value} was not parked.");
        }
    }
}
