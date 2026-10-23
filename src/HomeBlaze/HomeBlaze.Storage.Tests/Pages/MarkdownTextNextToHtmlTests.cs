using HomeBlaze.Storage.Pages;
using Markdig.Syntax;
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Tests.Pages;

/// <summary>
/// Text next to raw HTML is edited, inserted, removed and moved like any other text, for every type of HTML block, and the
/// checks of the page operations refuse every change that would change the HTML or join text into it.
/// </summary>
public class MarkdownTextNextToHtmlTests
{
    private static readonly IReadOnlyDictionary<int, IInterceptorSubject> NoSubjects = new Dictionary<int, IInterceptorSubject>();

    private const string ChangedText = "Changed.";

    // One HTML block of every type, each of which ends where it closes what it opens.
    public static TheoryData<string, HtmlBlockType> HtmlBlocks() => new()
    {
        { "<script>\nlet a = 1;\n</script>", HtmlBlockType.ScriptPreOrStyle },
        { "<!--\nc\n\nd\n-->", HtmlBlockType.Comment },
        { "<?xml version=\"1.0\"?>", HtmlBlockType.ProcessingInstruction },
        { "<!DOCTYPE html>", HtmlBlockType.DocumentType },
        { "<![CDATA[\n<div>\n]]>", HtmlBlockType.CData },
        { "<div align=\"center\">\n<img src=\"a.png\">\n</div>", HtmlBlockType.InterruptingBlock },
        { "<my-element>\nx\n</my-element>", HtmlBlockType.NonInterruptingBlock }
    };

    // The HTML blocks that end on the line of their closing marker, so that text can follow them on the next line.
    public static TheoryData<string, HtmlBlockType> HtmlBlocksThatEndAtAMarker() => new()
    {
        { "<script>\nlet a = 1;\n</script>", HtmlBlockType.ScriptPreOrStyle },
        { "<!--\nc\n\nd\n-->", HtmlBlockType.Comment },
        { "<?xml version=\"1.0\"?>", HtmlBlockType.ProcessingInstruction },
        { "<!DOCTYPE html>", HtmlBlockType.DocumentType },
        { "<![CDATA[\n<div>\n]]>", HtmlBlockType.CData }
    };

    private static MarkdownTextBlock TextAt(MarkdownPage page, int line) =>
        MarkdownTextBlocks.Find(page).Single(textBlock => textBlock.Line == line);

    private static int CountLines(string text) => text.Count(character => character == '\n') + 1;

    private static MarkdownPage ParseHtmlPage(string html, HtmlBlockType type, string source)
    {
        var page = MarkdownPageParser.Parse(source);
        var htmlBlock = Assert.Single(page.Document.OfType<HtmlBlock>());
        Assert.Equal(type, htmlBlock.Type);
        Assert.Equal(html, source.Substring(htmlBlock.Span.Start, htmlBlock.Span.Length));
        return page;
    }

    // The change kept the HTML as one text block of its own with the same lines of text.
    private static void AssertHtmlIsKept(MarkdownStructureChange? change, string? refusal, string html, string expectedSource)
    {
        Assert.Null(refusal);
        Assert.NotNull(change);
        Assert.Equal(expectedSource, change.Write.Page.Source);
        var after = change.Write.Page;
        var htmlTextBlock = Assert.Single(MarkdownTextBlocks.Find(after), textBlock => textBlock.HoldsHtml);
        Assert.Equal(html, MarkdownPageOperations.GetText(after, htmlTextBlock));
    }

    [Theory]
    [MemberData(nameof(HtmlBlocks))]
    public void WhenTheTextBeforeOrAfterRawHtmlIsChanged_ThenOnlyThatTextChanges(string html, HtmlBlockType type)
    {
        // Arrange
        var source = $"Before.\n\n{html}\n\nAfter.\n";
        var page = ParseHtmlPage(html, type, source);
        var afterLine = CountLines(html) + 4;

        // Act
        var changeBefore = MarkdownPageOperations.TryReplaceText(page, NoSubjects, TextAt(page, 1), ChangedText, out var refusalBefore);
        var changeAfter = MarkdownPageOperations.TryReplaceText(page, NoSubjects, TextAt(page, afterLine), ChangedText, out var refusalAfter);

        // Assert
        AssertHtmlIsKept(changeBefore, refusalBefore, html, $"{ChangedText}\n\n{html}\n\nAfter.\n");
        AssertHtmlIsKept(changeAfter, refusalAfter, html, $"Before.\n\n{html}\n\n{ChangedText}\n");
    }

