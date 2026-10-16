using System.Text;
using System.Text.RegularExpressions;
using HomeBlaze.E2E.Tests.Infrastructure;
using Microsoft.Playwright;
using static HomeBlaze.E2E.Tests.Infrastructure.PageGeometry;

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

    private static readonly Regex FirstNodePositionPattern = new("^::: Node X=(-?\\d+) Y=(-?\\d+) ", RegexOptions.Multiline);

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
    public async Task WhenCanvasIsViewed_ThenASliderInANodeCanBeDragged()
    {
        // Arrange
        var page = await OpenCanvasPageAsync(withControlsWidget: true);
        var slider = GetNodes(page).First.Locator("input[type=range]");
        var box = await GetBoxAsync(slider);
        var y = box.Top + box.Height / 2;

        // Act: the thumb is picked up in the middle and dropped at four fifths of the slider.
        await DragAsync(page, box.Left + box.Width / 2, y, box.Left + box.Width * 0.8, y);

        // Assert
        await Assertions.Expect(GetNodes(page).First.GetByTestId("slider-value")).ToHaveTextAsync(new Regex("^Level: (7\\d|8\\d)$"), new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenCanvasIsViewed_ThenKeysWorkInAWidgetInANode()
    {
        // Arrange
        var page = await OpenCanvasPageAsync(withControlsWidget: true);
        var node = GetNodes(page).First;
        var textField = node.GetByRole(AriaRole.Textbox);
        await textField.ClickAsync();

        // Act: text goes into the field, Tab moves to the button and Enter presses it. The text is waited for before Tab, so that every key
        // has arrived at the server before the focus moves.
        await textField.PressSequentiallyAsync("abc");
        await Assertions.Expect(node.GetByTestId("text-value")).ToHaveTextAsync("Text: abc", new() { Timeout = Timeout });
        await page.Keyboard.PressAsync("Tab");
        await page.Keyboard.PressAsync("Enter");

        // Assert
        await Assertions.Expect(node.GetByTestId("click-count")).ToHaveTextAsync("Clicks: 1", new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenCanvasIsViewed_ThenAWidgetInANodeThatScrollsScrollsBeforeThePage()
    {
        // Arrange
        var page = await OpenCanvasPageAsync(withTextBelow: true, withControlsWidget: true);
        var scrollArea = GetNodes(page).First.GetByTestId("scroll-area");
        var box = await GetBoxAsync(scrollArea);
        var scrollBefore = await GetScrollPositionAsync(page);
        await page.Mouse.MoveAsync((float)(box.Left + 50), (float)(box.Top + 30));

        // Act: the first wheel is more than the area can scroll.
        await page.Mouse.WheelAsync(0, 2000);

        // Assert: the area scrolls to its end and the page stays.
        await page.WaitForFunctionAsync(
            "element => element.scrollTop + element.clientHeight >= element.scrollHeight - 1",
            await scrollArea.ElementHandleAsync(),
            new() { Timeout = Timeout });
        Assert.Equal(scrollBefore, await GetScrollPositionAsync(page));

        // Act: the area cannot scroll any more.
        await page.Mouse.WheelAsync(0, 300);

        // Assert
        await page.WaitForFunctionAsync(
            $$"""
            ([selector, before]) => ({{ScrollPositionScript}})(selector) > before
            """,
            new object[] { ".canvas-layout-container", scrollBefore },
            new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenCanvasIsViewed_ThenDraggingFromTheContentOfANodeMovesNothing()
    {
        // Arrange
        var page = await OpenCanvasPageAsync(withOperableWidgets: true);
        var nodes = GetNodes(page);
        var positionsBefore = await GetNodePositionsAsync(page);
        var nodeBox = await GetBoxAsync(nodes.Last);
        var indicatorBox = await GetBoxAsync(nodes.Last.GetByTestId("indicator"));

        // Act: the drag starts on the indicator and ends on the empty canvas below the node.
        await DragAsync(
            page,
            indicatorBox.Left + indicatorBox.Width / 2,
            indicatorBox.Top + indicatorBox.Height / 2,
            nodeBox.Left + 40,
            nodeBox.Bottom + 60);
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

        // Assert: the click selects the widget of the node. Its actions are the round trip after which the page would have navigated.
        await Assertions.Expect(page.GetWidgetActionBar()).ToBeVisibleAsync(new() { Timeout = Timeout });
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
    public async Task WhenAPanOfTheCanvasEndedOutsideOfIt_ThenALaterClickOnTheEmptyCanvasOffersToAddANode()
    {
        // Arrange: the release is outside of the container, so no click reaches it and the pan is never followed by one.
        var page = await OpenCanvasPageAsync(editMode: "Split");
        var boxBefore = await GetBoxInCanvasAsync(GetNodes(page).First);
        var canvasBox = await GetBoxAsync(GetCanvas(page));
        var x = canvasBox.Right - 40;
        var y = canvasBox.Bottom - 40;
        await page.Mouse.MoveAsync((float)x, (float)y);
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync((float)(x - 150), (float)(y - 100), new() { Steps = 10 });
        await page.Mouse.MoveAsync((float)(canvasBox.Right + 100), (float)(y - 100), new() { Steps = 5 });
        await page.Mouse.UpAsync();
        await WaitForNodeToMoveAsync(page, boxBefore);
        await WaitForServerAsync(page, "Split");
        await Assertions.Expect(page.Locator(".mud-dialog")).ToHaveCountAsync(0);

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

        // A click on the node selects the widget in it, which shows the buttons of the nodes.
        await node.SelectWidgetAsync();

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

    private async Task<IPage> OpenCanvasPageAsync(bool withTextBelow = false, string? editMode = null, bool withOperableWidgets = false, bool withControlsWidget = false)
    {
        await _fixture.WriteDemoPageAsync(ScratchPage, CreateCanvasPage(withTextBelow, withOperableWidgets, withControlsWidget));

        var page = await _fixture.CreatePageAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = ViewportWidth, Height = ViewportHeight },
        });
        await page.GotoAsync($"{_fixture.ServerAddress}pages/Demo/{ScratchPage}");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        await page.WaitUntilInteractiveAsync(Timeout);

        if (editMode is not null)
        {
            await Assertions.Expect(page.GetByTestId("edit-mode-menu")).ToBeVisibleAsync(new() { Timeout = Timeout });
            await SelectModeAsync(page, editMode);
            await Assertions.Expect(page.Locator(".canvas-layout-container.editing")).ToBeVisibleAsync(new() { Timeout = Timeout });
        }

        // The library draws the border of a node and the area that a link starts from only after it has measured the node.
        var nodeCount = withControlsWidget ? 1 : 2;
        await Assertions.Expect(GetCanvas(page).Locator("svg rect")).ToHaveCountAsync(2 * nodeCount, new() { Timeout = Timeout });
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
        await _fixture.WaitForDemoPageTextAsync(
            ScratchPage,
            text =>
            {
                var match = FirstNodePositionPattern.Match(text);
                return match.Success
                    && double.Parse(match.Groups[1].Value) == position[0]
                    && double.Parse(match.Groups[2].Value) == position[1];
            },
            $"The page was not written with the new position {position[0]}, {position[1]} of the node");
    }

    private static ILocator GetCanvas(IPage page) => page.Locator(".canvas-layout-container");

    private static ILocator GetNodes(IPage page) => GetCanvas(page).Locator(".canvas-node");

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

    private static string CreateCanvasPage(bool withTextBelow, bool withOperableWidgets, bool withControlsWidget)
    {
        var builder = new StringBuilder();
        builder.Append("---\ntitle: Scratch\n---\n\n# Scratch\n\n");
        builder.Append($":: HomeBlaze.Components.CanvasLayout#canvas MinHeight={CanvasHeight}\n");
        if (withControlsWidget)
        {
            // One node with a widget that has a slider, a text field, a button and an area that scrolls.
            builder.Append("::: Node X=40 Y=10 Width=420 Height=380\n");
            builder.Append(":::: HomeBlaze.E2E.Tests.Models.ControlsTestSubject\n");
        }
        else if (withOperableWidgets)
        {
            // A card whose title is a link, and an icon indicator whose path does not exist, so that its tooltip is the path.
            builder.Append("::: Node X=40 Y=40 Width=300 Height=200\n");
            builder.Append(":::: HomeBlaze.Components.Card Title=\"Linked Card\" Href=/pages/Demo/Blocks.md\n");
            builder.Append("::: Node X=420 Y=40 Width=300 Height=200\n");
            builder.Append(":::: HomeBlaze.Components.Indicator Path=../Motor/Missing Kind=Icon\n");
        }
        else
        {
            builder.Append("::: Node X=40 Y=40 Width=300 Height=200\n");
            builder.Append(":::: HomeBlaze.Components.Widget Path=../Motor\n");
            builder.Append("::: Node X=420 Y=40 Width=300 Height=200\n");
            builder.Append(":::: HomeBlaze.Components.Widget Path=../Motor\n");
        }

        builder.Append("::\n\n");

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
