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
/// Pins that framework-invoked consequence writes publish as local origin (Source = null) and flow
/// to bound sources: hook cascades from OnXChanged, and derived recalculations. Replaces the cascade
/// pins removed with SubjectTransactionEchoSuppressionTests by the #341 cleanup, flipped from the old
/// inherited-source behavior to the new local-origin behavior.
/// </summary>
public class SubjectCascadeLocalOriginTests : TransactionTestBase
{
    // Drains until the sentinel arrives (excluded from the result); throws TimeoutException after 10s.
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

    [Fact]
    public async Task WhenCommitAppliedChangeTriggersCascade_ThenCascadePublishesLocalOrigin()
    {
        // Arrange
        var context = CreateContext();
        var device = new CascadingDevice(context);

        var writtenBatches = new List<SubjectPropertyChange[]>();
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

        var sentinel = new Person(context);
        sentinel.LastName = "Sentinel";
        var changes = DrainUntil(subscription, c =>
            ReferenceEquals(c.Property.Subject, sentinel) && c.Property.Name == nameof(Person.LastName));

        // Assert: the cascade produced Secondary = 10.
        Assert.Equal(10, secondaryAfterCommit);

        var primaryChanges = changes.Where(c => c.Property.Name == nameof(CascadingDevice.Primary)).ToList();
        var secondaryChanges = changes.Where(c => c.Property.Name == nameof(CascadingDevice.Secondary)).ToList();

        // Primary: the snapshot marking was removed in #345, so the transactional apply uses the
        // change's captured source (null, since there was no ambient source when device.Primary = 5
        // was written inside the transaction). Source = null for both Primary and Secondary.
        Assert.Single(primaryChanges);
        Assert.Equal(5, primaryChanges[0].GetNewValue<int>());
        Assert.Null(primaryChanges[0].Source);

        // Secondary (the cascade) publishes local origin (null) via the WithLocalOrigin() scope
        // that the generator wraps around OnPrimaryChanged on this branch.
        Assert.Single(secondaryChanges);
        Assert.Equal(10, secondaryChanges[0].GetNewValue<int>());
        Assert.Null(secondaryChanges[0].Source);

        // Stage 1 (the transactional source write) still contained only Primary; the cascade reaches
        // the source through the background queue, not the transaction.
        Assert.Single(writtenBatches);
        Assert.Single(writtenBatches[0]);
        Assert.Equal(nameof(CascadingDevice.Primary), writtenBatches[0][0].Property.Name);
    }

    [Fact]
    public async Task WhenInboundSourceValueTriggersCascade_ThenCascadeIsDeliveredToBoundSource()
    {
        // Arrange
        var context = CreateContext();
        var device = new CascadingDevice(context);
        var sourceMock = CreateSucceedingSource();

        new PropertyReference(device, nameof(CascadingDevice.Primary)).SetSource(sourceMock.Object);
        new PropertyReference(device, nameof(CascadingDevice.Secondary)).SetSource(sourceMock.Object);

        // A ChangeQueueProcessor whose source identity IS the bound source: it echo-drops changes
        // already marked with that source and delivers everything else.
        var delivered = new List<SubjectPropertyChange>();
        var processor = new ChangeQueueProcessor(
            source: sourceMock.Object,
            context: context,
            propertyFilter: _ => true,
            writeHandler: (batch, _) =>
            {
                lock (delivered)
                {
                    delivered.AddRange(batch.ToArray());
                }
                return ValueTask.CompletedTask;
            },
            bufferTime: TimeSpan.FromMilliseconds(8),
            logger: NullLogger.Instance);

        using var processorCts = new CancellationTokenSource();
        var processTask = processor.ProcessAsync(processorCts.Token);

        try
        {
            // Act: a value arrives from the source, triggering the OnPrimaryChanged cascade.
            new PropertyReference(device, nameof(CascadingDevice.Primary))
                .SetValueFromSource(sourceMock.Object, DateTimeOffset.UtcNow, null, 7);

            // Wait until the processor has delivered the Secondary cascade.
            await AsyncTestHelpers.WaitUntilAsync(
                () =>
                {
                    lock (delivered)
                    {
                        return delivered.Any(c => c.Property.Name == nameof(CascadingDevice.Secondary));
                    }
                },
                message: "Processor did not deliver the Secondary cascade.");
        }
        finally
        {
            await processorCts.CancelAsync();
            processor.Dispose();
            try { await processTask; } catch (OperationCanceledException) { }
        }

        // Assert
        Assert.Equal(14, device.Secondary);
        lock (delivered)
        {
            // Secondary (local origin) is delivered to the bound source.
            var secondary = delivered.Single(c => c.Property.Name == nameof(CascadingDevice.Secondary));
            Assert.Equal(14, secondary.GetNewValue<int>());

            // Primary (marked with the inbound source) is echo-dropped, not pushed back.
            Assert.DoesNotContain(delivered, c => c.Property.Name == nameof(CascadingDevice.Primary));
        }
    }

    [Fact]
    public async Task WhenCommitAppliedChangeTriggersDerivedRecalculation_ThenDerivedNotificationStaysLocalOrigin()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);

        var writtenBatches = new List<SubjectPropertyChange[]>();
        var sourceMock = CreateSucceedingSource();
        sourceMock
            .Setup(s => s.WriteChangesAsync(
                It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(),
                It.IsAny<CancellationToken>()))
            .Callback((ReadOnlyMemory<SubjectPropertyChange> batch, CancellationToken _) =>
                writtenBatches.Add(batch.ToArray()))
            .Returns(new ValueTask<WriteResult>(WriteResult.Success));

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(sourceMock.Object);

        using var subscription = context.CreatePropertyChangeQueueSubscription();

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "John";
            await transaction.CommitAsync(CancellationToken.None);
        }

        var sentinel = new Person(context);
        sentinel.LastName = "Sentinel";
        var changes = DrainUntil(subscription, c =>
            ReferenceEquals(c.Property.Subject, sentinel) && c.Property.Name == nameof(Person.LastName));

        // Assert: FullName recalculation stays local origin (unchanged behavior, re-pinned).
        var fullNameChanges = changes.Where(c =>
            ReferenceEquals(c.Property.Subject, person) && c.Property.Name == nameof(Person.FullName)).ToList();
        Assert.Single(fullNameChanges);
        Assert.Null(fullNameChanges[0].Source);

        // FirstName: snapshot marking was removed in #345, so the transactional apply uses the
        // captured source (null, no ambient source when person.FirstName = "John" was written).
        var firstNameChanges = changes.Where(c =>
            ReferenceEquals(c.Property.Subject, person) && c.Property.Name == nameof(Person.FirstName)).ToList();
        Assert.Single(firstNameChanges);
        Assert.Null(firstNameChanges[0].Source);
    }
}