    [Theory]
    [MemberData(nameof(HtmlBlocks))]
    public void WhenTheTextBeforeOrAfterRawHtmlIsRemoved_ThenOnlyThatTextIsGone(string html, HtmlBlockType type)
    {
        // Arrange
        var source = $"Before.\n\n{html}\n\nAfter.\n";
        var page = ParseHtmlPage(html, type, source);
        var afterLine = CountLines(html) + 4;

        // Act
        var changeBefore = MarkdownPageOperations.TryRemoveText(page, NoSubjects, TextAt(page, 1), out var refusalBefore);
        var changeAfter = MarkdownPageOperations.TryRemoveText(page, NoSubjects, TextAt(page, afterLine), out var refusalAfter);

        // Assert
        AssertHtmlIsKept(changeBefore, refusalBefore, html, $"{html}\n\nAfter.\n");
        AssertHtmlIsKept(changeAfter, refusalAfter, html, $"Before.\n\n{html}\n");
    }

    [Theory]
    [MemberData(nameof(HtmlBlocks))]
    public void WhenTextIsInsertedBeforeOrAfterRawHtml_ThenItStandsApartFromTheHtml(string html, HtmlBlockType type)
    {
        // Arrange
        var source = $"Before.\n\n{html}\n\nAfter.\n";
        var page = ParseHtmlPage(html, type, source);
        var afterLine = CountLines(html) + 4;

        // Act
        var changeBefore = MarkdownPageOperations.TryInsertText(page, NoSubjects, null, 3, ChangedText, out var refusalBefore);
        var changeAfter = MarkdownPageOperations.TryInsertText(page, NoSubjects, null, afterLine, ChangedText, out var refusalAfter);

        // Assert
        AssertHtmlIsKept(changeBefore, refusalBefore, html, $"Before.\n\n{ChangedText}\n\n{html}\n\nAfter.\n");
        AssertHtmlIsKept(changeAfter, refusalAfter, html, $"Before.\n\n{html}\n\n{ChangedText}\n\nAfter.\n");
    }

    [Theory]
    [MemberData(nameof(HtmlBlocks))]
    public void WhenTextIsMovedPastRawHtml_ThenItStandsApartFromTheHtmlOnTheOtherSide(string html, HtmlBlockType type)
    {
        // Arrange
        var source = $"Before.\n\n{html}\n\nAfter.\n";
        var page = ParseHtmlPage(html, type, source);
        var afterLine = CountLines(html) + 4;

        // Act
        var directionsBefore = MarkdownPageOperations.GetTextMoveDirections(page, TextAt(page, 1));
        var changeBefore = MarkdownPageOperations.TryMoveText(page, NoSubjects, TextAt(page, 1), 1, out var refusalBefore);
        var directionsAfter = MarkdownPageOperations.GetTextMoveDirections(page, TextAt(page, afterLine));
        var changeAfter = MarkdownPageOperations.TryMoveText(page, NoSubjects, TextAt(page, afterLine), -1, out var refusalAfter);

        // Assert
        Assert.Equal((false, true), directionsBefore);
        Assert.Equal((true, false), directionsAfter);
        AssertHtmlIsKept(changeBefore, refusalBefore, html, $"{html}\n\nBefore.\n\nAfter.\n");
        AssertHtmlIsKept(changeAfter, refusalAfter, html, $"Before.\n\nAfter.\n\n{html}\n");
    }

