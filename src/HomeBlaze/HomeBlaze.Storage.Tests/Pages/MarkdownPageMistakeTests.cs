using HomeBlaze.Components;
using HomeBlaze.Storage.Internal;
using HomeBlaze.Storage.Pages;

namespace HomeBlaze.Storage.Tests.Pages;

/// <summary>
/// Ordinary mistakes of a page author, loaded with the widget types that ship: each one must show the
/// message of the mistake at the line of the mistake.
/// </summary>
[Collection(MarkdownPageFixture.StorageCollection)]
public class MarkdownPageMistakeTests
{
    private const string TestNamespace = "HomeBlaze.Storage.Tests.Pages";

    private const string HeadError = "Expected key=value but found 'Title = \"x\"'.";

    public static TheoryData<string, string[]> InvalidBlocksInAWidgetWithoutDefaultSlot() => new()
    {
        {
            // A head line that does not parse.
            """
            :: HomeBlaze.Components.TabLayout
            ::: Tab Title = "x"
            one
            :::
            ::
            """,
            [$"Line 4: {HeadError}"]
        },
        {
            // An item that is one character short on both of its lines.
            """
            :: HomeBlaze.Components.FlowLayout
            :: Item
            one
            ::
            ::: Item
            two
            :::
            ::
            """,
            [
                "Line 4: A block inside another block needs more ':' characters than the block around it. " +
                "This block has 2 and the block around it, at line 3, has 2. Write ':::' here and on its closing line."
            ]
        },
        {
            // An item without a closing line.
            """
            :: HomeBlaze.Components.FlowLayout
            ::: Item
            one
            ::
            """,
            ["Line 4: This block is not closed. The line ':::' that ends it is missing."]
        },
        {
            // A closing line between two items that closes nothing.
            """
            :: HomeBlaze.Components.FlowLayout
            ::: Item
            one
            :::
            ::::
            ::: Item
            two
            :::
            ::
            """,
            [
                "Line 7: This line of 4 ':' characters closes no block. A closing line needs as many characters " +
                "as the head line of its block. Open here: line 3 (2 characters)."
            ]
        },
        {
            // A tab that is closed with the line of the layout, so the layout ends there.
            """
            :: HomeBlaze.Components.TabLayout
            ::: Tab Title="One"
            one
            ::
            ::: Tab Title="Two"
            two
            :::
            ::
            """,
            [
                "Line 4: This block is not closed. The line ':::' that ends it is missing.",
                "Line 7: 'Tab' is not a type name. A slot block is only allowed directly inside a widget block.",
                "Line 10: This line of 2 ':' characters closes no block. No block is open here."
            ]
        },
        {
            // Several invalid blocks in one layout, and a valid item between them.
            """
            :: HomeBlaze.Components.FlowLayout
            ::: Item oops
            one
            :::
            ::: Item
            two
            :::
            ::::
            ::
            """,
            [
                "Line 4: Expected key=value but found 'oops'.",
                "Line 10: This line of 4 ':' characters closes no block. A closing line needs as many characters " +
                "as the head line of its block. Open here: line 3 (2 characters)."
            ]
        },
        {
            // An invalid block and other loose content: the block is the mistake to fix first.
            """
            :: HomeBlaze.Components.FlowLayout
            Loose text.
            ::: Item oops
            one
            :::
            ::
            """,
            ["Line 5: Expected key=value but found 'oops'."]
        },
        {
            // The value of a slot is a subject without a default slot as well.
            $"""
            :: {TestNamespace}.TestFaultyHost
            ::: Faulty Mode=ok
            :::::
            :::
            ::
            """,
            [
                "Line 5: This line of 5 ':' characters closes no block. A closing line needs as many characters " +
                "as the head line of its block. Open here: line 3 (2 characters), line 4 (3 characters)."
            ]
        }
    };

