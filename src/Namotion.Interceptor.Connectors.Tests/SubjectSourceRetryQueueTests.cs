using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Interceptors;
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
        // writes wholesale (error without enumerated failed changes) while the flag is set.
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithFullPropertyTracking();

        var person = new Person(context);

        var gate = new object();
        var failWholesale = false;
        var receivedWrites = new List<string>();

        var source = new TestSubjectSource(person, context, NullLogger.Instance,
            bufferTime: TimeSpan.FromMilliseconds(8))
        {
            WriteChangesOverride = (changes, _) =>
            {
                lock (gate)
                {
                    var batch = changes.ToArray();
                    if (failWholesale && batch.Any(change => change.Property.Name == nameof(Person.FirstName)))
                    {
                        return ValueTask.FromResult(WriteResult.Failure(
                            ReadOnlyMemory<SubjectPropertyChange>.Empty,
                            new InvalidOperationException("Wholesale boom")));
                    }

                    foreach (var change in batch)
                    {
                        receivedWrites.Add($"{change.Property.Name}={change.GetNewValue<object?>()}");
                    }
                    return ValueTask.FromResult(WriteResult.Success);
                }
            },
        };

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(source);
        new PropertyReference(person, nameof(Person.LastName)).SetSource(source);

        await source.StartAsync(CancellationToken.None);
        try
        {
            // Wait until the pump processes outbound changes. The probe is re-written on each
            // poll because writes enqueued before the pump's subscription exists are not seen.
            var probeValue = 0;
            await AsyncTestHelpers.WaitUntilAsync(() =>
            {
                person.LastName = "Probe" + probeValue++;
                return CountWrites(gate, receivedWrites, nameof(Person.LastName)) >= 1;
            }, message: "Pump did not start processing changes.");

            // Act: fail the FirstName write wholesale; the change must land in the retry queue.
            lock (gate)
            {
                failWholesale = true;
            }
            person.FirstName = "John";
            await AsyncTestHelpers.WaitUntilAsync(
                () => source.Diagnostics.OutboundRetries.Depth > 0,
                message: "Wholesale-failed write was not queued for retry.");

            // Recover the source; subsequent outbound writes flush the retry queue first.
            lock (gate)
            {
                failWholesale = false;
            }
            await AsyncTestHelpers.WaitUntilAsync(() =>
            {
                person.LastName = "Probe" + probeValue++;
                lock (gate)
                {
                    return receivedWrites.Contains("FirstName=John");
                }
            }, message: "Queued write was not retried after recovery.");
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenTheSourceIsIdleWithParkedWrites_ThenTheyAreFlushedWithoutAFurtherChange()
    {
        // Arrange: a short retryTime so the idle tick fires inside the test.
        var failFirstName = false;
        var (source, person, writes) = await StartPumpAsync(
            retryTime: TimeSpan.FromMilliseconds(200),
            failBatch: batch => Volatile.Read(ref failFirstName) &&
                batch.Any(change => change.Property.Name == nameof(Person.FirstName)));
        try
        {
            Volatile.Write(ref failFirstName, true);
            person.FirstName = "John";
            await AsyncTestHelpers.WaitUntilAsync(
                () => source.Diagnostics.OutboundRetries.Depth > 0,
                message: "The failed write was not parked.");

            // Act: let writes succeed again and then make no further change to any property.
            Volatile.Write(ref failFirstName, false);

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(
                () => writes.Contains("FirstName=John"),
                message: "The parked write was never drained while the model was idle.");
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenResumeIsInProgress_ThenOutboundWritesAreParkedInsteadOfSent()
    {
        // Arrange: every write succeeds, so anything not sent was parked by the gate.
        var (source, person, writes) = await StartPumpAsync();
        try
        {
            // Act
            await BeginResumeAndParkAsync(source, person);

            // Assert
            Assert.False(writes.Contains("FirstName=Parked"), "The write was sent while the resume gate was set.");
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenResumeCompletes_ThenTheParkedWriteAndALaterWriteReachTheSource()
    {
        // Arrange
        var (source, person, writes) = await StartPumpAsync();
        try
        {
            var resumeEpoch = await BeginResumeAndParkAsync(source, person);

            // Act
            await source.CompleteResumeForTestAsync(resumeEpoch, CancellationToken.None);
            person.LastName = "AfterResume";

            // Assert: the reconcile sends the parked write past the gate, so only the later write
            // proves the gate itself was cleared.
            await AsyncTestHelpers.WaitUntilAsync(
                () => writes.Contains("FirstName=Parked"),
                message: "The parked write was not sent after the resume completed.");
            await AsyncTestHelpers.WaitUntilAsync(
                () => writes.Contains("LastName=AfterResume"),
                message: "A write made after the resume completed should reach the source, proving the gate was cleared.");
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenAnOlderResumeCompletesAfterANewerOneHasStarted_ThenItNeitherClearsTheGateNorSendsTheParkedWrites()
    {
        // Arrange: a newer resume takes the gate over before the older one completes, the way a
        // connector's own reconnect loop can while the attempt loop's load is still in flight. The
        // newer resume has not loaded the peer's state yet, so the parked write cannot be judged.
        var (source, person, writes) = await StartPumpAsync();
        try
        {
            var olderEpoch = await BeginResumeAndParkAsync(source, person);
            var newerEpoch = source.BeginResumeForTest();

            // Act
            await source.CompleteResumeForTestAsync(olderEpoch, CancellationToken.None);

            // Assert: the reconcile sends inline, so a write the older resume flushed would already be recorded.
            Assert.False(writes.Contains("FirstName=Parked"), "The older resume sent a write it could not judge.");
            Assert.True(source.Diagnostics.OutboundRetries.Depth > 0,
                "The parked write must stay queued for the resume that owns the gate.");
            Assert.True(source.IsResumeGateHeld, "The older resume cleared the gate the newer one owns.");

            await source.CompleteResumeForTestAsync(newerEpoch, CancellationToken.None);
            await AsyncTestHelpers.WaitUntilAsync(
                () => writes.Contains("FirstName=Parked"),
                message: "The parked write should reach the source once the owning resume completes.");
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenTheGateIsAlreadyOpen_ThenACompletingResumeStillJudgesTheParkedWrites()
    {
        // Arrange: the attempt loop holds a resume while the connector's reconnect loop opens and then
        // abandons a second one, so the gate is open and only the attempt loop can judge what it parked.
        var (source, person, writes) = await StartPumpAsync();
        try
        {
            var attemptEpoch = await BeginResumeAndParkAsync(source, person);

            var reconnectEpoch = source.BeginResumeForTest();
            Assert.True(source.TryEndResumeForTest(reconnectEpoch));
            Assert.False(source.IsResumeGateHeld);

            // Act
            await source.CompleteResumeForTestAsync(attemptEpoch, CancellationToken.None);

            // Assert: the reconcile sends inline, so the idle drain cannot be what delivered it.
            Assert.True(writes.Contains("FirstName=Parked"), "The completing resume did not judge the parked write.");
            Assert.Equal(0, source.Diagnostics.OutboundRetries.Depth);
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenTwoResumesOpenAtOnce_ThenTheNewerOneOwnsTheGate()
    {
        // Arrange: probabilistic, so it can miss the interleaving but never fails on correct code.
        using var source = CreateSource();
        const int iterations = 20_000;
        var second = 0;

        using var barrier = new Barrier(2);
        var opener = RaceOnDedicatedThreadAsync(barrier, iterations,
            () => Volatile.Write(ref second, source.BeginResumeForTest()));

        for (var iteration = 0; iteration < iterations; iteration++)
        {
            // Act
            barrier.SignalAndWait();
            var first = source.BeginResumeForTest();
            barrier.SignalAndWait();

            // Assert
            var newer = Volatile.Read(ref second);
            Assert.True(first != newer,
                $"Two concurrent resumes were handed the same epoch {first} (iteration {iteration}).");
            Assert.True(source.TryEndResumeForTest(Math.Max(first, newer)),
                $"The older of two concurrent resumes ended up owning the gate (iteration {iteration}).");
        }

        await opener;
    }

    [Fact]
    public void WhenTheEpochIsZero_ThenEndingTheResumeReportsNoOwnership()
    {
        // Arrange: zero is what a connector's reconnect loop carries before its first BeginResume.
        using var source = CreateSource();

        // Act & Assert
        Assert.False(source.TryEndResumeForTest(0));

        var epoch = source.BeginResumeForTest();
        Assert.False(source.TryEndResumeForTest(0));
        Assert.True(source.TryEndResumeForTest(epoch));
    }

    [Fact]
    public async Task WhenAResumeEndsWhileANewerOneIsStarting_ThenItNeverClearsTheNewerResumesGate()
    {
        // Arrange: probabilistic, so it can miss the interleaving but never fails on correct code.
        using var source = CreateSource();
        const int iterations = 20_000;
        var olderEpoch = 0;

        using var barrier = new Barrier(3);
        var ender = RaceOnDedicatedThreadAsync(barrier, iterations,
            () => source.TryEndResumeForTest(Volatile.Read(ref olderEpoch)));
        var beginner = RaceOnDedicatedThreadAsync(barrier, iterations,
            () => source.BeginResumeForTest());

        for (var iteration = 0; iteration < iterations; iteration++)
        {
            // Act
            Volatile.Write(ref olderEpoch, source.BeginResumeForTest());
            barrier.SignalAndWait();
            barrier.SignalAndWait();

            // Assert: the newer BeginResume is the last write to the gate in this iteration, so an open
            // gate here can only mean the older resume cleared one it no longer owned.
            Assert.True(source.IsResumeGateHeld,
                $"An older resume cleared the gate a concurrent BeginResume had taken (iteration {iteration}).");
        }

        await Task.WhenAll(ender, beginner);
    }

    [Fact]
    public async Task WhenTheGateClearsBetweenTheEnqueueAndTheSecondRead_ThenTheSelfHealSendsTheParkedWriteWithNoFurtherWrite()
    {
        // Arrange: the hook runs between the enqueue and the second gate read, so clearing the gate
        // there parks the write with no CompleteResumeAsync to come for its epoch.
        var (source, person, writes) = await StartPumpAsync();
        try
        {
            var resumeEpoch = source.BeginResumeForTest();
            var hookRan = 0;
            source.AfterResumeGateObserved = () =>
            {
                if (Interlocked.Exchange(ref hookRan, 1) == 0)
                {
                    source.TryEndResumeForTest(resumeEpoch);
                }
            };

            // Act: the only write, so only the self-heal can still deliver it.
            person.FirstName = "SelfHealed";

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(
                () => writes.Contains("FirstName=SelfHealed"),
                message: "The self-heal should have sent the write parked in the window between the gate read and the enqueue.");
        }
        finally
        {
            source.AfterResumeGateObserved = null;
            await source.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenTheReconcilesOwnRestoreIsReParkedWhileTheGateIsStillHeld_ThenTheSecondPassRescuesIt()
    {
        // Arrange: FirstName is parked and then moved off by a source write, as a load would, so the
        // reconcile restores it. The interceptor lets that restore commit and then blocks it, so the gate
        // is still held when the processor parks the restore back into the retry queue.
        var restoreGate = new BlockNextCommitInterceptor();
        var (source, person, writes) = await StartPumpAsync(
            configureContext: context => context.AddService<IWriteInterceptor>(restoreGate));
        try
        {
            var resumeEpoch = await BeginResumeAndParkAsync(source, person);
            new PropertyReference(person, nameof(Person.FirstName))
                .SetValueFromSource(source, null, null, "ServerChanged");

            // Act: on its own thread, because the armed restore blocks before CompleteResumeAsync first yields.
            restoreGate.Arm();
            var completeTask = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(
                () => source.CompleteResumeForTestAsync(resumeEpoch, CancellationToken.None));
            await restoreGate.Entered.WaitAsync(TimeSpan.FromSeconds(10));

            await AsyncTestHelpers.WaitUntilAsync(
                () => source.Diagnostics.OutboundRetries.Depth > 0,
                message: "The processor should have parked the restore back into the retry queue while the reconcile was still blocked inside it.");

            restoreGate.Release();
            await completeTask;

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(
                () => writes.Contains("FirstName=Parked"),
                message: "The restored value should have reached the source through the second reconcile pass.");
        }
        finally
        {
            restoreGate.Release();
            await source.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenParkingWithInsertAtFront_ThenTheParkedEntriesPrecedeAnyAlreadyQueued()
    {
        // Arrange - stands in for the re-park after a reconnect: the in-flight entries being re-parked
        // predate everything the gate already parked during the outage, so they must land ahead of it.
        var context = InterceptorSubjectContext.Create().WithRegistry().WithFullPropertyTracking();
        var person = new Person(context);
        var source = new TestSubjectSource(person, context, NullLogger.Instance);

        EnqueueRetryChange(source, person, nameof(Person.LastName), "OldLast", "QueuedDuringOutage");

        // Act
        source.ParkChangesForRetryForTest(
            [CreateChange(person, nameof(Person.FirstName), "OldFirst", "InFlightBeforeOutage")]);

        // Assert
        var drained = await source.WriteRetryQueue!.DrainForLocalReapplyAsync(CancellationToken.None);
        Assert.Equal(2, drained.Length);
        Assert.Equal(nameof(Person.FirstName), drained[0].Property.Name);
        Assert.Equal(nameof(Person.LastName), drained[1].Property.Name);
    }

    [Fact]
    public void WhenASubjectDetachesBeforeTheDrain_ThenItsDiscardedWritesAreCountedAndLogged()
    {
        // Arrange: a child subject owned by the source, written to and then detached from the graph.
        var logger = new RecordingLogger();
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithLifecycle()
            .WithFullPropertyTracking();

        var person = new Person(context);
        var source = new TestSubjectSource(person, context, logger);

        using var subscription = context.CreatePropertyChangeQueueSubscription();

        var child = new Person(context) { FirstName = "Child" };
        person.Father = child;

        // Ownership through the manager, not a raw SetSource: nothing clears a raw SetSource on
        // detach, so the change would take the owned branch, be parked, and never reach the branch
        // under test.
        using var ownership = new SourceOwnershipManager(source);
        ownership.ClaimSource(new PropertyReference(child, nameof(Person.FirstName)));

        child.FirstName = "WrittenBeforeDetach";
        person.Father = null;

        // Act
        source.DrainOwnedWritesToRetryQueue(subscription);

        // Assert
        Assert.Contains(logger.Warnings, warning => warning.Contains("detached", StringComparison.Ordinal));
    }

    [Fact]
    public void WhenANormalWriteToAnAttachedSubjectIsNotOwned_ThenNoWarningIsLogged()
    {
        // Arrange: a write to a subject that stays attached and was never owned by this source, the
        // everyday "not mine" discard that must not be mistaken for the detached case above.
        var logger = new RecordingLogger();
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithLifecycle()
            .WithFullPropertyTracking();

        var person = new Person(context);
        var source = new TestSubjectSource(person, context, logger);

        using var subscription = context.CreatePropertyChangeQueueSubscription();

        person.FirstName = "NeverOwned";

        // Act
        source.DrainOwnedWritesToRetryQueue(subscription);

        // Assert
        Assert.Empty(logger.Warnings);
    }

    private static void EnqueueRetryChange(TestSubjectSource source,
        IInterceptorSubject subject, string propertyName, string? oldValue, string? newValue) =>
        source.WriteRetryQueue!.Enqueue(new[] { CreateChange(subject, propertyName, oldValue, newValue) });

    private static SubjectPropertyChange CreateChange(
        IInterceptorSubject subject, string propertyName, string? oldValue, string? newValue) =>
        SubjectPropertyChange.Create(
            new PropertyReference(subject, propertyName),
            ChangeOrigin.Local,
            DateTimeOffset.UtcNow,
            null,
            oldValue,
            newValue);

    private static async Task<(TestSubjectSource Source, Person Person, WriteRecorder Writes)> StartPumpAsync(
        TimeSpan? retryTime = null,
        Func<SubjectPropertyChange[], bool>? failBatch = null,
        Action<IInterceptorSubjectContext>? configureContext = null)
    {
        var context = InterceptorSubjectContext.Create();

        // Before tracking, so an added write interceptor wraps PropertyChangeInterceptor, which publishes
        // to subscriptions on its own unwind.
        configureContext?.Invoke(context);
        context.WithRegistry().WithFullPropertyTracking();

        var person = new Person(context);
        var writes = new WriteRecorder();

        // The default outlasts every wait here, so the idle drain cannot deliver a write a test expects
        // only the gate logic to deliver.
        var source = new TestSubjectSource(person, context, NullLogger.Instance,
            bufferTime: TimeSpan.FromMilliseconds(8),
            retryTime: retryTime ?? TimeSpan.FromMinutes(1))
        {
            WriteChangesOverride = (changes, _) =>
            {
                var batch = changes.ToArray();
                if (failBatch?.Invoke(batch) == true)
                {
                    return ValueTask.FromResult(WriteResult.Failure(
                        ReadOnlyMemory<SubjectPropertyChange>.Empty,
                        new InvalidOperationException("Simulated failure")));
                }

                writes.Record(batch);
                return ValueTask.FromResult(WriteResult.Success);
            },
        };

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(source);
        new PropertyReference(person, nameof(Person.LastName)).SetSource(source);

        await source.StartAsync(CancellationToken.None);

        var probeValue = 0;
        await AsyncTestHelpers.WaitUntilAsync(() =>
        {
            person.LastName = "Probe" + probeValue++;
            return writes.Count(nameof(Person.LastName)) >= 1;
        }, message: "Pump did not start processing changes.");

        // The last probe can still be in flight, and a test that parks next would count it as its own
        // write. Zero is required on two consecutive polls, because a single read can land between the
        // processor's drain and its write handler.
        var previousDepth = -1;
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var depth = source.Diagnostics.OutboundChanges.Depth;
                var isSettled = depth == 0 && depth == previousDepth;
                previousDepth = depth;
                return isSettled;
            },
            message: "The processor's buffer did not settle at zero after the probe.");

        return (source, person, writes);
    }

    private static async Task<int> BeginResumeAndParkAsync(TestSubjectSource source, Person person)
    {
        var resumeEpoch = source.BeginResumeForTest();
        person.FirstName = "Parked";
        await AsyncTestHelpers.WaitUntilAsync(
            () => source.Diagnostics.OutboundRetries.Depth > 0,
            message: "The write was not parked while the resume gate was set.");

        return resumeEpoch;
    }

    private static TestSubjectSource CreateSource()
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithFullPropertyTracking();

        return new TestSubjectSource(new Person(context), context, NullLogger.Instance);
    }

    private static Task RaceOnDedicatedThreadAsync(Barrier barrier, int iterations, Action step) =>
        DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() =>
        {
            for (var iteration = 0; iteration < iterations; iteration++)
            {
                barrier.SignalAndWait();
                step();
                barrier.SignalAndWait();
            }
        });

    private static int CountWrites(object gate, List<string> writes, string propertyName)
    {
        lock (gate)
        {
            return writes.Count(write => write.StartsWith(propertyName + "=", StringComparison.Ordinal));
        }
    }

    private sealed class WriteRecorder
    {
        private readonly object _gate = new();
        private readonly List<string> _writes = [];

        public void Record(SubjectPropertyChange[] batch)
        {
            lock (_gate)
            {
                foreach (var change in batch)
                {
                    _writes.Add($"{change.Property.Name}={change.GetNewValue<object?>()}");
                }
            }
        }

        public bool Contains(string write)
        {
            lock (_gate)
            {
                return _writes.Contains(write);
            }
        }

        public int Count(string propertyName) => CountWrites(_gate, _writes, propertyName);
    }

    /// <summary>
    /// Once armed, blocks the next commit it sees after that commit has already run: <c>next</c> is
    /// called first, so the commit reaches the subject and the change subscription, and only then does
    /// the call sit until <see cref="Release"/>. Lets a test observe state that exists only while a
    /// commit has landed but the caller that made it has not yet returned.
    /// </summary>
    private sealed class BlockNextCommitInterceptor : IWriteInterceptor
    {
        private int _armed;
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes once the armed commit has run and the block has started.</summary>
        public Task Entered => _entered.Task;

        public void Arm() => Volatile.Write(ref _armed, 1);

        public void Release() => _release.TrySetResult();

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            if (Interlocked.CompareExchange(ref _armed, 0, 1) == 1)
            {
                next(ref context);
                _entered.TrySetResult();
                _release.Task.GetAwaiter().GetResult();
                return;
            }

            next(ref context);
        }
    }
}
