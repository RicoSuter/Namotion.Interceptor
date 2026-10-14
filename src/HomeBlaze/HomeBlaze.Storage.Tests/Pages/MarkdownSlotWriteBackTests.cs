using HomeBlaze.Abstractions;
using HomeBlaze.Services;
using HomeBlaze.Storage.Pages;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Tests.Pages;

[Collection(MarkdownPageFixture.StorageCollection)]
public class MarkdownSlotWriteBackTests
{
    private const string Namespace = "HomeBlaze.Storage.Tests.Pages";
    private const string BoardType = Namespace + ".TestBoard";
    private const string LeafType = Namespace + ".TestLeaf";
    private const string LeafDefaults = "Count=0 Enabled=false Interval=00:00:00";

    private static readonly TimeSpan DeadlockTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task WhenValueWithChildIsAdded_ThenItsBlockIsInsertedAfterTheLastOneWithTheChildInside()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var content = $"intro\n\n:: {BoardType}#board Columns=2\n::: Tile Position=1\none\n:::\n::\n\noutro\n";
        var page = await fixture.AddPageAsync("page.md", content);
        var board = (TestBoard)page.Children["board"];
        var first = board.Tiles[0];
        var added = new TestTile { Position = 2, Child = new TestLeaf { Name = "n" } };
        board.Tiles = [.. board.Tiles, added];

        // Act
        var handled = await page.WriteConfigurationAsync(board, CancellationToken.None);
        var written = await fixture.ReadAsync("page.md");
        await page.OnFileChangedAsync(CancellationToken.None);