    [Theory]
    [MemberData(nameof(InvalidBlocksInAWidgetWithoutDefaultSlot))]
    public async Task WhenBlockInsideAWidgetWithoutDefaultSlotIsInvalid_ThenTheBoxesNameTheLineAndTheMessageOfThatBlock(
        string widget, string[] expectedBoxes)
    {
        // Arrange
        var fixture = await DocumentationSamplesTests.CreateFixtureAsync();

        // Act
        var page = await fixture.AddPageAsync("page.md", $"Text before.\n\n{widget}\n\nText after.");

        // Assert
        Assert.Equal(expectedBoxes, DocumentationSamplesTests.FindErrorBoxes(page));

        var html = MarkdownPageFixture.Html(page.Children);
        Assert.Contains("Text before.", html);
        Assert.Contains("Text after.", html);
        Assert.All(page.Children.Values, child => Assert.IsType<HtmlSegment>(child));
    }

    [Fact]
    public async Task WhenAnHtmlCommentStandsBetweenTheSlotBlocksOfAWidgetWithoutDefaultSlot_ThenItIsNoContent()
    {
        // Arrange
        var fixture = await DocumentationSamplesTests.CreateFixtureAsync();

        // Act
        var page = await fixture.AddPageAsync("page.md", """
            Text before.

            :: HomeBlaze.Components.FlowLayout#flow
            <!-- the first cell -->
            ::: Item
            one
            :::

            <!--
            the second cell
            -->

            ::: Item
            two
            :::
            ::

            Text after.
            """);

        // Assert
        Assert.Empty(DocumentationSamplesTests.FindErrorBoxes(page));
        var flow = Assert.IsType<FlowLayout>(page.Children["flow"]);
        Assert.Equal(2, flow.Items.Length);
    }

    [Theory]
    [InlineData("Loose text.")]
    [InlineData("<!-- a comment --> and text after it")]
    [InlineData("{{ /x/y }}")]
    public async Task WhenOtherContentStandsBetweenTheSlotBlocksOfAWidgetWithoutDefaultSlot_ThenTheBoxIsAtTheLineOfTheWidget(
        string looseContent)
    {
        // Arrange
        var fixture = await DocumentationSamplesTests.CreateFixtureAsync();

        // Act
        var page = await fixture.AddPageAsync("page.md", $"""
            Text before.

            :: HomeBlaze.Components.FlowLayout
            ::: Item
            one
            :::

            {looseContent}

            ::: Item
            two
            :::
            ::

            Text after.
            """);

        // Assert
        Assert.Equal(
            ["Line 3: FlowLayout has no default slot, so it cannot have content."],
            DocumentationSamplesTests.FindErrorBoxes(page));
    }

    [Fact]
    public async Task WhenBlockInsideAWidgetWithDefaultSlotIsInvalid_ThenTheWidgetIsShownWithTheBoxWhereTheBlockWas()
    {
        // Arrange
        var fixture = await DocumentationSamplesTests.CreateFixtureAsync();

        // Act
        var page = await fixture.AddPageAsync("page.md", """
            :: HomeBlaze.Components.Card#card Title="Card"
            The body of the card.

            ::: Footer Title = "x"
            The footer of the card.
            :::

            The end of the body.
            ::
            """);

        // Assert
        var card = Assert.IsType<Card>(page.Children["card"]);
        Assert.Null(card.Footer);

        var content = MarkdownPageFixture.Html(Assert.IsType<MarkdownFragment>(card.Content).Children);
        var box = content.IndexOf($"Line 4: {System.Net.WebUtility.HtmlEncode(HeadError)}", StringComparison.Ordinal);
        Assert.InRange(box, content.IndexOf("The body of the card.", StringComparison.Ordinal), int.MaxValue);
        Assert.InRange(content.IndexOf("The end of the body.", StringComparison.Ordinal), box, int.MaxValue);
        Assert.DoesNotContain("The footer of the card.", content);
        Assert.Equal([$"Line 4: {HeadError}"], DocumentationSamplesTests.FindErrorBoxes(page));
    }

    [Fact]
    public async Task WhenDefaultSlotIsFilledByABlockAndAnotherBlockIsInvalid_ThenTheBoxNamesTheLineAndTheMessageOfThatBlock()
    {
        // Arrange
        var fixture = await DocumentationSamplesTests.CreateFixtureAsync();

        // Act
        var page = await fixture.AddPageAsync("page.md", """
            :: HomeBlaze.Components.Card#card Title="Card"
            ::: Content
            The body of the card.
            :::
            <!-- the footer -->
            ::: Footer Title = "x"
            The footer of the card.
            :::
            ::
            """);

        // Assert
        Assert.Equal([$"Line 6: {HeadError}"], DocumentationSamplesTests.FindErrorBoxes(page));
        Assert.False(page.Children.ContainsKey("card"));
    }

