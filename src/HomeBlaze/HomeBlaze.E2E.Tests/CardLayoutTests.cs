using HomeBlaze.E2E.Tests.Infrastructure;
using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for the spacing and the height of a card on a markdown page.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class CardLayoutTests
{
    private const int Timeout = 30000;

    private readonly PlaywrightFixture _fixture;

    public CardLayoutTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<IPage> OpenPageAsync()
    {
        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync($"{_fixture.ServerAddress}pages/Demo/Cards.md");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        // The edit menu is prerendered, and a click before the circuit is connected is lost.
        await page.WaitUntilInteractiveAsync(Timeout);
        return page;
    }

    [Fact]
    public async Task WhenACardHasOneSentence_ThenLittleRoomIsLeftAboveAndBelowTheText()
    {
        // Arrange
        var page = await OpenPageAsync();
        var card = page.GetCard("Sentence Card");
        await Assertions.Expect(card).ToBeVisibleAsync(new() { Timeout = Timeout });

        // Act: the lines of the text, not the boxes of the elements, which include the margins and the line height.
        var gaps = await card.EvaluateAsync<double[]>(
            """
            card => {
                const lines = element => {
                    const range = document.createRange();
                    range.selectNodeContents(element);
                    return [...range.getClientRects()].filter(rect => rect.height > 0);
                };
                const title = lines(card.querySelector('[data-testid=card-title]'));
                const body = lines(card.querySelector('[data-testid=card-content]'));
                const bottom = card.getBoundingClientRect().bottom;
                return [
                    Math.min(...body.map(rect => rect.top)) - Math.max(...title.map(rect => rect.bottom)),
                    bottom - Math.max(...body.map(rect => rect.bottom))
                ];
            }
            """);

        // Assert: the padding of 12 pixels and the space that the line height leaves around the letters.
        Assert.InRange(gaps[0], 0, 16);
        Assert.InRange(gaps[1], 12, 20);
    }

    [Fact]
    public async Task WhenACardHasAHeight_ThenItIsThatHighAndItsBodyScrolls()
    {
        // Arrange
        var page = await OpenPageAsync();
        var card = page.GetCard("Fixed Card");
        await Assertions.Expect(card).ToBeVisibleAsync(new() { Timeout = Timeout });

        // Act
        var size = await card.EvaluateAsync<double[]>(
            """
            card => {
                const body = card.querySelector('[data-testid=card-content]');
                return [card.getBoundingClientRect().height, body.scrollHeight, body.clientHeight];
            }
            """);

        // Assert
        Assert.Equal(150, size[0], 1.0);
        Assert.True(size[1] > size[2], $"The body is {size[1]} pixels high and shows {size[2]}.");
        await Assertions.Expect(card.GetByTestId("card-footer")).ToContainTextAsync("Footer of the fixed card");
        await Assertions.Expect(card.GetByTestId("card-footer")).ToBeInViewportAsync();
    }

    [Fact]
    public async Task WhenACardHasAnAspectRatio_ThenItsHeightFollowsItsWidth()
    {
        // Arrange
        var page = await OpenPageAsync();
        var card = page.GetCard("Ratio Card");
        await Assertions.Expect(card).ToBeVisibleAsync(new() { Timeout = Timeout });

        // Act
        var size = await card.EvaluateAsync<double[]>(
            "card => [card.getBoundingClientRect().width, card.getBoundingClientRect().height]");

        // Assert
        Assert.Equal(size[0] / 2, size[1], 1.0);
    }

    [Fact]
    public async Task WhenAnAspectRatioIsInvalid_ThenTheCardShowsAnArgumentError()
    {
        // Arrange & Act
        var page = await OpenPageAsync();

        // Assert
        await Assertions.Expect(page.GetByTestId("widget-argument-error"))
            .ToHaveTextAsync("AspectRatio must look like 4:3: 2/1", new() { Timeout = Timeout });
        await Assertions.Expect(page.GetByText("Body of the invalid ratio card.")).ToHaveCountAsync(0);
    }

    [Theory]
    [InlineData("Grid Card", ".grid-cell-content", false)]
    [InlineData("Grid Card", ".grid-cell-content", true)]
    [InlineData("Canvas Card", ".canvas-node-content", false)]
    [InlineData("Canvas Card", ".canvas-node-content", true)]
    public async Task WhenACardIsInAGridCellOrACanvasNode_ThenItFillsItWithoutAFrameOfItsOwn(string title, string container, bool isInlineMode)
    {
        // Arrange
        var page = await OpenPageAsync();
        var card = page.GetCard(title);
        await Assertions.Expect(card).ToBeVisibleAsync(new() { Timeout = Timeout });

        // Act
        if (isInlineMode)
        {
            await page.SelectEditModeAsync("Inline", Timeout);
            await Assertions.Expect(page.Locator(container).Filter(new() { Has = card }).Locator(".subject-component-wrapper.selectable"))
                .ToHaveCountAsync(1, new() { Timeout = Timeout });
        }

        // Assert
        var measures = await card.EvaluateAsync<double[]>(
            """
            (card, container) => {
                const style = getComputedStyle(card);
                return [
                    card.getBoundingClientRect().height,
                    card.closest(container).getBoundingClientRect().height,
                    parseFloat(style.borderTopWidth),
                    style.boxShadow === 'none' ? 0 : 1
                ];
            }
            """,
            container);
        Assert.True(measures[1] > 100, $"The container is {measures[1]} pixels high.");
        Assert.Equal(measures[1], measures[0], 1.0);
        Assert.Equal(0, measures[2]);
        Assert.Equal(0, measures[3]);
    }

    [Theory]
    [InlineData("Grid Ratio Card", ".grid-cell-content")]
    [InlineData("Canvas Ratio Card", ".canvas-node-content")]
    public async Task WhenACardWithAnAspectRatioIsInAGridCellOrACanvasNode_ThenItFillsItsWidthAndHeight(string title, string container)
    {
        // Arrange
        var page = await OpenPageAsync();
        var card = page.GetCard(title);
        await Assertions.Expect(card).ToBeVisibleAsync(new() { Timeout = Timeout });

        // Act: the size of the card and the content box of its container from one call.
        var measures = await card.EvaluateAsync<double[]>(
            """
            (card, container) => {
                const frame = card.closest(container);
                const style = getComputedStyle(frame);
                const cardBox = card.getBoundingClientRect();
                return [
                    cardBox.width,
                    cardBox.height,
                    frame.clientWidth - parseFloat(style.paddingLeft) - parseFloat(style.paddingRight),
                    frame.clientHeight - parseFloat(style.paddingTop) - parseFloat(style.paddingBottom)
                ];
            }
            """,
            container);

        // Assert
        Assert.Equal(measures[2], measures[0], 2.0);
        Assert.Equal(measures[3], measures[1], 2.0);
    }

    [Theory]
    [InlineData("Grid Card After Hidden Block", ".grid-cell-content")]
    [InlineData("Grid Card Before Hidden Block", ".grid-cell-content")]
    [InlineData("Canvas Card After Hidden Block", ".canvas-node-content")]
    public async Task WhenALoneCardSharesItsContentWithABlockThatShowsNothing_ThenItStillFillsTheCellOrTheNode(string title, string container)
    {
        // Arrange
        var page = await OpenPageAsync();
        var card = page.GetCard(title);
        await Assertions.Expect(card).ToBeVisibleAsync(new() { Timeout = Timeout });

        // Act: the distance of each edge of the card from the content box of its container, and the empty wrappers.
        var measures = await card.EvaluateAsync<double[]>(
            """
            (card, container) => {
                const frame = card.closest(container);
                const style = getComputedStyle(frame);
                const frameBox = frame.getBoundingClientRect();
                const cardBox = card.getBoundingClientRect();
                const left = frameBox.left + frame.clientLeft + parseFloat(style.paddingLeft);
                const top = frameBox.top + frame.clientTop + parseFloat(style.paddingTop);
                const right = frameBox.left + frame.clientLeft + frame.clientWidth - parseFloat(style.paddingRight);
                const bottom = frameBox.top + frame.clientTop + frame.clientHeight - parseFloat(style.paddingBottom);
                return [
                    cardBox.left - left,
                    cardBox.top - top,
                    right - cardBox.right,
                    bottom - cardBox.bottom,
                    bottom - top,
                    frame.querySelectorAll('.subject-component-wrapper:empty').length
                ];
            }
            """,
            container);

        // Assert
        Assert.Equal(1, measures[5]);
        Assert.True(measures[4] > 100, $"The container is {measures[4]} pixels high.");
        Assert.Equal(0, measures[0], 2.0);
        Assert.Equal(0, measures[1], 2.0);
        Assert.Equal(0, measures[2], 2.0);
        Assert.Equal(0, measures[3], 2.0);
    }
}
