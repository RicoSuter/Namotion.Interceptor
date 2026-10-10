using HomeBlaze.Storage.Abstractions;
using Moq;
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Tests;

public class SubjectCreatorExtensionsTests
{
    [Fact]
    public async Task WhenTheDialogIsCancelled_ThenNoSubjectIsAdded()
    {
        // Arrange
        var subjectSetupService = new Mock<ISubjectSetupService>();
        var storage = new Mock<IStorageContainer>();

        subjectSetupService
            .Setup(service => service.CreateSubjectAsync(It.IsAny<SubjectSetupRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CreateSubjectResult?)null);

        // Act
        await subjectSetupService.Object.CreateSubjectAndAddToStorageAsync(storage.Object, [], CancellationToken.None);

        // Assert
        storage.Verify(
            container => container.AddSubjectAsync(It.IsAny<string>(), It.IsAny<IInterceptorSubject>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task WhenASubjectIsCreated_ThenItIsAddedWithTheJsonExtension()
    {
        // Arrange
        var subjectSetupService = new Mock<ISubjectSetupService>();
        var storage = new Mock<IStorageContainer>();
        var subject = new Mock<IInterceptorSubject>();

        subjectSetupService
            .Setup(service => service.CreateSubjectAsync(It.IsAny<SubjectSetupRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreateSubjectResult(subject.Object, "MySubject"));

        // Act
        await subjectSetupService.Object.CreateSubjectAndAddToStorageAsync(storage.Object, [], CancellationToken.None);

        // Assert
        storage.Verify(
            container => container.AddSubjectAsync("MySubject.json", subject.Object, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task WhenTheResultHasNoName_ThenNoSubjectIsAdded()
    {
        // Arrange
        var subjectSetupService = new Mock<ISubjectSetupService>();
        var storage = new Mock<IStorageContainer>();
        var subject = new Mock<IInterceptorSubject>();

        subjectSetupService
            .Setup(service => service.CreateSubjectAsync(It.IsAny<SubjectSetupRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreateSubjectResult(subject.Object, null));

        // Act
        await subjectSetupService.Object.CreateSubjectAndAddToStorageAsync(storage.Object, [], CancellationToken.None);

        // Assert
        storage.Verify(
            container => container.AddSubjectAsync(It.IsAny<string>(), It.IsAny<IInterceptorSubject>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task WhenCreating_ThenExistingNamesAndCancellationTokenArePassedOn()
    {
        // Arrange
        var subjectSetupService = new Mock<ISubjectSetupService>();
        var storage = new Mock<IStorageContainer>();
        var subject = new Mock<IInterceptorSubject>();
        string[] existingNames = ["History", "Dashboard.md"];
        using var cancellationTokenSource = new CancellationTokenSource();

        subjectSetupService
            .Setup(service => service.CreateSubjectAsync(It.IsAny<SubjectSetupRequest>(), cancellationTokenSource.Token))
            .ReturnsAsync(new CreateSubjectResult(subject.Object, "Test"));

        // Act
        await subjectSetupService.Object.CreateSubjectAndAddToStorageAsync(
            storage.Object, existingNames, cancellationTokenSource.Token);

        // Assert
        subjectSetupService.Verify(
            service => service.CreateSubjectAsync(
                It.Is<SubjectSetupRequest>(request => request.ExistingNames.SequenceEqual(existingNames)),
                cancellationTokenSource.Token),
            Times.Once);
        storage.Verify(
            container => container.AddSubjectAsync("Test.json", subject.Object, cancellationTokenSource.Token),
            Times.Once);
    }
}
