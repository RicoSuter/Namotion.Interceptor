using HomeBlaze.Storage.Files;
using HomeBlaze.Storage.Internal;
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

    private static readonly string TextPage = $"# Title\n\nOld text.\n\n:: {LeafType}#leaf\n::\n";

    private static HtmlSegment FindText(MarkdownFile page, string text) =>
        page.Children.Values.OfType<HtmlSegment>().Single(segment => segment.Start == HtmlSegmentStart.Text && segment.Html.Contains(text));

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
    public async Task WhenTheListSlotsAndTheNameOfABlockAreAskedFor_ThenTheSlotsOfConfiguredItemsComeFromItsTypeAndTheNameFromItsHeadLine()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync("page.md", PanelPage);
        var panel = (TestPanel)page.Children["panel"];

        // Act
        var slots = page.GetListSlots(panel);

        // Assert
        Assert.Equal([new MarkdownListSlot("Row", typeof(TestRow))], slots);
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
        await page.MoveBlockAsync(rowA, 1, CancellationToken.None);
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
        var result = await page.MoveBlockAsync(row, 1, CancellationToken.None);

        // Assert
        Assert.Same(row, result.Subject);
    }

    [Fact]
    public async Task WhenTheTextOfABlockOfTextIsAskedFor_ThenItIsItsMarkdown()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync("page.md", TextPage);

        // Act & Assert
        Assert.Equal("Old text.", page.GetTextBlockText(FindText(page, "Old text.")));
        Assert.Equal("# Title", page.GetTextBlockText(FindText(page, "Title")));
        Assert.Null(page.GetTextBlockText(new HtmlSegment("<p>elsewhere</p>")));
    }

    [Fact]
    public async Task WhenTheLinesOfATextBlockAreAskedFor_ThenTheyAreItsFirstAndLastLineAlsoForTextThatCannotBeEdited()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync("page.md", "# Title\n\n<div>\nraw\n</div>\n\nOld text.\n");
        var html = page.Children.Values.OfType<HtmlSegment>().Single(segment => segment.Html.Contains("raw"));

        // Act & Assert
        Assert.Equal((3, 5), page.GetTextBlockLines(html));
        Assert.Equal((7, 7), page.GetTextBlockLines(FindText(page, "Old text.")));
        Assert.Null(page.GetTextBlockLines(new HtmlSegment("<p>elsewhere</p>")));
    }

    [Fact]
    public async Task WhenThePageHasALoneCarriageReturn_ThenItsTextHasNoMarkdownAndAllowsNothing()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync("page.md", "x\rb\n\nText.\n");
        var segment = page.Children.Values.OfType<HtmlSegment>().Single(candidate => candidate.Html.Contains("Text."));

        // Act
        var text = page.GetTextBlockText(segment);
        var actions = page.GetBlockActions(segment);

        // Assert
        Assert.Null(text);
        Assert.Equal(default, actions);
    }

    [Fact]
    public async Task WhenAParagraphIsReplaced_ThenTheFileHasTheNewText()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync("page.md", TextPage);

        // Act
        var result = await page.ReplaceTextBlockAsync(FindText(page, "Old text."), "Old text.", "New *text*.", CancellationToken.None);

        // Assert
        Assert.True(result.IsWritten);
        Assert.Equal($"# Title\n\nNew *text*.\n\n:: {LeafType}#leaf\n::\n", await fixture.ReadAsync("page.md"));
    }

    [Fact]
    public async Task WhenTheTextChangedSinceItWasOpened_ThenTheReplaceIsRefused()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync("page.md", TextPage);

        // Act
        var result = await page.ReplaceTextBlockAsync(FindText(page, "Old text."), "Other text.", "New.", CancellationToken.None);

        // Assert
        Assert.Equal("The text changed since you opened it.", result.Refusal);
        Assert.Equal(TextPage, await fixture.ReadAsync("page.md"));
    }

    [Fact]
    public async Task WhenTheSegmentIsFromAnOlderRead_ThenTheReplaceIsRefused()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync("page.md", TextPage);
        var segment = FindText(page, "Old text.");
        await fixture.UpdatePageAsync(page, "Intro.\n\n" + TextPage);

        // Act
        var result = await page.ReplaceTextBlockAsync(segment, "Old text.", "New.", CancellationToken.None);

        // Assert
        Assert.Equal("The text changed since you opened it.", result.Refusal);
    }

    [Fact]
    public async Task WhenTextIsMovedDownPastAnUnnamedWidget_ThenTheFileHasTheNewOrderAndEveryWidgetKeepsItsSubject()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync("page.md", UnnamedLeavesPage);
        var leafA = page.Children["TestLeaf_0"];
        var leafB = page.Children["TestLeaf_1"];

        // Act
        var result = await page.MoveTextBlockAsync(FindText(page, "intro"), 1, CancellationToken.None);
        await page.OnFileChangedAsync(CancellationToken.None);

        // Assert
        Assert.True(result.IsWritten);
        Assert.Equal($":: {LeafType} Name=a\n::\n\nintro\n\n:: {LeafType} Name=b\n::\n", await fixture.ReadAsync("page.md"));
        var leaves = page.Children.Values.OfType<TestLeaf>().ToList();
        Assert.Same(leafA, leaves[0]);
        Assert.Same(leafB, leaves[1]);
    }

    [Fact]
    public async Task WhenTheActionsOfTextBlocksAreAskedFor_ThenTheyMoveAmongTheItemsOfTheirContent()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync("page.md", TextPage);

        // Act
        var title = page.GetBlockActions(FindText(page, "Title"));
        var text = page.GetBlockActions(FindText(page, "Old text."));
        var elsewhere = page.GetBlockActions(new HtmlSegment("<p>elsewhere</p>"));

        // Assert
        Assert.Equal(new MarkdownBlockActions(true, false, true, 0), title);
        Assert.Equal(new MarkdownBlockActions(true, true, true, 0), text);
        Assert.Equal(default, elsewhere);
    }

    [Fact]
    public async Task WhenTheSegmentOfAMovedTextIsFromAnOlderRead_ThenTheMoveIsRefused()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync("page.md", TextPage);
        var segment = FindText(page, "Old text.");
        await fixture.UpdatePageAsync(page, "Intro.\n\n" + TextPage);

        // Act
        var result = await page.MoveTextBlockAsync(segment, -1, CancellationToken.None);

        // Assert
        Assert.Equal("The text moved or changed. Select it again.", result.Refusal);
        Assert.Equal("Intro.\n\n" + TextPage, await fixture.ReadAsync("page.md"));
    }

    [Fact]
    public async Task WhenTextIsInsertedBeforeABlockOrBeforeText_ThenItStandsThere()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync("page.md", TextPage);

        // Act
        await page.InsertTextAsync(page, page.Children["leaf"], "Before the leaf.", CancellationToken.None);
        await page.OnFileChangedAsync(CancellationToken.None);
        await page.InsertTextAsync(page, FindText(page, "Old text."), "Before the text.", CancellationToken.None);

        // Assert
        Assert.Equal(
            $"# Title\n\nBefore the text.\n\nOld text.\n\nBefore the leaf.\n\n:: {LeafType}#leaf\n::\n",
            await fixture.ReadAsync("page.md"));
    }

    [Fact]
    public async Task WhenTextWithAnUnnamedWidgetIsInsertedBeforeUnnamedWidgets_ThenTheyKeepTheirSubjects()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync("page.md", UnnamedLeavesPage);
        var leafA = page.Children["TestLeaf_0"];
        var leafB = page.Children["TestLeaf_1"];

        // Act
        var result = await page.InsertTextAsync(page, leafA, $"Text.\n\n:: {LeafType} Name=new\n::", CancellationToken.None);
        await page.OnFileChangedAsync(CancellationToken.None);

        // Assert
        Assert.True(result.IsWritten);
        Assert.Equal(
            $"intro\n\nText.\n\n:: {LeafType} Name=new\n::\n\n:: {LeafType} Name=a\n::\n\n:: {LeafType} Name=b\n::\n",
            await fixture.ReadAsync("page.md"));
        var leaves = page.Children.Values.OfType<TestLeaf>().ToList();
        Assert.Equal(["new", "a", "b"], leaves.Select(leaf => leaf.Name));
        Assert.Same(leafA, leaves[1]);
        Assert.Same(leafB, leaves[2]);
    }

    [Fact]
    public async Task WhenTheFenceOfANewWidgetInTextIsAskedFor_ThenItIsOneMoreThanTheBlockAroundTheText()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync("page.md", $"intro\n\n:: {PanelType}#panel Title=P\nPanel text.\n::: Row Label=A\n:::\n::\n");
        var panel = (TestPanel)page.Children["panel"];
        var panelText = ((MarkdownFragment)panel.Content!).Children.Values.OfType<HtmlSegment>()
            .Single(segment => segment.Html.Contains("Panel text."));

        // Act & Assert
        Assert.Equal(2, page.GetWidgetFenceLengthInText(FindText(page, "intro")));
        Assert.Equal(3, page.GetWidgetFenceLengthInText(panelText));
        Assert.Equal(0, page.GetWidgetFenceLengthInText(new HtmlSegment("<p>elsewhere</p>")));
        Assert.Equal(2, page.GetWidgetFenceLengthInContent(page));
        Assert.Equal(3, page.GetWidgetFenceLengthInContent(panel.Content!));
        Assert.Equal(4, page.GetWidgetFenceLengthInContent(panel.Rows[0].Content!));
        Assert.Equal(0, page.GetWidgetFenceLengthInContent(new MarkdownFragment()));
    }

    [Fact]
    public async Task WhenTheNewTextIsNestedBeyondTheDepthLimit_ThenItIsRefusedAndThePageStaysEditable()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var content = $"Old text.\n\n:: {PanelType}#panel Title=P\n::\n";
        var page = await fixture.AddPageAsync("page.md", content);
        var panel = page.Children["panel"];
        var nested = new string('[', 4096);

        // Act
        var replaced = await page.ReplaceTextBlockAsync(FindText(page, "Old text."), "Old text.", nested, CancellationToken.None);
        var inserted = await page.InsertTextAsync(page, null, nested, CancellationToken.None);
        var added = await page.AddTextToSlotAsync(panel, null, nested, CancellationToken.None);
        var unchanged = await fixture.ReadAsync("page.md");
        await page.OnFileChangedAsync(CancellationToken.None);
        var widget = await page.InsertWidgetAsync(page, null, new TestLeaf(), "leaf", CancellationToken.None);

        // Assert
        Assert.All([replaced, inserted, added], result => Assert.Contains("too deeply nested", result.Refusal));
        Assert.Equal(content, unchanged);
        Assert.True(widget.IsWritten);
    }

    [Fact]
    public async Task WhenTheLastBlockHasNoClosingLine_ThenTextCannotBeInsertedAtTheEnd()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync("page.md", $"Intro.\n\n:: {LeafType}#leaf\n");

        // Act & Assert
        Assert.False(page.CanInsertTextAt(page, null));
        Assert.True(page.CanInsertTextAt(page, page.Children["leaf"]));
    }

    [Fact]
    public async Task WhenTheBlockToInsertBeforeIsNotInTheContent_ThenTheInsertIsRefused()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync("page.md", PanelPage);
        var row = ((TestPanel)page.Children["panel"]).Rows[0];

        // Act
        var result = await page.InsertWidgetAsync(page, row, new TestLeaf(), null, CancellationToken.None);

        // Assert
        Assert.Equal("The block to insert before is not in this content.", result.Refusal);
    }

    [Fact]
    public async Task WhenAParagraphIsReplacedWithWhiteSpace_ThenItsBlockIsRemoved()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync("page.md", TextPage);

        // Act
        var result = await page.ReplaceTextBlockAsync(FindText(page, "Old text."), "Old text.", " \n ", CancellationToken.None);

        // Assert
        Assert.True(result.IsWritten);
        Assert.Equal($"# Title\n\n:: {LeafType}#leaf\n::\n", await fixture.ReadAsync("page.md"));
    }

    [Fact]
    public async Task WhenTheFileChangedBeforeAReplace_ThenTheRefusalAsksToTryAgain()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync("page.md", TextPage);
        var changed = TextPage + "\nexternal\n";
        await fixture.WriteAsync("page.md", changed);

        // Act
        var result = await page.ReplaceTextBlockAsync(FindText(page, "Old text."), "Old text.", "New.", CancellationToken.None);

        // Assert: the text is the same after the read, so a second save succeeds.
        Assert.Equal("The page changed while it was edited. Try again.", result.Refusal);
        Assert.Equal(changed, await fixture.ReadAsync("page.md"));
        Assert.Equal(changed, page.Content);
    }

    [Fact]
    public async Task WhenTheFileChangedBeforeAnInsertBeforeText_ThenTheRefusalSaysThePageChanged()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync("page.md", TextPage);
        var segment = FindText(page, "Old text.");
        var changed = TextPage + "\nexternal\n";
        await fixture.WriteAsync("page.md", changed);

        // Act
        var result = await page.InsertWidgetAsync(page, segment, new TestLeaf(), null, CancellationToken.None);

        // Assert
        Assert.Equal("The page changed since you opened it.", result.Refusal);
        Assert.Equal(changed, await fixture.ReadAsync("page.md"));
    }

    [Fact]
    public async Task WhenTheTextToInsertBeforeIsFromAnOlderRead_ThenTheInsertIsRefused()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync("page.md", TextPage);
        var segment = FindText(page, "Old text.");
        var changed = "Intro.\n\n" + TextPage;
        await fixture.UpdatePageAsync(page, changed);

        // Act
        var result = await page.InsertTextAsync(page, segment, "New.", CancellationToken.None);

        // Assert
        Assert.Equal("The page changed since you opened it.", result.Refusal);
        Assert.False(page.CanInsertTextAt(page, segment));
        Assert.Equal(changed, await fixture.ReadAsync("page.md"));
    }

    [Fact]
    public async Task WhenTheTextToInsertBeforeIsInAnotherContent_ThenTheInsertIsRefused()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var content = $"Intro.\n\n:: {PanelType}#panel\nInside.\n::\n";
        var page = await fixture.AddPageAsync("page.md", content);
        var panelContent = Assert.IsType<MarkdownFragment>(((TestPanel)page.Children["panel"]).Content);

        // Act
        var result = await page.InsertTextAsync(panelContent, FindText(page, "Intro."), "New.", CancellationToken.None);

        // Assert
        Assert.Equal("The block to insert before is not in this content.", result.Refusal);
        Assert.Equal(content, await fixture.ReadAsync("page.md"));
    }

    [Fact]
    public async Task WhenInsertedTextOrAnInsertedWidgetWouldMakeThePageTooLarge_ThenTheInsertIsRefusedAndTheFileIsUnchanged()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var content = "# Title\n\n" + new string('a', MarkdownFile.MaxContentSize - 20) + "\n";
        var page = await fixture.AddPageAsync("page.md", content);

        // Act
        var text = await page.InsertTextAsync(page, null, "Text of more than ten bytes.", CancellationToken.None);
        var widget = await page.InsertWidgetAsync(page, null, new TestLeaf(), "leaf", CancellationToken.None);

        // Assert
        Assert.StartsWith("The Markdown source would be ", text.Refusal);
        Assert.StartsWith("The Markdown source would be ", widget.Refusal);
        Assert.Equal(content, await fixture.ReadAsync("page.md"));
    }

    [Fact]
    public async Task WhenAPageIsAlreadyTooLarge_ThenEditsThatShrinkItAreWritten()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var large = new string('a', MarkdownFile.MaxContentSize + 100);
        var page = await fixture.AddPageAsync("page.md", $"{large}\n\nOld text.\n\n:: {LeafType}#leaf\n::\n");

        // Act
        var replaced = await page.ReplaceTextBlockAsync(FindText(page, "Old text."), "Old text.", "New.", CancellationToken.None);
        await page.OnFileChangedAsync(CancellationToken.None);
        var deleted = await page.DeleteBlockAsync(page.Children["leaf"], CancellationToken.None);

        // Assert
        Assert.True(replaced.IsWritten);
        Assert.True(deleted.IsWritten);
        Assert.Equal($"{large}\n\nNew.\n\n", await fixture.ReadAsync("page.md"));
    }

    [Fact]
    public async Task WhenAPageIsAlreadyTooLarge_ThenAnEditThatGrowsItIsRefused()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var content = $"{new string('a', MarkdownFile.MaxContentSize + 100)}\n\nOld text.\n";
        var page = await fixture.AddPageAsync("page.md", content);

        // Act
        var result = await page.ReplaceTextBlockAsync(FindText(page, "Old text."), "Old text.", "Old text, longer.", CancellationToken.None);

        // Assert
        Assert.StartsWith("The Markdown source would be ", result.Refusal);
        Assert.Equal(content, await fixture.ReadAsync("page.md"));
    }
}
