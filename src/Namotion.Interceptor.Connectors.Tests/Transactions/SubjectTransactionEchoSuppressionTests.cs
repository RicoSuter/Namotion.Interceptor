using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Connectors.Transactions;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Transactions;
using Xunit;

namespace Namotion.Interceptor.Connectors.Tests.Transactions;

/// <summary>
/// Tests pinning the echo-suppression contract: source-bound commit applies carry the
/// accepting source's identity so outbound ChangeQueueProcessor instances that belong to
/// that same source recognize the notification as an echo and drop it. Server-side
/// processors (different source identity) must still receive every notification.
/// </summary>
public class SubjectTransactionEchoSuppressionTests : TransactionTestBase
{
    // Drains changes from the subscription until a sentinel is received.
    // The sentinel itself is not included in the returned list.
    // Throws TimeoutException if the sentinel does not arrive within 10 seconds.
    private static List<SubjectPropertyChange> DrainUntil(
        PropertyChangeQueueSubscription subscription, Func<SubjectPropertyChange, bool> isSentinel)
    {
        var changes = new List<SubjectPropertyChange>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (subscription.TryDequeue(out var change, timeout.Token))
        {
            if (isSentinel(change))
            {
                return changes;
            }
            changes.Add(change);
        }
        throw new TimeoutException("Sentinel notification was not received within 10 seconds.");
    }

    private static List<SubjectPropertyChange> DrainUntilSentinelProperty(
        PropertyChangeQueueSubscription subscription, string sentinelPropertyName)
    {
        return DrainUntil(subscription, change => change.Property.Name == sentinelPropertyName);
    }

