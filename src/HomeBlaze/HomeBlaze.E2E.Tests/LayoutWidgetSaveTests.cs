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
            await page.SelectGridThroughFreeCellAsync();

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
            // A click on the node selects the grid in it, which shows the buttons of the node.
            await page.Locator(".canvas-layout-container.editing").WaitForCanvasNodesAsync(1);
            await page.Locator(".canvas-node.editing").SelectWidgetAsync();

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
            await page.Locator(".canvas-layout-container.editing").WaitForCanvasNodesAsync(1);

            // Act: the buttons of the node are at its bottom right and the one of the widget in it at its top right.
            await page.Locator(".canvas-node.editing").DragNodeAsync(80, 40, fromLeftEdge: true);

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
}
