using System.Text.RegularExpressions;
using HomeBlaze.E2E.Tests.Infrastructure;
using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for page editing functionality (split-view editor).
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class PageEditTests
{
    private readonly PlaywrightFixture _fixture;

    public PageEditTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task EditModeMenu_ShouldBeVisibleOnMarkdownPage()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();

        // Act - Navigate to Dashboard (a markdown page)
        await page.GotoAsync($"{_fixture.ServerAddress}pages/Dashboard.md");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        // Assert - Edit mode menu should be visible
        var editMenu = page.Locator("[data-testid='edit-mode-menu']");
        await Assertions.Expect(editMenu).ToBeVisibleAsync(new() { Timeout = 30000 });
    }

    [Fact]
    public async Task SplitMode_ShouldOpenSplitView()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync($"{_fixture.ServerAddress}pages/Dashboard.md");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        // Act - Open Split mode via dropdown menu
        var editMenu = page.Locator("[data-testid='edit-mode-menu']");
        await Assertions.Expect(editMenu).ToBeVisibleAsync(new() { Timeout = 30000 });
        await editMenu.ClickAsync();
        await page.GetByText("Split").ClickAsync();

        // Assert - Split view should be visible
        var splitter = page.Locator("[data-testid='edit-splitter']");
        await Assertions.Expect(splitter).ToBeVisibleAsync(new() { Timeout = 30000 });
    }

    [Fact]
    public async Task SplitView_ShouldHaveSaveButton()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync($"{_fixture.ServerAddress}pages/Dashboard.md");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        // Act - Open Split mode via dropdown menu
        var editMenu = page.Locator("[data-testid='edit-mode-menu']");
        await Assertions.Expect(editMenu).ToBeVisibleAsync(new() { Timeout = 30000 });
        await editMenu.ClickAsync();
        await page.GetByText("Split").ClickAsync();

        // Wait for split view to appear
        var splitter = page.Locator("[data-testid='edit-splitter']");
        await Assertions.Expect(splitter).ToBeVisibleAsync(new() { Timeout = 30000 });

        // Assert - Save button should be visible
        var saveButton = page.Locator("[data-testid='save-button']");
        await Assertions.Expect(saveButton).ToBeVisibleAsync(new() { Timeout = 30000 });
    }

    [Fact]
    public async Task ViewMode_ShouldCloseEditMode()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync($"{_fixture.ServerAddress}pages/Dashboard.md");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        // Act - Open Split mode
        var editMenu = page.Locator("[data-testid='edit-mode-menu']");
        await Assertions.Expect(editMenu).ToBeVisibleAsync(new() { Timeout = 30000 });
        await editMenu.ClickAsync();
        await page.GetByText("Split").ClickAsync();

        // Verify split view is open
        var splitter = page.Locator("[data-testid='edit-splitter']");
        await Assertions.Expect(splitter).ToBeVisibleAsync(new() { Timeout = 30000 });

        // Switch to View mode
        await editMenu.ClickAsync();
        await page.GetByText("View").ClickAsync();

        // Assert - Split view should no longer be visible
        await Assertions.Expect(splitter).Not.ToBeVisibleAsync(new() { Timeout = 30000 });
    }

    [Fact]
    public async Task SourceMode_ShouldShowEditorOnly()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync($"{_fixture.ServerAddress}pages/Dashboard.md");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        // Act - Open Source mode via dropdown menu
        var editMenu = page.Locator("[data-testid='edit-mode-menu']");
        await Assertions.Expect(editMenu).ToBeVisibleAsync(new() { Timeout = 30000 });
        await editMenu.ClickAsync();
        await page.GetByText("Source").ClickAsync();

        // Assert - Monaco editor should be visible (not in split view)
        var monacoEditor = page.Locator(".monaco-editor");
        await Assertions.Expect(monacoEditor).ToBeVisibleAsync(new() { Timeout = 30000 });

        // Split view should NOT be visible
        var splitter = page.Locator("[data-testid='edit-splitter']");
        await Assertions.Expect(splitter).Not.ToBeVisibleAsync(new() { Timeout = 5000 });
    }

    [Fact]
    public async Task WhenTheFormOfThePageEditorIsInvalid_ThenSaveIsDisabledUntilItIsCorrected()
    {
        // Arrange - the subject has no page component, but the page editor shows the generated properties
        // editor for it all the same.
        var (name, subject) = _fixture.AddPlainSubjectToRoot();
        try
        {
            var page = await _fixture.CreatePageAsync();
            await page.GotoAsync($"{_fixture.ServerAddress}pages/{name}");
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

            // The edit menu is prerendered, and a click before the circuit is connected is lost.
            await Assertions.Expect(page.GetByTitle(new Regex(@"^Automatic \(")))
                .ToBeVisibleAsync(new() { Timeout = 30000 });

            var editMenu = page.Locator("[data-testid='edit-mode-menu']");
            await Assertions.Expect(editMenu).ToBeVisibleAsync(new() { Timeout = 30000 });
            await editMenu.ClickAsync();
            await page.GetByText("Split").ClickAsync();

            var saveButton = page.Locator("[data-testid='save-button'] button");
            var limitInput = page.GetByLabel("Limit");
            await Assertions.Expect(page.GetByLabel("Label", new() { Exact = true }))
                .ToHaveValueAsync("Original label", new() { Timeout = 30000 });

            // Assert - the subject is valid, so nothing has to be touched before it can be saved.
            await Assertions.Expect(saveButton).ToBeEnabledAsync(new() { Timeout = 5000 });

            // Act - Enter commits the text without leaving the field.
            await limitInput.FillAsync("1.5");
            await limitInput.PressAsync("Enter");

            // Assert
            await Assertions.Expect(saveButton).ToBeDisabledAsync(new() { Timeout = 5000 });
            Assert.Null(subject.Limit);

            // Act
            await limitInput.FillAsync("3");
            await limitInput.PressAsync("Enter");

            // Assert
            await Assertions.Expect(saveButton).ToBeEnabledAsync(new() { Timeout = 5000 });

            // Act
            await saveButton.ClickAsync();

            // Assert - the editor stays open after a save, and what it wrote itself is no external change.
            await Assertions.Expect(page.GetByText("Configuration saved.")).ToBeVisibleAsync(new() { Timeout = 5000 });
            Assert.Equal(3, subject.Limit);
            await Assertions.Expect(page.GetByText("Configuration was changed externally")).ToHaveCountAsync(0);
        }
        finally
        {
            _fixture.RemovePlainSubjectFromRoot(name, subject);
        }
    }
}
