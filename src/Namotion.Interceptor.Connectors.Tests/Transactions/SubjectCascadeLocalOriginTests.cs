using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Xunit;

namespace Namotion.Interceptor.Connectors.Tests.Transactions;

/// <summary>
/// Pins the local-origin delivery behavior that complements the flipped cascade pins in
/// <see cref="SubjectTransactionEchoSuppressionTests"/>: a hook cascade triggered by an inbound
/// source value is actually delivered to its bound source by the outbound change queue (not just
/// marked Source = null), and an INotifyPropertyChanged handler that writes back during a
/// source-scoped apply publishes its write-back as local origin.
/// </summary>
public class SubjectCascadeLocalOriginTests : TransactionTestBase
{
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
            maxQueueDepth: null,
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
    public void WhenInpcHandlerWritesBackDuringSourceScopedApply_ThenWriteBackPublishesLocalOrigin()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);
        var sourceMock = CreateSucceedingSource();

        // An INPC handler that reacts to FirstName by writing LastName (a model write-back).
        person.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Person.FirstName))
            {
                person.LastName = "Derived";
            }
        };

        using var subscription = context.CreatePropertyChangeQueueSubscription();

        // Act: apply FirstName under a source scope, like an inbound/commit apply. The generated
        // RaisePropertyChanged enters a local-origin scope before invoking subscribers, so the
        // handler's LastName write-back is local origin.
        using (SubjectChangeContext.WithSource(sourceMock.Object))
        {
            person.FirstName = "John";
        }

        var changes = DrainWithSentinel(context, subscription);

        var personChanges = changes.Where(c => ReferenceEquals(c.Property.Subject, person)).ToList();
        var firstNameChanges = personChanges.Where(c => c.Property.Name == nameof(Person.FirstName)).ToList();
        var lastNameChanges = personChanges.Where(c => c.Property.Name == nameof(Person.LastName)).ToList();

        // Assert: the triggering FirstName write keeps the ambient source.
        Assert.Single(firstNameChanges);
        Assert.Same(sourceMock.Object, firstNameChanges[0].Source);

        // The INPC handler's LastName write-back publishes local origin.
        Assert.Single(lastNameChanges);
        Assert.Null(lastNameChanges[0].Source);
    }
}
