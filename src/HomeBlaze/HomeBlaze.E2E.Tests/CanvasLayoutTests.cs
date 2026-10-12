using System.Text;
using HomeBlaze.E2E.Tests.Infrastructure;
using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for what a canvas layout lets a user do with the mouse, while a page is viewed and while it is edited.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class CanvasLayoutTests
{
    private const int ViewportWidth = 1280;
    private const int ViewportHeight = 720;
    private const int CanvasHeight = 400;

    // The page that tests overwrite. Each of them writes its own content before it opens the page.
    private const string ScratchPage = "Scratch.md";

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
        var node = GetNodes(page).First;
        var box = await GetBoxAsync(node);

        // Act
        await DragAsync(page, box.Left, box.Top + box.Height / 2, box.Left + box.Width / 2, box.Bottom + 60);

        // Assert
        await Assertions.Expect(GetCanvas(page).Locator("line")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task WhenCanvasIsViewed_ThenDraggingTheCanvasMovesNothing()
    {
        // Arrange
        var page = await OpenCanvasPageAsync();
        var node = GetNodes(page).First;
        var boxBefore = await GetBoxAsync(node);
        var canvasBox = await GetBoxAsync(GetCanvas(page));
        var startX = canvasBox.Right - 40;
        var startY = canvasBox.Bottom - 40;

        // Act
        await DragAsync(page, startX, startY, startX - 150, startY - 100);

        // Assert
        var boxAfter = await GetBoxAsync(node);
        Assert.Equal(boxBefore, boxAfter);
    }

    [Fact]
    public async Task WhenCanvasIsViewed_ThenTheCursorOverTheEmptyCanvasIsNotTheGrabHand()
    {
        // Arrange
        var page = await OpenCanvasPageAsync();
        var canvasBox = await GetBoxAsync(GetCanvas(page));

        // Act
        await page.Mouse.MoveAsync((float)(canvasBox.Right - 40), (float)(canvasBox.Bottom - 40));

        // Assert
        var cursor = await page.EvaluateAsync<string>(
            """
            ([x, y]) => getComputedStyle(document.elementFromPoint(x, y)).cursor
            """,
            new[] { canvasBox.Right - 40, canvasBox.Bottom - 40 });
        Assert.NotEqual("grab", cursor);
    }

    [Fact]
    public async Task WhenCanvasIsViewed_ThenTheWheelScrollsThePageAndDoesNotZoom()
    {
        // Arrange
        var page = await OpenCanvasPageAsync(withTextBelow: true);
        var node = GetNodes(page).First;
        var boxBefore = await GetBoxAsync(node);
        var canvasBox = await GetBoxAsync(GetCanvas(page));
        var scrollBefore = await GetScrollPositionAsync(page);
        await page.Mouse.MoveAsync((float)(canvasBox.Right - 40), (float)(canvasBox.Bottom - 40));

        // Act
        await page.Mouse.WheelAsync(0, 300);

        // Assert
        await page.WaitForFunctionAsync(
            """
            ([selector, before]) => {
                const element = document.querySelector(selector);
                for (let current = element; current; current = current.parentElement) {
                    const style = getComputedStyle(current);
                    if ((style.overflowY === 'auto' || style.overflowY === 'scroll') && current.scrollHeight > current.clientHeight) {
                        return current.scrollTop > before;
                    }
                }

                return document.scrollingElement.scrollTop > before;
            }
            """,
            new object[] { ".canvas-layout-container", scrollBefore },
            new() { Timeout = 30000 });

        var boxAfter = await GetBoxAsync(node);
        Assert.Equal(boxBefore.Width, boxAfter.Width);
        Assert.Equal(boxBefore.Height, boxAfter.Height);
    }

    [Fact]
    public async Task WhenCanvasIsEdited_ThenDraggingFromANodeBorderDrawsNoLink()
    {
        // Arrange
        var page = await OpenCanvasPageAsync(edit: true);
        var node = GetNodes(page).First;
        var box = await GetBoxAsync(node);

        // Act
        await DragAsync(page, box.Left, box.Top + box.Height / 2, box.Left + box.Width / 2, box.Bottom + 60);

        // Assert
        await Assertions.Expect(GetCanvas(page).Locator("line")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task WhenCanvasIsEdited_ThenANodeCanStillBeMoved()
    {
        // Arrange
        var page = await OpenCanvasPageAsync(edit: true);
        var node = GetNodes(page).First;
        var boxBefore = await GetBoxAsync(node);

        // Act
        await DragAsync(
            page,
            boxBefore.Left + boxBefore.Width / 2,
            boxBefore.Top + boxBefore.Height / 2,
            boxBefore.Left + boxBefore.Width / 2 + 80,
            boxBefore.Top + boxBefore.Height / 2 + 60);

        // Assert
        await page.WaitForFunctionAsync(
            """
            ([selector, left, top]) => {
                const rectangle = document.querySelector(selector).getBoundingClientRect();
                return Math.abs(rectangle.left - left) > 20 && Math.abs(rectangle.top - top) > 20;
            }
            """,
            new object[] { ".canvas-layout-container .canvas-node", boxBefore.Left, boxBefore.Top },
            new() { Timeout = 30000 });
    }

    [Fact]
    public async Task WhenCanvasIsEdited_ThenTheCanvasCanStillBePanned()
    {
        // Arrange
        var page = await OpenCanvasPageAsync(edit: true);
        var boxBefore = await GetBoxAsync(GetNodes(page).First);
        var canvasBox = await GetBoxAsync(GetCanvas(page));
        var startX = canvasBox.Right - 40;
        var startY = canvasBox.Bottom - 40;

        // Act
        await DragAsync(page, startX, startY, startX - 150, startY - 100);

        // Assert
        await page.WaitForFunctionAsync(
            """
            ([selector, left, top]) => {
                const rectangle = document.querySelector(selector).getBoundingClientRect();
                return Math.abs(rectangle.left - left) > 20 && Math.abs(rectangle.top - top) > 20;
            }
            """,
            new object[] { ".canvas-layout-container .canvas-node", boxBefore.Left, boxBefore.Top },
            new() { Timeout = 30000 });
    }

    [Fact]
    public async Task WhenCanvasIsEdited_ThenTheWheelStillZoomsTheCanvas()
    {
        // Arrange
        var page = await OpenCanvasPageAsync(edit: true);
        var boxBefore = await GetBoxAsync(GetNodes(page).First);
        var canvasBox = await GetBoxAsync(GetCanvas(page));
        await page.Mouse.MoveAsync((float)(canvasBox.Right - 40), (float)(canvasBox.Bottom - 40));

        // Act
        await page.Mouse.WheelAsync(0, -300);

        // Assert
        await page.WaitForFunctionAsync(
            """
            ([selector, width]) => Math.abs(document.querySelector(selector).getBoundingClientRect().width - width) > 5
            """,
            new object[] { ".canvas-layout-container .canvas-node", boxBefore.Width },
            new() { Timeout = 30000 });
    }

    private async Task<IPage> OpenCanvasPageAsync(bool withTextBelow = false, bool edit = false)
    {
        await _fixture.WriteDemoPageAsync(ScratchPage, CreateCanvasPage(withTextBelow));

        var page = await _fixture.CreatePageAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = ViewportWidth, Height = ViewportHeight },
        });
        await page.GotoAsync($"{_fixture.ServerAddress}pages/Demo/{ScratchPage}");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        if (edit)
        {
            var editMenu = page.GetByTestId("edit-mode-menu");
            await Assertions.Expect(editMenu).ToBeVisibleAsync(new() { Timeout = 30000 });
            await editMenu.ClickAsync();
            await page.Locator(".mud-popover-open").GetByText("Split", new() { Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator(".canvas-layout-container.editing")).ToBeVisibleAsync(new() { Timeout = 30000 });
        }

        // The library draws the border of a node and the area that a link starts from only after it has measured the node.
        await Assertions.Expect(GetCanvas(page).Locator("svg rect")).ToHaveCountAsync(4, new() { Timeout = 30000 });
        await GetNodes(page).First.ScrollIntoViewIfNeededAsync();
        await Assertions.Expect(GetNodes(page).First).ToBeVisibleAsync();
        return page;
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

    private static Task<double> GetScrollPositionAsync(IPage page)
    {
        return page.EvaluateAsync<double>(
            """
            () => {
                for (let current = document.querySelector('.canvas-layout-container'); current; current = current.parentElement) {
                    const style = getComputedStyle(current);
                    if ((style.overflowY === 'auto' || style.overflowY === 'scroll') && current.scrollHeight > current.clientHeight) {
                        return current.scrollTop;
                    }
                }

                return document.scrollingElement.scrollTop;
            }
            """);
    }

    private static string CreateCanvasPage(bool withTextBelow)
    {
        var builder = new StringBuilder();
        builder.Append("---\ntitle: Scratch\n---\n\n# Scratch\n\n");
        builder.Append($":: HomeBlaze.Components.CanvasLayout#canvas MinHeight={CanvasHeight}\n");
        builder.Append("```json\n");
        builder.Append("{\n  \"Nodes\": [\n");
        builder.Append("    { \"X\": 40, \"Y\": 40, \"Width\": 300, \"Height\": 200, \"Child\": { \"$type\": \"HomeBlaze.Components.Widget\", \"Path\": \"../Motor\" } },\n");
        builder.Append("    { \"X\": 420, \"Y\": 40, \"Width\": 300, \"Height\": 200, \"Child\": { \"$type\": \"HomeBlaze.Components.Widget\", \"Path\": \"../Motor\" } }\n");
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
