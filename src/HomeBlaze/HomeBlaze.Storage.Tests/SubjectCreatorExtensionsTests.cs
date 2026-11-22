using HomeBlaze.Storage.Abstractions;
using Moq;
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Tests;

public class SubjectCreatorExtensionsTests
{
    /// <summary>
    /// A setup service whose prompt creates the results in order, each passed to the callback while it is open.
    /// </summary>
    private static Mock<ISubjectSetupService> CreateSetupService(params CreateSubjectResult[] results)
    {
        var subjectSetupService = new Mock<ISubjectSetupService>();
        subjectSetupService
            .Setup(service => service.CreateSubjectsAsync(
                It.IsAny<IStorageContainer>(),
                It.IsAny<Func<CreateSubjectResult, CancellationToken, Task>>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (IStorageContainer _, Func<CreateSubjectResult, CancellationToken, Task> addSubjectAsync, CancellationToken cancellationToken) =>
            {
                foreach (var result in results)
                {
                    await addSubjectAsync(result, cancellationToken);
                }
            });

        return subjectSetupService;
    }

    [Fact]
    public async Task WhenTheDialogIsCanceled_ThenNoSubjectIsAdded()
    {
        // Arrange
        var subjectSetupService = CreateSetupService();
        var storage = new Mock<IStorageContainer>();

        // Act
        await subjectSetupService.Object.CreateSubjectsAndAddToStorageAsync(storage.Object, CancellationToken.None);

        // Assert
        storage.Verify(
            container => container.AddSubjectAsync(It.IsAny<string>(), It.IsAny<IInterceptorSubject>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task WhenSeveralSubjectsAreCreatedInOnePrompt_ThenEachIsAdded()
    {
        // Arrange
        var first = new Mock<IInterceptorSubject>();
        var second = new Mock<IInterceptorSubject>();
        var subjectSetupService = CreateSetupService(
            new CreateSubjectResult(first.Object, "First"),
            new CreateSubjectResult(second.Object, "Second"));
        var storage = new Mock<IStorageContainer>();

        // Act
        await subjectSetupService.Object.CreateSubjectsAndAddToStorageAsync(storage.Object, CancellationToken.None);

        // Assert
        storage.Verify(
            container => container.AddSubjectAsync("First.json", first.Object, It.IsAny<CancellationToken>()),
            Times.Once);
        storage.Verify(
            container => container.AddSubjectAsync("Second.json", second.Object, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task WhenTheResultHasNoName_ThenNoSubjectIsAdded()
    {
        // Arrange
        var subject = new Mock<IInterceptorSubject>();
        var subjectSetupService = CreateSetupService(new CreateSubjectResult(subject.Object, null));
        var storage = new Mock<IStorageContainer>();

        // Act
        await subjectSetupService.Object.CreateSubjectsAndAddToStorageAsync(storage.Object, CancellationToken.None);

        // Assert
        storage.Verify(
            container => container.AddSubjectAsync(It.IsAny<string>(), It.IsAny<IInterceptorSubject>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task WhenCreating_ThenTargetContainerAndCancellationTokenArePassedOn()
    {
        // Arrange
        var subject = new Mock<IInterceptorSubject>();
        var subjectSetupService = CreateSetupService(new CreateSubjectResult(subject.Object, "Test"));
        var storage = new Mock<IStorageContainer>();
        using var cancellationTokenSource = new CancellationTokenSource();

        // Act
        await subjectSetupService.Object.CreateSubjectsAndAddToStorageAsync(storage.Object, cancellationTokenSource.Token);

        // Assert
        subjectSetupService.Verify(
            service => service.CreateSubjectsAsync(
                storage.Object,
                It.IsAny<Func<CreateSubjectResult, CancellationToken, Task>>(),
                cancellationTokenSource.Token),
            Times.Once);
        storage.Verify(
            container => container.AddSubjectAsync("Test.json", subject.Object, cancellationTokenSource.Token),
            Times.Once);
    }
}
