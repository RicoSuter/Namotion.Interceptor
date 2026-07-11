using Moq;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Connectors.Transactions;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Transactions;
using Xunit;

namespace Namotion.Interceptor.Connectors.Tests.Transactions;

/// <summary>
/// Pins the source semantics of transformed trigger values: when an OnChanging hook changes the
/// incoming value (a clamp or normalization), the stored value is locally computed, so the write
/// publishes with a null source and flows back to bound sources. The source stamp is kept only
/// when the stored value is exactly the value the source sent or confirmed.
/// </summary>
public class SubjectChangingHookTransformTests : TransactionTestBase
{
    [Fact]
    public void WhenInboundValueIsTransformedByChangingHook_ThenWritePublishesLocalOrigin()
    {
        // Arrange
        var context = CreateContext();
        var device = new ClampingDevice(context);
        var sourceMock = CreateSucceedingSource();

        using var subscription = context.CreatePropertyChangeQueueSubscription();

        // Act: the source sends 105; the hook clamps to 100, so the stored value is locally computed.
        new PropertyReference(device, nameof(ClampingDevice.Value))
            .SetValueFromSource(sourceMock.Object, DateTimeOffset.UtcNow, null, 105);

        var changes = DrainWithSentinel(context, subscription);

        // Assert: the write publishes local origin (Source = null), not the inbound source, so the
        // corrected value is delivered back to the bound source instead of echo-dropped.
        Assert.Equal(100, device.Value);
        var valueChanges = changes.Where(c => c.Property.Name == nameof(ClampingDevice.Value)).ToList();
        Assert.Single(valueChanges);
        Assert.Equal(100, valueChanges[0].GetNewValue<int>());
        Assert.Null(valueChanges[0].Source);
    }

    [Fact]
    public void WhenInboundValueIsNotTransformed_ThenWriteKeepsSourceStamp()
    {
        // Arrange
        var context = CreateContext();
        var device = new ClampingDevice(context);
        var sourceMock = CreateSucceedingSource();

        using var subscription = context.CreatePropertyChangeQueueSubscription();

        // Act: 50 passes the clamp unchanged, so the stored value is exactly what the source sent.
        new PropertyReference(device, nameof(ClampingDevice.Value))
            .SetValueFromSource(sourceMock.Object, DateTimeOffset.UtcNow, null, 50);

        var changes = DrainWithSentinel(context, subscription);

        // Assert
        Assert.Equal(50, device.Value);
        var valueChanges = changes.Where(c => c.Property.Name == nameof(ClampingDevice.Value)).ToList();
        Assert.Single(valueChanges);
        Assert.Equal(50, valueChanges[0].GetNewValue<int>());
        Assert.Same(sourceMock.Object, valueChanges[0].Source);
    }

    [Fact]
    public void WhenTransformedValueEchoesBack_ThenNoFurtherChangeIsPublished()
    {
        // Arrange: the clamp already produced 100 from an inbound 105.
        var context = CreateContext();
        var device = new ClampingDevice(context);
        var sourceMock = CreateSucceedingSource();

        new PropertyReference(device, nameof(ClampingDevice.Value))
            .SetValueFromSource(sourceMock.Object, DateTimeOffset.UtcNow, null, 105);
        Assert.Equal(100, device.Value);

        using var subscription = context.CreatePropertyChangeQueueSubscription();

        // Act: the source echoes the corrected value; the clamp is a projection, so nothing changes.
        new PropertyReference(device, nameof(ClampingDevice.Value))
            .SetValueFromSource(sourceMock.Object, DateTimeOffset.UtcNow, null, 100);

        var changes = DrainWithSentinel(context, subscription);

        // Assert: quiescence, no new Value notification (the correction loop terminates).
        Assert.Equal(100, device.Value);
        Assert.DoesNotContain(changes, c => c.Property.Name == nameof(ClampingDevice.Value));
    }

