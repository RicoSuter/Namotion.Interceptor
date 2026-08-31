using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests;

public class ChangeQueueProcessorTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TeardownWaitTimeout = TimeSpan.FromSeconds(10);

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
            logger: NullLogger.Instance,
            deliveryRule: ChangeDeliveryRule.SourceValuesMayBeStale);

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
            logger: NullLogger.Instance,
            deliveryRule: ChangeDeliveryRule.SourceValuesMayBeStale);

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
            logger: NullLogger.Instance,
            deliveryRule: ChangeDeliveryRule.SourceValuesMayBeStale);

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
            logger: NullLogger.Instance,
            deliveryRule: ChangeDeliveryRule.SourceValuesMayBeStale);

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
            logger: NullLogger.Instance,
            deliveryRule: ChangeDeliveryRule.SourceValuesMayBeStale);

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
            logger: NullLogger.Instance,
            deliveryRule: ChangeDeliveryRule.SourceValuesMayBeStale);

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
            logger: NullLogger.Instance,
            deliveryRule: ChangeDeliveryRule.SourceValuesMayBeStale);

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
            logger: NullLogger.Instance,
            deliveryRule: ChangeDeliveryRule.SourceValuesMayBeStale);

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
            logger: NullLogger.Instance,
            deliveryRule: ChangeDeliveryRule.SourceValuesMayBeStale);

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

    [Fact]
    public async Task WhenAnEchoIsDequeued_ThenAnOlderLocalWriteIsStillWritten()
    {
        // Arrange: an echo carries a value the source produced before it saw our write, and its revision
        // is stamped when we apply it locally, not when the source produced it (issue #373), so it cannot
        // rank against our writes. Suppressing a local commit against it would be permanent: the echo is
        // skipped rather than written, so no later change carries the value in the dropped one's place
        // and both ends settle on the value the source already had.
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
        using var observed = firstName.SubscribeInline((in SubjectPropertyChange change) =>
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
            logger: NullLogger.Instance,
            deliveryRule: ChangeDeliveryRule.SourceValuesMayBeStale);

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
        // outside the subject lock.
        EnqueueChange(processor, firstName, "Old", "Straggler", echoRevision - 1);
        EnqueueChange(processor, lastName, "Fence", "SecondFence", long.MaxValue);
        subject.LastName = "SecondFence";

        // Assert: the second fence shares the straggler's flush, so its arrival means the straggler was
        // considered rather than merely still in flight, and it was written.
        await AsyncTestHelpers.WaitUntilAsync(() => written.Contains("SecondFence"));
        Assert.Contains("Straggler", written);

        await cancellation.CancelAsync();
        try { await processing; } catch (OperationCanceledException) { /* expected */ }
    }

    /// <summary>
    /// The mirror of the echo case, for a source that hosts the state it serves. A client writing to an
    /// OPC UA server lands in the server's own node tree before the subject ever sees it, so the value is
    /// already settled there when we apply it. An older local commit arriving late must not be pushed over
    /// it: the apply is skipped as an echo, so nothing would then carry the client's value back, leaving
    /// the node on our older value and the subject on the client's, permanently.
    /// </summary>
    [Fact]
    public async Task WhenTheSourceAlreadyHoldsWhatItPushed_ThenAnOlderLocalWriteIsNotWrittenBack()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithPropertyChangeSubscriptions();

        var subject = new Person(context);
        var source = new object();
        var written = new ConcurrentQueue<string?>();

        var firstName = new PropertyReference(subject, nameof(Person.FirstName));
        var lastName = new PropertyReference(subject, nameof(Person.LastName));

        long inboundRevision = 0;
        using var observed = firstName.SubscribeInline((in SubjectPropertyChange change) =>
        {
            if (change.GetNewValue<string>() == "FromClient")
            {
                inboundRevision = change.Revision;
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
            logger: NullLogger.Instance,
            deliveryRule: ChangeDeliveryRule.SourceValuesAreSettled);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var processing = processor.ProcessAsync(cancellation.Token);

        // Leaves a usable revision below the inbound one, which 1 would not.
        subject.LastName = "Warmup";
        await AsyncTestHelpers.WaitUntilAsync(() => written.Contains("Warmup"));

        // Act: a client writes into the server's own store, and we apply what it wrote.
        using (PendingOrigin.Set(firstName, ChangeOrigin.FromSource(source), "FromClient"))
        {
            subject.FirstName = "FromClient";
        }

        // The dequeue loop is FIFO, so seeing this proves the apply ahead of it was already handled.
        subject.LastName = "Fence";
        await AsyncTestHelpers.WaitUntilAsync(() => written.Contains("Fence"));
        Assert.True(inboundRevision > 1, "the inbound apply needs a revision with room below it");

        // A commit that predates the client's write, arriving late because enqueuing happens after the
        // commit and outside the subject lock.
        EnqueueChange(processor, firstName, "Old", "Straggler", inboundRevision - 1);
        EnqueueChange(processor, lastName, "Fence", "SecondFence", long.MaxValue);
        subject.LastName = "SecondFence";

        // Assert: the second fence shares the straggler's flush, so its arrival means the straggler was
        // decided rather than merely still in flight.
        await AsyncTestHelpers.WaitUntilAsync(() => written.Contains("SecondFence"));
        Assert.DoesNotContain("Straggler", written);

        await cancellation.CancelAsync();
        try { await processing; } catch (OperationCanceledException) { /* expected */ }
    }

    /// <summary>
    /// A zero buffer time has no batch to merge, so the flush path's supersession check never runs there.
    /// A server must still not serve a value it has moved past, or the whole rule is inoperative for any
    /// connector configured that way.
    /// </summary>
    [Fact]
    public async Task WhenAServerHasNoBufferTime_ThenASupersededChangeIsStillNotWritten()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithPropertyChangeSubscriptions();

        var subject = new Person(context);
        var source = new object();
        var written = new ConcurrentQueue<string?>();

        var firstName = new PropertyReference(subject, nameof(Person.FirstName));
        var lastName = new PropertyReference(subject, nameof(Person.LastName));

        // Owned here rather than by the processor, because the immediate path reads straight from the
        // subscription and never touches the buffer the other tests inject into.
        using var subscription = context.CreatePropertyChangeQueueSubscription();

        using var processor = new ChangeQueueProcessor(
            source: source,
            subscription: subscription,
            propertyFilter: _ => true,
            writeHandler: (changes, _) =>
            {
                foreach (var change in changes.ToArray())
                {
                    written.Enqueue(change.GetNewValue<string>());
                }

                return ValueTask.CompletedTask;
            },
            bufferTime: TimeSpan.Zero,
            maxQueueDepth: null,
            logger: NullLogger.Instance,
            deliveryRule: ChangeDeliveryRule.SourceValuesAreSettled);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var processing = processor.ProcessAsync(cancellation.Token);

        subject.FirstName = "Warmup";
        await AsyncTestHelpers.WaitUntilAsync(() => written.Contains("Warmup"));

        subject.FirstName = "Settled";
        await AsyncTestHelpers.WaitUntilAsync(() => written.Contains("Settled"));
        Assert.True(firstName.TryGetWriteState(includeSourceCommitsInRevision: true, out var settledRevision, out _));

        // Act: a commit that predates the settled one, arriving late.
        subscription.Enqueue(SubjectPropertyChange.Create(
            firstName, ChangeOrigin.Local, DateTimeOffset.UtcNow, null,
            "Old", "Straggler", settledRevision - 1));

        subscription.Enqueue(SubjectPropertyChange.Create(
            lastName, ChangeOrigin.Local, DateTimeOffset.UtcNow, null,
            null, "Fence", long.MaxValue));

        // Assert: the fence is behind the straggler in a FIFO queue, so its arrival proves the straggler
        // was decided rather than still in flight.
        await AsyncTestHelpers.WaitUntilAsync(() => written.Contains("Fence"));
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

    /// <summary>
    /// The rule has no default so that omitting it is a compile error, but `default` and a literal 0
    /// still compile in a required parameter and would quietly select a rule. Both wrong choices lose
    /// data with no diagnostic, so the zero value is rejected rather than treated as a rule.
    /// </summary>
    [Fact]
    public void WhenTheDeliveryRuleIsUnspecified_ThenTheProcessorIsRejected()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithPropertyChangeSubscriptions();

        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChangeQueueProcessor(
            source: new object(),
            context: context,
            propertyFilter: _ => true,
            writeHandler: (_, _) => ValueTask.CompletedTask,
            bufferTime: TimeSpan.FromMilliseconds(8),
            maxQueueDepth: null,
            logger: NullLogger.Instance,
            deliveryRule: default));
    }

    /// <summary>
    /// Any value outside the two named ones has to be rejected here as well, not just the zero one. The
    /// delivery decision throws on an unknown rule, and it runs inside the flush, outside the try that
    /// wraps the write handler, so it would escape the periodic loop's catch and end delivery for the
    /// processor's lifetime while the dequeue loop kept filling the buffer.
    /// </summary>
    [Fact]
    public void WhenTheDeliveryRuleIsNotAKnownValue_ThenTheProcessorIsRejected()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithPropertyChangeSubscriptions();

        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChangeQueueProcessor(
            source: new object(),
            context: context,
            propertyFilter: _ => true,
            writeHandler: (_, _) => ValueTask.CompletedTask,
            bufferTime: TimeSpan.FromMilliseconds(8),
            maxQueueDepth: null,
            logger: NullLogger.Instance,
            deliveryRule: (ChangeDeliveryRule)3));
    }

    private static (IInterceptorSubjectContext Context, Person Subject, ConcurrentQueue<string?> Written, object Source, ChangeQueueProcessor Processor)
        CreateImmediateProcessor()
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithPropertyChangeSubscriptions();

        var subject = new Person(context);
        var written = new ConcurrentQueue<string?>();
        var source = new object();

        var processor = new ChangeQueueProcessor(
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
            bufferTime: TimeSpan.Zero,
            maxQueueDepth: null,
            logger: NullLogger.Instance,
            deliveryRule: ChangeDeliveryRule.SourceValuesMayBeStale);

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
            logger: NullLogger.Instance,
            deliveryRule: ChangeDeliveryRule.SourceValuesMayBeStale);

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
            logger: NullLogger.Instance,
            deliveryRule: ChangeDeliveryRule.SourceValuesMayBeStale);

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
            logger: NullLogger.Instance,
            deliveryRule: ChangeDeliveryRule.SourceValuesMayBeStale);

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

    [Fact]
    public async Task WhenProcessingStopsWithBufferedChanges_ThenTheyAreStillWritten()
    {
        // Arrange: a buffer time no periodic tick reaches during the test, so the change is taken off the
        // subscription and left in the processor buffer, where only the teardown drain can deliver it.
        // Nothing else can recover it there: it is gone from the subscription a source would drain.
        var context = InterceptorSubjectContext.Create();
        context.WithRegistry();
        context.WithPropertyChangeSubscriptions();

        var subject = new Person(context);
        var writtenValues = new ConcurrentQueue<string>();

        using var processor = new ChangeQueueProcessor(
            source: new object(),
            context: context,
            propertyFilter: _ => true,
            writeHandler: (changes, _) =>
            {
                foreach (var change in changes.ToArray())
                {
                    writtenValues.Enqueue(change.GetNewValue<string>()!);
                }
                return ValueTask.CompletedTask;
            },
            bufferTime: TimeSpan.FromMinutes(5),
            maxQueueDepth: null,
            logger: NullLogger.Instance,
            deliveryRule: ChangeDeliveryRule.SourceValuesMayBeStale);

        using var cancellation = new CancellationTokenSource();
        var processing = processor.ProcessAsync(cancellation.Token);

        subject.FirstName = "buffered";
        await AsyncTestHelpers.WaitUntilAsync(
            () => processor.QueueDepth == 1,
            message: "The change should be buffered by the processor before it stops");

        // Act
        await cancellation.CancelAsync();
        await processing;

        // Assert
        Assert.Equal(["buffered"], writtenValues.ToArray());
    }

    [Fact]
    public async Task WhenAnImmediateWriteIgnoresCancellation_ThenStoppingEndsAtTheBoundAndCountsItOnce()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithRegistry()
            .WithPropertyChangeSubscriptions();
        var subject = new Person(context);
        var writeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var processor = new ChangeQueueProcessor(
            source: new object(),
            context: context,
            propertyFilter: _ => true,
            writeHandler: async (_, _) =>
            {
                writeEntered.TrySetResult();
                await releaseWrite.Task.ConfigureAwait(false);
            },
            bufferTime: TimeSpan.Zero,
            maxQueueDepth: null,
            logger: NullLogger.Instance,
            deliveryRule: ChangeDeliveryRule.SourceValuesMayBeStale);

        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var watchdogRegistration = watchdog.Token.Register(() => releaseWrite.TrySetResult());
        using var cancellation = new CancellationTokenSource();
        var processing = processor.ProcessAsync(cancellation.Token);
        subject.FirstName = "immediate";
        await writeEntered.Task.WaitAsync(TestTimeout);

        try
        {
            // Act
            await cancellation.CancelAsync();
            await processing.WaitAsync(TeardownWaitTimeout);

            // Assert
            Assert.Equal(1, processor.DropCount);
            Assert.False(releaseWrite.Task.IsCompleted);
        }
        finally
        {
            releaseWrite.TrySetResult();
        }

        await processing.WaitAsync(TestTimeout);
        Assert.Equal(1, processor.DropCount);
    }

    [Fact]
    public async Task WhenABufferedChangeFinishesFilteringAfterTheDeadline_ThenTheHandlerIsNotInvoked()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithRegistry()
            .WithPropertyChangeSubscriptions();
        var subject = new Person(context);
        using var subscription = context.CreatePropertyChangeQueueSubscription();
        var filterEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFilter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completionReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writeHandlerEntered = false;

        using var processor = new ChangeQueueProcessor(
            source: new object(),
            subscription: subscription,
            propertyFilter: _ =>
            {
                filterEntered.TrySetResult();
                releaseFilter.Task.GetAwaiter().GetResult();
                return true;
            },
            writeHandler: (_, _) =>
            {
                writeHandlerEntered = true;
                return ValueTask.CompletedTask;
            },
            bufferTime: TimeSpan.FromMinutes(5),
            maxQueueDepth: null,
            logger: NullLogger.Instance,
            deliveryRule: ChangeDeliveryRule.SourceValuesMayBeStale,
            completionHandler: _ =>
            {
                completionReached.TrySetResult();
                return ValueTask.CompletedTask;
            });

        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var watchdogRegistration = watchdog.Token.Register(() => releaseFilter.TrySetResult());
        using var cancellation = new CancellationTokenSource();
        var processing = processor.ProcessAsync(cancellation.Token);
        subject.FirstName = "buffered";
        await filterEntered.Task.WaitAsync(TestTimeout);

        try
        {
            // Act
            await cancellation.CancelAsync();
            await processing.WaitAsync(TeardownWaitTimeout);
            processor.Dispose();
            releaseFilter.TrySetResult();
            await completionReached.Task.WaitAsync(TestTimeout);

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(
                () => processor.DropCount == 1,
                message: "The accepted late filter result should be rejected by closed delivery.");
            Assert.False(writeHandlerEntered);
            Assert.Equal(1, processor.DropCount);
        }
        finally
        {
            releaseFilter.TrySetResult();
        }
    }

    [Fact]
    public async Task WhenTheCompletionFlushIsCancelledAtTheDeadline_ThenTheCompletionHandlerStillRuns()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithRegistry()
            .WithPropertyChangeSubscriptions();
        var subject = new Person(context);
        using var subscription = context.CreatePropertyChangeQueueSubscription();
        var writeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completionCancellation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var processor = new ChangeQueueProcessor(
            source: new object(),
            subscription: subscription,
            propertyFilter: _ => true,
            writeHandler: async (_, teardownToken) =>
            {
                writeEntered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, teardownToken).ConfigureAwait(false);
            },
            bufferTime: TimeSpan.FromMinutes(5),
            maxQueueDepth: null,
            logger: NullLogger.Instance,
            deliveryRule: ChangeDeliveryRule.SourceValuesMayBeStale,
            completionHandler: teardownToken =>
            {
                completionCancellation.TrySetResult(teardownToken.IsCancellationRequested);
                return ValueTask.CompletedTask;
            });

        using var cancellation = new CancellationTokenSource();
        var processing = processor.ProcessAsync(cancellation.Token);
        subject.FirstName = "buffered";
        await AsyncTestHelpers.WaitUntilAsync(
            () => processor.QueueDepth == 1,
            message: "The change should reach the processor buffer before cancellation.");

        // Act
        await cancellation.CancelAsync();
        await writeEntered.Task.WaitAsync(TestTimeout);
        await processing.WaitAsync(TeardownWaitTimeout);

        // Assert
        Assert.True(await completionCancellation.Task.WaitAsync(TeardownWaitTimeout));
        Assert.Equal(1, processor.DropCount);
    }

    [Fact]
    public async Task WhenABufferedWriteIsCancelledBeforeTheDeadline_ThenMergedSurvivorsAreRetried()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithRegistry()
            .WithPropertyChangeSubscriptions();
        var subject = new Person(context);
        var firstAttemptStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completionAttemptFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = new ConcurrentQueue<SubjectPropertyChange[]>();
        var attemptCount = 0;
        subject.FirstName = "latest";
        subject.LastName = "survivor";
        using var subscription = context.CreatePropertyChangeQueueSubscription();

        using var processor = new ChangeQueueProcessor(
            source: new object(),
            subscription: subscription,
            propertyFilter: _ => true,
            writeHandler: async (changes, cancellationToken) =>
            {
                attempts.Enqueue(changes.ToArray());
                if (Interlocked.Increment(ref attemptCount) == 1)
                {
                    firstAttemptStarted.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    completionAttemptFinished.TrySetResult();
                }
            },
            bufferTime: TimeSpan.FromMilliseconds(10),
            maxQueueDepth: null,
            logger: NullLogger.Instance,
            deliveryRule: ChangeDeliveryRule.SourceValuesMayBeStale);

        var firstName = new PropertyReference(subject, nameof(Person.FirstName));
        var lastName = new PropertyReference(subject, nameof(Person.LastName));
        EnqueueChange(processor, firstName, null, "superseded", revision: 1);
        EnqueueChange(processor, lastName, null, "survivor", revision: 2);
        EnqueueChange(processor, firstName, "superseded", "latest", revision: 3);

        using var cancellation = new CancellationTokenSource();
        var processing = processor.ProcessAsync(cancellation.Token);
        await firstAttemptStarted.Task.WaitAsync(TestTimeout);

        // Act
        await cancellation.CancelAsync();
        await completionAttemptFinished.Task.WaitAsync(TestTimeout);
        await processing.WaitAsync(TestTimeout);

        // Assert
        var deliveredAttempts = attempts.ToArray();
        Assert.Equal(2, deliveredAttempts.Length);
        Assert.Equal(
            [nameof(Person.LastName), nameof(Person.FirstName)],
            deliveredAttempts[0].Select(change => change.Property.Name));
        Assert.Equal(
            [nameof(Person.LastName), nameof(Person.FirstName)],
            deliveredAttempts[1].Select(change => change.Property.Name));
        Assert.Equal(["survivor", "latest"],
            deliveredAttempts[1].Select(change => change.GetNewValue<string>()));
        Assert.Equal(0, processor.DropCount);
    }

    [Fact]
    public async Task WhenMergedCancellationSettlesAfterTerminalClose_ThenSurvivorsAreCountedOnce()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithRegistry()
            .WithPropertyChangeSubscriptions();
        var subject = new Person(context)
        {
            FirstName = "latest",
            LastName = "survivor"
        };
        using var subscription = context.CreatePropertyChangeQueueSubscription();
        var writeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCancellation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completionReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var processor = new ChangeQueueProcessor(
            source: new object(),
            subscription: subscription,
            propertyFilter: _ => true,
            writeHandler: async (_, cancellationToken) =>
            {
                writeEntered.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    cancellationObserved.TrySetResult();
                    await releaseCancellation.Task.ConfigureAwait(false);
                    throw;
                }
            },
            bufferTime: TimeSpan.FromMilliseconds(10),
            maxQueueDepth: null,
            logger: NullLogger.Instance,
            deliveryRule: ChangeDeliveryRule.SourceValuesMayBeStale,
            terminalHandler: () => releaseCancellation.TrySetResult(),
            completionHandler: _ =>
            {
                completionReached.TrySetResult();
                return ValueTask.CompletedTask;
            });

        var firstName = new PropertyReference(subject, nameof(Person.FirstName));
        var lastName = new PropertyReference(subject, nameof(Person.LastName));
        EnqueueChange(processor, firstName, null, "superseded", revision: 1);
        EnqueueChange(processor, lastName, null, "survivor", revision: 2);
        EnqueueChange(processor, firstName, "superseded", "latest", revision: 3);

        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var watchdogRegistration = watchdog.Token.Register(() => releaseCancellation.TrySetResult());
        using var cancellation = new CancellationTokenSource();
        var processing = processor.ProcessAsync(cancellation.Token);
        await writeEntered.Task.WaitAsync(TestTimeout);

        // Act
        await cancellation.CancelAsync();
        await cancellationObserved.Task.WaitAsync(TeardownWaitTimeout);
        await processing.WaitAsync(TeardownWaitTimeout);
        await completionReached.Task.WaitAsync(TestTimeout);

        // Assert
        Assert.Equal(2, processor.DropCount);
        Assert.Equal(0, processor.QueueDepth);
    }

    [Fact]
    public async Task WhenTheDropCallbackBlocks_ThenStoppingStillEndsAtTheBound()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithRegistry()
            .WithPropertyChangeSubscriptions();
        var subject = new Person(context);
        var writeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dropCallbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDropCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var processor = new ChangeQueueProcessor(
            source: new object(),
            context: context,
            propertyFilter: _ => true,
            writeHandler: async (_, _) =>
            {
                writeEntered.TrySetResult();
                await releaseWrite.Task.ConfigureAwait(false);
            },
            bufferTime: TimeSpan.Zero,
            maxQueueDepth: null,
            logger: NullLogger.Instance,
            deliveryRule: ChangeDeliveryRule.SourceValuesMayBeStale,
            dropHandler: count =>
            {
                if (count > 0)
                {
                    dropCallbackEntered.TrySetResult();
                    releaseDropCallback.Task.GetAwaiter().GetResult();
                }
            });

        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var watchdogRegistration = watchdog.Token.Register(() =>
        {
            releaseWrite.TrySetResult();
            releaseDropCallback.TrySetResult();
        });
        using var cancellation = new CancellationTokenSource();
        var processing = processor.ProcessAsync(cancellation.Token);
        subject.FirstName = "immediate";
        await writeEntered.Task.WaitAsync(TestTimeout);

        try
        {
            // Act
            await cancellation.CancelAsync();
            await dropCallbackEntered.Task.WaitAsync(TeardownWaitTimeout);
            await processing.WaitAsync(TeardownWaitTimeout);

            // Assert
            Assert.False(releaseDropCallback.Task.IsCompleted);
            Assert.Equal(1, processor.DropCount);
        }
        finally
        {
            releaseDropCallback.TrySetResult();
            releaseWrite.TrySetResult();
        }
    }

    [Fact]
    public async Task WhenACancellationCallbackBlocks_ThenStoppingStillEndsAtTheBound()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithRegistry()
            .WithPropertyChangeSubscriptions();
        var subject = new Person(context);
        var writeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationCallbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCancellationCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var processor = new ChangeQueueProcessor(
            source: new object(),
            context: context,
            propertyFilter: _ => true,
            writeHandler: async (_, processingToken) =>
            {
                using var registration = processingToken.Register(() =>
                {
                    cancellationCallbackEntered.TrySetResult();
                    releaseCancellationCallback.Task.GetAwaiter().GetResult();
                });
                writeEntered.TrySetResult();
                await releaseWrite.Task.ConfigureAwait(false);
            },
            bufferTime: TimeSpan.Zero,
            maxQueueDepth: null,
            logger: NullLogger.Instance,
            deliveryRule: ChangeDeliveryRule.SourceValuesMayBeStale);

        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var watchdogRegistration = watchdog.Token.Register(() =>
        {
            releaseWrite.TrySetResult();
            releaseCancellationCallback.TrySetResult();
        });
        using var cancellation = new CancellationTokenSource();
        var processing = processor.ProcessAsync(cancellation.Token);
        subject.FirstName = "immediate";
        await writeEntered.Task.WaitAsync(TestTimeout);

        try
        {
            // Act
            var cancelling = cancellation.CancelAsync();
            await cancellationCallbackEntered.Task.WaitAsync(TeardownWaitTimeout);
            await Task.WhenAll(cancelling, processing).WaitAsync(TeardownWaitTimeout);

            // Assert
            Assert.False(releaseCancellationCallback.Task.IsCompleted);
            Assert.Equal(1, processor.DropCount);
        }
        finally
        {
            releaseCancellationCallback.TrySetResult();
            releaseWrite.TrySetResult();
        }
    }

    [Fact]
    public async Task WhenTheLoggerBlocks_ThenStoppingStillEndsAtTheBound()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithRegistry()
            .WithPropertyChangeSubscriptions();
        var subject = new Person(context);
        var writeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loggerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLogger = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var logger = new BlockingTeardownLogger(loggerEntered, releaseLogger);

        using var processor = new ChangeQueueProcessor(
            source: new object(),
            context: context,
            propertyFilter: _ => true,
            writeHandler: async (_, _) =>
            {
                writeEntered.TrySetResult();
                await releaseWrite.Task.ConfigureAwait(false);
            },
            bufferTime: TimeSpan.Zero,
            maxQueueDepth: null,
            logger: logger,
            deliveryRule: ChangeDeliveryRule.SourceValuesMayBeStale);

        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var watchdogRegistration = watchdog.Token.Register(() =>
        {
            releaseWrite.TrySetResult();
            releaseLogger.TrySetResult();
        });
        using var cancellation = new CancellationTokenSource();
        var processing = processor.ProcessAsync(cancellation.Token);
        subject.FirstName = "immediate";
        await writeEntered.Task.WaitAsync(TestTimeout);

        try
        {
            // Act
            await cancellation.CancelAsync();
            await loggerEntered.Task.WaitAsync(TeardownWaitTimeout);
            await processing.WaitAsync(TeardownWaitTimeout);

            // Assert
            Assert.False(releaseLogger.Task.IsCompleted);
            Assert.Equal(1, processor.DropCount);
        }
        finally
        {
            releaseLogger.TrySetResult();
            releaseWrite.TrySetResult();
        }
    }

    [Fact]
    public async Task WhenTheTeardownWriteBlocksAndIgnoresCancellation_ThenStoppingStillCompletes()
    {
        // Arrange: a write handler that blocks synchronously and never reads its token, which is what the
        // OPC UA server does while it holds the SDK's node manager lock. The token the drain writes under
        // therefore bounds nothing, so a stop that only asked for cancellation would wait here as long as
        // the handler cares to block.
        var context = InterceptorSubjectContext.Create();
        context.WithRegistry();
        context.WithPropertyChangeSubscriptions();

        var subject = new Person(context);
        var writeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var processor = new ChangeQueueProcessor(
            source: new object(),
            context: context,
            propertyFilter: _ => true,
            writeHandler: (_, _) =>
            {
                writeEntered.TrySetResult();
                releaseWrite.Task.GetAwaiter().GetResult();
                return ValueTask.CompletedTask;
            },
            bufferTime: TimeSpan.FromMinutes(5),
            maxQueueDepth: null,
            logger: NullLogger.Instance,
            deliveryRule: ChangeDeliveryRule.SourceValuesMayBeStale);

        // Lets the handler out even when nothing else does, so a regression of the bound fails this test
        // instead of wedging the run on a permanently blocked thread.
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var watchdogRegistration = watchdog.Token.Register(() => releaseWrite.TrySetResult());

        using var cancellation = new CancellationTokenSource();
        var processing = processor.ProcessAsync(cancellation.Token);

        subject.FirstName = "buffered";
        await AsyncTestHelpers.WaitUntilAsync(
            () => processor.QueueDepth == 1,
            message: "The change should be buffered by the processor before it stops");

        try
        {
            // Act
            await cancellation.CancelAsync();
            await writeEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await processing.WaitAsync(TimeSpan.FromSeconds(30));

            // Assert: the stop completed while the handler was still inside the write, which nothing but
            // an externally enforced deadline can do.
            Assert.False(releaseWrite.Task.IsCompleted,
                "The drain waited for the blocked write handler instead of abandoning it at the deadline.");
        }
        finally
        {
            releaseWrite.TrySetResult();
        }
    }

    private sealed class BlockingTeardownLogger(
        TaskCompletionSource loggerEntered,
        TaskCompletionSource releaseLogger) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning && formatter(state, exception).StartsWith("Gave up waiting"))
            {
                loggerEntered.TrySetResult();
                releaseLogger.Task.GetAwaiter().GetResult();
            }
        }
    }
}