    [Fact]
    public async Task WhenDefaultSlotIsFilledByABlockAndAnHtmlCommentStandsNextToIt_ThenTheCommentIsNoContent()
    {
        // Arrange
        var fixture = await DocumentationSamplesTests.CreateFixtureAsync();

        // Act
        var page = await fixture.AddPageAsync("page.md", """
            :: HomeBlaze.Components.Card#card Title="Card"
            ::: Content
            The body of the card.
            :::
            <!-- the footer -->
            ::: Footer
            The footer of the card.
            :::
            ::
            """);

        // Assert
        Assert.Empty(DocumentationSamplesTests.FindErrorBoxes(page));
        var card = Assert.IsType<Card>(page.Children["card"]);
        Assert.Contains("The body of the card.", MarkdownPageFixture.Html(Assert.IsType<MarkdownFragment>(card.Content).Children));
        Assert.Contains("The footer of the card.", MarkdownPageFixture.Html(Assert.IsType<MarkdownFragment>(card.Footer).Children));
    }

    [Theory]
    [InlineData(":::")]
    [InlineData("::")]
    public async Task WhenHeadLineOfANestedBlockHasTooFewCharacters_ThenOnlyThatBlockIsAnErrorAndTheBlockAroundItIsShown(
        string closingLineOfTheMotor)
    {
        // Arrange
        var fixture = await DocumentationSamplesTests.CreateFixtureAsync();

        // Act
        var page = await fixture.AddPageAsync("page.md", $"""
            :: HomeBlaze.Components.Card#card
            :: HomeBlaze.Samples.Motor#m
            {closingLineOfTheMotor}
            text
            ::
            more page text
            """);

        // Assert
        Assert.Equal(
            [
                "Line 2: A block inside another block needs more ':' characters than the block around it. " +
                "This block has 2 and the block around it, at line 1, has 2. Write ':::' here and on its closing line."
            ],
            DocumentationSamplesTests.FindErrorBoxes(page));

        var card = Assert.IsType<Card>(page.Children["card"]);
        Assert.Contains("text", MarkdownPageFixture.Html(Assert.IsType<MarkdownFragment>(card.Content).Children));
        Assert.Contains("more page text", MarkdownPageFixture.Html(page.Children));
    }

    [Fact]
    public async Task WhenTextInsideABlockIsReadAsAHeadLineAndTakesItsClosingLine_ThenTheBoxOfTheBlockNamesBothLines()
    {
        // Arrange
        var fixture = await DocumentationSamplesTests.CreateFixtureAsync();

        // Act
        var page = await fixture.AddPageAsync("page.md", """
            Text before.

            :: HomeBlaze.Components.Card Title="Notes"
            :: note that this line is text
            The body of the card.
            ::

            Text after.
            """);

        // Assert
        Assert.Equal(
            [
                "Line 3: This block is not closed. The line '::' that ends it is missing. " +
                "The '::' at line 6 closed the block at line 4, which is inside this block and has 2 characters as well."
            ],
            DocumentationSamplesTests.FindErrorBoxes(page));
        Assert.Contains("Text before.", MarkdownPageFixture.Html(page.Children));
    }

    [Fact]
    public async Task WhenHeadLineDirectlyOnThePageHasNoClosingLine_ThenItsBoxSaysOnlyThatTheBlockIsNotClosed()
    {
        // Arrange
        var fixture = await DocumentationSamplesTests.CreateFixtureAsync();

        // Act
        var page = await fixture.AddPageAsync("page.md", """
            Text before.

            :: HomeBlaze.Components.Card Title="Notes"
            The body of the card.

            Text after.
            """);

        // Assert
        Assert.Equal(
            ["Line 3: This block is not closed. The line '::' that ends it is missing."],
            DocumentationSamplesTests.FindErrorBoxes(page));
        Assert.Contains("Text before.", MarkdownPageFixture.Html(page.Children));
    }
}
