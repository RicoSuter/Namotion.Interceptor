using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests;

public class SubjectSourceBaseTests
{
    [Fact]
    public async Task WhenStartingSourceAndPushingChanges_ThenUpdatesAreInCorrectOrder()
    {
        // Arrange
        var subjectContextMock = new Mock<IInterceptorSubjectContext>();
        subjectContextMock
            .Setup(s => s.TryGetService<ISubjectRegistry>())
            .Returns(new SubjectRegistry());

        var subjectMock = new Mock<IInterceptorSubject>();
        subjectMock
            .Setup(s => s.Context)
            .Returns(subjectContextMock.Object);

        var updates = new List<string>();
        var source = new TestSubjectSource(subjectMock.Object, subjectContextMock.Object, NullLogger.Instance)
        {
            StartListeningOverride = (propertyWriter, _) =>
            {
                propertyWriter.Write(updates, u => u.Add("Update1"));
                propertyWriter.Write(updates, u => u.Add("Update2"));
                return Task.FromResult<IAsyncDisposable?>(null);
            },
            LoadInitialStateOverride = _ =>
                Task.FromResult<Action?>(() => updates.Add("Complete")),
            WriteChangesOverride = (_, _) => ValueTask.FromResult(WriteResult.Success),
        };

        var cancellationTokenSource = new CancellationTokenSource();

        // Act
        await source.StartAsync(cancellationTokenSource.Token);
        await AsyncTestHelpers.WaitUntilAsync(() => updates.Count >= 3,
            message: "Expected 3 updates (Complete + Update1 + Update2)");
        await source.StopAsync(cancellationTokenSource.Token);

        await cancellationTokenSource.CancelAsync();

        // Assert

        // first apply complete state
        Assert.Equal("Complete", updates.ElementAt(0));

        // then replay since requesting complete state
        Assert.Equal("Update1", updates.ElementAt(1));
        Assert.Equal("Update2", updates.ElementAt(2));
    }

    [Fact]
    public async Task WhenPropertyChangeIsTriggered_ThenWriteToSourceAsyncIsCalled()
    {
        // Arrange
        var propertyChangedChannel = new PropertyChangeQueue();

        var context = new InterceptorSubjectContext();
        context.WithRegistry();
        context.AddService(propertyChangedChannel);

        var subject = new Person(context);

        SubjectPropertyChange[]? changes = null;
        var source = new TestSubjectSource(subject, context, NullLogger.Instance)
        {
            WriteChangesOverride = (c, _) =>
            {
                changes = c.ToArray();
                return ValueTask.FromResult(WriteResult.Success);
            },
        };

        // Claim ownership of the property
        new PropertyReference(subject, nameof(Person.FirstName)).SetSource(source);

        var cancellationTokenSource = new CancellationTokenSource();

        // Act
        await source.StartAsync(cancellationTokenSource.Token);

        var writeContext = new PropertyWriteContext<string?>(
            subject.GetPropertyReference(nameof(Person.FirstName)), null, "Bar");

        propertyChangedChannel.WriteProperty(ref writeContext, (ref _) => { });

        await AsyncTestHelpers.WaitUntilAsync(() => changes != null,
            message: "Expected WriteChangesAsync to be called");
        await source.StopAsync(cancellationTokenSource.Token);

        await cancellationTokenSource.CancelAsync();

        // Assert
        Assert.NotNull(changes);
        Assert.Equal("Bar", changes.First().GetNewValue<string?>());
    }

    [Fact]
    public async Task WhenWriteChangesThrowsException_ThenErrorIsLoggedAndServiceContinues()
    {
        // Arrange
        var propertyChangedChannel = new PropertyChangeQueue();

        var context = new InterceptorSubjectContext();
        context.WithRegistry();
        context.AddService(propertyChangedChannel);

        var subject = new Person(context);

        var tcs = new TaskCompletionSource();
        var source = new TestSubjectSource(subject, context, NullLogger.Instance)
        {
            WriteChangesOverride = (_, _) =>
            {
                tcs.TrySetResult();
                throw new Exception("Connection failed");
            },
        };

        // Claim ownership of the property
        new PropertyReference(subject, nameof(Person.FirstName)).SetSource(source);

        // Act
        await source.StartAsync(CancellationToken.None);

        var writeContext = new PropertyWriteContext<string?>(
            subject.GetPropertyReference(nameof(Person.FirstName)), null, "Test");
        propertyChangedChannel.WriteProperty(ref writeContext, (ref _) => { });

        // Wait for the write to be attempted
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await source.StopAsync(CancellationToken.None);

        // Assert - service processed the write (exception was logged, not thrown)
        Assert.True(tcs.Task.IsCompleted);
    }

