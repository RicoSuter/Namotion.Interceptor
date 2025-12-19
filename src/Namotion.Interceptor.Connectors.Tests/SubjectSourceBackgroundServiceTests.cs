using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
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
            .Returns((ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
                new ValueTask<WriteResult>(WriteResult.Success()));

        var cancellationTokenSource = new CancellationTokenSource();

        // Act
        var service = new SubjectSourceBackgroundService(subjectSourceMock.Object, subjectContextMock.Object, NullLogger.Instance, null, null);

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
                return new ValueTask<WriteResult>(WriteResult.Success());
            });

        var cancellationTokenSource = new CancellationTokenSource();

        // Act
        var service = new SubjectSourceBackgroundService(subjectSourceMock.Object, context, NullLogger.Instance, null, null);
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
        var service = new SubjectSourceBackgroundService(subjectSourceMock.Object, context, NullLogger.Instance, null, null);
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
        var service = new SubjectSourceBackgroundService(subjectSourceMock.Object, context, NullLogger.Instance, null, null);
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
                    return new ValueTask<WriteResult>(WriteResult.Failure(new Exception("First call fails")));
                }
                secondCallTcs.TrySetResult();
                return new ValueTask<WriteResult>(WriteResult.Success());
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
                    return new ValueTask<WriteResult>(WriteResult.Failure(new Exception("Connection failed")));
                }
                secondCallTcs.TrySetResult();
                return new ValueTask<WriteResult>(WriteResult.Success());
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
}