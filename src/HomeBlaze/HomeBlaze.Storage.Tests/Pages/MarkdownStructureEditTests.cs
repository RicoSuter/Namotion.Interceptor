using HomeBlaze.Storage.Pages;
using static HomeBlaze.Storage.Tests.Pages.TestTypeNames;

namespace HomeBlaze.Storage.Tests.Pages;

[Collection(MarkdownPageFixture.StorageCollection)]
public class MarkdownStructureEditTests
{
    private const string PanelType = Namespace + ".TestPanel";

    private static readonly string PanelPage =
        $"intro\n\n:: {PanelType}#panel Title=P\n::: Row Label=A\n:::\n::: Row Label=B\n:::\n::\n\n:: {LeafType}#leaf Name=l\n::\n";

    private static readonly string UnnamedLeavesPage = $"intro\n\n:: {LeafType} Name=a\n::\n\n:: {LeafType} Name=b\n::\n";

    [Fact]
    public async Task WhenABlockIsDeleted_ThenItsLinesAreRemovedFromTheFile()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync("page.md", PanelPage);
        var leaf = page.Children["leaf"];

        // Act
        var result = await page.DeleteBlockAsync(leaf, CancellationToken.None);
        var written = await fixture.ReadAsync("page.md");
        await page.OnFileChangedAsync(CancellationToken.None);

