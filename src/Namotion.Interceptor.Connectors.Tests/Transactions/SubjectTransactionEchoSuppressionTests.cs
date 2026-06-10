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
    public async Task WhenCommitAppliedChangeTriggersCascade_ThenCascadeInheritsSourceScope()
    {
        // Arrange
        var context = CreateContext();
        var device = new CascadingDevice(context);

        var writtenBatches = new List<SubjectPropertyChange[]>();
        // CreateSucceedingSource sets WriteBatchSize = 0 (unbatched); the Setup below overrides
        // only WriteChangesAsync to capture the written batches.
        var sourceMock = CreateSucceedingSource();
        sourceMock
            .Setup(s => s.WriteChangesAsync(
                It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(),
                It.IsAny<CancellationToken>()))
            .Callback((ReadOnlyMemory<SubjectPropertyChange> batch, CancellationToken _) =>
                writtenBatches.Add(batch.ToArray()))
            .Returns(new ValueTask<WriteResult>(WriteResult.Success));

        new PropertyReference(device, nameof(CascadingDevice.Primary)).SetSource(sourceMock.Object);
        new PropertyReference(device, nameof(CascadingDevice.Secondary)).SetSource(sourceMock.Object);

        using var subscription = context.CreatePropertyChangeQueueSubscription();

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            device.Primary = 5;
            await transaction.CommitAsync(CancellationToken.None);
        }

        var secondaryAfterCommit = device.Secondary;

        // Sentinel: use a sibling subject so the sentinel property is unambiguous.
        var sentinel = new Person(context);
        sentinel.LastName = "Sentinel";

        var changes = DrainUntilSentinelProperty(subscription, nameof(Person.LastName));

        // Assert: the cascade produced Secondary = 10.
        Assert.Equal(10, secondaryAfterCommit);

        var primaryChanges = changes.Where(c => c.Property.Name == nameof(CascadingDevice.Primary)).ToList();
        var secondaryChanges = changes.Where(c => c.Property.Name == nameof(CascadingDevice.Secondary)).ToList();

        // Primary change carries the source.
        Assert.Single(primaryChanges);
        Assert.Equal(5, primaryChanges[0].GetNewValue<int>());
        Assert.Same(sourceMock.Object, primaryChanges[0].Source);

        // Secondary change (the cascade) also carries the source, inheriting the scope.
        Assert.Single(secondaryChanges);
        Assert.Equal(10, secondaryChanges[0].GetNewValue<int>());
        Assert.Same(sourceMock.Object, secondaryChanges[0].Source);

        // The mock source received exactly one WriteChangesAsync call and that call contained
        // only the Primary change; the cascade (Secondary) is NOT submitted to the source because
        // it was not part of the transaction's pending set.

        // writtenBatches is fully populated here: the mock's WriteChangesAsync returns a
        // completed ValueTask, so the callback fires synchronously inside CommitAsync (stage 1)
        // before the local apply (stage 2) or the method return.
        Assert.Single(writtenBatches);
        Assert.Single(writtenBatches[0]);
        Assert.Equal(nameof(CascadingDevice.Primary), writtenBatches[0][0].Property.Name);
    }

    [Fact]
    public async Task WhenInboundSourceValueTriggersCascade_ThenCascadeInheritsSourceScope()
    {
        // Arrange
        var context = CreateContext();
        var device = new CascadingDevice(context);
        var sourceMock = CreateSucceedingSource();

        new PropertyReference(device, nameof(CascadingDevice.Primary)).SetSource(sourceMock.Object);
        new PropertyReference(device, nameof(CascadingDevice.Secondary)).SetSource(sourceMock.Object);

        using var subscription = context.CreatePropertyChangeQueueSubscription();

        // Act
        new PropertyReference(device, nameof(CascadingDevice.Primary))
            .SetValueFromSource(sourceMock.Object, DateTimeOffset.UtcNow, null, 7);

        // Sentinel: use a sibling subject so the sentinel property is unambiguous.
        var sentinel = new Person(context);
        sentinel.LastName = "Sentinel";

        var changes = DrainUntilSentinelProperty(subscription, nameof(Person.LastName));

        // Assert: the cascade produced Secondary = 14.
        Assert.Equal(14, device.Secondary);

        var primaryChanges = changes.Where(c => c.Property.Name == nameof(CascadingDevice.Primary)).ToList();
        var secondaryChanges = changes.Where(c => c.Property.Name == nameof(CascadingDevice.Secondary)).ToList();

        // Primary change carries the inbound source.
        Assert.Single(primaryChanges);
        Assert.Equal(7, primaryChanges[0].GetNewValue<int>());
        Assert.Same(sourceMock.Object, primaryChanges[0].Source);

        // Secondary change (the cascade) also carries the inbound source, inheriting the scope.
        Assert.Single(secondaryChanges);
        Assert.Equal(14, secondaryChanges[0].GetNewValue<int>());
        Assert.Same(sourceMock.Object, secondaryChanges[0].Source);
    }

    [Fact]
    public async Task WhenCommitAppliedChangeTriggersDerivedRecalculation_ThenDerivedNotificationStaysLocallySourced()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);

        var writtenBatches = new List<SubjectPropertyChange[]>();
        // CreateSucceedingSource sets WriteBatchSize = 0 (unbatched); the Setup below overrides
        // only WriteChangesAsync to capture the written batches.
        var sourceMock = CreateSucceedingSource();
        sourceMock
            .Setup(s => s.WriteChangesAsync(
                It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(),
                It.IsAny<CancellationToken>()))
            .Callback((ReadOnlyMemory<SubjectPropertyChange> batch, CancellationToken _) =>
                writtenBatches.Add(batch.ToArray()))
            .Returns(new ValueTask<WriteResult>(WriteResult.Success));

        // Only FirstName is source-bound; FullName is a derived property that recalculates
        // when FirstName changes and is never part of the transactional source write.
        new PropertyReference(person, nameof(Person.FirstName)).SetSource(sourceMock.Object);

        using var subscription = context.CreatePropertyChangeQueueSubscription();

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "John";
            await transaction.CommitAsync(CancellationToken.None);
        }

        // Sentinel: use a sibling subject so the sentinel property is unambiguous and cannot be
        // confused with any notification produced by the commit or the derived recalculation.
        var sentinel = new Person(context);
        sentinel.LastName = "Sentinel";

        var changes = DrainUntilSentinelProperty(subscription, nameof(Person.LastName));

        // Assert 1: the FullName recalculation notification carries a null source even though the
        // triggering apply ran under the confirming source scope. DerivedPropertyChangeHandler
        // publishes recalculations under an explicit WithSource(null) scope: a derived value is
        // computed locally and no source confirmed it. Consequence: derived recalculations are
        // never echo-suppressed, so a source-bound derived property still gets pushed.
        var fullNameChanges = changes.Where(c => c.Property.Name == nameof(Person.FullName)).ToList();
        Assert.Single(fullNameChanges);
        Assert.Null(fullNameChanges[0].Source);

        // Assert 2 (sanity): FirstName change also carries the source.
        var firstNameChanges = changes.Where(c => c.Property.Name == nameof(Person.FirstName)).ToList();
        Assert.Single(firstNameChanges);
        Assert.Same(sourceMock.Object, firstNameChanges[0].Source);

        // Assert 3: stage 1 (source write) contained exactly one batch with exactly one change
        // for FirstName; the derived FullName recalculation is never submitted to the source.
        Assert.Single(writtenBatches);
        Assert.Single(writtenBatches[0]);
        Assert.Equal(nameof(Person.FirstName), writtenBatches[0][0].Property.Name);
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
