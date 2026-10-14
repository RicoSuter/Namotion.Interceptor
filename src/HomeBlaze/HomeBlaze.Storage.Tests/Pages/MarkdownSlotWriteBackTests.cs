using System.Text;
using HomeBlaze.Abstractions;
using HomeBlaze.Services;
using HomeBlaze.Storage.Files;
using HomeBlaze.Storage.Internal;
using HomeBlaze.Storage.Pages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using static HomeBlaze.Storage.Tests.Pages.TestTypeNames;

namespace HomeBlaze.Storage.Tests.Pages;

[Collection(MarkdownPageFixture.StorageCollection)]
public class MarkdownSlotWriteBackTests
{
    private const string LeafDefaults = "Count=0 Enabled=false Interval=00:00:00";

    private const string ReloadRefusal = "Blocks cannot be inserted or removed until the page has been read again.";

    private static readonly TimeSpan DeadlockTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task WhenValueWithChildIsAdded_ThenItsBlockIsInsertedAfterTheLastOneWithTheChildInside()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var (page, board) = await AddBoardPageAsync(fixture, $"intro\n\n:: {BoardType}#board Columns=2\n::: Tile Position=1\none\n:::\n::\n\noutro\n");
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
    public async Task WhenValueIsRemoved_ThenItsBlockIsRemovedAndTheOtherValuesAreKept()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var (page, board) = await AddBoardPageAsync(
            fixture,
            $"intro\n\n:: {BoardType}#board Columns=2\n::: Tile Position=1\none\n:::\n" +
            $"::: Tile Position=2\n:::: {LeafType} Count=5\n::::\ntwo\n:::\n::: Tile\nthree\n:::\n::\n\noutro\n");
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
        var (page, board) = await AddBoardPageAsync(
            fixture,
            $":: {BoardType}#board Columns=2\n::: Tile Position=1\none\n:::\n" +
            $"::: Tile#keep Position=2\n:::: {LeafType}#leaf Count=5\n::::\n:::\n::\n");
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
        var (page, board) = await AddBoardPageAsync(fixture, $":: {BoardType}#board Columns=2\n::: Tile Position=1\n:::\n::\n");
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
        var (page, board) = await AddBoardPageAsync(fixture, $":: {BoardType}#board Columns=2\n::\n");
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
        var (fixture, log) = await CreateFixtureWithLogAsync();
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
        await AssertNothingWrittenAsync(
            handled,
            content,
            fixture,
            page,
            log,
            "The new 'Tile' block cannot be written, because its slot 'Child' holds content of a page or more than one new subject.");
    }

    [Fact]
    public async Task WhenChildOfANewValueHasSlotContent_ThenNothingIsWritten()
    {
        // Arrange
        var (fixture, log) = await CreateFixtureWithLogAsync();
        var content = $":: {BoardType}#board Columns=1\n::\n";
        var (page, board) = await AddBoardPageAsync(fixture, content);
        var child = new TestBoard { Tiles = [new TestTile { Position = 1 }] };
        board.Tiles = [new TestTile { Position = 1, Child = child }];

        // Act
        var handled = await page.WriteConfigurationAsync(board, CancellationToken.None);

        // Assert
        await AssertNothingWrittenAsync(
            handled,
            content,
            fixture,
            page,
            log,
            "The new 'Tile' block cannot be written, because its slot 'Child' holds content of a page or more than one new subject.");
    }

    [Fact]
    public async Task WhenNewValueIsNotConfigurable_ThenNothingIsWritten()
    {
        // Arrange: the type of the slot itself is not configurable, so the value has the type of the slot.
        var (fixture, log) = await CreateFixtureWithLogAsync();
        var content = $":: {Namespace}.TestPlainHost#host\n::\n";
        var page = await fixture.AddPageAsync("page.md", content);
        var host = (TestPlainHost)page.Children["host"];
        host.PlainValues = [new TestPlain()];

        // Act
        var handled = await page.WriteConfigurationAsync(host, CancellationToken.None);

        // Assert
        await AssertNothingWrittenAsync(
            handled, content, fixture, page, log, "A new 'Plain' block cannot be written for a subject of the type TestPlain, which is not configurable.");
    }

    [Fact]
    public async Task WhenInsertingABlockWouldChangeOtherBlocks_ThenNothingIsWritten()
    {
        // Arrange: Markdig keeps the empty line at the end of the page in the definition, which makes it loose.
        // After a new row the empty line is no part of it any more.
        var (fixture, log) = await CreateFixtureWithLogAsync();
        var content = $":: {Namespace}.TestPanel#panel Title=T\nterm\n:   definition\n\n";
        var page = await fixture.AddPageAsync("page.md", content);
        var panel = (TestPanel)page.Children["panel"];
        panel.Rows = [new TestRow { Label = "a" }];

        // Act
        var handled = await page.WriteConfigurationAsync(panel, CancellationToken.None);

        // Assert
        await AssertNothingWrittenAsync(handled, content, fixture, page, log, "Inserting a 'Row' block at line 4 would change other blocks of the page.");
    }

    [Fact]
    public async Task WhenBlockHasNoSubjectInTheBlockTable_ThenItIsNotRemoved()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var content = $":: {BoardType}#board Columns=2\n::: Tile Position=1\n:::\n::: Tile Position=2\n:::\n::\n";
        var (_, board) = await AddBoardPageAsync(fixture, content);
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
        var leaf = (TestLeaf)page.Children["leaf"];
        var (entered, release) = PauseReload((TestCallback)page.Children["callback"]);
        var reload = fixture.UpdatePageAsync(page, string.Format(blocks, "b"));
        await entered.WaitAsync(DeadlockTimeout);

        // Act: the save does not wait for the reload.
        leaf.Count = 7;
        var handled = await page.WriteConfigurationAsync(leaf, CancellationToken.None).WaitAsync(DeadlockTimeout);
        var written = await fixture.ReadAsync("page.md");
        release();
        await reload.WaitAsync(DeadlockTimeout);
        await page.OnFileChangedAsync(CancellationToken.None);

        // Assert: the save writes the text of the last parse it knows, so the outside edit that the reload was
        // reading is replaced in the file. Saves behaved like this before blocks were inserted and removed.
        Assert.True(handled);
        Assert.Contains($":: {LeafType}#leaf Count=7", written);
        Assert.Equal(7, leaf.Count);
    }

    [Fact]
    public async Task WhenConfigurationOfASubjectWithSlotsIsSerializedForItsBlock_ThenTheSlotsAreLeftOut()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var (_, board) = await AddBoardPageAsync(fixture, $":: {BoardType}#board Columns=2\n::: Tile Position=1\ntext\n:::\n::\n");
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
    public async Task WhenConfigurationPropertyDiffersFromASlotPropertyOnlyByCase_ThenItIsSerializedForTheBlock()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var serializer = fixture.Services.GetRequiredService<ConfigurableSubjectSerializer>();
        var board = new TestCaseBoard { Tilelist = 3 };

        // Act
        var json = MarkdownBlockConfiguration.Serialize(serializer, board);

        // Assert
        Assert.Contains("\"Tilelist\": 3", json);
        Assert.DoesNotContain("\"TileList\"", json);
    }

    [Theory]
    // The two values are swapped.
    [InlineData(new[] { 1, 0 })]
    // A new value stands before the values with a block, where no new block can go.
    [InlineData(new[] { -1, 0, 1 })]
    // A value stands twice, with a block or without one. The reload gives every block one value, so the second
    // entry would be gone after it.
    [InlineData(new[] { 0, 0 })]
    [InlineData(new[] { 0, -1, -1 })]
    public async Task WhenValuesStandInAnotherOrderThanTheirBlocks_ThenNothingIsWritten(int[] order)
    {
        // Arrange: -1 stands for one new tile.
        var (fixture, log) = await CreateFixtureWithLogAsync();
        var content = $":: {BoardType}#board Columns=2\n::: Tile Position=1\none\n:::\n::: Tile Position=2\ntwo\n:::\n::\n";
        var (page, board) = await AddBoardPageAsync(fixture, content);
        var added = new TestTile { Position = 3 };
        var tiles = board.Tiles;
        board.Tiles = [.. order.Select(position => position < 0 ? added : tiles[position])];

        // Act
        var handled = await page.WriteConfigurationAsync(board, CancellationToken.None);

        // Assert
        await AssertNothingWrittenAsync(
            handled,
            content,
            fixture,
            page,
            log,
            "The order of the 'Tile' values differs from the order of their blocks. The order is changed in the text of the page.");
    }

    [Fact]
    public async Task WhenNewValueHasAnotherTypeThanTheSlot_ThenNothingIsWritten()
    {
        // Arrange
        var (fixture, log) = await CreateFixtureWithLogAsync();
        var content = $":: {BoardType}#board Columns=2\n::\n";
        var (page, board) = await AddBoardPageAsync(fixture, content);
        board.Tiles = [new TestWideTile { Position = 1, Span = 2 }];

        // Act
        var handled = await page.WriteConfigurationAsync(board, CancellationToken.None);

        // Assert
        await AssertNothingWrittenAsync(
            handled,
            content,
            fixture,
            page,
            log,
            "A new 'Tile' block cannot be written for a subject of the type TestWideTile, because the slot holds subjects of the type TestTile.");
    }

    [Fact]
    public async Task WhenTypeOfANewChildCannotBeNamedOnAPage_ThenNothingIsWritten()
    {
        // Arrange: the full name of a nested type has a '+'.
        var (fixture, log) = await CreateFixtureWithLogAsync();
        var content = $":: {BoardType}#board Columns=2\n::\n";
        var (page, board) = await AddBoardPageAsync(fixture, content);
        board.Tiles = [new TestTile { Position = 1, Child = new TestNesting.TestNestedLeaf { Count = 3 } }];

        // Act
        var handled = await page.WriteConfigurationAsync(board, CancellationToken.None);

        // Assert
        await AssertNothingWrittenAsync(
            handled,
            content,
            fixture,
            page,
            log,
            "The new 'Tile' block cannot be written, because the type TestNestedLeaf of the subject in it cannot be named on a page.");
    }

    [Fact]
    public async Task WhenSlotOfTheSavedBlockHoldsASubjectFromTheGraph_ThenNothingIsWritten()
    {
        // Arrange
        var (fixture, log) = await CreateFixtureWithLogAsync();
        var content = $":: {BoardType}#board Columns=2\n::: Tile Position=1\n:::\n::\n";
        var (page, board) = await AddBoardPageAsync(fixture, content);
        var child = new TestLeaf { Count = 3 };
        board.Tiles[0].Child = child;

        // Act
        var handled = await page.WriteConfigurationAsync(child, CancellationToken.None);

        // Assert
        await AssertNothingWrittenAsync(
            handled, content, fixture, page, log, "The slot 'Child' of the block holds a subject that is not content of the page, so it cannot be written.");
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
        await AssertNothingWrittenAsync(handled, content, fixture, page, log, "Removing the 'Row' block at line 4 would change other blocks of the page.");
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task WhenNewChildHasAValueThatDoesNotFitOnTheHeadLine_ThenItsBlockGetsAJsonBlockWithTheLineEndingOfThePage(string lineEnding)
    {
        // Arrange: the fixture writes line feeds only, so the file is written through the storage.
        var fixture = await MarkdownPageFixture.CreateAsync();
        var content = $":: {BoardType}#board Columns=2\n::: Tile Position=1\none\n:::\n::\n".Replace("\n", lineEnding);
        var page = await fixture.AddPageAsync("page.md", content);
        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(content)))
        {
            await fixture.Storage.WriteBlobAsync("page.md", stream, CancellationToken.None);
        }

        await page.OnFileChangedAsync(CancellationToken.None);
        var contentOfThePage = page.Content;
        var board = (TestBoard)page.Children["board"];
        board.Tiles = [.. board.Tiles, new TestTile { Position = 2, Child = new TestLeaf { Count = 3, Tags = ["a", "b"] } }];

        // Act
        var handled = await page.WriteConfigurationAsync(board, CancellationToken.None);
        var written = await fixture.ReadAsync("page.md");
        await page.OnFileChangedAsync(CancellationToken.None);

        // Assert
        var expected =
            $":: {BoardType}#board Columns=2\n::: Tile Position=1\none\n:::\n" +
            $"::: Tile Position=2\n:::: {LeafType} Count=3 Enabled=false Interval=00:00:00\n" +
            "```json\n{\n  \"Tags\": [\n    \"a\",\n    \"b\"\n  ]\n}\n```\n::::\n:::\n::\n";
        Assert.True(handled);
        Assert.Equal(content, contentOfThePage);
        Assert.Equal(expected.Replace("\n", lineEnding), written);
        var leaf = Assert.IsType<MarkdownFragment>(board.Tiles[1].Child).Children.Values.OfType<TestLeaf>().Single();
        Assert.Equal(3, leaf.Count);
        Assert.Equal(["a", "b"], leaf.Tags);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WhenPageHasALoneCarriageReturnAndABlockIsInsertedOrRemoved_ThenNothingIsWritten(bool isInsert)
    {
        // Arrange: Markdig ends a line at the carriage return as well, so its line numbers after it are not the
        // line numbers of the text functions.
        var (fixture, log) = await CreateFixtureWithLogAsync();
        var content = $"intro\ra\n\n:: {BoardType}#board Columns=2\n::: Tile Position=1\none\n:::\n::: Tile Position=2\ntwo\n:::\n::\n";
        var (page, board) = await AddBoardPageAsync(fixture, content);
        board.Tiles = isInsert ? [.. board.Tiles, new TestTile { Position = 3 }] : [board.Tiles[0]];

        // Act
        var handled = await page.WriteConfigurationAsync(board, CancellationToken.None);

        // Assert
        await AssertNothingWrittenAsync(
            handled, content, fixture, page, log, "Blocks cannot be inserted or removed on a page with a line ending that is a carriage return alone.");
    }

    [Fact]
    public async Task WhenPageHasALoneCarriageReturnAndOnlyAHeadLineChanges_ThenItIsWritten()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var (page, board) = await AddBoardPageAsync(fixture, $"intro\ra\n\n:: {BoardType}#board Columns=2\n::: Tile Position=1\none\n:::\n::\n");
        board.Columns = 3;
        board.Tiles[0].Position = 5;

        // Act
        var handledBoard = await page.WriteConfigurationAsync(board, CancellationToken.None);
        var handledTile = await page.WriteConfigurationAsync(board.Tiles[0], CancellationToken.None);

        // Assert
        Assert.True(handledBoard);
        Assert.True(handledTile);
        Assert.Equal(
            $"intro\ra\n\n:: {BoardType}#board Columns=3\n::: Tile Position=5\none\n:::\n::\n",
            await fixture.ReadAsync("page.md"));
    }

    [Fact]
    public async Task WhenOwnerIsSavedWhileAReloadHasDroppedAValue_ThenNoBlockIsRemoved()
    {
        // Arrange: the file loses its first tile from outside. The reload keeps the first instance for the text
        // of the second tile and drops the second instance, and it stops in the block after the board.
        var (fixture, log) = await CreateFixtureWithLogAsync();
        var callbackBlock = $"\n:: {Namespace}.TestCallback#callback Label={{0}}\n::\n";
        var (page, board) = await AddBoardPageAsync(
            fixture,
            $":: {BoardType}#board Columns=2\n::: Tile Position=1\none\n:::\n::: Tile Position=2\ntwo\n:::\n::\n" +
            string.Format(callbackBlock, "a"));
        var (entered, release) = PauseReload((TestCallback)page.Children["callback"]);
        var edited =
            $":: {BoardType}#board Columns=2\n::: Tile Position=2\ntwo\n:::\n::\n" + string.Format(callbackBlock, "b");
        var reload = fixture.UpdatePageAsync(page, edited);
        await entered.WaitAsync(DeadlockTimeout);
        var tileCountDuringReload = board.Tiles.Length;

        // Act
        var handled = await page.WriteConfigurationAsync(board, CancellationToken.None).WaitAsync(DeadlockTimeout);
        var written = await fixture.ReadAsync("page.md");
        release();
        await reload.WaitAsync(DeadlockTimeout);

        // Assert: the graph had lost the value already, and the text that the edit kept is still in the file.
        Assert.Equal(1, tileCountDuringReload);
        Assert.False(handled);
        Assert.Equal(edited, written);
        AssertRefusal(ReloadRefusal, log);
    }

    [Fact]
    public async Task WhenValueIsAddedWhileAReloadRuns_ThenNoBlockIsInserted()
    {
        // Arrange: the reload has applied the board and stops in the block after it.
        var (fixture, log) = await CreateFixtureWithLogAsync();
        var blocks =
            $":: {BoardType}#board Columns=2\n::: Tile Position=1\none\n:::\n::\n\n" +
            $":: {Namespace}.TestCallback#callback Label={{0}}\n::\n";
        var (page, board) = await AddBoardPageAsync(fixture, string.Format(blocks, "a"));
        var (entered, release) = PauseReload((TestCallback)page.Children["callback"]);
        var edited = string.Format(blocks, "b");
        var reload = fixture.UpdatePageAsync(page, edited);
        await entered.WaitAsync(DeadlockTimeout);

        // Act
        board.Tiles = [.. board.Tiles, new TestTile { Position = 9 }];
        var handled = await page.WriteConfigurationAsync(board, CancellationToken.None).WaitAsync(DeadlockTimeout);
        var written = await fixture.ReadAsync("page.md");
        release();
        await reload.WaitAsync(DeadlockTimeout);
        var tilesAfterReload = board.Tiles.Select(tile => tile.Position).ToArray();
        var handledAfterReload = await page.WriteConfigurationAsync(board, CancellationToken.None);

        // Assert: the reload applied the board before the value was added, so the value is still in the graph
        // when the reload has ended, and the same save writes its block then.
        Assert.False(handled);
        Assert.Equal(edited, written);
        AssertRefusal(ReloadRefusal, log);
        Assert.Equal(new[] { 1, 9 }, tilesAfterReload);
        Assert.True(handledAfterReload);
        Assert.Equal(
            $":: {BoardType}#board Columns=2\n::: Tile Position=1\none\n:::\n::: Tile Position=9\n:::\n::\n\n" +
            $":: {Namespace}.TestCallback#callback Label=b\n::\n",
            await fixture.ReadAsync("page.md"));
    }

    [Fact]
    public async Task WhenSubjectIsSavedAfterAReloadFailedWhileItAppliedTheText_ThenNoBlockIsRemoved()
    {
        // Arrange: the file loses its first tile from outside. The reload applies the board, which drops the
        // second instance, and is cancelled in the block after it, so the page keeps the snapshot of the old text.
        var (fixture, log) = await CreateFixtureWithLogAsync();
        var callbackBlock = $"\n:: {Namespace}.TestCallback#callback Label={{0}}\n::\n";
        var content =
            $":: {BoardType}#board Columns=2\n::: Tile Position=1\none\n:::\n::: Tile Position=2\ntwo\n:::\n::\n" +
            string.Format(callbackBlock, "a");
        var (page, board) = await AddBoardPageAsync(fixture, content);
        var edited =
            $":: {BoardType}#board Columns=2\n::: Tile Position=2\ntwo\n:::\n::\n" + string.Format(callbackBlock, "b");
        await FailReloadAsync(fixture, page, edited);
        var tileCountAfterFailedReload = board.Tiles.Length;
        var contentAfterFailedReload = page.Content;

        // Act
        var handled = await page.WriteConfigurationAsync(board, CancellationToken.None);
        var written = await fixture.ReadAsync("page.md");
        await page.OnFileChangedAsync(CancellationToken.None);
        board.Tiles = [];
        var handledAfterReload = await page.WriteConfigurationAsync(board, CancellationToken.None);

        // Assert: the graph was ahead of the snapshot, and the next reload that succeeds ends that.
        Assert.Equal(1, tileCountAfterFailedReload);
        Assert.Equal(content, contentAfterFailedReload);
        Assert.False(handled);
        Assert.Equal(edited, written);
        AssertRefusal(ReloadRefusal, log);
        Assert.True(handledAfterReload);
        Assert.Equal(
            $":: {BoardType}#board Columns=2\n::\n" + string.Format(callbackBlock, "b"),
            await fixture.ReadAsync("page.md"));
    }

    [Fact]
    public async Task WhenSaveChangesNoSetOfBlocksAfterAReloadFailed_ThenTheHeadLineIsWritten()
    {
        // Arrange: the reload applies the board, which keeps its tile, and is cancelled in the block after it.
        // The graph counts as ahead of the snapshot from then on, which only holds back inserts and removals.
        var (fixture, log) = await CreateFixtureWithLogAsync();
        var blocks =
            $":: {BoardType}#board Columns={{0}}\n::: Tile Position=1\none\n:::\n::\n\n" +
            $":: {Namespace}.TestCallback#callback Label={{1}}\n::\n";
        var (page, board) = await AddBoardPageAsync(fixture, string.Format(blocks, 2, "a"));
        await FailReloadAsync(fixture, page, string.Format(blocks, 2, "b"));

        // Act
        board.Columns = 5;
        var handled = await page.WriteConfigurationAsync(board, CancellationToken.None);
        var written = await fixture.ReadAsync("page.md");
        board.Tiles = [];
        var handledRemoval = await page.WriteConfigurationAsync(board, CancellationToken.None);

        // Assert: the head line is written into the text of the snapshot, which is the text before the failed
        // reload. The save writes the text of the last parse it knows, so the outside edit that the reload was
        // reading is replaced in the file. Saves behaved like this before blocks were inserted and removed. A
        // removal is still refused.
        Assert.True(handled);
        Assert.Equal(string.Format(blocks, 5, "a"), written);
        Assert.False(handledRemoval);
        Assert.Equal(written, await fixture.ReadAsync("page.md"));
        AssertRefusal(ReloadRefusal, log);
    }

    private static async Task<(MarkdownPageFixture Fixture, RecordingLogger<MarkdownContentParser> Log)> CreateFixtureWithLogAsync()
    {
        var log = new RecordingLogger<MarkdownContentParser>();
        var fixture = await MarkdownPageFixture.CreateAsync(
            services => services.AddSingleton<ILogger<MarkdownContentParser>>(log));

        return (fixture, log);
    }

    private static async Task<(MarkdownFile Page, TestBoard Board)> AddBoardPageAsync(MarkdownPageFixture fixture, string content)
    {
        var page = await fixture.AddPageAsync("page.md", content);
        return (page, (TestBoard)page.Children["board"]);
    }

    // Lets the next reload stop in the callback block until the returned action releases it.
    private static (Task Entered, Action Release) PauseReload(TestCallback callback)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        callback.OnApply = async () =>
        {
            entered.TrySetResult();
            await release.Task;
        };

        return (entered.Task, release.SetResult);
    }

    // Reloads the page with the edited text and lets the reload fail in the callback block, after it applied
    // the blocks before it.
    private static async Task FailReloadAsync(MarkdownPageFixture fixture, MarkdownFile page, string edited)
    {
        var callback = (TestCallback)page.Children["callback"];
        using var cancellation = new CancellationTokenSource();
        callback.OnApply = () =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.UpdatePageAsync(page, edited, cancellation.Token));
        callback.OnApply = null;
    }

    private static async Task AssertNothingWrittenAsync(
        bool handled,
        string content,
        MarkdownPageFixture fixture,
        MarkdownFile page,
        RecordingLogger<MarkdownContentParser> log,
        string expectedReason)
    {
        Assert.False(handled);
        Assert.Equal(content, await fixture.ReadAsync("page.md"));
        Assert.Equal(content, page.Content);
        AssertRefusal(expectedReason, log);
    }

    private static void AssertRefusal(string expectedReason, RecordingLogger<MarkdownContentParser> log) =>
        Assert.Equal($"A change was not written to the page page.md: {expectedReason}", Assert.Single(log.Entries).Message);
}
