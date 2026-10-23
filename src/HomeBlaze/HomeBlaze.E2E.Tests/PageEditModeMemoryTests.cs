using HomeBlaze.E2E.Tests.Infrastructure;
using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for the edit mode that a browser keeps over the pages it opens.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class PageEditModeMemoryTests
{
    private const float Timeout = 30000;

    private readonly PlaywrightFixture _fixture;

    public PageEditModeMemoryTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task WhenSplitIsChosenAndTheAppNavigatesToAnotherPage_ThenThatPageOpensInSplit()
    {
        // Arrange
        var page = await OpenAsync("pages/Dashboard.md");
        await page.SelectEditModeAsync("Split");
        await Assertions.Expect(page.GetByTestId("edit-splitter")).ToBeVisibleAsync(new() { Timeout = Timeout });

        // Act
        await page.EvaluateAsync("url => Blazor.navigateTo(url)", "/pages/Demo/Cards.md");

        // Assert
        // The title comes with the render that sets the mode of the new page, so the old page's editor is gone by then.
        await Assertions.Expect(page).ToHaveTitleAsync("Cards Demo - HomeBlaze", new() { Timeout = Timeout });
        await Assertions.Expect(page.GetByTestId("edit-splitter")).ToBeVisibleAsync(new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenSourceIsChosenAndTheAppLeavesThePagesAndComesBack_ThenThePageOpensInSource()
    {
        // Arrange
        var page = await OpenAsync("pages/Dashboard.md");
        await page.SelectEditModeAsync("Source");
        await Assertions.Expect(page.Locator(".monaco-editor")).ToBeVisibleAsync(new() { Timeout = Timeout });
        await page.EvaluateAsync("url => Blazor.navigateTo(url)", "/browser");
        await page.WaitForURLAsync("**/browser", new() { Timeout = Timeout });
        await Assertions.Expect(page.Locator(".monaco-editor")).ToHaveCountAsync(0, new() { Timeout = Timeout });

        // Act
        await page.EvaluateAsync("url => Blazor.navigateTo(url)", "/pages/Demo/Cards.md");

        // Assert
        // The title comes with the render that sets the mode of the new page, so the old page's editor is gone by then.
        await Assertions.Expect(page).ToHaveTitleAsync("Cards Demo - HomeBlaze", new() { Timeout = Timeout });
        await Assertions.Expect(page.Locator(".monaco-editor")).ToBeVisibleAsync(new() { Timeout = Timeout });
        await Assertions.Expect(page.GetByTestId("edit-splitter")).ToHaveCountAsync(0, new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenSplitIsChosenAndThePageIsReloaded_ThenItOpensInView()
    {
        // Arrange: the choice belongs to the circuit, which a reload ends.
        var page = await OpenAsync("pages/Dashboard.md");
        await page.SelectEditModeAsync("Split");
        await Assertions.Expect(page.GetByTestId("edit-splitter")).ToBeVisibleAsync(new() { Timeout = Timeout });

        // Act
        await page.ReloadAsync();
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        await page.WaitUntilInteractiveAsync();

        // Assert
        await Assertions.Expect(page.GetByTestId("edit-mode-menu")).ToBeVisibleAsync(new() { Timeout = Timeout });
        await Assertions.Expect(page.GetByTestId("edit-splitter")).ToHaveCountAsync(0, new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenViewIsChosenAgain_ThenTheNextPageOpensInView()
    {
        // Arrange
        var page = await OpenAsync("pages/Dashboard.md");
        await page.SelectEditModeAsync("Split");
        await Assertions.Expect(page.GetByTestId("edit-splitter")).ToBeVisibleAsync(new() { Timeout = Timeout });
        await page.SelectEditModeAsync("View");
        await Assertions.Expect(page.GetByTestId("edit-splitter")).ToHaveCountAsync(0, new() { Timeout = Timeout });

        // Act
        await page.EvaluateAsync("url => Blazor.navigateTo(url)", "/pages/Demo/Cards.md");

        // Assert
        // The title comes with the render that sets the mode of the new page, so the old page's editor is gone by then.
        await Assertions.Expect(page).ToHaveTitleAsync("Cards Demo - HomeBlaze", new() { Timeout = Timeout });
        await Assertions.Expect(page.GetByTestId("edit-mode-menu")).ToBeVisibleAsync(new() { Timeout = Timeout });
        await Assertions.Expect(page.GetByTestId("edit-splitter")).ToHaveCountAsync(0, new() { Timeout = Timeout });
        await Assertions.Expect(page.Locator(".monaco-editor")).ToHaveCountAsync(0, new() { Timeout = Timeout });
    }

    private async Task<IPage> OpenAsync(string path)
    {
        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync($"{_fixture.ServerAddress}{path}");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        await page.WaitUntilInteractiveAsync();
        return page;
    }
}
