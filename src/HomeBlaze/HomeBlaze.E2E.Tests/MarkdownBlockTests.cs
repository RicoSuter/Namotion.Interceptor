using System.Text.RegularExpressions;
using HomeBlaze.E2E.Tests.Infrastructure;
using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for widget blocks and slots on markdown pages.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class MarkdownBlockTests
{
    private static readonly Regex TemperaturePattern = new(@"temperature of -?\d+([.,]\d+)? °C");

    private readonly PlaywrightFixture _fixture;

    public MarkdownBlockTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<IPage> OpenPageAsync(string name)
    {
        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync($"{_fixture.ServerAddress}pages/Demo/{name}");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        return page;
    }

    private static ILocator GetCard(IPage page, string title)
    {
        return page.GetByTestId("card").Filter(new() { HasText = title });
    }

    [Fact]
    public async Task WhenPageHasCard_ThenTitleContentAndFooterAreInsideIt()
    {
        // Arrange & Act
        var page = await OpenPageAsync("Blocks.md");

        // Assert
        var card = page.GetByTestId("card").First;
        await Assertions.Expect(card).ToBeVisibleAsync(new() { Timeout = 30000 });
        await Assertions.Expect(card).ToContainTextAsync("Motor Card");
        await Assertions.Expect(card).ToContainTextAsync("Card body with a live value");
        await Assertions.Expect(card).ToContainTextAsync("Card footer text");
    }

    [Fact]
    public async Task WhenCardContainsWidgetAndExpression_ThenBothRenderLiveInsideTheCard()
    {
        // Arrange & Act
        var page = await OpenPageAsync("Blocks.md");

        // Assert
        var card = page.GetByTestId("card").First;
        await Assertions.Expect(card).ToContainTextAsync("CardMotor", new() { Timeout = 30000 });
        await Assertions.Expect(card).ToContainTextAsync(new Regex(@"speed is [1-9]\d* RPM"), new() { Timeout = 30000 });
    }

    [Fact]
    public async Task WhenPageRenders_ThenItShowsNoBlockErrors()
    {
        // Arrange & Act
        var page = await OpenPageAsync("Blocks.md");

        // Assert
        await Assertions.Expect(page.GetByTestId("card").First).ToBeVisibleAsync(new() { Timeout = 30000 });
        await Assertions.Expect(page.Locator(".markdown-block-error")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task WhenWidgetsInACardReferToSubjectsByNameAndByRelativePath_ThenBothRenderTheReferencedSubject()
    {
        // Arrange & Act
        var page = await OpenPageAsync("Blocks.md");

        // Assert
        var card = GetCard(page, "Reference Card");
        await Assertions.Expect(card).ToBeVisibleAsync(new() { Timeout = 30000 });
        await Assertions.Expect(card).ToContainTextAsync("CardMotor", new() { Timeout = 30000 });
        await Assertions.Expect(card).ToContainTextAsync("Test Motor", new() { Timeout = 30000 });
        await Assertions.Expect(card).Not.ToContainTextAsync("Cannot resolve path");
    }

    [Fact]
    public async Task WhenExpressionInACardUsesANamedSubject_ThenItsValueChangesOverTime()
    {
        // Arrange
        var page = await OpenPageAsync("Blocks.md");
        var card = GetCard(page, "Reference Card");
        await Assertions.Expect(card).ToContainTextAsync(TemperaturePattern, new() { Timeout = 30000 });

        // Act
        var firstValue = TemperaturePattern.Match(await card.InnerTextAsync()).Value;

        // Assert: the temperature of the motor has noise, so every simulation step gives a new value.
        Assert.NotEmpty(firstValue);
        await Assertions.Expect(card).Not.ToContainTextAsync(firstValue, new() { Timeout = 30000 });
        await Assertions.Expect(card).ToContainTextAsync(TemperaturePattern);
    }

    [Fact]
    public async Task WhenWidgetsReferToTheCardsThatContainThem_ThenAMessageEndsTheRendering()
    {
        // Arrange & Act
        var page = await OpenPageAsync("Cycles.md");

        // Assert
        await Assertions.Expect(page.GetByText("Text after the cards.")).ToBeVisibleAsync(new() { Timeout = 30000 });

        var selfCard = GetCard(page, "Self Card");
        await Assertions.Expect(selfCard).ToHaveCountAsync(1);
        await Assertions.Expect(selfCard.GetByTestId("widget-cycle")).ToHaveCountAsync(1);

        // Whether the first reference or the nested one notices the cycle is not fixed, only that one of them does.
        var firstCard = GetCard(page, "First Card").First;
        await Assertions.Expect(firstCard.GetByTestId("widget-cycle").First).ToBeVisibleAsync();
    }
}