    [Theory]
    [MemberData(nameof(HtmlBlocksThatEndAtAMarker))]
    public void WhenTextDirectlyAroundRawHtmlIsChangedRemovedOrMoved_ThenAnEmptyLineKeepsItApartFromTheHtml(string html, HtmlBlockType type)
    {
        // Arrange: the HTML interrupts the paragraph before it and ends at its marker, so the text after it is a paragraph.
        var source = $"Before.\n{html}\nAfter.\n";
        var page = ParseHtmlPage(html, type, source);
        var afterLine = CountLines(html) + 2;

        // Act
        var changeBefore = MarkdownPageOperations.TryReplaceText(page, NoSubjects, TextAt(page, 1), ChangedText, out var refusalChangeBefore);
        var changeAfter = MarkdownPageOperations.TryReplaceText(page, NoSubjects, TextAt(page, afterLine), ChangedText, out var refusalChangeAfter);
        var removeBefore = MarkdownPageOperations.TryRemoveText(page, NoSubjects, TextAt(page, 1), out var refusalRemoveBefore);
        var removeAfter = MarkdownPageOperations.TryRemoveText(page, NoSubjects, TextAt(page, afterLine), out var refusalRemoveAfter);
        var moveBefore = MarkdownPageOperations.TryMoveText(page, NoSubjects, TextAt(page, 1), 1, out var refusalMoveBefore);
        var moveAfter = MarkdownPageOperations.TryMoveText(page, NoSubjects, TextAt(page, afterLine), -1, out var refusalMoveAfter);

        // Assert
        AssertHtmlIsKept(changeBefore, refusalChangeBefore, html, $"{ChangedText}\n\n{html}\nAfter.\n");
        AssertHtmlIsKept(changeAfter, refusalChangeAfter, html, $"Before.\n{html}\n\n{ChangedText}\n");
        AssertHtmlIsKept(removeBefore, refusalRemoveBefore, html, $"{html}\nAfter.\n");
        AssertHtmlIsKept(removeAfter, refusalRemoveAfter, html, $"Before.\n{html}\n");
        AssertHtmlIsKept(moveBefore, refusalMoveBefore, html, $"{html}\n\nBefore.\n\nAfter.\n");
        AssertHtmlIsKept(moveAfter, refusalMoveAfter, html, $"Before.\n\nAfter.\n\n{html}\n");
    }

    [Theory]
    [InlineData("<div align=\"center\">\n<img src=\"a.png\">\n</div>", HtmlBlockType.InterruptingBlock)]
    public void WhenTextDirectlyBeforeAnInterruptingHtmlBlockIsChangedOrMoved_ThenAnEmptyLineKeepsItApartFromTheHtml(string html, HtmlBlockType type)
    {
        // Arrange: the HTML block interrupts the paragraph before it and takes the lines up to the next empty line.
        var source = $"Before.\n{html}\n\nAfter.\n";
        var page = ParseHtmlPage(html, type, source);

        // Act
        var change = MarkdownPageOperations.TryReplaceText(page, NoSubjects, TextAt(page, 1), ChangedText, out var changeRefusal);
        var remove = MarkdownPageOperations.TryRemoveText(page, NoSubjects, TextAt(page, 1), out var removeRefusal);
        var move = MarkdownPageOperations.TryMoveText(page, NoSubjects, TextAt(page, 1), 1, out var moveRefusal);

        // Assert
        AssertHtmlIsKept(change, changeRefusal, html, $"{ChangedText}\n\n{html}\n\nAfter.\n");
        AssertHtmlIsKept(remove, removeRefusal, html, $"{html}\n\nAfter.\n");
        AssertHtmlIsKept(move, moveRefusal, html, $"{html}\n\nBefore.\n\nAfter.\n");
    }

    [Theory]
    [InlineData("<div align=\"center\">\n<img src=\"a.png\">\n</div>", HtmlBlockType.InterruptingBlock)]
    [InlineData("<my-element>\nx\n</my-element>", HtmlBlockType.NonInterruptingBlock)]
    public void WhenTheTextAfterAnHtmlBlockThatEndsAtAnEmptyLineIsRemovedOrChanged_ThenTheEmptyLineStays(string html, HtmlBlockType type)
    {
        // Arrange: the heading and the paragraph after it touch, so a removal leaves no empty line of its own behind.
        var source = $"{html}\n\n# Heading\nAfter.\n";
        var page = ParseHtmlPage(html, type, source);
        var headingLine = CountLines(html) + 2;

        // Act
        var remove = MarkdownPageOperations.TryRemoveText(page, NoSubjects, TextAt(page, headingLine), out var removeRefusal);
        var change = MarkdownPageOperations.TryReplaceText(page, NoSubjects, TextAt(page, headingLine), "Changed\nlines.", out var changeRefusal);

        // Assert
        AssertHtmlIsKept(remove, removeRefusal, html, $"{html}\n\nAfter.\n");
        AssertHtmlIsKept(change, changeRefusal, html, $"{html}\n\nChanged\nlines.\n\nAfter.\n");
    }

