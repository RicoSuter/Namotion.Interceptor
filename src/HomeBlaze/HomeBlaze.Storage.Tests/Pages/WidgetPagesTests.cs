using HomeBlaze.Abstractions;
using HomeBlaze.Components;
using HomeBlaze.Samples;
using HomeBlaze.Services;
using HomeBlaze.Storage.Files;
using HomeBlaze.Storage.Internal;
using HomeBlaze.Storage.Pages;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Tests.Pages;

/// <summary>
/// Loads the folder of the widget reference as the application does and checks that each widget page shows what its
/// samples promise, with the two shared sample motors of the folder.
/// </summary>
[Collection(MarkdownPageFixture.StorageCollection)]
public class WidgetPagesTests
{
    // Relative to DocumentationFolderFixture.DocumentationFolder, as DocumentationFolderFixture.LoadAsync takes it.
    private const string WidgetsFolder = "administration/widgets";
    private const string PumpPath = "../Pump";
    private const string FanPath = "../Fan";
    private const string MapPath = "../value-map.md/motorstate";

    [Fact]
    public async Task WhenValueMapPageIsLoaded_ThenTheMapHasACaseWithTextPerMotorStatus()
    {
        // Arrange
        using var fixture = await DocumentationFolderFixture.LoadAsync(WidgetsFolder);

        // Act
        IPathScope page = fixture.FindPage(GetPagePath("value-map.md"));

        // Assert
        var map = Assert.IsType<ValueMap>(page.TryGetNamedSubject("motorstate"));
        Assert.Equal(Enum.GetNames<MotorStatus>(), map.Cases.Select(valueCase => valueCase.Value));
        Assert.All(map.Cases, valueCase => Assert.False(string.IsNullOrEmpty(valueCase.Text)));
    }

    [Theory]
    [InlineData("value-list.md")]
    [InlineData("value-tile.md")]
    [InlineData("indicator.md")]
    public async Task WhenValueWidgetPageIsLoaded_ThenEveryMapIsTheMapOfTheValueMapPage(string fileName)
    {
        // Arrange
        using var fixture = await DocumentationFolderFixture.LoadAsync(WidgetsFolder);
        var map = Assert.IsType<ValueMap>(((IPathScope)fixture.FindPage(GetPagePath("value-map.md"))).TryGetNamedSubject("motorstate"));
        var resolver = fixture.Services.GetRequiredService<SubjectPathResolver>();

        // Act
        var widgets = DocumentationFolderFixture.FindSubjects(fixture.FindPage(GetPagePath(fileName)))
            .Select(subject => (Subject: subject, Map: subject switch
            {
                ValueRow row => row.Map,
                ValueTile tile => tile.Map,
                Indicator indicator => indicator.Map,
                _ => null
            }))
            .Where(widget => widget.Map is not null)
            .ToList();

        // Assert
        Assert.NotEmpty(widgets);
        Assert.All(widgets, widget => Assert.Equal(MapPath, widget.Map));
        Assert.All(widgets, widget => Assert.Same(map, resolver.ResolveScopedSubject(widget.Map!, PathStyle.Canonical, widget.Subject)));
    }

    [Fact]
    public async Task WhenValueWidgetPagesAreLoaded_ThenEachHasTheSamplesOfItsSection()
    {
        // Arrange
        using var fixture = await DocumentationFolderFixture.LoadAsync(WidgetsFolder);

        // Act
        var valueList = DocumentationFolderFixture.FindSubjects(fixture.FindPage(GetPagePath("value-list.md")));
        var valueTile = DocumentationFolderFixture.FindSubjects(fixture.FindPage(GetPagePath("value-tile.md")));
        var indicator = DocumentationFolderFixture.FindSubjects(fixture.FindPage(GetPagePath("indicator.md")));
        var progress = DocumentationFolderFixture.FindSubjects(fixture.FindPage(GetPagePath("progress.md")));

        // Assert
        Assert.Equal((3, 1), (valueList.OfType<ValueList>().Count(), valueList.OfType<Progress>().Count()));
        Assert.Equal(3, valueTile.OfType<ValueTile>().Count());
        Assert.Equal([(140, 3)], valueTile.OfType<FlowLayout>().Select(flow => (flow.MinWidth, flow.Items.Length)));
        Assert.Equal([IndicatorKind.Chip, IndicatorKind.Icon, IndicatorKind.Icon, IndicatorKind.Chip], indicator.OfType<Indicator>().Select(widget => widget.Kind));
        Assert.Single(indicator.OfType<Card>());
        Assert.Equal(2, progress.OfType<Progress>().Count());
    }