    [Fact]
    public async Task WhenSourceBoundChangeCommitted_ThenNotificationCarriesConfirmingSource()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);
        var sourceMock = CreateSucceedingSource();

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(sourceMock.Object);

        using var subscription = context.CreatePropertyChangeQueueSubscription();

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "John";
            await transaction.CommitAsync(CancellationToken.None);
        }

        // Sentinel: a different property (LastName, not source-bound) fires after the commit so we
        // know the commit notification has already been queued.
        person.LastName = "Sentinel";

        var changes = DrainUntilSentinelProperty(subscription, nameof(Person.LastName));

        // Assert
        var firstNameChanges = changes.Where(c => c.Property.Name == nameof(Person.FirstName)).ToList();
        Assert.Single(firstNameChanges);
        Assert.Same(sourceMock.Object, firstNameChanges[0].Source);
    }

    [Fact]
    public async Task WhenLocalAndSourceBoundChangesCommittedTogether_ThenOnlySourceBoundCarriesSource()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);
        var sourceMock = CreateSucceedingSource();

        // FirstName is source-bound; LastName is a local (no-source) change.
        new PropertyReference(person, nameof(Person.FirstName)).SetSource(sourceMock.Object);

        using var subscription = context.CreatePropertyChangeQueueSubscription();

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "John";
            person.LastName = "Doe";
            await transaction.CommitAsync(CancellationToken.None);
        }

        // Sentinel: FirstName_MaxLength is a distinct property, not involved in the commit.
        person.FirstName_MaxLength = 99;

        var changes = DrainUntilSentinelProperty(subscription, nameof(Person.FirstName_MaxLength));

        // Assert
        var firstNameChanges = changes.Where(c => c.Property.Name == nameof(Person.FirstName)).ToList();
        var lastNameChanges = changes.Where(c => c.Property.Name == nameof(Person.LastName)).ToList();

        Assert.Single(firstNameChanges);
        Assert.Single(lastNameChanges);
        Assert.Same(sourceMock.Object, firstNameChanges[0].Source);
        Assert.Null(lastNameChanges[0].Source);
    }

    [Fact]
    public async Task WhenRollbackRevertsLocalApplies_ThenRevertNotificationsCarrySource()
    {
        // Arrange
        var context = CreateContext();
        var device = new ThrowingDevice(context)
        {
            // PropertyB throws during commit-apply; PropertyA succeeds and is then reverted.
            ShouldThrow = propertyName => propertyName == nameof(ThrowingDevice.PropertyB)
        };
        var sourceMock = CreateSucceedingSource();

        new PropertyReference(device, nameof(ThrowingDevice.PropertyA)).SetSource(sourceMock.Object);
        new PropertyReference(device, nameof(ThrowingDevice.PropertyB)).SetSource(sourceMock.Object);

        // Use a separate Person on the same context as the sentinel carrier. After rollback,
        // PropertyA is already false again (reverted), so writing false would be suppressed by
        // the equality check; a string property on a sibling subject avoids that.
        var sentinel = new Person(context);

        using var subscription = context.CreatePropertyChangeQueueSubscription();

        // Act - capture writes during transaction body (no notifications yet); throw fires at commit.
        // Dispose the transaction before writing the sentinel: while the transaction is open the
        // SubjectTransaction.Current AsyncLocal still holds it, so property writes are captured into
        // the pending set, not published to the change queue.
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.Rollback))
        {
            device.PropertyA = true;
            device.PropertyB = true;

            device.ThrowingEnabled = true;

            await Assert.ThrowsAsync<SubjectTransactionException>(
                () => transaction.CommitAsync(CancellationToken.None).AsTask());
        } // Dispose clears SubjectTransaction.Current so subsequent writes are published immediately.

        // Sentinel: write to the sibling subject; this is outside any transaction so it fires immediately.
        sentinel.LastName = "Sentinel";

        var changes = DrainUntilSentinelProperty(subscription, nameof(Person.LastName));

        // Assert: exactly 2 PropertyA notifications: the forward apply (true) and the rollback revert (false).
        // Both must carry the source identity so the outbound filter on that source treats them as echoes.
        var propertyAChanges = changes.Where(c => c.Property.Name == nameof(ThrowingDevice.PropertyA)).ToList();
        Assert.Equal(2, propertyAChanges.Count);
        Assert.All(propertyAChanges, change => Assert.Same(sourceMock.Object, change.Source));
        Assert.True(propertyAChanges[0].GetNewValue<bool>());   // forward apply: true
        Assert.False(propertyAChanges[1].GetNewValue<bool>());  // rollback revert: false
    }

    [Fact]
    public async Task WhenCommitSpansMultipleSources_ThenEachChangeCarriesItsOwnSource()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);
        var sourceA = CreateSucceedingSource();
        var sourceB = CreateSucceedingSource();

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(sourceA.Object);
        new PropertyReference(person, nameof(Person.LastName)).SetSource(sourceB.Object);

        using var subscription = context.CreatePropertyChangeQueueSubscription();

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "John";
            person.LastName = "Doe";
            await transaction.CommitAsync(CancellationToken.None);
        }

        // Sentinel: FirstName_MaxLength is local, not bound to any source.
        person.FirstName_MaxLength = 77;

        var changes = DrainUntilSentinelProperty(subscription, nameof(Person.FirstName_MaxLength));

        // Assert: each committed change carries its own source identity.
        var firstNameChanges = changes.Where(c => c.Property.Name == nameof(Person.FirstName)).ToList();
        var lastNameChanges = changes.Where(c => c.Property.Name == nameof(Person.LastName)).ToList();

        Assert.Single(firstNameChanges);
        Assert.Single(lastNameChanges);
        Assert.Same(sourceA.Object, firstNameChanges[0].Source);
        Assert.Same(sourceB.Object, lastNameChanges[0].Source);
    }

    [Fact]
    public async Task WhenSourceBoundChangeCommitted_ThenServerSideProcessorStillReceivesIt()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);
        var sourceMock = CreateSucceedingSource();

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(sourceMock.Object);

        // The server-side processor uses a fresh, unique object as its source identity so the
        // echo filter (change.Source == _source) never matches the committed change's source.
        var serverSource = new object();
        var serverReceived = new List<SubjectPropertyChange>();

        var processor = new ChangeQueueProcessor(
            source: serverSource,
            context: context,
            propertyFilter: _ => true,
            writeHandler: (changes, _) =>
            {
                lock (serverReceived)
                {
                    serverReceived.AddRange(changes.ToArray());
                }
                return ValueTask.CompletedTask;
            },
            bufferTime: TimeSpan.FromMilliseconds(8),
            logger: NullLogger.Instance);

        using var processorCts = new CancellationTokenSource();
        var processTask = processor.ProcessAsync(processorCts.Token);

        try
        {
            // Act
            using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
            {
                person.FirstName = "John";
                await transaction.CommitAsync(CancellationToken.None);
            }

            // Wait until the server-side processor has received the FirstName change.
            await AsyncTestHelpers.WaitUntilAsync(
                () =>
                {
                    lock (serverReceived)
                    {
                        return serverReceived.Any(c => c.Property.Name == nameof(Person.FirstName));
                    }
                },
                message: "Server-side processor did not receive the committed FirstName change.");
        }
        finally
        {
            // Follow the ChangeQueueProcessorTests lifecycle pattern: cancel, dispose, then await.
            await processorCts.CancelAsync();
            processor.Dispose();
            try { await processTask; } catch (OperationCanceledException) { }
        }

        // Assert: the server-side processor received the FirstName change with the confirming source
        // stamped on it. Its own echo filter does not suppress it because serverSource != sourceMock.Object.
        lock (serverReceived)
        {
            var change = serverReceived.First(c => c.Property.Name == nameof(Person.FirstName));
            Assert.Same(sourceMock.Object, change.Source);
        }
    }
}
