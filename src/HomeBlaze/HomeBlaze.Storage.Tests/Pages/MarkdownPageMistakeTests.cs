using HomeBlaze.Components;
using HomeBlaze.Storage.Internal;
using HomeBlaze.Storage.Pages;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor.Registry;

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

    private const string MissingTypeError =
        "A subject in the json block needs a '$type' with its full type name, as the first name in its braces.";

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
        Assert.Equal(expectedBoxes, PageErrorBoxes.FindSortedByLine(page));

        var html = MarkdownPageFixture.Html(page.Children);
        Assert.Contains("Text before.", html);
        Assert.Contains("Text after.", html);
        Assert.All(page.Children.Values, child => Assert.IsType<HtmlSegment>(child));
    }

    [Fact]
    public async Task WhenItemHasAsManyCharactersAsItsLayout_ThenItEndsTheLayoutAndEveryItemIsAnErrorBesideIt()
    {
        // Arrange
        var fixture = await DocumentationSamplesTests.CreateFixtureAsync();

        // Act
        var page = await fixture.AddPageAsync("page.md", """
            Text before.

            :: HomeBlaze.Components.FlowLayout#flow
            :: Item
            one
            ::
            ::: Item
            two
            :::
            ::

            Text after.
            """);

        // Assert
        Assert.Equal(
            [
                "Line 4: 'Item' is not a type name. A slot block is only allowed directly inside a widget block.",
                "Line 7: 'Item' is not a type name. A slot block is only allowed directly inside a widget block.",
                "Line 10: This line of 2 ':' characters closes no block. No block is open here."
            ],
            PageErrorBoxes.FindSortedByLine(page));
        Assert.Empty(Assert.IsType<FlowLayout>(page.Children["flow"]).Items);
        Assert.Contains("Text after.", MarkdownPageFixture.Html(page.Children));
    }

    [Fact]
    public async Task WhenItemHasNoClosingLine_ThenTheClosingLineOfTheLayoutEndsItAndThereIsNoBox()
    {
        // Arrange
        var fixture = await DocumentationSamplesTests.CreateFixtureAsync();

        // Act
        var page = await fixture.AddPageAsync("page.md", """
            Text before.

            :: HomeBlaze.Components.FlowLayout#flow
            ::: Item
            one
            ::

            Text after.
            """);

        // Assert
        Assert.Empty(PageErrorBoxes.FindSortedByLine(page));
        var item = Assert.Single(Assert.IsType<FlowLayout>(page.Children["flow"]).Items);
        Assert.Equal("\n<p>one</p>\n", MarkdownPageFixture.Html(Assert.IsType<MarkdownFragment>(item).Children));
        Assert.Contains("Text after.", MarkdownPageFixture.Html(page.Children));
    }

    [Fact]
    public async Task WhenTabIsClosedWithTheLineOfTheLayout_ThenTheLayoutEndsThereAndTheNextTabIsAnErrorBesideIt()
    {
        // Arrange
        var fixture = await DocumentationSamplesTests.CreateFixtureAsync();

        // Act
        var page = await fixture.AddPageAsync("page.md", """
            Text before.

            :: HomeBlaze.Components.TabLayout#tabs
            ::: Tab Title="One"
            one
            ::
            ::: Tab Title="Two"
            two
            :::
            ::

            Text after.
            """);

        // Assert
        Assert.Equal(
            [
                "Line 7: 'Tab' is not a type name. A slot block is only allowed directly inside a widget block.",
                "Line 10: This line of 2 ':' characters closes no block. No block is open here."
            ],
            PageErrorBoxes.FindSortedByLine(page));
        var tab = Assert.Single(Assert.IsType<TabLayout>(page.Children["tabs"]).Tabs);
        Assert.Equal("One", tab.Title);
        Assert.Contains("Text after.", MarkdownPageFixture.Html(page.Children));
    }

    [Theory]
    [InlineData("")]
    [InlineData("::")]
    [InlineData(":: HomeBlaze.Samples.Motor#m")]
    public async Task WhenContentIsInsideAWidgetWithoutDefaultSlotThatHasNoClosingLine_ThenTheBoxSaysWhichLineEndsTheWidget(
        string lineThatEndsTheWidget)
    {
        // Arrange
        var fixture = await DocumentationSamplesTests.CreateFixtureAsync();
        var source =
            ":: HomeBlaze.Components.Card#card Title=\"Pump\"\n" +
            "::: HomeBlaze.Components.Widget Path=../WaterPump\n" +
            "The pump of the cellar.\n" +
            lineThatEndsTheWidget;

        // Act
        var page = await fixture.AddPageAsync("page.md", source);

        // Assert
        Assert.Equal(
            [
                "Line 2: Widget has no default slot, so it cannot have content. The content is inside this block " +
                "because the block is still open there. A closing line ':::' before the content ends the block."
            ],
            PageErrorBoxes.FindSortedByLine(page));
        Assert.IsType<Card>(page.Children["card"]);
    }

    [Fact]
    public async Task WhenBlockIsInsideAWidgetWithoutDefaultSlotThatHasNoClosingLine_ThenTheBoxSaysWhichLineEndsTheWidget()
    {
        // Arrange
        var fixture = await DocumentationSamplesTests.CreateFixtureAsync();

        // Act
        var page = await fixture.AddPageAsync("page.md", """
            :: HomeBlaze.Components.Card#card Title="Pumps"
            ::: HomeBlaze.Components.Widget Path=../WaterPump
            :::: HomeBlaze.Components.Widget Path=../Compressor
            """);

        // Assert
        Assert.Equal(
            [
                "Line 2: Widget has no default slot, so it cannot have content. The content is inside this block " +
                "because the block is still open there. A closing line ':::' before the content ends the block."
            ],
            PageErrorBoxes.FindSortedByLine(page));
        Assert.IsType<Card>(page.Children["card"]);
    }

    [Fact]
    public async Task WhenContentIsInsideASlotValueWithoutDefaultSlotThatHasNoClosingLine_ThenTheBoxSaysWhichLineEndsTheSlotBlock()
    {
        // Arrange
        var fixture = await DocumentationSamplesTests.CreateFixtureAsync();

        // Act
        var page = await fixture.AddPageAsync("page.md", $"""
            :: {TestNamespace}.TestFaultyHost
            ::: Faulty Mode=ok
            Text that was meant for the page.
            """);

        // Assert
        Assert.Equal(
            [
                "Line 2: TestFaulty has no default slot, so it cannot have content. The content is inside this block " +
                "because the block is still open there. A closing line ':::' before the content ends the block."
            ],
            PageErrorBoxes.FindSortedByLine(page));
    }

    [Theory]
    [InlineData(":: HomeBlaze.Samples.Motor#m\nText inside the motor.\n::", "Line 1: Motor has no default slot, so it cannot have content.")]
    [InlineData(
        ":: HomeBlaze.Components.Card\n::: HomeBlaze.Components.Widget Path=../WaterPump\nThe pump.\n:::\n::",
        "Line 2: Widget has no default slot, so it cannot have content.")]
    [InlineData(
        ":: HomeBlaze.Components.FlowLayout\n::: Item\none\n:::\nLoose text.\n::",
        "Line 1: FlowLayout has no default slot, so it cannot have content.")]
    public async Task WhenContentIsInsideAWidgetWithoutDefaultSlotThatHasItsClosingLine_ThenTheBoxIsAsBefore(
        string source, string expectedBox)
    {
        // Arrange
        var fixture = await DocumentationSamplesTests.CreateFixtureAsync();

        // Act
        var page = await fixture.AddPageAsync("page.md", source);

        // Assert
        Assert.Equal([expectedBox], PageErrorBoxes.FindSortedByLine(page));
    }

    [Fact]
    public async Task WhenLayoutWithoutClosingLineIsFollowedByPageText_ThenTheBoxSaysWhichLineEndsTheLayout()
    {
        // Arrange
        var fixture = await DocumentationSamplesTests.CreateFixtureAsync();

        // Act
        var page = await fixture.AddPageAsync("page.md", """
            Text before.

            :: HomeBlaze.Components.FlowLayout
            ::: Item
            one
            :::

            Text that was meant for the page.
            """);

        // Assert
        Assert.Equal(
            [
                "Line 3: FlowLayout has no default slot, so it cannot have content. The content is inside this block " +
                "because the block is still open there. A closing line '::' before the content ends the block."
            ],
            PageErrorBoxes.FindSortedByLine(page));
    }

    public static TheoryData<string, string[]> WidgetBlocksBesideTheSlotBlockTheyWereMeantFor() => new()
    {
        {
            """
            :: HomeBlaze.Components.FlowLayout
            ::: Item
            ::: HomeBlaze.Components.Card Title="A"
            """,
            [
                "Line 5: This block has as many ':' characters as the slot block 'Item' at line 4, so it is not inside " +
                "that block but directly inside FlowLayout, which has no default slot. Write '::::' here to put it inside 'Item'."
            ]
        },
        {
            // With its own closing line, which has to change as well.
            """
            :: HomeBlaze.Components.TabLayout
            ::: Tab Title="One"
            ::: HomeBlaze.Components.Card Title="A"
            The body of the card.
            :::
            ::
            """,
            [
                "Line 5: This block has as many ':' characters as the slot block 'Tab' at line 4, so it is not inside " +
                "that block but directly inside TabLayout, which has no default slot. Write '::::' here and on its closing line " +
                "to put it inside 'Tab'."
            ]
        },
        {
            // Every cell of the layout has the mistake, and the first block has fewer characters than its item.
            """
            :: HomeBlaze.Components.FlowLayout
            :::: Item
            ::: HomeBlaze.Components.Card Title="A"
            ::: Item
            ::: HomeBlaze.Components.Card Title="B"
            ::
            """,
            [
                "Line 5: This block has fewer ':' characters than the slot block 'Item' at line 4, so it is not inside " +
                "that block but directly inside FlowLayout, which has no default slot. Write ':::::' here to put it inside 'Item'.",
                "Line 7: This block has as many ':' characters as the slot block 'Item' at line 6, so it is not inside " +
                "that block but directly inside FlowLayout, which has no default slot. Write '::::' here to put it inside 'Item'."
            ]
        },
        {
            // Other loose content next to such a block: the block is the mistake to fix first.
            """
            :: HomeBlaze.Components.FlowLayout
            Loose text.
            ::: Item
            ::: HomeBlaze.Components.Card Title="A"
            """,
            [
                "Line 6: This block has as many ':' characters as the slot block 'Item' at line 5, so it is not inside " +
                "that block but directly inside FlowLayout, which has no default slot. Write '::::' here to put it inside 'Item'."
            ]
        }
    };

    [Theory]
    [MemberData(nameof(WidgetBlocksBesideTheSlotBlockTheyWereMeantFor))]
    public async Task WhenWidgetBlockEndedTheSlotBlockBeforeItInAWidgetWithoutDefaultSlot_ThenTheBoxNamesBothLinesAndTheCharactersToWrite(
        string widget, string[] expectedBoxes)
    {
        // Arrange
        var fixture = await DocumentationSamplesTests.CreateFixtureAsync();

        // Act
        var page = await fixture.AddPageAsync("page.md", $"Text before.\n\n{widget}");

        // Assert
        Assert.Equal(expectedBoxes, PageErrorBoxes.FindSortedByLine(page));
        Assert.Contains("Text before.", MarkdownPageFixture.Html(page.Children));
        Assert.All(page.Children.Values, child => Assert.IsType<HtmlSegment>(child));
    }

    [Theory]
    // The slot block before the widget block has its closing line, so the widget block did not end it.
    [InlineData(":: HomeBlaze.Components.FlowLayout\n::: Item\n:::\n::: HomeBlaze.Components.Card Title=\"A\"\n:::\n::")]
    // The widget block is the first block of the layout.
    [InlineData(":: HomeBlaze.Components.FlowLayout\n::: HomeBlaze.Components.Card Title=\"A\"\n:::\n::: Item\n:::\n::")]
    public async Task WhenWidgetBlockInAWidgetWithoutDefaultSlotEndedNoSlotBlock_ThenTheBoxIsTheOneOfTheWidgetAroundIt(string source)
    {
        // Arrange
        var fixture = await DocumentationSamplesTests.CreateFixtureAsync();

        // Act
        var page = await fixture.AddPageAsync("page.md", source);

        // Assert
        Assert.Equal(
            ["Line 1: FlowLayout has no default slot, so it cannot have content."],
            PageErrorBoxes.FindSortedByLine(page));
    }

    [Fact]
    public async Task WhenWidgetBlockEndedASlotBlockInAWidgetWithDefaultSlot_ThenItIsContentOfThatWidgetAndThereIsNoBox()
    {
        // Arrange
        var fixture = await DocumentationSamplesTests.CreateFixtureAsync();

        // Act
        var page = await fixture.AddPageAsync("page.md", """
            :: HomeBlaze.Components.Card#card Title="Card"
            ::: Footer
            ::: HomeBlaze.Samples.Motor#m
            """);

        // Assert
        Assert.Empty(PageErrorBoxes.FindSortedByLine(page));
        var card = Assert.IsType<Card>(page.Children["card"]);
        Assert.Empty(Assert.IsType<MarkdownFragment>(card.Footer).Children);
        Assert.IsType<HomeBlaze.Samples.Motor>(Assert.IsType<MarkdownFragment>(card.Content).Children["m"]);
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
        Assert.Empty(PageErrorBoxes.FindSortedByLine(page));
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
            PageErrorBoxes.FindSortedByLine(page));
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
        Assert.Equal([$"Line 4: {HeadError}"], PageErrorBoxes.FindSortedByLine(page));
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
        Assert.Equal([$"Line 6: {HeadError}"], PageErrorBoxes.FindSortedByLine(page));
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
        Assert.Empty(PageErrorBoxes.FindSortedByLine(page));
        var card = Assert.IsType<Card>(page.Children["card"]);
        Assert.Contains("The body of the card.", MarkdownPageFixture.Html(Assert.IsType<MarkdownFragment>(card.Content).Children));
        Assert.Contains("The footer of the card.", MarkdownPageFixture.Html(Assert.IsType<MarkdownFragment>(card.Footer).Children));
    }

    [Fact]
    public async Task WhenBlockInACardHasAsManyCharactersAsTheCard_ThenItIsABlockAfterTheCardAndTheClosingLineOfTheCardClosesNothing()
    {
        // Arrange
        var fixture = await DocumentationSamplesTests.CreateFixtureAsync();

        // Act
        var page = await fixture.AddPageAsync("page.md", """
            :: HomeBlaze.Components.Card#card
            :: HomeBlaze.Samples.Motor#m
            ::
            text
            ::
            more page text
            """);

        // Assert
        Assert.Equal(
            ["Line 5: This line of 2 ':' characters closes no block. No block is open here."],
            PageErrorBoxes.FindSortedByLine(page));

        Assert.Null(Assert.IsType<Card>(page.Children["card"]).Content);
        Assert.IsType<HomeBlaze.Samples.Motor>(page.Children["m"]);
        var html = MarkdownPageFixture.Html(page.Children);
        Assert.Contains("<p>text</p>", html);
        Assert.Contains("more page text", html);
    }

    [Fact]
    public async Task WhenBlockInACardHasAsManyCharactersAsTheCardAndALongerClosingLine_ThenThatLineClosesNothingAndIsTheBoxOfTheBlock()
    {
        // Arrange
        var fixture = await DocumentationSamplesTests.CreateFixtureAsync();

        // Act
        var page = await fixture.AddPageAsync("page.md", """
            :: HomeBlaze.Components.Card#card
            :: HomeBlaze.Samples.Motor#m
            :::
            text
            ::
            more page text
            """);

        // Assert
        Assert.Equal(
            [
                "Line 3: This line of 3 ':' characters closes no block. A closing line needs as many characters " +
                "as the head line of its block. Open here: line 2 (2 characters)."
            ],
            PageErrorBoxes.FindSortedByLine(page));

        Assert.Null(Assert.IsType<Card>(page.Children["card"]).Content);
        Assert.False(page.Children.ContainsKey("m"));
        Assert.Contains("more page text", MarkdownPageFixture.Html(page.Children));
    }

    [Fact]
    public async Task WhenTextInsideABlockIsReadAsAHeadLine_ThenItEndsTheBlockAndItsBoxReplacesTheTextUpToTheClosingLine()
    {
        // Arrange
        var fixture = await DocumentationSamplesTests.CreateFixtureAsync();

        // Act
        var page = await fixture.AddPageAsync("page.md", """
            Text before.

            :: HomeBlaze.Components.Card#notes Title="Notes"
            :: note that this line is text
            The body of the card.
            ::

            Text after.
            """);

        // Assert
        Assert.Equal(
            ["Line 4: Expected key=value but found 'that this line is text'."],
            PageErrorBoxes.FindSortedByLine(page));
        Assert.Null(Assert.IsType<Card>(page.Children["notes"]).Content);

        var html = MarkdownPageFixture.Html(page.Children);
        Assert.Contains("Text before.", html);
        Assert.DoesNotContain("The body of the card.", html);
        Assert.Contains("Text after.", html);
    }

    [Fact]
    public async Task WhenBlockDirectlyOnThePageHasNoClosingLine_ThenItTakesTheRestOfThePageAndThereIsNoBox()
    {
        // Arrange
        var fixture = await DocumentationSamplesTests.CreateFixtureAsync();

        // Act
        var page = await fixture.AddPageAsync("page.md", """
            Text before.

            :: HomeBlaze.Components.Card#notes Title="Notes"
            The body of the card.

            Text after.
            """);

        // Assert
        Assert.Empty(PageErrorBoxes.FindSortedByLine(page));
        Assert.Equal("<p>Text before.</p>\n", MarkdownPageFixture.Html(page.Children));

        var card = Assert.IsType<Card>(page.Children["notes"]);
        Assert.Equal(
            "\n<p>The body of the card.</p>\n<p>Text after.</p>\n",
            MarkdownPageFixture.Html(Assert.IsType<MarkdownFragment>(card.Content).Children));
    }

    [Fact]
    public async Task WhenSingleValueInTheJsonBlockDoesNotFitItsProperty_ThenTheBoxNamesThePropertyAndTheExpectedType()
    {
        // Arrange
        var fixture = await DocumentationSamplesTests.CreateFixtureAsync();

        // Act
        var page = await fixture.AddPageAsync("page.md", """
            Text before.

            :: HomeBlaze.Components.GridLayout#grid Columns=3
            ```json
            { "Rows": "three" }
            ```
            ::

            Text after.
            """);

        // Assert
        Assert.Equal(
            ["Line 3: 'three' is not a valid value for 'Rows'. Expected a value of type Int32."],
            PageErrorBoxes.FindSortedByLine(page));
        Assert.False(page.Children.ContainsKey("grid"));
        Assert.Contains("Text after.", MarkdownPageFixture.Html(page.Children));
    }

    [Theory]
    [InlineData("1.5", "'1.5' is not a valid value for 'Rows'. Expected a value of type Int32.")]
    [InlineData("null", "'null' is not a valid value for 'Rows'. Expected a value of type Int32.")]
    [InlineData("true", "'true' is not a valid value for 'Rows'. Expected a value of type Int32.")]
    public async Task WhenValueInTheJsonBlockIsNoText_ThenTheBoxShowsItAsItIsWritten(string value, string expectedMessage)
    {
        // Arrange
        var fixture = await DocumentationSamplesTests.CreateFixtureAsync();

        // Act
        var page = await fixture.AddPageAsync(
            "page.md", $":: HomeBlaze.Components.GridLayout\n```json\n{{ \"Rows\": {value} }}\n```\n::");

        // Assert
        Assert.Equal([$"Line 1: {expectedMessage}"], PageErrorBoxes.FindSortedByLine(page));
    }

    [Fact]
    public async Task WhenTypeOfASubjectInsideAValueOfTheJsonBlockIsMisspelled_ThenTheBoxNamesThePropertyAndThePlace()
    {
        // Arrange
        var fixture = await DocumentationSamplesTests.CreateFixtureAsync();

        // Act
        var page = await fixture.AddPageAsync("page.md", """
            Text before.

            :: HomeBlaze.Components.GridLayout#grid Rows=1 Columns=2
            ```json
            {
              "Cells": [
                { "Row": 0, "Column": 0, "Child": { "$type": "HomeBlaze.Components.Widget", "Path": "../Compressor" } },
                { "Row": 0, "Column": 1, "Child": { "$type": "HomeBlaze.Components.Widgt", "Path": "../WaterPump" } }
              ]
            }
            ```
            ::

            Text after.
            """);

        // Assert
        var box = Assert.Single(PageErrorBoxes.FindSortedByLine(page));
        Assert.StartsWith("Line 3: The value of 'Cells' in the json block cannot be read at Cells[1].Child: ", box);
        Assert.Contains("HomeBlaze.Components.Widgt", box);
        Assert.DoesNotContain("LineNumber", box);
        Assert.False(page.Children.ContainsKey("grid"));
        Assert.Contains("Text after.", MarkdownPageFixture.Html(page.Children));
    }

    [Theory]
    [InlineData("\"none\"", "Line 1: The value of 'Cells' in the json block cannot be read: ")]
    [InlineData("[ { \"Row\": \"zero\" } ]", "Line 1: The value of 'Cells' in the json block cannot be read at Cells[0].Row: ")]
    public async Task WhenListInTheJsonBlockCannotBeRead_ThenTheBoxNamesThePropertyAndThePlaceInsideIt(
        string cells, string expectedStart)
    {
        // Arrange
        var fixture = await DocumentationSamplesTests.CreateFixtureAsync();

        // Act
        var page = await fixture.AddPageAsync(
            "page.md", $":: HomeBlaze.Components.GridLayout\n```json\n{{ \"Cells\": {cells} }}\n```\n::");

        // Assert
        var box = Assert.Single(PageErrorBoxes.FindSortedByLine(page));
        Assert.StartsWith(expectedStart, box);
        Assert.DoesNotContain("LineNumber", box);
    }

    [Theory]
    [InlineData(
        """{ "Cells": [ { "Child": { "Path": "../a" } } ] }""",
        "Cells[0].Child")]
    [InlineData(
        """{ "Cells": [ { "Child": { "Path": "../a", "$type": "HomeBlaze.Components.Widget" } } ] }""",
        "Cells[0].Child")]
    [InlineData(
        """{ "Cells": [ { "Row": 0 }, { "Child": { "$type": "HomeBlaze.Components.GridLayout", "Cells": [ { "Child": { "Path": "../a" } } ] } } ] }""",
        "Cells[1].Child.Cells[0].Child")]
    public async Task WhenSubjectInsideAValueOfTheJsonBlockHasNoTypeAsItsFirstName_ThenTheBoxNamesThePropertyThePlaceAndTheMissingType(
        string json, string expectedPlace)
    {
        // Arrange
        var fixture = await DocumentationSamplesTests.CreateFixtureAsync();

        // Act
        var page = await fixture.AddPageAsync(
            "page.md", $"Text before.\n\n:: HomeBlaze.Components.GridLayout#grid\n```json\n{json}\n```\n::\n\nText after.");

        // Assert
        Assert.Equal(
            [$"Line 3: The value of 'Cells' in the json block cannot be read at {expectedPlace}: {MissingTypeError}"],
            PageErrorBoxes.FindSortedByLine(page));
        Assert.False(page.Children.ContainsKey("grid"));
        Assert.Contains("Text after.", MarkdownPageFixture.Html(page.Children));
    }

    [Fact]
    public async Task WhenValueOfTheJsonBlockIsItselfASubjectWithoutType_ThenTheBoxNamesThePropertyAndTheMissingType()
    {
        // Arrange
        var fixture = await DocumentationSamplesTests.CreateFixtureAsync();

        // Act
        var page = await fixture.AddPageAsync(
            "page.md", $":: {TestNamespace}.TestChildHost\n```json\n{{ \"Child\": {{ \"Name\": \"one\" }} }}\n```\n::");

        // Assert
        Assert.Equal(
            [$"Line 1: The value of 'Child' in the json block cannot be read: {MissingTypeError}"],
            PageErrorBoxes.FindSortedByLine(page));
    }

    [Fact]
    public async Task WhenBlockIsUpdatedInPlaceWithASubjectWithoutTypeInTheJsonBlock_ThenItIsABoxAndItsSubjectLeavesThePage()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync(services => services.AddSingleton(new TestCreationCounter()));
        var source = $$"""
            :: {{TestNamespace}}.TestChildHost#host Count=1
            ```json
            { "Children": [ { "$type": "{{TestNamespace}}.TestCountedChild", "Name": "one" } ] }
            ```
            ::
            """;
        var page = await fixture.AddPageAsync("page.md", source);
        var host = Assert.IsType<TestChildHost>(page.Children["host"]);

        // Act
        await fixture.UpdatePageAsync(page, source.Replace($"\"$type\": \"{TestNamespace}.TestCountedChild\", ", string.Empty));

        // Assert
        Assert.Equal(
            [$"Line 1: The value of 'Children' in the json block cannot be read at Children[0]: {MissingTypeError}"],
            PageErrorBoxes.FindSortedByLine(page));
        Assert.False(page.Children.ContainsKey("host"));
        Assert.Null(host.TryGetRegisteredSubject());
    }

    [Fact]
    public async Task WhenValueInTheJsonBlockHoldsSubjectsAndFits_ThenThereIsNoBoxAndEachSubjectIsCreatedOnce()
    {
        // Arrange
        var counter = new TestCreationCounter();
        var fixture = await MarkdownPageFixture.CreateAsync(services => services.AddSingleton(counter));

        // Act
        var page = await fixture.AddPageAsync("page.md", $$"""
            :: {{TestNamespace}}.TestChildHost#host Count=2
            ```json
            {
              "Children": [
                { "$type": "{{TestNamespace}}.TestCountedChild", "Name": "one" },
                { "$type": "{{TestNamespace}}.TestCountedChild", "Name": "two" }
              ]
            }
            ```
            ::
            """);

        // Assert
        Assert.Empty(PageErrorBoxes.FindSortedByLine(page));
        var host = Assert.IsType<TestChildHost>(page.Children["host"]);
        Assert.Equal(["one", "two"], host.Children.Select(child => Assert.IsType<TestCountedChild>(child).Name));
        Assert.Equal(2, counter.Count);
    }

    [Fact]
    public async Task WhenBlockIsUpdatedInPlaceWithAValueInTheJsonBlockThatDoesNotFit_ThenItIsABoxAndItsSubjectLeavesThePage()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var source = $":: {TestNamespace}.TestLeaf#leaf Name=A\n```json\n{{ \"Count\": 1 }}\n```\n::\n\n:: {TestNamespace}.TestLeaf#other Count=1\n::";
        var page = await fixture.AddPageAsync("page.md", source);
        var leaf = Assert.IsType<TestLeaf>(page.Children["leaf"]);
        var other = page.Children["other"];

        // Act
        await fixture.UpdatePageAsync(page, source.Replace("\"Count\": 1", "\"Count\": \"many\"").Replace("Count=1", "Count=2"));

        // Assert
        Assert.Equal(
            ["Line 1: 'many' is not a valid value for 'Count'. Expected a value of type Int32."],
            PageErrorBoxes.FindSortedByLine(page));
        Assert.False(page.Children.ContainsKey("leaf"));
        Assert.Null(page.GetBlockSubject(0));
        Assert.Null(leaf.TryGetRegisteredSubject());
        Assert.Same(other, page.Children["other"]);
        Assert.Equal(2, ((TestLeaf)other).Count);
    }

    [Fact]
    public async Task WhenValueInTheJsonBlockIsFixed_ThenTheBlockIsCreatedAgain()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var source = $":: {TestNamespace}.TestLeaf#leaf\n```json\n{{ \"Count\": \"many\" }}\n```\n::";
        var page = await fixture.AddPageAsync("page.md", source);
        var boxesBefore = PageErrorBoxes.FindSortedByLine(page);

        // Act
        await fixture.UpdatePageAsync(page, source.Replace("\"many\"", "4"));

        // Assert
        Assert.Single(boxesBefore);
        Assert.Empty(PageErrorBoxes.FindSortedByLine(page));
        Assert.Equal(4, Assert.IsType<TestLeaf>(page.Children["leaf"]).Count);
    }

    [Theory]
    [InlineData(
        ":: HomeBlaze.Components.TabLayout Title=x\n::",
        "Line 1: TabLayout has no configuration properties, so 'Title' cannot be set.")]
    [InlineData(
        ":: HomeBlaze.Components.TabLayout\n```json\n{ \"title\": \"x\" }\n```\n::",
        "Line 1: TabLayout has no configuration properties, so 'title' cannot be set.")]
    [InlineData(
        ":: HomeBlaze.Samples.Motor#m\n::: Footer\ntext\n:::\n::",
        "Line 2: Motor has no slots, so 'Footer' cannot be used inside it.")]
    [InlineData(
        ":: HomeBlaze.Components.Card Titel=x\n::",
        "Line 1: Card has no configuration property 'Titel'. Available: Icon, Title.")]
    public async Task WhenNameIsUnknown_ThenTheMessageListsTheNamesOrSaysThatTheTypeHasNone(string source, string expectedBox)
    {
        // Arrange
        var fixture = await DocumentationSamplesTests.CreateFixtureAsync();

        // Act
        var page = await fixture.AddPageAsync("page.md", source);

        // Assert
        Assert.Equal([expectedBox], PageErrorBoxes.FindSortedByLine(page));
    }
}