    [Fact]
    public async Task WhenWriteChangesThrowsOperationCanceled_ThenServiceStops()
    {
        // Arrange
        var propertyChangedChannel = new PropertyChangeQueue();

        var context = new InterceptorSubjectContext();
        context.WithRegistry();
        context.AddService(propertyChangedChannel);

        var subject = new Person(context);

        var tcs = new TaskCompletionSource();
        var source = new TestSubjectSource(subject, context, NullLogger.Instance)
        {
            WriteChangesOverride = (_, _) =>
            {
                tcs.TrySetResult();
                throw new OperationCanceledException();
            },
        };

        // Claim ownership of the property
        new PropertyReference(subject, nameof(Person.FirstName)).SetSource(source);

        // Act
        await source.StartAsync(CancellationToken.None);

        var writeContext = new PropertyWriteContext<string?>(
            subject.GetPropertyReference(nameof(Person.FirstName)), null, "Test");
        propertyChangedChannel.WriteProperty(ref writeContext, (ref _) => { });

        // Wait for the write to be attempted
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await source.StopAsync(CancellationToken.None);

        // Assert - write was attempted (OperationCanceledException propagated up)
        Assert.True(tcs.Task.IsCompleted);
    }

    [Fact]
    public async Task WhenFlushFails_ThenChangesAreEnqueued()
    {
        // Arrange
        var propertyChangedChannel = new PropertyChangeQueue();

        var context = new InterceptorSubjectContext();
        context.WithRegistry();
        context.AddService(propertyChangedChannel);

        var subject = new Person(context);

        // First call fails (simulates queued items failing to flush), second succeeds
        var callCount = 0;
        var firstCallTcs = new TaskCompletionSource();
        var secondCallTcs = new TaskCompletionSource();
        var source = new TestSubjectSource(subject, context, NullLogger.Instance,
            bufferTime: TimeSpan.Zero) // Disable buffering for immediate writes
        {
            WriteChangesOverride = (changes, _) =>
            {
                callCount++;
                if (callCount == 1)
                {
                    firstCallTcs.TrySetResult();
                    return new ValueTask<WriteResult>(WriteResult.Failure(changes, new Exception("First call fails")));
                }
                secondCallTcs.TrySetResult();
                return new ValueTask<WriteResult>(WriteResult.Success);
            },
        };

        // Claim ownership of the property
        new PropertyReference(subject, nameof(Person.FirstName)).SetSource(source);

        // Act
        await source.StartAsync(CancellationToken.None);

        // First change - will fail and be queued
        var writeContext1 = new PropertyWriteContext<string?>(
            subject.GetPropertyReference(nameof(Person.FirstName)), null, "First");
        propertyChangedChannel.WriteProperty(ref writeContext1, (ref _) => { });

        // Wait for first write to be attempted before triggering second
        await firstCallTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Second change - will succeed and flush the queued item
        var writeContext2 = new PropertyWriteContext<string?>(
            subject.GetPropertyReference(nameof(Person.FirstName)), "First", "Second");
        propertyChangedChannel.WriteProperty(ref writeContext2, (ref _) => { });

        await secondCallTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await source.StopAsync(CancellationToken.None);

        // Assert - both writes were attempted (first failed and was retried)
        Assert.True(callCount >= 2);
    }

