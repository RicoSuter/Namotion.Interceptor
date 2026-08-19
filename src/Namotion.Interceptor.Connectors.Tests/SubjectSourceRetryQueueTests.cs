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
    public async Task WhenResumeIsInProgress_ThenOutboundWritesAreParkedInsteadOfSent()
    {
        // Arrange: a live pump whose writes all succeed, so anything not sent was parked by the gate.
        var (source, person, gate, receivedWrites) = await StartPumpAsync();
        try
        {
            // Act
            source.BeginResumeForTest();
            person.FirstName = "Parked";
            await AsyncTestHelpers.WaitUntilAsync(
                () => source.Diagnostics.OutboundRetries.Depth > 0,
                message: "The write was not parked while the resume gate was set.");

            // Assert
            lock (gate)
            {
                Assert.DoesNotContain("FirstName=Parked", receivedWrites);
            }
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenResumeCompletes_ThenTheParkedWriteReachesTheSource()
    {
        // Arrange: a live pump with a FirstName write parked while the resume gate is set.
        var (source, person, gate, receivedWrites) = await StartPumpAsync();
        var resumeEpoch = source.BeginResumeForTest();
        person.FirstName = "Parked";
        await AsyncTestHelpers.WaitUntilAsync(
            () => source.Diagnostics.OutboundRetries.Depth > 0,
            message: "The write was not parked while the resume gate was set.");
        try
        {
            // Act
            await source.CompleteResumeForTestAsync(resumeEpoch, CancellationToken.None);

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(() =>
            {
                lock (gate)
                {
                    return receivedWrites.Contains("FirstName=Parked");
                }
            }, message: "The parked write was not sent after the resume completed.");
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenResumeCompletes_ThenANewWriteAfterwardsReachesTheSource()
    {
        // Arrange: the resume has already completed and its own parked write already reached the
        // source through the reconcile's direct send, which bypasses the gate entirely. Only a
        // distinct write made afterwards, through the normal gated path, can prove the gate itself
        // was cleared rather than left set.
        var (source, person, gate, receivedWrites) = await StartPumpAsync();
        var resumeEpoch = source.BeginResumeForTest();
        person.FirstName = "Parked";
        await AsyncTestHelpers.WaitUntilAsync(
            () => source.Diagnostics.OutboundRetries.Depth > 0,
            message: "The write was not parked while the resume gate was set.");
        await source.CompleteResumeForTestAsync(resumeEpoch, CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() =>
        {
            lock (gate)
            {
                return receivedWrites.Contains("FirstName=Parked");
            }
        }, message: "The parked write was not sent after the resume completed.");

        try
        {
            // Act
            person.LastName = "AfterResume";

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(() =>
            {
                lock (gate)
                {
                    return receivedWrites.Contains("LastName=AfterResume");
                }
            }, message: "A write made after the resume completed should reach the source, proving the gate was cleared.");
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenAnOlderResumeCompletesAfterANewerOneHasStarted_ThenItDoesNotClearTheNewerResumesGate()
    {
        // Arrange: a live pump. A second resume takes over before the first one completes, the way
        // the WebSocket client's own reconnect loop can open a new BeginResume while the attempt
        // loop's first load is still in flight for the same connect window.
        var (source, person, gate, receivedWrites) = await StartPumpAsync();
        try
        {
            // Act
            var olderEpoch = source.BeginResumeForTest();
            var newerEpoch = source.BeginResumeForTest();

            // The older resume completes first; its completion must not release the gate the newer
            // resume still owns.
            await source.CompleteResumeForTestAsync(olderEpoch, CancellationToken.None);

            var depthBeforeWrite = source.Diagnostics.OutboundRetries.Depth;
            person.FirstName = "StillHeld";

            // Assert: the write parks rather than reaching the source, proving the gate is still up.
            await AsyncTestHelpers.WaitUntilAsync(
                () => source.Diagnostics.OutboundRetries.Depth > depthBeforeWrite,
                message: "The write should have parked because the newer resume still owns the gate.");
            lock (gate)
            {
                Assert.DoesNotContain("FirstName=StillHeld", receivedWrites);
            }

            // The newer resume completing does clear the gate, so a further write goes through.
            await source.CompleteResumeForTestAsync(newerEpoch, CancellationToken.None);
            await AsyncTestHelpers.WaitUntilAsync(() =>
            {
                lock (gate)
                {
                    return receivedWrites.Contains("FirstName=StillHeld");
                }
            }, message: "The parked write should reach the source once the newer resume completes.");
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenTheGateClearsBetweenTheEnqueueAndTheSecondRead_ThenTheSelfHealSendsTheParkedWriteWithNoFurtherWrite()
    {
        // Arrange: a live pump with the resume gate held. AfterResumeGateObservedForTest fires between
        // the retry-queue enqueue and the second gate read inside WriteChangesViaRetryQueueAsync, which
        // has no other externally observable synchronization point, so clearing the gate from inside it
        // reproduces the TOCTOU window the self-heal exists for: the write parks while the gate still
        // reads as held, and by the time the write handler re-reads it, it has already cleared, with no
        // CompleteResumeAsync ever having run for this epoch.
        var (source, person, gate, receivedWrites) = await StartPumpAsync();
        var resumeEpoch = source.BeginResumeForTest();

        var hookRan = 0;
        source.AfterResumeGateObservedForTest = () =>
        {
            if (Interlocked.Exchange(ref hookRan, 1) == 0)
            {
                source.AbortResumeForTest(resumeEpoch);
            }
        };

        try
        {
            // Act: the only write in this test. Nothing else ever triggers the write handler again, so
            // only the self-heal inside the parked branch can still deliver it.
            person.FirstName = "SelfHealed";

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(() =>
            {
                lock (gate)
                {
                    return receivedWrites.Contains("FirstName=SelfHealed");
                }
            }, message: "The self-heal should have sent the write parked in the window between the gate read and the enqueue.");
        }
        finally
        {
            source.AfterResumeGateObservedForTest = null;
            await source.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenTheReconcilesOwnRestoreIsReParkedWhileTheGateIsStillHeld_ThenTheSecondPassRescuesIt()
    {
        // Arrange: one property parked under the gate, then moved off that parked value by a
        // source-tagged write, the way an initial-state load does, so the reconcile has to restore it.
        // A write interceptor lets the restore commit for real (next(ref context) runs first, so it
        // reaches the live processor's subscription), then blocks before returning, so the gate is
        // still provably held when the depth check below observes the processor parking the restore
        // right back into the retry queue. That is the exact strand the second reconcile pass exists
        // to rescue: without it, the restore is the only change, so no later flush tick calls the
        // write handler again and the value never arrives.
        //
        // Registered before WithFullPropertyTracking() so it sits outside PropertyChangeInterceptor in
        // the write chain: that interceptor enqueues to subscriptions on its own unwind, after its own
        // call to next() returns, so a gate positioned inside it would hold up the very publish this
        // test needs to observe while still blocked.
        var context = InterceptorSubjectContext.Create();

        var restoreGate = new BlockNextCommitInterceptor();
        context.AddService<IWriteInterceptor>(restoreGate);

        context.WithRegistry().WithFullPropertyTracking();

        var person = new Person(context);

        var gate = new object();
        var receivedWrites = new List<string>();

        var source = new TestSubjectSource(person, context, NullLogger.Instance,
            bufferTime: TimeSpan.FromMilliseconds(8))
        {
            WriteChangesOverride = (changes, _) =>
            {
                lock (gate)
                {
                    foreach (var change in changes.ToArray())
                    {
                        receivedWrites.Add($"{change.Property.Name}={change.GetNewValue<object?>()}");
                    }
                }

                return ValueTask.FromResult(WriteResult.Success);
            },
        };

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(source);
        new PropertyReference(person, nameof(Person.LastName)).SetSource(source);

        await source.StartAsync(CancellationToken.None);
        try
        {
            var probeValue = 0;
            await AsyncTestHelpers.WaitUntilAsync(() =>
            {
                person.LastName = "Probe" + probeValue++;
                lock (gate)
                {
                    return receivedWrites.Any(w => w.StartsWith("LastName=", StringComparison.Ordinal));
                }
            }, message: "Pump did not start processing changes.");

            // The probe loop above can write a value the pump has not flushed yet by the time it exits
            // (it only waits for the first one to land, not for its own last write). Draining here
            // keeps that leftover from later masking the fix under test: left parked, it would sit in
            // the retry queue alongside the write under test, the reconcile would classify it as
            // sendable because the model still holds it, and the reconcile's own send would flush the
            // whole queue, including the re-parked restore, before the gate is cleared, rescuing the
            // restore even without the second pass.
            //
            // The depth is required to hold at zero across two consecutive polls: a bare zero read can
            // land in the microseconds between a drain and the handler running, which is a false red
            // here rather than a false green, but the stronger check costs nothing.
            var previousDepth = -1;
            await AsyncTestHelpers.WaitUntilAsync(
                () =>
                {
                    var depth = source.Diagnostics.OutboundChanges.Depth;
                    var isSettled = depth == 0 && depth == previousDepth;
                    previousDepth = depth;
                    return isSettled;
                },
                message: "The processor's buffer did not settle at zero after the warmup probe.");

            // Act: park the write, move the model off it with a source-tagged commit so the reconcile
            // takes the restore branch, then arm the interceptor and start the resume without awaiting
            // it so the test can observe the park while the reconcile is still blocked inside it.
            var resumeEpoch = source.BeginResumeForTest();
            person.FirstName = "Parked";
            await AsyncTestHelpers.WaitUntilAsync(
                () => source.Diagnostics.OutboundRetries.Depth > 0,
                message: "The write was not parked while the resume gate was set.");

            new PropertyReference(person, nameof(Person.FirstName))
                .SetValueFromSource(source, null, null, "ServerChanged");

            // Task.Run so the interceptor's synchronous block runs on a pool thread: the restore
            // commit reaches the interceptor before any real await, so calling this inline would block
            // the test's own thread there, and it is that same thread the awaits below need to reach
            // restoreGate.Release().
            restoreGate.Arm();
            var completeTask = Task.Run(() => source.CompleteResumeForTestAsync(resumeEpoch, CancellationToken.None));
            await restoreGate.Entered.WaitAsync(TimeSpan.FromSeconds(10));

            await AsyncTestHelpers.WaitUntilAsync(
                () => source.Diagnostics.OutboundRetries.Depth > 0,
                message: "The processor should have parked the restore back into the retry queue while the reconcile was still blocked inside it.");

            restoreGate.Release();
            await completeTask;

            // Assert: the second pass rescues the re-parked restore.
            await AsyncTestHelpers.WaitUntilAsync(() =>
            {
                lock (gate)
                {
                    return receivedWrites.Contains("FirstName=Parked");
                }
            }, message: "The restored value should have reached the source through the second reconcile pass.");
        }
        finally
        {
            restoreGate.Release();
            await source.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenTheGateIsHeldWhileStopping_ThenThePendingWriteIsDiscardedAndCountedRatherThanParked()
    {
        // Arrange: a buffer time long enough that the periodic flush cannot fire during this test, so
        // the pending write can only be handled by the teardown flush that runs when the source stops,
        // the way a connector's own reconnect can leave the gate set if a drop lands right before the
        // host shuts down. Nothing comes back for a write parked at that point: the queue is disposed
        // with the source, and the connection it was captured against is being replaced, so the write
        // must be discarded and counted rather than silently parked or attempted.
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithFullPropertyTracking();

        var person = new Person(context);
        var gate = new object();
        var receivedWrites = new List<string>();
        var recordingLogger = new RecordingLogger();

        var source = new TestSubjectSource(person, context, recordingLogger,
            bufferTime: TimeSpan.FromSeconds(30),
            teardownFlushTimeout: TimeSpan.FromSeconds(10))
        {
            WriteChangesOverride = (changes, _) =>
            {
                lock (gate)
                {
                    foreach (var change in changes.ToArray())
                    {
                        receivedWrites.Add($"{change.Property.Name}={change.GetNewValue<object?>()}");
                    }
                }

                return ValueTask.FromResult(WriteResult.Success);
            },
        };

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(source);
        new PropertyReference(person, nameof(Person.LastName)).SetSource(source);

        await source.StartAsync(CancellationToken.None);

        // The buffer time is long enough that nothing here is ever flushed, so readiness cannot be
        // observed through a received write the way the other tests in this file do it; re-probing a
        // different property until it lands in the processor's own buffer is the equivalent signal.
        var probeValue = 0;
        await AsyncTestHelpers.WaitUntilAsync(() =>
        {
            person.LastName = "Warmup" + probeValue++;
            return source.Diagnostics.OutboundChanges.Depth > 0;
        }, message: "The processor did not start buffering changes.");
        var depthBeforeRealWrite = source.Diagnostics.OutboundChanges.Depth;

        // Act: the gate is still up when the stop begins, so the teardown flush hits the gate branch
        // while stopping.
        source.BeginResumeForTest();
        person.FirstName = "StuckDuringShutdown";
        await AsyncTestHelpers.WaitUntilAsync(
            () => source.Diagnostics.OutboundChanges.Depth > depthBeforeRealWrite,
            message: "The write did not reach the processor's own buffer.");
        await source.StopAsync(CancellationToken.None);

        // Assert: never attempted, not parked, counted as a drop, and logged.
        lock (gate)
        {
            Assert.DoesNotContain("FirstName=StuckDuringShutdown", receivedWrites);
        }
        Assert.Equal(0, source.Diagnostics.OutboundRetries.Depth);
        Assert.True(source.Diagnostics.OutboundRetries.TotalDropped > 0);
        Assert.Contains(recordingLogger.Errors, message => message.Contains("Discarded", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WhenTheGateIsHeldWhileStoppingAndTheWriteWouldFail_ThenThePendingWriteIsStillDiscardedAndCountedRatherThanAttempted()
    {
        // Arrange: identical to the sibling test above, except WriteChangesOverride fails instead of
        // succeeding. The fix must never reach the write at all while stopping under the gate, so the
        // outcome, and whether WriteChangesOverride is invoked, must be the same either way.
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithFullPropertyTracking();

        var person = new Person(context);
        var recordingLogger = new RecordingLogger();
        var firstNameWriteAttempted = false;

        var source = new TestSubjectSource(person, context, recordingLogger,
            bufferTime: TimeSpan.FromSeconds(30),
            teardownFlushTimeout: TimeSpan.FromSeconds(10))
        {
            // Only FirstName fails: the connect-window reconcile can independently send a LastName
            // warmup probe before the test's own gate is even set (its send branch bypasses the gate
            // by design, see BeginResume's remarks), and that write must not pollute this test's
            // narrower claim about FirstName specifically.
            WriteChangesOverride = (changes, _) =>
            {
                var batch = changes.ToArray();
                if (batch.Any(change => change.Property.Name == nameof(Person.FirstName)))
                {
                    Volatile.Write(ref firstNameWriteAttempted, true);
                    return ValueTask.FromResult(WriteResult.Failure(
                        ReadOnlyMemory<SubjectPropertyChange>.Empty,
                        new InvalidOperationException("Simulated dead socket")));
                }

                return ValueTask.FromResult(WriteResult.Success);
            },
        };

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(source);
        new PropertyReference(person, nameof(Person.LastName)).SetSource(source);

        await source.StartAsync(CancellationToken.None);

        var probeValue = 0;
        await AsyncTestHelpers.WaitUntilAsync(() =>
        {
            person.LastName = "Warmup" + probeValue++;
            return source.Diagnostics.OutboundChanges.Depth > 0;
        }, message: "The processor did not start buffering changes.");
        var depthBeforeRealWrite = source.Diagnostics.OutboundChanges.Depth;

        // Act
        source.BeginResumeForTest();
        person.FirstName = "StuckDuringShutdown";
        await AsyncTestHelpers.WaitUntilAsync(
            () => source.Diagnostics.OutboundChanges.Depth > depthBeforeRealWrite,
            message: "The write did not reach the processor's own buffer.");
        await source.StopAsync(CancellationToken.None);

        // Assert: discarded before any attempt, so a failing transport never even gets asked.
        Assert.False(Volatile.Read(ref firstNameWriteAttempted));
        Assert.Equal(0, source.Diagnostics.OutboundRetries.Depth);
        Assert.True(source.Diagnostics.OutboundRetries.TotalDropped > 0);
        Assert.Contains(recordingLogger.Errors, message => message.Contains("Discarded", StringComparison.Ordinal));
    }

    /// <summary>
    /// Starts a live pump with FirstName and LastName owned by the source, and waits until it is
    /// processing outbound changes. Pure arrangement: callers drive the resume gate themselves.
    /// </summary>
    private static async Task<(TestSubjectSource Source, Person Person, object Gate, List<string> ReceivedWrites)>
        StartPumpAsync()
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithFullPropertyTracking();

        var person = new Person(context);

        var gate = new object();
        var receivedWrites = new List<string>();

        var source = new TestSubjectSource(person, context, NullLogger.Instance,
            bufferTime: TimeSpan.FromMilliseconds(8))
        {
            WriteChangesOverride = (changes, _) =>
            {
                lock (gate)
                {
                    foreach (var change in changes.ToArray())
                    {
                        receivedWrites.Add($"{change.Property.Name}={change.GetNewValue<object?>()}");
                    }
                }

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
            return CountWrites(gate, receivedWrites, nameof(Person.LastName)) >= 1;
        }, message: "Pump did not start processing changes.");

        return (source, person, gate, receivedWrites);
    }

    private static int CountWrites(object gate, List<string> writes, string propertyName)
    {
        lock (gate)
        {
            return writes.Count(write => write.StartsWith(propertyName + "=", StringComparison.Ordinal));
        }
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