        // Assert
        Assert.True(result.IsWritten);
        Assert.Equal($"intro\n\n:: {PanelType}#panel Title=P\n::: Row Label=A\n:::\n::: Row Label=B\n:::\n::\n\n", written);
        Assert.False(page.Children.ContainsKey("leaf"));
    }

    [Fact]
    public async Task WhenARowIsMovedDown_ThenTheFileAndTheSlotHaveTheNewOrder()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync("page.md", PanelPage);
        var panel = (TestPanel)page.Children["panel"];

        // Act
        var result = await page.MoveBlockAsync(panel.Rows[0], 1, isSkipped: null, CancellationToken.None);
        var written = await fixture.ReadAsync("page.md");
        await page.OnFileChangedAsync(CancellationToken.None);

        // Assert
        Assert.True(result.IsWritten);
        Assert.Contains("::: Row Label=B\n:::\n::: Row Label=A\n:::", written);
        Assert.Equal(["B", "A"], panel.Rows.Select(row => row.Label));
    }

    [Fact]
    public async Task WhenAWidgetIsInsertedBeforeABlock_ThenTheFileHasItThereWithItsConfiguration()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync("page.md", PanelPage);
        var widget = new TestLeaf { Name = "fresh" };

        // Act
        var result = await page.InsertWidgetAsync(page, page.Children["leaf"], widget, "added", CancellationToken.None);
        var written = await fixture.ReadAsync("page.md");
        await page.OnFileChangedAsync(CancellationToken.None);

        // Assert
        Assert.True(result.IsWritten);
        Assert.Contains($"::\n\n:: {LeafType}#added Name=fresh\n::\n\n:: {LeafType}#leaf Name=l\n::", written);
        Assert.Equal("fresh", ((TestLeaf)page.Children["added"]).Name);
    }

    [Fact]
    public async Task WhenAnItemIsAddedToAListSlot_ThenItsBlockFollowsTheLastOne()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync("page.md", PanelPage);
        var panel = (TestPanel)page.Children["panel"];

        // Act
        var result = await page.AddSlotItemAsync(panel, "Row", new TestRow { Label = "C" }, CancellationToken.None);
        var written = await fixture.ReadAsync("page.md");
        await page.OnFileChangedAsync(CancellationToken.None);

        // Assert
        Assert.True(result.IsWritten);
        Assert.Contains("::: Row Label=B\n:::\n::: Row Label=C\n:::\n::", written);
        Assert.Equal(["A", "B", "C"], panel.Rows.Select(row => row.Label));
    }

    [Fact]
    public async Task WhenAnItemIsAddedToAnOpenListSlot_ThenAnEmptyBlockIsWritten()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync("page.md", PanelPage);
        var panel = (TestPanel)page.Children["panel"];

        // Act
        var result = await page.AddSlotItemAsync(panel, "Item", null, CancellationToken.None);
        var written = await fixture.ReadAsync("page.md");

        // Assert
        Assert.True(result.IsWritten);
        Assert.Contains("::: Item\n:::\n::", written);
    }

    [Fact]
    public async Task WhenTheSubjectHasNoBlock_ThenTheEditIsRefusedAndTheFileIsUnchanged()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync("page.md", PanelPage);

        // Act
        var result = await page.DeleteBlockAsync(new TestLeaf(), CancellationToken.None);

        // Assert
        Assert.False(result.IsWritten);
        Assert.Equal("The subject has no block on the page.", result.Refusal);
        Assert.Equal(PanelPage, await fixture.ReadAsync("page.md"));
    }

    [Fact]
    public async Task WhenTheActionsOfTheBlocksAreAskedFor_ThenRowsMoveAmongRowsAndThePanelPastTheTextAndTheLeaf()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync("page.md", PanelPage);
        var panel = (TestPanel)page.Children["panel"];

        // Act
        var first = page.GetBlockActions(panel.Rows[0]);
        var last = page.GetBlockActions(panel.Rows[1]);
        var panelActions = page.GetBlockActions(panel);

        // Assert
        Assert.Equal(new MarkdownBlockActions(true, false, true, 0), first);
        Assert.Equal(new MarkdownBlockActions(true, true, false, 0), last);
        Assert.Equal(new MarkdownBlockActions(true, true, true, 2), panelActions);
        Assert.Equal(default, page.GetBlockActions(new TestLeaf()));
    }

    [Fact]
    public async Task WhenTheNextWidgetIsSkipped_ThenTheLeafCannotMoveDown()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync("page.md", PanelPage + $"\n:: {LeafType}#hidden\n::\n");
        var hidden = page.Children["hidden"];

        // Act
        var actions = page.GetBlockActions(page.Children["leaf"], subject => ReferenceEquals(subject, hidden));

        // Assert
        Assert.False(actions.CanMoveDown);
    }

    [Fact]
    public async Task WhenTheListSlotsAndTheNameOfABlockAreAskedFor_ThenTheyComeFromItsTypeAndItsHeadLine()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync("page.md", PanelPage);
        var panel = (TestPanel)page.Children["panel"];

        // Act
        var slots = page.GetListSlots(panel);

        // Assert
        Assert.Equal([new MarkdownListSlot("Row", typeof(TestRow)), new MarkdownListSlot("Item", null)], slots);
        Assert.Equal("panel", page.GetBlockName(panel));
        Assert.Null(page.GetBlockName(panel.Rows[0]));
        Assert.True(page.IsContentOfPage(page));
        Assert.True(page.IsContentOfPage(panel.Rows[0].Content!));
        Assert.False(page.IsContentOfPage(new MarkdownFragment()));
    }

    [Fact]
    public async Task WhenAnUnnamedRowIsMovedDown_ThenEachRowKeepsItsSubject()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync("page.md", PanelPage);
        var panel = (TestPanel)page.Children["panel"];
        var rowA = panel.Rows[0];
        var rowB = panel.Rows[1];

        // Act
        await page.MoveBlockAsync(rowA, 1, isSkipped: null, CancellationToken.None);
        await page.OnFileChangedAsync(CancellationToken.None);

        // Assert
        Assert.Same(rowB, panel.Rows[0]);
        Assert.Same(rowA, panel.Rows[1]);
        Assert.Equal(["B", "A"], panel.Rows.Select(row => row.Label));
    }

    [Fact]
    public async Task WhenAWidgetIsInsertedBeforeUnnamedWidgets_ThenTheyKeepTheirSubjects()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync("page.md", UnnamedLeavesPage);
        var leafA = page.Children["TestLeaf_0"];
        var leafB = page.Children["TestLeaf_1"];

        // Act
        await page.InsertWidgetAsync(page, leafA, new TestLeaf { Name = "new" }, null, CancellationToken.None);
        await page.OnFileChangedAsync(CancellationToken.None);

        // Assert
        var leaves = page.Children.Values.OfType<TestLeaf>().ToList();
        Assert.Equal(["new", "a", "b"], leaves.Select(leaf => leaf.Name));
        Assert.Same(leafA, leaves[1]);
        Assert.Same(leafB, leaves[2]);
    }

    [Fact]
    public async Task WhenAnUnnamedWidgetIsDeleted_ThenTheWidgetAfterItKeepsItsSubject()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync("page.md", UnnamedLeavesPage);
        var leafB = page.Children["TestLeaf_1"];

        // Act
        await page.DeleteBlockAsync(page.Children["TestLeaf_0"], CancellationToken.None);
        await page.OnFileChangedAsync(CancellationToken.None);

        // Assert
        Assert.Same(leafB, Assert.Single(page.Children.Values.OfType<TestLeaf>()));
    }

    [Fact]
    public async Task WhenTheFileChangedOutsideThePage_ThenTheReloadFindsTheSubjectsByTheirKeys()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync("page.md", UnnamedLeavesPage);
        var first = page.Children["TestLeaf_0"];

        // Act
        await fixture.UpdatePageAsync(page, $"intro\n\n:: {LeafType} Name=b\n::\n\n:: {LeafType} Name=a\n::\n");

        // Assert: by key, the subject at the first position takes the configuration of the block now there.
        Assert.Same(first, page.Children["TestLeaf_0"]);
        Assert.Equal("b", ((TestLeaf)first).Name);
    }

    [Fact]
    public async Task WhenTheFileChangedSinceThePageWasRead_ThenTheStructureEditIsRefusedAndThePageReadsTheFile()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync("page.md", PanelPage);
        var changed = PanelPage + "\nexternal\n";
        await fixture.WriteAsync("page.md", changed);

        // Act
        var result = await page.DeleteBlockAsync(page.Children["leaf"], CancellationToken.None);

        // Assert
        Assert.False(result.IsWritten);
        Assert.Equal("The page changed while it was edited. Try again.", result.Refusal);
        Assert.Equal(changed, await fixture.ReadAsync("page.md"));
        Assert.Equal(changed, page.Content);

        // A second try works on the file that the page read after the refusal.
        var retry = await page.DeleteBlockAsync(page.Children["leaf"], CancellationToken.None);
        Assert.True(retry.IsWritten);
        var written = await fixture.ReadAsync("page.md");
        Assert.DoesNotContain("#leaf", written);
        Assert.EndsWith("\nexternal\n", written);
    }

    [Fact]
    public async Task WhenTheFileOfThePageWasDeleted_ThenTheStructureEditThrowsAnIOExceptionAndWritesNothing()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync("page.md", PanelPage);
        var leaf = page.Children["leaf"];
        await fixture.DeleteAsync("page.md");

        // Act
        var exception = await Record.ExceptionAsync(() => page.DeleteBlockAsync(leaf, CancellationToken.None));

        // Assert
        Assert.IsType<FileNotFoundException>(exception);
        Assert.False(await fixture.ExistsAsync("page.md"));
    }

    [Fact]
    public async Task WhenARowIsMoved_ThenTheResultNamesTheRow()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync("page.md", PanelPage);
        var row = ((TestPanel)page.Children["panel"]).Rows[0];

        // Act
        var result = await page.MoveBlockAsync(row, 1, isSkipped: null, CancellationToken.None);

        // Assert
        Assert.Same(row, result.Subject);
    }
}
