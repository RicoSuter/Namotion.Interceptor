using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests;

public class SubjectConnectorBackgroundServiceTests
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

        var subjectConnectorMock = new Mock<ISubjectUpstreamConnector>();

        var updates = new List<string>();
        subjectConnectorMock
            .Setup(s => s.StartListeningAsync(It.IsAny<ConnectorUpdateBuffer>(), It.IsAny<CancellationToken>()))
            .Callback((ConnectorUpdateBuffer updateBuffer, CancellationToken _) =>
            {
                updateBuffer.ApplyUpdate(updates, u => u.Add("Update1"));
                updateBuffer.ApplyUpdate(updates, u => u.Add("Update2"));
            })
            .ReturnsAsync((IDisposable?)null);

        subjectConnectorMock
            .Setup(s => s.LoadCompleteSourceStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => { updates.Add("Complete"); });

        subjectConnectorMock
            .Setup(s => s.WriteToSourceAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        var cancellationTokenSource = new CancellationTokenSource();

        // Act
        var service = new SubjectUpstreamConnectorBackgroundService(subjectConnectorMock.Object, subjectContextMock.Object, NullLogger.Instance);

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
        var subjectConnectorMock = new Mock<ISubjectUpstreamConnector>();

        subjectConnectorMock
            .Setup(s => s.IsPropertyIncluded(It.IsAny<RegisteredSubjectProperty>()))
            .Returns(true);

        subjectConnectorMock
            .Setup(s => s.StartListeningAsync(It.IsAny<ConnectorUpdateBuffer>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IDisposable?)null);

        subjectConnectorMock
            .Setup(s => s.LoadCompleteSourceStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((Action?)null);

        SubjectPropertyChange[]? changes = null;

        subjectConnectorMock
            .Setup(s => s.WriteToSourceAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Callback((ReadOnlyMemory<SubjectPropertyChange> c, CancellationToken _) => changes = c.ToArray())
            .Returns(ValueTask.CompletedTask);

        var cancellationTokenSource = new CancellationTokenSource();

        // Act
        var service = new SubjectUpstreamConnectorBackgroundService(subjectConnectorMock.Object, context, NullLogger.Instance);
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
}