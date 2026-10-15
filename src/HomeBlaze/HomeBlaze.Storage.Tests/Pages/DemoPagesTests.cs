using System.ComponentModel;
using System.Reflection;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Components;
using HomeBlaze.History.Blazor;
using HomeBlaze.Samples;
using HomeBlaze.Storage.Files;
using HomeBlaze.Storage.Internal;
using HomeBlaze.Storage.Pages;

namespace HomeBlaze.Storage.Tests.Pages;

/// <summary>
/// Loads the demo pages of the sample data with the widget types they use, so that a page which
/// stops working is found here and not by the person who opens it.
/// </summary>
[Collection(MarkdownPageFixture.StorageCollection)]
public class DemoPagesTests
{
    // The reader of the JSON adds its own text after this, which is not ours to pin.
    private const string BrokenJsonError = "The json block must contain a valid JSON object without duplicate keys.";

    // References.md and Dashboard.md are here as well: what a Widget block says about its path is shown by the
    // widget when it renders and is no block error, and the sample motors next to the pages are not part of the fixture.
    [Theory]
    [InlineData("Blocks.md")]
    [InlineData("Dashboard.md")]
    [InlineData("References.md")]
    [InlineData("Configuration.md")]
    public async Task WhenDemoPageIsLoaded_ThenNoBlockIsShownAsError(string fileName)
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateWithWidgetsAndSamplesAsync();

        // Act
        var page = await AddDemoPageAsync(fixture, fileName);

