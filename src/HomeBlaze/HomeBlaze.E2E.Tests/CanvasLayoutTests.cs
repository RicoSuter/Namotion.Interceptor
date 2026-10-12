using System.Text;
using System.Text.RegularExpressions;
using HomeBlaze.E2E.Tests.Infrastructure;
using Microsoft.Playwright;
using Namotion.Interceptor.Testing;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for what a canvas layout lets a user do with the mouse, while a page is viewed and while it is edited.
/// </summary>
/// <remarks>
/// The page runs on Blazor Server, so what a mouse gesture causes on the canvas arrives only after a round trip. A test
/// that asserts that nothing happened therefore first waits for something that can only be there after a later round
/// trip: the events and the render batches of one circuit are ordered, so the result of the later one proves that
/// everything the gesture caused has arrived.
/// </remarks>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class CanvasLayoutTests
{
    private const int ViewportWidth = 1280;
    private const int ViewportHeight = 720;
    private const int CanvasHeight = 400;
    private const int Timeout = 30000;

    // The page that tests overwrite. Each of them writes its own content before it opens the page.
    private const string ScratchPage = "Scratch.md";

    private static readonly Regex FirstNodePositionPattern = new("\"X\": (-?\\d+),\\s*\"Y\": (-?\\d+),");

    private readonly PlaywrightFixture _fixture;

    public CanvasLayoutTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task WhenCanvasIsViewed_ThenDraggingFromANodeBorderDrawsNoLink()
    {
        // Arrange
        var page = await OpenCanvasPageAsync();
        var box = await GetBoxAsync(GetNodes(page).First);

        // Act
        await DragAsync(page, box.Left, box.Top + box.Height / 2, box.Left + box.Width / 2, box.Bottom + 60);
        await WaitForServerAsync(page);

        // Assert
        await Assertions.Expect(GetCanvas(page).Locator("line")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task WhenCanvasIsViewed_ThenDraggingTheCanvasMovesNothing()
    {
        // Arrange
        var page = await OpenCanvasPageAsync();
        var node = GetNodes(page).First;
        var boxBefore = await GetBoxInCanvasAsync(node);
        var canvasBox = await GetBoxAsync(GetCanvas(page));
        var startX = canvasBox.Right - 40;
        var startY = canvasBox.Bottom - 40;

        // Act
        await DragAsync(page, startX, startY, startX - 150, startY - 100);
        await WaitForServerAsync(page);

        // Assert
        var boxAfter = await GetBoxInCanvasAsync(node);
        Assert.Equal(boxBefore, boxAfter);
    }

    [Fact]
    public async Task WhenCanvasIsViewed_ThenTheCursorOverTheEmptyCanvasIsNotTheGrabHand()
    {
        // Arrange
        var page = await OpenCanvasPageAsync();
        var canvasBox = await GetBoxAsync(GetCanvas(page));
        var x = canvasBox.Right - 40;
        var y = canvasBox.Bottom - 40;

        // Act
        await page.Mouse.MoveAsync((float)x, (float)y);

        // Assert
        var cursor = await page.EvaluateAsync<string>(
            "([x, y]) => getComputedStyle(document.elementFromPoint(x, y)).cursor",
            new[] { x, y });
        Assert.NotEqual("grab", cursor);
    }

    [Fact]
    public async Task WhenCanvasIsViewed_ThenTheWheelScrollsThePageAndDoesNotZoom()
    {
        // Arrange, Act & Assert
        await AssertWheelScrollsPageAsync(canvasBox => (canvasBox.Right - 40, canvasBox.Bottom - 40));
    }

    [Fact]
    public async Task WhenCanvasIsViewed_ThenTheWheelOverANodeScrollsThePageAndDoesNotZoom()
    {
        // Arrange
        var page = await OpenCanvasPageAsync(withTextBelow: true);
        var nodeBox = await GetBoxAsync(GetNodes(page).First);

        // Act & Assert
        await AssertWheelScrollsPageAsync(page, nodeBox.Left + nodeBox.Width / 2, nodeBox.Top + nodeBox.Height / 2);
    }

    [Fact]
    public async Task WhenCanvasIsViewed_ThenAWidgetInANodeCanBeOperated()
    {
        // Arrange
        var page = await OpenCanvasPageAsync(withOperableWidgets: true);
        var title = GetNodes(page).First.GetByTestId("card-title");

        // Act
        await title.ClickAsync();

        // Assert
        await page.WaitForURLAsync(new Regex("/pages/Demo/Blocks\\.md$"), new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenCanvasIsViewed_ThenHoveringAWidgetInANodeShowsItsTooltip()
    {
        // Arrange
        var page = await OpenCanvasPageAsync(withOperableWidgets: true);
        var indicator = GetNodes(page).Last.GetByTestId("indicator");

        // Act
        await indicator.HoverAsync();

        // Assert
        await Assertions.Expect(page.Locator(".mud-tooltip.mud-popover-open")).ToHaveTextAsync("../Motor/Missing", new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenCanvasIsViewed_ThenAWidgetInANodeThatScrollsScrollsBeforeThePage()
    {
        // Arrange: a box with a scroll bar of its own in the first node, which a widget with a long list would have.
        var page = await OpenCanvasPageAsync(withTextBelow: true, withOperableWidgets: true);
        var node = GetNodes(page).First;
        await node.EvaluateAsync(
            """
            node => {
                const box = document.createElement('div');
                box.dataset.testid = 'scroll-box';
                box.style.cssText = 'position: absolute; left: 0; top: 0; width: 100%; height: 100px; overflow: auto; background: white';
                box.innerHTML = '<div style="height: 1000px">Long content</div>';
                node.appendChild(box);
            }
            """);
        var scrollBox = node.GetByTestId("scroll-box");
        var box = await GetBoxAsync(scrollBox);
        var scrollBefore = await GetScrollPositionAsync(page);
        await page.Mouse.MoveAsync((float)(box.Left + 50), (float)(box.Top + 50));

        // Act
        await page.Mouse.WheelAsync(0, 100);

        // Assert
        await page.WaitForFunctionAsync(
            "element => element.scrollTop > 0",
            await scrollBox.ElementHandleAsync(),
            new() { Timeout = Timeout });
        Assert.Equal(scrollBefore, await GetScrollPositionAsync(page));
    }

    [Fact]
    public async Task WhenCanvasIsViewed_ThenDraggingFromTheContentOfANodeMovesNothing()
    {
        // Arrange
        var page = await OpenCanvasPageAsync(withOperableWidgets: true);
        var nodes = GetNodes(page);
        var positionsBefore = await GetNodePositionsAsync(page);
        var box = await GetBoxAsync(nodes.Last);

        // Act: the drag starts on the indicator and ends on the empty canvas.
        await DragAsync(page, box.Left + 100, box.Top + 100, box.Left + 100 - 60, box.Bottom + 60);
        await WaitForServerAsync(page);

        // Assert
        Assert.Equal(positionsBefore, await GetNodePositionsAsync(page));
        await Assertions.Expect(GetCanvas(page).Locator("line")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task WhenCanvasIsEdited_ThenAWidgetInANodeCannotBeOperated()
    {
        // Arrange
        var page = await OpenCanvasPageAsync(editMode: "Split", withOperableWidgets: true);
        var titleBox = await GetBoxAsync(GetNodes(page).First.GetByTestId("card-title"));
        var urlBefore = page.Url;

        // Act: a click on the title, which would follow its link while the page is viewed.
        await page.Mouse.ClickAsync((float)(titleBox.Left + 5), (float)(titleBox.Top + titleBox.Height / 2));

        // Assert: the click is the click on a node, which offers to add a node at that place. Closing the dialog is the round trip
        // after which the page would have navigated.
        await page.GetByTestId("cancel-button").ClickAsync();
        Assert.Equal(urlBefore, page.Url);
    }

    [Fact]
    public async Task WhenCanvasIsEdited_ThenDraggingFromANodeBorderDrawsNoLink()
    {
        // Arrange
        var page = await OpenCanvasPageAsync(editMode: "Split");
        var node = GetNodes(page).First;
        var positionsBefore = await GetNodePositionsAsync(page);
        var box = await GetBoxAsync(node);

        // Act: the border moves the node like the rest of it does, so a moved node is what the drag causes.
        await DragAsync(page, box.Left, box.Top + box.Height / 2, box.Left + box.Width / 2, box.Bottom + 60);
        await WaitForServerAsync(page, "Split");
        var movedPosition = await WaitForNodeToBeMovedAsync(page, positionsBefore);
        await WaitForPositionToBeWrittenAsync(movedPosition);

        // Assert
        await Assertions.Expect(GetCanvas(page).Locator("line")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task WhenCanvasIsEdited_ThenANodeCanStillBeMoved()
    {
        // Arrange
        var page = await OpenCanvasPageAsync(editMode: "Split");
        var node = GetNodes(page).First;
        var positionsBefore = await GetNodePositionsAsync(page);
        var box = await GetBoxAsync(node);

        // Act
        await DragAsync(
            page,
            box.Left + box.Width / 2,
            box.Top + box.Height / 2,
            box.Left + box.Width / 2 + 80,
            box.Top + box.Height / 2 + 60);

        // Assert: the move writes the page, and the next test writes the same page, so this test ends with the write.
        await WaitForServerAsync(page, "Split");
        var movedPosition = await WaitForNodeToBeMovedAsync(page, positionsBefore);
        await WaitForPositionToBeWrittenAsync(movedPosition);
    }

    [Fact]
    public async Task WhenCanvasIsEdited_ThenTheCanvasCanStillBePanned()
    {
        // Arrange
        var page = await OpenCanvasPageAsync(editMode: "Split");
        var boxBefore = await GetBoxInCanvasAsync(GetNodes(page).First);
        var canvasBox = await GetBoxAsync(GetCanvas(page));
        var startX = canvasBox.Right - 40;
        var startY = canvasBox.Bottom - 40;

        // Act
        await DragAsync(page, startX, startY, startX - 150, startY - 100);

        // Assert
        await WaitForNodeToMoveAsync(page, boxBefore);
    }

    [Fact]
    public async Task WhenCanvasIsEdited_ThenTheWheelStillZoomsTheCanvas()
    {
        // Arrange
        var page = await OpenCanvasPageAsync(editMode: "Split");
        var widthBefore = (await GetBoxAsync(GetNodes(page).First)).Width;
        var canvasBox = await GetBoxAsync(GetCanvas(page));
        await page.Mouse.MoveAsync((float)(canvasBox.Right - 40), (float)(canvasBox.Bottom - 40));

        // Act
        await page.Mouse.WheelAsync(0, -300);

        // Assert
        await WaitForNodeWidthToChangeAsync(page, widthBefore);
    }

    [Fact]
    public async Task WhenCanvasIsEdited_ThenAClickOnTheEmptyCanvasOffersToAddANode()
    {
        // Arrange
        var page = await OpenCanvasPageAsync(editMode: "Split");
        var canvasBox = await GetBoxAsync(GetCanvas(page));

        // Act
        await page.Mouse.ClickAsync((float)(canvasBox.Right - 40), (float)(canvasBox.Bottom - 40));

        // Assert: the dialog to add a node is open, and closing it creates nothing.
        var dialog = page.GetByTestId("create-subject-dialog");
        await Assertions.Expect(dialog).ToBeVisibleAsync(new() { Timeout = Timeout });
        await page.GetByTestId("cancel-button").ClickAsync();
        await Assertions.Expect(dialog).ToHaveCountAsync(0, new() { Timeout = Timeout });
        await Assertions.Expect(GetNodes(page)).ToHaveCountAsync(2);
    }

    [Fact]
    public async Task WhenCanvasIsPannedInEditMode_ThenNoDialogOpens()
    {
        // Arrange
        var page = await OpenCanvasPageAsync(editMode: "Split");
        var boxBefore = await GetBoxInCanvasAsync(GetNodes(page).First);
        var canvasBox = await GetBoxAsync(GetCanvas(page));
        var startX = canvasBox.Right - 40;
        var startY = canvasBox.Bottom - 40;

        // Act: the release of the drag counts as a click on the empty canvas.
        await DragAsync(page, startX, startY, startX - 150, startY - 100);
        await WaitForNodeToMoveAsync(page, boxBefore);
        await WaitForServerAsync(page, "Split");

        // Assert
        await Assertions.Expect(page.Locator(".mud-dialog")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task WhenCanvasWasPannedInEditMode_ThenALaterClickOnTheEmptyCanvasOffersToAddANode()
    {
        // Arrange
        var page = await OpenCanvasPageAsync(editMode: "Split");
        var boxBefore = await GetBoxInCanvasAsync(GetNodes(page).First);
        var canvasBox = await GetBoxAsync(GetCanvas(page));
        var x = canvasBox.Right - 40;
        var y = canvasBox.Bottom - 40;
        await DragAsync(page, x, y, x - 150, y - 100);
        await WaitForNodeToMoveAsync(page, boxBefore);
        await WaitForServerAsync(page, "Split");

        // Act
        await page.Mouse.ClickAsync((float)x, (float)y);

        // Assert
        await Assertions.Expect(page.GetByTestId("create-subject-dialog")).ToBeVisibleAsync(new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenCanvasIsEdited_ThenTheButtonsOfANodeStillWork()
    {
        // Arrange
        var page = await OpenCanvasPageAsync(editMode: "Split");
        var node = GetNodes(page).First;

        // Act
        await node.GetByTitle("Edit Node").ClickAsync();

        // Assert: the dialog of the node is open, and closing it changes nothing.
        var dialog = page.Locator(".mud-dialog");
        await Assertions.Expect(dialog).ToBeVisibleAsync(new() { Timeout = Timeout });
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Cancel" }).ClickAsync();
        await Assertions.Expect(dialog).ToHaveCountAsync(0, new() { Timeout = Timeout });
        await Assertions.Expect(GetNodes(page)).ToHaveCountAsync(2);
    }

    [Fact]
    public async Task WhenCanvasGoesFromViewToEdit_ThenItCanBeZoomedAndPanned()
    {
        // Arrange: the inline mode keeps the widget of the page, so that this is the diagram that was read-only.
        var page = await OpenCanvasPageAsync();
        await MarkCanvasAsync(page);
        await SelectModeAsync(page, "Inline");
        await Assertions.Expect(page.Locator(".canvas-layout-container.editing")).ToBeVisibleAsync(new() { Timeout = Timeout });
        Assert.True(await IsCanvasMarkedAsync(page), "The canvas was built again, so the test would not show that the diagram of the viewed page is changed");

        var node = GetNodes(page).First;
        var boxBefore = await GetBoxInCanvasAsync(node);
        var widthBefore = (await GetBoxAsync(node)).Width;
        var canvasBox = await GetBoxAsync(GetCanvas(page));
        var x = canvasBox.Right - 40;
        var y = canvasBox.Bottom - 40;

        // Act & Assert
        await DragAsync(page, x, y, x - 150, y - 100);
        await WaitForNodeToMoveAsync(page, boxBefore);

        await page.Mouse.MoveAsync((float)x, (float)y);
        await page.Mouse.WheelAsync(0, -300);
        await WaitForNodeWidthToChangeAsync(page, widthBefore);
    }

    [Fact]
    public async Task WhenCanvasGoesFromEditBackToView_ThenItIsReadOnlyAgain()
    {
        // Arrange: the inline mode keeps the widget of the page, so that this is the diagram that could be edited.
        var page = await OpenCanvasPageAsync(withTextBelow: true, editMode: "Inline");
        await MarkCanvasAsync(page);
        await SelectModeAsync(page, "View");
        await Assertions.Expect(page.Locator(".canvas-layout-container:not(.editing)")).ToBeVisibleAsync(new() { Timeout = Timeout });
        Assert.True(await IsCanvasMarkedAsync(page), "The canvas was built again, so the test would not show that the diagram that was edited is read-only");

        var node = GetNodes(page).First;
        var boxBefore = await GetBoxInCanvasAsync(node);
        var box = await GetBoxAsync(node);
        var canvasBox = await GetBoxAsync(GetCanvas(page));

        // Act
        await DragAsync(page, box.Left, box.Top + box.Height / 2, box.Left + box.Width / 2, box.Bottom + 60);
        await DragAsync(page, canvasBox.Right - 40, canvasBox.Bottom - 40, canvasBox.Right - 190, canvasBox.Bottom - 140);
        await WaitForServerAsync(page);

        // Assert
        await Assertions.Expect(GetCanvas(page).Locator("line")).ToHaveCountAsync(0);
        Assert.Equal(boxBefore, await GetBoxInCanvasAsync(node));

        await AssertWheelScrollsPageAsync(page, canvasBox.Right - 40, canvasBox.Bottom - 40);
    }

    private async Task AssertWheelScrollsPageAsync(Func<Box, (double X, double Y)> getTarget)
    {
        var page = await OpenCanvasPageAsync(withTextBelow: true);
        var canvasBox = await GetBoxAsync(GetCanvas(page));
        var target = getTarget(canvasBox);

        await AssertWheelScrollsPageAsync(page, target.X, target.Y);
    }

    private static async Task AssertWheelScrollsPageAsync(IPage page, double x, double y)
    {
        // Arrange
        var widthBefore = (await GetBoxAsync(GetNodes(page).First)).Width;
        var scrollBefore = await GetScrollPositionAsync(page);
        await page.Mouse.MoveAsync((float)x, (float)y);

        // Act
        await page.Mouse.WheelAsync(0, 300);

        // Assert: the page scrolls, which the library would prevent if the wheel reached the diagram.
        await page.WaitForFunctionAsync(
            $$"""
            ([selector, before]) => ({{ScrollPositionScript}})(selector) > before
            """,
            new object[] { ".canvas-layout-container", scrollBefore },
            new() { Timeout = Timeout });

        Assert.Equal(widthBefore, (await GetBoxAsync(GetNodes(page).First)).Width);
    }

    private async Task<IPage> OpenCanvasPageAsync(bool withTextBelow = false, string? editMode = null, bool withOperableWidgets = false)
    {
        await _fixture.WriteDemoPageAsync(ScratchPage, CreateCanvasPage(withTextBelow, withOperableWidgets));

        var page = await _fixture.CreatePageAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = ViewportWidth, Height = ViewportHeight },
        });
        await page.GotoAsync($"{_fixture.ServerAddress}pages/Demo/{ScratchPage}");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        if (editMode is not null)
        {
            await Assertions.Expect(page.GetByTestId("edit-mode-menu")).ToBeVisibleAsync(new() { Timeout = Timeout });
            await SelectModeAsync(page, editMode);
            await Assertions.Expect(page.Locator(".canvas-layout-container.editing")).ToBeVisibleAsync(new() { Timeout = Timeout });
        }

        // The library draws the border of a node and the area that a link starts from only after it has measured the node.
        await Assertions.Expect(GetCanvas(page).Locator("svg rect")).ToHaveCountAsync(4, new() { Timeout = Timeout });
        await GetNodes(page).First.ScrollIntoViewIfNeededAsync();
        await Assertions.Expect(GetNodes(page).First).ToBeVisibleAsync();
        return page;
    }

    private static async Task SelectModeAsync(IPage page, string mode)
    {
        await page.GetByTestId("edit-mode-menu").ClickAsync();
        await page.Locator(".mud-popover-open").GetByText(mode, new() { Exact = true }).ClickAsync();
    }

    // Opens the menu of the edit modes and closes it with the mode that is already selected. Each step is a round
    // trip with a visible result, and the second one comes after every event that the mouse caused before.
    private static async Task WaitForServerAsync(IPage page, string currentMode = "View")
    {
        await SelectModeAsync(page, currentMode);
        await Assertions.Expect(page.Locator(".mud-popover-open")).ToHaveCountAsync(0, new() { Timeout = Timeout });
    }

    // A data attribute that the page does not know stays only as long as the element, and Blazor keeps the element of a
    // component that is rendered again and replaces the one of a component that is built again.
    private static Task MarkCanvasAsync(IPage page)
    {
        return page.EvaluateAsync("() => { document.querySelector('.canvas-layout-container').dataset.mark = 'same'; }");
    }

    private static Task<bool> IsCanvasMarkedAsync(IPage page)
    {
        return page.EvaluateAsync<bool>("() => document.querySelector('.canvas-layout-container').dataset.mark === 'same'");
    }

    private static Task WaitForNodeToMoveAsync(IPage page, Box boxBefore)
    {
        return page.WaitForFunctionAsync(
            """
            ([left, top]) => {
                const node = document.querySelector('.canvas-layout-container .canvas-node');
                const canvas = node.closest('.canvas-layout-container').getBoundingClientRect();
                const rectangle = node.getBoundingClientRect();
                return Math.abs(rectangle.left - canvas.left - left) > 20 && Math.abs(rectangle.top - canvas.top - top) > 20;
            }
            """,
            new object[] { boxBefore.Left, boxBefore.Top },
            new() { Timeout = Timeout });
    }

    // The positions of all nodes, relative to the canvas.
    private static Task<double[][]> GetNodePositionsAsync(IPage page)
    {
        return GetNodes(page).EvaluateAllAsync<double[][]>(
            """
            nodes => nodes.map(node => {
                const canvas = node.closest('.canvas-layout-container').getBoundingClientRect();
                const rectangle = node.getBoundingClientRect();
                return [rectangle.left - canvas.left, rectangle.top - canvas.top];
            })
            """);
    }

    // The diagram puts the node that was moved last in the page and builds it again when the page is written, so the first node
    // of the page is another one afterwards and no handle of an element stays. The moved node is the one that is away from
    // every position that a node had before. Returns the position that the page gives it, which is the one of the node itself.
    private static async Task<double[]> WaitForNodeToBeMovedAsync(IPage page, double[][] positionsBefore)
    {
        const string FindMovedNode =
            """
            positionsBefore => {
                const nodes = [...document.querySelectorAll('.canvas-layout-container .canvas-node')];
                const moved = nodes.find(node => {
                    const canvas = node.closest('.canvas-layout-container').getBoundingClientRect();
                    const rectangle = node.getBoundingClientRect();
                    return positionsBefore.every(position =>
                        Math.abs(rectangle.left - canvas.left - position[0]) > 20 || Math.abs(rectangle.top - canvas.top - position[1]) > 20);
                });

                return moved ? [parseFloat(moved.parentElement.style.left), parseFloat(moved.parentElement.style.top)] : null;
            }
            """;

        var position = await page.WaitForFunctionAsync(FindMovedNode, positionsBefore, new() { Timeout = Timeout });
        return await position.JsonValueAsync<double[]>();
    }

    private static Task WaitForNodeWidthToChangeAsync(IPage page, double widthBefore)
    {
        return page.WaitForFunctionAsync(
            "width => Math.abs(document.querySelector('.canvas-layout-container .canvas-node').getBoundingClientRect().width - width) > 5",
            widthBefore,
            new() { Timeout = Timeout });
    }

    // The move of a node writes the page. The position that the diagram shows is the one of the node, so it is the
    // one that the write has when it is done. It is the final one only after the events of the drag have arrived.
    private async Task WaitForPositionToBeWrittenAsync(double[] position)
    {
        var path = _fixture.GetDemoPagePath(ScratchPage);

        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var match = FirstNodePositionPattern.Match(_fixture.ReadFileAsync(path).GetAwaiter().GetResult());
                return match.Success
                    && double.Parse(match.Groups[1].Value) == position[0]
                    && double.Parse(match.Groups[2].Value) == position[1];
            },
            message: $"The page was not written with the new position {position[0]}, {position[1]} of the node");
    }

    private static ILocator GetCanvas(IPage page) => page.Locator(".canvas-layout-container");

    private static ILocator GetNodes(IPage page) => GetCanvas(page).Locator(".canvas-node");

    private static async Task DragAsync(IPage page, double fromX, double fromY, double toX, double toY)
    {
        await page.Mouse.MoveAsync((float)fromX, (float)fromY);
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync((float)toX, (float)toY, new() { Steps = 10 });
        await page.Mouse.UpAsync();
    }

    private sealed record Box(double Left, double Top, double Width, double Height)
    {
        public double Right => Left + Width;

        public double Bottom => Top + Height;
    }

    // Left, top, width and height come from one call, so that a layout that is still moving cannot give values of different moments.
    private static async Task<Box> GetBoxAsync(ILocator locator)
    {
        var values = await locator.EvaluateAsync<double[]>(
            """
            element => {
                const rectangle = element.getBoundingClientRect();
                return [rectangle.left, rectangle.top, rectangle.width, rectangle.height];
            }
            """);

        return new Box(values[0], values[1], values[2], values[3]);
    }

    // Relative to the canvas, so that a page that scrolls does not count as a move.
    private static async Task<Box> GetBoxInCanvasAsync(ILocator locator)
    {
        var values = await locator.EvaluateAsync<double[]>(
            """
            element => {
                const canvas = element.closest('.canvas-layout-container').getBoundingClientRect();
                const rectangle = element.getBoundingClientRect();
                return [rectangle.left - canvas.left, rectangle.top - canvas.top, rectangle.width, rectangle.height];
            }
            """);

        return new Box(values[0], values[1], values[2], values[3]);
    }

    // A function of a selector that returns how far the nearest scrollable ancestor is scrolled.
    private const string ScrollPositionScript =
        """
        selector => {
            for (let current = document.querySelector(selector); current; current = current.parentElement) {
                const style = getComputedStyle(current);
                if ((style.overflowY === 'auto' || style.overflowY === 'scroll') && current.scrollHeight > current.clientHeight) {
                    return current.scrollTop;
                }
            }

            return document.scrollingElement.scrollTop;
        }
        """;

    private static Task<double> GetScrollPositionAsync(IPage page)
    {
        return page.EvaluateAsync<double>($"() => ({ScrollPositionScript})('.canvas-layout-container')");
    }

    private static string CreateCanvasPage(bool withTextBelow, bool withOperableWidgets)
    {
        var builder = new StringBuilder();
        builder.Append("---\ntitle: Scratch\n---\n\n# Scratch\n\n");
        builder.Append($":: HomeBlaze.Components.CanvasLayout#canvas MinHeight={CanvasHeight}\n");
        builder.Append("```json\n");
        builder.Append("{\n  \"Nodes\": [\n");
        if (withOperableWidgets)
        {
            // A card whose title is a link, and an icon indicator whose path does not exist, so that its tooltip is the path.
            builder.Append("    { \"X\": 40, \"Y\": 40, \"Width\": 300, \"Height\": 200, \"Child\": { \"$type\": \"HomeBlaze.Components.Card\", \"Title\": \"Linked Card\", \"Href\": \"/pages/Demo/Blocks.md\" } },\n");
            builder.Append("    { \"X\": 420, \"Y\": 40, \"Width\": 300, \"Height\": 200, \"Child\": { \"$type\": \"HomeBlaze.Components.Indicator\", \"Path\": \"../Motor/Missing\", \"Kind\": \"Icon\" } }\n");
        }
        else
        {
            builder.Append("    { \"X\": 40, \"Y\": 40, \"Width\": 300, \"Height\": 200, \"Child\": { \"$type\": \"HomeBlaze.Components.Widget\", \"Path\": \"../Motor\" } },\n");
            builder.Append("    { \"X\": 420, \"Y\": 40, \"Width\": 300, \"Height\": 200, \"Child\": { \"$type\": \"HomeBlaze.Components.Widget\", \"Path\": \"../Motor\" } }\n");
        }

        builder.Append("  ]\n}\n");
        builder.Append("```\n::\n\n");

        if (withTextBelow)
        {
            for (var index = 1; index <= 60; index++)
            {
                builder.Append($"Paragraph {index} below the canvas, which makes the page taller than the window.\n\n");
            }
        }

        return builder.ToString();
    }
}