    // Text that opens a block which goes on until a closing marker, or to the end of the page without one.
    public static TheoryData<string, string> OpeningTextsAndHtmlBlocks()
    {
        var data = new TheoryData<string, string>();
        foreach (var text in (string[])["<!-- open", "<script>", "<pre>", "<?open", "<![CDATA[", "```"])
        {
            foreach (var row in HtmlBlocks())
            {
                data.Add(text, (string)row[0]);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(OpeningTextsAndHtmlBlocks))]
    public void WhenChangedTextOpensSomethingThatTakesTheRawHtmlAfterIt_ThenTheChangeIsRefused(string text, string html)
    {
        // Arrange
        var page = MarkdownPageParser.Parse($"Before.\n\n{html}\n\nAfter.\n");

        // Act
        var change = MarkdownPageOperations.TryReplaceText(page, NoSubjects, TextAt(page, 1), text, out var refusal);

        // Assert
        Assert.Null(change);
        Assert.Equal("Changing the text would change other blocks of the page.", refusal);
    }

    [Fact]
    public void WhenRawHtmlIsChangedOrRemovedAsText_ThenTheChangeIsRefused()
    {
        // Arrange
        var page = MarkdownPageParser.Parse("Before.\n\n<div>x</div>\n\nAfter.\n");
        var htmlTextBlock = TextAt(page, 3);

        // Act
        var change = MarkdownPageOperations.TryReplaceText(page, NoSubjects, htmlTextBlock, ChangedText, out var changeRefusal);
        var remove = MarkdownPageOperations.TryRemoveText(page, NoSubjects, htmlTextBlock, out var removeRefusal);
        var directions = MarkdownPageOperations.GetTextMoveDirections(page, htmlTextBlock);

        // Assert
        Assert.Null(change);
        Assert.Null(remove);
        Assert.Equal("The text cannot be edited as text.", changeRefusal);
        Assert.Equal("The text cannot be edited as text.", removeRefusal);
        Assert.Equal((false, false), directions);
    }

    [Fact]
    public void WhenTextIsMovedPastRawHtmlThatOpensAnElementALaterBlockCloses_ThenItGoesPastTheClosingBlock()
    {
        // Arrange
        var page = MarkdownPageParser.Parse("Before.\n\n<div class=\"note\">\n\nInside.\n\n</div>\n\nAfter.\n");

        // Act
        var down = MarkdownPageOperations.TryMoveText(page, NoSubjects, TextAt(page, 1), 1, out var downRefusal);
        var up = MarkdownPageOperations.TryMoveText(page, NoSubjects, TextAt(page, 9), -1, out var upRefusal);

        // Assert
        Assert.Null(downRefusal);
        Assert.Equal("<div class=\"note\">\n\nInside.\n\n</div>\n\nBefore.\n\nAfter.\n", down!.Write.Page.Source);
        Assert.Null(upRefusal);
        Assert.Equal("Before.\n\nAfter.\n\n<div class=\"note\">\n\nInside.\n\n</div>\n", up!.Write.Page.Source);
    }

    [Fact]
    public void WhenTextIsMovedPastAWidgetIntoAnElementThatRawHtmlOpens_ThenTheMoveIsRefused()
    {
        // Arrange: the moved text would come after the opening tag, inside the element.
        var page = MarkdownPageParser.Parse("<div>\n\n:: Demo.One\n::\n\nText.\n\n</div>\n");
        var textBlock = TextAt(page, 6);

        // Act
        var directions = MarkdownPageOperations.GetTextMoveDirections(page, textBlock);
        var change = MarkdownPageOperations.TryMoveText(page, NoSubjects, textBlock, -1, out var refusal);

        // Assert
        Assert.False(directions.CanMoveUp);
        Assert.Null(change);
        Assert.Equal("The text would join the HTML next to it.", refusal);
    }
}
