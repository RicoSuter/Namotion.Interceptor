using HomeBlaze.E2E.Tests.Infrastructure;
using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for page editing functionality (split-view editor).
/// </summary>
[Collection(nameof(PlaywrightCollection))]
public class PageEditTests
{
    private readonly PlaywrightFixture _fixture;

    public PageEditTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task EditButton_ShouldBeVisibleOnMarkdownPage()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();

        // Act - Navigate to Dashboard (a markdown page)
        await page.GotoAsync($"{_fixture.ServerAddress}pages/Children/Dashboard.md");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Assert - Edit FAB should be visible
        var editButton = page.Locator("[data-testid='edit-fab']");
        await Assertions.Expect(editButton).ToBeVisibleAsync();
    }

    [Fact]
    public async Task EditButton_ShouldOpenSplitView()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync($"{_fixture.ServerAddress}pages/Children/Dashboard.md");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Act - Click edit button
        var editFab = page.Locator("[data-testid='edit-fab']");
        await editFab.ClickAsync();

        // Wait for Blazor to re-render
        await page.WaitForTimeoutAsync(2000);

        // Assert - Split view should be visible
        var splitter = page.Locator("[data-testid='edit-splitter']");
        await Assertions.Expect(splitter).ToBeVisibleAsync(new() { Timeout = 10000 });
    }

    [Fact]
    public async Task SplitView_ShouldHaveSaveButton()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync($"{_fixture.ServerAddress}pages/Children/Dashboard.md");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Act - Open edit mode
        var editFab = page.Locator("[data-testid='edit-fab']");
        await editFab.ClickAsync();

        // Wait for Blazor to re-render
        await page.WaitForTimeoutAsync(1000);

        // Wait for split view to appear
        var splitter = page.Locator("[data-testid='edit-splitter']");
        await Assertions.Expect(splitter).ToBeVisibleAsync(new() { Timeout = 10000 });

        // Assert - Save button should be visible
        var saveButton = page.Locator("[data-testid='save-button']");
        await Assertions.Expect(saveButton).ToBeVisibleAsync(new() { Timeout = 10000 });
    }

    [Fact]
    public async Task EditButton_ShouldToggleEditMode()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync($"{_fixture.ServerAddress}pages/Children/Dashboard.md");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Act - Open edit mode
        var editFab = page.Locator("[data-testid='edit-fab']");
        await editFab.ClickAsync();

        // Wait for Blazor to re-render
        await page.WaitForTimeoutAsync(1000);

        // Verify split view is open
        var splitter = page.Locator("[data-testid='edit-splitter']");
        await Assertions.Expect(splitter).ToBeVisibleAsync(new() { Timeout = 10000 });

        // Click FAB again to close edit mode
        await editFab.ClickAsync();

        // Wait for Blazor to re-render
        await page.WaitForTimeoutAsync(1000);

        // Assert - Split view should no longer be visible
        await Assertions.Expect(splitter).Not.ToBeVisibleAsync(new() { Timeout = 10000 });
    }
}
