using HomeBlaze.Components;
using HomeBlaze.E2E.Tests.Infrastructure;
using HomeBlaze.Services;
using HomeBlaze.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Namotion.Interceptor;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for the changes a layout widget makes to its layout and saves on its own, without an edit dialog.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class LayoutWidgetSaveTests
{
    private const string PagePath = "Demo/LayoutPage.md";
    private const string NotSavedWarning = "Configuration applied but not saved";

    private readonly PlaywrightFixture _fixture;

    public LayoutWidgetSaveTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task WhenCellOfUnstoredGridIsDeleted_ThenWarningIsShown()
    {
        // Arrange
        var subjectFactory = _fixture.ServerServices.GetRequiredService<SubjectFactory>();

        var cell = subjectFactory.CreateSubject<GridCell>();
        cell.Row = 0;
        cell.Column = 0;
        cell.Child = subjectFactory.CreateSubject<GridLayout>();

        var layout = subjectFactory.CreateSubject<GridLayout>();
        layout.Cells = [cell];

        await WithUnstoredLayoutAsync(layout, async page =>
        {
            // Act
            await ConfirmDeleteAsync(page);

            // Assert
            await Assertions.Expect(page.GetByText(NotSavedWarning)).ToBeVisibleAsync(new() { Timeout = 30000 });
            Assert.Empty(layout.Cells);
        });
    }

    [Fact]
    public async Task WhenNodeOfUnstoredCanvasIsDeleted_ThenWarningIsShown()
    {
        // Arrange
        var (layout, _) = CreateCanvasWithNode();

        await WithUnstoredLayoutAsync(layout, async page =>
        {
            // Act
            await ConfirmDeleteAsync(page);

            // Assert
            await Assertions.Expect(page.GetByText(NotSavedWarning)).ToBeVisibleAsync(new() { Timeout = 30000 });
            Assert.Empty(layout.Nodes);
        });
    }

    [Fact]
    public async Task WhenNodeOfUnstoredCanvasIsDragged_ThenWarningIsShown()
    {
        // Arrange
        var (layout, node) = CreateCanvasWithNode();

        await WithUnstoredLayoutAsync(layout, async page =>
        {
            // Act
            await DragNodeAsync(page, 80, 40);

            // Assert
            await Assertions.Expect(page.GetByText(NotSavedWarning)).ToBeVisibleAsync(new() { Timeout = 30000 });
            Assert.NotEqual((100, 100), (node.X, node.Y));
        });
    }

    [Fact]
    public async Task WhenNodeOfUnstoredCanvasIsDraggedAgain_ThenWarningIsNotRepeated()
    {
        // Arrange: the first warning is closed, because a notification that is still listed would swallow
        // a second one with the same text.
        var (layout, node) = CreateCanvasWithNode();

        await WithUnstoredLayoutAsync(layout, async page =>
        {
            var warning = page.GetByText(NotSavedWarning);

            await DragNodeAsync(page, 80, 40);
            await Assertions.Expect(warning).ToBeVisibleAsync(new() { Timeout = 30000 });
            var positionAfterFirstDrag = (node.X, node.Y);

            await page.Locator(".mud-snackbar button").ClickAsync();
            await Assertions.Expect(warning).ToHaveCountAsync(0);

            // Act
            await DragNodeAsync(page, 80, 40);

            // Assert: the circuit handles events in order, so once the dialog of the later click is shown,
            // a warning of the drag before it would be shown too.
            await page.GetByTitle("Edit Node").ClickAsync();
            await Assertions.Expect(page.Locator(".mud-dialog")).ToBeVisibleAsync(new() { Timeout = 30000 });
            Assert.NotEqual(positionAfterFirstDrag, (node.X, node.Y));
            await Assertions.Expect(warning).ToHaveCountAsync(0);
        });
    }

    private (CanvasLayout Layout, CanvasNode Node) CreateCanvasWithNode()
    {
        var subjectFactory = _fixture.ServerServices.GetRequiredService<SubjectFactory>();

        var node = subjectFactory.CreateSubject<CanvasNode>();
        node.X = 100;
        node.Y = 100;
        node.Child = subjectFactory.CreateSubject<GridLayout>();

        var layout = subjectFactory.CreateSubject<CanvasLayout>();
        layout.Nodes = [node];

        return (layout, node);
    }

    /// <summary>
    /// Runs the test on the layout page in inline edit mode, with the layout in the place of the stored one
    /// in the tree, so that the storage knows no file for it.
    /// </summary>
    private async Task WithUnstoredLayoutAsync(IInterceptorSubject layout, Func<IPage, Task> testAsync)
    {
        var rootManager = _fixture.ServerServices.GetRequiredService<RootManager>();
        var root = (FluentStorageContainer)rootManager.Root!;
        var folder = (VirtualFolder)root.Children["Demo"];
        var originalChildren = folder.Children;

        try
        {
            folder.Children = new Dictionary<string, IInterceptorSubject>(originalChildren) { ["Layout"] = layout };

            var page = await _fixture.CreatePageAsync();
            await page.GotoAsync($"{_fixture.ServerAddress}pages/{PagePath}");
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

            var editMenu = page.Locator("[data-testid='edit-mode-menu']");
            await Assertions.Expect(editMenu).ToBeVisibleAsync(new() { Timeout = 30000 });
            await editMenu.ClickAsync();
            await page.GetByText("Inline").ClickAsync();

            await testAsync(page);
        }
        finally
        {
            folder.Children = originalChildren;
        }
    }

    private static async Task ConfirmDeleteAsync(IPage page)
    {
        var deleteButton = page.GetByTitle("Delete", new() { Exact = true });
        await Assertions.Expect(deleteButton).ToBeVisibleAsync(new() { Timeout = 30000 });
        await deleteButton.ClickAsync();

        await page.Locator(".mud-dialog").GetByRole(AriaRole.Button, new() { Name = "Delete" }).ClickAsync();
    }

    private static async Task DragNodeAsync(IPage page, float deltaX, float deltaY)
    {
        var node = page.Locator(".canvas-node.editing");
        await Assertions.Expect(node).ToBeVisibleAsync(new() { Timeout = 30000 });

        // The buttons of the node are at its top right, so it is grabbed at its lower left.
        var box = (await node.BoundingBoxAsync())!;
        var startX = box.X + box.Width / 4;
        var startY = box.Y + box.Height * 3 / 4;

        await page.Mouse.MoveAsync(startX, startY);
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync(startX + deltaX, startY + deltaY, new() { Steps = 8 });
        await page.Mouse.UpAsync();
    }
}
