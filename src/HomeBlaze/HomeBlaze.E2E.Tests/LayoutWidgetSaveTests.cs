using HomeBlaze.Components;
using HomeBlaze.E2E.Tests.Infrastructure;
using HomeBlaze.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for the changes a layout widget makes to its layout and saves on its own, without an edit dialog.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class LayoutWidgetSaveTests
{
    private readonly PlaywrightFixture _fixture;

    public LayoutWidgetSaveTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task WhenCellOfUnstoredGridIsDeleted_ThenWarningIsShown()
    {
        // Arrange
        var (layout, _) = _fixture.CreateGridWithCell();

        await _fixture.WithUnstoredLayoutAsync(layout, async page =>
        {
            // Act
            await ConfirmDeleteAsync(page);

            // Assert
            await Assertions.Expect(page.GetByText("Configuration applied but not saved")).ToBeVisibleAsync(new() { Timeout = 30000 });
            Assert.Empty(layout.Cells);
        });
    }

    [Fact]
    public async Task WhenNodeOfUnstoredCanvasIsDeleted_ThenWarningIsShown()
    {
        // Arrange
        var (layout, _) = CreateCanvasWithNode();

        await _fixture.WithUnstoredLayoutAsync(layout, async page =>
        {
            // Act
            await ConfirmDeleteAsync(page);

            // Assert
            await Assertions.Expect(page.GetByText("Configuration applied but not saved")).ToBeVisibleAsync(new() { Timeout = 30000 });
            Assert.Empty(layout.Nodes);
        });
    }

    [Fact]
    public async Task WhenNodeOfUnstoredCanvasIsDragged_ThenWarningIsShown()
    {
        // Arrange
        var (layout, node) = CreateCanvasWithNode();

        await _fixture.WithUnstoredLayoutAsync(layout, async page =>
        {
            // Act
            await DragNodeAsync(page, 80, 40);

            // Assert
            await Assertions.Expect(page.GetByText("Configuration applied but not saved")).ToBeVisibleAsync(new() { Timeout = 30000 });
            Assert.NotEqual((100, 100), (node.X, node.Y));
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
