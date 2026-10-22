using HomeBlaze.Storage.Pages;
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Tests.Pages;

public class MarkdownPageOperationsTests
{
    private static readonly IReadOnlyDictionary<int, IInterceptorSubject> NoSubjects = new Dictionary<int, IInterceptorSubject>();

    private const string Card = "intro\n\n:: Demo.Card\n::: Row Label=A\n:::\n::: Row Label=B\n:::\n::\n\noutro\n";

    private static int InsertLine(MarkdownPage page, MarkdownBlock? owner, MarkdownBlock? before) =>
        MarkdownPageOperations.GetInsertLine(page, owner, before?.Line);

    private static MarkdownTextBlock TextAt(MarkdownPage page, int line) =>
        MarkdownTextBlocks.Find(page).Single(textBlock => textBlock.Line == line);

    [Fact]
    public void WhenARowIsRemoved_ThenItsLinesAreGoneAndTheOtherRowKeepsItsSubject()
    {
        // Arrange
        var page = MarkdownPageParser.Parse(Card);
        var rowA = page.Blocks[0].Children[0];
        var rowB = page.Blocks[0].Children[1];
        var subjectB = new TestLeaf();
        var subjects = new Dictionary<int, IInterceptorSubject> { [rowA.Index] = new TestLeaf(), [rowB.Index] = subjectB };

        // Act
        var change = MarkdownPageOperations.TryRemove(page, subjects, rowA, out var refusal);

        // Assert
        Assert.Null(refusal);
        Assert.NotNull(change);
        Assert.Equal("intro\n\n:: Demo.Card\n::: Row Label=B\n:::\n::\n\noutro\n", change.Write.Page.Source);
        Assert.Same(subjectB, Assert.Single(change.Write.BlockSubjects).Value);
        Assert.Equal(1, Assert.Single(change.Write.BlockSubjects).Key);
        Assert.True(change.Write.HasInsertedOrRemovedBlocks);
        Assert.Same(page.Blocks[0], change.ChangedBlock);
    }

    [Fact]
    public void WhenARowIsMovedDown_ThenItGoesAfterTheNextRowAndKeepsItsSubject()
    {
        // Arrange
        var page = MarkdownPageParser.Parse(Card);
        var rowA = page.Blocks[0].Children[0];
        var subjectA = new TestLeaf();
        var subjects = new Dictionary<int, IInterceptorSubject> { [rowA.Index] = subjectA };

        // Act
        var change = MarkdownPageOperations.TryMove(page, subjects, rowA, 1, out var refusal);

        // Assert
        Assert.Null(refusal);
        Assert.NotNull(change);
        Assert.Equal("intro\n\n:: Demo.Card\n::: Row Label=B\n:::\n::: Row Label=A\n:::\n::\n\noutro\n", change.Write.Page.Source);
        var movedIndex = change.Write.Page.Blocks[0].Children[1].Index;
        Assert.Same(subjectA, change.Write.BlockSubjects[movedIndex]);
    }

    [Fact]
    public void WhenAWidgetIsMovedUpPastText_ThenItGoesBeforeTheTextAndTheTextStays()
    {
        // Arrange
        const string source = "a\n\n:: Demo.One\n::\n\ntext\n\n:: Demo.Two\n::\n";
        var page = MarkdownPageParser.Parse(source);

        // Act
        var change = MarkdownPageOperations.TryMove(page, NoSubjects, page.Blocks[1], -1, out var refusal);

        // Assert
        Assert.Null(refusal);
        Assert.Equal("a\n\n:: Demo.One\n::\n\n:: Demo.Two\n::\n\ntext\n", change!.Write.Page.Source);
        Assert.Null(change.ChangedBlock);
    }

    [Theory]
    [InlineData(0, -1)]
    [InlineData(1, 1)]
    public void WhenThereIsNoRowInTheDirection_ThenTheMoveIsRefused(int rowPosition, int direction)
    {
        // Arrange
        var page = MarkdownPageParser.Parse(Card);

        // Act
        var change = MarkdownPageOperations.TryMove(page, NoSubjects, page.Blocks[0].Children[rowPosition], direction, out var refusal);

        // Assert
        Assert.Null(change);
        Assert.Contains("has nothing", refusal);
    }

    [Fact]
    public void WhenTheOnlyOtherSlotBlockIsOfAnotherSlot_ThenTheMoveIsRefused()
    {
        // Arrange
        var page = MarkdownPageParser.Parse(":: Demo.Card\n::: Row\n:::\n::: Item\n:::\n::\n");

        // Act
        var change = MarkdownPageOperations.TryMove(page, NoSubjects, page.Blocks[0].Children[0], 1, out _);

        // Assert
        Assert.Null(change);
        Assert.Null(MarkdownPageOperations.FindMoveLine(page, page.Blocks[0].Children[0], 1));
    }

    [Fact]
    public void WhenARowWithoutClosingLineIsMovedPastARow_ThenTheTextAfterTheOtherRowStaysThere()
    {
        // Arrange
        var page = MarkdownPageParser.Parse(":: Demo.Card\n::: Row Label=A\n:::\nbetween\n::: Row Label=B\nsecond\n::\n");

        // Act
        var change = MarkdownPageOperations.TryMove(page, NoSubjects, page.Blocks[0].Children[1], -1, out var refusal);

        // Assert
        Assert.Null(refusal);
        Assert.Equal(":: Demo.Card\n::: Row Label=B\nsecond\n::: Row Label=A\n:::\nbetween\n::\n", change!.Write.Page.Source);
    }

    [Fact]
    public void WhenAWidgetIsMovedUpPastAWidget_ThenItStandsBeforeItWithAnEmptyLineBetween()
    {
        // Arrange
        var page = MarkdownPageParser.Parse("a\n\n:: Demo.One\n::\n\n:: Demo.Two\n::\n");

        // Act
        var change = MarkdownPageOperations.TryMove(page, NoSubjects, page.Blocks[1], -1, out var refusal);

        // Assert
        Assert.Null(refusal);
        Assert.Equal("a\n\n:: Demo.Two\n::\n\n:: Demo.One\n::\n", change!.Write.Page.Source);
    }

    [Fact]
    public void WhenAWidgetFollowsText_ThenItsMoveLineUpIsTheLineOfTheText()
    {
        // Arrange
        var page = MarkdownPageParser.Parse("Text.\n\n:: Demo.One\n::\n");

        // Act & Assert
        Assert.Equal(1, MarkdownPageOperations.FindMoveLine(page, page.Blocks[0], -1));
        Assert.Null(MarkdownPageOperations.FindMoveLine(page, page.Blocks[0], 1));
    }

