using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests;

public class ChangeQueueProcessorTests
{
    [Fact]
    public async Task WhenMultipleChangesToSameProperty_ThenOnlyLastValueIsWritten()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        context.WithRegistry();
        context.WithPropertyChangeSubscriptions();

        var subject = new Person(context);
        var writtenChanges = new List<SubjectPropertyChange>();

        var processor = new ChangeQueueProcessor(
            source: null,
            context: context,
            propertyFilter: _ => true,
            writeHandler: (changes, _) =>
            {
                writtenChanges.AddRange(changes.ToArray());
                return ValueTask.CompletedTask;
            },
            bufferTime: TimeSpan.FromMilliseconds(50),
            maxQueueDepth: null,
            logger: NullLogger.Instance);

        // Act - enqueue multiple changes to the same property and trigger flush
        var property = new PropertyReference(subject, nameof(Person.FirstName));
        EnqueueChange(processor, property, "Value1", "Value2");
        EnqueueChange(processor, property, "Value2", "Value3");
        EnqueueChange(processor, property, "Value3", "Value4");

        subject.FirstName = "Value4";

        await TriggerFlushAsync(processor);

        processor.Dispose();

        // Assert - only the last value should be written (merged)
        // Merged change keeps oldest old value ("Value1") and newest new value ("Value4")
        Assert.Single(writtenChanges);
        Assert.Equal("Value1", writtenChanges[0].GetOldValue<string>());
        Assert.Equal("Value4", writtenChanges[0].GetNewValue<string>());
    }

    [Fact]
    public async Task WhenChangesToDifferentProperties_ThenAllAreWritten()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        context.WithRegistry();
        context.WithPropertyChangeSubscriptions();

        var subject = new Person(context);
        var writtenChanges = new List<SubjectPropertyChange>();

        var processor = new ChangeQueueProcessor(
            source: null,
            context: context,
            propertyFilter: _ => true,
            writeHandler: (changes, _) =>
            {
                writtenChanges.AddRange(changes.ToArray());
                return ValueTask.CompletedTask;
            },
            bufferTime: TimeSpan.FromMilliseconds(50),
            maxQueueDepth: null,
            logger: NullLogger.Instance);

        // Act - enqueue changes to different properties
        var firstNameProperty = new PropertyReference(subject, nameof(Person.FirstName));
        var lastNameProperty = new PropertyReference(subject, nameof(Person.LastName));

        EnqueueChange(processor, firstNameProperty, null, "John");
        EnqueueChange(processor, lastNameProperty, null, "Doe");

        // Only the current value is delivered, so the model has to hold what the changes carry.
        subject.FirstName = "John";
        subject.LastName = "Doe";

        await TriggerFlushAsync(processor);

        processor.Dispose();

        // Assert - both changes should be written
        Assert.Equal(2, writtenChanges.Count);
    }

    [Fact]
    public async Task WhenMerging_ThenOrderOfLastOccurrencesIsPreservedAndValuesAreMerged()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        context.WithRegistry();
        context.WithPropertyChangeSubscriptions();

        var subject = new Person(context);
        var writtenChanges = new List<SubjectPropertyChange>();

        var processor = new ChangeQueueProcessor(
            source: null,
            context: context,
            propertyFilter: _ => true,
            writeHandler: (changes, _) =>
            {
                writtenChanges.AddRange(changes.ToArray());
                return ValueTask.CompletedTask;
            },
            bufferTime: TimeSpan.FromMilliseconds(50),
            maxQueueDepth: null,
            logger: NullLogger.Instance);

        // Act - enqueue in order: A, B, A (last occurrence of A is after B)
        var firstNameProperty = new PropertyReference(subject, nameof(Person.FirstName));
        var lastNameProperty = new PropertyReference(subject, nameof(Person.LastName));

        EnqueueChange(processor, firstNameProperty, null, "First1");
        EnqueueChange(processor, lastNameProperty, null, "Last1");
        EnqueueChange(processor, firstNameProperty, "First1", "First2"); // A again

        subject.FirstName = "First2";
        subject.LastName = "Last1";

        await TriggerFlushAsync(processor);

        processor.Dispose();

        // Assert - order of last occurrences: LastName, then FirstName (A's last change was after B)
        // Merged change keeps oldest old value (null) and newest new value ("First2")
        Assert.Equal(2, writtenChanges.Count);
        Assert.Equal(nameof(Person.LastName), writtenChanges[0].Property.Name);
        Assert.Equal(nameof(Person.FirstName), writtenChanges[1].Property.Name);
        Assert.Null(writtenChanges[1].GetOldValue<string>());
        Assert.Equal("First2", writtenChanges[1].GetNewValue<string>());
    }

    [Fact]
    public async Task WhenEmptyQueue_ThenNoWriteHandlerCalled()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        context.WithRegistry();
        context.WithPropertyChangeSubscriptions();

        var writeHandlerCalled = false;

        var processor = new ChangeQueueProcessor(
            source: null,
            context: context,
            propertyFilter: _ => true,
            writeHandler: (_, _) =>
            {
                writeHandlerCalled = true;
                return ValueTask.CompletedTask;
            },
            bufferTime: TimeSpan.FromMilliseconds(50),
            maxQueueDepth: null,
            logger: NullLogger.Instance);

        // Act - trigger flush without enqueuing anything
        await TriggerFlushAsync(processor);

        processor.Dispose();

        // Assert - write handler not called for empty queue
        Assert.False(writeHandlerCalled);
    }

    [Fact]
    public void WhenDisposed_ThenResourcesAreCleaned()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        context.WithRegistry();
        context.WithPropertyChangeSubscriptions();

        var processor = new ChangeQueueProcessor(
            source: null,
            context: context,
            propertyFilter: _ => true,
            writeHandler: (_, _) => ValueTask.CompletedTask,
            bufferTime: TimeSpan.FromMilliseconds(50),
            maxQueueDepth: null,
            logger: NullLogger.Instance);

        // Act & Assert - should not throw
        processor.Dispose();
        processor.Dispose(); // Second dispose should be safe (idempotent)
    }

    [Fact]
    public async Task WhenFlushInProgress_ThenConcurrentFlushSkipsToAvoidContention()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        context.WithRegistry();
        context.WithPropertyChangeSubscriptions();

        var subject = new Person(context);
        var flushCount = 0;
        var flushStarted = new TaskCompletionSource();
        var allowFlush = new TaskCompletionSource();

        var processor = new ChangeQueueProcessor(
            source: null,
            context: context,
            propertyFilter: _ => true,
            writeHandler: async (_, _) =>
            {
                Interlocked.Increment(ref flushCount);
                flushStarted.TrySetResult();
                await allowFlush.Task;
            },
            bufferTime: TimeSpan.FromMilliseconds(50),
            maxQueueDepth: null,
            logger: NullLogger.Instance);

        // Act - enqueue and start first flush
        var property = new PropertyReference(subject, nameof(Person.FirstName));
        EnqueueChange(processor, property, null, "Value1");
        subject.FirstName = "Value1";

        var firstFlush = TriggerFlushAsync(processor);
        await flushStarted.Task;

        // Try second flush while first is blocked - should skip to avoid contention
        // (changes will be picked up on next timer tick, not lost)
        EnqueueChange(processor, property, "Value1", "Value2");
        var secondFlush = TriggerFlushAsync(processor);

        // Allow first flush to complete
        allowFlush.TrySetResult();
        await firstFlush;
        await secondFlush;

        processor.Dispose();

        // Assert - only one flush executed (concurrent flush skipped, not blocked)
        Assert.Equal(1, flushCount);
    }

    [Fact]
    public async Task WhenBoundedQueueOverflows_ThenOldestChangesAreDropped()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        context.WithRegistry();
        context.WithPropertyChangeSubscriptions();

        var subject = new Person(context);

        // A large buffer time keeps the periodic flush from draining the queue during the test, so the
        // bound is exercised purely by enqueue-side overflow. A non-null source ensures the direct
        // (source-less) property changes are not filtered out as self-originated.
        using var processor = new ChangeQueueProcessor(
            source: new object(),
            context: context,
            propertyFilter: _ => true,
            writeHandler: (_, _) => ValueTask.CompletedTask,
            bufferTime: TimeSpan.FromMinutes(10),
            maxQueueDepth: 2,
            logger: NullLogger.Instance);

        using var cancellation = new CancellationTokenSource();
        var processing = processor.ProcessAsync(cancellation.Token);

        // Act - five changes into a buffer bounded to two; the three oldest must be dropped
        for (var i = 1; i <= 5; i++)
        {
            subject.FirstName = $"v{i}";
        }

        await AsyncTestHelpers.WaitUntilAsync(
            () => processor.DropCount >= 3,
            message: "Three of the five changes should be dropped");

        // Assert
        Assert.Equal(3, processor.DropCount);

        // Cleanup
        await cancellation.CancelAsync();
        await processing;
    }

    [Fact]
    public async Task WhenUnbounded_ThenNoChangesAreDropped()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        context.WithRegistry();
        context.WithPropertyChangeSubscriptions();

        var subject = new Person(context);

        var lastWritten = "";
        using var processor = new ChangeQueueProcessor(
            source: new object(),
            context: context,
            propertyFilter: _ => true,
            writeHandler: (changes, _) =>
            {
                foreach (var change in changes.ToArray())
                {
                    lastWritten = change.GetNewValue<string>() ?? lastWritten;
                }
                return ValueTask.CompletedTask;
            },
            bufferTime: TimeSpan.FromMilliseconds(20),
            maxQueueDepth: null,
            logger: NullLogger.Instance);

        using var cancellation = new CancellationTokenSource();
        var processing = processor.ProcessAsync(cancellation.Token);

        // Act - five changes through an unbounded buffer; all must flow, none dropped
        for (var i = 1; i <= 5; i++)
        {
            subject.FirstName = $"v{i}";
        }

        await AsyncTestHelpers.WaitUntilAsync(
            () => lastWritten == "v5",
            message: "The newest change should be flushed");

        // Assert
        Assert.Equal(0, processor.DropCount);

        // Cleanup
        await cancellation.CancelAsync();
        await processing;
    }

    [Fact]
    public async Task WhenTheNewerCommitIsEnqueuedFirst_ThenTheFlushedSurvivorTakesItsNewValue()
    {
        // Arrange - a change is enqueued after its commit and outside the subject lock, so two writers to
        // one property can enqueue in the opposite order they committed. The flush must resolve that by
        // revision, not by queue position.
        var context = InterceptorSubjectContext.Create();
        context.WithRegistry();
        context.WithPropertyChangeSubscriptions();

        var subject = new Person(context);
        var writtenChanges = new List<SubjectPropertyChange>();

        var processor = new ChangeQueueProcessor(
            source: null,
            context: context,
            propertyFilter: _ => true,
            writeHandler: (changes, _) =>
            {
                writtenChanges.AddRange(changes.ToArray());
                return ValueTask.CompletedTask;
            },
            bufferTime: TimeSpan.FromMilliseconds(50),
            maxQueueDepth: null,
            logger: NullLogger.Instance);

        // Act
        var property = new PropertyReference(subject, nameof(Person.FirstName));
        EnqueueChange(processor, property, "Committed1", "Committed2", revision: 2);
        EnqueueChange(processor, property, "Committed0", "Committed1", revision: 1);

        // The higher revision is the committed state, whichever order they were enqueued in.
        subject.FirstName = "Committed2";

        await TriggerFlushAsync(processor);

        processor.Dispose();

        // Assert - the survivor spans the batch: the lowest revision's old value and the highest
        // revision's new value, even though the highest arrived first
        var change = Assert.Single(writtenChanges);
        Assert.Equal("Committed0", change.GetOldValue<string>());
        Assert.Equal("Committed2", change.GetNewValue<string>());
        Assert.Equal(2, change.Revision);
    }

    /// <summary>
    /// The immediate path (no buffer time) has no batch to merge, so it consults the delivered-revision
    /// filter directly. Without that call it would write a commit the source has already moved past,
    /// and nothing else in the suite covers it.
    /// </summary>

    [Fact]
    public async Task WhenAnEchoIsDequeued_ThenAnOlderStragglerIsNotWritten()
    {
        // Arrange: the echo is skipped rather than written, so nothing downstream proves it was seen.
        // Recording it is what suppresses a local commit that predates it. Nothing is delivered for
        // FirstName beforehand on purpose: a delivered change records its own revision, which would
        // suppress the straggler by itself and make this test pass with the bookkeeping removed.
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithPropertyChangeSubscriptions();

        var subject = new Person(context);
        var source = new object();
        var written = new ConcurrentQueue<string?>();

        var firstName = new PropertyReference(subject, nameof(Person.FirstName));
        var lastName = new PropertyReference(subject, nameof(Person.LastName));

        long echoRevision = 0;
        using var observed = firstName.Subscribe((in SubjectPropertyChange change) =>
        {
            if (change.GetNewValue<string>() == "FromSource")
            {
                echoRevision = change.Revision;
            }
        });

        using var processor = new ChangeQueueProcessor(
            source: source,
            context: context,
            propertyFilter: _ => true,
            writeHandler: (changes, _) =>
            {
                foreach (var change in changes.ToArray())
                {
                    written.Enqueue(change.GetNewValue<string>());
                }

                return ValueTask.CompletedTask;
            },
            bufferTime: TimeSpan.FromMilliseconds(5),
            maxQueueDepth: null,
            logger: NullLogger.Instance);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var processing = processor.ProcessAsync(cancellation.Token);

        // Advances the subject's counter so the echo does not land on revision 1, which would leave no
        // usable revision below it (0 orders against nothing and is never suppressed).
        subject.LastName = "Warmup";
        await AsyncTestHelpers.WaitUntilAsync(() => written.Contains("Warmup"));

        // Act: the source pushes a value for FirstName, which this processor skips without writing.
        using (PendingOrigin.Set(firstName, ChangeOrigin.FromSource(source), "FromSource"))
        {
            subject.FirstName = "FromSource";
        }

        // The dequeue loop is FIFO, so seeing this proves the echo ahead of it was already handled.
        subject.LastName = "Fence";
        await AsyncTestHelpers.WaitUntilAsync(() => written.Contains("Fence"));
        Assert.True(echoRevision > 1, "the echo needs a revision with room below it");

        // A commit that predates the echo, arriving late because enqueuing happens after the commit and
        // outside the subject lock. Only the echo's recorded revision can suppress it.
        EnqueueChange(processor, firstName, "Old", "Straggler", echoRevision - 1);
        EnqueueChange(processor, lastName, "Fence", "SecondFence", long.MaxValue);
        subject.LastName = "SecondFence";

        // Assert: the second fence shares the straggler's flush, so its arrival means the straggler was
        // considered and dropped rather than merely still in flight.
        await AsyncTestHelpers.WaitUntilAsync(() => written.Contains("SecondFence"));
        Assert.DoesNotContain("Straggler", written);

        await cancellation.CancelAsync();
        try { await processing; } catch (OperationCanceledException) { /* expected */ }
    }

    /// <summary>
    /// A transaction writes to the source itself and then applies locally, and that apply arrives as a
    /// confirmation. If a write of ours landed on the source in between, the source is left holding the
    /// older commit while the subject holds the confirmed one, and nothing would ever correct it.
    /// </summary>
    [Fact]
    public async Task WhenAConfirmationFollowsOurOwnWrite_ThenItIsSentBackToRepairTheSource()
    {
        // Arrange
        var (context, subject, written, source, processor) = CreateImmediateProcessor();
        using var _ = processor;

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var processing = processor.ProcessAsync(cancellation.Token);

        // Act: our own write reaches the source first, so the source may since have been overwritten.
        subject.FirstName = "Ours";
        await AsyncTestHelpers.WaitUntilAsync(() => written.Contains("Ours"));

        var property = new PropertyReference(subject, nameof(Person.FirstName));
        using (PendingOrigin.Set(property, ChangeOrigin.Confirmed(source), "Confirmed"))
        {
            subject.FirstName = "Confirmed";
        }

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => written.Contains("Confirmed"));

        await cancellation.CancelAsync();
        try { await processing; } catch (OperationCanceledException) { /* expected */ }
    }

    [Fact]
    public async Task WhenAConfirmationDoesNotFollowOurOwnWrite_ThenItIsNotSentBack()
    {
        // Arrange: nothing of ours reached the source for this property, so the source still holds what
        // the transaction wrote and sending it again would be a redundant round trip.
        var (context, subject, written, source, processor) = CreateImmediateProcessor();
        using var _ = processor;

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var processing = processor.ProcessAsync(cancellation.Token);

        // Act
        var property = new PropertyReference(subject, nameof(Person.FirstName));
        using (PendingOrigin.Set(property, ChangeOrigin.Confirmed(source), "Confirmed"))
        {
            subject.FirstName = "Confirmed";
        }

        // A change on another property proves the loop ran past the confirmation rather than stalling.
        subject.LastName = "Other";

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => written.Contains("Other"));
        Assert.DoesNotContain("Confirmed", written);

        await cancellation.CancelAsync();
        try { await processing; } catch (OperationCanceledException) { /* expected */ }
    }

    private static (IInterceptorSubjectContext Context, Person Subject, List<string?> Written, object Source, ChangeQueueProcessor Processor)
        CreateImmediateProcessor()
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithPropertyChangeSubscriptions();

        var subject = new Person(context);
        var written = new List<string?>();
        var source = new object();

        var processor = new ChangeQueueProcessor(
            source: source,
            context: context,
            propertyFilter: _ => true,
            writeHandler: (changes, _) =>
            {
                foreach (var change in changes.ToArray())
                {
                    written.Add(change.GetNewValue<string>());
                }

                return ValueTask.CompletedTask;
            },
            bufferTime: TimeSpan.Zero,
            maxQueueDepth: null,
            logger: NullLogger.Instance);

        return (context, subject, written, source, processor);
    }


    private static void EnqueueChange(
        ChangeQueueProcessor processor,
        PropertyReference property,
        string? oldValue,
        string? newValue,
        long revision = 0)
    {
        // Use reflection to access the private _changes queue
        var changesField = typeof(ChangeQueueProcessor)
            .GetField("_changes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        var queue = (System.Collections.Concurrent.ConcurrentQueue<SubjectPropertyChange>)changesField!.GetValue(processor)!;

        var change = SubjectPropertyChange.Create(
            property,
            ChangeOrigin.Local,
            DateTimeOffset.UtcNow,
            null,
            oldValue,
            newValue,
            revision);

        queue.Enqueue(change);
    }

    private static async Task TriggerFlushAsync(ChangeQueueProcessor processor)
    {
        // Use reflection to call the private TryFlushAsync method
        var tryFlushMethod = typeof(ChangeQueueProcessor)
            .GetMethod("TryFlushAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        var task = (ValueTask)tryFlushMethod!.Invoke(processor, [CancellationToken.None])!;
        await task;
    }
    [Fact]
    public async Task WhenChangeQueuedBeforeProcessingIsSuperseded_ThenOnlyCurrentValueIsWritten()
    {
        // Arrange: changes queued before ProcessAsync starts were captured while the
        // source was still connecting. Such a change whose value the model has since
        // moved past must be dropped instead of pushed back to the source.
        var context = InterceptorSubjectContext.Create();
        context.WithRegistry();
        context.WithPropertyChangeSubscriptions();

        var subject = new Person(context);
        var receivedValues = new ConcurrentQueue<string?>();

        using var processor = new ChangeQueueProcessor(
            source: new object(),
            context: context,
            propertyFilter: _ => true,
            writeHandler: (changes, _) =>
            {
                foreach (var change in changes.ToArray())
                {
                    receivedValues.Enqueue(change.GetNewValue<string>());
                }
                return ValueTask.CompletedTask;
            },
            bufferTime: TimeSpan.Zero,
            maxQueueDepth: null,
            logger: NullLogger.Instance);

        // Act: both writes are queued before processing starts; the second supersedes the first.
        subject.FirstName = "superseded";
        subject.FirstName = "current";

        using var cancellation = new CancellationTokenSource();
        var processing = processor.ProcessAsync(cancellation.Token);

        await AsyncTestHelpers.WaitUntilAsync(
            () => receivedValues.Contains("current"),
            message: "The current value should be written");

        // Assert: the immediate path delivers in order, so once "current" arrived,
        // "superseded" can no longer show up later.
        Assert.DoesNotContain("superseded", receivedValues);

        // Cleanup
        await cancellation.CancelAsync();
        await processing;
    }

    [Fact]
    public void WhenConstructedWithExternalSubscription_ThenDisposeDoesNotDisposeIt()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        context.WithRegistry();
        context.WithPropertyChangeSubscriptions();
        var subject = new Person(context);
        using var subscription = context.CreatePropertyChangeQueueSubscription();

        var processor = new ChangeQueueProcessor(
            source: null,
            subscription: subscription,
            propertyFilter: _ => true,
            writeHandler: (_, _) => ValueTask.CompletedTask,
            bufferTime: TimeSpan.FromMilliseconds(50),
            maxQueueDepth: null,
            logger: NullLogger.Instance);

        // Act
        processor.Dispose();

        // Assert - the externally owned subscription is still capturing (not disposed/completed)
        subject.FirstName = "still-capturing";
        Assert.True(subscription.TryDequeueImmediate(out var change));
        Assert.Equal("still-capturing", change.GetNewValue<string?>());
    }

    [Fact]
    public void WhenDrainingImmediately_ThenReturnsQueuedItemsThenFalse()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        context.WithRegistry();
        context.WithPropertyChangeSubscriptions();
        var subject = new Person(context);
        using var subscription = context.CreatePropertyChangeQueueSubscription();

        subject.FirstName = "A";
        subject.FirstName = "B";

        // Act
        var drained = new List<string?>();
        while (subscription.TryDequeueImmediate(out var change))
        {
            drained.Add(change.GetNewValue<string?>());
        }

        // Assert
        Assert.Equal(["A", "B"], drained);
        Assert.False(subscription.TryDequeueImmediate(out _));
    }

    [Fact]
    public async Task WhenSteadyStateChangesCarryOldTimestamps_ThenEveryChangeIsWritten()
    {
        // Arrange: steady-state changes may carry application-provided timestamps far in
        // the past (device source timestamps, WithChangedTimestamp scopes). Connect-time
        // classification is positional, not timestamp-based, so such changes must never
        // be staleness-checked or dropped, even when the model has already moved on.
        var context = InterceptorSubjectContext.Create();
        context.WithRegistry();
        context.WithPropertyChangeSubscriptions();

        var subject = new Person(context);
        var receivedValues = new ConcurrentQueue<string>();
        var firstWriteReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowFurtherWrites = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var processor = new ChangeQueueProcessor(
            source: new object(),
            context: context,
            propertyFilter: _ => true,
            writeHandler: async (changes, _) =>
            {
                foreach (var change in changes.ToArray())
                {
                    receivedValues.Enqueue(change.GetNewValue<string>()!);
                }
                firstWriteReceived.TrySetResult();
                await allowFurtherWrites.Task;
            },
            bufferTime: TimeSpan.Zero,
            maxQueueDepth: null,
            logger: NullLogger.Instance);

        using var cancellation = new CancellationTokenSource();
        var processing = processor.ProcessAsync(cancellation.Token);

        var oldTimestamp = DateTimeOffset.UtcNow.AddMinutes(-5);

        // Act: the first write blocks the consumer inside the write handler...
        using (SubjectChangeContext.WithChangedTimestamp(oldTimestamp))
        {
            subject.FirstName = "v1";
        }
        await firstWriteReceived.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // ...so these two queue up as steady-state while the model moves on to "v3".
        // A timestamp-based classification would wrongly drop "v2" as superseded.
        using (SubjectChangeContext.WithChangedTimestamp(oldTimestamp))
        {
            subject.FirstName = "v2";
            subject.FirstName = "v3";
        }
        allowFurtherWrites.TrySetResult();

        await AsyncTestHelpers.WaitUntilAsync(
            () => receivedValues.Count == 3,
            message: "All three changes should be written on the immediate path");

        // Assert
        Assert.Equal(["v1", "v2", "v3"], receivedValues.ToArray());

        // Cleanup
        await cancellation.CancelAsync();
        await processing;
    }

}