    [Fact]
    public async Task WhenWriteChangesInBatchesThrowsOperationCanceled_ThenExceptionPropagates()
    {
        // Arrange
        var propertyChangedChannel = new PropertyChangeQueue();

        var context = new InterceptorSubjectContext();
        context.WithRegistry();
        context.AddService(propertyChangedChannel);

        var subject = new Person(context);

        var tcs = new TaskCompletionSource();
        var source = new TestSubjectSource(subject, context, NullLogger.Instance)
        {
            WriteChangesOverride = (_, _) =>
            {
                tcs.TrySetResult();
                throw new OperationCanceledException();
            },
        };

        // Claim ownership of the property
        new PropertyReference(subject, nameof(Person.FirstName)).SetSource(source);

        // Act
        await source.StartAsync(CancellationToken.None);

        var writeContext = new PropertyWriteContext<string?>(
            subject.GetPropertyReference(nameof(Person.FirstName)), null, "Test");
        propertyChangedChannel.WriteProperty(ref writeContext, (ref _) => { });

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await source.StopAsync(CancellationToken.None);

        // Assert - OperationCanceledException was thrown (propagates up)
        Assert.True(tcs.Task.IsCompleted);
    }

    [Fact]
    public async Task WhenWriteChangesInBatchesThrowsException_ThenChangesAreEnqueued()
    {
        // Arrange
        var propertyChangedChannel = new PropertyChangeQueue();

        var context = new InterceptorSubjectContext();
        context.WithRegistry();
        context.AddService(propertyChangedChannel);

        var subject = new Person(context);

        var callCount = 0;
        var firstCallTcs = new TaskCompletionSource();
        var secondCallTcs = new TaskCompletionSource();
        var source = new TestSubjectSource(subject, context, NullLogger.Instance,
            bufferTime: TimeSpan.Zero) // Disable buffering for immediate writes
        {
            WriteChangesOverride = (changes, _) =>
            {
                callCount++;
                if (callCount == 1)
                {
                    firstCallTcs.TrySetResult();
                    return new ValueTask<WriteResult>(WriteResult.Failure(changes, new Exception("Connection failed")));
                }
                secondCallTcs.TrySetResult();
                return new ValueTask<WriteResult>(WriteResult.Success);
            },
        };

        // Claim ownership of the property
        new PropertyReference(subject, nameof(Person.FirstName)).SetSource(source);

        // Act
        await source.StartAsync(CancellationToken.None);

        // First change fails, second triggers retry
        var writeContext1 = new PropertyWriteContext<string?>(
            subject.GetPropertyReference(nameof(Person.FirstName)), null, "First");
        propertyChangedChannel.WriteProperty(ref writeContext1, (ref _) => { });

        // Wait for first write to be attempted before triggering second
        await firstCallTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var writeContext2 = new PropertyWriteContext<string?>(
            subject.GetPropertyReference(nameof(Person.FirstName)), "First", "Second");
        propertyChangedChannel.WriteProperty(ref writeContext2, (ref _) => { });

        await secondCallTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await source.StopAsync(CancellationToken.None);

        // Assert - changes were enqueued and retried
        Assert.True(callCount >= 2);
    }