    [Theory]
    [InlineData("value-list.md")]
    [InlineData("value-tile.md")]
    [InlineData("indicator.md")]
    [InlineData("progress.md")]
    public async Task WhenValueWidgetPageIsLoaded_ThenEveryValueWidgetReadsAPropertyOfASharedMotor(string fileName)
    {
        // Arrange
        using var fixture = await DocumentationFolderFixture.LoadAsync(WidgetsFolder);
        var resolver = fixture.Services.GetRequiredService<SubjectPathResolver>();

        // Act
        var page = fixture.FindPage(GetPagePath(fileName));

        // Assert
        var subjects = DocumentationFolderFixture.FindSubjects(page);
        var widgetPaths = subjects.OfType<ValueRow>().Select(row => (Subject: (IInterceptorSubject)row, row.Path))
            .Concat(subjects.OfType<ValueTile>().Select(tile => (Subject: (IInterceptorSubject)tile, tile.Path)))
            .Concat(subjects.OfType<Indicator>().Select(widget => (Subject: (IInterceptorSubject)widget, widget.Path)))
            .Concat(subjects.OfType<Progress>().Select(bar => (Subject: (IInterceptorSubject)bar, bar.Path)))
            .Where(widget => widget.Path is not null)
            .ToList();
        Assert.NotEmpty(widgetPaths);
        Assert.All(widgetPaths, widget => Assert.True(
            widget.Path!.StartsWith(PumpPath + "/", StringComparison.Ordinal) || widget.Path.StartsWith(FanPath + "/", StringComparison.Ordinal),
            widget.Path));
        Assert.All(widgetPaths, widget => Assert.NotNull(resolver.ResolveScopedProperty(widget.Path!, PathStyle.Canonical, widget.Subject)));
    }

    [Fact]
    public async Task WhenFlowLayoutPageIsLoaded_ThenTheFlowLayoutsHaveTheWidthsAndTheCellsOfTheirSamples()
    {
        // Arrange
        using var fixture = await DocumentationFolderFixture.LoadAsync(WidgetsFolder);

        // Act
        var page = fixture.FindPage(GetPagePath("flow-layout.md"));

        // Assert
        var subjects = DocumentationFolderFixture.FindSubjects(page);
        Assert.Equal([(320, 3), (120, 6), (600, 2)], subjects.OfType<FlowLayout>().Select(flow => (flow.MinWidth, flow.Items.Length)));
        Assert.Equal(["Pump", "Fan"], subjects.OfType<Widget>().Select(widget => Assert.IsType<Motor>(widget.ResolvedSubject).Name));
    }

    [Fact]
    public async Task WhenTabLayoutPageIsLoaded_ThenTheTabLayoutsHaveTheTabsAndThePositionsOfTheirSamples()
    {
        // Arrange
        using var fixture = await DocumentationFolderFixture.LoadAsync(WidgetsFolder);

        // Act
        var page = fixture.FindPage(GetPagePath("tab-layout.md"));

        // Assert
        var layouts = DocumentationFolderFixture.FindSubjects(page).OfType<TabLayout>().ToList();
        string[] expectedTitles =
        [
            "Pump (WaterDrop), Fan (Air), Notes ()",
            "Motors (Settings), About (Info)",
            "Pump (WaterDrop), Fan (Air)",
            "Cards (Dashboard), Text (Notes)",
            "Pump (), Fan ()",
            "Pump (WaterDrop), Fan (Air)"
        ];
        Assert.Equal(expectedTitles, layouts.Select(layout => string.Join(", ", layout.Tabs.Select(tab => $"{tab.Title} ({tab.Icon})"))));

        // Tabs inside tabs: the first tab of the second layout holds the third layout.
        Assert.Contains(layouts[2], Assert.IsType<MarkdownFragment>(layouts[1].Tabs[0].Content).Children.Values);

        // Cards in a flow inside a tab.
        var flow = Assert.Single(Assert.IsType<MarkdownFragment>(layouts[3].Tabs[0].Content).Children.Values.OfType<FlowLayout>());
        Assert.Equal((320, 2), (flow.MinWidth, flow.Items.Length));
        Assert.All(flow.Items, item => Assert.Single(Assert.IsType<MarkdownFragment>(item).Children.Values.OfType<Card>()));

        // A tab layout inside a card with a footer.
        var card = Assert.Single(DocumentationFolderFixture.FindSubjects(page).OfType<Card>(), card => card.Title == "Motors");
        Assert.Contains(layouts[4], Assert.IsType<MarkdownFragment>(card.Content).Children.Values);
        Assert.NotNull(card.Footer);

        Assert.Equal(
            [TabPosition.Top, TabPosition.Top, TabPosition.Top, TabPosition.Top, TabPosition.Top, TabPosition.Left],
            layouts.Select(layout => layout.Position));

        var notes = Assert.IsType<MarkdownFragment>(layouts[0].Tabs[2].Content);
        Assert.NotEmpty(notes.Children);
        Assert.All(notes.Children.Values, child => Assert.IsType<HtmlSegment>(child));

        // Every other tab has a name, which keeps it selected while the page is edited.
        IPathScope scope = page;
        string[] tabNames = ["pumptab", "fantab", "motorstab", "abouttab", "innerpump", "innerfan", "cardstab", "texttab", "cardpump", "cardfan", "sidepump", "sidefan"];
        IEnumerable<IInterceptorSubject?> namedTabs = layouts.SelectMany(layout => layout.Tabs).Where(tab => tab.Title != "Notes");
        Assert.Equal(namedTabs, tabNames.Select(scope.TryGetNamedSubject));
    }

