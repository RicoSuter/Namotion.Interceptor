using HomeBlaze.E2E.Tests.Infrastructure;
using HomeBlaze.Samples;
using HomeBlaze.Storage.Files;
using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for the tile kind of the switch and the operation button, and for the layouts that size tiles.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class TileWidgetTests
{
    private const int Timeout = 30000;
    private const string PageName = "Tiles.md";

    private readonly PlaywrightFixture _fixture;

    public TileWidgetTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    private Motor GetMotor() =>
        (Motor)((MarkdownFile)_fixture.GetDemoFolder().Children[PageName]).Children["tilemotor"];

    private async Task<IPage> OpenPageAsync(int? width = null, int height = 900, string? initScript = null)
    {
        var page = await _fixture.CreatePageAsync();

        // Before the page opens: a narrower window closes the navigation drawer with a transition, which the page
        // follows for a moment after the resize.
        if (width is { } viewportWidth)
        {
            await page.SetViewportSizeAsync(viewportWidth, height);
        }

        if (initScript is not null)
        {
            await page.AddInitScriptAsync(initScript);
        }

        await page.GotoAsync($"{_fixture.ServerAddress}pages/Demo/{PageName}");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        await page.WaitUntilInteractiveAsync(Timeout);
        return page;
    }

    private static ILocator GetSwitchTile(IPage page, string label) =>
        page.GetByRole(AriaRole.Switch, new() { Name = label, Exact = true });

    // The root of a tile holds the style of the tile and the badge, next to the button.
    private static ILocator GetTileRoot(ILocator tileButton) => tileButton.Locator("xpath=..");

    private static ILocator GetGridCell(ILocator tile) =>
        tile.Locator("xpath=ancestor::div[contains(@class,'grid-cell')][1]");

    private static Task<string> GetBackgroundAsync(ILocator tileButton) =>
        GetTileRoot(tileButton).EvaluateAsync<string>("element => getComputedStyle(element).backgroundColor");

    [Fact]
    public async Task WhenPageRenders_ThenItShowsNoBlockErrors()
    {
        // Arrange & Act
        var page = await OpenPageAsync();

        // Assert
        await Assertions.Expect(page.GetByTestId("switch-tile").First).ToBeVisibleAsync(new() { Timeout = Timeout });
        await Assertions.Expect(page.Locator(".markdown-block-error")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task WhenSwitchTileIsClicked_ThenItInvokesTheOperationAndTakesTheFillOfTheNewState()
    {
        // Arrange
        GetMotor().IsPowered = true;
        var page = await OpenPageAsync();
        var tile = GetSwitchTile(page, "Motor");
        await Assertions.Expect(tile).ToBeCheckedAsync(new() { Timeout = Timeout });
        var warning = await page.ResolveColorAsync("var(--mud-palette-warning)");
        Assert.Equal(warning, await GetBackgroundAsync(tile));

        // Act
        await tile.ClickAsync();

        // Assert
        await Assertions.Expect(tile).Not.ToBeCheckedAsync(new() { Timeout = Timeout });
        await Assertions.Expect(tile).ToBeEnabledAsync(new() { Timeout = Timeout });
        Assert.False(GetMotor().IsPowered);
        var surface = await page.ResolveColorAsync("var(--mud-palette-surface)");
        Assert.Equal(surface, await GetBackgroundAsync(tile));
        await Assertions.Expect(tile.GetByTestId("tile-subtitle")).ToHaveTextAsync("Hall");
    }

    [Fact]
    public async Task WhenBadgeIsClicked_ThenTheTileToggles()
    {
        // Arrange
        GetMotor().IsPowered = true;
        var page = await OpenPageAsync();
        var tile = GetSwitchTile(page, "Motor");
        await Assertions.Expect(tile).ToBeCheckedAsync(new() { Timeout = Timeout });
        var badge = GetTileRoot(tile).GetByTestId("tile-badge");

        // Act: the badge takes no pointer input, so Playwright's actionability check would wait forever; a forced click
        // goes to the point, where the tile below receives it, which is what a user's click does.
        await badge.ClickAsync(new() { Force = true });

        // Assert
        await Assertions.Expect(tile).Not.ToBeCheckedAsync(new() { Timeout = Timeout });
    }

    [Theory]
    [InlineData("Plain")] // no map
    [InlineData("No case")] // a map without a case for true
    public async Task WhenSwitchTileHasNoCaseForTheValue_ThenItIsFilledWithThePrimaryColorWhileTrue(string label)
    {
        // Arrange
        GetMotor().IsPowered = true;
        var page = await OpenPageAsync();
        var tile = GetSwitchTile(page, label);

        // Act
        await Assertions.Expect(tile).ToBeCheckedAsync(new() { Timeout = Timeout });

        // Assert
        var primary = await page.ResolveColorAsync("var(--mud-palette-primary)");
        Assert.Equal(primary, await GetBackgroundAsync(tile));
    }

    [Fact]
    public async Task WhenSwitchTileMapCaseForTheValueHasNoColor_ThenItStaysOnTheSurfaceWhileTrue()
    {
        // Arrange
        GetMotor().IsPowered = true;
        var page = await OpenPageAsync();
        var tile = GetSwitchTile(page, "Colorless case");

        // Act
        await Assertions.Expect(tile).ToBeCheckedAsync(new() { Timeout = Timeout });

        // Assert
        await Assertions.Expect(tile.GetByTestId("tile-subtitle")).ToHaveTextAsync("Colorless");
        var surface = await page.ResolveColorAsync("var(--mud-palette-surface)");
        Assert.Equal(surface, await GetBackgroundAsync(tile));
    }

    [Fact]
    public async Task WhenSwitchMapCannotBeResolved_ThenItShowsTheErrorInPlaceOfTheSwitch()
    {
        // Arrange & Act
        var page = await OpenPageAsync();

        // Assert
        var error = page.GetByTestId("widget-argument-error").Filter(new() { HasText = "unresolvedtilemap" });
        await Assertions.Expect(error).ToHaveTextAsync("Cannot resolve map: unresolvedtilemap", new() { Timeout = Timeout });
        await Assertions.Expect(GetSwitchTile(page, "Unresolved map")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task WhenSwitchTileHasNoSubtitle_ThenItShowsTheTextOfTheMatchedCase()
    {
        // Arrange
        GetMotor().IsPowered = true;
        var page = await OpenPageAsync();
        var tile = GetSwitchTile(page, "Case text");

        // Act
        await Assertions.Expect(tile).ToBeCheckedAsync(new() { Timeout = Timeout });

        // Assert
        await Assertions.Expect(tile.GetByTestId("tile-subtitle")).ToHaveTextAsync("Running");
    }

    [Fact]
    public async Task WhenSwitchTilePathLeadsNowhere_ThenItShowsAnIconAndIsDisabled()
    {
        // Arrange & Act
        var page = await OpenPageAsync();
        var tile = GetSwitchTile(page, "Missing");

        // Assert
        await Assertions.Expect(tile).ToBeDisabledAsync(new() { Timeout = Timeout });
        await Assertions.Expect(tile.GetByTestId("tile-icon")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task WhenSwitchTileIsDisabledWhileTrue_ThenItKeepsTheFillOfItsStateAndIsDimmed()
    {
        // Arrange: with On and Off swapped, the operation for the other state (PowerOn) is not enabled while powered.
        GetMotor().IsPowered = true;
        var page = await OpenPageAsync();
        var tile = GetSwitchTile(page, "Blocked tile");

        // Act
        await Assertions.Expect(tile).ToBeDisabledAsync(new() { Timeout = Timeout });
        await Assertions.Expect(tile).ToBeCheckedAsync(new() { Timeout = Timeout });

        // Assert
        var primary = await page.ResolveColorAsync("var(--mud-palette-primary)");
        Assert.Equal(primary, await GetBackgroundAsync(tile));
        var opacity = await GetTileRoot(tile).EvaluateAsync<double>("element => parseFloat(getComputedStyle(element).opacity)");
        Assert.InRange(opacity, 0.01, 0.99);
    }

    [Fact]
    public async Task WhenSwitchTileIsEnabled_ThenItIsNotDimmed()
    {
        // Arrange
        GetMotor().IsPowered = true;
        var page = await OpenPageAsync();
        var tile = GetSwitchTile(page, "Motor");

        // Act
        await Assertions.Expect(tile).ToBeEnabledAsync(new() { Timeout = Timeout });

        // Assert
        var opacity = await GetTileRoot(tile).EvaluateAsync<double>("element => parseFloat(getComputedStyle(element).opacity)");
        Assert.Equal(1, opacity);
    }

    [Fact]
    public async Task WhenOperationButtonTilePathLeadsNowhereAndHasNoLabel_ThenItShowsThePathAsItsLabel()
    {
        // Arrange & Act
        var page = await OpenPageAsync();
        var tile = page.GetByRole(AriaRole.Button, new() { Name = "tilemotor/DoesNotExistEither", Exact = true });

        // Assert
        await Assertions.Expect(tile).ToBeDisabledAsync(new() { Timeout = Timeout });
        await Assertions.Expect(tile.GetByTestId("tile-label")).ToHaveTextAsync("tilemotor/DoesNotExistEither");
    }

    [Fact]
    public async Task WhenTileIsTheOnlyContentOfAGridCell_ThenItFillsTheCell()
    {
        // Arrange & Act
        var page = await OpenPageAsync();
        var tile = GetTileRoot(GetSwitchTile(page, "Plain"));
        await Assertions.Expect(tile).ToBeVisibleAsync(new() { Timeout = Timeout });

        // Assert
        var tileBox = await PageGeometry.GetBoxAsync(tile);
        var cellBox = await PageGeometry.GetBoxAsync(GetGridCell(tile));
        Assert.InRange(cellBox.Width - tileBox.Width, 0, 2.5);
        Assert.InRange(cellBox.Height - tileBox.Height, 0, 2.5);
    }

    [Fact]
    public async Task WhenOperationButtonTileHasNoLabel_ThenItShowsOnlyTheIconAndIsDisabledWhileTheOperationIsNotEnabled()
    {
        // Arrange
        GetMotor().IsPowered = true;
        var page = await OpenPageAsync();
        var tile = page.GetByRole(AriaRole.Button, new() { Name = "Power On", Exact = true });

        // Act & Assert: PowerOn is not enabled while the motor is powered.
        await Assertions.Expect(tile).ToBeDisabledAsync(new() { Timeout = Timeout });
        await Assertions.Expect(tile.GetByTestId("tile-icon")).ToBeVisibleAsync();
        await Assertions.Expect(tile.GetByTestId("tile-label")).ToHaveCountAsync(0);

        GetMotor().IsPowered = false;
        await Assertions.Expect(tile).ToBeEnabledAsync(new() { Timeout = Timeout });
    }

    [Theory]
    [InlineData("Square", 1280)]
    [InlineData("Square", 800)]
    // The columns of this grid are narrower than the 96 pixel minimum height of a tile at 800 pixels.
    [InlineData("Narrow", 1280)]
    [InlineData("Narrow", 800)]
    public async Task WhenGridHasASquareCellAspectRatio_ThenItsCellsAreSquareAndTheTileFillsThemAtEveryWidth(string label, int width)
    {
        // Arrange
        var page = await OpenPageAsync(width);
        var tile = GetTileRoot(GetSwitchTile(page, label));
        await Assertions.Expect(tile).ToBeVisibleAsync(new() { Timeout = Timeout });

        // Act
        var cell = await PageGeometry.GetBoxAsync(GetGridCell(tile));
        var tileBox = await PageGeometry.GetBoxAsync(tile);

        // Assert
        Assert.InRange(Math.Abs(cell.Width - cell.Height), 0, 1.5);
        Assert.InRange(Math.Abs(cell.Width - tileBox.Width), 0, 2.5);
        Assert.InRange(Math.Abs(cell.Height - tileBox.Height), 0, 2.5);
    }

    [Theory]
    [InlineData(1280)]
    [InlineData(800)]
    [InlineData(375)]
    public async Task WhenCanvasFitsTheWidth_ThenItsNodesScaleWithTheWidthOfThePage(int width)
    {
        // Arrange
        var (_, frame, node) = await OpenScaledCanvasAsync(width, 900);

        // Act
        var frameBox = await PageGeometry.GetBoxAsync(frame);
        var nodeBox = await PageGeometry.GetBoxAsync(node);

        // Assert: the frame is as high as the 200 pixel MinHeight times the factor, which is half of its width.
        Assert.InRange(Math.Abs(nodeBox.Width - frameBox.Width / 2), 0, 2);
        Assert.InRange(Math.Abs(nodeBox.Height - frameBox.Width / 2), 0, 2);
        Assert.InRange(Math.Abs(frameBox.Height - frameBox.Width / 2), 0, 2);
    }

    [Theory]
    [InlineData(800)]
    [InlineData(375)]
    public async Task WhenNavigationDrawerClosesAfterTheCanvasWasScaled_ThenTheCanvasFitsTheWiderFrame(int width)
    {
        // Arrange: below the medium breakpoint the navigation drawer is hidden and the page takes its place, in a
        // transition that widens the frame after the canvas was scaled to it.
        var page = await OpenPageAsync(initScript: HoldTransitionsUntilCanvasIsScaledScript);
        var frame = GetCanvasFrame(page);
        var node = GetCanvasNode(page);
        await Assertions.Expect(node).ToBeVisibleAsync(new() { Timeout = Timeout });
        await WaitForScaledNodeAsync(page, frame, node);

        // Act
        await page.SetViewportSizeAsync(width, 900);
        await WaitForScaledNodeAsync(page, frame, node);

        // Assert
        var mismatch = await GetLargestMismatchOfScaledNodeAsync(page, frame, node);
        Assert.InRange(mismatch, 0, 2);
    }

    [Theory]
    [InlineData(1280)]
    [InlineData(375)]
    public async Task WhenTileIsTheOnlyContentOfACanvasNodeThatFitsTheWidth_ThenItFillsTheNode(int width)
    {
        // Arrange
        var (page, _, node) = await OpenScaledCanvasAsync(width, 900);
        var tile = node.GetByTestId("switch-tile");

        // Act: the node has a border of 1 pixel on each side, which is scaled with the canvas (the node is 200 pixels wide),
        // so the node is larger than the tile by that and no more. The page is still changing its width for a moment after
        // the viewport has, so the boxes must agree for 20 frames in a row (polled once per frame).
        await page.WaitForFunctionAsync(
            """
            ([node, tile]) => {
                const nodeBox = node.getBoundingClientRect();
                const tileBox = tile.getBoundingClientRect();
                const tolerance = 2 * nodeBox.width / 200 + 0.5;
                const fills = Math.abs(nodeBox.width - tileBox.width) <= tolerance && Math.abs(nodeBox.height - tileBox.height) <= tolerance;
                node.__fillFrames = fills ? (node.__fillFrames ?? 0) + 1 : 0;
                return node.__fillFrames >= 20;
            }
            """,
            new object[] { await node.ElementHandleAsync(), await tile.ElementHandleAsync() },
            new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenCanvasThatFitsTheWidthIsEdited_ThenItIsDrawnUnscaled()
    {
        // Arrange: the node is drawn at the factor of the 1008 pixel frame first, about 504 pixels wide.
        var (page, _, node) = await OpenScaledCanvasAsync(1280, 900);
        Assert.True((await PageGeometry.GetBoxAsync(node)).Width > 300);

        // Act
        await page.SelectEditModeAsync("Inline");

        // Assert
        await WaitForUnscaledNodeAsync(page, node);
    }

    [Fact]
    public async Task WhenCanvasThatFitsTheWidthIsEditedOnANarrowPage_ThenItScrollsHorizontally()
    {
        // Arrange: the node is drawn at the factor of the narrow frame first, below 200 pixels wide.
        var (page, frame, node) = await OpenScaledCanvasAsync(375, 812);
        Assert.True((await PageGeometry.GetBoxAsync(node)).Width < 190);

        // Act
        await page.SelectEditModeAsync("Inline");

        // Assert: the 400 pixel design width is wider than the frame, which scrolls instead of scaling the node.
        await WaitForUnscaledNodeAsync(page, node);
        var scrollLeft = await frame.EvaluateAsync<double>(
            "frame => getComputedStyle(frame).overflowX === 'auto' ? (frame.scrollLeft = 50, frame.scrollLeft) : -1");
        Assert.True(scrollLeft > 0, $"The frame did not scroll: {scrollLeft}");
    }

    [Fact]
    public async Task WhenCanvasThatFitsTheWidthIsViewedWhileThePageResizes_ThenThePageIsNotSaved()
    {
        // Arrange
        var path = _fixture.GetDemoPagePath(PageName);
        var (page, frame, node) = await OpenScaledCanvasAsync(1280, 900);
        var contentBefore = await _fixture.ReadFileAsync(path);
        var writeTimeBefore = File.GetLastWriteTimeUtc(path);

        // Act: every resize measures the frame, rescales the canvas and lets the diagram measure its nodes again.
        foreach (var width in new[] { 900, 1280, 700, 1280 })
        {
            await page.SetViewportSizeAsync(width, 900);
            await WaitForScaledNodeAsync(page, frame, node);
        }

        // Assert
        Assert.Equal(contentBefore, await _fixture.ReadFileAsync(path));
        Assert.Equal(writeTimeBefore, File.GetLastWriteTimeUtc(path));
    }

    // Opens the page at the size and waits until the canvas that fits the width is drawn at the factor of its frame.
    private async Task<(IPage Page, ILocator Frame, ILocator Node)> OpenScaledCanvasAsync(int width, int height)
    {
        var page = await OpenPageAsync(width, height);
        var frame = GetCanvasFrame(page);
        var node = GetCanvasNode(page);
        await Assertions.Expect(node).ToBeVisibleAsync(new() { Timeout = Timeout });
        await WaitForScaledNodeAsync(page, frame, node);
        return (page, frame, node);
    }

    private static ILocator GetCanvasFrame(IPage page) => page.GetByTestId("canvas-scale-frame").Last;

    private static ILocator GetCanvasNode(IPage page) =>
        page.Locator(".canvas-node").Filter(new() { Has = GetSwitchTile(page, "Canvas tile") });

    // The node is half of the 400 pixel design width, so it is drawn at half of the frame once the frame is measured. The
    // canvas is scaled to every width the frame is measured at, so while a transition still widens or narrows the frame (as
    // that of the margin the navigation drawer leaves when a resize crosses the medium breakpoint), a node that matches it
    // only matches a width that is about to change.
    private static async Task WaitForScaledNodeAsync(IPage page, ILocator frame, ILocator node) =>
        await page.WaitForFunctionAsync(
            """
            ([frame, node]) =>
                !document.getAnimations().some(animation => animation instanceof CSSTransition && animation.playState === 'running') &&
                Math.abs(node.getBoundingClientRect().width - frame.getBoundingClientRect().width / 2) < 2
            """,
            new object[] { await frame.ElementHandleAsync(), await node.ElementHandleAsync() },
            new() { Timeout = Timeout });

    // How far the node is from half of the frame at most, over the next 20 frames.
    private static async Task<double> GetLargestMismatchOfScaledNodeAsync(IPage page, ILocator frame, ILocator node) =>
        await page.EvaluateAsync<double>(
            """
            ([frame, node]) => new Promise(resolve => {
                let largest = 0;
                let count = 0;
                const measure = () => {
                    largest = Math.max(largest, Math.abs(node.getBoundingClientRect().width - frame.getBoundingClientRect().width / 2));
                    if (++count < 20) requestAnimationFrame(measure); else resolve(largest);
                };
                requestAnimationFrame(measure);
            })
            """,
            new object[] { await frame.ElementHandleAsync(), await node.ElementHandleAsync() });

    // Holds the transitions that a resize of the window starts, such as that of the margin the navigation drawer leaves
    // below the medium breakpoint, until the canvas that fits the width is drawn at the factor of its frame (its node is
    // half of the 400 pixel width), as a slow machine can draw it before the transition has moved. A playback rate of 0
    // keeps a transition running, which a paused one is not.
    private const string HoldTransitionsUntilCanvasIsScaledScript =
        """
        window.addEventListener('resize', () => {
            const transitions = document.getAnimations().filter(animation => animation instanceof CSSTransition);
            transitions.forEach(transition => transition.playbackRate = 0);
            const release = () => {
                const frame = [...document.querySelectorAll('[data-testid="canvas-scale-frame"]')].pop();
                const node = frame?.querySelector('.canvas-node');
                if (node && Math.abs(node.getBoundingClientRect().width - frame.getBoundingClientRect().width / 2) < 2) {
                    transitions.forEach(transition => transition.playbackRate = 1);
                } else {
                    requestAnimationFrame(release);
                }
            };
            requestAnimationFrame(release);
        });
        """;

    // Unscaled, the node is drawn at its own width of 200 pixels.
    private static async Task WaitForUnscaledNodeAsync(IPage page, ILocator node) =>
        await page.WaitForFunctionAsync(
            "node => Math.abs(node.getBoundingClientRect().width - 200) < 3",
            await node.ElementHandleAsync(),
            new() { Timeout = Timeout });
}
