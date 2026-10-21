using HomeBlaze.Storage.Pages;
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Tests.Pages;

public class MarkdownPageOperationsTests
{
    private static readonly IReadOnlyDictionary<int, IInterceptorSubject> NoSubjects = new Dictionary<int, IInterceptorSubject>();

    private const string Card = "intro\n\n:: Demo.Card\n::: Row Label=A\n:::\n::: Row Label=B\n:::\n::\n\noutro\n";

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
    public void WhenARowIsMovedDown_ThenItSwapsWithTheNextRowAndKeepsItsSubject()
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
    public void WhenAWidgetIsMovedUpPastText_ThenTheTextStaysBetweenThem()
    {
        // Arrange
        const string source = "a\n\n:: Demo.One\n::\n\ntext\n\n:: Demo.Two\n::\n";
        var page = MarkdownPageParser.Parse(source);

        // Act
        var change = MarkdownPageOperations.TryMove(page, NoSubjects, page.Blocks[1], -1, out var refusal);

        // Assert
        Assert.Null(refusal);
        Assert.Equal("a\n\n:: Demo.Two\n::\n\ntext\n\n:: Demo.One\n::\n", change!.Write.Page.Source);
        Assert.Null(change.ChangedBlock);
    }

    [Fact]
    public void WhenTheLastBlockEndsTheTextWithoutALineEnding_ThenTheMoveKeepsTheTextEndingWithout()
    {
        // Arrange
        const string source = "x\n\n:: Demo.One\n::\n\n:: Demo.Two\n::";
        var page = MarkdownPageParser.Parse(source);

        // Act
        var change = MarkdownPageOperations.TryMove(page, NoSubjects, page.Blocks[0], 1, out _);

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

    [Theory]
    [InlineData(0, -1)]
    [InlineData(1, 1)]
    public void WhenThereIsNoSiblingInTheDirection_ThenTheMoveIsRefused(int rowPosition, int direction)
    {
        // Arrange
        var page = MarkdownPageParser.Parse(Card);

        // Act
        var change = MarkdownPageOperations.TryMove(page, NoSubjects, page.Blocks[0].Children[rowPosition], direction, out var refusal);

        // Assert
        Assert.Null(change);
        Assert.Contains("no block of the same slot", refusal);
    }

    [Fact]
    public void WhenTheNeighborIsOfAnotherSlot_ThenTheMoveIsRefused()
    {
        // Arrange
        var page = MarkdownPageParser.Parse(":: Demo.Card\n::: Row\n:::\n::: Item\n:::\n::\n");

        // Act
        var change = MarkdownPageOperations.TryMove(page, NoSubjects, page.Blocks[0].Children[0], 1, out _);

        // Assert
        Assert.Null(change);
        Assert.Null(MarkdownPageOperations.FindMoveTarget(page, page.Blocks[0].Children[0], 1));
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
}
