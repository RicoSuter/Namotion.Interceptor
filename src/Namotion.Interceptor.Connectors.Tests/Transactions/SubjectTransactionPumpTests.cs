using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Connectors.Transactions;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Transactions;
using Xunit;

namespace Namotion.Interceptor.Connectors.Tests.Transactions;

public class SubjectTransactionPumpTests
{
    [Fact]
    public async Task WhenTransactionCommitsSourceBoundChange_ThenSourceReceivesItExactlyOnce()
    {
        // Arrange: real context with transactions and a running SubjectSourceBase pump
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithTransactions()
            .WithFullPropertyTracking()
            .WithSourceTransactions();

        var person = new Person(context);
        var receivedBatches = new List<SubjectPropertyChange[]>();

        var source = new TestSubjectSource(person, context, NullLogger.Instance,
            bufferTime: TimeSpan.FromMilliseconds(8))
        {
            WriteChangesOverride = (changes, _) =>
            {
                lock (receivedBatches)
                {
                    receivedBatches.Add(changes.ToArray());
                }
                return ValueTask.FromResult(WriteResult.Success);
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
                return CountWrites(receivedBatches, nameof(Person.LastName)) >= 1;
            }, message: "Pump did not start processing changes.");

            // Act: commit a source-bound change
            using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
            {
                person.FirstName = "John";
                await transaction.CommitAsync(CancellationToken.None);
            }

            // Sentinel: the queue preserves order, so once a post-commit write arrives at the
            // source, any echo of the commit would already have been flushed.
            var lastNameWritesBeforeSentinel = CountWrites(receivedBatches, nameof(Person.LastName));
            person.LastName = "Sentinel";
            await AsyncTestHelpers.WaitUntilAsync(
                () => CountWrites(receivedBatches, nameof(Person.LastName)) > lastNameWritesBeforeSentinel,
                message: "Sentinel write did not reach the source.");
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
        }

        // Assert: stage 1 wrote FirstName once; the local apply must not be re-pushed.
        // The exactly-once probe uses LastName as the sentinel (different from the committed FirstName)
        // so the queue deduplicator can never merge them; keep the two properties distinct in future edits.
        Assert.Equal(1, CountWrites(receivedBatches, nameof(Person.FirstName)));
        Assert.Equal("John", person.FirstName);
    }

    private static int CountWrites(List<SubjectPropertyChange[]> batches, string propertyName)
    {
        lock (batches)
        {
            return batches.Sum(batch => batch.Count(change => change.Property.Name == propertyName));
        }
    }
}
