using System.Text.RegularExpressions;
using HomeBlaze.E2E.Tests.Infrastructure;
using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for the page menu in the head of a page: the button alone stands on the page, and its menu tells about the
/// page above the edit modes.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class PageMenuTests
{
    private const int Timeout = 30000;

    private readonly PlaywrightFixture _fixture;

    public PageMenuTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task WhenAPageIsShown_ThenOnlyTheButtonOfThePageMenuStandsInItsHead()
    {
        // Arrange & Act
        var page = await OpenPageAsync();

        // Assert
        var head = page.GetByTestId("page-head");
        await Assertions.Expect(head.GetByRole(AriaRole.Button, new() { Name = "Page info and edit mode" })).ToBeVisibleAsync(new() { Timeout = Timeout });
        await Assertions.Expect(head).ToHaveTextAsync("");
        await Assertions.Expect(page.GetByTestId("page-last-modified")).ToBeHiddenAsync();
    }

    [Fact]
    public async Task WhenThePageMenuIsOpened_ThenItShowsTheTitleThePathAndTheTimeOfTheLastChange()
    {
        // Arrange
        var page = await OpenPageAsync();

        // Act
        await page.GetByTestId("edit-mode-menu").ClickAsync();

        // Assert
        var info = page.GetByTestId("page-info");
        await Assertions.Expect(info).ToBeVisibleAsync(new() { Timeout = Timeout });
        await Assertions.Expect(info).ToContainTextAsync("Spacing Demo");
        await Assertions.Expect(page.GetByTestId("page-path")).ToHaveTextAsync("Demo/Spacing.md");
        await Assertions.Expect(page.GetByTestId("page-last-modified")).ToHaveTextAsync(new Regex(@"^Modified \d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$"));
    }

    [Fact]
    public async Task WhenTheInfoOfThePageMenuIsClicked_ThenTheMenuStaysOpenAndAnEditModeCanBeChosen()
    {
        // Arrange
        var page = await OpenPageAsync();
        await page.GetByTestId("edit-mode-menu").ClickAsync();

        // Act
        await page.GetByTestId("page-info").ClickAsync();
        await page.Locator(".mud-popover-open").GetByRole(AriaRole.Menuitem, new() { Name = "Split", Exact = true }).ClickAsync();

        // Assert
        await Assertions.Expect(page.GetByTestId("edit-splitter")).ToBeVisibleAsync(new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenThePageMenuIsOpenedWithTheKeyboard_ThenTheInfoHasTheFocusAndEscapeGivesItBackToTheButton()
    {
        // Arrange
        var page = await OpenPageAsync();
        var button = page.GetByRole(AriaRole.Button, new() { Name = "Page info and edit mode" });
        await button.FocusAsync();

        // Act & Assert
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(page.GetByTestId("page-info")).ToBeFocusedAsync(new() { Timeout = Timeout });

        await page.Keyboard.PressAsync("Escape");
        await Assertions.Expect(page.GetByTestId("page-info")).ToBeHiddenAsync(new() { Timeout = Timeout });
        await Assertions.Expect(button).ToBeFocusedAsync(new() { Timeout = Timeout });
    }

    private async Task<IPage> OpenPageAsync()
    {
        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync($"{_fixture.ServerAddress}pages/Demo/Spacing.md");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        // The page menu is prerendered, and a click before the circuit is connected is lost.
        await page.WaitUntilInteractiveAsync(Timeout);
        return page;
    }
}
