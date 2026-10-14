using System.Net;
using HomeBlaze.Abstractions;
using HomeBlaze.Components;
using HomeBlaze.Samples;
using HomeBlaze.Services;
using HomeBlaze.Storage.Pages;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor.Tracking.Parent;

namespace HomeBlaze.Storage.Tests.Pages;

[Collection(MarkdownPageFixture.StorageCollection)]
public class LayoutSlotBlockTests
{
    private const string Motors =
        ":: HomeBlaze.Samples.Motor#pump Name=\"Pump\" TargetSpeed=1200\n::\n\n";

    [Fact]
    public async Task WhenGridHasCellBlocks_ThenEachCellHoldsItsContent()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateWithWidgetsAndSamplesAsync();
        var content = Motors +
            ":: HomeBlaze.Components.GridLayout#grid Rows=2 Columns=2\n" +
            "::: Cell Row=0 Column=0\n:::: HomeBlaze.Components.Widget Path=pump\n" +
            "::: Cell#status Row=0 Column=1 ColumnSpan=2\n**Pump** is set to {{ pump/TargetSpeed }}.\n" +
            "::: Cell\n" +
            "::\n";

        // Act
        var page = await fixture.AddPageAsync("page.md", content);

        // Assert
        var grid = Assert.IsType<GridLayout>(page.Children["grid"]);
        Assert.Equal(
            new (int?, int?, int)[] { (0, 0, 1), (0, 1, 2), (null, null, 1) },
            grid.Cells.Select(cell => (cell.Row, cell.Column, cell.ColumnSpan)).ToArray());
        var widget = Assert.IsType<MarkdownFragment>(grid.Cells[0].Child).Children.Values.OfType<Widget>().Single();
        Assert.Equal("pump", widget.Path);
        Assert.Contains("<strong>Pump</strong>", MarkdownPageFixture.Html(Assert.IsType<MarkdownFragment>(grid.Cells[1].Child).Children));
        Assert.Null(grid.Cells[2].Child);
        Assert.Same(grid.Cells[1], ((IPathScope)page).TryGetNamedSubject("status"));
    }

    [Fact]
    public async Task WhenCanvasHasNodeBlocks_ThenEachNodeHoldsItsContent()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateWithWidgetsAndSamplesAsync();
        var content = Motors +
            ":: HomeBlaze.Components.CanvasLayout#plan MinHeight=300\n" +
            "::: Node X=20 Y=40 Width=300 Height=120\n:::: HomeBlaze.Components.Widget Path=pump\n" +
            "::: Node\ntext\n" +
            "::\n";

        // Act
        var page = await fixture.AddPageAsync("page.md", content);

        // Assert
        var canvas = Assert.IsType<CanvasLayout>(page.Children["plan"]);
        Assert.Equal(
            new[] { (20, 40, 300, 120), (0, 0, 200, 150) },
            canvas.Nodes.Select(node => (node.X, node.Y, node.Width, node.Height)).ToArray());
        Assert.Single(Assert.IsType<MarkdownFragment>(canvas.Nodes[0].Child).Children.Values.OfType<Widget>());
        Assert.Contains("text", MarkdownPageFixture.Html(Assert.IsType<MarkdownFragment>(canvas.Nodes[1].Child).Children));
    }

    [Theory]
    [InlineData(
        ":: HomeBlaze.Components.GridLayout\n```json\n{ \"Cells\": [] }\n```\n::",
        "Line 1: 'Cells' is written as blocks on a page: one 'Cell' block per value inside this block.")]
    [InlineData(
        ":: HomeBlaze.Components.CanvasLayout\n```json\n{ \"Nodes\": [] }\n```\n::",
        "Line 1: 'Nodes' is written as blocks on a page: one 'Node' block per value inside this block.")]
    [InlineData(
        ":: HomeBlaze.Components.GridLayout\n::: Cell Child=x\n::",
        "Line 2: 'Child' is the content of the block on a page, so it cannot be set as an argument.")]
    public async Task WhenCellsOrNodesAreWrittenAsConfigurationOnAPage_ThenTheBlockIsAnError(string content, string expectedMessage)
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateWithWidgetsAndSamplesAsync();

        // Act
        var page = await fixture.AddPageAsync("page.md", content);

        // Assert
        Assert.Contains(expectedMessage, WebUtility.HtmlDecode(MarkdownPageFixture.Html(page.Children)));
    }

    [Fact]
    public async Task WhenCellWithWidgetIsAddedAndSaved_ThenItsBlockIsWrittenAndTheReloadKeepsTheCell()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateWithWidgetsAndSamplesAsync();
        var page = await fixture.AddPageAsync("page.md", Motors + ":: HomeBlaze.Components.GridLayout#grid Rows=1 Columns=2\n::\n");
        var grid = (GridLayout)page.Children["grid"];
        var motor = new Motor { Name = "Added", TargetSpeed = 500 };
        var cell = new GridCell { Row = 0, Column = 1, Child = motor };
        grid.Cells = [.. grid.Cells, cell];

        // Act: the add dialog saves the new child.
        var handled = await page.WriteConfigurationAsync(motor, CancellationToken.None);
        var written = await fixture.ReadAsync("page.md");
        var parentCountBeforeReload = motor.GetParents().Length;
        await page.OnFileChangedAsync(CancellationToken.None);

        // Assert
        Assert.True(handled);
        Assert.Contains("::: Cell Row=0 Column=1 RowSpan=1 ColumnSpan=1\n:::: HomeBlaze.Samples.Motor Name=Added", written);
        Assert.Equal(1, parentCountBeforeReload);
        Assert.Same(cell, Assert.Single(grid.Cells));
        var reloaded = Assert.IsType<MarkdownFragment>(cell.Child).Children.Values.OfType<Motor>().Single();
        Assert.Equal(("Added", 500), (reloaded.Name, reloaded.TargetSpeed));

        // The reload creates the child of a new cell from the text, so the subject that the dialog created is replaced.
        Assert.NotSame(motor, reloaded);
        Assert.Empty(motor.GetParents());
    }

    [Fact]
    public async Task WhenNodeIsMovedAndSaved_ThenOnlyItsHeadLineChanges()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateWithWidgetsAndSamplesAsync();
        var content =
            ":: HomeBlaze.Components.CanvasLayout#plan MinHeight=300\n" +
            "::: Node X=20 Y=40\nfirst\n" +
            "::: Node X=60 Y=80\nsecond\n" +
            "::\n";
        var page = await fixture.AddPageAsync("page.md", content);
        var node = ((CanvasLayout)page.Children["plan"]).Nodes[1];
        node.X = 100;
        node.Y = 120;

        // Act
        var handled = await page.WriteConfigurationAsync(node, CancellationToken.None);

        // Assert
        Assert.True(handled);
        Assert.Equal(
            content.Replace("::: Node X=60 Y=80", "::: Node X=100 Y=120 Width=200 Height=150"),
            await fixture.ReadAsync("page.md"));
    }

    // The shape of the demo dashboard: a grid and a canvas in tabs of tabs, without closing lines, with text after them.
    private const string Dashboard =
        "# Dashboard\n\n" +
        ":: HomeBlaze.Components.TabLayout\n" +
        "::: Tab#grids Title=\"Grid and Canvas\" Icon=GridView\n" +
        ":::: HomeBlaze.Components.TabLayout\n" +
        "::::: Tab#grid Title=\"Grid\" Icon=GridView\n" +
        ":::::: HomeBlaze.Components.GridLayout#dashboardgrid Rows=2 Columns=3\n" +
        "::::::: Cell Row=0 Column=0\n" +
        ":::::::: HomeBlaze.Components.Widget Path=/Demo/Compressor\n" +
        "::::::: Cell Row=0 Column=1\n" +
        ":::::::: HomeBlaze.Components.Widget Path=/Demo/WaterPump\n" +
        "::::::: Cell Row=1 Column=1 ColumnSpan=2\n" +
        ":::::::: HomeBlaze.Components.Widget Path=/Demo/ExhaustFan\n" +
        "::::: Tab#canvas Title=\"Canvas\" Icon=Map\n" +
        ":::::: HomeBlaze.Components.CanvasLayout#dashboardcanvas MinHeight=620 IsSnapToGridEnabled=true GridSize=20\n" +
        "::::::: Node X=20 Y=20 Width=320 Height=280\n" +
        ":::::::: HomeBlaze.Components.Widget Path=/Demo/WaterPump\n" +
        "::::::: Node X=380 Y=60 Width=320 Height=280\n" +
        ":::::::: HomeBlaze.Components.Widget Path=/Demo/Compressor\n" +
        "::\n\n" +
        "Text after the tabs.\n";

    [Fact]
    public async Task WhenCellIsAddedToAGridInTabsWithoutClosingLines_ThenItsBlockFollowsTheLastCellAndTheReloadKeepsTheCells()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateWithWidgetsAndSamplesAsync();
        var page = await fixture.AddPageAsync("page.md", Dashboard);
        var grid = (GridLayout)((IPathScope)page).TryGetNamedSubject("dashboardgrid")!;
        var cells = grid.Cells;
        var widget = new Widget(fixture.Services.GetRequiredService<SubjectPathResolver>()) { Path = "/Demo/Conveyor" };
        var added = new GridCell { Row = 0, Column = 2, Child = widget };
        grid.Cells = [.. cells, added];

        // Act
        var handled = await page.WriteConfigurationAsync(grid, CancellationToken.None);
        var written = await fixture.ReadAsync("page.md");
        await page.OnFileChangedAsync(CancellationToken.None);

        // Assert
        Assert.True(handled);
        Assert.Equal(
            Dashboard.Replace(
                ":::::::: HomeBlaze.Components.Widget Path=/Demo/ExhaustFan\n",
                ":::::::: HomeBlaze.Components.Widget Path=/Demo/ExhaustFan\n" +
                "::::::: Cell Row=0 Column=2 RowSpan=1 ColumnSpan=1\n" +
                ":::::::: HomeBlaze.Components.Widget Path=/Demo/Conveyor\n"),
            written);
        Assert.Same(grid, ((IPathScope)page).TryGetNamedSubject("dashboardgrid"));
        Assert.Equal([cells[0], cells[1], cells[2], added], grid.Cells);
        var canvas = (CanvasLayout)((IPathScope)page).TryGetNamedSubject("dashboardcanvas")!;
        Assert.Equal(2, canvas.Nodes.Length);
    }

    [Fact]
    public async Task WhenCellIsRemovedFromAGridInTabsWithoutClosingLines_ThenItsLinesAreGoneAndTheReloadKeepsTheOtherCells()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateWithWidgetsAndSamplesAsync();
        var page = await fixture.AddPageAsync("page.md", Dashboard);
        var grid = (GridLayout)((IPathScope)page).TryGetNamedSubject("dashboardgrid")!;
        var (first, third) = (grid.Cells[0], grid.Cells[2]);
        grid.Cells = [first, third];

        // Act
        var handled = await page.WriteConfigurationAsync(grid, CancellationToken.None);
        var written = await fixture.ReadAsync("page.md");
        await page.OnFileChangedAsync(CancellationToken.None);

        // Assert
        Assert.True(handled);
        Assert.Equal(
            Dashboard.Replace("::::::: Cell Row=0 Column=1\n:::::::: HomeBlaze.Components.Widget Path=/Demo/WaterPump\n", string.Empty),
            written);
        Assert.Equal([first, third], grid.Cells);
        var widget = Assert.IsType<MarkdownFragment>(third.Child).Children.Values.OfType<Widget>().Single();
        Assert.Equal("/Demo/ExhaustFan", widget.Path);
    }

    [Fact]
    public async Task WhenNodeOfACanvasInTabsWithoutClosingLinesIsMoved_ThenOnlyItsHeadLineChangesAndTheReloadKeepsTheNodes()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateWithWidgetsAndSamplesAsync();
        var page = await fixture.AddPageAsync("page.md", Dashboard);
        var canvas = (CanvasLayout)((IPathScope)page).TryGetNamedSubject("dashboardcanvas")!;
        var nodes = canvas.Nodes;
        nodes[1].X = 400;
        nodes[1].Y = 80;

        // Act
        var handled = await page.WriteConfigurationAsync(nodes[1], CancellationToken.None);
        var written = await fixture.ReadAsync("page.md");
        await page.OnFileChangedAsync(CancellationToken.None);

        // Assert
        Assert.True(handled);
        Assert.Equal(
            Dashboard.Replace("::::::: Node X=380 Y=60 Width=320 Height=280", "::::::: Node X=400 Y=80 Width=320 Height=280"),
            written);
        Assert.Equal(nodes, canvas.Nodes);
        Assert.Equal((400, 80), (nodes[1].X, nodes[1].Y));
        Assert.Equal(3, ((GridLayout)((IPathScope)page).TryGetNamedSubject("dashboardgrid")!).Cells.Length);
    }

    [Fact]
    public async Task WhenCellIsRemovedAndTheGridSaved_ThenItsBlockIsRemovedWithItsContent()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateWithWidgetsAndSamplesAsync();
        var content =
            ":: HomeBlaze.Components.GridLayout#grid Rows=1 Columns=2\n" +
            "::: Cell Row=0 Column=0\nfirst\n" +
            "::: Cell Row=0 Column=1\nsecond\n" +
            "::\n";
        var page = await fixture.AddPageAsync("page.md", content);
        var grid = (GridLayout)page.Children["grid"];
        grid.Cells = [grid.Cells[1]];

        // Act
        await page.WriteConfigurationAsync(grid, CancellationToken.None);

        // Assert
        Assert.Equal(
            ":: HomeBlaze.Components.GridLayout#grid Rows=1 Columns=2\n::: Cell Row=0 Column=1\nsecond\n::\n",
            await fixture.ReadAsync("page.md"));
    }
}
