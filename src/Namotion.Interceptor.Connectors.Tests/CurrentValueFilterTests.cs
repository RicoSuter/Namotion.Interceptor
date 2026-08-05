using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests;

/// <summary>
/// Delivering only the current value is safe because a change is dropped only when the model holds a
/// different one, and every transition to the model's current value is itself enqueued to every
/// processor that does not own it. These pin the cases where that could fail: a stored value that is
/// not the value that was written, a derived value the getter recomputes, and a newer value that
/// arrives from a different source.
/// </summary>
public class CurrentValueFilterTests
{
    [Fact]
    public async Task WhenAHookClampsTheWrittenValue_ThenTheStoredValueIsStillDelivered()
    {
        // Arrange: OnValueChanging rewrites 150 to 100, so the value written and the value stored differ.
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithPropertyChangeSubscriptions();

        var subject = new ClampingDevice(context);
        var written = new ConcurrentQueue<int>();

        using var processor = CreateProcessor(context, change => written.Enqueue(change.GetNewValue<int>()));

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var processing = processor.ProcessAsync(cancellation.Token);

        // Act
        subject.Value = 150;

        // Assert: the clamped value reaches the source rather than being dropped as not-current.
        await AsyncTestHelpers.WaitUntilAsync(() => written.Contains(100));
        Assert.Equal(100, subject.Value);

        await StopAsync(cancellation, processing);
    }

    [Fact]
    public async Task WhenADerivedValueIsRecomputed_ThenTheSettledValueIsDelivered()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithFullPropertyTracking();

        var subject = new Person(context);
        var written = new ConcurrentQueue<string?>();

        using var processor = CreateProcessor(context, change =>
        {
            if (change.Property.Name == nameof(Person.FullName))
            {
                written.Enqueue(change.GetNewValue<string?>());
            }
        });

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var processing = processor.ProcessAsync(cancellation.Token);

        // Act: two writes, so an earlier derived recomputation is superseded by a later one.
        subject.FirstName = "A";
        subject.LastName = "B";

        // Assert: whatever intermediate derived values are dropped, the settled one is delivered.
        await AsyncTestHelpers.WaitUntilAsync(() => written.Contains(subject.FullName));

        await StopAsync(cancellation, processing);
    }

    [Fact]
    public async Task WhenADerivedGetterReturnsAFreshInstance_ThenItsChangeIsStillDeliveredWhenBuffered()
    {
        // Arrange: the getter recomputes, so what it hands back is never reference-equal to the value
        // the change carries. Staleness is unprovable here, and dropping would be permanent: the
        // transition that would re-enqueue the value is the change being dropped. Buffered on purpose,
        // because that is the path the flush-time check runs on, and it is every connector's default.
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var subject = new DerivedCollectionDevice(context);
        var written = new ConcurrentQueue<string>();

        using var processor = CreateProcessor(context, change =>
        {
            if (change.Property.Name == nameof(DerivedCollectionDevice.Pair))
            {
                written.Enqueue(string.Join(",", change.GetNewValue<int[]>()));
            }
        });

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var processing = processor.ProcessAsync(cancellation.Token);

        // Act
        subject.First = 1;
        subject.Second = 2;

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => written.Contains("1,2"));

        await StopAsync(cancellation, processing);
    }

    [Fact]
    public async Task WhenANewerValueArrivesFromAnotherSource_ThenThisSourceStillReceivesIt()
    {
        // Arrange: two sources on one property. A pending write for source A is superseded by a value
        // that source B applied, and A is owed that value because A did not send it.
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithPropertyChangeSubscriptions();

        var subject = new Person(context);
        var sourceA = new object();
        var sourceB = new object();
        var written = new ConcurrentQueue<string?>();

        var firstName = new PropertyReference(subject, nameof(Person.FirstName));

        using var processor = new ChangeQueueProcessor(
            source: sourceA,
            context: context,
            propertyFilter: _ => true,
            writeHandler: (changes, _) =>
            {
                foreach (var change in changes.ToArray())
                {
                    written.Enqueue(change.GetNewValue<string?>());
                }

                return ValueTask.CompletedTask;
            },
            bufferTime: TimeSpan.FromMilliseconds(8),
            maxQueueDepth: null,
            logger: NullLogger.Instance);

        // Both commit before processing starts, so the loop meets the local write with the model
        // already holding source B's value.
        subject.FirstName = "LocalPending";

        using (PendingOrigin.Set(firstName, ChangeOrigin.FromSource(sourceB), "FromB"))
        {
            subject.FirstName = "FromB";
        }

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var processing = processor.ProcessAsync(cancellation.Token);

        // Assert: source A receives B's value, and never the value the model moved past.
        await AsyncTestHelpers.WaitUntilAsync(() => written.Contains("FromB"));
        Assert.DoesNotContain("LocalPending", written);

        await StopAsync(cancellation, processing);
    }

    private static ChangeQueueProcessor CreateProcessor(
        IInterceptorSubjectContext context, Action<SubjectPropertyChange> onWritten)
    {
        // A distinct source object: with a null source the dequeue loop's echo check matches every
        // Local change, whose origin source is also null, and nothing is ever delivered.
        return new ChangeQueueProcessor(
            source: new object(),
            context: context,
            propertyFilter: _ => true,
            writeHandler: (changes, _) =>
            {
                foreach (var change in changes.ToArray())
                {
                    onWritten(change);
                }

                return ValueTask.CompletedTask;
            },
            bufferTime: TimeSpan.FromMilliseconds(8),
            maxQueueDepth: null,
            logger: NullLogger.Instance);
    }

    private static async Task StopAsync(CancellationTokenSource cancellation, Task processing)
    {
        await cancellation.CancelAsync();
        try { await processing; } catch (OperationCanceledException) { /* expected */ }
    }
}
