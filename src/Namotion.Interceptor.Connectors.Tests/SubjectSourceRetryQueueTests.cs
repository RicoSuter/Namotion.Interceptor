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
        // writes wholesale (error without enumerated failed changes) while the flag is set. The long retry
        // time leaves the retry to the next write rather than the idle flush.
        var failWholesale = false;
        var (person, source, gate, receivedWrites) = CreateSource(() => failWholesale, retryTime: TimeSpan.FromMinutes(1));

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenTheSourceIsIdleWithAParkedWrite_ThenItIsSentWithoutAFurtherChange(bool reloadInsideConnector)
    {
        // Arrange: without the reload only the interval can send it in time, with it only the Synchronized wake.
        var failFirstName = false;
        SubjectPropertyWriter? propertyWriter = null;
        var (person, source, gate, receivedWrites) = CreateSource(
            () => failFirstName,
            retryTime: reloadInsideConnector ? TimeSpan.FromMinutes(1) : TimeSpan.FromMilliseconds(200),
            startListening: (writer, _) =>
            {
                propertyWriter = writer;
                return Task.FromResult<IAsyncDisposable?>(null);
            });

        await source.StartAsync(CancellationToken.None);
        try
        {
            var probeValue = 0;
            await AsyncTestHelpers.WaitUntilAsync(() =>
            {
                person.LastName = "Probe" + probeValue++;
                return CountWrites(gate, receivedWrites, nameof(Person.LastName)) >= 1;
            }, message: "Pump did not start processing changes.");

            lock (gate)
            {
                failFirstName = true;
            }
            person.FirstName = "John";
            await AsyncTestHelpers.WaitUntilAsync(
                () => source.Diagnostics.OutboundRetries.Depth > 0,
                message: "The failed write was not parked.");

            // Act: let writes succeed again and make no further change to any property.
            lock (gate)
            {
                failFirstName = false;
            }

            if (reloadInsideConnector)
            {
                // A reload inside the connector, as on a transport reconnect, which ends in Synchronized.
                propertyWriter!.StartBuffering();
                await propertyWriter.LoadInitialStateAndResumeAsync(CancellationToken.None);
            }

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(() =>
            {
                lock (gate)
                {
                    return receivedWrites.Contains("FirstName=John");
                }
            }, message: "The parked write was not sent while the model was idle.");
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenAReloadInsideTheConnectorOverwritesAParkedWrite_ThenTheWriteIsReplayedAfterTheLoadAndReadBack()
    {
        // Arrange: a peer that holds FirstName and reads every accepted write back through the property writer,
        // as a subscription does. The long retry time leaves the replay to the wake on Synchronized.
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
                string? acceptedValue = null;
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

                        acceptedValue = change.GetNewValue<string?>();
                        peerFirstName = acceptedValue!;
                        events.Add("Write=" + acceptedValue);
                    }
                }

                if (acceptedValue is not null)
                {
                    propertyWriter!.Write((firstName, source, acceptedValue),
                        static state => state.firstName.SetValueFromSource(state.source, null, null, state.acceptedValue));
                }

                return ValueTask.FromResult(WriteResult.Success);
            },
        };

        firstName.SetSource(source);
        new PropertyReference(person, nameof(Person.LastName)).SetSource(source);

        await source.StartAsync(CancellationToken.None);
        try
        {
            // A write before the pump runs could go out with the startup wake rather than after the reload.
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
                    return peerFirstName == "Written" && person.FirstName == "Written";
                }
            }, message: "The parked write was not replayed and read back after the reload.");

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
    public async Task WhenRetryTimeIsBelowOneMillisecond_ThenWritesAreStillDelivered()
    {
        // Arrange
        var (person, source, gate, receivedWrites) = CreateSource(() => false, retryTime: TimeSpan.FromMicroseconds(500));

        await source.StartAsync(CancellationToken.None);
        try
        {
            // The reconcile before the pump can deliver a probe too, so only the write below proves the pump runs.
            var probeValue = 0;
            await AsyncTestHelpers.WaitUntilAsync(() =>
            {
                person.LastName = "Probe" + probeValue++;
                return CountWrites(gate, receivedWrites, nameof(Person.LastName)) >= 1;
            }, message: "Pump did not start processing changes.");

            // Act
            person.FirstName = "John";

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(() =>
            {
                lock (gate)
                {
                    return receivedWrites.Contains("FirstName=John");
                }
            }, message: "The write was not delivered.");
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Creates a source owning FirstName and LastName that records each delivered write as "Name=Value"
    /// under the returned gate, and fails FirstName writes wholesale while <paramref name="failFirstName"/>
    /// returns true, which is read under the gate.
    /// </summary>
    private static (Person Person, TestSubjectSource Source, object Gate, List<string> ReceivedWrites) CreateSource(
        Func<bool> failFirstName,
        TimeSpan? retryTime = null,
        Func<SubjectPropertyWriter, CancellationToken, Task<IAsyncDisposable?>>? startListening = null)
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithFullPropertyTracking();

        var person = new Person(context);

        var gate = new object();
        var receivedWrites = new List<string>();

        var source = new TestSubjectSource(person, context, NullLogger.Instance,
            bufferTime: TimeSpan.FromMilliseconds(8),
            retryTime: retryTime)
        {
            StartListeningOverride = startListening,
            WriteChangesOverride = (changes, _) =>
            {
                lock (gate)
                {
                    var batch = changes.ToArray();
                    if (failFirstName() && batch.Any(change => change.Property.Name == nameof(Person.FirstName)))
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

        return (person, source, gate, receivedWrites);
    }

    private static int CountWrites(object gate, List<string> writes, string propertyName)
    {
        lock (gate)
        {
            return writes.Count(write => write.StartsWith(propertyName + "=", StringComparison.Ordinal));
        }
    }
}