        // Assert
        Assert.True(handled);
        Assert.Equal(
            $"intro\n\n:: {BoardType}#board Columns=2\n::: Tile Position=1\none\n:::\n" +
            $"::: Tile Position=2\n:::: {LeafType} Name=n {LeafDefaults}\n::::\n:::\n::\n\noutro\n",
            written);
        Assert.Same(board, page.Children["board"]);
        Assert.Equal([first, added], board.Tiles);
        var leaf = Assert.IsType<MarkdownFragment>(added.Child).Children.Values.OfType<TestLeaf>().Single();
        Assert.Equal("n", leaf.Name);
    }

    [Fact]
    public async Task WhenPageHasNoClosingLines_ThenTheNewBlockHasNoneEither()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync("page.md", $":: {BoardType}#board Columns=2\n::: Tile Position=1\none\n::\n");
        var board = (TestBoard)page.Children["board"];
        board.Tiles = [.. board.Tiles, new TestTile { Position = 2, Child = new TestLeaf { Name = "n" } }];

        // Act
        await page.WriteConfigurationAsync(board, CancellationToken.None);
        var written = await fixture.ReadAsync("page.md");
        await page.OnFileChangedAsync(CancellationToken.None);

        // Assert
        Assert.Equal(
            $":: {BoardType}#board Columns=2\n::: Tile Position=1\none\n::: Tile Position=2\n:::: {LeafType} Name=n {LeafDefaults}\n::\n",
            written);
        Assert.Equal([1, 2], board.Tiles.Select(tile => tile.Position));
    }

    [Fact]
    public async Task WhenFirstValueIsAdded_ThenItsBlockGoesToTheEndOfTheOwner()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync("page.md", $":: {BoardType}#board Columns=2\n::\n\noutro\n");
        var board = (TestBoard)page.Children["board"];
        board.Tiles = [new TestTile { Position = 4 }];

        // Act
        await page.WriteConfigurationAsync(board, CancellationToken.None);
        var written = await fixture.ReadAsync("page.md");
        await page.OnFileChangedAsync(CancellationToken.None);

        // Assert
        Assert.Equal($":: {BoardType}#board Columns=2\n::: Tile Position=4\n:::\n::\n\noutro\n", written);
        Assert.Null(Assert.Single(board.Tiles).Child);
    }

    [Fact]
    public async Task WhenValueIsRemoved_ThenItsBlockIsRemovedAndTheOtherValuesAreKept()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var content =
            $"intro\n\n:: {BoardType}#board Columns=2\n::: Tile Position=1\none\n:::\n" +
            $"::: Tile Position=2\n:::: {LeafType} Count=5\n::::\ntwo\n:::\n::: Tile\nthree\n:::\n::\n\noutro\n";
        var page = await fixture.AddPageAsync("page.md", content);
        var board = (TestBoard)page.Children["board"];
        var (first, third) = (board.Tiles[0], board.Tiles[2]);
        board.Tiles = [first, third];

        // Act
        var handled = await page.WriteConfigurationAsync(board, CancellationToken.None);
        var written = await fixture.ReadAsync("page.md");
        await page.OnFileChangedAsync(CancellationToken.None);

        // Assert: the third tile has no arguments, and saving the board does not give it any.
        Assert.True(handled);
        Assert.Equal(
            $"intro\n\n:: {BoardType}#board Columns=2\n::: Tile Position=1\none\n:::\n::: Tile\nthree\n:::\n::\n\noutro\n",
            written);
        Assert.Equal([first, third], board.Tiles);
    }

    [Fact]
    public async Task WhenValuesAreAddedAndRemovedAroundANamedValue_ThenTheSubjectInsideItIsKept()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var content =
            $":: {BoardType}#board Columns=2\n::: Tile Position=1\none\n:::\n" +
            $"::: Tile#keep Position=2\n:::: {LeafType}#leaf Count=5\n::::\n:::\n::\n";
        var page = await fixture.AddPageAsync("page.md", content);
        var board = (TestBoard)page.Children["board"];
        var kept = board.Tiles[1];
        var leaf = Assert.IsType<MarkdownFragment>(kept.Child).Children.Values.OfType<TestLeaf>().Single();
        board.Tiles = [kept, new TestTile { Position = 3 }];

        // Act
        await page.WriteConfigurationAsync(board, CancellationToken.None);
        var written = await fixture.ReadAsync("page.md");
        await page.OnFileChangedAsync(CancellationToken.None);

        // Assert
        Assert.Equal(
            $":: {BoardType}#board Columns=2\n::: Tile#keep Position=2\n:::: {LeafType}#leaf Count=5\n::::\n:::\n::: Tile Position=3\n:::\n::\n",
            written);
        Assert.Same(kept, board.Tiles[0]);
        Assert.Same(leaf, Assert.IsType<MarkdownFragment>(kept.Child).Children.Values.OfType<TestLeaf>().Single());
    }

    [Fact]
    public async Task WhenAddedValueIsSavedAgainBeforeTheReload_ThenItsNewBlockIsWritten()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync("page.md", $":: {BoardType}#board Columns=2\n::: Tile Position=1\n:::\n::\n");
        var board = (TestBoard)page.Children["board"];
        var added = new TestTile { Position = 2 };
        board.Tiles = [.. board.Tiles, added];
        await page.WriteConfigurationAsync(board, CancellationToken.None);
        added.Position = 9;

        // Act
        var handled = await page.WriteConfigurationAsync(added, CancellationToken.None);

        // Assert
        Assert.True(handled);
        Assert.Equal(
            $":: {BoardType}#board Columns=2\n::: Tile Position=1\n:::\n::: Tile Position=9\n:::\n::\n",
            await fixture.ReadAsync("page.md"));
    }

    [Fact]
    public async Task WhenChildOfANewValueIsSaved_ThenTheBlockOfTheValueIsInserted()
    {
        // Arrange: the add dialog of a layout saves the new child, which is in no block yet.
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync("page.md", $":: {BoardType}#board Columns=2\n::\n");
        var board = (TestBoard)page.Children["board"];
        var child = new TestLeaf { Count = 3 };
        board.Tiles = [new TestTile { Position = 1, Child = child }];

        // Act
        var handled = await page.WriteConfigurationAsync(child, CancellationToken.None);

        // Assert
        Assert.True(handled);
        Assert.Equal(
            $":: {BoardType}#board Columns=2\n::: Tile Position=1\n:::: {LeafType} Count=3 Enabled=false Interval=00:00:00\n::::\n:::\n::\n",
            await fixture.ReadAsync("page.md"));
    }

    [Fact]
    public async Task WhenSlotIsOnlyASlot_ThenItsValuesAreReconciledAsWell()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync("page.md", $":: {Namespace}.TestPanel#panel Title=T\n::: Row Label=a\n:::\n::\n");
        var panel = (TestPanel)page.Children["panel"];
        panel.Rows = [.. panel.Rows, new TestRow { Label = "b" }];

        // Act
        await page.WriteConfigurationAsync(panel, CancellationToken.None);

        // Assert
        Assert.Equal(
            $":: {Namespace}.TestPanel#panel Title=T\n::: Row Label=a\n:::\n::: Row Label=b\n:::\n::\n",
            await fixture.ReadAsync("page.md"));
    }

    [Fact]
    public async Task WhenValueWithoutBlockHoldsContentOfThePage_ThenNothingIsWritten()
    {
        // Arrange: a tile of the first board is put into the second one. Its content exists only as text of the first.
        var fixture = await MarkdownPageFixture.CreateAsync();
        var content =
            $":: {BoardType}#first Columns=1\n::: Tile Position=1\nhand-written **text**\n:::\n::\n\n" +
            $":: {BoardType}#second Columns=1\n::\n";
        var page = await fixture.AddPageAsync("page.md", content);
        var first = (TestBoard)page.Children["first"];
        var second = (TestBoard)page.Children["second"];
        second.Tiles = [first.Tiles[0]];

        // Act
        var handled = await page.WriteConfigurationAsync(second, CancellationToken.None);

        // Assert
        Assert.False(handled);
        Assert.Equal(content, await fixture.ReadAsync("page.md"));
        Assert.Equal(content, page.Content);
    }

    [Fact]
    public async Task WhenChildOfANewValueHasSlotContent_ThenNothingIsWritten()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var content = $":: {BoardType}#board Columns=1\n::\n";
        var page = await fixture.AddPageAsync("page.md", content);
        var board = (TestBoard)page.Children["board"];
        var child = new TestBoard { Tiles = [new TestTile { Position = 1 }] };
        board.Tiles = [new TestTile { Position = 1, Child = child }];

        // Act
        var handled = await page.WriteConfigurationAsync(board, CancellationToken.None);

        // Assert
        Assert.False(handled);
        Assert.Equal(content, await fixture.ReadAsync("page.md"));
    }

    [Fact]
    public async Task WhenBlockHasNoSubjectInTheBlockTable_ThenItIsNotRemoved()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var content = $":: {BoardType}#board Columns=2\n::: Tile Position=1\n:::\n::: Tile Position=2\n:::\n::\n";
        var page = await fixture.AddPageAsync("page.md", content);
        var board = (TestBoard)page.Children["board"];
        var blockSubjects = new Dictionary<int, IInterceptorSubject> { [0] = board, [1] = board.Tiles[0] };
        board.Tiles = [board.Tiles[0]];

        // Act
        var write = MarkdownBlockReconciler.TryWrite(
            MarkdownPageParser.Parse(content),
            blockSubjects,
            0,
            fixture.Services.GetRequiredService<ConfigurableSubjectSerializer>(),
            out var refusal);

        // Assert
        Assert.Null(refusal);
        Assert.Equal(content, write!.Page.Source);
    }

    [Fact]
    public async Task WhenWriteBackStartsDuringAReload_ThenItWritesTheTextOfThatReload()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var blocks = $":: {Namespace}.TestCallback#callback Label={{0}}\n::\n\n:: {LeafType}#leaf Count=1\n::\n";
        var page = await fixture.AddPageAsync("page.md", string.Format(blocks, "a"));
        var callback = (TestCallback)page.Children["callback"];
        var leaf = (TestLeaf)page.Children["leaf"];
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        callback.OnApply = async () =>
        {
            entered.TrySetResult();
            await release.Task;
        };

        var reload = fixture.UpdatePageAsync(page, "added by the reload\n\n" + string.Format(blocks, "b"));
        await entered.Task.WaitAsync(DeadlockTimeout);

        // Act
        var write = page.WriteConfigurationAsync(leaf, CancellationToken.None);
        release.SetResult();
        await reload.WaitAsync(DeadlockTimeout);
        var handled = await write.WaitAsync(DeadlockTimeout);

        // Assert: a write-back that did not wait would have written the text from before the reload.
        Assert.True(handled);
        Assert.StartsWith("added by the reload\n", await fixture.ReadAsync("page.md"));
    }

    [Fact]
    public async Task WhenWriteBackIsCalledFromInsideAReloadOfThePage_ThenItIsRefused()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync("page.md", $":: {Namespace}.TestCallback#callback Label=a\n::\n");
        var callback = (TestCallback)page.Children["callback"];
        Exception? refusal = null;
        callback.OnApply = async () =>
        {
            try
            {
                await page.WriteConfigurationAsync(callback, CancellationToken.None);
            }
            catch (Exception exception)
            {
                refusal = exception;
            }
        };

        // Act
        await fixture.UpdatePageAsync(page, $":: {Namespace}.TestCallback#callback Label=b\n::\n").WaitAsync(DeadlockTimeout);

        // Assert
        Assert.Equal(
            "The page cannot be written from inside its own reload.",
            Assert.IsType<InvalidOperationException>(refusal).Message);
        Assert.Same(callback, page.Children["callback"]);
    }

    [Fact]
    public async Task WhenWriteBackComesAfterTheReloadFromWorkThatTheReloadStarted_ThenItIsWritten()
    {
        // Arrange: a subject starts work while its configuration is applied, and that work saves it later.
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync("page.md", $":: {Namespace}.TestCallback#callback Label=a\n::\n");
        var callback = (TestCallback)page.Children["callback"];
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool>? write = null;
        callback.OnApply = () =>
        {
            write = Task.Run(async () =>
            {
                await release.Task;
                return await page.WriteConfigurationAsync(callback, CancellationToken.None);
            });

            return Task.CompletedTask;
        };

        await fixture.UpdatePageAsync(page, $":: {Namespace}.TestCallback#callback Label=b\n::\n").WaitAsync(DeadlockTimeout);
        callback.Label = "c";

        // Act
        release.SetResult();
        var handled = await write!.WaitAsync(DeadlockTimeout);

        // Assert
        Assert.True(handled);
        Assert.Equal($":: {Namespace}.TestCallback#callback Label=c\n::\n", await fixture.ReadAsync("page.md"));
    }
}
