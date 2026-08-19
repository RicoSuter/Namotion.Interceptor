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
    public async Task WhenTheReconcileRestoresManyPropertiesWhileALiveProcessorIsRunning_ThenEveryRestoredValueReachesTheSourceOnce()
    {
        // Arrange: many owned properties, each parked under the gate and then moved off that value
        // with a source-tagged commit the way an initial-state load does, so the reconcile has to
        // restore every one of them. A real recovery runs the full interceptor chain per restore
        // (registry updates, tracking, any hooks), which is real wall-clock cost the bare property
        // model here has none of; the per-commit interceptor below stands in for that cost so the
        // reconcile reliably spans a flush tick instead of finishing inside a single one, which is
        // what stranded restores before this fix and what a real, slower model hits on its own.
        //
        // The cost is load-bearing and was measured, not assumed. With the fix removed and the
        // interceptor taken out, this passes in under half a second every time, because a bare
        // in-memory model finishes 50 restores inside one flush tick no matter how short that tick
        // is; shrinking the buffer time instead was tried across property counts from 200 to 50,000
        // and never reproduced the bug once. With the interceptor in place it detects a removed fix
        // about 17 times in 20, and passes 20 times in 20 with the fix present. That asymmetry is the
        // point: the race is real rather than scheduled, so detection is probabilistic, while a false
        // red is not. Treat an isolated failure here as a genuine regression, not as flake.
        const int propertyCount = 50;

        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithFullPropertyTracking();
        context.AddService<IWriteInterceptor>(new PerCommitLatencyInterceptor());

        var people = Enumerable.Range(0, propertyCount)
            .Select(i => new Person(context) { FirstName = $"Original{i}" })
            .ToList();

        var gate = new object();
        var receivedWrites = new List<string>();

        var source = new TestSubjectSource(people[0], context, NullLogger.Instance,
            bufferTime: TimeSpan.FromMilliseconds(1),
            writeRetryQueueSize: propertyCount * 2)
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

        foreach (var person in people)
        {
            new PropertyReference(person, nameof(Person.FirstName)).SetSource(source);
        }

        // Also owned, only so the probe below has a property whose flush is observable independently
        // of the hundred FirstName properties the test parks.
        new PropertyReference(people[0], nameof(Person.LastName)).SetSource(source);

        await source.StartAsync(CancellationToken.None);
        try
        {
            var probeValue = 0;
            await AsyncTestHelpers.WaitUntilAsync(() =>
            {
                people[0].LastName = "Probe" + probeValue++;
                lock (gate)
                {
                    return receivedWrites.Any(w => w.StartsWith("LastName=", StringComparison.Ordinal));
                }
            }, message: "Pump did not start processing changes.");

            // Act: park a write on every property, then move each one off the parked value with a
            // source-tagged commit, so every one hits the reconcile's restore branch.
            var resumeEpoch = source.BeginResumeForTest();
            var expectedWrites = new List<string>();
            for (var i = 0; i < propertyCount; i++)
            {
                var parkedValue = $"Parked{i}";
                expectedWrites.Add($"FirstName={parkedValue}");
                people[i].FirstName = parkedValue;
            }

            await AsyncTestHelpers.WaitUntilAsync(
                () => source.Diagnostics.OutboundRetries.Depth >= propertyCount,
                message: "Not every write was parked while the resume gate was set.");

            foreach (var person in people)
            {
                new PropertyReference(person, nameof(Person.FirstName))
                    .SetValueFromSource(source, null, null, "ServerChanged");
            }

            await source.CompleteResumeForTestAsync(resumeEpoch, CancellationToken.None);

            // Assert: every restored value reaches the source, and none of them arrives more than
            // once, which is what a write left stuck in the retry queue after being re-parked
            // mid-reconcile would miss.
            await AsyncTestHelpers.WaitUntilAsync(() =>
            {
                lock (gate)
                {
                    return expectedWrites.All(receivedWrites.Contains);
                }
            }, timeout: TimeSpan.FromSeconds(15),
                message: "Not every value restored by the reconcile reached the source.");

            lock (gate)
            {
                foreach (var expected in expectedWrites)
                {
                    Assert.Equal(1, receivedWrites.Count(w => w == expected));
                }
            }
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenTheGateIsHeldWhileStopping_ThenTheTeardownFlushStillAttemptsTheWrite()
    {
        // Arrange: a buffer time long enough that the periodic flush cannot fire during this test, so
        // the pending write can only reach the source through the teardown drain that runs when the
        // source stops, the way a connector's own reconnect can leave the gate set if a drop lands
        // right before the host shuts down.
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithFullPropertyTracking();

        var person = new Person(context);
        var gate = new object();
        var receivedWrites = new List<string>();

        var source = new TestSubjectSource(person, context, NullLogger.Instance,
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

        // Act: the gate is still up when the stop begins, so the pending write has to reach the
        // source through the teardown drain rather than being silently swallowed by it.
        source.BeginResumeForTest();
        person.FirstName = "StuckDuringShutdown";
        await AsyncTestHelpers.WaitUntilAsync(
            () => source.Diagnostics.OutboundChanges.Depth > depthBeforeRealWrite,
            message: "The write did not reach the processor's own buffer.");
        await source.StopAsync(CancellationToken.None);

        // Assert
        lock (gate)
        {
            Assert.Contains("FirstName=StuckDuringShutdown", receivedWrites);
        }
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
    /// Adds a small, real cost to every commit. Not a synchronization wait: nothing here waits for a
    /// condition. It stands in for the per-commit cost a real connected model has (registry updates,
    /// tracking, hooks) so a tight loop of bare property writes cannot finish faster than production
    /// code would, which is what makes the flush-tick race in the test above reachable at all.
    /// </summary>
    private sealed class PerCommitLatencyInterceptor : IWriteInterceptor
    {
        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            next(ref context);
            Thread.Sleep(1);
        }
    }
}
