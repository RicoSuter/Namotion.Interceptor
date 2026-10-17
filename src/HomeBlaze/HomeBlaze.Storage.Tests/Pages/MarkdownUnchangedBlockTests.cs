using HomeBlaze.Abstractions;
using HomeBlaze.Storage.Files;
using static HomeBlaze.Storage.Tests.Pages.TestTypeNames;

namespace HomeBlaze.Storage.Tests.Pages;

/// <summary>
/// A reload keeps a block whose text did not change since it was last applied in full, with the subjects of the
/// blocks inside it, and gives them to the blocks of the new parse.
/// </summary>
[Collection(MarkdownPageFixture.StorageCollection)]
public class MarkdownUnchangedBlockTests
{
    private const string CallbackType = $"{Namespace}.TestCallback";

    [Fact]
    public async Task WhenABlockIsInsertedAboveAnUnchangedBlockWithInnerBlocks_ThenItsInnerSubjectsMoveToTheirNewBlockIndexes()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var board =
            $":: {BoardType}#board Columns=2\n::: Tile#first Position=1\n:::\n" +
            $"::: Tile Position=2\n:::: {LeafType}#inner Count=1\n::::\n:::\n::\n";
        var page = await fixture.AddPageAsync("page.md", board);
        var testBoard = Assert.IsType<TestBoard>(page.GetBlockSubject(0));
        var (first, second) = (testBoard.Tiles[0], testBoard.Tiles[1]);
        var inner = Assert.IsType<TestLeaf>(page.GetBlockSubject(3));
        var unsaved = new TestTile { Position = 9 };
        testBoard.Tiles = [first, second, unsaved];

        // Act
        await fixture.UpdatePageAsync(page, $":: {LeafType}#top Count=1\n::\n\n{board}");

