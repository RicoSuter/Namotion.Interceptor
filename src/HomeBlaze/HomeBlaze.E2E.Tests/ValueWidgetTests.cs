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

    private static readonly Regex TemperaturePattern = new(@"^-?\d+(\.\d+)?°C$");

    private readonly PlaywrightFixture _fixture;

    public ValueWidgetTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<IPage> OpenPageAsync(int? viewportWidth = null, string pageName = "Values.md")
    {
        var page = await _fixture.CreatePageAsync();
        if (viewportWidth is not null)
        {
            await page.SetViewportSizeAsync(viewportWidth.Value, 800);
        }

        await page.GotoAsync($"{_fixture.ServerAddress}pages/Demo/{pageName}");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        // The circuit replaces the prerendered elements when it connects. A box measured across that moment belongs
        // to an element that has left the page, and is empty.
        await ExpectInteractiveAsync(page);
        return page;
    }

    // The page that these tests overwrite. Nothing else on it changes, so that only the graph can make it render again.
    private const string ScratchPage = "Scratch.md";

    private const string MissingText = "–";

    // The three tile tests rely on the last render before their act being a hit of the path cache, which the prerender fills:
    // a miss reads the children of the root, the property the act writes, and the page would render without the event.
    private async Task<IPage> OpenQuietPageAsync(string content)
    {
        await _fixture.WriteDemoPageAsync(ScratchPage, "---\ntitle: Scratch\n---\n\n# Scratch\n\n" + content);

        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync($"{_fixture.ServerAddress}pages/Demo/{ScratchPage}");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        // A change of the graph before the circuit is connected would be picked up by its first render, which proves
        // nothing about a page that is open.
        await ExpectInteractiveAsync(page);
        return page;
    }

    // The automatic time zone resolves through JS after interactive rendering, so its title tells that the circuit is connected.
    private static Task ExpectInteractiveAsync(IPage page) =>
        Assertions.Expect(page.GetByTitle(new Regex(@"^Automatic \("))).ToBeVisibleAsync(new() { Timeout = Timeout });

    private static string CreateTilePage(string subjectName) =>
        $":: HomeBlaze.Components.ValueTile Path=\"/{subjectName}/Label\" Label=\"Followed\"\n::\n";

    private static string CreateExpressionPage(string subjectName) =>
        $"Start {{{{ /{subjectName}/Label }}}} End\n";

    private static ILocator GetExpressionParagraph(IPage page) =>
        page.Locator(".markdown-content p").Filter(new() { HasText = "Start" });

    // The motor of the test page gives its temperature another value every second. A page also renders without a change of a value
    // while it starts, when the circuit takes over from the prerendered page and when the time zone of the viewer arrives, so the
    // first other text proves no live update. The second one comes after the circuit is interactive and after a first change.
    private static async Task ExpectTextToKeepChangingAsync(IPage page, ILocator text)
    {
        await ExpectInteractiveAsync(page);

        var first = await text.InnerTextAsync();
        await Assertions.Expect(text).Not.ToHaveTextAsync(first, new() { Timeout = Timeout });

        var second = await text.InnerTextAsync();
        await Assertions.Expect(text).Not.ToHaveTextAsync(second, new() { Timeout = Timeout });
    }

    private static ILocator GetCard(IPage page, string title) =>
        page.GetByTestId("card").Filter(new() { HasText = title });

    private static ILocator GetRow(IPage page, string label) =>
        page.GetByTestId("value-row").Filter(new() { Has = page.GetByTestId("value-label").GetByText(label, new() { Exact = true }) });

    private static ILocator GetCardRow(ILocator card, string label) =>
        card.GetByTestId("value-row").Filter(new() { Has = card.Page.GetByTestId("value-label").GetByText(label, new() { Exact = true }) });

    private static ILocator GetPanel(IPage page, string title) =>
        page.GetByTestId("expansion-panel").Filter(new() { HasText = title });

    // A progress in the content of a row is a bar too, and the test of the share means the one on its own.
    private static ILocator GetBarOutsideRows(IPage page) =>
        page.Locator("[data-testid=progress]:not([data-testid=value-row] *)");

    // The path of an icon is what tells two icons of MudBlazor apart.
    private static Task<string?> GetIconShapeAsync(ILocator row) =>
        row.GetByTestId("value-icon").Locator("path").Last.GetAttributeAsync("d");

    // Positions are fractions of a pixel, because the sizes of fonts and the shares of a flex row are, so the tolerance of 1 pixel
    // is for rounding. A line that is off by a gutter or a margin is 4 pixels or more away.
    private const double PositionTolerance = 1.0;

    // The header of a card that has an indicator at its right is 1 pixel higher than one that has not.
    private const double HeaderHeightTolerance = 2.0;

    // For a distance that is at least a value: rounding of fractions of a pixel, and less than the 4 pixels of a margin.
    private const double DistanceTolerance = 0.5;

    private sealed record ElementBox(double Left, double Top, double Right, double Bottom)
    {
        public double Width => Right - Left;

        public double CenterY => (Top + Bottom) / 2;

        public double Height => Bottom - Top;
    }

    // One part of the page to measure: an element inside a card, inside a row of the card, or the text range of the element.
    private sealed record Part(string? Card, string? Row, string Selector, bool TextOnly = false);

    // The motor of the test page changes the text of its values every second and the width of a text moves the edges of
    // what stands next to it. A test that compares edges therefore takes all of them from one evaluation, so that they belong to the same layout.
    private static async Task<ElementBox[]> GetBoxesAsync(IPage page, params Part[] parts)
    {
        var rectangles = await page.Locator("body").EvaluateAsync<double[][]>(
            """
            (root, parts) => parts.map(part => {
                let scope = root;
                if (part.card) {
                    scope = [...root.querySelectorAll('[data-testid=card]')].find(card => card.textContent.includes(part.card));
                }
                if (part.row) {
                    scope = [...scope.querySelectorAll('[data-testid=value-row]')]
                        .find(row => row.querySelector('[data-testid=value-label]').textContent.trim() === part.row);
                }
                const element = part.selector === ':scope' ? scope : scope.querySelector(part.selector);
                if (!element) {
                    throw new Error('Nothing found for ' + JSON.stringify(part));
                }
                if (!part.textOnly) {
                    const rectangle = element.getBoundingClientRect();
                    return [rectangle.left, rectangle.top, rectangle.right, rectangle.bottom];
                }
                // The range of an element would include the boxes of its child elements, which are wider than their text.
                const box = [Infinity, Infinity, -Infinity, -Infinity];
                const walker = document.createTreeWalker(element, NodeFilter.SHOW_TEXT);
                while (walker.nextNode()) {
                    if (!walker.currentNode.textContent.trim()) {
                        continue;
                    }
                    const range = document.createRange();
                    range.selectNodeContents(walker.currentNode);
                    const rectangle = range.getBoundingClientRect();
                    box[0] = Math.min(box[0], rectangle.left);
                    box[1] = Math.min(box[1], rectangle.top);
                    box[2] = Math.max(box[2], rectangle.right);
                    box[3] = Math.max(box[3], rectangle.bottom);
                }
                return box;
            })
            """,
            parts.Select(part => new { card = part.Card, row = part.Row, selector = part.Selector, textOnly = part.TextOnly }).ToArray());

        return rectangles.Select(values => new ElementBox(values[0], values[1], values[2], values[3])).ToArray();
    }

    private async Task<IPage> OpenPageWhenCardHasValuesAsync(string title)
    {
        // The cells of the narrow card are narrower than 280 pixels only for a window of about 1236 to 1347 pixels.
        var page = await OpenPageAsync(viewportWidth: 1280);
        await Assertions.Expect(GetCard(page, title).GetByTestId("value-text").Last).ToBeVisibleAsync(new() { Timeout = Timeout });
        return page;
    }

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
    public async Task WhenCardHasNoOptions_ThenItIsOutlinedWithoutAShadow()
    {
        // Arrange & Act
        var page = await OpenPageAsync();

        // Assert
        var card = GetCard(page, "Plain Card");
        await Assertions.Expect(card).ToHaveClassAsync(new Regex("mud-paper-outlined"), new() { Timeout = Timeout });
        await Assertions.Expect(card).Not.ToHaveClassAsync(new Regex(@"mud-elevation-[1-9]"));
    }

    [Fact]
    public async Task WhenCardIsNotOutlined_ThenItHasAShadowAndNoBorder()
    {
        // Arrange & Act
        var page = await OpenPageAsync();

        // Assert
        var card = GetCard(page, "Raised Card");
        await Assertions.Expect(card).ToHaveClassAsync(new Regex(@"mud-elevation-[1-9]"), new() { Timeout = Timeout });
        await Assertions.Expect(card).Not.ToHaveClassAsync(new Regex("mud-paper-outlined"));
        await Assertions.Expect(card).Not.ToHaveClassAsync(new Regex("mud-elevation-0"));
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

    [Fact]
    public async Task WhenRowHasPathAndLabel_ThenItShowsTheFormattedValue()
    {
        // Arrange & Act
        var page = await OpenPageAsync();

        // Assert
        await Assertions.Expect(GetRow(page, "Soll").GetByTestId("value-text")).ToHaveTextAsync("800", new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenRowHasNoLabel_ThenLabelIsThePropertyAndValueHasItsUnit()
    {
        // Arrange & Act
        var page = await OpenPageAsync();

        // Assert
        var row = GetRow(page, "Temperature");
        await Assertions.Expect(row.GetByTestId("value-text")).ToHaveTextAsync(TemperaturePattern, new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenValueChanges_ThenRowUpdatesLive()
    {
        // Arrange & Act: the motor of the page changes its temperature by itself.
        var page = await OpenPageAsync();
        var text = GetRow(page, "Temperature").GetByTestId("value-text");
        await Assertions.Expect(text).ToHaveTextAsync(TemperaturePattern, new() { Timeout = Timeout });

        // Assert
        await ExpectTextToKeepChangingAsync(page, text);
        await Assertions.Expect(text).ToHaveTextAsync(TemperaturePattern);
    }

    [Fact]
    public async Task WhenSubjectOfAnAbsolutePathAppears_ThenTheTileShowsItsValueWithoutAReload()
    {
        // Arrange
        var name = PlaywrightFixture.CreatePlainSubjectName();
        var page = await OpenQuietPageAsync(CreateTilePage(name));
        var tile = page.GetByTestId("value-tile");
        await Assertions.Expect(tile.GetByTestId("value-missing")).ToHaveTextAsync(MissingText, new() { Timeout = Timeout });

        // Act
        var (_, subject) = await _fixture.AddPlainSubjectToRootAsync(name, "Appeared label");
        try
        {
            // Assert
            await Assertions.Expect(tile.GetByTestId("value-text")).ToHaveTextAsync("Appeared label", new() { Timeout = Timeout });
            await Assertions.Expect(tile.GetByTestId("value-missing")).ToHaveCountAsync(0);
        }
        finally
        {
            _fixture.RemoveSubjectFromRoot(name, subject);
        }
    }

    [Fact]
    public async Task WhenSubjectOfAnAbsolutePathIsReplaced_ThenTheTileFollowsTheNewSubjectWithoutAReload()
    {
        // Arrange
        var (name, subject) = await _fixture.AddPlainSubjectToRootAsync();
        try
        {
            var page = await OpenQuietPageAsync(CreateTilePage(name));
            var text = page.GetByTestId("value-tile").GetByTestId("value-text");
            await Assertions.Expect(text).ToHaveTextAsync("Original label", new() { Timeout = Timeout });

            // Act
            subject = _fixture.ReplacePlainSubjectInRoot(name, subject, "Replaced label");

            // Assert
            await Assertions.Expect(text).ToHaveTextAsync("Replaced label", new() { Timeout = Timeout });

            // Act: the render that showed the new subject also has to record the read of its value.
            subject.Label = "Changed label";

            // Assert
            await Assertions.Expect(text).ToHaveTextAsync("Changed label", new() { Timeout = Timeout });
        }
        finally
        {
            _fixture.RemoveSubjectFromRoot(name, subject);
        }
    }

    [Fact]
    public async Task WhenSubjectOfAnAbsolutePathIsRemoved_ThenTheTileShowsADashWithoutAReload()
    {
        // Arrange
        var (name, subject) = await _fixture.AddPlainSubjectToRootAsync();
        var isRemoved = false;
        try
        {
            var page = await OpenQuietPageAsync(CreateTilePage(name));
            var tile = page.GetByTestId("value-tile");
            await Assertions.Expect(tile.GetByTestId("value-text")).ToHaveTextAsync("Original label", new() { Timeout = Timeout });

            // Act
            _fixture.RemoveSubjectFromRoot(name, subject);
            isRemoved = true;

            // Assert
            await Assertions.Expect(tile.GetByTestId("value-missing")).ToHaveTextAsync(MissingText, new() { Timeout = Timeout });
            await Assertions.Expect(tile.GetByTestId("value-text")).ToHaveCountAsync(0);
        }
        finally
        {
            if (!isRemoved)
            {
                _fixture.RemoveSubjectFromRoot(name, subject);
            }
        }
    }

    [Fact]
    public async Task WhenSubjectOfAnAbsolutePathAppears_ThenTheExpressionShowsItsValueWithoutAReload()
    {
        // Arrange
        var name = PlaywrightFixture.CreatePlainSubjectName();
        var page = await OpenQuietPageAsync(CreateExpressionPage(name));
        var paragraph = GetExpressionParagraph(page);
        await Assertions.Expect(paragraph).ToHaveTextAsync(new Regex(@"^Start\s*End$"), new() { Timeout = Timeout });

        // Act
        var (_, subject) = await _fixture.AddPlainSubjectToRootAsync(name, "Appeared label");
        try
        {
            // Assert
            await Assertions.Expect(paragraph).ToHaveTextAsync("Start Appeared label End", new() { Timeout = Timeout });
        }
        finally
        {
            _fixture.RemoveSubjectFromRoot(name, subject);
        }
    }

    [Fact]
    public async Task WhenSubjectOfAnAbsolutePathIsReplaced_ThenTheExpressionShowsTheValueOfTheNewSubjectWithoutAReload()
    {
        // Arrange
        var (name, subject) = await _fixture.AddPlainSubjectToRootAsync();
        try
        {
            var page = await OpenQuietPageAsync(CreateExpressionPage(name));
            var paragraph = GetExpressionParagraph(page);
            await Assertions.Expect(paragraph).ToHaveTextAsync("Start Original label End", new() { Timeout = Timeout });

            // Act
            subject = _fixture.ReplacePlainSubjectInRoot(name, subject, "Replaced label");

            // Assert
            await Assertions.Expect(paragraph).ToHaveTextAsync("Start Replaced label End", new() { Timeout = Timeout });
        }
        finally
        {
            _fixture.RemoveSubjectFromRoot(name, subject);
        }
    }

    [Fact]
    public async Task WhenRowHasMap_ThenItShowsTheTextOfTheCase()
    {
        // Arrange & Act
        var page = await OpenPageAsync();

        // Assert
        await Assertions.Expect(GetRow(page, "Zustand").GetByTestId("value-text")).ToHaveTextAsync(new Regex("^(steht|startet|läuft|stoppt)$"), new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenPathLeadsNowhere_ThenRowShowsADashAndNoError()
    {
        // Arrange & Act
        var page = await OpenPageAsync();

        // Assert
        var row = GetRow(page, "Fehlt");
        await Assertions.Expect(row.GetByTestId("value-missing")).ToHaveTextAsync("–", new() { Timeout = Timeout });
        await Assertions.Expect(row.GetByTestId("value-text")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task WhenRowHasContentAndNoPath_ThenItShowsTheContentOnly()
    {
        // Arrange & Act
        var page = await OpenPageAsync();

        // Assert
        var row = GetRow(page, "Zwei Werte");
        await Assertions.Expect(row).ToContainTextAsync("800 von 800", new() { Timeout = Timeout });
        await Assertions.Expect(row.GetByTestId("value-text")).ToHaveCountAsync(0);
        await Assertions.Expect(row.GetByTestId("value-missing")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task WhenPageHasValueTile_ThenItShowsLabelAndValue()
    {
        // Arrange & Act
        var page = await OpenPageAsync();

        // Assert
        var tile = page.GetByTestId("value-tile");
        await Assertions.Expect(tile.GetByTestId("value-label")).ToHaveTextAsync("Tile Target", new() { Timeout = Timeout });
        await Assertions.Expect(tile.GetByTestId("value-text")).ToHaveTextAsync("800");
    }

    [Fact]
    public async Task WhenCaseHasColor_ThenValueTextHasThatColor()
    {
        // Arrange & Act
        var page = await OpenPageAsync();

        // Assert
        await Assertions.Expect(GetRow(page, "Zustand").GetByTestId("value-text")).ToHaveClassAsync(new Regex(@"\bmud-(error|warning|success)-text\b"), new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenCaseHasIcon_ThenItReplacesTheIconOfTheRow()
    {
        // Arrange & Act
        var page = await OpenPageAsync();
        await Assertions.Expect(GetRow(page, "Lampe").GetByTestId("value-text")).ToBeVisibleAsync(new() { Timeout = Timeout });

        // Assert
        var rowIcon = await GetIconShapeAsync(GetRow(page, "Lampe"));
        var lightbulb = await GetIconShapeAsync(GetRow(page, "Lampe fest"));
        var speed = await GetIconShapeAsync(GetRow(page, "Soll"));
        Assert.Equal(lightbulb, rowIcon);
        Assert.NotEqual(speed, rowIcon);
    }

    [Fact]
    public async Task WhenOnlyOtherRowsHaveAnIcon_ThenTheLabelsStillLineUp()
    {
        // Arrange & Act
        var page = await OpenPageAsync();
        await Assertions.Expect(GetRow(page, "Ohne Symbol").GetByTestId("value-text")).ToBeVisibleAsync(new() { Timeout = Timeout });

        // Assert
        var withIcon = await GetRow(page, "Lampe").GetByTestId("value-label").BoundingBoxAsync();
        var withoutIcon = await GetRow(page, "Ohne Symbol").GetByTestId("value-label").BoundingBoxAsync();
        Assert.Equal(withIcon!.X, withoutIcon!.X);
    }

    [Fact]
    public async Task WhenNoRowHasAnIcon_ThenLabelsStartAtTheEdge()
    {
        // Arrange & Act
        var page = await OpenPageAsync();
        var list = page.GetByTestId("value-list").Filter(new() { Has = page.GetByTestId("value-label").GetByText("Nur Text", new() { Exact = true }) });
        await Assertions.Expect(list.GetByTestId("value-text").First).ToBeVisibleAsync(new() { Timeout = Timeout });

        // Assert
        var listBox = await list.BoundingBoxAsync();
        var label = await list.GetByTestId("value-label").First.BoundingBoxAsync();
        Assert.True(label!.X - listBox!.X < 40);
    }

    [Fact]
    public async Task WhenValueListIsInACard_ThenTheIconsOfItsRowsLineUpWithTheIconOfTheHeader()
    {
        // Arrange
        var page = await OpenPageWhenCardHasValuesAsync("Aligned Card");

        // Act
        var boxes = await GetBoxesAsync(page,
            new("Aligned Card", null, "[data-testid=card-icon]"),
            new("Aligned Card", "Aligned Icon", "[data-testid=value-icon]"),
            new("Aligned Card", "Aligned Bar", "[data-testid=value-icon]"));

        // Assert
        Assert.Equal(boxes[0].Left, boxes[1].Left, PositionTolerance);
        Assert.Equal(boxes[0].Left, boxes[2].Left, PositionTolerance);
    }

    [Fact]
    public async Task WhenValueListIsInACard_ThenTheLabelsOfItsRowsLineUpWithTheTitleAndARowWithoutAnIconKeepsTheRoomOfOne()
    {
        // Arrange
        var page = await OpenPageWhenCardHasValuesAsync("Aligned Card");

        // Act
        var boxes = await GetBoxesAsync(page,
            new("Aligned Card", null, "[data-testid=card-title]"),
            new("Aligned Card", "Aligned Icon", "[data-testid=value-label]"),
            new("Aligned Card", "Aligned Plain", "[data-testid=value-label]"),
            new("Aligned Card", "Aligned Icon", "[data-testid=value-icon]"),
            new("Aligned Card", "Aligned Plain", "[data-testid=value-icon-space]"),
            new("Aligned Card", null, "[data-testid=card-icon]"));

        // Assert
        Assert.Equal(boxes[0].Left, boxes[1].Left, PositionTolerance);
        Assert.Equal(boxes[0].Left, boxes[2].Left, PositionTolerance);
        Assert.Equal(boxes[3].Width, boxes[4].Width, PositionTolerance);
        Assert.Equal(boxes[5].Left, boxes[4].Left, PositionTolerance);
    }

    [Fact]
    public async Task WhenCardHasAnIndicatorInItsHeader_ThenTheValuesOfTheRowsEndWhereTheIndicatorEndsAndItStandsOnTheTitleLine()
    {
        // Arrange
        var page = await OpenPageWhenCardHasValuesAsync("Aligned Card");

        // Act
        var boxes = await GetBoxesAsync(page,
            new("Aligned Card", null, "[data-testid=card-header-right] [data-testid=indicator]"),
            new("Aligned Card", null, "[data-testid=card-title]"),
            new("Aligned Card", "Aligned Icon", "[data-testid=value-text]"),
            new("Aligned Card", "Aligned Plain", "[data-testid=value-text]"),
            new("Aligned Card", "Aligned Bar", "[data-testid=value-text]"),
            new("Aligned Card", "Aligned Text", "[data-testid=value-text]"));

        // Assert
        Assert.All(boxes.Skip(2), value => Assert.Equal(boxes[0].Right, value.Right, PositionTolerance));
        Assert.Equal(boxes[1].CenterY, boxes[0].CenterY, PositionTolerance);
    }

    [Fact]
    public async Task WhenCardHasAnIconIndicatorInItsHeader_ThenItEndsWhereTheValuesEndAndStandsOnTheTitleLine()
    {
        // Arrange
        var page = await OpenPageWhenCardHasValuesAsync("Icon Indicator Card");

        // Act
        var boxes = await GetBoxesAsync(page,
            new("Icon Indicator Card", null, "[data-testid=card-header-right] [data-testid=indicator]"),
            new("Icon Indicator Card", null, "[data-testid=card-title]"),
            new("Icon Indicator Card", "Icon Row", "[data-testid=value-text]"));

        // Assert
        Assert.Equal(boxes[2].Right, boxes[0].Right, PositionTolerance);
        Assert.Equal(boxes[1].CenterY, boxes[0].CenterY, PositionTolerance);
    }

    [Fact]
    public async Task WhenCardHasAnIconIndicatorInItsHeaderAndNoSubtitle_ThenTheHeaderIsAsHighAsThatOfACardWithoutIndicator()
    {
        // Arrange: the narrow card has an icon and a title and nothing at the right of the header.
        var page = await OpenPageWhenCardHasValuesAsync("Icon Indicator Card");

        // Act
        var boxes = await GetBoxesAsync(page,
            new("Icon Indicator Card", null, ".mud-card-header"),
            new("Icon Indicator Card", null, "[data-testid=card-icon]"),
            new("Icon Indicator Card", null, "[data-testid=card-title]"),
            new("Narrow Card", null, ".mud-card-header"),
            new("Narrow Card", null, "[data-testid=card-icon]"),
            new("Narrow Card", null, "[data-testid=card-title]"));

        // Assert: without mb-n1 on the wrapper of the indicator the header is 4 pixels higher than that of the other card. The
        // 1 pixel that it is higher with it is the measured one, so the tolerance of 2 pixels holds it from the 4 pixels. The title
        // does not move against the icon either way, so the offset between them is the same in both cards.
        Assert.Equal(boxes[3].Height, boxes[0].Height, HeaderHeightTolerance);
        Assert.Equal(boxes[4].CenterY - boxes[5].CenterY, boxes[1].CenterY - boxes[2].CenterY, PositionTolerance);
    }

    [Fact]
    public async Task WhenSubtitleOfACardTakesSeveralLines_ThenTheIconAndTheIndicatorStandOnTheTitleLine()
    {
        // Arrange: the cell of the card is narrow enough for its subtitle to wrap.
        var page = await OpenPageAsync(viewportWidth: 1280);
        await Assertions.Expect(GetCard(page, "Wrapped Card").GetByTestId("card-subtitle")).ToBeVisibleAsync(new() { Timeout = Timeout });

        // Act
        var boxes = await GetBoxesAsync(page,
            new("Wrapped Card", null, "[data-testid=card-title]"),
            new("Wrapped Card", null, "[data-testid=card-subtitle]"),
            new("Wrapped Card", null, "[data-testid=card-icon]"),
            new("Wrapped Card", null, "[data-testid=card-header-right] [data-testid=indicator]"));

        // Assert: a subtitle of one line is lower than the title, so one that is higher has wrapped.
        Assert.True(boxes[1].Height > boxes[0].Height, $"The subtitle is {boxes[1].Height} pixels high, the title {boxes[0].Height}");
        Assert.Equal(boxes[0].CenterY, boxes[2].CenterY, PositionTolerance);
        Assert.Equal(boxes[0].CenterY, boxes[3].CenterY, PositionTolerance);
    }

    [Fact]
    public async Task WhenRowHasContent_ThenTheBarFillsTheRoomBeforeTheValueAndTextStandsRightBeforeIt()
    {
        // Arrange
        var page = await OpenPageWhenCardHasValuesAsync("Aligned Card");

        // Act
        var boxes = await GetBoxesAsync(page,
            new("Aligned Card", "Aligned Bar", "[data-testid=progress]"),
            new("Aligned Card", "Aligned Bar", "[data-testid=value-label]"),
            new("Aligned Card", "Aligned Bar", "[data-testid=value-text]"),
            new("Aligned Card", "Aligned Text", "[data-testid=value-content]", TextOnly: true),
            new("Aligned Card", "Aligned Text", "[data-testid=value-text]"));

        // Assert: the gap between two parts of a row is 8 pixels.
        Assert.Equal(boxes[1].Right + 8, boxes[0].Left, PositionTolerance);
        Assert.Equal(boxes[2].Left - 8, boxes[0].Right, PositionTolerance);
        Assert.Equal(boxes[4].Left - 8, boxes[3].Right, PositionTolerance);
    }

    [Fact]
    public async Task WhenCardIsNarrow_ThenItsRowsDoNotOverflowAndTheBarIsStillVisible()
    {
        // Arrange
        var page = await OpenPageWhenCardHasValuesAsync("Narrow Card");

        // Act
        var boxes = await GetBoxesAsync(page,
            new("Narrow Card", null, ":scope"),
            new("Narrow Card", "Speed", "[data-testid=value-text]"),
            new("Narrow Card", "Speed", "[data-testid=progress]"),
            new("Narrow Card", null, ".mud-card-content"));
        var overflow = await GetCard(page, "Narrow Card").EvaluateAsync<double[]>(
            "card => [card, ...card.querySelectorAll('[data-testid=value-row]')].map(element => element.scrollWidth - element.clientWidth)");

        // Assert: the cell is narrower than the card of the dashboard, which has to fit as well.
        Assert.True(boxes[0].Width < 280, $"The card is {boxes[0].Width} pixels wide");
        Assert.All(overflow, scrollOverflow => Assert.Equal(0, scrollOverflow));
        Assert.True(boxes[1].Right <= boxes[0].Right, $"The value ends at {boxes[1].Right}, the card at {boxes[0].Right}");
        Assert.Equal(boxes[3].Right - 12, boxes[1].Right, PositionTolerance);
        Assert.True(boxes[2].Width >= 40, $"The bar is {boxes[2].Width} pixels wide");
    }

    [Fact]
    public async Task WhenValueListIsNotInACard_ThenItsRowsStartAtTheLeftEdgeOfTheTextAroundIt()
    {
        // Arrange
        var page = await OpenPageAsync();
        await Assertions.Expect(GetRow(page, "Soll").GetByTestId("value-text")).ToBeVisibleAsync(new() { Timeout = Timeout });

        // Act
        var boxes = await GetBoxesAsync(page,
            new(null, null, ".markdown-content h1"),
            new(null, "Soll", "[data-testid=value-icon]"),
            new(null, "Soll", "[data-testid=value-text]"));
        var listRight = await GetRow(page, "Soll").EvaluateAsync<double>(
            "row => row.closest('[data-testid=value-list]').getBoundingClientRect().right");

        // Assert
        Assert.Equal(boxes[0].Left, boxes[1].Left, PositionTolerance);
        Assert.Equal(listRight, boxes[2].Right, PositionTolerance);
    }

    [Fact]
    public async Task WhenRowHasAnIcon_ThenItHasTheMutedColourOfTheIconsOfAList()
    {
        // Arrange
        var page = await OpenPageWhenCardHasValuesAsync("Aligned Card");

        // Act: the variable is resolved next to the icon, because the theme may define it on an element below the body.
        var colours = await GetCard(page, "Aligned Card").GetByTestId("value-icon").First.EvaluateAsync<string[]>(
            """
            icon => {
                const probe = document.createElement('span');
                probe.style.color = 'var(--mud-palette-action-default)';
                icon.parentElement.appendChild(probe);
                const expected = getComputedStyle(probe).color;
                probe.remove();
                return [getComputedStyle(icon).color, expected];
            }
            """);

        // Assert
        Assert.Equal(colours[1], colours[0]);
        Assert.NotEqual("rgba(0, 0, 0, 0)", colours[1]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task WhenIndicatorIsAChipOrAnIcon_ThenItKeepsADistanceOf4PixelsAroundItself(int position)
    {
        // Arrange: the first indicator of the page is a chip and the second one is an icon.
        var page = await OpenPageAsync();
        var indicator = page.GetByTestId("indicator").Nth(position);
        await Assertions.Expect(indicator).ToBeVisibleAsync(new() { Timeout = Timeout });

        // Act
        var margins = await indicator.EvaluateAsync<string[]>(
            "indicator => { const style = getComputedStyle(indicator); return [style.marginTop, style.marginRight, style.marginBottom, style.marginLeft]; }");

        // Assert
        Assert.All(margins, margin => Assert.Equal("4px", margin));
    }

    [Fact]
    public async Task WhenTwoIndicatorChipsFollowEachOther_ThenTheyDoNotTouch()
    {
        // Arrange: the two chips are the fourth and fifth indicator outside of cards. A block stands on a line of its own, so the distance is the one between two lines.
        var page = await OpenPageAsync();
        var chips = page.Locator("[data-testid=indicator]:not([data-testid=card] *)");
        await Assertions.Expect(chips).ToHaveCountAsync(5, new() { Timeout = Timeout });
        await Assertions.Expect(chips.Nth(4)).ToBeVisibleAsync();

        // Act
        var rectangles = await page.EvaluateAsync<double[][]>(
            "() => [...document.querySelectorAll('[data-testid=indicator]:not([data-testid=card] *)')].slice(3, 5).map(chip => { const box = chip.getBoundingClientRect(); return [box.top, box.bottom]; })");

        // Assert: the distance of two lines is 8 pixels, measured as 8.0 on a layout whose sizes are fractions of a pixel, so a
        // result that is a hair below 8 is rounding. A chip that lost its margin on one side is 4 pixels closer.
        var distance = rectangles[1][0] - rectangles[0][1];
        Assert.True(distance >= 8 - DistanceTolerance, $"The second chip starts {distance} pixels below the first");
    }

    [Fact]
    public async Task WhenIndicatorIsAChip_ThenItShowsTheTextOfTheCase()
    {
        // Arrange & Act
        var page = await OpenPageAsync();

        // Assert
        var chip = page.GetByTestId("indicator").First;
        await Assertions.Expect(chip).ToHaveTextAsync(new Regex("^(steht|startet|läuft|stoppt)$"), new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenIndicatorIsAnIcon_ThenItShowsNoText()
    {
        // Arrange & Act
        var page = await OpenPageAsync();

        // Assert
        var icon = page.GetByTestId("indicator").Nth(1);
        await Assertions.Expect(icon).ToBeVisibleAsync(new() { Timeout = Timeout });
        await Assertions.Expect(icon).ToHaveTextAsync(string.Empty);
    }

    [Fact]
    public async Task WhenIndicatorIsAnIcon_ThenItShowsTheTextOfTheCaseAsTooltip()
    {
        // Arrange
        var page = await OpenPageAsync();
        var icon = page.GetByTestId("indicator").Nth(1);
        await Assertions.Expect(icon).ToBeVisibleAsync(new() { Timeout = Timeout });

        // Act
        await icon.HoverAsync();

        // Assert
        await Assertions.Expect(page.Locator(".mud-tooltip.mud-popover-open")).ToHaveTextAsync(new Regex("^(steht|startet|läuft|stoppt)$"), new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenIndicatorHasNoValue_ThenChipShowsADashWithThePathAsTooltip()
    {
        // Arrange & Act
        var page = await OpenPageAsync();

        // Assert
        var chip = page.GetByTestId("indicator").Nth(2);
        await Assertions.Expect(chip).ToHaveTextAsync("–", new() { Timeout = Timeout });
        await Assertions.Expect(page.Locator(".markdown-block-error")).ToHaveCountAsync(0);

        // The map error that the page shows is that of the indicator with a map that does not exist.
        await Assertions.Expect(page.GetByTestId("value-map-error")).ToHaveCountAsync(1);

        await chip.HoverAsync();
        await Assertions.Expect(page.Locator(".mud-tooltip").GetByText("valuemotor/Missing")).ToBeVisibleAsync(new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenMapOfAWidgetDoesNotExist_ThenTheWidgetShowsTheError()
    {
        // Arrange & Act
        var page = await OpenPageAsync();

        // Assert
        await Assertions.Expect(page.GetByTestId("value-map-error")).ToHaveTextAsync("Cannot resolve map: nomap", new() { Timeout = Timeout });
        // Outside the cards are the three indicators of the first blocks and the two chips that follow them.
        await Assertions.Expect(page.Locator("[data-testid=indicator]:not([data-testid=card] *)")).ToHaveCountAsync(5);
    }

    [Fact]
    public async Task WhenProgressHasARange_ThenBarShowsTheShareOfTheValue()
    {
        // Arrange & Act
        var page = await OpenPageAsync();

        // Assert
        var bar = GetBarOutsideRows(page);
        await Assertions.Expect(bar).ToHaveCountAsync(1, new() { Timeout = Timeout });
        await Assertions.Expect(bar).ToHaveAttributeAsync("data-percent", "50");
    }

    [Fact]
    public async Task WhenRowHasAProgressAsContent_ThenTheBarHasAWidthAndStandsBeforeTheValue()
    {
        // Arrange & Act
        var page = await OpenPageAsync();
        var row = GetRow(page, "Balken");
        await Assertions.Expect(row.GetByTestId("value-text")).ToHaveTextAsync("800", new() { Timeout = Timeout });

        // Assert
        var bar = row.GetByTestId("progress");
        await Assertions.Expect(bar).ToBeVisibleAsync();
        var barBox = await bar.BoundingBoxAsync();
        var valueBox = await row.GetByTestId("value-text").BoundingBoxAsync();
        Assert.True(barBox!.Width >= 100, $"The bar is {barBox.Width} pixels wide");
        Assert.True(barBox.X + barBox.Width <= valueBox!.X, $"The bar ends at {barBox.X + barBox.Width}, the value starts at {valueBox.X}");
    }

    [Fact]
    public async Task WhenCardHasImageAndFooter_ThenBothAreShown()
    {
        // Arrange & Act
        var page = await OpenPageAsync();

        // Assert
        var card = GetCard(page, "Picture Card");
        await Assertions.Expect(card.GetByTestId("card-footer")).ToHaveTextAsync("Picture card footer", new() { Timeout = Timeout });
        var media = card.Locator(".mud-card-media");
        await Assertions.Expect(media).ToHaveCountAsync(1);
        await Assertions.Expect(media).ToHaveAttributeAsync("style", new Regex(@"background-image:\s*url\(.*/favicon\.ico"));
    }

    [Fact]
    public async Task WhenPanelIsExpandedOnThePage_ThenItsContentIsVisible()
    {
        // Arrange & Act
        var page = await OpenPageAsync();

        // Assert
        var content = GetPanel(page, "Open Panel").GetByTestId("expansion-panel-content");
        await Assertions.Expect(content).ToBeVisibleAsync(new() { Timeout = Timeout });
        await Assertions.Expect(content).ToContainTextAsync("Open panel target 800");
    }

    [Fact]
    public async Task WhenClosedPanelIsOpened_ThenItsContentShowsALiveValue()
    {
        // Arrange
        var page = await OpenPageAsync();
        var panel = GetPanel(page, "Closed Panel");
        var content = panel.GetByTestId("expansion-panel-content");
        await Assertions.Expect(content).ToBeAttachedAsync(new() { Timeout = Timeout });
        await Assertions.Expect(content).ToBeHiddenAsync();

        // Act
        await panel.GetByText("Closed Panel", new() { Exact = true }).ClickAsync();

        // Assert
        await Assertions.Expect(content).ToBeVisibleAsync();
        var text = content.GetByText("Closed panel temperature");
        await Assertions.Expect(text).ToHaveTextAsync(new Regex(@"^Closed panel temperature -?\d+(\.\d+)?°C$"), new() { Timeout = Timeout });
        await ExpectTextToKeepChangingAsync(page, text);
    }

    [Fact]
    public async Task WhenValueIsInAGridCell_ThenItUpdatesLive()
    {
        // Arrange & Act: the motor of the page changes its temperature by itself.
        var page = await OpenPageAsync(pageName: "LiveLayouts.md");
        var text = page.Locator(".grid-cell").GetByTestId("value-tile").GetByTestId("value-text");
        await Assertions.Expect(text).ToHaveTextAsync(TemperaturePattern, new() { Timeout = Timeout });

        // Assert
        await ExpectTextToKeepChangingAsync(page, text);
        await Assertions.Expect(text).ToHaveTextAsync(TemperaturePattern);
    }

    [Fact]
    public async Task WhenValueIsInACanvasNode_ThenItUpdatesLive()
    {
        // Arrange & Act: the motor of the page changes its temperature by itself.
        var page = await OpenPageAsync(pageName: "LiveLayouts.md");
        var text = page.Locator(".canvas-node").GetByTestId("value-tile").GetByTestId("value-text");
        await Assertions.Expect(text).ToHaveTextAsync(TemperaturePattern, new() { Timeout = Timeout });

        // Assert
        await ExpectTextToKeepChangingAsync(page, text);
        await Assertions.Expect(text).ToHaveTextAsync(TemperaturePattern);
    }

    [Fact]
    public async Task WhenLayoutIsSingle_ThenOpeningAPanelClosesTheOther()
    {
        // Arrange
        var page = await OpenPageAsync();
        var first = GetPanel(page, "Exclusive A").GetByTestId("expansion-panel-content");
        var second = GetPanel(page, "Exclusive B").GetByTestId("expansion-panel-content");
        await Assertions.Expect(first).ToBeVisibleAsync(new() { Timeout = Timeout });
        await Assertions.Expect(second).ToBeHiddenAsync();

        // Act
        await GetPanel(page, "Exclusive B").GetByText("Exclusive B", new() { Exact = true }).ClickAsync();

        // Assert
        await Assertions.Expect(second).ToBeVisibleAsync();
        await Assertions.Expect(first).ToBeHiddenAsync();
    }

    [Fact]
    public async Task WhenPageHasAlert_ThenItShowsItsTextAndTheIconOfItsSeverity()
    {
        // Arrange & Act
        var page = await OpenPageAsync();

        // Assert
        var alert = page.GetByTestId("alert");
        await Assertions.Expect(alert).ToContainTextAsync("Alert text", new() { Timeout = Timeout });
        await Assertions.Expect(alert).ToHaveClassAsync(new Regex("mud-alert-text-warning"));
        await Assertions.Expect(alert.Locator("svg")).ToHaveCountAsync(1);
    }

    [Fact]
    public async Task WhenAlertHasAOneLineText_ThenTheTextIsCentredNextToTheIcon()
    {
        // Arrange & Act
        var page = await OpenPageAsync();
        var alert = page.GetByTestId("alert");
        await Assertions.Expect(alert).ToContainTextAsync("Alert text", new() { Timeout = Timeout });

        // Assert
        var icon = await alert.Locator("svg").BoundingBoxAsync();
        var text = await alert.Locator("p").BoundingBoxAsync();
        Assert.InRange(Math.Abs((icon!.Y + icon.Height / 2) - (text!.Y + text.Height / 2)), 0, 4);
    }
}
