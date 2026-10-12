using System.Text.RegularExpressions;
using HomeBlaze.E2E.Tests.Infrastructure;
using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for the card options and the value widgets on markdown pages.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class ValueWidgetTests
{
    private const int Timeout = 30000;

    private readonly PlaywrightFixture _fixture;

    public ValueWidgetTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<IPage> OpenPageAsync()
    {
        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync($"{_fixture.ServerAddress}pages/Demo/Values.md");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        return page;
    }

    private static ILocator GetCard(IPage page, string title) =>
        page.GetByTestId("card").Filter(new() { HasText = title });

    [Fact]
    public async Task WhenCardHasHeaderOptions_ThenHeaderShowsThem()
    {
        // Arrange & Act
        var page = await OpenPageAsync();

        // Assert
        var card = GetCard(page, "Linked Card");
        await Assertions.Expect(card.GetByTestId("card-subtitle")).ToHaveTextAsync("Card subtitle", new() { Timeout = Timeout });
        await Assertions.Expect(card.GetByTestId("card-icon")).ToBeVisibleAsync();
        await Assertions.Expect(card.GetByTestId("card-header-right")).ToContainTextAsync("Corner text");
        await Assertions.Expect(card).ToContainTextAsync("Body of the linked card.");
        await Assertions.Expect(card).ToHaveClassAsync(new Regex("mud-paper-outlined"));
    }

    [Fact]
    public async Task WhenCardHasLink_ThenTitleLeadsToThePageNextToIt()
    {
        // Arrange & Act
        var page = await OpenPageAsync();

        // Assert
        var title = GetCard(page, "Linked Card").GetByTestId("card-title");
        await Assertions.Expect(title).ToHaveAttributeAsync("href", "/pages/Demo/Blocks.md", new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenCardHasOnlyATitle_ThenItHasNoLinkAndNoHeaderParts()
    {
        // Arrange & Act
        var page = await OpenPageAsync();

        // Assert
        var card = GetCard(page, "Plain Card");
        await Assertions.Expect(card.GetByTestId("card-title")).ToHaveTextAsync("Plain Card", new() { Timeout = Timeout });
        await Assertions.Expect(card.Locator("a")).ToHaveCountAsync(0);
        await Assertions.Expect(card.GetByTestId("card-subtitle")).ToHaveCountAsync(0);
        await Assertions.Expect(card.GetByTestId("card-header-right")).ToHaveCountAsync(0);
        await Assertions.Expect(card.GetByTestId("card-footer")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task WhenPageRenders_ThenItShowsNoBlockErrors()
    {
        // Arrange & Act
        var page = await OpenPageAsync();

        // Assert
        await Assertions.Expect(GetCard(page, "Plain Card")).ToBeVisibleAsync(new() { Timeout = Timeout });
        await Assertions.Expect(page.Locator(".markdown-block-error")).ToHaveCountAsync(0);
    }
}