    [Fact]
    public void WhenThePageHasALoneCarriageReturn_ThenMovingIsRefused()
    {
        // Arrange
        var page = MarkdownPageParser.Parse("a\r:: Demo.One\n::\n\n:: Demo.Two\n::\n");

        // Act
        var change = MarkdownPageOperations.TryMove(page, NoSubjects, page.Blocks[1], -1, out var refusal);

        // Assert
        Assert.Null(change);
        Assert.Contains("carriage return", refusal);
    }

    [Theory]
    [InlineData("- item\n\n  :: Demo.Inner\n  ::\n\n:: Demo.One\n::\n", ":: Demo.One\n::\n\n- item\n\n  :: Demo.Inner\n  ::\n")]
    [InlineData("> quote\n> :: Demo.Inner\n> ::\n\n:: Demo.One\n::\n", ":: Demo.One\n::\n\n> quote\n> :: Demo.Inner\n> ::\n")]
    [InlineData(":: {.ab}\n::: Demo.Inner\n:::\n::\n\n:: Demo.One\n::\n", ":: Demo.One\n::\n\n:: {.ab}\n::: Demo.Inner\n:::\n::\n")]
    public void WhenAWidgetIsMovedPastAContainerThatHoldsABlock_ThenItPassesTheContainerAsOneItemAndTheBlockInsideStays(
        string source, string expected)
    {
        // Arrange: a block inside a list item, a quote or a style block is an error block, which is no item of its own.
        var page = MarkdownPageParser.Parse(source);
        var inner = page.AllBlocks[0];

        // Act
        var change = MarkdownPageOperations.TryMove(page, NoSubjects, page.Blocks[^1], -1, out var refusal);
        var moved = change!.Write.Page;
        var back = MarkdownPageOperations.TryMove(moved, NoSubjects, moved.Blocks[0], 1, out _);

        // Assert
        Assert.Null(refusal);
        Assert.Equal(expected, moved.Source);
        Assert.Equal(source, back!.Write.Page.Source);
        Assert.Equal(MarkdownBlockKind.Invalid, inner.Kind);
        Assert.Null(MarkdownPageOperations.FindMoveLine(page, inner, -1));
        Assert.Null(MarkdownPageOperations.FindMoveLine(page, inner, 1));
    }

