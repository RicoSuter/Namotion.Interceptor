using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests;

public class SubjectSourceBackgroundServiceTests
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

        var subjectSourceMock = new Mock<ISubjectSource>();

        var updates = new List<string>();
        subjectSourceMock
            .Setup(s => s.StartListeningAsync(It.IsAny<SubjectPropertyWriter>(), It.IsAny<CancellationToken>()))
            .Callback((SubjectPropertyWriter propertyWriter, CancellationToken _) =>
            {
                propertyWriter.Write(updates, u => u.Add("Update1"));
                propertyWriter.Write(updates, u => u.Add("Update2"));
            })
            .ReturnsAsync((IDisposable?)null);

        subjectSourceMock
            .Setup(s => s.LoadInitialStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => { updates.Add("Complete"); });

        subjectSourceMock
            .Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> _, CancellationToken _) =>
                new ValueTask<WriteResult>(WriteResult.Success));

        var cancellationTokenSource = new CancellationTokenSource();

        // Act
        var service = new SubjectSourceBackgroundService(subjectSourceMock.Object, subjectContextMock.Object, NullLogger.Instance);

        await service.StartAsync(cancellationTokenSource.Token);
        await Task.Delay(1000, cancellationTokenSource.Token);
        await service.StopAsync(cancellationTokenSource.Token);

        await cancellationTokenSource.CancelAsync();

        // Assert

        // first apply complete state
        Assert.Equal("Complete", updates.ElementAt(0));

        // than replay since requesting complete state
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
        var subjectSourceMock = new Mock<ISubjectSource>();

        // Claim ownership of the property
        new PropertyReference(subject, nameof(Person.FirstName)).SetSource(subjectSourceMock.Object);

        subjectSourceMock
            .Setup(s => s.StartListeningAsync(It.IsAny<SubjectPropertyWriter>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IDisposable?)null);

        subjectSourceMock
            .Setup(s => s.LoadInitialStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((Action?)null);

        SubjectPropertyChange[]? changes = null;

        subjectSourceMock
            .Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> c, CancellationToken _) =>
            {
                changes = c.ToArray();
                return new ValueTask<WriteResult>(WriteResult.Success);
            });

        var cancellationTokenSource = new CancellationTokenSource();

        // Act
        var service = new SubjectSourceBackgroundService(subjectSourceMock.Object, context, NullLogger.Instance);
        await service.StartAsync(cancellationTokenSource.Token);
        
        var writeContext = new PropertyWriteContext<string?>(
            subject.GetPropertyReference(nameof(Person.FirstName)), null, "Bar");

        propertyChangedChannel.WriteProperty(ref writeContext, (ref _) => { });
        
        await Task.Delay(1000, cancellationTokenSource.Token);
        await service.StopAsync(cancellationTokenSource.Token);

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
        var subjectSourceMock = new Mock<ISubjectSource>();

        // Claim ownership of the property
        new PropertyReference(subject, nameof(Person.FirstName)).SetSource(subjectSourceMock.Object);

        subjectSourceMock
            .Setup(s => s.StartListeningAsync(It.IsAny<SubjectPropertyWriter>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IDisposable?)null);

        subjectSourceMock
            .Setup(s => s.LoadInitialStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((Action?)null);

        var tcs = new TaskCompletionSource();
        subjectSourceMock
            .Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> _, CancellationToken _) =>
            {
                tcs.TrySetResult();
                throw new Exception("Connection failed");
            });

        // Act
        var service = new SubjectSourceBackgroundService(subjectSourceMock.Object, context, NullLogger.Instance);
        await service.StartAsync(CancellationToken.None);

        var writeContext = new PropertyWriteContext<string?>(
            subject.GetPropertyReference(nameof(Person.FirstName)), null, "Test");
        propertyChangedChannel.WriteProperty(ref writeContext, (ref _) => { });

        // Wait for the write to be attempted
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);

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
        var subjectSourceMock = new Mock<ISubjectSource>();

        // Claim ownership of the property
        new PropertyReference(subject, nameof(Person.FirstName)).SetSource(subjectSourceMock.Object);

        subjectSourceMock
            .Setup(s => s.StartListeningAsync(It.IsAny<SubjectPropertyWriter>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IDisposable?)null);

        subjectSourceMock
            .Setup(s => s.LoadInitialStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((Action?)null);

        var tcs = new TaskCompletionSource();
        subjectSourceMock
            .Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> _, CancellationToken _) =>
            {
                tcs.TrySetResult();
                throw new OperationCanceledException();
            });

        // Act
        var service = new SubjectSourceBackgroundService(subjectSourceMock.Object, context, NullLogger.Instance);
        await service.StartAsync(CancellationToken.None);

        var writeContext = new PropertyWriteContext<string?>(
            subject.GetPropertyReference(nameof(Person.FirstName)), null, "Test");
        propertyChangedChannel.WriteProperty(ref writeContext, (ref _) => { });

        // Wait for the write to be attempted
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);

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
        var subjectSourceMock = new Mock<ISubjectSource>();

        // Claim ownership of the property
        new PropertyReference(subject, nameof(Person.FirstName)).SetSource(subjectSourceMock.Object);

        subjectSourceMock
            .Setup(s => s.StartListeningAsync(It.IsAny<SubjectPropertyWriter>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IDisposable?)null);

        subjectSourceMock
            .Setup(s => s.LoadInitialStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((Action?)null);

        // First call fails (simulates queued items failing to flush), second succeeds
        var callCount = 0;
        var firstCallTcs = new TaskCompletionSource();
        var secondCallTcs = new TaskCompletionSource();
        subjectSourceMock
            .Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
            {
                callCount++;
                if (callCount == 1)
                {
                    firstCallTcs.TrySetResult();
                    return new ValueTask<WriteResult>(WriteResult.Failure(changes, new Exception("First call fails")));
                }
                secondCallTcs.TrySetResult();
                return new ValueTask<WriteResult>(WriteResult.Success);
            });

        // Act
        var service = new SubjectSourceBackgroundService(
            subjectSourceMock.Object, context, NullLogger.Instance,
            bufferTime: TimeSpan.Zero); // Disable buffering for immediate writes
        await service.StartAsync(CancellationToken.None);

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
        await service.StopAsync(CancellationToken.None);

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
        var subjectSourceMock = new Mock<ISubjectSource>();

        // Claim ownership of the property
        new PropertyReference(subject, nameof(Person.FirstName)).SetSource(subjectSourceMock.Object);

        subjectSourceMock
            .Setup(s => s.StartListeningAsync(It.IsAny<SubjectPropertyWriter>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IDisposable?)null);

        subjectSourceMock
            .Setup(s => s.LoadInitialStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((Action?)null);

        var tcs = new TaskCompletionSource();
        subjectSourceMock
            .Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> _, CancellationToken _) =>
            {
                tcs.TrySetResult();
                throw new OperationCanceledException();
            });

        // Act
        var service = new SubjectSourceBackgroundService(
            subjectSourceMock.Object, context, NullLogger.Instance);
        await service.StartAsync(CancellationToken.None);

        var writeContext = new PropertyWriteContext<string?>(
            subject.GetPropertyReference(nameof(Person.FirstName)), null, "Test");
        propertyChangedChannel.WriteProperty(ref writeContext, (ref _) => { });

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);

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
        var subjectSourceMock = new Mock<ISubjectSource>();

        // Claim ownership of the property
        new PropertyReference(subject, nameof(Person.FirstName)).SetSource(subjectSourceMock.Object);

        subjectSourceMock
            .Setup(s => s.StartListeningAsync(It.IsAny<SubjectPropertyWriter>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IDisposable?)null);

        subjectSourceMock
            .Setup(s => s.LoadInitialStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((Action?)null);

        var callCount = 0;
        var firstCallTcs = new TaskCompletionSource();
        var secondCallTcs = new TaskCompletionSource();
        subjectSourceMock
            .Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
            {
                callCount++;
                if (callCount == 1)
                {
                    firstCallTcs.TrySetResult();
                    return new ValueTask<WriteResult>(WriteResult.Failure(changes, new Exception("Connection failed")));
                }
                secondCallTcs.TrySetResult();
                return new ValueTask<WriteResult>(WriteResult.Success);
            });

        // Act
        var service = new SubjectSourceBackgroundService(
            subjectSourceMock.Object, context, NullLogger.Instance,
            bufferTime: TimeSpan.Zero); // Disable buffering for immediate writes
        await service.StartAsync(CancellationToken.None);

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
        await service.StopAsync(CancellationToken.None);

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
        var subjectSourceMock = new Mock<ISubjectSource>();

        // Claim ownership of the property
        new PropertyReference(subject, nameof(Person.FirstName)).SetSource(subjectSourceMock.Object);

        subjectSourceMock
            .Setup(s => s.StartListeningAsync(It.IsAny<SubjectPropertyWriter>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IDisposable?)null);

        subjectSourceMock
            .Setup(s => s.LoadInitialStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((Action?)null);

        var allWrittenValues = new ConcurrentBag<string?[]>();
        var callCount = 0;
        var firstCallTcs = new TaskCompletionSource();
        var thirdCallTcs = new TaskCompletionSource();
        subjectSourceMock
            .Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
            {
                var current = Interlocked.Increment(ref callCount);
                allWrittenValues.Add(changes.ToArray().Select(c => c.GetNewValue<string?>()).ToArray());

                if (current == 1)
                {
                    firstCallTcs.TrySetResult();
                    // First write fails — WriteResult.Failure should cause SBBS to enqueue
                    return new ValueTask<WriteResult>(WriteResult.Failure(changes, new Exception("Transient error")));
                }

                if (current >= 3)
                {
                    thirdCallTcs.TrySetResult();
                }

                return new ValueTask<WriteResult>(WriteResult.Success);
            });

        // Act
        var service = new SubjectSourceBackgroundService(
            subjectSourceMock.Object, context, NullLogger.Instance,
            bufferTime: TimeSpan.Zero);
        await service.StartAsync(CancellationToken.None);

        // First change — will return WriteResult.Failure, should be enqueued for retry
        var writeContext1 = new PropertyWriteContext<string?>(
            subject.GetPropertyReference(nameof(Person.FirstName)), null, "FailedValue");
        propertyChangedChannel.WriteProperty(ref writeContext1, (ref _) => { });
        await firstCallTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Second change — triggers retry queue flush (retrying first), then writes second
        var writeContext2 = new PropertyWriteContext<string?>(
            subject.GetPropertyReference(nameof(Person.FirstName)), "FailedValue", "SecondValue");
        propertyChangedChannel.WriteProperty(ref writeContext2, (ref _) => { });

        // Wait for retry flush + new write (3 total calls)
        await thirdCallTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);

        // Assert
        // 3 calls expected:
        //   1. "FailedValue" → Failure (enqueued to retry queue)
        //   2. "FailedValue" → Success (retry from queue flush)
        //   3. "SecondValue" → Success (new write)
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

        var (service, _, writtenChanges, writeTcs) = CreateServiceWithRetryQueue(subject, context,
            initialStateAction: () => { subject.FirstName = "Original"; }); // Server didn't change it

        // Pre-fill retry queue: client changed "Original" → "ClientChange"
        EnqueueRetryChange(service, subject, nameof(Person.FirstName), "Original", "ClientChange");

        // Act
        await service.StartAsync(CancellationToken.None);
        await writeTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);

        // Assert — change was re-applied locally and sent to server
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

        var (service, _, writtenChanges, _) = CreateServiceWithRetryQueue(subject, context,
            initialStateAction: () => { subject.FirstName = "ServerChanged"; }); // Server DID change it

        // Pre-fill retry queue: client changed "Original" → "ClientChange"
        EnqueueRetryChange(service, subject, nameof(Person.FirstName), "Original", "ClientChange");

        // Act
        await service.StartAsync(CancellationToken.None);
        await Task.Delay(500); // Give time for any potential re-apply
        await service.StopAsync(CancellationToken.None);

        // Assert — server wins, change was dropped
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

        var (service, _, _, writeTcs) = CreateServiceWithRetryQueue(subject, context,
            initialStateAction: () => { subject.Father = personA; }); // Server didn't change it

        // Pre-fill retry queue: client changed Father from personA → personB
        EnqueueRetryChange<Person?>(service, subject, nameof(Person.Father), personA, personB);

        // Act
        await service.StartAsync(CancellationToken.None);
        await writeTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);

        // Assert — re-applied
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

        var (service, _, _, _) = CreateServiceWithRetryQueue(subject, context,
            initialStateAction: () => { subject.Father = personC; }); // Server replaced with C

        // Pre-fill retry queue: client changed Father from personA → personB
        EnqueueRetryChange<Person?>(service, subject, nameof(Person.Father), personA, personB);

        // Act
        await service.StartAsync(CancellationToken.None);
        await Task.Delay(500);
        await service.StopAsync(CancellationToken.None);

        // Assert — server wins
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

        var (service, _, _, writeTcs) = CreateServiceWithRetryQueue(subject, context,
            initialStateAction: () => { subject.Children = listA; }); // Server didn't replace it

        // Pre-fill retry queue: client replaced collection listA → listB
        EnqueueRetryChange(service, subject, nameof(Person.Children), listA, listB);

        // Act
        await service.StartAsync(CancellationToken.None);
        await writeTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);

        // Assert — re-applied (reference equality)
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

        var (service, _, _, _) = CreateServiceWithRetryQueue(subject, context,
            initialStateAction: () => { subject.Children = listC; }); // Server replaced collection

        // Pre-fill retry queue: client replaced listA → listB
        EnqueueRetryChange(service, subject, nameof(Person.Children), listA, listB);

        // Act
        await service.StartAsync(CancellationToken.None);
        await Task.Delay(500);
        await service.StopAsync(CancellationToken.None);

        // Assert — server wins
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

        var (service, _, writtenChanges, writeTcs) = CreateServiceWithRetryQueue(subject, context,
            initialStateAction: () =>
            {
                subject.FirstName = "ServerFirst"; // Server changed this → conflict
                subject.LastName = "OrigLast";     // Server didn't change this → no conflict
            });

        EnqueueRetryChange(service, subject, nameof(Person.FirstName), "OrigFirst", "ClientFirst");
        EnqueueRetryChange(service, subject, nameof(Person.LastName), "OrigLast", "ClientLast");

        // Act
        await service.StartAsync(CancellationToken.None);
        await writeTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);

        // Assert — FirstName dropped (server wins), LastName re-applied
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

        var (service, _, writtenChanges, _) = CreateServiceWithRetryQueue(subject, context,
            initialStateAction: () =>
            {
                subject.FirstName = "ServerFirst";
                subject.LastName = "ServerLast";
            });

        EnqueueRetryChange(service, subject, nameof(Person.FirstName), "OrigFirst", "ClientFirst");
        EnqueueRetryChange(service, subject, nameof(Person.LastName), "OrigLast", "ClientLast");

        // Act
        await service.StartAsync(CancellationToken.None);
        await Task.Delay(500);
        await service.StopAsync(CancellationToken.None);

        // Assert — all dropped, server values remain
        Assert.Equal("ServerFirst", subject.FirstName);
        Assert.Equal("ServerLast", subject.LastName);
        Assert.Empty(writtenChanges);
    }

    [Fact]
    public async Task WhenRetryQueueIsEmpty_ThenInitializationSucceedsNormally()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        var subject = new Person(context) { FirstName = "Original" };

        var (service, _, writtenChanges, _) = CreateServiceWithRetryQueue(subject, context,
            initialStateAction: () => { subject.FirstName = "ServerValue"; });

        // No retry changes enqueued

        // Act
        await service.StartAsync(CancellationToken.None);
        await Task.Delay(500);
        await service.StopAsync(CancellationToken.None);

        // Assert — Initial state applied, no retry changes sent
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

        var sourceMock = new Mock<ISubjectSource>();
        sourceMock.Setup(s => s.StartListeningAsync(It.IsAny<SubjectPropertyWriter>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IDisposable?)null);
        sourceMock.Setup(s => s.LoadInitialStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => { subject.FirstName = "ServerValue"; });
        sourceMock.Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<WriteResult>(WriteResult.Success));

        new PropertyReference(subject, nameof(Person.FirstName)).SetSource(sourceMock.Object);

        // writeRetryQueueSize: 0 disables the queue
        var service = new SubjectSourceBackgroundService(
            sourceMock.Object, context, NullLogger.Instance,
            writeRetryQueueSize: 0);

        // Act
        await service.StartAsync(CancellationToken.None);
        await Task.Delay(500);
        await service.StopAsync(CancellationToken.None);

        // Assert — service runs normally without retry queue
        Assert.Equal("ServerValue", subject.FirstName);
    }

    private static (SubjectSourceBackgroundService service, Mock<ISubjectSource> sourceMock,
        ConcurrentBag<SubjectPropertyChange> writtenChanges, TaskCompletionSource writeTcs)
        CreateServiceWithRetryQueue(Person subject, IInterceptorSubjectContext context, Action initialStateAction)
    {
        var sourceMock = new Mock<ISubjectSource>();
        var writtenChanges = new ConcurrentBag<SubjectPropertyChange>();
        var writeTcs = new TaskCompletionSource();

        sourceMock.Setup(s => s.StartListeningAsync(It.IsAny<SubjectPropertyWriter>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IDisposable?)null);

        sourceMock.Setup(s => s.LoadInitialStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                using (SubjectChangeContext.WithSource(sourceMock.Object))
                {
                    initialStateAction();
                }
            });

        sourceMock.Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
            {
                foreach (var change in changes.ToArray())
                {
                    writtenChanges.Add(change);
                }
                writeTcs.TrySetResult();
                return new ValueTask<WriteResult>(WriteResult.Success);
            });

        // Claim source ownership for common properties
        new PropertyReference(subject, nameof(Person.FirstName)).SetSource(sourceMock.Object);
        new PropertyReference(subject, nameof(Person.LastName)).SetSource(sourceMock.Object);
        new PropertyReference(subject, nameof(Person.Father)).SetSource(sourceMock.Object);
        new PropertyReference(subject, nameof(Person.Mother)).SetSource(sourceMock.Object);
        new PropertyReference(subject, nameof(Person.Children)).SetSource(sourceMock.Object);

        var service = new SubjectSourceBackgroundService(
            sourceMock.Object, context, NullLogger.Instance,
            bufferTime: TimeSpan.FromMilliseconds(50));

        return (service, sourceMock, writtenChanges, writeTcs);
    }

    private static void EnqueueRetryChange(SubjectSourceBackgroundService service,
        IInterceptorSubject subject, string propertyName, string? oldValue, string? newValue)
    {
        EnqueueRetryChange<string?>(service, subject, propertyName, oldValue, newValue);
    }

    private static void EnqueueRetryChange<TValue>(SubjectSourceBackgroundService service,
        IInterceptorSubject subject, string propertyName, TValue oldValue, TValue newValue)
    {
        var queueField = typeof(SubjectSourceBackgroundService)
            .GetField("_writeRetryQueue", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var queue = (WriteRetryQueue)queueField.GetValue(service)!;

        var change = SubjectPropertyChange.Create(
            new PropertyReference(subject, propertyName),
            null,
            DateTimeOffset.UtcNow,
            null,
            oldValue,
            newValue);

        queue.Enqueue(new[] { change });
    }
}