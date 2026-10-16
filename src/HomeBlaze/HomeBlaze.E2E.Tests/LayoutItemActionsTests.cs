using HomeBlaze.E2E.Tests.Infrastructure;
using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for when the buttons of the cells of a grid and of the nodes of a canvas show in inline edit mode.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class LayoutItemActionsTests
{
    private const int Timeout = 30000;

    // The page that tests overwrite. Each of them writes its own content before it opens the page.
    private const string ScratchPage = "Scratch.md";

    // A grid with a cell that holds a card and an empty cell, and a canvas with a node that holds a card and one that
    // holds text only.
    private const string LayoutsPage =
        "---\ntitle: Scratch\n---\n\n# Scratch\n\n"
        + ":: HomeBlaze.Components.GridLayout#grid Rows=1 Columns=3\n"
        + "::: Cell Row=0 Column=0\n"
        + ":::: HomeBlaze.Components.Card Title=\"Grid Card\"\n"
        + "::: Cell Row=0 Column=1\n"
        + "::\n\n"
        + ":: HomeBlaze.Components.CanvasLayout#canvas MinHeight=300\n"
        + "::: Node X=40 Y=40 Width=300 Height=150\n"
        + ":::: HomeBlaze.Components.Card Title=\"Node Card\"\n"
        + "::: Node X=400 Y=40 Width=300 Height=150\n"
        + "Text in a node.\n"
        + "::\n";

    private readonly PlaywrightFixture _fixture;

    public LayoutItemActionsTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task WhenNothingIsSelectedInInlineMode_ThenNoCellOrNodeHasButtons()
    {
        // Arrange
        await _fixture.WriteDemoPageAsync(ScratchPage, LayoutsPage);

        // Act
        var page = await _fixture.OpenPageInEditModeAsync($"Demo/{ScratchPage}");

        // Assert
        await Assertions.Expect(GetCells(page)).ToHaveCountAsync(2, new() { Timeout = Timeout });
        await Assertions.Expect(GetNodes(page)).ToHaveCountAsync(2, new() { Timeout = Timeout });
        await Assertions.Expect(page.Locator(".subject-component-wrapper.selectable").First).ToBeVisibleAsync(new() { Timeout = Timeout });
        await Assertions.Expect(page.Locator(".layout-item-actions")).ToHaveCountAsync(4);
        await Assertions.Expect(page.Locator(".layout-item-actions:visible")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task WhenTheGridIsSelected_ThenAllItsCellsShowTheirButtonsAndTheNodesDoNot()
    {
        // Arrange
        await _fixture.WriteDemoPageAsync(ScratchPage, LayoutsPage);
        var page = await _fixture.OpenPageInEditModeAsync($"Demo/{ScratchPage}");
        var emptyCell = GetCells(page).Nth(1);

        // Act: the empty cell has no widget, so a click on it selects the grid.
        await emptyCell.SelectWidgetAsync();

        // Assert
        await Assertions.Expect(page.Locator(".subject-component-wrapper.selected > .grid-layout-container")).ToHaveCountAsync(1);
        await Assertions.Expect(GetCells(page).GetByTitle("Edit Cell")).ToHaveCountAsync(2);
        await Assertions.Expect(GetCells(page).Nth(0).GetByTitle("Edit Cell")).ToBeVisibleAsync();
        await Assertions.Expect(emptyCell.GetByTitle("Edit Cell")).ToBeVisibleAsync();
        await Assertions.Expect(emptyCell.GetByTitle("Delete", new() { Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(GetNodes(page).Locator(".layout-item-actions:visible")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task WhenAWidgetInACellIsSelected_ThenTheCellsOfItsGridShowTheirButtons()
    {
        // Arrange
        await _fixture.WriteDemoPageAsync(ScratchPage, LayoutsPage);
        var page = await _fixture.OpenPageInEditModeAsync($"Demo/{ScratchPage}");

        // Act
        await GetCells(page).GetByText("Grid Card", new() { Exact = true }).SelectWidgetAsync();

        // Assert
        await Assertions.Expect(page.Locator(".subject-component-wrapper.selected")).ToContainTextAsync("Grid Card");
        await Assertions.Expect(GetCells(page).Locator(".layout-item-actions:visible")).ToHaveCountAsync(2);
        await Assertions.Expect(GetNodes(page).Locator(".layout-item-actions:visible")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task WhenEscapeIsPressedWithTheGridSelected_ThenItsCellsHideTheirButtons()
    {
        // Arrange
        await _fixture.WriteDemoPageAsync(ScratchPage, LayoutsPage);
        var page = await _fixture.OpenPageInEditModeAsync($"Demo/{ScratchPage}");
        await GetCells(page).Nth(1).SelectWidgetAsync();
        await Assertions.Expect(GetCells(page).Locator(".layout-item-actions:visible")).ToHaveCountAsync(2);

        // Act
        await page.Keyboard.PressAsync("Escape");

        // Assert
        await Assertions.Expect(page.GetWidgetActionBar()).ToHaveCountAsync(0, new() { Timeout = Timeout });
        await Assertions.Expect(page.Locator(".layout-item-actions:visible")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task WhenTabIsPressedAfterTheBarOfAWidgetInACell_ThenTheButtonsOfItsCellAreFocused()
    {
        // Arrange
        await _fixture.WriteDemoPageAsync(ScratchPage, LayoutsPage);
        var page = await _fixture.OpenPageInEditModeAsync($"Demo/{ScratchPage}");
        var cell = GetCells(page).Nth(0);
        var wrapper = cell.Locator(".subject-component-wrapper.selectable").Filter(new() { HasText = "Grid Card" }).Last;
        await Assertions.Expect(wrapper).ToBeVisibleAsync(new() { Timeout = Timeout });
        await wrapper.FocusAsync();
        await page.Keyboard.PressAsync("Enter");
        var actionBar = page.GetWidgetActionBar();
        await Assertions.Expect(actionBar.GetByTestId("edit-subject-button")).ToBeVisibleAsync(new() { Timeout = Timeout });

        // Act: the bar of the card holds the buttons that select the grid and edit the card.
        await page.Keyboard.PressAsync("Tab");
        await Assertions.Expect(actionBar.GetByTestId("select-parent-button")).ToBeFocusedAsync(new() { Timeout = Timeout });
        await page.Keyboard.PressAsync("Tab");
        await Assertions.Expect(actionBar.GetByTestId("edit-subject-button")).ToBeFocusedAsync(new() { Timeout = Timeout });
        await page.Keyboard.PressAsync("Tab");

        // Assert
        await Assertions.Expect(cell.GetByTitle("Edit Cell")).ToBeFocusedAsync(new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenANodeWithAWidgetIsClicked_ThenTheNodesOfItsCanvasShowTheirButtons()
    {
        // Arrange
        await _fixture.WriteDemoPageAsync(ScratchPage, LayoutsPage);
        var page = await _fixture.OpenPageInEditModeAsync($"Demo/{ScratchPage}");
        await WaitForNodesAsync(page);

        // Act
        await GetNodes(page).Filter(new() { HasText = "Node Card" }).SelectWidgetAsync();

        // Assert
        await Assertions.Expect(page.Locator(".subject-component-wrapper.selected")).ToContainTextAsync("Node Card");
        await Assertions.Expect(GetNodes(page).Locator(".layout-item-actions:visible")).ToHaveCountAsync(2);
        await Assertions.Expect(GetCells(page).Locator(".layout-item-actions:visible")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task WhenANodeWithoutAWidgetIsClicked_ThenTheCanvasIsSelectedAndItsNodesShowTheirButtons()
    {
        // Arrange
        await _fixture.WriteDemoPageAsync(ScratchPage, LayoutsPage);
        var page = await _fixture.OpenPageInEditModeAsync($"Demo/{ScratchPage}");
        await WaitForNodesAsync(page);
        var textNode = GetNodes(page).Filter(new() { HasText = "Text in a node." });

        // Act
        await textNode.SelectWidgetAsync();

        // Assert
        await Assertions.Expect(page.Locator(".subject-component-wrapper.selected > .canvas-layout-container")).ToHaveCountAsync(1);
        await Assertions.Expect(textNode.GetByTitle("Edit Node")).ToBeVisibleAsync();
        await Assertions.Expect(GetNodes(page).Locator(".layout-item-actions:visible")).ToHaveCountAsync(2);
        await Assertions.Expect(page.GetByTestId("create-subject-dialog")).ToHaveCountAsync(0);
    }

    private static ILocator GetCells(IPage page) => page.Locator(".grid-cell.editing");

    private static ILocator GetNodes(IPage page) => page.Locator(".canvas-node.editing");

    // The canvas selects a node with a press only after the library has measured the node and drawn the area that moves it.
    private static async Task WaitForNodesAsync(IPage page)
    {
        await Assertions.Expect(page.Locator(".canvas-layout-container.editing svg rect")).ToHaveCountAsync(4, new() { Timeout = Timeout });
        await GetNodes(page).First.ScrollIntoViewIfNeededAsync();
    }
}