    [Theory]
    [InlineData(":: Demo.Card\n::: Row\n:::: Demo.One\n::::\n:::\n::: Row\n:::\n::\n")]
    [InlineData(":: Demo.Card\n::: Demo.One\n:::\n::: Footer\n:::\n::\n")]
    [InlineData(":: Demo.Card\n::: Demo.One\n:::\n::: Footer\n:::\ntrailing\n::\n")]
    public void WhenAWidgetIsAloneInItsContent_ThenItCannotMoveIntoAnotherSlotBlockOrOutOfItsOwner(string source)
    {
        // Arrange
        var page = MarkdownPageParser.Parse(source);
        var widget = page.AllBlocks.Single(block => block.Head?.Name == "Demo.One");

        // Act
        var change = MarkdownPageOperations.TryMove(page, NoSubjects, widget, 1, out var refusal);

        // Assert
        Assert.Null(change);
        Assert.Contains("has nothing", refusal);
        Assert.Null(MarkdownPageOperations.FindMoveLine(page, widget, -1));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, -1)]
    public void WhenTheLastBlockEndsTheTextWithoutALineEnding_ThenTheMoveKeepsTheTextEndingWithout(int blockPosition, int direction)
    {
        // Arrange
        const string source = "x\n\n:: Demo.One\n::\n\n:: Demo.Two\n::";
        var page = MarkdownPageParser.Parse(source);

        // Act
        var change = MarkdownPageOperations.TryMove(page, NoSubjects, page.Blocks[blockPosition], direction, out _);

        // Assert
        Assert.Equal("x\n\n:: Demo.Two\n::\n\n:: Demo.One\n::", change!.Write.Page.Source);
    }

    [Fact]
    public void WhenRowsHaveNoClosingLines_ThenTheMoveSwapsTheirContent()
    {
        // Arrange
        const string source = ":: Demo.Card\n::: Row Label=A\nfirst\n::: Row Label=B\nsecond\n::\n";
        var page = MarkdownPageParser.Parse(source);

        // Act
        var change = MarkdownPageOperations.TryMove(page, NoSubjects, page.Blocks[0].Children[1], -1, out var refusal);

        // Assert
        Assert.Null(refusal);
        Assert.Equal(":: Demo.Card\n::: Row Label=B\nsecond\n::: Row Label=A\nfirst\n::\n", change!.Write.Page.Source);
    }

    [Fact]
    public void WhenWindowsLineEndingsAreUsed_ThenTheMoveKeepsThem()
    {
        // Arrange
        var page = MarkdownPageParser.Parse(Card.Replace("\n", "\r\n"));

        // Act
        var change = MarkdownPageOperations.TryMove(page, NoSubjects, page.Blocks[0].Children[0], 1, out _);

        // Assert
        Assert.Equal(
            "intro\n\n:: Demo.Card\n::: Row Label=B\n:::\n::: Row Label=A\n:::\n::\n\noutro\n".Replace("\n", "\r\n"),
            change!.Write.Page.Source);
    }

    [Fact]
    public void WhenBlocksAreCounted_ThenTheBlockAndEveryBlockInsideItCount()
    {
        // Arrange
        var page = MarkdownPageParser.Parse(Card);

        // Act & Assert
        Assert.Equal(3, MarkdownPageOperations.CountBlocks(page.Blocks[0]));
    }

    [Fact]
    public void WhenThePageHasALoneCarriageReturn_ThenRemovingIsRefused()
    {
        // Arrange
        var page = MarkdownPageParser.Parse("a\r:: Demo.One\n::\n");

        // Act
        var change = MarkdownPageOperations.TryRemove(page, NoSubjects, page.Blocks[0], out var refusal);

        // Assert
        Assert.Null(change);
        Assert.Contains("carriage return", refusal);
    }

    [Fact]
    public void WhenAWidgetWithASlotChildIsMovedPastAnotherWidget_ThenEverySubjectFollowsItsBlock()
    {
        // Arrange
        var page = MarkdownPageParser.Parse(":: Demo.One\n::: Row\n:::\n::\n\n:: Demo.Two\n::\n");
        var widgetOne = page.Blocks[0];
        var row = widgetOne.Children[0];
        var widgetTwo = page.Blocks[1];
        var subjectOne = new TestLeaf();
        var subjectRow = new TestLeaf();
        var subjectTwo = new TestLeaf();
        var subjects = new Dictionary<int, IInterceptorSubject>
        {
            [widgetOne.Index] = subjectOne, [row.Index] = subjectRow, [widgetTwo.Index] = subjectTwo
        };

        // Act
        var change = MarkdownPageOperations.TryMove(page, subjects, widgetOne, 1, out var refusal);

        // Assert
        Assert.Null(refusal);
        Assert.Equal(":: Demo.Two\n::\n\n:: Demo.One\n::: Row\n:::\n::\n", change!.Write.Page.Source);
        var result = change.Write;
        Assert.Same(subjectTwo, result.BlockSubjects[result.Page.Blocks[0].Index]);
        Assert.Same(subjectOne, result.BlockSubjects[result.Page.Blocks[1].Index]);
        Assert.Same(subjectRow, result.BlockSubjects[result.Page.Blocks[1].Children[0].Index]);
    }

    [Fact]
    public void WhenAWidgetIsInsertedBeforeABlockOnThePage_ThenItStandsBeforeItWithEmptyLinesAround()
    {
        // Arrange
        var page = MarkdownPageParser.Parse("intro\n\n:: Demo.One\n::\n");

        // Act
        var change = MarkdownPageOperations.TryInsertWidget(
            page, NoSubjects, owner: null, InsertLine(page, null, page.Blocks[0]), "Demo.New", "fresh", """{ "Min": 1 }""", out var refusal);

        // Assert
        Assert.Null(refusal);
        Assert.Equal("intro\n\n:: Demo.New#fresh Min=1\n::\n\n:: Demo.One\n::\n", change!.Write.Page.Source);
        Assert.Null(change.ChangedBlock);
    }

    [Fact]
    public void WhenAWidgetIsAppendedToThePage_ThenItEndsThePage()
    {
        // Arrange
        var page = MarkdownPageParser.Parse("intro\n");

        // Act
        var change = MarkdownPageOperations.TryInsertWidget(page, NoSubjects, null, InsertLine(page, null, null), "Demo.New", null, "{}", out _);

        // Assert
        Assert.Equal("intro\n\n:: Demo.New\n::\n", change!.Write.Page.Source);
    }

    [Fact]
    public void WhenAWidgetIsAppendedToAnEmptyCard_ThenItIsInsideTheCardWithALongerFence()
    {
        // Arrange
        var page = MarkdownPageParser.Parse("::Demo.Card Title=A\n::\n");
        var card = page.Blocks[0];

        // Act
        var change = MarkdownPageOperations.TryInsertWidget(page, NoSubjects, card, InsertLine(page, card, null), "Demo.New", null, "{}", out var refusal);

        // Assert
        Assert.Null(refusal);
        Assert.Equal("::Demo.Card Title=A\n\n::: Demo.New\n:::\n\n::\n", change!.Write.Page.Source);
        Assert.Same(card, change.ChangedBlock);
        Assert.Equal(MarkdownBlockKind.Widget, change.Write.Page.Blocks[0].Children.Single().Kind);
    }

    [Fact]
    public void WhenAWidgetIsAppendedToACardWithAFooter_ThenItGoesBeforeTheFooter()
    {
        // Arrange
        var page = MarkdownPageParser.Parse(":: Demo.Card\nbody\n::: Footer\nfoot\n:::\n::\n");

        // Act
        var change = MarkdownPageOperations.TryInsertWidget(page, NoSubjects, page.Blocks[0], InsertLine(page, page.Blocks[0], null), "Demo.New", null, "{}", out var refusal);

        // Assert
        Assert.Null(refusal);
        Assert.Equal(":: Demo.Card\nbody\n\n::: Demo.New\n:::\n\n::: Footer\nfoot\n:::\n::\n", change!.Write.Page.Source);
    }

    [Fact]
    public void WhenThePageHasNoClosingLines_ThenTheInsertedWidgetHasNoneEither()
    {
        // Arrange
        var page = MarkdownPageParser.Parse(":: Demo.One\n\n:: Demo.Two\n");

        // Act
        var change = MarkdownPageOperations.TryInsertWidget(page, NoSubjects, null, InsertLine(page, null, page.Blocks[1]), "Demo.New", null, "{}", out var refusal);

        // Assert
        Assert.Null(refusal);
        Assert.Equal(":: Demo.One\n\n:: Demo.New\n\n:: Demo.Two\n", change!.Write.Page.Source);
    }

    [Fact]
    public void WhenAWidgetIsAppendedToASlotBlockWithoutClosingLinesBeforeItsNextSibling_ThenItIsInsideThatSlotBlock()
    {
        // Arrange
        var page = MarkdownPageParser.Parse(":: Demo.Flow\n::: Item\n:::: Demo.Tile\n::: Item\n:::: Demo.Tile\n");
        var item = page.Blocks[0].Children[0];

        // Act
        var change = MarkdownPageOperations.TryInsertWidget(page, NoSubjects, item, InsertLine(page, item, null), "Demo.New", null, "{}", out var refusal);

        // Assert
        Assert.Null(refusal);
        Assert.Equal(":: Demo.Flow\n::: Item\n:::: Demo.Tile\n\n:::: Demo.New\n\n::: Item\n:::: Demo.Tile\n", change!.Write.Page.Source);
        Assert.Same(item, change.ChangedBlock);
        var newItem = change.Write.Page.Blocks[0].Children[0];
        Assert.Equal(2, newItem.Children.Count);
        Assert.Equal(MarkdownBlockKind.Widget, newItem.Children[1].Kind);
        Assert.Equal(5, newItem.Children[1].Line);
        Assert.Single(change.Write.Page.Blocks[0].Children[1].Children);
    }

    [Fact]
    public void WhenASlotBlockIsAppended_ThenItFollowsTheLastBlockOfTheSlotLikeIt()
    {
        // Arrange
        var page = MarkdownPageParser.Parse("::Demo.List\n::: Value Label=A\n:::\n::\n");

        // Act
        var change = MarkdownPageOperations.TryAppendSlotBlock(page, NoSubjects, page.Blocks[0], "Value", """{ "Label": "B" }""", out var refusal);

        // Assert
        Assert.Null(refusal);
        Assert.Equal("::Demo.List\n::: Value Label=A\n:::\n::: Value Label=B\n:::\n::\n", change!.Write.Page.Source);
        Assert.Same(page.Blocks[0], change.ChangedBlock);
    }

    [Fact]
    public void WhenAWidgetIsInsertedInsideAParagraph_ThenTheInsertIsRefused()
    {
        // Arrange
        var page = MarkdownPageParser.Parse("First line\nsecond line\n");

        // Act
        var change = MarkdownPageOperations.TryInsertWidget(page, NoSubjects, null, 2, "Demo.New", null, "{}", out var refusal);

        // Assert
        Assert.Null(change);
        Assert.Contains("would change other blocks", refusal);
    }

    [Fact]
    public void WhenAWidgetIsInserted_ThenTheChangeNamesItsBlock()
    {
        // Arrange
        var page = MarkdownPageParser.Parse("intro\n\n:: Demo.One\n::\n");

        // Act
        var change = MarkdownPageOperations.TryInsertWidget(
            page, NoSubjects, owner: null, InsertLine(page, null, page.Blocks[0]), "Demo.New", null, "{}", out _);

        // Assert
        Assert.Equal(change!.Write.Page.Blocks[0].Index, change.ResultBlockIndex);
    }

    [Fact]
    public void WhenASlotBlockIsAppended_ThenTheChangeNamesItsBlock()
    {
        // Arrange
        var page = MarkdownPageParser.Parse("::Demo.List\n::: Value Label=A\n:::\n::\n");

        // Act
        var change = MarkdownPageOperations.TryAppendSlotBlock(page, NoSubjects, page.Blocks[0], "Value", "{}", out _);

        // Assert
        Assert.Equal(change!.Write.Page.Blocks[0].Children[1].Index, change.ResultBlockIndex);
    }

    [Fact]
    public void WhenAParagraphIsReplaced_ThenItsLinesHaveTheNewText()
    {
        // Arrange
        var page = MarkdownPageParser.Parse("# Title\n\nOld text.\n\n:: Demo.One\n::\n");

        // Act
        var change = MarkdownPageOperations.TryReplaceText(page, NoSubjects, TextAt(page, 3), "New text.\nOn two lines.", out var refusal);

        // Assert
        Assert.Null(refusal);
        Assert.Equal("# Title\n\nNew text.\nOn two lines.\n\n:: Demo.One\n::\n", change!.Write.Page.Source);
        Assert.Equal(3, change.ResultTextLine);
        Assert.False(change.Write.HasInsertedOrRemovedBlocks);
        Assert.Null(change.ChangedBlock);
    }

    [Fact]
    public void WhenAHeadingRightBeforeAParagraphBecomesAParagraph_ThenAnEmptyLineKeepsThemApart()
    {
        // Arrange
        var page = MarkdownPageParser.Parse("# Title\nText.\n");

        // Act
        var change = MarkdownPageOperations.TryReplaceText(page, NoSubjects, TextAt(page, 1), "Intro.", out var refusal);

        // Assert
        Assert.Null(refusal);
        Assert.Equal("Intro.\n\nText.\n", change!.Write.Page.Source);
    }

    [Fact]
    public void WhenTheTextOfACardIsReplaced_ThenItStaysInTheCard()
    {
        // Arrange
        var page = MarkdownPageParser.Parse(":: Demo.Card\nOld.\n::\n");

        // Act
        var change = MarkdownPageOperations.TryReplaceText(page, NoSubjects, TextAt(page, 2), "New.", out var refusal);

        // Assert
        Assert.Null(refusal);
        Assert.Equal(":: Demo.Card\nNew.\n::\n", change!.Write.Page.Source);
        Assert.Same(page.Blocks[0], change.ChangedBlock);
    }

    [Fact]
    public void WhenTheLastTextOfABlockWithoutClosingLineIsReplaced_ThenTheBlockKeepsIt()
    {
        // Arrange
        var page = MarkdownPageParser.Parse(":: Demo.Card\nold\n\n:: Demo.Two\n::\n");

        // Act
        var change = MarkdownPageOperations.TryReplaceText(page, NoSubjects, TextAt(page, 2), "new", out var refusal);

        // Assert
        Assert.Null(refusal);
        Assert.Equal(":: Demo.Card\nnew\n\n:: Demo.Two\n::\n", change!.Write.Page.Source);
    }

    [Fact]
    public void WhenTextIsReplacedOnAPageWithWindowsLineEndings_ThenTheNewTextHasThem()
    {
        // Arrange
        var page = MarkdownPageParser.Parse("Old.\r\n\r\nB\r\n");

        // Act
        var change = MarkdownPageOperations.TryReplaceText(page, NoSubjects, TextAt(page, 1), "One.\nTwo.", out _);

        // Assert
        Assert.Equal("One.\r\nTwo.\r\n\r\nB\r\n", change!.Write.Page.Source);
    }

    [Fact]
    public void WhenTheTextIsReplacedWithNothing_ThenItIsRemoved()
    {
        // Arrange
        var page = MarkdownPageParser.Parse("a\n\nP\n\nb\n");

        // Act
        var change = MarkdownPageOperations.TryReplaceText(page, NoSubjects, TextAt(page, 3), "  \n", out var refusal);

        // Assert
        Assert.Null(refusal);
        Assert.Equal("a\n\nb\n", change!.Write.Page.Source);
    }

    [Fact]
    public void WhenTextIsInsertedBeforeAWidget_ThenItStandsThereWithEmptyLinesAround()
    {
        // Arrange
        var page = MarkdownPageParser.Parse("intro\n\n:: Demo.One\n::\n");

        // Act
        var change = MarkdownPageOperations.TryInsertText(
            page, NoSubjects, null, InsertLine(page, null, page.Blocks[0]), "New.", out var refusal);

        // Assert
        Assert.Null(refusal);
        Assert.Equal("intro\n\nNew.\n\n:: Demo.One\n::\n", change!.Write.Page.Source);
        Assert.Equal(3, change.ResultTextLine);
    }

    [Fact]
    public void WhenTextIsAppendedToACard_ThenItIsTheLastContentOfTheCard()
    {
        // Arrange
        var page = MarkdownPageParser.Parse("::Demo.Card Title=A\n::\n");
        var card = page.Blocks[0];

        // Act
        var change = MarkdownPageOperations.TryInsertText(page, NoSubjects, card, InsertLine(page, card, null), "New.", out var refusal);

        // Assert
        Assert.Null(refusal);
        Assert.Equal("::Demo.Card Title=A\n\nNew.\n\n::\n", change!.Write.Page.Source);
        Assert.Same(card, change.ChangedBlock);
    }

    [Fact]
    public void WhenInsertedTextWouldChangeTheRestOfThePage_ThenTheInsertIsRefused()
    {
        // Arrange
        var page = MarkdownPageParser.Parse("intro\n\n:: Demo.One\n::\n\nafter\n");

        // Act
        var change = MarkdownPageOperations.TryInsertText(page, NoSubjects, null, 3, "```", out var refusal);

        // Assert
        Assert.Null(change);
        Assert.Contains("would change other blocks", refusal);
    }

    [Fact]
    public void WhenTheBlockBeforeHasNoClosingLine_ThenTextCannotBeInsertedAfterIt()
    {
        // Arrange
        var withoutClosingLines = MarkdownPageParser.Parse(":: Demo.One\n\n:: Demo.Two\n");
        var withClosingLines = MarkdownPageParser.Parse("intro\n\n:: Demo.One\n::\n");

        // Act & Assert
        Assert.False(MarkdownPageOperations.CanInsertTextAt(withoutClosingLines, null, withoutClosingLines.Blocks[1].Line));
        Assert.True(MarkdownPageOperations.CanInsertTextAt(withClosingLines, null, withClosingLines.Blocks[0].Line));
        Assert.True(MarkdownPageOperations.CanInsertTextAt(withClosingLines, null, MarkdownPageOperations.GetInsertLine(withClosingLines, null, null)));
    }

    [Fact]
    public void WhenTheTextOfABlockOfTextIsAskedFor_ThenItHasLineFeeds()
    {
        // Arrange
        var page = MarkdownPageParser.Parse("Old.\r\nSecond line.\r\n");

        // Act
        var text = MarkdownPageOperations.GetText(page, TextAt(page, 1));

        // Assert
        Assert.Equal("Old.\nSecond line.", text);
    }

    [Theory]
    [InlineData("- item\n  :: Demo.Card\n  text\n  ::\n")]
    [InlineData("> quote\n>\n> :: Demo.Card\n> text\n> ::\n")]
    [InlineData(":: {.ab}\nStyled.\n::\n")]
    public void WhenTextThatCannotBeEditedIsReplaced_ThenTheChangeIsRefused(string source)
    {
        // Arrange
        var page = MarkdownPageParser.Parse(source);
        var textBlock = TextAt(page, 1);

        // Act
        var change = MarkdownPageOperations.TryReplaceText(page, NoSubjects, textBlock, "New.", out var refusal);

        // Assert
        Assert.False(textBlock.IsEditable);
        Assert.Null(change);
        Assert.Contains("cannot be edited", refusal);
    }

    [Theory]
    [InlineData("- item\n  :: Demo.Card\n  text\n  ::\n")]
    [InlineData("> quote\n>\n> :: Demo.Card\n> text\n> ::\n")]
    [InlineData(":: {.ab}\nStyled.\n::\n")]
    public void WhenTextThatCannotBeEditedIsRemoved_ThenTheChangeIsRefused(string source)
    {
        // Arrange
        var page = MarkdownPageParser.Parse(source);

        // Act
        var change = MarkdownPageOperations.TryRemoveText(page, NoSubjects, TextAt(page, 1), out var refusal);

        // Assert
        Assert.Null(change);
        Assert.Contains("cannot be edited", refusal);
    }

    [Fact]
    public void WhenTextIsRemoved_ThenTheTextAroundItStaysApart()
    {
        // Arrange
        var page = MarkdownPageParser.Parse(":: Demo.Card\na\n\nP\n\nb\n::\n");

        // Act
        var change = MarkdownPageOperations.TryRemoveText(page, NoSubjects, TextAt(page, 4), out var refusal);

        // Assert
        Assert.Null(refusal);
        Assert.Equal(":: Demo.Card\na\n\nb\n::\n", change!.Write.Page.Source);
        Assert.Same(page.Blocks[0], change.ChangedBlock);
        Assert.False(change.Write.HasInsertedOrRemovedBlocks);
    }

    [Fact]
    public void WhenReplacedTextWouldBeTheConfigurationOfTheBlock_ThenTheChangeIsRefused()
    {
        // Arrange
        var page = MarkdownPageParser.Parse(":: Demo.Card\nOld.\n::\n");

        // Act
        var change = MarkdownPageOperations.TryReplaceText(page, NoSubjects, TextAt(page, 2), "```json\n{ \"Title\": \"A\" }\n```", out var refusal);

        // Assert
        Assert.Null(change);
        Assert.Contains("would change other blocks", refusal);
    }

    [Fact]
    public void WhenTheTextOfAnIndentedBlockIsAskedForAndReplaced_ThenItIsWithoutAndWithTheIndentation()
    {
        // Arrange
        var page = MarkdownPageParser.Parse("  :: Demo.Card\n  Old.\n  Second.\n  ::\n");
        var textBlock = TextAt(page, 2);

        // Act
        var text = MarkdownPageOperations.GetText(page, textBlock);
        var change = MarkdownPageOperations.TryReplaceText(page, NoSubjects, textBlock, "New.\n\n- item", out var refusal);

        // Assert
        Assert.Equal("Old.\nSecond.", text);
        Assert.Null(refusal);
        Assert.Equal("  :: Demo.Card\n  New.\n\n  - item\n  ::\n", change!.Write.Page.Source);
    }

    [Fact]
    public void WhenTheReplacedTextHasEmptyLinesAroundIt_ThenTheyAreLeftOut()
    {
        // Arrange
        var page = MarkdownPageParser.Parse("a\n\nOld.\n\nb\n");

        // Act
        var change = MarkdownPageOperations.TryReplaceText(page, NoSubjects, TextAt(page, 3), " \t\n  \nNew.\n \n", out var refusal);

        // Assert
        Assert.Null(refusal);
        Assert.Equal("a\n\nNew.\n\nb\n", change!.Write.Page.Source);
    }

    [Fact]
    public void WhenTextIsInsertedOnAPageWithWindowsLineEndings_ThenTheNewTextHasThem()
    {
        // Arrange
        var page = MarkdownPageParser.Parse("intro\r\n\r\n:: Demo.One\r\n::\r\n");

        // Act
        var change = MarkdownPageOperations.TryInsertText(
            page, NoSubjects, null, InsertLine(page, null, page.Blocks[0]), "One.\nTwo.", out var refusal);

        // Assert
        Assert.Null(refusal);
        Assert.Equal("intro\r\n\r\nOne.\r\nTwo.\r\n\r\n:: Demo.One\r\n::\r\n", change!.Write.Page.Source);
    }

    [Fact]
    public void WhenAParagraphIsReplacedWithTextThatHoldsAWidget_ThenTheWidgetIsANewBlockAndTheChangeNamesIt()
    {
        // Arrange
        var page = MarkdownPageParser.Parse("# Title\n\nOld text.\n\n:: Demo.One\n::\n");
        var one = new TestLeaf();
        var subjects = new Dictionary<int, IInterceptorSubject> { [page.Blocks[0].Index] = one };

        // Act
        var change = MarkdownPageOperations.TryReplaceText(
            page, subjects, TextAt(page, 3), "Before.\n\n:: Demo.New Name=x\n::\n\nAfter.", out var refusal);

        // Assert
        Assert.Null(refusal);
        Assert.Equal("# Title\n\nBefore.\n\n:: Demo.New Name=x\n::\n\nAfter.\n\n:: Demo.One\n::\n", change!.Write.Page.Source);
        Assert.True(change.Write.HasInsertedOrRemovedBlocks);
        Assert.Equal(change.Write.Page.Blocks[0].Index, change.ResultBlockIndex);
        Assert.Same(one, Assert.Single(change.Write.BlockSubjects).Value);
        Assert.Equal(change.Write.Page.Blocks[1].Index, Assert.Single(change.Write.BlockSubjects).Key);
    }

    [Fact]
    public void WhenTextWithTwoWidgetsIsInserted_ThenBothAreNewBlocksAndTheChangeNamesTheFirst()
    {
        // Arrange
        var page = MarkdownPageParser.Parse("intro\n\n:: Demo.One\n::\n");

        // Act
        var change = MarkdownPageOperations.TryInsertText(
            page, NoSubjects, null, InsertLine(page, null, page.Blocks[0]), ":: Demo.A\n::\n\n:: Demo.B\n::", out var refusal);

        // Assert
        Assert.Null(refusal);
        Assert.Equal("intro\n\n:: Demo.A\n::\n\n:: Demo.B\n::\n\n:: Demo.One\n::\n", change!.Write.Page.Source);
        Assert.Equal(change.Write.Page.Blocks[0].Index, change.ResultBlockIndex);
        Assert.True(change.Write.HasInsertedOrRemovedBlocks);
    }

    [Fact]
    public void WhenTheTextOfACardIsReplacedWithAWidgetWithSlotBlocks_ThenTheWidgetStandsInTheCardWithThem()
    {
        // Arrange
        var page = MarkdownPageParser.Parse(":: Demo.Card\nOld.\n::\n");

        // Act
        var change = MarkdownPageOperations.TryReplaceText(
            page, NoSubjects, TextAt(page, 2), "::: Demo.List\n:::: Row Label=A\n::::\n:::", out var refusal);

        // Assert
        Assert.Null(refusal);
        Assert.Equal(":: Demo.Card\n::: Demo.List\n:::: Row Label=A\n::::\n:::\n::\n", change!.Write.Page.Source);
        Assert.Equal(change.Write.Page.Blocks[0].Children[0].Index, change.ResultBlockIndex);
        Assert.Same(page.Blocks[0], change.ChangedBlock);
    }

    [Theory]
    [InlineData("::: Row Label=A\n:::")]
    [InlineData("::")]
    public void WhenTextHoldsASlotBlockOrAClosingLineAtItsTopLevel_ThenTheChangeIsRefused(string text)
    {
        // Arrange
        var page = MarkdownPageParser.Parse(":: Demo.Card\nOld.\n::\n");

        // Act
        var change = MarkdownPageOperations.TryReplaceText(page, NoSubjects, TextAt(page, 2), text, out var refusal);

        // Assert
        Assert.Null(change);
        Assert.Equal("Text can hold widgets, not the parts of a widget.", refusal);
    }

    [Fact]
    public void WhenInsertedTextHoldsASlotBlock_ThenTheInsertIsRefused()
    {
        // Arrange
        var page = MarkdownPageParser.Parse("intro\n\n:: Demo.One\n::\n\nafter\n");

        // Act
        var change = MarkdownPageOperations.TryInsertText(page, NoSubjects, null, 3, "::: Row\n:::", out var refusal);

        // Assert
        Assert.Null(change);
        Assert.Equal("Text can hold widgets, not the parts of a widget.", refusal);
    }

    [Fact]
    public void WhenAWidgetInTheTextOfACardHasNoMoreColonsThanTheCard_ThenTheChangeIsRefused()
    {
        // Arrange
        var page = MarkdownPageParser.Parse(":: Demo.Card\nOld.\n::\n");

        // Act
        var change = MarkdownPageOperations.TryReplaceText(page, NoSubjects, TextAt(page, 2), ":: Demo.X\n::", out var refusal);

        // Assert
        Assert.Null(change);
        Assert.Equal("A widget in this content needs at least 3 ':' characters.", refusal);
    }

    [Fact]
    public void WhenANewWidgetWithoutClosingLineWouldTakeTheTextAfterIt_ThenTheChangeIsRefused()
    {
        // Arrange
        var page = MarkdownPageParser.Parse("intro\n\nOld.\n\nafter\n");

        // Act
        var change = MarkdownPageOperations.TryReplaceText(page, NoSubjects, TextAt(page, 3), ":: Demo.New", out var refusal);

        // Assert
        Assert.Null(change);
        Assert.Contains("would change other blocks", refusal);
    }

    [Fact]
    public void WhenTheFenceOfANewWidgetIsAskedFor_ThenItIsTwoOnThePageAndOneMoreThanTheBlockAroundIt()
    {
        // Arrange
        var page = MarkdownPageParser.Parse(":: Demo.Card\n::: Footer\n:::\n::\n");

        // Act & Assert
        Assert.Equal(2, MarkdownPageOperations.GetWidgetFenceLength(null));
        Assert.Equal(3, MarkdownPageOperations.GetWidgetFenceLength(page.Blocks[0]));
        Assert.Equal(4, MarkdownPageOperations.GetWidgetFenceLength(page.Blocks[0].Children[0]));
    }

    [Theory]
    [InlineData(":: Demo.Copy#first\n::")]
    [InlineData(":: Demo.Outer\n::: Demo.Inner#first\n:::\n::")]
    [InlineData(":: Demo.A#twice\n::\n\n:: Demo.B#twice\n::")]
    public void WhenAWidgetInTextHasANameThatIsAlreadyUsed_ThenTheChangeIsRefusedNamingIt(string text)
    {
        // Arrange
        var page = MarkdownPageParser.Parse("Old.\n\n:: Demo.One#first\n::\n");
        var name = text.Contains("#twice") ? "twice" : "first";

        // Act
        var change = MarkdownPageOperations.TryReplaceText(page, NoSubjects, TextAt(page, 1), text, out var refusal);

        // Assert
        Assert.Null(change);
        Assert.Equal($"The name '{name}' is already used on this page.", refusal);
    }

    [Fact]
    public void WhenInsertedTextHasAWidgetWithANameOfAnotherBlock_ThenTheInsertIsRefused()
    {
        // Arrange
        var page = MarkdownPageParser.Parse(":: Demo.Card#card\n::: Footer#footer\n:::\n::\n");

        // Act
        var change = MarkdownPageOperations.TryInsertText(
            page, NoSubjects, null, InsertLine(page, null, page.Blocks[0]), ":: Demo.X#footer\n::", out var refusal);

        // Assert
        Assert.Null(change);
        Assert.Equal("The name 'footer' is already used on this page.", refusal);
    }

    [Fact]
    public void WhenTextHoldsAStyleBlockAtItsTopLevel_ThenTheChangeIsRefused()
    {
        // Arrange
        var page = MarkdownPageParser.Parse("intro\n\nOld.\n\nafter\n");

        // Act
        var change = MarkdownPageOperations.TryReplaceText(page, NoSubjects, TextAt(page, 3), ":: {.note}\nStyled.\n::", out var refusal);

        // Assert
        Assert.Null(change);
        Assert.Equal("Text can hold widgets, not style blocks.", refusal);
    }

    [Theory]
    [InlineData(":: Demo.X Title=\"open\n::")]
    [InlineData("- item\n\n  :: Demo.X\n  ::")]
    [InlineData("> :: Demo.X\n> ::")]
    [InlineData(":: Demo.X\n:::::\n::")]
    public void WhenTextHoldsAnInvalidBlock_ThenTheChangeIsRefusedWithItsError(string text)
    {
        // Arrange
        var page = MarkdownPageParser.Parse("intro\n\nOld.\n\nafter\n");
        var expected = MarkdownPageParser.Parse(text).AllBlocks.First(block => block.Kind == MarkdownBlockKind.Invalid).Error;

        // Act
        var change = MarkdownPageOperations.TryReplaceText(page, NoSubjects, TextAt(page, 3), text, out var refusal);

        // Assert
        Assert.NotNull(expected);
        Assert.Null(change);
        Assert.Equal(expected, refusal);
    }

    [Fact]
    public void WhenTextHoldsAWidgetWithTextInsideAndTextAfterIt_ThenTheResultTextLineIsTheTextAfterIt()
    {
        // Arrange
        var page = MarkdownPageParser.Parse("intro\n\nOld.\n\nafter\n");

        // Act
        var change = MarkdownPageOperations.TryReplaceText(
            page, NoSubjects, TextAt(page, 3), ":: Demo.New\nInside.\n::\n\nAfter the widget.", out var refusal);

        // Assert
        Assert.Null(refusal);
        Assert.Equal("intro\n\n:: Demo.New\nInside.\n::\n\nAfter the widget.\n\nafter\n", change!.Write.Page.Source);
        Assert.Equal(7, change.ResultTextLine);
        Assert.Equal(change.Write.Page.Blocks[0].Index, change.ResultBlockIndex);
    }

    [Fact]
    public void WhenTextHoldsOnlyAWidget_ThenTheChangeNamesNoTextLine()
    {
        // Arrange
        var page = MarkdownPageParser.Parse("intro\n\nOld.\n\nafter\n");

        // Act
        var change = MarkdownPageOperations.TryReplaceText(page, NoSubjects, TextAt(page, 3), ":: Demo.New\nInside.\n::", out var refusal);

        // Assert
        Assert.Null(refusal);
        Assert.Equal(0, change!.ResultTextLine);
    }

    [Fact]
    public void WhenTextIsMovedDownPastAWidget_ThenItStandsAfterTheWidgetAndEveryWidgetKeepsItsSubject()
    {
        // Arrange
        var page = MarkdownPageParser.Parse("intro\n\n:: Demo.One\n::\n\n:: Demo.Two\n::\n");
        var one = new TestLeaf();
        var two = new TestLeaf();
        var subjects = new Dictionary<int, IInterceptorSubject> { [0] = one, [1] = two };

        // Act
        var change = MarkdownPageOperations.TryMoveText(page, subjects, TextAt(page, 1), 1, out var refusal);

        // Assert
        Assert.Null(refusal);
        Assert.Equal(":: Demo.One\n::\n\nintro\n\n:: Demo.Two\n::\n", change!.Write.Page.Source);
        Assert.Equal(4, change.ResultTextLine);
        Assert.Equal(-1, change.ResultBlockIndex);
        Assert.False(change.Write.HasInsertedOrRemovedBlocks);
        Assert.Null(change.ChangedBlock);
        Assert.Same(one, change.Write.BlockSubjects[0]);
        Assert.Same(two, change.Write.BlockSubjects[1]);
    }

    [Fact]
    public void WhenTextIsMovedUpPastAnotherText_ThenTheTwoSwap()
    {
        // Arrange
        var page = MarkdownPageParser.Parse("a\n\nb\n");

        // Act
        var change = MarkdownPageOperations.TryMoveText(page, NoSubjects, TextAt(page, 3), -1, out var refusal);

        // Assert
        Assert.Null(refusal);
        Assert.Equal("b\n\na\n", change!.Write.Page.Source);
        Assert.Equal(1, change.ResultTextLine);
    }

    [Fact]
    public void WhenTextIsMovedPastAWidgetWithSlotBlocks_ThenItPassesTheWholeBlock()
    {
        // Arrange: a value map is such a widget, and a chip on the page.
        var page = MarkdownPageParser.Parse("intro\n\n:: Demo.Map\n::: Case Value=A\n:::\n::\n");

        // Act
        var change = MarkdownPageOperations.TryMoveText(page, NoSubjects, TextAt(page, 1), 1, out var refusal);

        // Assert
        Assert.Null(refusal);
        Assert.Equal(":: Demo.Map\n::: Case Value=A\n:::\n::\n\nintro\n", change!.Write.Page.Source);
    }

    [Fact]
    public void WhenTextInACardIsMovedUpPastAWidget_ThenItStaysInTheCardAndNeverPassesTheFooter()
    {
        // Arrange
        var page = MarkdownPageParser.Parse(":: Demo.Card\n::: Demo.One\n:::\n\nbody\n::: Footer\nfooter\n:::\n::\n");
        var body = TextAt(page, 5);

        // Act
        var bodyDirections = MarkdownPageOperations.GetTextMoveDirections(page, body);
        var footerDirections = MarkdownPageOperations.GetTextMoveDirections(page, TextAt(page, 7));
        var change = MarkdownPageOperations.TryMoveText(page, NoSubjects, body, -1, out var refusal);

        // Assert
        Assert.Equal((true, false), bodyDirections);
        Assert.Equal((false, false), footerDirections);
        Assert.Null(refusal);
        Assert.Equal(":: Demo.Card\n\nbody\n\n::: Demo.One\n:::\n\n::: Footer\nfooter\n:::\n::\n", change!.Write.Page.Source);
        Assert.Same(page.Blocks[0], change.ChangedBlock);
        Assert.Equal(3, change.ResultTextLine);
    }

    [Theory]
    [InlineData("a\n\n:: Demo.One\n::\n", 1, -1)]
    [InlineData(":: Demo.Card\nbody\n::: Footer\n:::\n::\n", 2, 1)]
    public void WhenThereIsNothingToMovePastInTheDirection_ThenTheMoveIsRefused(string source, int line, int direction)
    {
        // Arrange
        var page = MarkdownPageParser.Parse(source);

        // Act
        var change = MarkdownPageOperations.TryMoveText(page, NoSubjects, TextAt(page, line), direction, out var refusal);

        // Assert
        Assert.Null(change);
        Assert.Contains("has nothing", refusal);
    }

    [Fact]
    public void WhenTextThatCannotBeEditedIsMoved_ThenTheMoveIsRefusedAndOffersNoDirection()
    {
        // Arrange
        var page = MarkdownPageParser.Parse(":: {.ab}\nStyled.\n::\n\nafter\n");
        var textBlock = TextAt(page, 1);

        // Act
        var directions = MarkdownPageOperations.GetTextMoveDirections(page, textBlock);
        var change = MarkdownPageOperations.TryMoveText(page, NoSubjects, textBlock, 1, out var refusal);

        // Assert
        Assert.Equal((false, false), directions);
        Assert.Null(change);
        Assert.Contains("cannot be edited", refusal);
    }

    [Fact]
    public void WhenABlockWithoutClosingLineWouldTakeTheMovedText_ThenTheMoveIsRefusedAndNotOffered()
    {
        // Arrange
        var page = MarkdownPageParser.Parse("text\n\n:: Demo.One\n\n:: Demo.Two\n::\n");
        var textBlock = TextAt(page, 1);

        // Act
        var directions = MarkdownPageOperations.GetTextMoveDirections(page, textBlock);
        var change = MarkdownPageOperations.TryMoveText(page, NoSubjects, textBlock, 1, out var refusal);

        // Assert
        Assert.Equal((false, false), directions);
        Assert.Null(change);
        Assert.Contains("without a closing line", refusal);
    }

    [Fact]
    public void WhenAMovedListWouldJoinTheListBeforeIt_ThenTheMoveIsRefused()
    {
        // Arrange
        var page = MarkdownPageParser.Parse("- a\n\n:: Demo.One\n::\n\n- b\n");

        // Act
        var change = MarkdownPageOperations.TryMoveText(page, NoSubjects, TextAt(page, 6), -1, out var refusal);

        // Assert
        Assert.Null(change);
        Assert.Contains("would change other blocks", refusal);
    }

    [Theory]
    [InlineData("- a\n\n- a2\n\n:: Demo.One\n::\n\n- b\n", 8, -1)]
    [InlineData("- a\n\n  more\n\n:: Demo.One\n::\n\n  x\n", 8, -1)]
    [InlineData("[^1]: note\n\n:: Demo.One\n::\n\n    code\n", 6, -1)]
    public void WhenTheMovedTextWouldJoinTheTextBeforeIt_ThenTheMoveIsRefused(string source, int line, int direction)
    {
        // Arrange: the text before the moved text is a loose list, a loose list item or a footnote, which takes it.
        var page = MarkdownPageParser.Parse(source);

        // Act
        var change = MarkdownPageOperations.TryMoveText(page, NoSubjects, TextAt(page, line), direction, out var refusal);

        // Assert
        Assert.Null(change);
        Assert.Equal($"The text at line {line} would join the text around it.", refusal);
    }

    [Theory]
    [InlineData("<!-- c -->\n\n:: Demo.One\n::\n\ntext\n", 6, -1)]
    [InlineData("text\n\n:: Demo.One\n::\n\n<!-- c -->\n", 1, 1)]
    [InlineData("text\n\n:: Demo.One\n::\n\n<div>x</div>\n\n:: Demo.Two\n::\n", 1, 1)]
    public void WhenTheMovedTextWouldJoinRawHtml_ThenTheMoveIsRefusedAndNotOffered(string source, int line, int direction)
    {
        // Arrange
        var page = MarkdownPageParser.Parse(source);
        var textBlock = TextAt(page, line);

        // Act
        var directions = MarkdownPageOperations.GetTextMoveDirections(page, textBlock);
        var change = MarkdownPageOperations.TryMoveText(page, NoSubjects, textBlock, direction, out var refusal);

        // Assert
        Assert.Equal((false, false), directions);
        Assert.Null(change);
        Assert.Equal($"The text at line {line} would join the HTML next to it.", refusal);
    }

    [Fact]
    public void WhenThePageHasALoneCarriageReturn_ThenNoTextMoveIsOffered()
    {
        // Arrange
        var page = MarkdownPageParser.Parse("x\rb\n\n:: Demo.One\n::\n");

        // Act
        var directions = MarkdownPageOperations.GetTextMoveDirections(page, TextAt(page, 1));

        // Assert
        Assert.Equal((false, false), directions);
    }

    [Fact]
    public void WhenMovedCodeWouldContinueTheListBeforeIt_ThenTheMoveIsRefused()
    {
        // Arrange
        var page = MarkdownPageParser.Parse("- x\n\n:: Demo.One\n::\n\n    code\n");

        // Act
        var change = MarkdownPageOperations.TryMoveText(page, NoSubjects, TextAt(page, 6), -1, out var refusal);

        // Assert
        Assert.Null(change);
        Assert.Contains("would change other blocks", refusal);
    }

    [Fact]
    public void WhenAMovedListWouldJoinTheListAfterIt_ThenTheMoveIsRefused()
    {
        // Arrange
        var page = MarkdownPageParser.Parse("- a\n\n:: Demo.One\n::\n\n- b\n");

        // Act
        var change = MarkdownPageOperations.TryMoveText(page, NoSubjects, TextAt(page, 1), 1, out var refusal);

        // Assert
        Assert.Null(change);
        Assert.Contains("would change other blocks", refusal);
    }

    [Fact]
    public void WhenTextIsMovedOnAPageWithWindowsLineEndings_ThenTheMoveKeepsThem()
    {
        // Arrange
        var page = MarkdownPageParser.Parse("a\r\n\r\nb\r\n");

        // Act
        var change = MarkdownPageOperations.TryMoveText(page, NoSubjects, TextAt(page, 3), -1, out var refusal);

        // Assert
        Assert.Null(refusal);
        Assert.Equal("b\r\n\r\na\r\n", change!.Write.Page.Source);
    }
}
