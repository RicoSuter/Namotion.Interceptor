using System.Text;
using HomeBlaze.Abstractions;
using HomeBlaze.Services;
using HomeBlaze.Storage.Internal;
using HomeBlaze.Storage.Pages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
    public async Task WhenSubjectIsChangedAndSavedWhileAReloadRuns_ThenTheChangeIsWritten()
    {
        // Arrange: the reload stops in the first block, before it reaches the leaf.
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

        var reload = fixture.UpdatePageAsync(page, string.Format(blocks, "b"));
        await entered.Task.WaitAsync(DeadlockTimeout);

        // Act: the save does not wait for the reload.
        leaf.Count = 7;
        var handled = await page.WriteConfigurationAsync(leaf, CancellationToken.None).WaitAsync(DeadlockTimeout);
        var written = await fixture.ReadAsync("page.md");
        release.SetResult();
        await reload.WaitAsync(DeadlockTimeout);
        await page.OnFileChangedAsync(CancellationToken.None);

        // Assert
        Assert.True(handled);
        Assert.Contains($":: {LeafType}#leaf Count=7", written);
        Assert.Equal(7, leaf.Count);
    }

    [Fact]
    public async Task WhenConfigurationOfASubjectWithSlotsIsSerializedForItsBlock_ThenTheSlotsAreLeftOut()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync("page.md", $":: {BoardType}#board Columns=2\n::: Tile Position=1\ntext\n:::\n::\n");
        var board = (TestBoard)page.Children["board"];
        var serializer = fixture.Services.GetRequiredService<ConfigurableSubjectSerializer>();

        // Act
        var boardJson = MarkdownBlockConfiguration.Serialize(serializer, board);
        var tileJson = MarkdownBlockConfiguration.Serialize(serializer, board.Tiles[0]);

        // Assert
        Assert.Contains("\"Columns\": 2", boardJson);
        Assert.DoesNotContain("Tiles", boardJson);
        Assert.Contains("\"Position\": 1", tileJson);
        Assert.DoesNotContain("Child", tileJson);
    }

    [Fact]
    public async Task WhenValuesAreReordered_ThenNothingIsWritten()
    {
        // Arrange
        var (fixture, log) = await CreateFixtureWithLogAsync();
        var content = $":: {BoardType}#board Columns=2\n::: Tile Position=1\none\n:::\n::: Tile Position=2\ntwo\n:::\n::\n";
        var page = await fixture.AddPageAsync("page.md", content);
        var board = (TestBoard)page.Children["board"];
        board.Tiles = [board.Tiles[1], board.Tiles[0]];

        // Act
        var handled = await page.WriteConfigurationAsync(board, CancellationToken.None);

        // Assert
        Assert.False(handled);
        Assert.Equal(content, await fixture.ReadAsync("page.md"));
        Assert.Equal(content, page.Content);
        AssertRefusal(
            "The order of the 'Tile' values differs from the order of their blocks. The order is changed in the text of the page.",
            log);
    }

    [Fact]
    public async Task WhenNewValueStandsBeforeAValueWithABlock_ThenNothingIsWritten()
    {
        // Arrange
        var (fixture, log) = await CreateFixtureWithLogAsync();
        var content = $":: {BoardType}#board Columns=2\n::: Tile Position=1\none\n:::\n::: Tile Position=2\ntwo\n:::\n::\n";
        var page = await fixture.AddPageAsync("page.md", content);
        var board = (TestBoard)page.Children["board"];
        board.Tiles = [new TestTile { Position = 0 }, .. board.Tiles];

        // Act
        var handled = await page.WriteConfigurationAsync(board, CancellationToken.None);

        // Assert
        Assert.False(handled);
        Assert.Equal(content, await fixture.ReadAsync("page.md"));
        Assert.Equal(content, page.Content);
        AssertRefusal(
            "The order of the 'Tile' values differs from the order of their blocks. The order is changed in the text of the page.",
            log);
    }

    [Fact]
    public async Task WhenNewValueHasAnotherTypeThanTheSlot_ThenNothingIsWritten()
    {
        // Arrange
        var (fixture, log) = await CreateFixtureWithLogAsync();
        var content = $":: {BoardType}#board Columns=2\n::\n";
        var page = await fixture.AddPageAsync("page.md", content);
        var board = (TestBoard)page.Children["board"];
        board.Tiles = [new TestWideTile { Position = 1, Span = 2 }];

        // Act
        var handled = await page.WriteConfigurationAsync(board, CancellationToken.None);

        // Assert
        Assert.False(handled);
        Assert.Equal(content, await fixture.ReadAsync("page.md"));
        Assert.Equal(content, page.Content);
        AssertRefusal(
            "A new 'Tile' block cannot be written for a subject of the type TestWideTile, because the slot holds subjects of the type TestTile.",
            log);
    }

    [Fact]
    public async Task WhenTypeOfANewChildCannotBeNamedOnAPage_ThenNothingIsWritten()
    {
        // Arrange: the full name of a nested type has a '+'.
        var (fixture, log) = await CreateFixtureWithLogAsync();
        var content = $":: {BoardType}#board Columns=2\n::\n";
        var page = await fixture.AddPageAsync("page.md", content);
        var board = (TestBoard)page.Children["board"];
        board.Tiles = [new TestTile { Position = 1, Child = new TestNesting.TestNestedLeaf { Count = 3 } }];

        // Act
        var handled = await page.WriteConfigurationAsync(board, CancellationToken.None);

        // Assert
        Assert.False(handled);
        Assert.Equal(content, await fixture.ReadAsync("page.md"));
        Assert.Equal(content, page.Content);
        AssertRefusal(
            "The new 'Tile' block cannot be written, because the type TestNestedLeaf of the subject in it cannot be named on a page.",
            log);
    }

    [Fact]
    public async Task WhenSlotOfTheSavedBlockHoldsASubjectFromTheGraph_ThenNothingIsWritten()
    {
        // Arrange
        var (fixture, log) = await CreateFixtureWithLogAsync();
        var content = $":: {BoardType}#board Columns=2\n::: Tile Position=1\n:::\n::\n";
        var page = await fixture.AddPageAsync("page.md", content);
        var board = (TestBoard)page.Children["board"];
        var child = new TestLeaf { Count = 3 };
        board.Tiles[0].Child = child;

        // Act
        var handled = await page.WriteConfigurationAsync(child, CancellationToken.None);

        // Assert
        Assert.False(handled);
        Assert.Equal(content, await fixture.ReadAsync("page.md"));
        Assert.Equal(content, page.Content);
        AssertRefusal(
            "The slot 'Child' of the block holds a subject that is not content of the page, so it cannot be written.",
            log);
    }

    [Fact]
    public async Task WhenRemovingABlockWouldLetAnotherBlockTakeTextAfterIt_ThenNothingIsWritten()
    {
        // Arrange: the first row has no closing line, so without the second one it goes on to the end of the panel.
        var (fixture, log) = await CreateFixtureWithLogAsync();
        var content = $":: {Namespace}.TestPanel#panel Title=T\n::: Row Label=a\none\n::: Row Label=b\ntwo\n:::\ntext of the panel\n::\n";
        var page = await fixture.AddPageAsync("page.md", content);
        var panel = (TestPanel)page.Children["panel"];
        panel.Rows = [panel.Rows[0]];

        // Act
        var handled = await page.WriteConfigurationAsync(panel, CancellationToken.None);

        // Assert
        Assert.False(handled);
        Assert.Equal(content, await fixture.ReadAsync("page.md"));
        Assert.Equal(content, page.Content);
        AssertRefusal("Removing the 'Row' block at line 4 would change other blocks of the page.", log);
    }

    [Fact]
    public async Task WhenNewChildHasAValueThatDoesNotFitOnTheHeadLine_ThenItsBlockGetsAJsonBlock()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync("page.md", $":: {BoardType}#board Columns=2\n::\n");
        var board = (TestBoard)page.Children["board"];
        board.Tiles = [new TestTile { Position = 1, Child = new TestLeaf { Count = 3, Tags = ["a", "b"] } }];

        // Act
        var handled = await page.WriteConfigurationAsync(board, CancellationToken.None);
        var written = await fixture.ReadAsync("page.md");
        await page.OnFileChangedAsync(CancellationToken.None);

        // Assert
        Assert.True(handled);
        Assert.Equal(
            $":: {BoardType}#board Columns=2\n::: Tile Position=1\n:::: {LeafType} Count=3 Enabled=false Interval=00:00:00\n" +
            "```json\n{\n  \"Tags\": [\n    \"a\",\n    \"b\"\n  ]\n}\n```\n::::\n:::\n::\n",
            written);
        var tile = Assert.Single(board.Tiles);
        var leaf = Assert.IsType<MarkdownFragment>(tile.Child).Children.Values.OfType<TestLeaf>().Single();
        Assert.Equal(3, leaf.Count);
        Assert.Equal(["a", "b"], leaf.Tags);
    }

    [Fact]
    public async Task WhenPageHasWindowsLineEndings_ThenTheNewBlockHasThemToo()
    {
        // Arrange: the fixture writes line feeds only, so the file is written through the storage.
        var fixture = await MarkdownPageFixture.CreateAsync();
        var content = $":: {BoardType}#board Columns=2\r\n::: Tile Position=1\r\none\r\n:::\r\n::\r\n";
        var page = await fixture.AddPageAsync("page.md", content);
        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(content)))
        {
            await fixture.Storage.WriteBlobAsync("page.md", stream, CancellationToken.None);
        }

        await page.OnFileChangedAsync(CancellationToken.None);
        var contentOfThePage = page.Content;
        var board = (TestBoard)page.Children["board"];
        board.Tiles = [.. board.Tiles, new TestTile { Position = 2, Child = new TestLeaf { Count = 3, Tags = ["a"] } }];

        // Act
        var handled = await page.WriteConfigurationAsync(board, CancellationToken.None);
        var written = await fixture.ReadAsync("page.md");

        // Assert
        var expected =
            $":: {BoardType}#board Columns=2\n::: Tile Position=1\none\n:::\n" +
            $"::: Tile Position=2\n:::: {LeafType} Count=3 Enabled=false Interval=00:00:00\n" +
            "```json\n{\n  \"Tags\": [\n    \"a\"\n  ]\n}\n```\n::::\n:::\n::\n";
        Assert.True(handled);
        Assert.Equal(content, contentOfThePage);
        Assert.Equal(expected.Replace("\n", "\r\n"), written);
    }

    private static async Task<(MarkdownPageFixture Fixture, RecordingLogger<MarkdownContentParser> Log)> CreateFixtureWithLogAsync()
    {
        var log = new RecordingLogger<MarkdownContentParser>();
        var fixture = await MarkdownPageFixture.CreateAsync(
            services => services.AddSingleton<ILogger<MarkdownContentParser>>(log));

        return (fixture, log);
    }

    private static void AssertRefusal(string expectedReason, RecordingLogger<MarkdownContentParser> log) =>
        Assert.Equal($"A change was not written to the page page.md: {expectedReason}", Assert.Single(log.Entries).Message);
}
