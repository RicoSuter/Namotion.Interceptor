using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Sources.Tests.Models;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Sources.Tests;

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
            .Setup(s => s.StartListeningAsync(It.IsAny<ISubjectMutationDispatcher>(), It.IsAny<CancellationToken>()))
            .Callback((ISubjectMutationDispatcher dispatcher, CancellationToken _) =>
            {
                dispatcher.EnqueueSubjectUpdate(() => updates.Add("Update1"));
                dispatcher.EnqueueSubjectUpdate(() => updates.Add("Update2"));
            })
            .ReturnsAsync((IDisposable?)null);

        subjectSourceMock
            .Setup(s => s.LoadCompleteSourceStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => { updates.Add("Complete"); });

        subjectSourceMock
            .Setup(s => s.WriteToSourceAsync(It.IsAny<IEnumerable<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

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
        var propertyChangedObservable = new PropertyChangedObservable();

        var context = new InterceptorSubjectContext();
        context.WithRegistry();
        context.AddService(propertyChangedObservable);

        var subject = new Person(context);
        var subjectSourceMock = new Mock<ISubjectSource>();

        subjectSourceMock
            .Setup(s => s.IsPropertyIncluded(It.IsAny<RegisteredSubjectProperty>()))
            .Returns(true);

        subjectSourceMock
            .Setup(s => s.StartListeningAsync(It.IsAny<ISubjectMutationDispatcher>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IDisposable?)null);

        subjectSourceMock
            .Setup(s => s.LoadCompleteSourceStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((Action?)null);

        IEnumerable<SubjectPropertyChange>? changes = null;

        subjectSourceMock
            .Setup(s => s.WriteToSourceAsync(It.IsAny<IEnumerable<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Callback((IEnumerable<SubjectPropertyChange> c, CancellationToken _) => changes = c)
            .Returns(Task.CompletedTask);

        var cancellationTokenSource = new CancellationTokenSource();

        // Act
        var service = new SubjectSourceBackgroundService(subjectSourceMock.Object, context, NullLogger.Instance);

        await service.StartAsync(cancellationTokenSource.Token);
        propertyChangedObservable.WriteProperty(
            new WritePropertyInterception(
                subject.GetPropertyReference(nameof(Person.FirstName)), null, null),
                _ => "Bar");
        
        await Task.Delay(1000, cancellationTokenSource.Token);
        await service.StopAsync(cancellationTokenSource.Token);

        await cancellationTokenSource.CancelAsync();

        // Assert
        Assert.NotNull(changes);
        Assert.Equal("Bar", changes.First().NewValue);
    }
}