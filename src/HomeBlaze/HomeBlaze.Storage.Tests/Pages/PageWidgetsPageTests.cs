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
/// Loads the reference page of the built-in page widgets with the folder around it, as the application loads it,
/// and with the widget types that ship. Its samples are live and the page is part of an installation, so it must
/// show what its samples promise with nothing but itself and the sample motors of the widgets folder.
/// </summary>
[Collection(MarkdownPageFixture.StorageCollection)]
public class PageWidgetsPageTests
{
    private const string Folder = "administration";
    private const string PagePath = "/administration/page-widgets.md";

    // The two sample motors of the widgets folder, as the samples write them and as they are found from the root.
    private const string PumpPath = "../widgets/Pump";
    private const string FanPath = "../widgets/Fan";
    private const string PumpAbsolutePath = "/administration/widgets/Pump";
    private const string FanAbsolutePath = "/administration/widgets/Fan";

    // The only path of the page that leads to nothing, in the sample that shows the message for it.
    private const string PathToNothing = "/Missing/Motor";

    [Fact]
    public async Task WhenPageIsLoaded_ThenNoBlockIsShownAsError()
    {
        // Arrange
        using var fixture = await DocumentationFolderFixture.LoadAsync(Folder);

        // Act
        var page = fixture.FindPage(PagePath);

        // Assert
        Assert.Empty(PageErrorBoxes.FindSortedByLine(page));
    }

    [Fact]
    public async Task WhenPageIsLoaded_ThenItCreatesNoMotorAndTheSamplesUseTheTwoMotorsOfTheWidgetsFolder()
    {
        // Arrange
        using var fixture = await DocumentationFolderFixture.LoadAsync(Folder);

        // Act
        var page = fixture.FindPage(PagePath);

        // Assert
        var pump = Assert.IsType<Motor>(fixture.FindSubject(PumpAbsolutePath));
        var fan = Assert.IsType<Motor>(fixture.FindSubject(FanAbsolutePath));
        Assert.Equal(("Pump", 1200), (pump.Name, pump.TargetSpeed));
        Assert.Equal(("Fan", 800), (fan.Name, fan.TargetSpeed));

        // A motor is a running background service, so no sample creates one of its own.
        Assert.Empty(DocumentationFolderFixture.FindSubjects(page).OfType<Motor>());
    }

    [Fact]
    public async Task WhenPageIsLoaded_ThenEveryWidgetShowsASampleMotorExceptThePathThatLeadsNowhere()
    {
        // Arrange
        using var fixture = await DocumentationFolderFixture.LoadAsync(Folder);

        // Act
        var page = fixture.FindPage(PagePath);

        // Assert
        var widgets = DocumentationFolderFixture.FindSubjects(page).OfType<Widget>().ToList();
        Assert.Equal([PathToNothing], widgets.Where(widget => widget.ResolvedSubject is null).Select(widget => widget.Path));

        var shown = widgets.Where(widget => widget.Path != PathToNothing).ToList();
        Assert.Equal([FanPath, PumpPath], shown.Select(widget => widget.Path).Distinct().Order());
        Assert.All(shown, widget => Assert.Same(FindMotor(fixture, widget.Path), widget.ResolvedSubject));
    }

    [Fact]
    public async Task WhenPageIsLoaded_ThenEveryExpressionReadsFromASampleMotor()
    {
        // Arrange
        using var fixture = await DocumentationFolderFixture.LoadAsync(Folder);

        // Act
        var page = fixture.FindPage(PagePath);

        // Assert
        var expressions = DocumentationFolderFixture.FindSubjects(page).OfType<RenderExpression>().ToList();
        Assert.NotEmpty(expressions);
        Assert.All(expressions, expression =>
        {
            Assert.True(
                expression.Path.StartsWith(PumpPath + "/", StringComparison.Ordinal) ||
                expression.Path.StartsWith(FanPath + "/", StringComparison.Ordinal),
                expression.Path);
            Assert.NotNull(expression.Value);
        });
    }

    [Fact]
    public async Task WhenPageIsLoaded_ThenTheCardsAreThoseOfTheSamples()
    {
        // Arrange
        using var fixture = await DocumentationFolderFixture.LoadAsync(Folder);

        // Act
        var page = fixture.FindPage(PagePath);

        // Assert
        var cards = DocumentationFolderFixture.FindSubjects(page).OfType<Card>().ToList();
        Assert.Equal(22, cards.Count);

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
        using var fixture = await DocumentationFolderFixture.LoadAsync(Folder);

        // Act
        var page = fixture.FindPage(PagePath);

        // Assert
        var card = Assert.Single(DocumentationFolderFixture.FindSubjects(page).OfType<Card>(), card => card.Subtitle is not null);
        Assert.Equal(("Sample motor", "pages.md", MudBlazor.Color.Primary, false, 6), (card.Subtitle, card.Href, card.IconColor, card.Outlined, card.Elevation));
        var headerRight = Assert.IsType<MarkdownFragment>(card.HeaderRight);
        Assert.Contains(headerRight.Children.Values, child => child is RenderExpression { Path: PumpPath + "/Status" });
    }

    [Fact]
    public async Task WhenPageIsLoaded_ThenThreeCardsHaveAnAspectRatioAndOneHasAHeight()
    {
        // Arrange
        using var fixture = await DocumentationFolderFixture.LoadAsync(Folder);

        // Act
        var page = fixture.FindPage(PagePath);

        // Assert
        var cards = DocumentationFolderFixture.FindSubjects(page).OfType<Card>().ToList();
        Assert.Equal(["Pump", "Fan", "Notes"], cards.Where(card => card.AspectRatio is not null).Select(card => card.Title));
        Assert.All(cards.Where(card => card.AspectRatio is not null), card => Assert.Equal("4:3", card.AspectRatio));
        var sized = Assert.Single(cards, card => card.Height is not null);
        Assert.Equal(200, sized.Height);
        Assert.Contains(Assert.IsType<MarkdownFragment>(sized.Content).Children.Values, child => child is Widget);
        Assert.NotNull(sized.Footer);
    }

