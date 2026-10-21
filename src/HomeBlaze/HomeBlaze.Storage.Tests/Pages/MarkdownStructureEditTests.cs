using HomeBlaze.Storage.Pages;
using static HomeBlaze.Storage.Tests.Pages.TestTypeNames;

namespace HomeBlaze.Storage.Tests.Pages;

[Collection(MarkdownPageFixture.StorageCollection)]
public class MarkdownStructureEditTests
{
    private const string PanelType = Namespace + ".TestPanel";

    private static readonly string PanelPage =
        $"intro\n\n:: {PanelType}#panel Title=P\n::: Row Label=A\n:::\n::: Row Label=B\n:::\n::\n\n:: {LeafType}#leaf Name=l\n::\n";

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
        var result = await page.MoveBlockAsync(panel.Rows[0], 1, CancellationToken.None);
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
    public async Task WhenTheActionsOfTheRowsAreAskedFor_ThenOnlyTheirSiblingsCanBeSwappedWith()
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
        Assert.Equal(new MarkdownBlockActions(true, false, true, 2), panelActions);
        Assert.Equal(default, page.GetBlockActions(new TestLeaf()));
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
}