        // Assert: the tile without a block shows that the board was kept and not applied again.
        var scope = (IPathScope)page;
        Assert.Equal(1, Assert.IsType<TestLeaf>(page.GetBlockSubject(0)).Count);
        Assert.Same(testBoard, page.GetBlockSubject(1));
        Assert.Equal([first, second, unsaved], testBoard.Tiles);
        Assert.Same(first, page.GetBlockSubject(2));
        Assert.Same(second, page.GetBlockSubject(3));
        Assert.Same(inner, page.GetBlockSubject(4));
        Assert.Same(first, scope.TryGetNamedSubject("first"));
        Assert.Same(inner, scope.TryGetNamedSubject("inner"));
    }

    [Fact]
    public async Task WhenABlockAboveTakesTheNameOfABlockInsideAnUnchangedBlock_ThenTheUnchangedBlockIsAppliedAgainAndShowsTheError()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var board = $":: {BoardType}#board Columns=2\n::: Tile#tile Position=1\n:::\n::\n";
        var page = await fixture.AddPageAsync("page.md", board);
        Assert.IsType<TestTile>(((IPathScope)page).TryGetNamedSubject("tile"));

        // Act
        await fixture.UpdatePageAsync(page, $":: {LeafType}#tile Count=1\n::\n\n{board}");

        // Assert
        var leaf = Assert.IsType<TestLeaf>(page.Children["tile"]);
        Assert.Same(leaf, ((IPathScope)page).TryGetNamedSubject("tile"));
        Assert.Same(leaf, page.GetBlockSubject(0));
        Assert.Null(page.GetBlockSubject(1));
        Assert.Null(page.GetBlockSubject(2));
        Assert.False(page.Children.ContainsKey("board"));
        Assert.Equal(["The name 'tile' is used twice on this page."], PageErrorBoxes.FindMessages(page));
    }

    [Fact]
    public async Task WhenTheFirstOfTwoIdenticalUnnamedBlocksIsDeletedInTheFile_ThenTheRemainingBlockHasOneBoardWithItsOwnTiles()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var board = $":: {BoardType} Columns=2\n::: Tile Position=1\n:::\n::\n";
        var page = await fixture.AddPageAsync("page.md", $"intro\n\n{board}\n{board}");
        var firstBoard = Assert.IsType<TestBoard>(page.GetBlockSubject(0));
        var secondBoard = Assert.IsType<TestBoard>(page.GetBlockSubject(2));

        // Act
        await fixture.UpdatePageAsync(page, $"intro\n\n{board}");

        // Assert: the remaining block takes the key, and with it the subject, of the first block.
        var remaining = Assert.IsType<TestBoard>(Assert.Single(page.Children.Values.OfType<TestBoard>()));
        Assert.Same(firstBoard, remaining);
        Assert.Same(remaining, page.GetBlockSubject(0));
        Assert.Same(remaining.Tiles.Single(), page.GetBlockSubject(1));
        Assert.Null(page.GetBlockSubject(2));
        Assert.DoesNotContain(secondBoard.Tiles.Single(), new[] { page.GetBlockSubject(0), page.GetBlockSubject(1) });
    }

    [Fact]
    public async Task WhenTheWriteOfATopLevelBlockIsRefused_ThenOnlyThatBlockIsReadAgainAndTheUnsavedValueOfAnotherBlockIsKept()
    {
        // Arrange: the swapped tiles stand in another order than their blocks, which refuses the write.
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync(
            "page.md",
            $":: {LeafType}#leaf Count=1\n::\n\n" +
            $":: {BoardType}#board Columns=2\n::: Tile Position=1\n:::\n::: Tile Position=2\n:::\n::\n");
        var leaf = Assert.IsType<TestLeaf>(page.Children["leaf"]);
        var board = Assert.IsType<TestBoard>(page.Children["board"]);
        leaf.Count = 5;
        board.Columns = 4;
        board.Tiles = [board.Tiles[1], board.Tiles[0]];

        // Act
        var isRefusedWriteWritten = await page.WriteConfigurationAsync(board, CancellationToken.None);
        var columnsAfterRefusal = board.Columns;
        board.Columns = 3;
        var isLaterWriteWritten = await page.WriteConfigurationAsync(board, CancellationToken.None);

        // Assert
        Assert.False(isRefusedWriteWritten);
        Assert.Equal(2, columnsAfterRefusal);
        Assert.Equal([1, 2], board.Tiles.Select(tile => tile.Position));
        Assert.Same(leaf, page.Children["leaf"]);
        Assert.Equal(5, leaf.Count);
        Assert.True(isLaterWriteWritten);
        Assert.Equal(
            $":: {LeafType}#leaf Count=1\n::\n\n" +
            $":: {BoardType}#board Columns=3\n::: Tile Position=1\n:::\n::: Tile Position=2\n:::\n::\n",
            await fixture.ReadAsync("page.md"));
    }

    [Fact]
    public async Task WhenAnotherBlockOfThePageChanges_ThenTheConfigurationOfAnUnchangedBlockAndOfTheBlocksInsideItIsNotAppliedAgain()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var content =
            $":: {CallbackType}#callback Label=a\n::\n\n" +
            $":: {BoardType}#board Columns=2\n::: Tile Position=1\n:::: {CallbackType}#inner Label=b\n::::\n:::\n::\n\n" +
            $":: {LeafType}#leaf Count={{0}}\n::\n";
        var page = await fixture.AddPageAsync("page.md", string.Format(content, 1));
        var callback = Assert.IsType<TestCallback>(((IPathScope)page).TryGetNamedSubject("callback"));
        var inner = Assert.IsType<TestCallback>(((IPathScope)page).TryGetNamedSubject("inner"));
        var applyCount = 0;
        callback.OnApply = inner.OnApply = () =>
        {
            Interlocked.Increment(ref applyCount);
            return Task.CompletedTask;
        };

        // Act
        await fixture.UpdatePageAsync(page, string.Format(content, 2));

        // Assert
        Assert.Equal(2, Assert.IsType<TestLeaf>(page.Children["leaf"]).Count);
        Assert.Same(callback, ((IPathScope)page).TryGetNamedSubject("callback"));
        Assert.Same(inner, ((IPathScope)page).TryGetNamedSubject("inner"));
        Assert.Equal(0, applyCount);
    }
}