    [Fact]
    public async Task WhenWriteReturnsFailureResult_ThenFailedChangesAreEnqueuedAndRetried()
    {
        // Arrange
        var propertyChangedChannel = new PropertyChangeQueue();

        var context = new InterceptorSubjectContext();
        context.WithRegistry();
        context.AddService(propertyChangedChannel);

        var subject = new Person(context);

        var allWrittenValues = new ConcurrentBag<string?[]>();
        var callCount = 0;
        var firstCallTcs = new TaskCompletionSource();
        var thirdCallTcs = new TaskCompletionSource();
        var source = new TestSubjectSource(subject, context, NullLogger.Instance,
            bufferTime: TimeSpan.Zero)
        {
            WriteChangesOverride = (changes, _) =>
            {
                var current = Interlocked.Increment(ref callCount);
                allWrittenValues.Add(changes.ToArray().Select(c => c.GetNewValue<string?>()).ToArray());

                if (current == 1)
                {
                    firstCallTcs.TrySetResult();
                    // First write fails - WriteResult.Failure should cause SubjectSourceBase to enqueue
                    return new ValueTask<WriteResult>(WriteResult.Failure(changes, new Exception("Transient error")));
                }

                if (current >= 3)
                {
                    thirdCallTcs.TrySetResult();
                }

                return new ValueTask<WriteResult>(WriteResult.Success);
            },
        };

        // Claim ownership of the property
        new PropertyReference(subject, nameof(Person.FirstName)).SetSource(source);

        // Act
        await source.StartAsync(CancellationToken.None);

        // First change - will return WriteResult.Failure, should be enqueued for retry
        var writeContext1 = new PropertyWriteContext<string?>(
            subject.GetPropertyReference(nameof(Person.FirstName)), null, "FailedValue");
        propertyChangedChannel.WriteProperty(ref writeContext1, (ref _) => { });
        await firstCallTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Second change - triggers retry queue flush (retrying first), then writes second
        var writeContext2 = new PropertyWriteContext<string?>(
            subject.GetPropertyReference(nameof(Person.FirstName)), "FailedValue", "SecondValue");
        propertyChangedChannel.WriteProperty(ref writeContext2, (ref _) => { });

        // Wait for retry flush + new write (3 total calls)
        await thirdCallTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await source.StopAsync(CancellationToken.None);

        // Assert
        // 3 calls expected:
        //   1. "FailedValue" -> Failure (enqueued to retry queue)
        //   2. "FailedValue" -> Success (retry from queue flush)
        //   3. "SecondValue" -> Success (new write)
        Assert.True(callCount >= 3,
            $"Expected at least 3 write calls (initial + retry + new), got {callCount}");

        // Verify the failed value was retried (appears in at least 2 calls)
        var failedValueCallCount = allWrittenValues.Count(batch =>
            batch.Any(v => v == "FailedValue"));
        Assert.True(failedValueCallCount >= 2,
            $"Expected 'FailedValue' in at least 2 write calls (original + retry), appeared in {failedValueCallCount}");
    }

    // Optimistic retry re-apply tests

    [Fact]
    public async Task WhenRetryQueueHasNonConflictingValueChange_ThenChangeIsReappliedAndSentToServer()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        var subject = new Person(context) { FirstName = "Original" };

        var (source, writtenChanges, writeTcs) = CreateSourceWithRetryQueue(subject, context,
            initialStateAction: s => { subject.FirstName = "Original"; }); // Server didn't change it

        // Pre-fill retry queue: client changed "Original" -> "ClientChange"
        EnqueueRetryChange(source, subject, nameof(Person.FirstName), "Original", "ClientChange");

        // Act
        await source.StartAsync(CancellationToken.None);
        await writeTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await source.StopAsync(CancellationToken.None);