    [Fact]
    public void WhenTransformedValueEqualsStoredValue_ThenNoCorrectionIsPublished()
    {
        // Arrange: the model already holds the projection of the incoming value.
        var context = CreateContext();
        var device = new ClampingDevice(context);
        var sourceMock = CreateSucceedingSource();
        device.Value = 100;

        using var subscription = context.CreatePropertyChangeQueueSubscription();

        // Act: the source sends 105; the clamp projects it to 100, which equals the stored value,
        // so the equality check drops the write before anything is published.
        new PropertyReference(device, nameof(ClampingDevice.Value))
            .SetValueFromSource(sourceMock.Object, DateTimeOffset.UtcNow, null, 105);

        var changes = DrainWithSentinel(context, subscription);

        // Assert: pins the documented boundary that a correction requires a stored-value change;
        // the source keeps its out-of-range value until the next model change (see connectors.md).
        Assert.Equal(100, device.Value);
        Assert.DoesNotContain(changes, c => c.Property.Name == nameof(ClampingDevice.Value));
    }

    [Fact]
    public async Task WhenInboundValueIsTransformed_ThenCorrectionIsDeliveredToBoundSource()
    {
        // Arrange
        var context = CreateContext();
        var device = new ClampingDevice(context);
        var sourceMock = CreateSucceedingSource();

        new PropertyReference(device, nameof(ClampingDevice.Value)).SetSource(sourceMock.Object);

        // Act: the source sends 105; the clamp stores 100 as local origin. The processor's source
        // identity IS the bound source, so the correction is actually written back to the bound
        // source, not echo-dropped.
        var delivered = await DeliverThroughChangeQueueProcessorAsync(
            context,
            sourceMock.Object,
            () =>
            {
                new PropertyReference(device, nameof(ClampingDevice.Value))
                    .SetValueFromSource(sourceMock.Object, DateTimeOffset.UtcNow, null, 105);
                return Task.CompletedTask;
            },
            isAwaitedChange: c => c.Property.Name == nameof(ClampingDevice.Value),
            timeoutMessage: "Processor did not deliver the clamped correction.");

        // Assert
        Assert.Equal(100, device.Value);
        var correction = delivered.Single(c => c.Property.Name == nameof(ClampingDevice.Value));
        Assert.Equal(100, correction.GetNewValue<int>());
    }

    [Fact]
    public async Task WhenTransactionValueIsTransformedByChangingHook_ThenTransformedValueIsCommittedWithSourceStamp()
    {
        // Arrange
        var context = CreateContext();
        var device = new ClampingDevice(context);

        var writtenBatches = new List<SubjectPropertyChange[]>();
        var sourceMock = CreateSucceedingSource();
        sourceMock
            .Setup(s => s.WriteChangesAsync(
                It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(),
                It.IsAny<CancellationToken>()))
            .Callback((ReadOnlyMemory<SubjectPropertyChange> batch, CancellationToken _) =>
                writtenBatches.Add(batch.ToArray()))
            .Returns(new ValueTask<WriteResult>(WriteResult.Success));

        new PropertyReference(device, nameof(ClampingDevice.Value)).SetSource(sourceMock.Object);

        using var subscription = context.CreatePropertyChangeQueueSubscription();

        // Act: the hook transforms at capture, so the transaction stages and confirms the clamped
        // value; at commit replay the projection is stable and the source stamp is truthful.
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            device.Value = 105;
            await transaction.CommitAsync(CancellationToken.None);
        }

        var changes = DrainWithSentinel(context, subscription);

        // Assert: the source received the clamped value, once.
        Assert.Equal(100, device.Value);
        Assert.Single(writtenBatches);
        Assert.Single(writtenBatches[0]);
        Assert.Equal(100, writtenBatches[0][0].GetNewValue<int>());

        // The commit apply notification keeps the confirming source stamp: the stored value is
        // exactly the value the source confirmed.
        var valueChanges = changes.Where(c => c.Property.Name == nameof(ClampingDevice.Value)).ToList();
        Assert.Single(valueChanges);
        Assert.Equal(100, valueChanges[0].GetNewValue<int>());
        Assert.Same(sourceMock.Object, valueChanges[0].Source);
    }
}
