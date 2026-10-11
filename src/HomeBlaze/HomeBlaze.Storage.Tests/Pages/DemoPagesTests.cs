using HomeBlaze.Abstractions;
using HomeBlaze.Components;
using HomeBlaze.Samples;
using HomeBlaze.Services;
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

    // References.md is here as well: what a Widget block says about its path is shown by the widget when it
    // renders and is no block error, and the sample motors next to the pages are not part of the fixture.
    [Theory]
    [InlineData("Blocks.md")]
    [InlineData("Layouts.md")]
    [InlineData("References.md")]
    [InlineData("Configuration.md")]
    public async Task WhenDemoPageIsLoaded_ThenNoBlockIsShownAsError(string fileName)
    {
        // Arrange
        var fixture = await CreateFixtureAsync();

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
    [InlineData("Layouts.md", "notes", typeof(Tab))]
    [InlineData("Layouts.md", "pumps", typeof(Tab))]
    [InlineData("Layouts.md", "cardexhaust", typeof(Tab))]
    [InlineData("References.md", "refmotor", typeof(Motor))]
    [InlineData("References.md", "depth", typeof(Tab))]
    [InlineData("References.md", "secondcard", typeof(Card))]
    [InlineData("Configuration.md", "valuemotor", typeof(Motor))]
    [InlineData("Configuration.md", "canvas", typeof(CanvasLayout))]
    [InlineData("Configuration.md", "grid", typeof(GridLayout))]
    [InlineData("Configuration.md", "keptmotor", typeof(Motor))]
    public async Task WhenDemoPageIsLoaded_ThenTheBlockWithANameCreatesItsSubject(string fileName, string name, Type expectedType)
    {
        // Arrange
        var fixture = await CreateFixtureAsync();

        // Act
        IPathScope page = await AddDemoPageAsync(fixture, fileName);

        // Assert
        Assert.IsType(expectedType, page.TryGetNamedSubject(name));
    }

    [Fact]
    public async Task WhenErrorsPageIsLoaded_ThenEverySectionShowsItsOwnError()
    {
        // Arrange
        var fixture = await CreateFixtureAsync();

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
            "Card has no configuration property 'Titel'. Available: Icon, Title.",
            "Card has no configuration property 'title'. Did you mean 'Title'? Available: Icon, Title.",
            "'fast' is not a valid value for 'TargetSpeed'. Expected a value of type Int32.",
            "Card has no slot 'Footr'. Available: Content, Footer.",
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
        var fixture = await CreateFixtureAsync();

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
        var fixture = await CreateFixtureAsync();

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
        var fixture = await CreateFixtureAsync();

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

    private static Task<MarkdownPageFixture> CreateFixtureAsync() =>
        MarkdownPageFixture.CreateAsync(services =>
        {
            // The type registry reads the provider on first use, so the assemblies are added before anything is created.
            var typeProvider = (TypeProvider)services
                .Single(service => service.ServiceType == typeof(TypeProvider))
                .ImplementationInstance!;

            typeProvider
                .AddAssembly(typeof(Card).Assembly)
                .AddAssembly(typeof(Motor).Assembly);
        });

    private static Task<MarkdownFile> AddDemoPageAsync(MarkdownPageFixture fixture, string fileName) =>
        fixture.AddPageAsync(fileName, ReadDemoPage(fileName));

    private static string ReadDemoPage(string fileName) =>
        RepositoryPagesTests.ReadPage(RepositoryPagesTests.DemoFolder + fileName);

    /// <summary>Gets the 1-based line of the block of the page that has this title. A code fence shows no block.</summary>
    private static int FindBlockLine(string fileName, string title) =>
        MarkdownPageParser.Parse(ReadDemoPage(fileName)).AllBlocks
            .Single(block => block.Head is not null && block.Head.Arguments.Any(argument => argument.Key == "Title" && argument.Value == title))
            .Line;
}