        // Assert
        Assert.Empty(PageErrorBoxes.FindMessages(page));
    }

    // A page that creates nothing has no error box either, so each page is also asked for subjects it names.
    [Theory]
    [InlineData("Blocks.md", "cardmotor", typeof(Motor))]
    [InlineData("Blocks.md", "exhaust", typeof(Tab))]
    [InlineData("Blocks.md", "closedmotor", typeof(Motor))]
    [InlineData("Blocks.md", "openmotor", typeof(Motor))]
    [InlineData("Dashboard.md", "dashboardmotor", typeof(Motor))]
    [InlineData("Dashboard.md", "dashboardgrid", typeof(GridLayout))]
    [InlineData("Dashboard.md", "dashboardcanvas", typeof(CanvasLayout))]
    [InlineData("References.md", "refmotor", typeof(Motor))]
    [InlineData("References.md", "depth", typeof(Tab))]
    [InlineData("References.md", "secondcard", typeof(Card))]
    [InlineData("Configuration.md", "valuemotor", typeof(Motor))]
    [InlineData("Configuration.md", "canvas", typeof(CanvasLayout))]
    [InlineData("Configuration.md", "note", typeof(Card))]
    [InlineData("Configuration.md", "cellgrid", typeof(GridLayout))]
    [InlineData("Configuration.md", "speed", typeof(GridCell))]
    [InlineData("Configuration.md", "keptmotor", typeof(Motor))]
    public async Task WhenDemoPageIsLoaded_ThenTheBlockWithANameCreatesItsSubject(string fileName, string name, Type expectedType)
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateWithWidgetsAndSamplesAsync();

        // Act
        IPathScope page = await AddDemoPageAsync(fixture, fileName);

        // Assert
        Assert.IsType(expectedType, page.TryGetNamedSubject(name));
    }

    [Fact]
    public async Task WhenDashboardPageIsLoaded_ThenItsTabsHoldTheLayoutsOfTheDashboard()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateWithWidgetsAndSamplesAsync();

        // Act
        var page = await AddDemoPageAsync(fixture, "Dashboard.md");

        // Assert
        var dashboard = Assert.Single(page.Children.Values.OfType<TabLayout>());
        Assert.Equal(["Devices", "Grid and Canvas", "On This Page"], dashboard.Tabs.Select(tab => tab.Title));
        Assert.Equal(4, PageErrorBoxes.Flatten(page.Children.Values).OfType<TabLayout>().Count());

        // Tabs three levels deep: the tab of the devices holds a tab layout, and its tab of the fans holds another one.
        var devices = Assert.Single(FindInTab<TabLayout>(dashboard.Tabs[0]));
        Assert.Equal(["Pumps", "Fans", "Conveyor", "Sample Devices"], devices.Tabs.Select(tab => tab.Title));
        var fans = Assert.Single(FindInTab<TabLayout>(devices.Tabs[1]));
        Assert.Equal(["Cooling Fan", "Exhaust Fan"], fans.Tabs.Select(tab => tab.Title));

        var gridAndCanvas = Assert.Single(FindInTab<TabLayout>(dashboard.Tabs[1]));
        Assert.Equal(["Grid", "Canvas"], gridAndCanvas.Tabs.Select(tab => tab.Title));
        var grid = Assert.Single(FindInTab<GridLayout>(gridAndCanvas.Tabs[0]));
        Assert.Equal((2, 3, 5), (grid.Rows, grid.Columns, grid.Cells.Length));
        var canvas = Assert.Single(FindInTab<CanvasLayout>(gridAndCanvas.Tabs[1]));
        Assert.Equal(3, canvas.Nodes.Length);

        // The motor that the page creates is shown once more by its name, in the same tab.
        var ownTab = PageErrorBoxes.Flatten([dashboard.Tabs[2]]).ToList();
        var motor = Assert.Single(ownTab.OfType<Motor>());
        Assert.Equal("Dashboard Motor", motor.Name);
        Assert.Same(motor, Assert.Single(ownTab.OfType<Widget>()).ResolvedSubject);
    }

    [Fact]
    public async Task WhenDashboardPageIsLoaded_ThenItUsesEveryValueWidgetAndEachMapOfAWidgetIsAMapOfThePage()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateWithWidgetsAndSamplesAsync();

        // Act
        var page = await AddDemoPageAsync(fixture, "Dashboard.md");

        // Assert
        var subjects = PageErrorBoxes.Flatten(page.Children.Values).ToList();
        var map = Assert.IsType<ValueMap>(((IPathScope)page).TryGetNamedSubject("motorstate"));
        Assert.Equal(Enum.GetNames<MotorStatus>(), map.Cases.Select(valueCase => valueCase.Value));

        var tiles = subjects.OfType<ValueTile>().ToList();
        Assert.Equal(5, tiles.Count);
        Assert.All(tiles, tile => Assert.StartsWith("/Demo/", tile.Path));

        var cards = subjects.OfType<Card>().Where(card => card.Subtitle is not null).ToList();
        Assert.Equal(5, cards.Count);
        Assert.All(cards, card => Assert.Single(Assert.IsType<MarkdownFragment>(card.HeaderRight).Children.Values.OfType<Indicator>()));

        var panels = Assert.Single(subjects.OfType<ExpansionPanels>());
        Assert.Equal(["Drives", "Fans", "All Motors"], panels.Panels.Select(panel => panel.Title));
        Assert.Equal([true, false, false], panels.Panels.Select(panel => panel.Expanded));

        Assert.NotEmpty(subjects.OfType<ValueList>());
        Assert.NotEmpty(subjects.OfType<Progress>());
        Assert.NotEmpty(subjects.OfType<Alert>());

        var modeMap = Assert.IsType<ValueMap>(((IPathScope)page).TryGetNamedSubject("motormode"));
        Assert.Equal(Enum.GetNames<MotorMode>(), modeMap.Cases.Select(valueCase => valueCase.Value));

        var maps = subjects.OfType<ValueRow>().Select(row => row.Map)
            .Concat(subjects.OfType<Indicator>().Select(indicator => indicator.Map))
            .Concat(subjects.OfType<Select>().Select(control => control.Map))
            .Concat(subjects.OfType<SubjectTableColumn>().Select(column => column.Map))
            .Where(name => name is not null);
        Assert.All(maps, name => Assert.Contains(name, new[] { "motorstate", "motormode" }));
    }

    [Fact]
    public async Task WhenDashboardPageIsLoaded_ThenItShowsEveryWidgetThatShips()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateWithWidgetsAndSamplesAsync();
        var widgetTypes = typeof(Card).Assembly.GetTypes()
            .Where(type => type.GetCustomAttribute<CategoryAttribute>()?.Category == "Dashboard"
                && type.GetCustomAttribute<ExcludeFromCreationAttribute>() is null)
            .Append(typeof(HistoryChart))
            .ToList();

        // Act
        var page = await AddDemoPageAsync(fixture, "Dashboard.md");

        // Assert
        var subjects = PageErrorBoxes.Flatten(page.Children.Values).ToList();
        var shownTypes = subjects.Select(subject => subject.GetType()).ToHashSet();
        Assert.Contains(widgetTypes, type => type.Name == nameof(Responsive));
        Assert.Empty(widgetTypes.Where(type => !shownTypes.Contains(type)).Select(type => type.Name));

        IPathScope scope = page;
        Assert.IsType<HistoryChart>(scope.TryGetNamedSubject("trends"));
        Assert.IsType<SubjectTable>(scope.TryGetNamedSubject("motortable"));
        Assert.Equal(5, Assert.IsType<FlowLayout>(scope.TryGetNamedSubject("controlroom")).Items.Length);
        Assert.Equal([ResponsiveShow.Phone, ResponsiveShow.NotPhone], subjects.OfType<Responsive>().Select(block => block.Show));
    }

    [Fact]
    public async Task WhenDashboardPageIsLoaded_ThenItsControlsWriteNoConfigurationOfTheDemoMotors()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateWithWidgetsAndSamplesAsync();

        // Act
        var page = await AddDemoPageAsync(fixture, "Dashboard.md");

        // Assert: development runs against the checked-in files, which a control that saves would change.
        var subjects = PageErrorBoxes.Flatten(page.Children.Values).ToList();
        var writtenPaths = subjects.OfType<Switch>().Select(control => control.Path)
            .Concat(subjects.OfType<Slider>().Select(control => control.Path))
            .Concat(subjects.OfType<Select>().Select(control => control.Path))
            .ToList();
        Assert.Equal(15, writtenPaths.Count);
        Assert.All(writtenPaths, path =>
        {
            Assert.NotNull(path);
            Assert.StartsWith("/Demo/", path);
            var property = typeof(Motor).GetProperty(path[(path.LastIndexOf('/') + 1)..]);
            Assert.NotNull(property);
            Assert.Null(property.GetCustomAttribute<ConfigurationAttribute>());
        });
        Assert.All(subjects.OfType<Switch>(), control =>
        {
            Assert.NotNull(control.Path);
            var motor = control.Path[..control.Path.LastIndexOf('/')];
            Assert.Equal(($"{motor}/PowerOn", $"{motor}/PowerOff"), (control.On, control.Off));
        });
    }

    [Fact]
    public async Task WhenErrorsPageIsLoaded_ThenEverySectionShowsItsOwnError()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateWithWidgetsAndSamplesAsync();

        // Act
        var page = await AddDemoPageAsync(fixture, "Errors.md");

        // Assert
        var strayCardLine = FindBlockLine("Errors.md", "Stray Closing Line");
        var itemLine = FindBlockLine("Errors.md", "Short Card") - 1;
        string[] expectedMessages =
        [
            "Unknown type 'HomeBlaze.Components.Crad'. Use the full type name including the namespace.",
            "The type 'HomeBlaze.Storage.Files.MarkdownFile' cannot be used as a block because it does not implement IConfigurable.",
            "'Card' is not a type name. A slot block is only allowed directly inside a widget block.",
            "Card has no configuration property 'Titel'. Available: AspectRatio, Elevation, Height, Href, Icon, IconColor, Image, Outlined, Subtitle, Title.",
            "Card has no configuration property 'title'. Did you mean 'Title'? Available: AspectRatio, Elevation, Height, Href, Icon, IconColor, Image, Outlined, Subtitle, Title.",
            "'fast' is not a valid value for 'TargetSpeed'. Expected a value of type Int32.",
            "Card has no slot 'Footr'. Available: Content, Footer, HeaderRight.",
            "Motor has no default slot, so it cannot have content.",
            "The slot 'Footer' takes one block, but there are 2.",
            "The name 'twin' is used twice on this page.",
            "This line of 3 ':' characters closes no block. A closing line needs as many characters as the head line of its block. " +
            $"Open here: line {strayCardLine} (2 characters).",
            "Widget has no default slot, so it cannot have content. The content is inside this block because the block is still open there. " +
            "A closing line ':::' before the content ends the block.",
            $"This block has as many ':' characters as the slot block 'Item' at line {itemLine}, so it is not inside that block " +
            "but directly inside FlowLayout, which has no default slot. Write '::::' here to put it inside 'Item'.",
            "Expected key=value but found 'that this line is text'.",
            "A block is only allowed directly on the page or directly inside another block.",
            BrokenJsonError,
            "The subject(name) fence is no longer supported. Write the subject as a block: a line with :: Full.Type.Name#name key=value, then a closing :: line."
        ];

        // The order is that of the page, because every error of this page is in a section of its own.
        var messages = PageErrorBoxes.FindMessages(page)
            .Select(message => message.StartsWith(BrokenJsonError, StringComparison.Ordinal) ? BrokenJsonError : message);

        Assert.Equal(expectedMessages, messages);
    }

    [Fact]
    public async Task WhenErrorsPageIsLoaded_ThenTheBlocksAroundAFenceErrorAreStillShown()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateWithWidgetsAndSamplesAsync();

        // Act
        var page = await AddDemoPageAsync(fixture, "Errors.md");

        // Assert
        var cards = page.Children.Values.OfType<Card>().ToDictionary(card => card.Title!);
        var strayCard = MarkdownPageFixture.Html(((MarkdownFragment)cards["Stray Closing Line"].Content!).Children);
        Assert.Contains("The first paragraph of the card.", strayCard);
        Assert.Contains("closes no block", strayCard);
        Assert.Contains("The second paragraph of the card.", strayCard);

        var openWidgetCard = MarkdownPageFixture.Html(((MarkdownFragment)cards["Open Widget"].Content!).Children);
        Assert.Contains("Widget has no default slot", openWidgetCard);
    }

    [Fact]
    public async Task WhenErrorsPageIsLoaded_ThenTheMistakesWithoutABoxShowTheirBlocksAtTheWrongPlace()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateWithWidgetsAndSamplesAsync();

        // Act
        var page = await AddDemoPageAsync(fixture, "Errors.md");

        // Assert
        var cards = page.Children.Values.OfType<Card>().ToDictionary(card => card.Title!);

        // One colon too few: the motor is beside the card and not in it.
        var outerCard = Assert.IsType<MarkdownFragment>(cards["Outer Card"].Content);
        Assert.Equal("\n<p>The body of the card.</p>\n", MarkdownPageFixture.Html(outerCard.Children));
        Assert.All(outerCard.Children.Values, child => Assert.IsType<HtmlSegment>(child));
        Assert.Equal("Short Motor", Assert.IsType<Motor>(page.Children["shortmotor"]).Name);

        // A forgotten closing line: the text for the page is in the card, up to the head line of the next card.
        var openCard = MarkdownPageFixture.Html(Assert.IsType<MarkdownFragment>(cards["Open Card"].Content).Children);
        Assert.Contains("This sentence was meant for the page and is shown in the card.", openCard);
        Assert.DoesNotContain("The head line of this card ends the card above.", openCard);
        Assert.Contains(
            "The head line of this card ends the card above.",
            MarkdownPageFixture.Html(Assert.IsType<MarkdownFragment>(cards["Next Card"].Content).Children));

        // A line of text that is read as a head line: it ended the card, and its box hides the body.
        Assert.Null(cards["Lunch Plan"].Content);
    }

    [Fact]
    public async Task WhenErrorsPageIsLoaded_ThenTheBlocksWithoutAnErrorAreCreated()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateWithWidgetsAndSamplesAsync();

        // Act
        var page = await AddDemoPageAsync(fixture, "Errors.md");

        // Assert
        var healthy = Assert.IsType<Motor>(page.Children["healthy"]);
        Assert.Equal("Healthy Motor", healthy.Name);

        // The healthy block is the last block of the page text, so it comes after every error.
        var lastBlock = MarkdownPageParser.Parse(ReadDemoPage("Errors.md")).AllBlocks[^1];
        Assert.Equal("healthy", lastBlock.Head?.Id);
        Assert.Same(healthy, page.GetBlockSubject(lastBlock.Index));

        // The first of two blocks with the same name keeps the name.
        Assert.Equal("First Twin", Assert.IsType<Motor>(page.Children["twin"]).Name);
    }

    private static Task<MarkdownFile> AddDemoPageAsync(MarkdownPageFixture fixture, string fileName) =>
        fixture.AddPageAsync(fileName, ReadDemoPage(fileName));

    private static string ReadDemoPage(string fileName) =>
        RepositoryPagesTests.ReadPage(RepositoryPagesTests.DemoFolder + fileName);

    /// <summary>Gets the blocks of this type that stand directly in the tab.</summary>
    private static IEnumerable<T> FindInTab<T>(Tab tab) =>
        Assert.IsType<MarkdownFragment>(tab.Content).Children.Values.OfType<T>();

    /// <summary>Gets the 1-based line of the block of the page that has this title. A code fence shows no block.</summary>
    private static int FindBlockLine(string fileName, string title) =>
        MarkdownPageParser.Parse(ReadDemoPage(fileName)).AllBlocks
            .Single(block => block.Head is not null && block.Head.Arguments.Any(argument => argument.Key == "Title" && argument.Value == title))
            .Line;
}
