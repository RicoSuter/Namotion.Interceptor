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
/// Loads the reference page of the built-in page widgets as a whole with the widget types that ship.
/// Its samples are live and the page is part of an installation, so it must show what its samples
/// promise with nothing but the subjects that it creates itself.
/// </summary>
[Collection(MarkdownPageFixture.StorageCollection)]
public class PageWidgetsPageTests
{
    private const string PagePath = "src/HomeBlaze/HomeBlaze/Data/Files/Docs/administration/page-widgets.md";

    // The two motors that the page creates for its samples.
    private const string PumpName = "pump";
    private const string FanName = "fan";

    // The only path of the page that leads to nothing, in the sample that shows the message for it.
    private const string PathToNothing = "/Missing/Motor";

    [Fact]
    public async Task WhenPageIsLoaded_ThenNoBlockIsShownAsError()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateWithWidgetsAndSamplesAsync();

        // Act
        var page = await AddPageAsync(fixture);

        // Assert
        Assert.Empty(PageErrorBoxes.FindSortedByLine(page));
    }

    [Fact]
    public async Task WhenPageIsLoaded_ThenItsOnlyMotorsAreTheTwoThatTheSamplesShowByName()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateWithWidgetsAndSamplesAsync();

        // Act
        var page = await AddPageAsync(fixture);

        // Assert
        var pump = FindMotor(page, PumpName);
        var fan = FindMotor(page, FanName);
        Assert.Equal(("Pump", 1200), (pump.Name, pump.TargetSpeed));
        Assert.Equal(("Fan", 800), (fan.Name, fan.TargetSpeed));

        // A motor is a running background service, so no sample creates one of its own.
        Assert.Equal([pump, fan], FindSubjects(page).OfType<Motor>());
    }

    [Fact]
    public async Task WhenPageIsLoaded_ThenEveryWidgetShowsAMotorOfThePageExceptThePathThatLeadsNowhere()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateWithWidgetsAndSamplesAsync();

        // Act
        var page = await AddPageAsync(fixture);

        // Assert
        var widgets = FindSubjects(page).OfType<Widget>().ToList();
        Assert.Equal([PathToNothing], widgets.Where(widget => widget.ResolvedSubject is null).Select(widget => widget.Path));

        var shown = widgets.Where(widget => widget.Path != PathToNothing).ToList();
        Assert.Equal([FanName, PumpName], shown.Select(widget => widget.Path).Distinct().Order());
        Assert.All(shown, widget => Assert.Same(FindMotor(page, widget.Path), widget.ResolvedSubject));
    }

    [Fact]
    public async Task WhenPageIsLoaded_ThenEveryExpressionReadsFromAMotorOfThePage()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateWithWidgetsAndSamplesAsync();

        // Act
        var page = await AddPageAsync(fixture);

        // Assert
        var expressions = FindSubjects(page).OfType<RenderExpression>().ToList();
        Assert.NotEmpty(expressions);
        Assert.All(expressions, expression =>
        {
            Assert.Contains(expression.Path.Split('/')[0], new[] { PumpName, FanName });
            Assert.NotNull(expression.Value);
        });
    }

    [Fact]
    public async Task WhenPageIsLoaded_ThenTheCardsAreThoseOfTheSamples()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateWithWidgetsAndSamplesAsync();

        // Act
        var page = await AddPageAsync(fixture);

        // Assert
        var cards = FindSubjects(page).OfType<Card>().ToList();
        Assert.Equal(18, cards.Count);

        // The two samples of the card itself are the first cards of the page: one with everything, one plain.
        var fullCard = cards[0];
        var body = Assert.IsType<MarkdownFragment>(fullCard.Content).Children.Values;
        Assert.Equal(("Pump", "WaterDrop"), (fullCard.Title, fullCard.Icon));
        Assert.Contains(body, child => child is RenderExpression);
        Assert.Contains(body, child => child is Widget);
        Assert.Contains("Target speed", MarkdownPageFixture.Html(Assert.IsType<MarkdownFragment>(fullCard.Footer).Children));

        var plainCard = cards[1];
        Assert.Equal((null, null, null), (plainCard.Title, plainCard.Icon, plainCard.Footer));
        Assert.Contains("no title, no icon and no footer", MarkdownPageFixture.Html(Assert.IsType<MarkdownFragment>(plainCard.Content).Children));
        Assert.Same(plainCard, Assert.Single(cards, card => card.Title is null));
    }

    [Fact]
    public async Task WhenPageIsLoaded_ThenOneCardUsesEveryNewOptionAndTheHeaderSlot()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateWithWidgetsAndSamplesAsync();

        // Act
        var page = await AddPageAsync(fixture);

        // Assert
        var card = Assert.Single(FindSubjects(page).OfType<Card>(), card => card.Subtitle is not null);
        Assert.Equal(("Sample motor", "pages.md", MudBlazor.Color.Primary, false, 6), (card.Subtitle, card.Href, card.IconColor, card.Outlined, card.Elevation));
        var headerRight = Assert.IsType<MarkdownFragment>(card.HeaderRight);
        Assert.Contains(headerRight.Children.Values, child => child is RenderExpression { Path: "pump/Status" });
    }

    [Fact]
    public async Task WhenPageIsLoaded_ThenTheMapHasACasePerMotorStatusAndEveryWidgetRefersToIt()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateWithWidgetsAndSamplesAsync();

        // Act
        IPathScope page = await AddPageAsync(fixture);

        // Assert
        var map = Assert.IsType<ValueMap>(page.TryGetNamedSubject("motorstate"));
        Assert.Equal(Enum.GetNames<MotorStatus>(), map.Cases.Select(valueCase => valueCase.Value));
        Assert.All(map.Cases, valueCase => Assert.False(string.IsNullOrEmpty(valueCase.Text)));

        var subjects = FindSubjects((MarkdownFile)page);
        var maps = subjects.OfType<ValueRow>().Select(row => row.Map)
            .Concat(subjects.OfType<ValueTile>().Select(tile => tile.Map))
            .Concat(subjects.OfType<Indicator>().Select(indicator => indicator.Map))
            .Where(name => name is not null)
            .ToList();
        Assert.NotEmpty(maps);
        Assert.All(maps, name => Assert.Equal("motorstate", name));
    }

    [Fact]
    public async Task WhenPageIsLoaded_ThenEveryValueWidgetReadsAPropertyOfAMotorOfThePage()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateWithWidgetsAndSamplesAsync();
        var resolver = fixture.Services.GetRequiredService<SubjectPathResolver>();

        // Act
        var page = await AddPageAsync(fixture);

        // Assert
        var subjects = FindSubjects(page);
        var widgetPaths = subjects.OfType<ValueRow>().Select(row => (Subject: (IInterceptorSubject)row, row.Path))
            .Concat(subjects.OfType<ValueTile>().Select(tile => (Subject: (IInterceptorSubject)tile, tile.Path)))
            .Concat(subjects.OfType<Indicator>().Select(indicator => (Subject: (IInterceptorSubject)indicator, indicator.Path)))
            .Concat(subjects.OfType<Progress>().Select(progress => (Subject: (IInterceptorSubject)progress, progress.Path)))
            .Where(widget => widget.Path is not null)
            .ToList();

        Assert.NotEmpty(widgetPaths);
        Assert.All(widgetPaths, widget => Assert.NotNull(resolver.ResolveScopedProperty(widget.Path!, PathStyle.Canonical, widget.Subject)));
    }

    [Fact]
    public async Task WhenPageIsLoaded_ThenEachNewWidgetTypeHasTheSamplesOfItsSection()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateWithWidgetsAndSamplesAsync();

        // Act
        var page = await AddPageAsync(fixture);

        // Assert
        var subjects = FindSubjects(page);
        (Type Type, int Count)[] expectedCounts =
        [
            (typeof(ValueMap), 1), (typeof(ValueList), 5), (typeof(ValueTile), 3), (typeof(Indicator), 4),
            (typeof(Progress), 3), (typeof(ExpansionPanels), 2), (typeof(Alert), 2)
        ];
        Assert.Equal(expectedCounts, expectedCounts.Select(expected => (expected.Type, subjects.Count(subject => subject.GetType() == expected.Type))));

        var panels = subjects.OfType<ExpansionPanels>().ToList();
        Assert.Equal([false, true], panels.Select(layout => layout.Single));
        Assert.All(panels, layout => Assert.Equal([true, false], layout.Panels.Select(panel => panel.Expanded)));
        Assert.Equal([MudBlazor.Severity.Info, MudBlazor.Severity.Warning], subjects.OfType<Alert>().Select(alert => alert.Severity));
        Assert.Equal(MudBlazor.Variant.Outlined, subjects.OfType<Alert>().Last().Variant);
        Assert.Equal([IndicatorKind.Chip, IndicatorKind.Icon, IndicatorKind.Icon, IndicatorKind.Chip], subjects.OfType<Indicator>().Select(indicator => indicator.Kind));
    }

    [Fact]
    public async Task WhenPageIsLoaded_ThenTheFlowLayoutsHaveTheWidthsAndTheCellsOfTheirSamples()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateWithWidgetsAndSamplesAsync();

        // Act
        var page = await AddPageAsync(fixture);

        // Assert
        var flows = FindSubjects(page).OfType<FlowLayout>().Select(flow => (flow.MinWidth, flow.Items.Length));
        Assert.Equal([(320, 3), (120, 6), (600, 2), (320, 2), (140, 3)], flows);
    }

    [Fact]
    public async Task WhenPageIsLoaded_ThenTheTabLayoutsHaveTheTabsAndThePositionsOfTheirSamples()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateWithWidgetsAndSamplesAsync();

        // Act
        var page = await AddPageAsync(fixture);

        // Assert
        var layouts = FindSubjects(page).OfType<TabLayout>().ToList();
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
        Assert.All(flow.Items, item => Assert.Single(Assert.IsType<MarkdownFragment>(item).Children.Values.OfType<Card>()));

        // A tab layout inside a card with a footer.
        var card = Assert.Single(FindSubjects(page).OfType<Card>(), card => card.Title == "Motors");
        Assert.Contains(layouts[4], Assert.IsType<MarkdownFragment>(card.Content).Children.Values);
        Assert.NotNull(card.Footer);

        // Only the last layout has its tab bar at a side.
        Assert.Equal(
            [TabPosition.Top, TabPosition.Top, TabPosition.Top, TabPosition.Top, TabPosition.Top, TabPosition.Left],
            layouts.Select(layout => layout.Position));

        // A tab with plain content.
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
    public async Task WhenPageIsLoaded_ThenTheGridShowsTheMotorsByNameAndItsLastCellSpansTwoColumns()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateWithWidgetsAndSamplesAsync();

        // Act
        IPathScope page = await AddPageAsync(fixture);

        // Assert
        var grid = Assert.IsType<GridLayout>(page.TryGetNamedSubject("motorgrid"));
        Assert.Equal((2, 2), (grid.Rows, grid.Columns));
        Assert.Equal(
            new (int?, int?, int)[] { (0, 0, 1), (0, 1, 1), (1, 0, 2) },
            grid.Cells.Select(cell => (cell.Row, cell.Column, cell.ColumnSpan)));
        Assert.Equal(
            [PumpName, FanName],
            grid.Cells.Take(2).Select(cell => Assert.IsType<MarkdownFragment>(cell.Child).Children.Values.OfType<Widget>().Single().Path));

        // The last cell holds text with an expression per motor and no widget.
        var lastCell = Assert.IsType<MarkdownFragment>(grid.Cells[2].Child).Children.Values;
        Assert.Empty(lastCell.OfType<Widget>());
        Assert.Equal(
            [$"{PumpName}/TargetSpeed", $"{FanName}/TargetSpeed"],
            lastCell.OfType<RenderExpression>().Select(expression => expression.Path));
        Assert.Contains("The pump is set to", MarkdownPageFixture.Html(Assert.IsType<MarkdownFragment>(grid.Cells[2].Child).Children));
    }

    [Fact]
    public async Task WhenPageIsLoaded_ThenTheCanvasShowsTheMotorsByNameInNodesThatFitANarrowWindow()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateWithWidgetsAndSamplesAsync();

        // Act
        IPathScope page = await AddPageAsync(fixture);

        // Assert
        var canvas = Assert.IsType<CanvasLayout>(page.TryGetNamedSubject("plan"));
        Assert.Equal(
            [PumpName, FanName],
            canvas.Nodes.Select(node => Assert.IsType<MarkdownFragment>(node.Child).Children.Values.OfType<Widget>().Single().Path));

        // The content of a page is this wide in a window of 390 pixels.
        const int contentWidthOnAPhone = 358;
        Assert.All(canvas.Nodes, node => Assert.InRange(node.X + node.Width, 0, contentWidthOnAPhone));
        Assert.All(canvas.Nodes, node => Assert.InRange(node.Y + node.Height, 0, canvas.MinHeight!.Value));
    }

    private static Task<MarkdownFile> AddPageAsync(MarkdownPageFixture fixture) =>
        fixture.AddPageAsync("page-widgets.md", RepositoryPagesTests.ReadPage(PagePath));

    private static Motor FindMotor(IPathScope page, string name) => Assert.IsType<Motor>(page.TryGetNamedSubject(name));

    /// <summary>
    /// Gets the subjects of the page at every depth in the order of the page. What the cells of its grids
    /// and the nodes of its canvases hold is among them, because cells and nodes are slots.
    /// </summary>
    private static List<IInterceptorSubject> FindSubjects(MarkdownFile page) =>
        PageErrorBoxes.Flatten(page.Children.Values).ToList();
}
