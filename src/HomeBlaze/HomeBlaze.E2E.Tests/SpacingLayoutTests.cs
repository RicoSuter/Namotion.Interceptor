using System.Text.RegularExpressions;
using HomeBlaze.E2E.Tests.Infrastructure;
using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for the rule that only containers create space: a widget inside a card, a tab or an expansion panel adds
/// no room at the edges of it, and the blocks of a page keep their distance from each other.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class SpacingLayoutTests
{
    private const int Timeout = 30000;

    // Rounding of the boxes and of the line boxes of text.
    private const double Tolerance = 2.0;

    private const double CardPadding = 12;
    private const double TitleGap = 4;
    private const double BlockSpacing = 16;

    private readonly PlaywrightFixture _fixture;

    public SpacingLayoutTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<IPage> OpenPageAsync(bool isInlineMode = false)
    {
        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync($"{_fixture.ServerAddress}pages/Demo/Spacing.md");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        // The edit menu is prerendered, and a click before the circuit is connected is lost.
        await Assertions.Expect(page.GetByTitle(new Regex(@"^Automatic \("))).ToBeVisibleAsync(new() { Timeout = Timeout });
        if (isInlineMode)
        {
            await page.SwitchToInlineModeAsync();
        }

        return page;
    }

    private static ILocator GetCard(IPage page, string title) =>
        page.GetByTestId("card").Filter(new() { Has = page.GetByTestId("card-title").GetByText(title, new() { Exact = true }) });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenAFlowOfGaugesIsInACard_ThenTheCardPaddingAloneIsAroundIt(bool isInlineMode)
    {
        // Arrange
        var page = await OpenPageAsync(isInlineMode);
        var card = GetCard(page, "Gauge Card");
        await Assertions.Expect(card.GetByTestId("gauge")).ToHaveCountAsync(2, new() { Timeout = Timeout });

        // Act
        var gaps = await card.EvaluateAsync<double[]>(
            """
            card => {
                const title = card.querySelector('[data-testid=card-title]').getBoundingClientRect();
                const flow = card.querySelector('[data-testid=flow-layout]').getBoundingClientRect();
                const gauges = [...card.querySelectorAll('[data-testid=gauge]')].map(gauge => gauge.getBoundingClientRect());
                const frame = card.getBoundingClientRect();
                return [
                    flow.top - title.bottom,
                    frame.bottom - flow.bottom,
                    frame.bottom - Math.max(...gauges.map(gauge => gauge.bottom))
                ];
            }
            """);

        // Assert
        Assert.Equal(TitleGap, gaps[0], Tolerance);
        Assert.Equal(CardPadding, gaps[1], Tolerance);
        Assert.Equal(CardPadding, gaps[2], Tolerance);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenBlocksStandInAContainer_ThenTheFirstAndTheLastAddNoSpaceAtItsEdges(bool isInlineMode)
    {
        // Arrange
        var page = await OpenPageAsync(isInlineMode);
        await Assertions.Expect(GetCard(page, "Panel Card")).ToBeVisibleAsync(new() { Timeout = Timeout });

        // Act: for every visible card body, tab panel and expansion panel, the room between its padding and the boxes of
        // its first and last visible block.
        var edges = await page.EvaluateAsync<string[]>(
            """
            () => {
                const results = [];
                const containers = document.querySelectorAll('.card-widget > .mud-card-content, .tab-layout-panel, .mud-expand-panel-content');
                for (const container of containers) {
                    if (container.getBoundingClientRect().height === 0) continue;
                    const style = getComputedStyle(container);
                    const box = container.getBoundingClientRect();
                    const top = box.top + parseFloat(style.paddingTop) + parseFloat(style.borderTopWidth);
                    const bottom = box.bottom - parseFloat(style.paddingBottom) - parseFloat(style.borderBottomWidth);
                    const fragment = container.querySelector(':scope > .subject-component-wrapper > .markdown-fragment');
                    const blocks = [...(fragment ?? container.firstElementChild).children]
                        .map(block => block.getBoundingClientRect())
                        .filter(rect => rect.height > 0);
                    if (blocks.length === 0) continue;
                    const label = container.closest('[data-testid=card]')?.querySelector('[data-testid=card-title]')?.textContent
                        ?? container.className;
                    // The body of a card in a flow grows to the height of the row, below its content.
                    const grows = container.matches('.mud-card-content') && container.closest('.flow-layout-item') !== null;
                    const below = grows ? 0 : Math.round(bottom - blocks[blocks.length - 1].bottom);
                    results.push(`${label.trim()}: ${Math.round(blocks[0].top - top)} ${below}`);
                }
                return results;
            }
            """);

        // Assert: also the tab whose first block is a Responsive that this screen hides.
        Assert.Contains(edges, edge => edge.StartsWith("tab-layout-panel", StringComparison.Ordinal));
        Assert.True(edges.All(edge => edge.EndsWith(": 0 0", StringComparison.Ordinal)), string.Join("; ", edges));
    }

    [Fact]
    public async Task WhenCardsShareARowOfAFlow_ThenTheyAreAsHighAsTheRowAndTheGapsAreEqual()
    {
        // Arrange
        var page = await OpenPageAsync();
        var flow = page.Locator("[data-testid=flow-layout]").Filter(new() { Has = page.GetByText("Short Card", new() { Exact = true }) });
        await Assertions.Expect(flow.GetByTestId("card")).ToHaveCountAsync(4, new() { Timeout = Timeout });

        // Act
        var boxes = await flow.EvaluateAsync<double[][]>(
            "flow => [...flow.querySelectorAll('[data-testid=card]')].map(card => { const box = card.getBoundingClientRect(); return [box.left, box.top, box.right, box.bottom]; })");

        // Assert: the cards of the first row end on one line, and the cells are as far apart across as down.
        var firstRow = boxes.Where(box => Math.Abs(box[1] - boxes[0][1]) < 1).ToArray();
        Assert.True(firstRow.Length is > 1 and < 4, $"The first row has {firstRow.Length} cards.");
        Assert.All(firstRow, box => Assert.Equal(firstRow[0][3], box[3], 1.0));
        Assert.Equal(BlockSpacing, firstRow[1][0] - firstRow[0][2], 1.0);
        Assert.Equal(BlockSpacing, boxes[firstRow.Length][1] - firstRow[0][3], 1.0);
    }

    [Fact]
    public async Task WhenAFlowStandsBetweenParagraphsOfAPage_ThenItKeepsTheSpacingOfBlocks()
    {
        // Arrange
        var page = await OpenPageAsync();
        var flow = page.Locator("[data-testid=flow-layout]").Filter(new() { Has = page.GetByText("Short Card", new() { Exact = true }) });
        await Assertions.Expect(flow).ToBeVisibleAsync(new() { Timeout = Timeout });

        // Act
        var gaps = await page.EvaluateAsync<double[]>(
            """
            () => {
                const paragraph = text => [...document.querySelectorAll('.markdown-content > p')].find(p => p.textContent === text).getBoundingClientRect();
                const flow = [...document.querySelectorAll('[data-testid=flow-layout]')].find(f => f.textContent.includes('Short Card')).getBoundingClientRect();
                const card = [...document.querySelectorAll('[data-testid=card]')].find(c => c.textContent.includes('Gauge Card')).getBoundingClientRect();
                return [
                    flow.top - paragraph('Text before the top flow.').bottom,
                    paragraph('Text after the top flow.').top - flow.bottom,
                    card.top - paragraph('Text before the gauge card.').bottom
                ];
            }
            """);

        // Assert
        Assert.All(gaps, gap => Assert.Equal(BlockSpacing, gap, Tolerance));
    }

    [Fact]
    public async Task WhenAFooterHasTwoButtons_ThenTheyStandInARow()
    {
        // Arrange
        var page = await OpenPageAsync();
        var footer = GetCard(page, "Footer Card").GetByTestId("card-footer");
        await Assertions.Expect(footer.Locator("button")).ToHaveCountAsync(2, new() { Timeout = Timeout });

        // Act
        var boxes = await footer.EvaluateAsync<double[][]>(
            "footer => [...footer.querySelectorAll('button')].map(button => { const box = button.getBoundingClientRect(); return [box.left, box.top, box.right]; })");

        // Assert
        Assert.Equal(boxes[0][1], boxes[1][1], 1.0);
        Assert.Equal(8, boxes[1][0] - boxes[0][2], 1.0);
    }

    [Fact]
    public async Task WhenControlsStandBelowEachOther_ThenTheirVisibleEdgesStartWhereTheTextStarts()
    {
        // Arrange
        var page = await OpenPageAsync();
        var card = GetCard(page, "Controls Card");
        await Assertions.Expect(card.GetByTestId("slider")).ToBeVisibleAsync(new() { Timeout = Timeout });

        // Act
        var edges = await card.EvaluateAsync<double[]>(
            """
            card => {
                const left = selector => card.querySelector(selector).getBoundingClientRect().left;
                const content = card.querySelector('[data-testid=card-content]');
                return [
                    left('[data-testid=card-content]') + parseFloat(getComputedStyle(content).paddingLeft),
                    left('.switch-widget .mud-switch-track'),
                    left('.select-widget fieldset, .select-widget .mud-input-outlined-border'),
                    left('[data-testid=slider-label]')
                ];
            }
            """);

        // Assert
        Assert.Equal(edges[0], edges[1], Tolerance);
        Assert.Equal(edges[0], edges[2], Tolerance);
        Assert.Equal(edges[0], edges[3], Tolerance);
    }
}