    [Fact]
    public async Task WhenPageIsLoaded_ThenTheMapHasACasePerMotorStatusAndEveryWidgetRefersToIt()
    {
        // Arrange
        using var fixture = await DocumentationFolderFixture.LoadAsync(Folder);

        // Act
        IPathScope page = fixture.FindPage(PagePath);

        // Assert
        var map = Assert.IsType<ValueMap>(page.TryGetNamedSubject("motorstate"));
        Assert.Equal(Enum.GetNames<MotorStatus>(), map.Cases.Select(valueCase => valueCase.Value));
        Assert.All(map.Cases, valueCase => Assert.False(string.IsNullOrEmpty(valueCase.Text)));

        var subjects = DocumentationFolderFixture.FindSubjects((MarkdownFile)page);
        var maps = subjects.OfType<ValueRow>().Select(row => row.Map)
            .Concat(subjects.OfType<ValueTile>().Select(tile => tile.Map))
            .Concat(subjects.OfType<Indicator>().Select(indicator => indicator.Map))
            .Where(name => name is not null)
            .ToList();
        Assert.NotEmpty(maps);
        Assert.All(maps, name => Assert.Equal("motorstate", name));
    }

    [Fact]
    public async Task WhenPageIsLoaded_ThenEveryValueWidgetReadsAPropertyOfASampleMotor()
    {
        // Arrange
        using var fixture = await DocumentationFolderFixture.LoadAsync(Folder);
        var resolver = fixture.Services.GetRequiredService<SubjectPathResolver>();

        // Act
        var page = fixture.FindPage(PagePath);

        // Assert
        var subjects = DocumentationFolderFixture.FindSubjects(page);
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
        using var fixture = await DocumentationFolderFixture.LoadAsync(Folder);

        // Act
        var page = fixture.FindPage(PagePath);

        // Assert
        var subjects = DocumentationFolderFixture.FindSubjects(page);
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
        using var fixture = await DocumentationFolderFixture.LoadAsync(Folder);

        // Act
        var page = fixture.FindPage(PagePath);

        // Assert
        var flows = DocumentationFolderFixture.FindSubjects(page).OfType<FlowLayout>().Select(flow => (flow.MinWidth, flow.Items.Length));
        Assert.Equal([(320, 3), (320, 3), (120, 6), (600, 2), (320, 2), (140, 3)], flows);
    }

    [Fact]
    public async Task WhenPageIsLoaded_ThenTheTabLayoutsHaveTheTabsAndThePositionsOfTheirSamples()
    {
        // Arrange
        using var fixture = await DocumentationFolderFixture.LoadAsync(Folder);

        // Act
        var page = fixture.FindPage(PagePath);

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
        Assert.All(flow.Items, item => Assert.Single(Assert.IsType<MarkdownFragment>(item).Children.Values.OfType<Card>()));

        // A tab layout inside a card with a footer.
        var card = Assert.Single(DocumentationFolderFixture.FindSubjects(page).OfType<Card>(), card => card.Title == "Motors");
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
    public async Task WhenPageIsLoaded_ThenTheGridShowsTheSampleMotorsAndItsLastCellSpansTwoColumns()
    {
        // Arrange
        using var fixture = await DocumentationFolderFixture.LoadAsync(Folder);

        // Act
        IPathScope page = fixture.FindPage(PagePath);

        // Assert
        var grid = Assert.IsType<GridLayout>(page.TryGetNamedSubject("motorgrid"));
        Assert.Equal((2, 2), (grid.Rows, grid.Columns));
        Assert.Equal(
            new (int?, int?, int)[] { (0, 0, 1), (0, 1, 1), (1, 0, 2) },
            grid.Cells.Select(cell => (cell.Row, cell.Column, cell.ColumnSpan)));
        Assert.Equal(
            [PumpPath, FanPath],
            grid.Cells.Take(2).Select(cell => Assert.IsType<MarkdownFragment>(cell.Child).Children.Values.OfType<Widget>().Single().Path));

        // The last cell holds text with an expression per motor and no widget.
        var lastCell = Assert.IsType<MarkdownFragment>(grid.Cells[2].Child).Children.Values;
        Assert.Empty(lastCell.OfType<Widget>());
        Assert.Equal(
            [$"{PumpPath}/TargetSpeed", $"{FanPath}/TargetSpeed"],
            lastCell.OfType<RenderExpression>().Select(expression => expression.Path));
        Assert.Contains("The pump is set to", MarkdownPageFixture.Html(Assert.IsType<MarkdownFragment>(grid.Cells[2].Child).Children));
    }

    [Fact]
    public async Task WhenPageIsLoaded_ThenTheCanvasShowsTheSampleMotorsInNodesThatFitANarrowWindow()
    {
        // Arrange
        using var fixture = await DocumentationFolderFixture.LoadAsync(Folder);

        // Act
        IPathScope page = fixture.FindPage(PagePath);

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

    private static Motor FindMotor(DocumentationFolderFixture fixture, string samplePath) =>
        Assert.IsType<Motor>(fixture.FindSubject(samplePath == PumpPath ? PumpAbsolutePath : FanAbsolutePath));
}