        // Assert - change was re-applied locally and sent to server
        Assert.Equal("ClientChange", subject.FirstName);
        Assert.Contains(writtenChanges, c =>
            c.Property.Name == nameof(Person.FirstName) &&
            c.GetNewValue<string?>() == "ClientChange");
    }

    [Fact]
    public async Task WhenRetryQueueHasConflictingValueChange_ThenChangeIsDropped()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        var subject = new Person(context) { FirstName = "Original" };

        var (source, writtenChanges, _) = CreateSourceWithRetryQueue(subject, context,
            initialStateAction: s => { subject.FirstName = "ServerChanged"; }); // Server DID change it

        // Pre-fill retry queue: client changed "Original" -> "ClientChange"
        EnqueueRetryChange(source, subject, nameof(Person.FirstName), "Original", "ClientChange");

        // Act
        await source.StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => source.WriteRetryQueue!.IsEmpty,
            message: "Expected retry queue to be drained by ReapplyRetryQueue");
        await source.StopAsync(CancellationToken.None);

        // Assert - server wins, change was dropped
        Assert.Equal("ServerChanged", subject.FirstName);
        Assert.DoesNotContain(writtenChanges, c => c.Property.Name == nameof(Person.FirstName));
    }

    [Fact]
    public async Task WhenRetryQueueHasNonConflictingObjectRefChange_ThenChangeIsReapplied()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        var personA = new Person(context) { FirstName = "A" };
        var personB = new Person(context) { FirstName = "B" };
        var subject = new Person(context) { Father = personA };

        var (source, _, writeTcs) = CreateSourceWithRetryQueue(subject, context,
            initialStateAction: s => { subject.Father = personA; }); // Server didn't change it

        // Pre-fill retry queue: client changed Father from personA -> personB
        EnqueueRetryChange<Person?>(source, subject, nameof(Person.Father), personA, personB);

        // Act
        await source.StartAsync(CancellationToken.None);
        await writeTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await source.StopAsync(CancellationToken.None);

        // Assert - re-applied
        Assert.Same(personB, subject.Father);
    }

    [Fact]
    public async Task WhenRetryQueueHasConflictingObjectRefChange_ThenChangeIsDropped()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        var personA = new Person(context) { FirstName = "A" };
        var personB = new Person(context) { FirstName = "B" };
        var personC = new Person(context) { FirstName = "C" };
        var subject = new Person(context) { Father = personA };

        var (source, _, _) = CreateSourceWithRetryQueue(subject, context,
            initialStateAction: s => { subject.Father = personC; }); // Server replaced with C

        // Pre-fill retry queue: client changed Father from personA -> personB
        EnqueueRetryChange<Person?>(source, subject, nameof(Person.Father), personA, personB);

        // Act
        await source.StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => source.WriteRetryQueue!.IsEmpty,
            message: "Expected retry queue to be drained by ReapplyRetryQueue");
        await source.StopAsync(CancellationToken.None);

        // Assert - server wins
        Assert.Same(personC, subject.Father);
    }

    [Fact]
    public async Task WhenRetryQueueHasNonConflictingCollectionChange_ThenChangeIsReapplied()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        var listA = new List<Person>();
        var listB = new List<Person> { new Person(context) { FirstName = "Child" } };
        var subject = new Person(context) { Children = listA };

        var (source, _, writeTcs) = CreateSourceWithRetryQueue(subject, context,
            initialStateAction: s => { subject.Children = listA; }); // Server didn't replace it

        // Pre-fill retry queue: client replaced collection listA -> listB
        EnqueueRetryChange(source, subject, nameof(Person.Children), listA, listB);

        // Act
        await source.StartAsync(CancellationToken.None);
        await writeTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await source.StopAsync(CancellationToken.None);

        // Assert - re-applied (reference equality)
        Assert.Same(listB, subject.Children);
    }

    [Fact]
    public async Task WhenRetryQueueHasConflictingCollectionChange_ThenChangeIsDropped()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        var listA = new List<Person>();
        var listB = new List<Person> { new Person(context) { FirstName = "ClientChild" } };
        var listC = new List<Person> { new Person(context) { FirstName = "ServerChild" } };
        var subject = new Person(context) { Children = listA };

        var (source, _, _) = CreateSourceWithRetryQueue(subject, context,
            initialStateAction: s => { subject.Children = listC; }); // Server replaced collection

        // Pre-fill retry queue: client replaced listA -> listB
        EnqueueRetryChange(source, subject, nameof(Person.Children), listA, listB);

        // Act
        await source.StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => source.WriteRetryQueue!.IsEmpty,
            message: "Expected retry queue to be drained by ReapplyRetryQueue");
        await source.StopAsync(CancellationToken.None);

        // Assert - server wins
        Assert.Same(listC, subject.Children);
    }

    [Fact]
    public async Task WhenRetryQueueHasMixedChanges_ThenOnlyNonConflictingAreReapplied()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        var subject = new Person(context) { FirstName = "OrigFirst", LastName = "OrigLast" };

        var (source, writtenChanges, writeTcs) = CreateSourceWithRetryQueue(subject, context,
            initialStateAction: s =>
            {
                subject.FirstName = "ServerFirst"; // Server changed this -> conflict
                subject.LastName = "OrigLast";     // Server didn't change this -> no conflict
            });

        EnqueueRetryChange(source, subject, nameof(Person.FirstName), "OrigFirst", "ClientFirst");
        EnqueueRetryChange(source, subject, nameof(Person.LastName), "OrigLast", "ClientLast");

        // Act
        await source.StartAsync(CancellationToken.None);
        await writeTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await source.StopAsync(CancellationToken.None);

        // Assert - FirstName dropped (server wins), LastName re-applied
        Assert.Equal("ServerFirst", subject.FirstName);
        Assert.Equal("ClientLast", subject.LastName);
        Assert.DoesNotContain(writtenChanges, c => c.Property.Name == nameof(Person.FirstName));
        Assert.Contains(writtenChanges, c =>
            c.Property.Name == nameof(Person.LastName) &&
            c.GetNewValue<string?>() == "ClientLast");
    }

    [Fact]
    public async Task WhenAllRetryChangesConflict_ThenAllDroppedAndServiceRunsNormally()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        var subject = new Person(context) { FirstName = "OrigFirst", LastName = "OrigLast" };

        var (source, writtenChanges, _) = CreateSourceWithRetryQueue(subject, context,
            initialStateAction: s =>
            {
                subject.FirstName = "ServerFirst";
                subject.LastName = "ServerLast";
            });

        EnqueueRetryChange(source, subject, nameof(Person.FirstName), "OrigFirst", "ClientFirst");
        EnqueueRetryChange(source, subject, nameof(Person.LastName), "OrigLast", "ClientLast");

        // Act
        await source.StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => source.WriteRetryQueue!.IsEmpty,
            message: "Expected retry queue to be drained by ReapplyRetryQueue");
        await source.StopAsync(CancellationToken.None);

        // Assert - all dropped, server values remain
        Assert.Equal("ServerFirst", subject.FirstName);
        Assert.Equal("ServerLast", subject.LastName);
        Assert.Empty(writtenChanges);
    }

    [Fact]
    public async Task WhenRetryQueueHasNullValues_ThenNullEqualsNullAndChangeIsReapplied()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        var subject = new Person(context); // FirstName starts as null

        var (source, writtenChanges, writeTcs) = CreateSourceWithRetryQueue(subject, context,
            initialStateAction: s => { }); // Server didn't set it either - stays null

        // Pre-fill retry queue: client changed null -> "ClientValue"
        EnqueueRetryChange(source, subject, nameof(Person.FirstName), null, "ClientValue");

        // Act
        await source.StartAsync(CancellationToken.None);
        await writeTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await source.StopAsync(CancellationToken.None);

        // Assert - null == null -> non-conflicting, re-applied
        Assert.Equal("ClientValue", subject.FirstName);
        Assert.Contains(writtenChanges, c =>
            c.Property.Name == nameof(Person.FirstName) &&
            c.GetNewValue<string?>() == "ClientValue");
    }

    [Fact]
    public async Task WhenRetryQueueHasNullOldValueButServerSetValue_ThenChangeIsDropped()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        var subject = new Person(context); // FirstName starts as null

        var (source, writtenChanges, _) = CreateSourceWithRetryQueue(subject, context,
            initialStateAction: s => { subject.FirstName = "ServerValue"; }); // Server set it

        // Pre-fill retry queue: client changed null -> "ClientValue"
        EnqueueRetryChange(source, subject, nameof(Person.FirstName), null, "ClientValue");

        // Act
        await source.StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => source.WriteRetryQueue!.IsEmpty,
            message: "Expected retry queue to be drained by ReapplyRetryQueue");
        await source.StopAsync(CancellationToken.None);

        // Assert - null != "ServerValue" -> conflict, dropped
        Assert.Equal("ServerValue", subject.FirstName);
        Assert.DoesNotContain(writtenChanges, c => c.Property.Name == nameof(Person.FirstName));
    }

    [Fact]
    public async Task WhenRetryQueueIsEmpty_ThenInitializationSucceedsNormally()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        var subject = new Person(context) { FirstName = "Original" };

        var (source, writtenChanges, _) = CreateSourceWithRetryQueue(subject, context,
            initialStateAction: s => { subject.FirstName = "ServerValue"; });

        // No retry changes enqueued

        // Act
        await source.StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => subject.FirstName == "ServerValue",
            message: "Expected initial state to be applied");
        await source.StopAsync(CancellationToken.None);

        // Assert - Initial state applied, no retry changes sent
        Assert.Equal("ServerValue", subject.FirstName);
        Assert.Empty(writtenChanges);
    }

    [Fact]
    public async Task WhenRetryQueueDisabled_ThenInitializationSucceedsWithoutReapply()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        var subject = new Person(context) { FirstName = "Original" };

        // writeRetryQueueSize: 0 disables the queue
        var source = new TestSubjectSource(subject, context, NullLogger.Instance,
            writeRetryQueueSize: 0)
        {
            LoadInitialStateOverride = _ => Task.FromResult<Action?>(() =>
            {
                subject.FirstName = "ServerValue";
            }),
            WriteChangesOverride = (_, _) => new ValueTask<WriteResult>(WriteResult.Success),
        };

        new PropertyReference(subject, nameof(Person.FirstName)).SetSource(source);

        // Act
        await source.StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => subject.FirstName == "ServerValue",
            message: "Expected initial state to be applied");
        await source.StopAsync(CancellationToken.None);

        // Assert - service runs normally without retry queue
        Assert.Equal("ServerValue", subject.FirstName);
    }

    [Fact]
    public async Task WhenRetryChangeThrowsDuringReapply_ThenRemainingChangesAreStillProcessed()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        var subject = new Person(context) { FirstName = "Original", LastName = "Original" };

        var (source, writtenChanges, writeTcs) = CreateSourceWithRetryQueue(subject, context,
            initialStateAction: s => { }); // Server didn't change anything

        // Enqueue a change with a bogus property name - Metadata access will throw
        EnqueueRetryChange(source, subject, "NonExistentProperty", "old", "new");

        // Enqueue a valid change after the broken one
        EnqueueRetryChange(source, subject, nameof(Person.LastName), "Original", "ClientLast");

        // Act
        await source.StartAsync(CancellationToken.None);
        await writeTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await source.StopAsync(CancellationToken.None);

        // Assert - broken change failed, but LastName was still re-applied
        Assert.Equal("ClientLast", subject.LastName);
        Assert.Contains(writtenChanges, c =>
            c.Property.Name == nameof(Person.LastName) &&
            c.GetNewValue<string?>() == "ClientLast");
    }

    private static (TestSubjectSource source,
        ConcurrentBag<SubjectPropertyChange> writtenChanges, TaskCompletionSource writeTcs)
        CreateSourceWithRetryQueue(Person subject, IInterceptorSubjectContext context,
            Action<TestSubjectSource> initialStateAction)
    {
        var writtenChanges = new ConcurrentBag<SubjectPropertyChange>();
        var writeTcs = new TaskCompletionSource();

        TestSubjectSource? source = null;
        source = new TestSubjectSource(subject, context, NullLogger.Instance,
            bufferTime: TimeSpan.FromMilliseconds(50))
        {
            LoadInitialStateOverride = _ => Task.FromResult<Action?>(() =>
            {
                initialStateAction(source!);
            }),
            WriteChangesOverride = (changes, _) =>
            {
                foreach (var change in changes.ToArray())
                {
                    writtenChanges.Add(change);
                }
                writeTcs.TrySetResult();
                return new ValueTask<WriteResult>(WriteResult.Success);
            },
        };

        // Claim source ownership for common properties
        new PropertyReference(subject, nameof(Person.FirstName)).SetSource(source);
        new PropertyReference(subject, nameof(Person.LastName)).SetSource(source);
        new PropertyReference(subject, nameof(Person.Father)).SetSource(source);
        new PropertyReference(subject, nameof(Person.Mother)).SetSource(source);
        new PropertyReference(subject, nameof(Person.Children)).SetSource(source);

        return (source, writtenChanges, writeTcs);
    }

    private static void EnqueueRetryChange(TestSubjectSource source,
        IInterceptorSubject subject, string propertyName, string? oldValue, string? newValue)
    {
        EnqueueRetryChange<string?>(source, subject, propertyName, oldValue, newValue);
    }

    private static void EnqueueRetryChange<TValue>(TestSubjectSource source,
        IInterceptorSubject subject, string propertyName, TValue oldValue, TValue newValue)
    {
        var queue = source.WriteRetryQueue!;

        var change = SubjectPropertyChange.Create(
            new PropertyReference(subject, propertyName),
            ChangeOrigin.Local,
            DateTimeOffset.UtcNow,
            null,
            oldValue,
            newValue);

        queue.Enqueue(new[] { change });
    }

    [Fact]
    public async Task WhenStartListeningAsyncFails_ThenRetriesAndEventuallySucceeds()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        var subject = new Person(context);

        var callCount = 0;
        var source = new TestSubjectSource(subject, context, NullLogger.Instance,
            retryTime: TimeSpan.FromMilliseconds(50))
        {
            StartListeningOverride = (_, _) =>
            {
                var current = Interlocked.Increment(ref callCount);
                if (current <= 2)
                {
                    throw new InvalidOperationException($"Simulated failure #{current}");
                }
                return Task.FromResult<IAsyncDisposable?>(null);
            },
            LoadInitialStateOverride = _ =>
                Task.FromResult<Action?>(() => { subject.FirstName = "Loaded"; }),
            WriteChangesOverride = (_, _) => ValueTask.FromResult(WriteResult.Success),
        };

        // Act
        await source.StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => subject.FirstName == "Loaded",
            message: "Expected source to retry and eventually load initial state");
        await source.StopAsync(CancellationToken.None);

        // Assert
        Assert.True(callCount >= 3, $"Expected at least 3 calls (2 failures + 1 success), got {callCount}");
        Assert.Equal("Loaded", subject.FirstName);
    }

    [Fact]
    public async Task WhenStartListeningOverrideSpawnsTaskAndThrows_ThenSpawnedTaskIsCleanedUpBeforeRethrow()
    {
        // Spec section 11 R2: per-connector StartListeningAsync overrides must own their
        // spawned background tasks (OPC UA session-health, MQTT connection-monitor,
        // WebSocket receive+monitor) and clean them up if the override throws after
        // the task is spawned. This is a guard test for the cleanup-before-rethrow
        // pattern at the abstraction level all three connectors share.

        // Arrange
        var subjectContextMock = new Mock<IInterceptorSubjectContext>();
        subjectContextMock
            .Setup(s => s.TryGetService<ISubjectRegistry>())
            .Returns(new SubjectRegistry());

        var subjectMock = new Mock<IInterceptorSubject>();
        subjectMock
            .Setup(s => s.Context)
            .Returns(subjectContextMock.Object);

        var spawnCount = 0;
        var spawnedTaskCancelled = false;
        var spawnedTaskCompleted = false;
        var cleanupRan = false;

        var source = new TestSubjectSource(
            subjectMock.Object,
            subjectContextMock.Object,
            NullLogger.Instance,
            retryTime: TimeSpan.FromMilliseconds(50))
        {
            StartListeningOverride = async (_, listenToken) =>
            {
                CancellationTokenSource? spawnCts = null;
                Task? spawnedTask = null;
                try
                {
                    spawnCts = CancellationTokenSource.CreateLinkedTokenSource(listenToken);
                    var cts = spawnCts;
                    spawnedTask = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(Timeout.Infinite, cts.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            spawnedTaskCancelled = true;
                        }
                        finally
                        {
                            spawnedTaskCompleted = true;
                        }
                    });

                    Interlocked.Increment(ref spawnCount);
                    throw new InvalidOperationException("simulated post-spawn failure");
                }
                catch
                {
                    cleanupRan = true;
                    if (spawnCts is not null)
                    {
                        try { await spawnCts.CancelAsync().ConfigureAwait(false); } catch { /* best effort */ }
                    }
                    if (spawnedTask is not null)
                    {
                        try { await spawnedTask.ConfigureAwait(false); } catch { /* expected */ }
                    }
                    if (spawnCts is not null)
                    {
                        try { spawnCts.Dispose(); } catch { /* best effort */ }
                    }
                    throw;
                }
            },
        };

        var cancellationTokenSource = new CancellationTokenSource();

        // Act
        await source.StartAsync(cancellationTokenSource.Token);
        await AsyncTestHelpers.WaitUntilAsync(
            () => Volatile.Read(ref spawnCount) >= 1 && cleanupRan && spawnedTaskCompleted,
            message: "Expected the override to have spawned a task, thrown, and cleaned up.");
        await source.StopAsync(cancellationTokenSource.Token);
        await cancellationTokenSource.CancelAsync();

        // Assert
        Assert.True(cleanupRan, "Cleanup-on-failure block should run before re-throwing.");
        Assert.True(spawnedTaskCancelled, "Spawned task should observe cancellation from the cleanup helper.");
        Assert.True(spawnedTaskCompleted, "Spawned task should run to completion (cancelled), not be left dangling.");
    }
}
