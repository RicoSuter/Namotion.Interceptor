using HomeBlaze.Abstractions;
using HomeBlaze.Components;
using HomeBlaze.Samples;
using HomeBlaze.Storage.Files;
using HomeBlaze.Storage.Internal;
using HomeBlaze.Storage.Pages;
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
        var fixture = await DocumentationSamplesTests.CreateFixtureAsync();

        // Act
        var page = await AddPageAsync(fixture);

        // Assert
        Assert.Empty(PageErrorBoxes.FindSortedByLine(page));
    }

    [Fact]
    public async Task WhenPageIsLoaded_ThenItsOnlyMotorsAreTheTwoThatTheSamplesShowByName()
    {
        // Arrange
        var fixture = await DocumentationSamplesTests.CreateFixtureAsync();

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
        var fixture = await DocumentationSamplesTests.CreateFixtureAsync();

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
        var fixture = await DocumentationSamplesTests.CreateFixtureAsync();

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
        var fixture = await DocumentationSamplesTests.CreateFixtureAsync();

        // Act
        var page = await AddPageAsync(fixture);

        // Assert
        var cards = FindSubjects(page).OfType<Card>().ToList();
        Assert.Equal(16, cards.Count);

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
    public async Task WhenPageIsLoaded_ThenTheFlowLayoutsHaveTheWidthsAndTheCellsOfTheirSamples()
    {
        // Arrange
        var fixture = await DocumentationSamplesTests.CreateFixtureAsync();

        // Act
        var page = await AddPageAsync(fixture);

        // Assert
        var flows = FindSubjects(page).OfType<FlowLayout>().Select(flow => (flow.MinWidth, flow.Items.Length));
        Assert.Equal([(320, 3), (120, 6), (600, 2), (320, 2)], flows);
    }

    [Fact]
    public async Task WhenPageIsLoaded_ThenTheTabLayoutsHaveTheTabsOfTheirSamples()
    {
        // Arrange
        var fixture = await DocumentationSamplesTests.CreateFixtureAsync();

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
            "Pump (), Fan ()"
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

        // A tab with plain content.
        var notes = Assert.IsType<MarkdownFragment>(layouts[0].Tabs[2].Content);
        Assert.All(notes.Children.Values, child => Assert.IsType<HtmlSegment>(child));

        // Every other tab has a name, which keeps it selected while the page is edited.
        IPathScope scope = page;
        string[] tabNames = ["pumptab", "fantab", "motorstab", "abouttab", "innerpump", "innerfan", "cardstab", "texttab", "cardpump", "cardfan"];
        IEnumerable<IInterceptorSubject?> namedTabs = layouts.SelectMany(layout => layout.Tabs).Where(tab => tab.Title != "Notes");
        Assert.Equal(namedTabs, tabNames.Select(scope.TryGetNamedSubject));
    }

    [Fact]
    public async Task WhenPageIsLoaded_ThenTheGridShowsTheMotorsByNameAndItsLastCellSpansTwoColumns()
    {
        // Arrange
        var fixture = await DocumentationSamplesTests.CreateFixtureAsync();

        // Act
        IPathScope page = await AddPageAsync(fixture);

        // Assert
        var grid = Assert.IsType<GridLayout>(page.TryGetNamedSubject("motorgrid"));
        Assert.Equal((2, 2), (grid.Rows, grid.Columns));
        Assert.Equal(
            [(0, 0, 1, PumpName), (0, 1, 1, FanName), (1, 0, 2, PumpName)],
            grid.Cells.Select(cell => (cell.Row, cell.Column, cell.ColumnSpan, Assert.IsType<Widget>(cell.Child).Path)));
    }

    [Fact]
    public async Task WhenPageIsLoaded_ThenTheCanvasShowsTheMotorsByNameInNodesThatFitANarrowWindow()
    {
        // Arrange
        var fixture = await DocumentationSamplesTests.CreateFixtureAsync();

        // Act
        IPathScope page = await AddPageAsync(fixture);

        // Assert
        var canvas = Assert.IsType<CanvasLayout>(page.TryGetNamedSubject("plan"));
        Assert.Equal([PumpName, FanName], canvas.Nodes.Select(node => Assert.IsType<Widget>(node.Child).Path));

        // The content of a page is 358 pixels wide in a window of 390 pixels.
        Assert.All(canvas.Nodes, node => Assert.InRange(node.X + node.Width, 0, 358));
        Assert.All(canvas.Nodes, node => Assert.InRange(node.Y + node.Height, 0, canvas.MinHeight!.Value));
    }

    private static Task<MarkdownFile> AddPageAsync(MarkdownPageFixture fixture) =>
        fixture.AddPageAsync("page-widgets.md", RepositoryPagesTests.ReadPage(PagePath));

    private static Motor FindMotor(IPathScope page, string name) => Assert.IsType<Motor>(page.TryGetNamedSubject(name));

    /// <summary>
    /// Gets the subjects of the page at every depth in the order of the page, followed by the subjects
    /// that its grids and canvases hold in their configuration.
    /// </summary>
    private static List<IInterceptorSubject> FindSubjects(MarkdownFile page)
    {
        var subjects = PageErrorBoxes.Flatten(page.Children.Values).ToList();
        var configured = subjects
            .SelectMany(subject => subject switch
            {
                GridLayout grid => grid.Cells.Select(cell => cell.Child),
                CanvasLayout canvas => canvas.Nodes.Select(node => node.Child),
                _ => []
            })
            .OfType<IInterceptorSubject>()
            .ToList();

        return [.. subjects, .. configured];
    }
}
