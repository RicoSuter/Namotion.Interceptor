using HomeBlaze.E2E.Tests.Infrastructure;
using HomeBlaze.Services;
using HomeBlaze.Storage;
using HomeBlaze.Storage.Files;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for what saving an edited page writes and applies.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class PageSaveTests
{
    private const string PagePath = "Demo/Editable.md";

    // Far enough in the past that any write of the file moves it, whatever the resolution of the file system.
    private static readonly DateTime UntouchedTime = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly PlaywrightFixture _fixture;

    public PageSaveTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task WhenMarkdownPageIsSaved_ThenOnlyThePageIsWritten()
    {
        // Arrange
        var rootManager = _fixture.ServerServices.GetRequiredService<RootManager>();
        var pageFilePath = GetPageFilePath(rootManager);
        var originalContent = await File.ReadAllTextAsync(pageFilePath);
        var originalConfigurationTime = File.GetLastWriteTimeUtc(rootManager.ConfigurationPath);
        var pageSubject = GetPageSubject(rootManager);
        var marker = $"Saved {Guid.NewGuid():N}";

        try
        {
            File.SetLastWriteTimeUtc(rootManager.ConfigurationPath, UntouchedTime);

            var page = await _fixture.CreatePageAsync();
            await page.GotoAsync($"{_fixture.ServerAddress}pages/{PagePath}");
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

            var editMenu = page.Locator("[data-testid='edit-mode-menu']");
            await Assertions.Expect(editMenu).ToBeVisibleAsync(new() { Timeout = 30000 });
            await editMenu.ClickAsync();
            await page.GetByText("Source").ClickAsync();

            var editorContent = page.Locator(".monaco-editor .view-lines");
            await Assertions.Expect(editorContent).ToBeVisibleAsync(new() { Timeout = 30000 });
            await editorContent.ClickAsync();
            await page.Keyboard.PressAsync("ControlOrMeta+End");
            await page.Keyboard.TypeAsync(marker);
            await Assertions.Expect(editorContent).ToContainTextAsync(marker, new() { Timeout = 30000 });

            // Act
            await page.Locator("[data-testid='save-button'] button").ClickAsync();

            // Assert: the notification is the last step of the save, so everything it writes is written by now.
            await Assertions.Expect(page.GetByText("Configuration saved.")).ToBeVisibleAsync(new() { Timeout = 30000 });

            var savedContent = await File.ReadAllTextAsync(pageFilePath);
            Assert.Multiple(
                () => Assert.Contains(marker, savedContent),
                () => Assert.Equal(UntouchedTime, File.GetLastWriteTimeUtc(rootManager.ConfigurationPath)),
                () => Assert.Same(pageSubject, GetPageSubject(rootManager)));
        }
        finally
        {
            await RestoreAsync(rootManager, originalContent, originalConfigurationTime);
        }
    }

    [Fact]
    public async Task WhenEmbeddedSubjectIsSaved_ThenItIsWrittenThroughItsPage()
    {
        // Arrange
        var rootManager = _fixture.ServerServices.GetRequiredService<RootManager>();
        var pageFilePath = GetPageFilePath(rootManager);
        var originalContent = await File.ReadAllTextAsync(pageFilePath);
        var originalConfigurationTime = File.GetLastWriteTimeUtc(rootManager.ConfigurationPath);
        var pageSubject = GetPageSubject(rootManager);
        var motorName = $"Motor{Guid.NewGuid():N}";

        try
        {
            File.SetLastWriteTimeUtc(rootManager.ConfigurationPath, UntouchedTime);

            var page = await _fixture.CreatePageAsync();
            await page.GotoAsync($"{_fixture.ServerAddress}pages/{PagePath}");
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

            var editMenu = page.Locator("[data-testid='edit-mode-menu']");
            await Assertions.Expect(editMenu).ToBeVisibleAsync(new() { Timeout = 30000 });
            await editMenu.ClickAsync();
            await page.GetByText("Inline").ClickAsync();

            var editButton = page.Locator("[data-testid='edit-subject-button']");
            await Assertions.Expect(editButton).ToBeVisibleAsync(new() { Timeout = 30000 });
            await editButton.ClickAsync();

            var dialog = page.Locator(".mud-dialog");
            var nameField = dialog.GetByLabel("Name");
            await Assertions.Expect(nameField).ToBeVisibleAsync(new() { Timeout = 30000 });
            await nameField.FillAsync(motorName);

            // Act
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();

            // Assert: the notification is the last step of the save, so everything it writes is written by now.
            await Assertions.Expect(page.GetByText("Configuration saved.")).ToBeVisibleAsync(new() { Timeout = 30000 });

            var savedContent = await File.ReadAllTextAsync(pageFilePath);
            Assert.Multiple(
                () => Assert.Contains($"\"name\": \"{motorName}\"", savedContent),
                () => Assert.Equal(UntouchedTime, File.GetLastWriteTimeUtc(rootManager.ConfigurationPath)),
                () => Assert.Same(pageSubject, GetPageSubject(rootManager)));
        }
        finally
        {
            await RestoreAsync(rootManager, originalContent, originalConfigurationTime);
        }
    }

    private static string GetPageFilePath(RootManager rootManager)
        => Path.Combine(rootManager.DataDirectory, "TestData", PagePath);

    private static MarkdownFile GetPageSubject(RootManager rootManager)
    {
        var root = (FluentStorageContainer)rootManager.Root!;
        var folder = (VirtualFolder)root.Children["Demo"];
        return (MarkdownFile)folder.Children["Editable.md"];
    }

    private static async Task RestoreAsync(RootManager rootManager, string originalContent, DateTime originalConfigurationTime)
    {
        // Written through the subject, so that the page the server holds is restored along with the file.
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(originalContent));
        await GetPageSubject(rootManager).WriteAsync(stream, CancellationToken.None);

        File.SetLastWriteTimeUtc(rootManager.ConfigurationPath, originalConfigurationTime);
    }
}
