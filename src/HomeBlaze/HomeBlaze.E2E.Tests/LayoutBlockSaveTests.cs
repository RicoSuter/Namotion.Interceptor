using System.Text.RegularExpressions;
using HomeBlaze.Components;
using HomeBlaze.E2E.Tests.Infrastructure;
using HomeBlaze.Storage.Files;
using Microsoft.Playwright;
using Namotion.Interceptor.Testing;
using Xunit.Abstractions;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for what the editors of a grid and of a canvas write into the text of the page that holds the layout.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class LayoutBlockSaveTests
{
    private const int Timeout = 30000;

    private const string GridPage = "Grid.md";
    private const string CanvasPage = "Canvas.md";

    private const string GridHeadLine = ":: HomeBlaze.Components.GridLayout#grid Rows=1 Columns=4";
    private const string FirstNodeLine = "::: Node X=40 Y=40 Width=200 Height=100";
    private const string NotSavedWarning = "Configuration applied but not saved";

    private readonly PlaywrightFixture _fixture;
    private readonly ITestOutputHelper _output;

    public LayoutBlockSaveTests(PlaywrightFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task WhenCellIsAddedToGridOnPage_ThenACellBlockWithTheWidgetIsWritten()
    {
        // Arrange
        const string motorName = "CellMotor";

        await WithPageAsync(GridPage, CreateGridPage(), async () =>
        {
            var page = await _fixture.OpenPageInInlineModeAsync($"Demo/{GridPage}");
            var emptyCell = page.Locator(".grid-cell.empty[style*='grid-column: 3']");
            await Assertions.Expect(emptyCell).ToBeVisibleAsync(new() { Timeout = Timeout });

            // Act
            await emptyCell.ClickAsync();
            await Assertions.Expect(SubjectSetupDialogTests.TypeRows(page).First).ToBeVisibleAsync(new() { Timeout = Timeout });
            await SubjectSetupDialogTests.PickTypeAsync(page, "motor");
            await SubjectSetupDialogTests.EnterMotorNameAsync(page, motorName);
            await Assertions.Expect(SubjectSetupDialogTests.CreateButton(page)).ToBeEnabledAsync(new() { Timeout = Timeout });
            await SubjectSetupDialogTests.CreateButton(page).ClickAsync();
            await Assertions.Expect(SubjectSetupDialogTests.Dialog(page)).Not.ToBeVisibleAsync(new() { Timeout = Timeout });

            // Assert
            var text = await WaitForPageTextAsync(
                GridPage,
                written => written.Contains("::: Cell "),
                "The page was not written with a block for the new cell");

            Assert.Contains(
                $"{GridHeadLine}\n::: Cell Row=0 Column=2 RowSpan=1 ColumnSpan=1\n:::: HomeBlaze.Samples.Motor Name={motorName}",
                text);
            Assert.DoesNotContain("json", text);
            await Assertions.Expect(page.GetByText(NotSavedWarning)).ToHaveCountAsync(0);

            // The buttons of the cell are beside the edit button of the widget in it, and not over it.
            var cell = page.Locator(".grid-cell.editing[style*='grid-column: 3 / span 1']");
            await Assertions.Expect(cell).ToContainTextAsync(motorName, new() { Timeout = Timeout });
            await Assertions.Expect(cell.GetByTitle("Edit Cell")).ToBeVisibleAsync();
            await Assertions.Expect(cell.GetByTestId("edit-subject-button")).ToBeVisibleAsync(new() { Timeout = Timeout });

            var cellButtons = await GetBoxAsync(cell.Locator(".layout-item-actions"));
            var widgetButton = await GetBoxAsync(cell.GetByTestId("edit-subject-button"));
            Assert.True(
                cellButtons.Right <= widgetButton.Left,
                $"The buttons of the cell end at {cellButtons.Right} and cover the edit button of the widget, which starts at {widgetButton.Left}.");
        });
    }

    [Fact]
    public async Task WhenCellOfGridOnPageIsDeleted_ThenItsBlockIsRemoved()
    {
        // Arrange
        var before = CreateGridPage("::: Cell Row=0 Column=0", "first cell", "::: Cell Row=0 Column=1", "second cell");

        await WithPageAsync(GridPage, before, async () =>
        {
            var page = await _fixture.OpenPageInInlineModeAsync($"Demo/{GridPage}");
            var firstCell = page.Locator(".grid-cell.editing").Filter(new() { HasText = "first cell" });
            await Assertions.Expect(firstCell).ToBeVisibleAsync(new() { Timeout = Timeout });

            // Act
            await firstCell.GetByTitle("Delete", new() { Exact = true }).ClickAsync();
            await page.Locator(".mud-dialog").GetByRole(AriaRole.Button, new() { Name = "Delete" }).ClickAsync();

            // Assert
            var text = await WaitForPageTextAsync(
                GridPage,
                written => !written.Contains("first cell"),
                "The page was not written without the block of the deleted cell");

            Assert.Equal(CreateGridPage("::: Cell Row=0 Column=1", "second cell"), text);
            await Assertions.Expect(page.GetByText(NotSavedWarning)).ToHaveCountAsync(0);
            await Assertions.Expect(page.Locator(".grid-cell.editing").GetByText("second cell")).ToBeVisibleAsync(new() { Timeout = Timeout });
            await Assertions.Expect(page.GetByText("first cell")).ToHaveCountAsync(0, new() { Timeout = Timeout });
        });
    }

    [Fact]
    public async Task WhenEmptyCellOfGridOnPageIsShownInEditMode_ThenItHasItsButtons()
    {
        // Arrange
        await WithPageAsync(GridPage, CreateGridPage("::: Cell Row=0 Column=0"), async () =>
        {
            // Act
            var page = await _fixture.OpenPageInInlineModeAsync($"Demo/{GridPage}");

            // Assert
            var cell = page.Locator(".grid-cell.editing");
            await Assertions.Expect(cell).ToHaveCountAsync(1, new() { Timeout = Timeout });
            await Assertions.Expect(cell.GetByTitle("Edit Cell")).ToBeVisibleAsync();
            await Assertions.Expect(cell.GetByTitle("Delete", new() { Exact = true })).ToBeVisibleAsync();

            // They are at the left corner of the cell, where nothing that the cell shows has buttons of its own.
            var cellBox = await GetBoxAsync(cell);
            var buttons = await GetBoxAsync(cell.Locator(".layout-item-actions"));
            Assert.InRange(buttons.Left - cellBox.Left, 0, 12);
            Assert.InRange(buttons.Top - cellBox.Top, 0, 12);

            // A button that is visible can still be covered or take no clicks, so one of them is used.
            await cell.GetByTitle("Edit Cell").ClickAsync();
            var dialog = page.Locator(".mud-dialog");
            await Assertions.Expect(dialog.GetByLabel("Column Span")).ToBeVisibleAsync(new() { Timeout = Timeout });
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Cancel" }).ClickAsync();
            await Assertions.Expect(dialog).ToHaveCountAsync(0, new() { Timeout = Timeout });
        });
    }

    [Fact]
    public async Task WhenNodeOfCanvasOnPageIsDragged_ThenOnlyTheLineOfThatNodeChanges()
    {
        // Arrange
        var before = Normalize(await _fixture.ReadFileAsync(_fixture.GetDemoPagePath(CanvasPage)));
        Assert.Contains(FirstNodeLine + "\nfirst node\n", before);

        await WithPageAsync(CanvasPage, before, async () =>
        {
            var page = await _fixture.OpenPageInInlineModeAsync($"Demo/{CanvasPage}");
            var canvas = page.Locator(".canvas-layout-container.editing");
            await Assertions.Expect(canvas).ToBeVisibleAsync(new() { Timeout = Timeout });

            // The library draws the area that moves a node only after it has measured the node.
            await Assertions.Expect(canvas.Locator("svg rect")).ToHaveCountAsync(4, new() { Timeout = Timeout });
            var node = canvas.Locator(".canvas-node").Filter(new() { HasText = "first node" });
            await node.ScrollIntoViewIfNeededAsync();
            var box = await GetBoxAsync(node);
            var startX = box.Left + box.Width / 2;
            var startY = box.Top + box.Height / 2;

            // Act
            await page.Mouse.MoveAsync((float)startX, (float)startY);
            await page.Mouse.DownAsync();
            await page.Mouse.MoveAsync((float)(startX + 100), (float)(startY + 60), new() { Steps = 10 });
            await page.Mouse.UpAsync();

            // Assert: the position is the final one only after the events of the drag have arrived, and the page is
            // written at the release.
            var x = 0;
            var y = 0;
            var text = await WaitForPageTextAsync(
                CanvasPage,
                written =>
                {
                    if (TryGetFirstCanvasNode() is not { } firstNode)
                    {
                        return false;
                    }

                    (x, y) = (firstNode.X, firstNode.Y);
                    return x != 40 && y != 40 && written.Contains($"::: Node X={x} Y={y} Width=200 Height=100\n");
                },
                "The page was not written with the new position of the moved node");

            Assert.True(x > 40, $"X is {x}");
            Assert.True(y > 40, $"Y is {y}");
            Assert.Equal(before.Replace(FirstNodeLine, $"::: Node X={x} Y={y} Width=200 Height=100"), text);
            Assert.Contains("::: Node#second X=300 Y=40 Width=200 Height=100\nsecond node\n", text);
            await Assertions.Expect(page.GetByText(NotSavedWarning)).ToHaveCountAsync(0);
            await Assertions.Expect(page.Locator(".mud-dialog")).ToHaveCountAsync(0);
        });
    }

    [Fact]
    public async Task WhenCellOfGridOnPageIsEdited_ThenItsHeadLineChanges()
    {
        // Arrange
        var before = CreateGridPage("::: Cell Row=0 Column=0", "the cell");

        await WithPageAsync(GridPage, before, async () =>
        {
            var page = await _fixture.OpenPageInInlineModeAsync($"Demo/{GridPage}");
            var cell = page.Locator(".grid-cell.editing").Filter(new() { HasText = "the cell" });
            await Assertions.Expect(cell).ToBeVisibleAsync(new() { Timeout = Timeout });

            // Act
            await cell.GetByTitle("Edit Cell").ClickAsync();
            var dialog = page.Locator(".mud-dialog");
            var columnSpanField = dialog.GetByLabel("Column Span");
            await Assertions.Expect(columnSpanField).ToBeVisibleAsync(new() { Timeout = Timeout });
            await Assertions.Expect(dialog.GetByText("Page content")).ToBeVisibleAsync();
            await columnSpanField.FillAsync("2");
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();

            // Assert
            var text = await WaitForPageTextAsync(
                GridPage,
                written => written.Contains("ColumnSpan=2"),
                "The page was not written with the new column span of the cell");

            Assert.Equal(CreateGridPage("::: Cell Row=0 Column=0 RowSpan=1 ColumnSpan=2", "the cell"), text);
            await Assertions.Expect(page.GetByText(NotSavedWarning)).ToHaveCountAsync(0);
            await Assertions.Expect(page.Locator(".grid-cell.editing").GetByText("the cell")).ToBeVisibleAsync(new() { Timeout = Timeout });
        });
    }

    [Fact]
    public async Task WhenGridWithCellsIsEditedThroughThePageEditor_ThenItsCellsStayBlocks()
    {
        // Arrange
        var before = CreateGridPage("::: Cell Row=0 Column=0", "first cell", "::: Cell Row=0 Column=1", "second cell");

        await WithPageAsync(GridPage, before, async () =>
        {
            var page = await _fixture.CreatePageAsync();
            await page.GotoAsync($"{_fixture.ServerAddress}pages/Demo/{GridPage}");
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
            await Assertions.Expect(page.Locator(".grid-cell").GetByText("first cell")).ToBeVisibleAsync(new() { Timeout = Timeout });

            // The edit menu is prerendered, and a click before the circuit is connected is lost.
            await Assertions.Expect(page.GetByTitle(new Regex(@"^Automatic \("))).ToBeVisibleAsync(new() { Timeout = Timeout });
            await page.GetByTestId("edit-mode-menu").ClickAsync();
            await page.Locator(".mud-popover-open").GetByText("Split", new() { Exact = true }).ClickAsync();

            // The editor only knows its block regions once the decorations are applied.
            await Assertions.Expect(page.Locator(".subject-block-decoration").First).ToBeVisibleAsync(new() { Timeout = Timeout });
            var headLine = page.Locator(".monaco-editor .view-line", new() { HasTextString = "HomeBlaze.Components.GridLayout#grid" });
            await Assertions.Expect(headLine).ToBeVisibleAsync(new() { Timeout = Timeout });

            // Act: the dialog of the grid block saves the grid, and the editor then puts the grid into its own text.
            await headLine.ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Edit grid" }).ClickAsync();

            var dialog = page.GetByRole(AriaRole.Dialog);
            await dialog.GetByLabel("Columns").FillAsync("3");
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();

            var writtenHeadLine = page.Locator(".monaco-editor .view-line", new() { HasTextString = "Columns=3" });
            await Assertions.Expect(writtenHeadLine).ToBeVisibleAsync(new() { Timeout = Timeout });

            // The dialog reports its save before the editor writes its text, and the editor is done long before
            // that notification is gone again.
            await Assertions.Expect(page.GetByText("Configuration saved.")).ToHaveCountAsync(0, new() { Timeout = Timeout });

            // Saving writes the page and reads it again, which gives the page a new dictionary of children. The
            // notification of the save is not what is waited for: the editor updates its decorations after the write,
            // and it reports the save as failed when that update fails.
            var gridPage = (MarkdownFile)_fixture.GetDemoFolder().Children[GridPage];
            var childrenBeforeSave = gridPage.Children;
            await page.Locator("[data-testid='save-button'] button").ClickAsync();
            await AsyncTestHelpers.WaitUntilAsync(
                () => !ReferenceEquals(gridPage.Children, childrenBeforeSave),
                message: "The page was not written and read again after it was saved in the editor");

            // Assert
            var text = Normalize(await _fixture.ReadFileAsync(_fixture.GetDemoPagePath(GridPage)));

            Assert.Contains(":: HomeBlaze.Components.GridLayout#grid Rows=1 Columns=3\n", text);
            Assert.Contains("::: Cell Row=0 Column=0\nfirst cell\n", text);
            Assert.Contains("::: Cell Row=0 Column=1\nsecond cell\n", text);
            Assert.DoesNotContain("json", text);
            Assert.Equal(before.Replace("Columns=4", "Columns=3"), text);
            await Assertions.Expect(page.Locator(".markdown-block-error")).ToHaveCountAsync(0);
        });
    }

    // Runs the test on a page of the test data with the given text and gives the page its text back afterwards.
    private async Task WithPageAsync(string name, string content, Func<Task> testAsync)
    {
        var original = await _fixture.ReadFileAsync(_fixture.GetDemoPagePath(name));
        var isCompleted = false;
        try
        {
            // Written also when the file has this text: a test that gave the file its bytes back left the page that
            // the server holds as it was.
            await _fixture.WriteDemoPageAsync(name, content);
            await testAsync();
            isCompleted = true;
        }
        finally
        {
            try
            {
                // Written through the subject, so that the page the server holds is restored along with the file.
                await _fixture.WriteDemoPageAsync(name, original);
            }
            catch (Exception exception) when (!isCompleted)
            {
                // The failure of the test itself is the one to report.
                _output.WriteLine($"Restoring {name} failed: {exception}");
            }
        }
    }

    // Reads the page until its text is the expected one. A write that the page refuses leaves the text as it was.
    private async Task<string> WaitForPageTextAsync(string name, Func<string, bool> isExpected, string message)
    {
        var path = _fixture.GetDemoPagePath(name);
        var text = string.Empty;

        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                text = Normalize(_fixture.ReadFileAsync(path).GetAwaiter().GetResult());
                return isExpected(text);
            },
            message: message);

        return text;
    }

    // Null while the page is read again and holds no canvas with a node for a moment.
    private CanvasNode? TryGetFirstCanvasNode()
    {
        return _fixture.GetDemoFolder().Children.TryGetValue(CanvasPage, out var file)
            && file is MarkdownFile canvasPage
            && canvasPage.Children.TryGetValue("canvas", out var canvas)
            && canvas is CanvasLayout { Nodes: { Length: > 0 } nodes }
                ? nodes[0]
                : null;
    }

    private static string CreateGridPage(params string[] linesOfCells)
    {
        return $"---\ntitle: Grid Demo\n---\n\n# Grid Demo\n\n{GridHeadLine}\n"
            + string.Concat(linesOfCells.Select(line => line + "\n"))
            + "::\n";
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n");

    private sealed record Box(double Left, double Top, double Width, double Height)
    {
        public double Right => Left + Width;
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
}
