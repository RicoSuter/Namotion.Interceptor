using HomeBlaze.E2E.Tests.Infrastructure;
using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for the Delete button of a subject's pane in the subject browser.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class SubjectPaneDeleteTests
{
    private const int PageLoadTimeout = 30000;

    private readonly PlaywrightFixture _fixture;

    public SubjectPaneDeleteTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// Opens the subject browser at a subject, given by its path, and returns the pane of that subject.
    /// </summary>
    private async Task<(IPage Page, ILocator Pane)> OpenPaneAsync(string subjectPath)
    {
        var page = await _fixture.CreatePageAsync();
        await page.SetViewportSizeAsync(1280, 720);
        await page.GotoAsync($"{_fixture.ServerAddress}browser/{subjectPath}");

        // The page is prerendered, and a click before its circuit is interactive is lost.
        await page.WaitUntilInteractiveAsync(PageLoadTimeout);
        return (page, page.Locator("#scrollContainer [data-testid='subject-pane']").Last);
    }

    [Fact]
    public async Task WhenTheRootStorageIsShown_ThenItHasNoDeleteButton()
    {
        // Act
        var (_, pane) = await OpenPaneAsync(string.Empty);

        // Assert
        await Assertions.Expect(pane.GetByRole(AriaRole.Heading)).ToBeVisibleAsync(new() { Timeout = PageLoadTimeout });
        await Assertions.Expect(pane.GetByTestId("reorder-children-button")).ToBeVisibleAsync(new() { Timeout = PageLoadTimeout });
        await Assertions.Expect(pane.GetByTestId("delete-subject-button")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task WhenASubjectInsideAFileIsShown_ThenItHasNoDeleteButton()
    {
        // Act
        var (_, pane) = await OpenPaneAsync("Demo/Layout/Cells/0");

        // Assert
        await Assertions.Expect(pane.GetByRole(AriaRole.Heading)).ToBeVisibleAsync(new() { Timeout = PageLoadTimeout });
        await Assertions.Expect(pane.GetByTestId("edit-metadata-button")).ToBeVisibleAsync(new() { Timeout = PageLoadTimeout });
        await Assertions.Expect(pane.GetByTestId("delete-subject-button")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task WhenAFolderWithChildrenIsShown_ThenItsDeleteButtonIsDisabled()
    {
        // Act
        var (_, pane) = await OpenPaneAsync("Demo");

        // Assert
        await Assertions.Expect(pane.GetByTestId("delete-subject-button")).ToBeDisabledAsync(new() { Timeout = PageLoadTimeout });
    }

    [Fact]
    public async Task WhenAStoredSubjectIsShown_ThenItHasADeleteButtonAndNoDeleteOperationButton()
    {
        // Arrange
        await using var motor = await _fixture.AddStoredMotorAsync();

        // Act
        var (_, pane) = await OpenPaneAsync(motor.Name);

        // Assert
        await Assertions.Expect(pane.GetByTestId("delete-subject-button")).ToBeEnabledAsync(new() { Timeout = PageLoadTimeout });
        var content = pane.GetByTestId("subject-pane-content");
        await Assertions.Expect(content.GetByRole(AriaRole.Button, new() { Name = "Restart Controller", Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(content.GetByRole(AriaRole.Button, new() { Name = "Delete", Exact = true })).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task WhenTheDeleteOfAStoredSubjectIsConfirmed_ThenItsFileIsGoneAndItsPaneCloses()
    {
        // Arrange
        await using var motor = await _fixture.AddStoredMotorAsync();
        var (page, pane) = await OpenPaneAsync(motor.Name);
        var deleteButton = pane.GetByTestId("delete-subject-button");
        await Assertions.Expect(deleteButton).ToBeEnabledAsync(new() { Timeout = PageLoadTimeout });

        // Act
        await deleteButton.ClickAsync();
        var dialog = page.GetByRole(AriaRole.Dialog);
        await Assertions.Expect(dialog).ToContainTextAsync("Delete '", new() { Timeout = PageLoadTimeout });
        await Assertions.Expect(dialog).ToContainTextAsync("Deletes the file of this subject from the storage.");
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Delete", Exact = true }).ClickAsync();

        // Assert
        await Assertions.Expect(page.Locator("#scrollContainer [data-testid='subject-pane']")).ToHaveCountAsync(1, new() { Timeout = PageLoadTimeout });
        Assert.False(File.Exists(motor.FilePath));
    }

    [Fact]
    public async Task WhenAnEmptyFolderIsDeleted_ThenItsDirectoryIsGoneAndItsPaneCloses()
    {
        // Arrange
        var name = $"Empty{Guid.NewGuid():N}";
        var directoryPath = _fixture.GetPagePath(name);
        Directory.CreateDirectory(directoryPath);
        try
        {
            await _fixture.ReconcileStorageAsync();
            var (page, pane) = await OpenPaneAsync(name);
            var deleteButton = pane.GetByTestId("delete-subject-button");
            await Assertions.Expect(deleteButton).ToBeEnabledAsync(new() { Timeout = PageLoadTimeout });

            // Act
            await deleteButton.ClickAsync();
            var dialog = page.GetByRole(AriaRole.Dialog);
            await Assertions.Expect(dialog).ToContainTextAsync("Delete '", new() { Timeout = PageLoadTimeout });
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Delete", Exact = true }).ClickAsync();

            // Assert
            await Assertions.Expect(page.Locator("#scrollContainer [data-testid='subject-pane']")).ToHaveCountAsync(1, new() { Timeout = PageLoadTimeout });
            Assert.False(Directory.Exists(directoryPath));
        }
        finally
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
                await _fixture.ReconcileStorageAsync();
            }
        }
    }
}
