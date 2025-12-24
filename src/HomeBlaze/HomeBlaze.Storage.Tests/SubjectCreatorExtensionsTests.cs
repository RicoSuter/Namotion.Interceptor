using HomeBlaze.Storage.Abstractions;
using Moq;
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Tests;

public class SubjectCreatorExtensionsTests
{
    [Fact]
    public async Task CreateAndAddAsync_WhenResultIsNull_DoesNotCallAddSubject()
    {
        // Arrange
        var subjectCreator = new Mock<ISubjectSetupService>();
        var storage = new Mock<IStorageContainer>();

        subjectCreator.Setup(x => x.CreateSubjectAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((CreateSubjectResult?)null);

        // Act
        await subjectCreator.Object.CreateSubjectAndAddToStorageAsync(storage.Object, CancellationToken.None);

        // Assert
        storage.Verify(
            x => x.AddSubjectAsync(It.IsAny<string>(), It.IsAny<IInterceptorSubject>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CreateAndAddAsync_WhenResultIsReturned_AddsSubjectWithJsonExtension()
    {
        // Arrange
        var subjectCreator = new Mock<ISubjectSetupService>();
        var storage = new Mock<IStorageContainer>();
        var subject = new Mock<IInterceptorSubject>();
        var result = new CreateSubjectResult(subject.Object, "mysubject");

        subjectCreator.Setup(x => x.CreateSubjectAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

        // Act
        await subjectCreator.Object.CreateSubjectAndAddToStorageAsync(storage.Object, CancellationToken.None);

        // Assert
        storage.Verify(
            x => x.AddSubjectAsync("mysubject.json", subject.Object, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CreateAndAddAsync_PassesCancellationToken()
    {
        // Arrange
        var subjectCreator = new Mock<ISubjectSetupService>();
        var storage = new Mock<IStorageContainer>();
        var subject = new Mock<IInterceptorSubject>();
        var result = new CreateSubjectResult(subject.Object, "test");
        using var cts = new CancellationTokenSource();

        subjectCreator.Setup(x => x.CreateSubjectAsync(cts.Token))
            .ReturnsAsync(result);

        // Act
        await subjectCreator.Object.CreateSubjectAndAddToStorageAsync(storage.Object, cts.Token);

        // Assert
        subjectCreator.Verify(x => x.CreateSubjectAsync(cts.Token), Times.Once);
        storage.Verify(x => x.AddSubjectAsync("test.json", subject.Object, cts.Token), Times.Once);
    }
}
