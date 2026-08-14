using System.Collections.Immutable;
using Microsoft.Extensions.Logging.Abstractions;
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
    public async Task WhenAChangeIsRefusedUntilReconnect_ThenTheNextWriteReachesTheSourceOnTheNextTick()
    {
        // Arrange: the source refuses every FirstName write until it reconnects. A flush that reported
        // that refusal as a failure would divert the tick's own changes into the queue unattempted, so
        // every later write would reach the source one flush window late, for good.
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithFullPropertyTracking();

        var person = new Person(context);

        var gate = new object();
        var batches = new List<string[]>();

        var source = new TestSubjectSource(person, context, NullLogger.Instance,
            bufferTime: TimeSpan.FromMilliseconds(8))
        {
            WriteChangesOverride = (changes, _) =>
            {
                lock (gate)
                {
                    var batch = changes.ToArray();
                    batches.Add(batch
                        .Select(change => $"{change.Property.Name}={change.GetNewValue<object?>()}")
                        .ToArray());

                    var refused = batch
                        .Where(change => change.Property.Name == nameof(Person.FirstName))
                        .ToImmutableArray();

                    return ValueTask.FromResult(refused.IsEmpty
                        ? WriteResult.Success
                        : WriteResult
                            .Failure(refused.AsSpan().ToArray(), new InvalidOperationException("Refused"))
                            .WithRefusedUntilReconnect(refused));
                }
            },
        };

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(source);
        new PropertyReference(person, nameof(Person.LastName)).SetSource(source);

        await source.StartAsync(CancellationToken.None);
        try
        {
            // Writes enqueued before the pump's subscription exists are not seen, so probe until one
            // arrives. The already-written probe is checked before the next one is written, so nothing
            // is still in flight once this returns.
            var probeValue = 0;
            var probe = string.Empty;
            await AsyncTestHelpers.WaitUntilAsync(() =>
            {
                if (IndexOfBatchWith(gate, batches, probe) >= 0)
                {
                    return true;
                }

                probe = $"{nameof(Person.LastName)}=Probe{probeValue++}";
                person.LastName = $"Probe{probeValue - 1}";
                return false;
            }, message: "Pump did not start processing changes.");

            // Act
            person.FirstName = "Bad";
            await AsyncTestHelpers.WaitUntilAsync(
                () => IndexOfBatchWith(gate, batches, "FirstName=Bad") >= 0,
                message: "The refused write never reached the source.");

            var refusalIndex = IndexOfBatchWith(gate, batches, "FirstName=Bad");

            person.LastName = "Second";
            await AsyncTestHelpers.WaitUntilAsync(
                () => IndexOfBatchWith(gate, batches, "LastName=Second") >= 0,
                timeout: TimeSpan.FromSeconds(10),
                message: "The write after a refusal never reached the source.");

            // Assert: it travelled in the write right after the refusal, not one flush window later
            Assert.Equal(refusalIndex + 1, IndexOfBatchWith(gate, batches, "LastName=Second"));

            lock (gate)
            {
                Assert.Equal(1, batches.Count(batch => batch.Contains("FirstName=Bad")));
            }

            // A held-back write stays visible and must not read as a stalled connection.
            Assert.Equal(1, source.RefusedWriteCount);
            Assert.Equal(0, source.Diagnostics.OutboundRetries.Depth);
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenTheSourceStartsAcceptingAHeldProperty_ThenTheHeldWriteIsNotSentOnTheNextConnection()
    {
        // Arrange: the source refuses FirstName, then takes it. Role permissions and access levels are
        // the server's to change mid-session, which is the whole reason those refusals are scoped to a
        // connection rather than treated as permanent. Holding the write keeps the queue empty, so the
        // newer one goes straight out and nothing collapses the two.
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithFullPropertyTracking();

        var person = new Person(context);

        var gate = new object();
        var batches = new List<string[]>();
        var refusing = true;

        var source = new TestSubjectSource(person, context, NullLogger.Instance,
            bufferTime: TimeSpan.FromMilliseconds(8))
        {
            WriteChangesOverride = (changes, _) =>
            {
                lock (gate)
                {
                    var batch = changes.ToArray();
                    batches.Add(batch
                        .Select(change => $"{change.Property.Name}={change.GetNewValue<object?>()}")
                        .ToArray());

                    var refused = refusing
                        ? batch.Where(change => change.Property.Name == nameof(Person.FirstName)).ToImmutableArray()
                        : ImmutableArray<SubjectPropertyChange>.Empty;

                    return ValueTask.FromResult(refused.IsEmpty
                        ? WriteResult.Success
                        : WriteResult
                            .Failure(refused.AsSpan().ToArray(), new InvalidOperationException("Refused"))
                            .WithRefusedUntilReconnect(refused));
                }
            },
        };

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(source);
        new PropertyReference(person, nameof(Person.LastName)).SetSource(source);

        await source.StartAsync(CancellationToken.None);
        try
        {
            var probeValue = 0;
            var probe = string.Empty;
            await AsyncTestHelpers.WaitUntilAsync(() =>
            {
                if (IndexOfBatchWith(gate, batches, probe) >= 0)
                {
                    return true;
                }

                probe = $"{nameof(Person.LastName)}=Probe{probeValue++}";
                person.LastName = $"Probe{probeValue - 1}";
                return false;
            }, message: "Pump did not start processing changes.");

            person.FirstName = "Refused";
            await AsyncTestHelpers.WaitUntilAsync(
                () => source.RefusedWriteCount == 1,
                message: "The refused write was never held back.");

            // Act: the source takes the property now, and a newer value reaches it
            refusing = false;
            person.FirstName = "Accepted";
            await AsyncTestHelpers.WaitUntilAsync(
                () => IndexOfBatchWith(gate, batches, "FirstName=Accepted") >= 0,
                message: "The accepted write never reached the source.");

            var acceptedIndex = IndexOfBatchWith(gate, batches, "FirstName=Accepted");
            source.SimulateConnectionLost();

            // Assert: releasing must not put the superseded value back on the source, which would then
            // report it and take the model down with it.
            await AsyncTestHelpers.WaitUntilAsync(
                () => source.Diagnostics.OutboundRetries.Depth == 0 && source.RefusedWriteCount == 0,
                message: "The held write was never resolved after the connection was replaced.");

            lock (gate)
            {
                var sentAfterAcceptance = batches.Skip(acceptedIndex + 1);
                Assert.DoesNotContain(sentAfterAcceptance, batch => batch.Contains("FirstName=Refused"));
            }
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
        }
    }

    private static int IndexOfBatchWith(object gate, List<string[]> batches, string entry)
    {
        if (entry.Length == 0)
        {
            return -1;
        }

        lock (gate)
        {
            return batches.FindIndex(batch => batch.Contains(entry));
        }
    }

    private static int CountWrites(object gate, List<string> writes, string propertyName)
    {
        lock (gate)
        {
            return writes.Count(write => write.StartsWith(propertyName + "=", StringComparison.Ordinal));
        }
    }
}