    [Fact]
    public async Task WhenGridLayoutPageIsLoaded_ThenTheGridShowsTheMotorsAndItsLastCellHoldsText()
    {
        // Arrange
        using var fixture = await DocumentationFolderFixture.LoadAsync(WidgetsFolder);

        // Act
        IPathScope page = fixture.FindPage(GetPagePath("grid-layout.md"));

        // Assert
        var grid = Assert.IsType<GridLayout>(page.TryGetNamedSubject("motorgrid"));
        Assert.Equal((2, 2), (grid.Rows, grid.Columns));
        Assert.Equal(new (int?, int?, int)[] { (0, 0, 1), (0, 1, 1), (1, 0, 2) }, grid.Cells.Select(cell => (cell.Row, cell.Column, cell.ColumnSpan)));
        Assert.Equal(
            [PumpPath, FanPath],
            grid.Cells.Take(2).Select(cell => Assert.IsType<MarkdownFragment>(cell.Child).Children.Values.OfType<Widget>().Single().Path));

        var lastCell = Assert.IsType<MarkdownFragment>(grid.Cells[2].Child).Children.Values;
        Assert.Empty(lastCell.OfType<Widget>());
        Assert.Equal([$"{PumpPath}/TargetSpeed", $"{FanPath}/TargetSpeed"], lastCell.OfType<RenderExpression>().Select(expression => expression.Path));
    }

    [Fact]
    public async Task WhenCanvasLayoutPageIsLoaded_ThenTheCanvasShowsTheMotorsInNodesThatFitANarrowWindow()
    {
        // Arrange
        using var fixture = await DocumentationFolderFixture.LoadAsync(WidgetsFolder);

        // Act
        IPathScope page = fixture.FindPage(GetPagePath("canvas-layout.md"));

        // Assert
        var canvas = Assert.IsType<CanvasLayout>(page.TryGetNamedSubject("plan"));
        Assert.Equal(
            [PumpPath, FanPath],
            canvas.Nodes.Select(node => Assert.IsType<MarkdownFragment>(node.Child).Children.Values.OfType<Widget>().Single().Path));

        // The content of a page is this wide in a window of 390 pixels.
        const int contentWidthOnAPhone = 358;
        Assert.All(canvas.Nodes, node => Assert.InRange(node.X + node.Width, 0, contentWidthOnAPhone));
        Assert.All(canvas.Nodes, node => Assert.InRange(node.Y + node.Height, 0, canvas.MinHeight!.Value));
    }

    [Fact]
    public async Task WhenExpansionPanelsPageIsLoaded_ThenThePanelsHaveTheModesOfTheirSamplesAndUseTheSharedMap()
    {
        // Arrange
        using var fixture = await DocumentationFolderFixture.LoadAsync(WidgetsFolder);

        // Act
        var page = fixture.FindPage(GetPagePath("expansion-panels.md"));

        // Assert
        var subjects = DocumentationFolderFixture.FindSubjects(page);
        var panels = subjects.OfType<ExpansionPanels>().ToList();
        Assert.Equal([false, true], panels.Select(layout => layout.Single));
        Assert.All(panels, layout => Assert.Equal([true, false], layout.Panels.Select(panel => panel.Expanded)));
        Assert.Equal(2, subjects.OfType<ValueList>().Count());
        Assert.All(subjects.OfType<ValueRow>().Where(row => row.Map is not null), row => Assert.Equal(MapPath, row.Map));
    }

    // The path of a page of the folder in the storage, for example /administration/widgets/value-map.md.
    private static string GetPagePath(string fileName) => $"/{WidgetsFolder}/{fileName}";
}
