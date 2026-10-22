# Inline Editing of Markdown Pages, Phase 2 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Keep subjects stable across the page's own edits (block-position keys, identity through the snapshot, results that name the subject), turn a move into a cut and an insert that passes chips, put chips in place, make insert lines overlays, make the Split preview inert, add editing of the top-level Markdown text of a page, and fold the simplifications that phase 1 left.

**Architecture:** HomeBlaze.Storage keys unnamed widgets by block position and lets a reload of the text that the snapshot describes take the snapshot's subjects by block index. `MarkdownPageOperations` gains a two-step move (cut, insert, each aligned) and text operations over `MarkdownTextBlocks`, the top-level content of each content area, which the segment builder marks so that every text block starts an `HtmlSegment` of its own. `MarkdownFile` refuses edits on a changed file, returns the moved or inserted subject, and offers text queries and edits. The UI talks to it through the abstract class `PageStructureEditor`, selects the result through `WidgetSelection.SelectSubject`, renders text blocks as selectable `MarkdownTextWidget`s in Inline mode only, and passes clicks on the inert Split preview to the widget under the pointer.

**Tech Stack:** .NET 10, C# 14, Blazor Server, MudBlazor 9, BlazorMonaco, Markdig 0.45, xUnit, Playwright (E2E).

**Spec:** [docs/superpowers/specs/2026-10-22-markdown-inline-editing-design.md](../specs/2026-10-22-markdown-inline-editing-design.md)

**Phase 1 plan (format and history):** [docs/superpowers/plans/2026-10-22-markdown-inline-editing.md](2026-10-22-markdown-inline-editing.md)

## Global Constraints

- Priorities from AGENTS.md: correctness (thread-safety, no torn snapshots, quiescent consistency) first, then allocations and CPU, then style.
- `MarkdownFile` reads `_parseResult` into a local once per operation and never pairs two reads; every write takes `_writeLock` through the existing `EnsureNotWithinWrite` / `BeginWrite` / `EndWrite` pattern.
- `WidgetSelection`, `SubjectComponent` and the editor run on the renderer's dispatcher. Never call `WidgetSelection` from the storage worker.
- Warnings are errors (`Directory.Build.props`). Every task ends with a clean `dotnet build src/Namotion.Interceptor.slnx`.
- Test names follow `When<Condition>_Then<ExpectedBehavior>` with `// Arrange`, `// Act`, `// Assert` (`// Act & Assert` for exceptions).
- No `Task.Delay` or `Thread.Sleep` in tests. E2E tests wait with Playwright `Assertions.Expect`, `PlaywrightFixture.WaitForDemoPageTextAsync` or `AsyncTestHelpers.WaitUntilAsync`.
- Descriptive names, no abbreviations, US English, no em dashes in docs, comments or messages, no hard wrapping in markdown.
- Comments explain only the why a reader cannot derive. XML docs state the contract.
- Allocations matter: nothing new allocates per widget per render in View mode. Lambdas and dictionaries in markup are fine where they render only in Inline mode at the selection.
- Public API snapshots: HomeBlaze.Storage and HomeBlaze.Components have `VerifyChecksTests.PublicApi`. When a task changes their public API, replace `VerifyChecksTests.PublicApi.verified.txt` with the `.received.txt` the test writes. Every type changed here was added by PR #785 and is unreleased, so no breaking-change approval is needed; `PageListSlot` and `MarkdownListSlot` are not merged (that would move a public type).
- No application references outside `src/HomeBlaze` (all work here is inside it).
- Commit messages: conventional prefix (`feat:`, `fix:`, `refactor:`, `test:`, `docs:`), no AI attribution of any kind (no agent names, no `Co-Authored-By`, no "Generated with").
- Unit tests: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests --filter "FullyQualifiedName~<Class>"` (and `HomeBlaze.Components.Tests`, `HomeBlaze.Storage.Blazor.Tests` likewise). Six `StorageMetadataWriteTests` fail on macOS before this work (case-only names); they are not regressions.
- E2E tests are `[Trait("Category", "Integration")]` and run per class: `dotnet test src/HomeBlaze/HomeBlaze.E2E.Tests --filter "FullyQualifiedName~InlineStructureEditingTests"`. A task that changes the UI runs the whole E2E project once before its commit: `dotnet test src/HomeBlaze/HomeBlaze.E2E.Tests`.

## File Structure

**HomeBlaze.Storage**
- Modify `Pages/MarkdownSubjectKeys.cs`: key unnamed widgets by block position (Task 1).
- Modify `Internal/MarkdownContentParser.cs`, `Internal/MarkdownContentParser.Slots.cs`: block-position keys, reuse of the snapshot's subjects, text segments (Tasks 1, 2, 12).
- Modify `Internal/MarkdownWriteRules.cs`: remove the block position rule (Task 1).
- Modify `Files/MarkdownFile.cs`: reuse hint, file check, result subjects, skip predicate, text API (Tasks 2, 3, 4, 7, 12, 14).
- Modify `Pages/MarkdownEditResult.cs`: `Subject` (Task 4).
- Modify `Pages/MarkdownPageOperations.cs`: result indices, cut-and-insert move, insert lines, text operations (Tasks 4, 7, 13).
- Modify `Pages/MarkdownBlockWriter.cs`: `GetLines` (replaces the dead `GetBlockText`), `CutLines`, `InsertMovedLines`, `HasEmptyLineAround`, `ReplaceLines` (Tasks 7, 13).
- Modify `Pages/MarkdownBlockAlignment.cs`: owner without closing line ending on the new lines (Task 13).
- Create `Pages/MarkdownTextBlocks.cs`: `MarkdownTextBlock` and `MarkdownTextBlocks.Find` (Task 6).
- Modify `Pages/MarkdownSegmentBuilder.cs`, `Pages/MarkdownParseResult.cs`, `Internal/HtmlSegment.cs`: text markers, `TextBlocks`, `HtmlSegmentStart` (Task 12).

**HomeBlaze.Storage.Blazor**
- Modify `Files/MarkdownPageStructureEditor.cs`: abstract base, reselection, dialog guard, skip predicate, text commands (Tasks 5, 7, 15, 16).
- Modify `Files/MarkdownFilePageComponent.razor` and `.razor.css`: editor construction, chip and line styles, text wrapper spacing (Tasks 5, 8, 9, 15).
- Modify `Files/MarkdownContentView.razor`: chips in place, labeled overlay lines, text groups, the insert menu (Tasks 8, 9, 15, 16).
- Create `Files/MarkdownTextWidget.razor`, `Files/MarkdownTextDialog.razor` (Task 15).

**HomeBlaze.Components**
- Rename `IPageStructureEditor.cs` to `PageStructureEditor.cs` (abstract class) (Task 5), extend in Tasks 15, 16.
- Modify `PageBlockActions.cs` (`CanEditText`, Task 15), `PageListSlot.cs` (`Find`, Task 17).
- Modify `WidgetSelection.cs` (`SelectSubject`, Task 5), `SubjectComponent.razor` (Tasks 5, 10, 15), `SubjectComponent.razor.css` (Tasks 8, 9, 10).
- Create `ITextBlockWidget.cs` (Task 15), `SelectionContainingWidgetBase.cs` (Task 17).
- Modify `IInvisibleWidget.cs` doc (Task 8). Delete `SubjectRevealer.cs` (Task 10).
- Modify `Components/BlockInsertLine.razor` (Tasks 9, 16), `Components/ValueMapWidget.razor`, `Components/ValueListWidget.razor`, `Components/ResponsiveWidget.razor` (Task 17), `Responsive.cs` (Task 18).

**HomeBlaze (app) and HomeBlaze.AI**
- Modify `HomeBlaze/Components/Pages/Pages.razor` and `Pages.razor.js`: inert preview, click forwarding, reveal target (Task 10).
- Modify `HomeBlaze.AI/Mcp/HomeBlazeMarkdownToolProvider.cs`: drop the block position sentence (Task 1).
- Modify docs under `HomeBlaze/Data/Files` (Tasks 1, 2, 19).

**Tests**
- Modify `HomeBlaze.Storage.Tests/MarkdownFileWriteTests.cs`, `Pages/MarkdownWidgetBlockTests.cs`, `Pages/MarkdownStructureEditTests.cs`, `Pages/MarkdownPageOperationsTests.cs`, `Pages/MarkdownBlockWriterTests.cs`, `Pages/MarkdownSegmentBuilderTests.cs`, `Pages/RepositoryPagesTests.cs`.
- Create `HomeBlaze.Storage.Tests/Pages/MarkdownTextBlocksTests.cs`.
- Modify `HomeBlaze.Components.Tests/ResponsiveTests.cs`.
- Modify `HomeBlaze.E2E.Tests/InlineStructureEditingTests.cs`; create `HomeBlaze.E2E.Tests/InlineTextEditingTests.cs`, `HomeBlaze.E2E.Tests/ViewHtmlGoldenTests.cs`, `HomeBlaze.E2E.Tests/TestData/Demo/TextEditing.md`, `HomeBlaze.E2E.Tests/TestData/Demo/TextBlocks.md`, `HomeBlaze.E2E.Tests/Golden/TextBlocks.view.html`.

## Task Order

Foundations: 1 keys, 2 identity, 3 changed-file refusal, 4 result subjects, 5 abstract editor and reselection, 6 text blocks of a content, 7 cut-and-insert move. UI: 8 chips in place, 9 overlay insert lines, 10 inert Split preview. Text editing: 11 golden view HTML, 12 text markers and segments, 13 text operations, 14 `MarkdownFile` text API, 15 selectable text blocks with Edit and Delete, 16 insert text from the plus menu. Simplifications: 17 selection base class and slot lookup, 18 Responsive hidden-class table. Then 19 docs and full verification.

Simplifications that are absorbed earlier: TryMove arithmetic (rewritten in Task 7), dead `GetBlockText` (becomes `GetLines` in Task 7), `SubjectRevealer` (Task 10).

---

### Task 1: Key unnamed widgets by their position among the blocks of their content

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.Storage/Pages/MarkdownSubjectKeys.cs:12-17,73-99`
- Modify: `src/HomeBlaze/HomeBlaze.Storage/Internal/MarkdownContentParser.cs:289-403`
- Modify: `src/HomeBlaze/HomeBlaze.Storage/Internal/MarkdownWriteRules.cs:70-73,98-104,148-153,430-463,672`
- Modify: `src/HomeBlaze/HomeBlaze.AI/Mcp/HomeBlazeMarkdownToolProvider.cs:32-38`
- Modify: `src/HomeBlaze/HomeBlaze/Data/Files/Demo/Configuration.md:144`, `src/HomeBlaze/HomeBlaze/Data/Files/Docs/administration/pages.md:918-924`, `src/HomeBlaze/HomeBlaze/Data/Files/Docs/architecture/design/storage.md:227,236`
- Test: `src/HomeBlaze/HomeBlaze.Storage.Tests/Pages/MarkdownWidgetBlockTests.cs`, `src/HomeBlaze/HomeBlaze.Storage.Tests/MarkdownFileWriteTests.cs:440-500`

**Interfaces:**
- Produces: `MarkdownSubjectKeys.GetContentKey(string? id, Type type, int blockPosition)`: the key of an unnamed widget counts only block segments of its content. `MarkdownSubjectKeys.Walk` and `MarkdownContentParser.BuildChildrenAsync` count the same way. `MarkdownWriteRules` no longer has `BlockPositionMessage` or `ActiveContentOfPage.UnnamedBlockPositions`.

- [ ] **Step 1: Write the failing tests**

Add to `MarkdownWidgetBlockTests`:

```csharp
    [Fact]
    public async Task WhenTextIsAddedBeforeAnUnnamedBlock_ThenTheReloadKeepsItsSubject()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync("page.md", $":: {LeafType} Name=a\n::\n");
        var leaf = page.Children["TestLeaf_0"];

        // Act
        await fixture.UpdatePageAsync(page, $"# Title\n\nIntro with {{{{ missing/Path }}}}.\n\n:: {LeafType} Name=a\n::\n");

        // Assert
        Assert.Same(leaf, page.Children["TestLeaf_0"]);
    }

    [Fact]
    public async Task WhenTextIsAddedBeforeAnUnnamedWidgetInsideABlock_ThenTheReloadKeepsItsSubject()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync(
            "page.md", $":: {TestTypeNames.Namespace}.TestPanel#panel\n::: {LeafType} Name=a\n:::\n::\n");
        var fragment = (MarkdownFragment)((TestPanel)page.Children["panel"]).Content!;
        var leaf = fragment.Children["TestLeaf_0"];

        // Act
        await fixture.UpdatePageAsync(
            page, $":: {TestTypeNames.Namespace}.TestPanel#panel\nText before the leaf.\n\n::: {LeafType} Name=a\n:::\n::\n");

        // Assert
        Assert.Same(leaf, ((MarkdownFragment)((TestPanel)page.Children["panel"]).Content!).Children["TestLeaf_0"]);
    }
```

Add `using HomeBlaze.Storage.Pages;` to the file if it is missing.

In `MarkdownFileWriteTests`, replace the three tests that expect the block position message (`WhenAnExpressionIsAddedBeforeABlockWithoutAName_ThenItIsRejectedBecauseTheSubjectWouldStartAgain`, `WhenAnExpressionIsAddedBeforeACardWithAnUnnamedWidget_ThenOnlyAnUnnamedCardRefusesIt`, `WhenTheFirstTextIsAddedBeforeABlockWithoutAName_ThenItIsRejectedBecauseTheSubjectWouldStartAgain`) with:

```csharp
    [Theory]
    [InlineData("# Motors", "# Motors at {{ /Devices/Speed }}")]
    [InlineData("# Motors\n\n", "# Motors\n\nThe speed is {{ /Devices/Speed }}.\n\n")]
    public async Task WhenAnExpressionIsAddedBeforeABlockWithoutAName_ThenTheTextIsChangedAndTheSubjectStays(string oldText, string newText)
    {
        // Arrange
        WriteFile("Motors.md", PageWithUnnamedBlock);
        var storage = await ConnectAsync();
        var markdown = Assert.IsType<MarkdownFile>(storage.Children["Motors.md"]);
        var motor = markdown.Children["Motor_0"];

        // Act
        await markdown.ReplaceTextAsync(oldText, newText, CancellationToken.None);

        // Assert
        Assert.Equal(PageWithUnnamedBlock.Replace(oldText, newText), ReadFile("Motors.md"));
        Assert.Same(motor, markdown.Children["Motor_0"]);
    }

    [Fact]
    public async Task WhenAnExpressionIsAddedBeforeAnUnnamedCardWithAnUnnamedWidget_ThenBothKeepTheirSubjects()
    {
        // Arrange
        const string content = "# Cards\n\n:: HomeBlaze.Components.Card Title=Main\n::: HomeBlaze.Components.Widget Path=/Devices\n:::\n::\n";
        WriteFile("Cards.md", content);
        var storage = await ConnectAsync();
        var markdown = Assert.IsType<MarkdownFile>(storage.Children["Cards.md"]);
        var card = markdown.Children["Card_0"];

        // Act
        await markdown.ReplaceTextAsync("# Cards", "# Cards at {{ /Devices/Speed }}", CancellationToken.None);

        // Assert
        Assert.Equal(content.Replace("# Cards", "# Cards at {{ /Devices/Speed }}"), ReadFile("Cards.md"));
        Assert.Same(card, markdown.Children["Card_0"]);
    }

    [Fact]
    public async Task WhenTheFirstTextIsAddedBeforeABlockWithoutAName_ThenTheTextIsChangedAndTheSubjectStays()
    {
        // Arrange
        const string content = ":: HomeBlaze.Samples.Motor Name=Unnamed\n::\n\nAfter the motor.";
        WriteFile("Motors.md", content);
        var storage = await ConnectAsync();
        var markdown = Assert.IsType<MarkdownFile>(storage.Children["Motors.md"]);
        var motor = markdown.Children["Motor_0"];

        // Act
        await markdown.ReplaceContentAsync("# Motors\n\n" + content, CancellationToken.None);

        // Assert
        Assert.Equal("# Motors\n\n" + content, ReadFile("Motors.md"));
        Assert.Same(motor, markdown.Children["Motor_0"]);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests --filter "FullyQualifiedName~MarkdownWidgetBlockTests|FullyQualifiedName~MarkdownFileWriteTests"`
Expected: FAIL. The widget tests fail on `Assert.Same` (or a missing `TestLeaf_0` key); the write tests throw `ArgumentException` with the block position message, and `Motor_0` does not exist yet (the key is `Motor_1`).

- [ ] **Step 3: Key by block position**

In `MarkdownSubjectKeys.cs` replace `GetContentKey` and the loop of `WalkContent`:

```csharp
    /// <summary>
    /// Gets the key of a widget block in the children of a page or of a fragment: its #name, or the name of its type and
    /// its position among the blocks of that content. Text and expressions around the block do not count.
    /// </summary>
    public static string GetContentKey(string? id, Type type, int blockPosition) =>
        id ?? $"{type.Name}_{blockPosition.ToString(CultureInfo.InvariantCulture)}";
```

```csharp
    private static void WalkContent(
        IReadOnlyList<MarkdownSegment> content,
        string path,
        Func<string, Type?> findType,
        Func<MarkdownBlock, IEnumerable<string>> getConfigurationNames,
        List<KeyedBlock> blocks)
    {
        var blockPosition = 0;
        foreach (var item in content)
        {
            if (item is not BlockMarkdownSegment segment)
            {
                continue;
            }

            var position = blockPosition++;
            var block = segment.Block;
            var type = block is { Kind: MarkdownBlockKind.Widget, Head: { } head } ? findType(head.Name) : null;
            var key = type is not null
                ? GetContentKey(block.Head!.Id, type, position)
                : $"?{block.Kind}_{position.ToString(CultureInfo.InvariantCulture)}";
            var place = $"{path}/c:{key}<{type?.FullName}>";
            blocks.Add(new KeyedBlock(segment, type, place, Slot: null, ContentKey: type is null ? null : key, ContainerPath: path));
            if (type is not null)
            {
                WalkOwner(segment, type, place + FormatNames(block, getConfigurationNames), findType, getConfigurationNames, blocks);
            }
        }
    }
```

In `MarkdownContentParser.BuildChildrenAsync`, count the block segments and pass the position:

```csharp
        var children = new Dictionary<string, IInterceptorSubject>();
        var blockPosition = 0;

        for (var index = 0; index < segments.Count; index++)
        {
            switch (segments[index])
            {
                // ... HtmlMarkdownSegment and ExpressionMarkdownSegment unchanged ...

                case BlockMarkdownSegment block:
                    var (key, subject, error) = await CreateWidgetAsync(
                        block, children, existingChildren, blockPosition++, state, cancellationToken);
```

In `CreateWidgetAsync`, rename the parameter `int index` to `int blockPosition` and use it in `MarkdownSubjectKeys.GetContentKey(head.Id, type, blockPosition)`.

- [ ] **Step 4: Remove the block position rule**

In `MarkdownWriteRules.cs`:
- Delete the constant `BlockPositionMessage` and the check that throws it (`if (!oldContent.UnnamedBlockPositions.SequenceEqual(...))`).
- Delete `GetUnnamedBlockPositions` and `AddUnnamedBlockPositions`.
- Remove `List<string> UnnamedBlockPositions` from the record `ActiveContentOfPage` and change its construction to `new ActiveContentOfPage(GetUnits(page, analysis), activeMarkup, analysis)`.
- Replace the first sentence of the XML summary of the public `EnsureActiveContentUnchanged` with: "Ensures that the new source of a page keeps its blocks and old subject(name) code blocks exactly as they are and in the same order, which keeps the key of every block (see <see cref="MarkdownSubjectKeys"/>), and adds none of the <see cref="MarkdownFile.ActiveContent"/> that the old source does not already contain."

The units comparison already keeps the sequence of all blocks, and the keys count only blocks, so no text change can move an unnamed block any more. Remove `using System.Globalization;` only if the compiler reports it unused.

In `HomeBlazeMarkdownToolProvider.cs`, end `BlocksRule` after "...the page keeps the real value." (drop the sentence about expressions, the first text and text between two blocks).

- [ ] **Step 5: Update the docs**

`Demo/Configuration.md` line 144, replace the paragraph with:

```markdown
The motor above has no name, so the page knows it by its type and its position among the blocks of the page. Add a block anywhere before it and save the page: the motor is created again and starts at a speed of zero, while the named motor keeps its speed. Text and expressions before it do not move it.
```

`Docs/administration/pages.md`, replace the paragraph "Add a sentence above both motors..." with "Add a block above both motors and save the page: the named motor keeps its speed, and the unnamed motor starts again at zero, because its position among the blocks changed. Text and expressions above it change nothing." Replace the bullet **Without a name** with:

```markdown
- **Without a name**: The position counts the blocks before the block at the same level, which is the page or the block around it. Adding or removing a block before it creates its subject again. Text and expressions do not count. A block that is moved, inserted or deleted in Inline mode leaves every other subject in place, see [Editing the blocks of a page inline](#editing-the-blocks-of-a-page-inline).
```

`Docs/architecture/design/storage.md`: in the tree, rename `Card_1` to `Card_0`; in the **Keys** bullet replace "its type name and its position when it has no name" with "its type name and its position among the blocks of its content when it has no name, so that text and expressions around it do not change it".

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests`
Expected: PASS except the six known `StorageMetadataWriteTests` on macOS. If another test asserts a positional key such as `Card_3` or the block position message, change it to the block position (count only blocks) or to the acceptance the rule now gives, and keep its intent.

Run: `dotnet test src/HomeBlaze/HomeBlaze.AI.Tests` (if it exists) to catch a snapshot of the tool description.
Expected: PASS, or update the snapshot that contains `BlocksRule`.

- [ ] **Step 7: Commit**

```bash
git add src/HomeBlaze docs
git commit -m "feat: key unnamed widgets by their position among the blocks of their content"
```

---

### Task 2: Keep subjects across the read inside the page's own writes

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.Storage/Internal/MarkdownContentParser.cs:256-287,347-403,576-612`
- Modify: `src/HomeBlaze/HomeBlaze.Storage/Internal/MarkdownContentParser.Slots.cs:224-232`
- Modify: `src/HomeBlaze/HomeBlaze.Storage/Files/MarkdownFile.cs:148-194`
- Modify: `src/HomeBlaze/HomeBlaze/Data/Files/Docs/architecture/design/storage.md:236`
- Test: `src/HomeBlaze/HomeBlaze.Storage.Tests/Pages/MarkdownStructureEditTests.cs`

**Interfaces:**
- Consumes: `MarkdownParseResult.BlockSubjects` of the current snapshot.
- Produces: `internal Task<MarkdownParseResult> MarkdownContentParser.ParseAsync(string? content, MarkdownFile parent, IDictionary<string, IInterceptorSubject> existingChildren, IReadOnlyDictionary<int, IInterceptorSubject>? reusableSubjects, CancellationToken cancellationToken)`. The public overload without `reusableSubjects` stays and passes null. `MarkdownFile.OnFileChangedAsync` passes the snapshot's block subjects when the text it read equals the snapshot's source.

- [ ] **Step 1: Write the failing tests**

Add to `MarkdownStructureEditTests`:

```csharp
    private static readonly string UnnamedLeavesPage = $"intro\n\n:: {LeafType} Name=a\n::\n\n:: {LeafType} Name=b\n::\n";

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
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests --filter "FullyQualifiedName~MarkdownStructureEditTests"`
Expected: FAIL on `WhenAnUnnamedRowIsMovedDown_ThenEachRowKeepsItsSubject` (`panel.Rows[0]` is `rowA` with label B), `WhenAWidgetIsInsertedBeforeUnnamedWidgets_ThenTheyKeepTheirSubjects` and `WhenAnUnnamedWidgetIsDeleted_ThenTheWidgetAfterItKeepsItsSubject`. The external change test passes already and guards the fallback.

- [ ] **Step 3: Thread the reusable subjects through the parser**

In `MarkdownContentParser.cs`, split `ParseAsync`:

```csharp
    /// <summary>
    /// Parses page content and reconciles it with the existing children.
    /// </summary>
    public Task<MarkdownParseResult> ParseAsync(
        string? content,
        MarkdownFile parent,
        IDictionary<string, IInterceptorSubject> existingChildren,
        CancellationToken cancellationToken) =>
        ParseAsync(content, parent, existingChildren, reusableSubjects: null, cancellationToken);

    /// <summary>
    /// Parses page content and reconciles it with the existing children. With <paramref name="reusableSubjects"/>, the
    /// subjects of a parse of the same content by block index, a block takes its subject from there instead of by its key.
    /// </summary>
    internal async Task<MarkdownParseResult> ParseAsync(
        string? content,
        MarkdownFile parent,
        IDictionary<string, IInterceptorSubject> existingChildren,
        IReadOnlyDictionary<int, IInterceptorSubject>? reusableSubjects,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(content))
        {
            return MarkdownParseResult.Empty;
        }

        var page = MarkdownPageParser.Parse(content);
        var basePath = GetLinkBasePath(parent);
        var pageName = EscapeFileNameForLink(Path.GetFileName(parent.FullPath));
        var segments = MarkdownSegmentBuilder.Build(page, url => RewriteLink(url, basePath, pageName));

        var state = new ParseState(parent, page, reusableSubjects);
        // ... rest unchanged
    }
```

In `ParseState`, add the constructor parameter and the property:

```csharp
        public ParseState(MarkdownFile page, MarkdownPage parsedPage, IReadOnlyDictionary<int, IInterceptorSubject>? reusableSubjects)
        {
            Page = page;
            Blocks = parsedPage.AllBlocks;
            ReusableSubjects = reusableSubjects;
            _source = parsedPage.Source;
            // ... rest unchanged
        }

        /// <summary>
        /// The subjects of the blocks of a parse of the same text by block index, which this parse takes instead of finding
        /// them by their key, or null.
        /// </summary>
        public IReadOnlyDictionary<int, IInterceptorSubject>? ReusableSubjects { get; }
```

In `CreateWidgetAsync`, replace `existingChildren.TryGetValue(key, out var existing);` with:

```csharp
        // The text is the one that the page describes, so the subjects of that description are where their blocks are,
        // also after a block before them was moved, inserted or removed, which would change their key.
        var existing = state.ReusableSubjects is { } reusableSubjects
            ? reusableSubjects.GetValueOrDefault(segment.Block.Index)
            : existingChildren.GetValueOrDefault(key);
```

In `MarkdownContentParser.Slots.cs` `ApplyContentAsync`, replace the line with `FindExistingSlotValue`:

```csharp
                // The fragments of an open slot have no block subject, so they keep being found by their position.
                var existing = state.ReusableSubjects is { } reusableSubjects && !slot.IsOpen
                    ? reusableSubjects.GetValueOrDefault(blocks[index].Block.Index)
                    : FindExistingSlotValue(existingValues, blocks[index].Block.Head!.Id, ref unnamedPosition);
```

`CreateBlockSubjectAsync` already ignores an existing subject of another type, so a reused subject of the wrong type is replaced.

- [ ] **Step 4: Pass the snapshot's subjects on a read of the described text**

In `MarkdownFile.OnFileChangedAsync`, replace the parse call:

```csharp
            _isGraphAheadOfSnapshot = true;
            Thread.MemoryBarrier();

            // A read of the text that the snapshot describes, such as the read inside the write of a structure edit, takes the
            // subjects of the snapshot: the edit has put them where their blocks went, which their keys may not tell.
            var described = _parseResult;
            var reusableSubjects = described.Page is { } describedPage && describedPage.Source == content
                ? described.BlockSubjects
                : null;
            var result = await _parser.ParseAsync(content, this, Children, reusableSubjects, cancellationToken);
```

`_reloadLock` is held, so no other reload publishes between the read of `_parseResult` and the parse; a write-back publishing in between makes this reload's subjects those of the text it read, which the next reload corrects by key.

- [ ] **Step 5: Document it**

In `Docs/architecture/design/storage.md`, append to the **Keys** bullet: "A read of exactly the text that the page last described, such as the read inside the write of an edit in Inline mode, takes the subjects of that description by block instead. The edit has carried them to their new blocks, so a moved, inserted or deleted block leaves every other subject in place."

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests`
Expected: PASS (except the six known macOS failures).

- [ ] **Step 7: Commit**

```bash
git add src/HomeBlaze
git commit -m "fix: keep the subjects of blocks across the read inside the page's own writes"
```

---

### Task 3: Refuse a structure edit when the file changed since it was read

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.Storage/Files/MarkdownFile.cs:491-570,624-708`
- Test: `src/HomeBlaze/HomeBlaze.Storage.Tests/Pages/MarkdownStructureEditTests.cs`

**Interfaces:**
- Produces: `private Task<MarkdownEditResult> EditStructureAsync(Func<MarkdownParseResult, (MarkdownStructureChange? Change, string? Refusal)> operation, CancellationToken cancellationToken)`: the operation gets the whole snapshot; the method reads the file first and refuses with `StructureChangedRefusal` ("The page changed while it was edited. Try again.") after reading the page again when the file differs. `private Task ReloadAfterRefusalAsync(CancellationToken cancellationToken)`.

- [ ] **Step 1: Write the failing test**

```csharp
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
    }
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests --filter "FullyQualifiedName~WhenTheFileChangedSinceThePageWasRead"`
Expected: FAIL: the delete is written and the external line is lost.

- [ ] **Step 3: Implement**

In `MarkdownFile.cs`, extract the reload of `WriteBlockAsync` into a method and use it there:

```csharp
    // Under _writeLock, after a refusal. A failed read is logged, and the next pass of the storage reads the file again.
    private async Task ReloadAfterRefusalAsync(CancellationToken cancellationToken)
    {
        try
        {
            await OnFileChangedAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _parser.LogFailedReload(FullPath, exception);
        }
    }
```

In `WriteBlockAsync` replace the `try { await OnFileChangedAsync(...) } catch ...` block with `await ReloadAfterRefusalAsync(cancellationToken);`.

Replace `EditStructureAsync`:

```csharp
    private async Task<MarkdownEditResult> EditStructureAsync(
        Func<MarkdownParseResult, (MarkdownStructureChange? Change, string? Refusal)> operation,
        CancellationToken cancellationToken)
    {
        EnsureNotWithinWrite();
        await _writeLock.WaitAsync(cancellationToken);
        var write = BeginWrite();
        try
        {
            var snapshot = _parseResult;
            if (snapshot.Page is not { } page)
            {
                return MarkdownEditResult.Refused("The page has not been read yet.");
            }

            // The new text is computed from the snapshot, which lags behind a change of the file until its event arrives,
            // and would undo that change. The page is read now, so that a second try works on the file.
            if (await ReadSourceUnlockedAsync(cancellationToken) != page.Source)
            {
                _parser.LogRefusedWrite(FullPath, StructureChangedRefusal);
                await ReloadAfterRefusalAsync(cancellationToken);
                return MarkdownEditResult.Refused(StructureChangedRefusal);
            }

            // The operations change only text, so a refusal leaves the graph as the snapshot has it.
            var (change, refusal) = operation(snapshot);
            if (change is null)
            {
                _parser.LogRefusedWrite(FullPath, refusal!);
                return MarkdownEditResult.Refused(refusal!);
            }

            var publishRefusal = await PublishAndWriteAsync(snapshot, change.Write, change.ChangedBlock, StructureChangedRefusal);
            return publishRefusal is null ? MarkdownEditResult.Written : MarkdownEditResult.Refused(publishRefusal);
        }
        finally
        {
            EndWrite(write);
        }
    }
```

Change the four callers to take the snapshot, for example:

```csharp
    public Task<MarkdownEditResult> DeleteBlockAsync(IInterceptorSubject subject, CancellationToken cancellationToken) =>
        EditStructureAsync(
            snapshot => FindOwnBlock(snapshot.Page!, snapshot.BlockSubjects, subject) is { } block
                ? (MarkdownPageOperations.TryRemove(snapshot.Page!, snapshot.BlockSubjects, block, out var refusal), refusal)
                : (null, NoBlockRefusal),
            cancellationToken);
```

Do the same in `MoveBlockAsync`, `InsertWidgetAsync` (`var page = snapshot.Page!; var subjects = snapshot.BlockSubjects;` at the top of its lambda) and `AddSlotItemAsync`. Add to the XML docs of the four methods: "Refused when the file differs from the text the page last read; the page then reads it."

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests --filter "FullyQualifiedName~MarkdownStructureEditTests|FullyQualifiedName~MarkdownFileWriteTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/HomeBlaze
git commit -m "fix: refuse a structure edit when the file changed since the page read it"
```

---

### Task 4: Name the moved or inserted subject in the result of a structure edit

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.Storage/Pages/MarkdownEditResult.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Storage/Pages/MarkdownPageOperations.cs:5-10,46-108,266-348`
- Modify: `src/HomeBlaze/HomeBlaze.Storage/Files/MarkdownFile.cs` (`EditStructureAsync`)
- Modify: `src/HomeBlaze/HomeBlaze.Storage.Tests/VerifyChecksTests.PublicApi.verified.txt`
- Test: `src/HomeBlaze/HomeBlaze.Storage.Tests/Pages/MarkdownPageOperationsTests.cs`, `Pages/MarkdownStructureEditTests.cs`, `MarkdownFileWriteTests.cs`

**Interfaces:**
- Produces:
  - `public readonly record struct MarkdownEditResult(string? Refusal, IInterceptorSubject? Subject = null)` with `IsWritten`, `Written`, `WrittenFor(IInterceptorSubject? subject)`, `Refused(string refusal)`.
  - `internal sealed record MarkdownStructureChange(MarkdownBlockWrite Write, MarkdownBlock? ChangedBlock, int ResultBlockIndex = -1)`: the index in `Write.Page` of the block whose subject the result names.
  - `private static MarkdownBlock? MarkdownPageOperations.FindFirstBlockFrom(MarkdownPage page, MarkdownLineEdit edit)`.

- [ ] **Step 1: Write the failing tests**

`MarkdownPageOperationsTests`:

```csharp
    [Fact]
    public void WhenAWidgetIsInserted_ThenTheChangeNamesItsBlock()
    {
        // Arrange
        var page = MarkdownPageParser.Parse("intro\n\n:: Demo.One\n::\n");

        // Act
        var change = MarkdownPageOperations.TryInsertWidget(
            page, NoSubjects, owner: null, before: page.Blocks[0], "Demo.New", null, "{}", out _);

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
```

`MarkdownStructureEditTests`:

```csharp
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
```

`MarkdownFileWriteTests` (its storage reads the page inside the write):

```csharp
    [Fact]
    public async Task WhenAWidgetIsInsertedIntoAPageOfTheStorage_ThenTheResultNamesTheSubjectOfItsBlock()
    {
        // Arrange
        WriteFile("Cards.md", "# Cards\n");
        var storage = await ConnectAsync();
        var markdown = Assert.IsType<MarkdownFile>(storage.Children["Cards.md"]);

        // Act
        var result = await markdown.InsertWidgetAsync(
            markdown, null, new HomeBlaze.Components.Card { Title = "Added" }, "added", CancellationToken.None);

        // Assert
        Assert.True(result.IsWritten);
        Assert.Same(markdown.Children["added"], result.Subject);
    }
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests --filter "FullyQualifiedName~TheChangeNamesItsBlock|FullyQualifiedName~TheResultNames"`
Expected: FAIL to compile (`ResultBlockIndex`, `Subject` do not exist).

- [ ] **Step 3: Implement the result**

`MarkdownEditResult.cs`:

```csharp
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Pages;

/// <summary>Whether a change of the blocks or the text of a page was written, why not, and what it wrote.</summary>
/// <param name="Refusal">Why nothing was written, or null when the change was written.</param>
/// <param name="Subject">
/// The subject of the block that the change moved or inserted, or the <c>HtmlSegment</c> of the first text block that it
/// wrote, once the page describes the written text, which it does when the storage read the file inside the write. Null
/// otherwise, and for a deletion.
/// </param>
/// <remarks>
/// A change whose write the storage fails has no result: the exception of the storage propagates, and the page then
/// describes its file again, unless reading it failed as well.
/// </remarks>
public readonly record struct MarkdownEditResult(string? Refusal, IInterceptorSubject? Subject = null)
{
    /// <summary>Gets whether the change was written.</summary>
    public bool IsWritten => Refusal is null;

    /// <summary>A change that was written and names no subject.</summary>
    public static MarkdownEditResult Written => default;

    /// <summary>A change that was written, with the subject it names.</summary>
    public static MarkdownEditResult WrittenFor(IInterceptorSubject? subject) => new(null, subject);

    /// <summary>A change that was refused for <paramref name="refusal"/>.</summary>
    public static MarkdownEditResult Refused(string refusal) => new(refusal);
}
```

`MarkdownPageOperations.cs`:

```csharp
/// <summary>What a change of the blocks of a page did.</summary>
/// <param name="Write">The page with the new text and the subjects of its blocks.</param>
/// <param name="ChangedBlock">
/// The block of the page before the change whose content changed, or null when the change is directly on the page.
/// </param>
/// <param name="ResultBlockIndex">The index in the page of <paramref name="Write"/> of the block that the change moved or inserted, or -1.</param>
internal sealed record MarkdownStructureChange(MarkdownBlockWrite Write, MarkdownBlock? ChangedBlock, int ResultBlockIndex = -1);
```

Add the helper next to `FindBlockAt`:

```csharp
    // The first block on the inserted lines, which is the new block: blocks are listed in the order of their head lines.
    private static MarkdownBlock? FindFirstBlockFrom(MarkdownPage page, MarkdownLineEdit edit)
    {
        foreach (var block in page.AllBlocks)
        {
            if (block.Line >= edit.Line && block.Line < edit.InsertedEnd)
            {
                return block;
            }
        }

        return null;
    }
```

In `TryInsertWidget` and `TryAppendSlotBlock` return `new MarkdownStructureChange(..., owner, FindFirstBlockFrom(after, edit)?.Index ?? -1)` (in `TryAppendSlotBlock` keep the parsed page in a variable named `after`).

In `TryMove` (replaced in Task 7), name the moved block:

```csharp
        var secondAfter = FindBlockAt(after, first.Line);
        var firstAfter = FindBlockAt(after, first.Line + secondLineCount + middleLineCount);
        CarrySubjects(blockSubjects, second, secondAfter, subjects);
        CarrySubjects(blockSubjects, first, firstAfter, subjects);

        var moved = ReferenceEquals(block, second) ? secondAfter : firstAfter;
        return new MarkdownStructureChange(new MarkdownBlockWrite(after, subjects, [], HasInsertedOrRemovedBlocks: true), parent, moved.Index);
```

In `MarkdownFile.EditStructureAsync`, return the subject:

```csharp
            var publishRefusal = await PublishAndWriteAsync(snapshot, change.Write, change.ChangedBlock, StructureChangedRefusal);
            return publishRefusal is null
                ? MarkdownEditResult.WrittenFor(FindResultSubject(change))
                : MarkdownEditResult.Refused(publishRefusal);
```

```csharp
    // After the write: the read inside it has created the subject of a new block, when the storage read the file there.
    // Without that read, the published snapshot still names a moved subject, because the edit carried it.
    private IInterceptorSubject? FindResultSubject(MarkdownStructureChange change)
    {
        var snapshot = _parseResult;
        if (snapshot.Page?.Source != change.Write.Page.Source)
        {
            return null;
        }

        return change.ResultBlockIndex >= 0 ? snapshot.BlockSubjects.GetValueOrDefault(change.ResultBlockIndex) : null;
    }
```

- [ ] **Step 4: Run the tests and accept the API snapshot**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests`
Expected: all new tests PASS; `VerifyChecksTests.PublicApi` FAILS with the new `MarkdownEditResult` members. Replace `VerifyChecksTests.PublicApi.verified.txt` with the `.received.txt`, review that the only differences are `MarkdownEditResult`, run again: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/HomeBlaze
git commit -m "feat: name the moved or inserted subject in the result of a structure edit"
```

---

### Task 5: Abstract page structure editor, reselection and a fresh action bar

**Files:**
- Rename: `src/HomeBlaze/HomeBlaze.Components/IPageStructureEditor.cs` to `src/HomeBlaze/HomeBlaze.Components/PageStructureEditor.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Components/WidgetSelection.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Components/SubjectComponent.razor:177-178,310-333,446-458`
- Modify: `src/HomeBlaze/HomeBlaze.Components/Components/ValueMapWidget.razor:45-46`, `Components/ValueListWidget.razor:66-67`
- Modify: `src/HomeBlaze/HomeBlaze.Storage.Blazor/Files/MarkdownContentView.razor:70-71`
- Modify: `src/HomeBlaze/HomeBlaze.Storage.Blazor/Files/MarkdownFilePageComponent.razor`
- Modify: `src/HomeBlaze/HomeBlaze.Storage.Blazor/Files/MarkdownPageStructureEditor.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Storage/Files/MarkdownFile.cs` (`GetBlockActions`)
- Modify: `src/HomeBlaze/HomeBlaze.Components.Tests/VerifyChecksTests.PublicApi.verified.txt`
- Test: `src/HomeBlaze/HomeBlaze.E2E.Tests/InlineStructureEditingTests.cs`

**Interfaces:**
- Consumes: `MarkdownEditResult.Subject` (Task 4).
- Produces:
  - `public abstract class PageStructureEditor` in HomeBlaze.Components with the abstract members `GetActions`, `GetListSlots`, `GetBlockName`, `CanInsertInto`, `DeleteAsync`, `MoveAsync`, `InsertWidgetAsync`, `AddListItemAsync` (same signatures as the interface).
  - `public void WidgetSelection.SelectSubject(IInterceptorSubject subject, SubjectComponent root)` and `internal void WidgetSelection.OnRendered(SubjectComponent component)`.
  - `internal SubjectComponent? SubjectComponent.FindSelectable(IInterceptorSubject subject)`.
  - `MarkdownPageStructureEditor(MarkdownFile file, WidgetSelection selection, SubjectComponent pageComponent, IDialogService dialogService, ISnackbar snackbar, SubjectFactory subjectFactory, ILogger logger)` with `File`, `Selection`, `PageComponent`.
  - E2E helpers in `InlineStructureEditingTests`: `GetInsertLineBefore(ILocator wrapper)` and `OpenWidgetDialogAtAsync(IPage page, ILocator line)`.

- [ ] **Step 1: Write the failing E2E tests**

Add to `InlineStructureEditingTests`:

```csharp
    [Fact]
    public async Task WhenARowIsMovedDown_ThenItStaysSelectedAndItsBarOffersMoveUp()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();
        var actionBar = await SelectRowAsync(page, "Row A");
        await Assertions.Expect(actionBar.GetByTestId("move-up-button")).ToHaveCountAsync(0, new() { Timeout = Timeout });

        // Act
        await actionBar.GetByTestId("move-down-button").ClickAsync();

        // Assert
        await _fixture.WaitForDemoPageTextAsync(
            PageName,
            text => text.IndexOf("Label=\"Row B\"", StringComparison.Ordinal) < text.IndexOf("Label=\"Row A\"", StringComparison.Ordinal),
            "The rows were not swapped.");
        await Assertions.Expect(GetValueRowWrapper(page, "Row A")).ToHaveClassAsync(new Regex(@"\bselected\b"), new() { Timeout = Timeout });
        await Assertions.Expect(page.GetWidgetActionBar().GetByTestId("move-up-button")).ToBeVisibleAsync(new() { Timeout = Timeout });
        await Assertions.Expect(page.GetWidgetActionBar().GetByTestId("move-down-button")).ToHaveCountAsync(0, new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenAWidgetIsInserted_ThenItIsSelected()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();
        var line = GetInsertLineBefore(GetWrapperOf(page, "switch"));

        // Act
        await OpenWidgetDialogAtAsync(page, line);
        var dialog = page.GetByTestId("create-subject-dialog");
        await dialog.GetByTestId("type-search-input").FillAsync("Alert");
        await dialog.GetByTestId("type-row-alert").ClickAsync();
        await dialog.GetByTestId("create-name-input").FillAsync("selectedalert");
        await page.GetByTestId("create-button").ClickAsync();

        // Assert
        await _fixture.WaitForDemoPageTextAsync(
            PageName, text => text.Contains("HomeBlaze.Components.Alert#selectedalert"), "The alert was not inserted.");
        await Assertions.Expect(page.Locator(".subject-component-wrapper.selected .mud-alert")).ToHaveCountAsync(1, new() { Timeout = Timeout });
    }
```

Add the helpers at the end of the class, and change `WhenNothingIsSelected_ThenThePageHasInsertLinesAndOneInsertsAWidgetThere` to use them (`var line = GetInsertLineBefore(GetWrapperOf(page, "switch")); await OpenWidgetDialogAtAsync(page, line);` instead of `lines.Nth(1).ClickAsync()`, keeping its first assertion on `lines.First`):

```csharp
    // The insert line right before the widget: the lines are siblings of the widgets of their content.
    private static ILocator GetInsertLineBefore(ILocator wrapper) =>
        wrapper.Locator("xpath=preceding-sibling::*[@data-testid='block-insert-line'][1]");

    // Opens the dialog that inserts a widget at the line.
    private static async Task OpenWidgetDialogAtAsync(IPage page, ILocator line)
    {
        await line.GetByRole(AriaRole.Button).ClickAsync();
        await Assertions.Expect(page.GetByTestId("create-subject-dialog")).ToBeVisibleAsync(new() { Timeout = Timeout });
    }
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.E2E.Tests --filter "FullyQualifiedName~InlineStructureEditingTests"`
Expected: the two new tests FAIL: the row's bar still lacks Move up after the move (it does not render again), and no alert is selected after the insert.

- [ ] **Step 3: The abstract class**

`git mv src/HomeBlaze/HomeBlaze.Components/IPageStructureEditor.cs src/HomeBlaze/HomeBlaze.Components/PageStructureEditor.cs`, then:

```csharp
using Namotion.Interceptor;

namespace HomeBlaze.Components;

/// <summary>
/// Changes the blocks of the page that is edited in place. A page cascades it while it is edited inline, and the widgets
/// show their structure actions only then. A member added later has a default that does nothing, so that an editor of
/// another kind of page keeps working.
/// </summary>
public abstract class PageStructureEditor
{
    /// <summary>Gets what the block of the subject allows, all false when the subject has no block of its own.</summary>
    public abstract PageBlockActions GetActions(IInterceptorSubject subject);

    /// <summary>Gets the slots of the block of <paramref name="owner"/> that hold lists of blocks.</summary>
    public abstract IReadOnlyList<PageListSlot> GetListSlots(IInterceptorSubject owner);

    /// <summary>Gets the name of the block of the subject on the page, or null without one.</summary>
    public abstract string? GetBlockName(IInterceptorSubject subject);

    /// <summary>Gets whether new blocks can be inserted into <paramref name="content"/>, the page or a fragment of it.</summary>
    public abstract bool CanInsertInto(IInterceptorSubject content);

    /// <summary>Asks for confirmation and deletes the block of the subject.</summary>
    public abstract Task DeleteAsync(IInterceptorSubject subject);

    /// <summary>Moves the block of the subject one place up (-1) or down (1) among the content around it, and keeps it selected.</summary>
    public abstract Task MoveAsync(IInterceptorSubject subject, int direction);

    /// <summary>
    /// Asks for a widget, inserts it into <paramref name="content"/> before <paramref name="before"/>, or at its end, and
    /// selects it.
    /// </summary>
    public abstract Task InsertWidgetAsync(IInterceptorSubject content, IInterceptorSubject? before);

    /// <summary>Asks for the item when the slot has a type, appends it to the slot of <paramref name="owner"/> and selects it.</summary>
    public abstract Task AddListItemAsync(IInterceptorSubject owner, PageListSlot slot);
}
```

Replace every `IPageStructureEditor` with `PageStructureEditor` in `SubjectComponent.razor`, `ValueMapWidget.razor`, `ValueListWidget.razor`, `MarkdownContentView.razor` and `MarkdownFilePageComponent.razor` (`TValue="PageStructureEditor"`).

- [ ] **Step 4: Select a subject, now or once it renders**

`WidgetSelection.cs`:

```csharp
using Namotion.Interceptor;

namespace HomeBlaze.Components;

/// <summary>
/// The widget of a page in edit mode whose actions are shown. At most one widget of a page is selected.
/// </summary>
/// <remarks>
/// Not thread-safe: use it on the dispatcher of the renderer, like the components that it notifies.
/// </remarks>
public sealed class WidgetSelection
{
    // The subject whose widget is selected when it renders, see SelectSubject.
    private IInterceptorSubject? _pendingSubject;

    /// <summary>
    /// Gets the selected widget, or null when none is selected.
    /// </summary>
    public SubjectComponent? Selected { get; private set; }

    /// <summary>
    /// Raised after the selection changed, for components that depend on where the selection is, such as a widget
    /// that shows more while the selection is inside it.
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// Selects the widget, or clears the selection when it is null.
    /// </summary>
    public void Select(SubjectComponent? component)
    {
        _pendingSubject = null;
        var previous = Selected;
        if (ReferenceEquals(previous, component))
        {
            return;
        }

        Selected = component;
        previous?.OnSelectionChanged();
        component?.OnSelectionChanged();
        Changed?.Invoke();
    }

    /// <summary>
    /// Selects the selectable widget of <paramref name="subject"/> among <paramref name="root"/> and the widgets inside it.
    /// When there is none yet, such as for a block that a reload of the page has just created, the selection is cleared and
    /// the first such widget that renders is selected.
    /// </summary>
    public void SelectSubject(IInterceptorSubject subject, SubjectComponent root)
    {
        if (root.FindSelectable(subject) is { } component)
        {
            Select(component);
            return;
        }

        Select(null);
        _pendingSubject = subject;
    }

    /// <summary>
    /// Clears the selection.
    /// </summary>
    public void Clear() => Select(null);

    internal void OnRendered(SubjectComponent component)
    {
        if (_pendingSubject is { } subject && ReferenceEquals(component.Subject, subject) && component.IsSelectable)
        {
            Select(component);
        }
    }

    internal void Remove(SubjectComponent component)
    {
        if (ReferenceEquals(Selected, component))
        {
            Selected = null;
            Changed?.Invoke();
        }
    }
}
```

In `SubjectComponent.razor`, below the existing `FindSelectable()`:

```csharp
    /// <summary>Gets the selectable widget of the subject among this widget and the widgets inside it, or null.</summary>
    internal SubjectComponent? FindSelectable(IInterceptorSubject subject)
    {
        if (ReferenceEquals(Subject, subject) && IsSelectable)
        {
            return this;
        }

        if (_childComponents != null)
        {
            foreach (var child in _childComponents)
            {
                if (child.FindSelectable(subject) is { } selectable)
                {
                    return selectable;
                }
            }
        }

        return null;
    }
```

At the start of `OnAfterRenderAsync`:

```csharp
        // A reload creates the widget of an inserted block after the edit returned, see WidgetSelection.SelectSubject.
        Selection?.OnRendered(this);
```

- [ ] **Step 5: A bar that follows the page**

In `MarkdownFile.GetBlockActions`, read the tracked children first:

```csharp
    /// <summary>
    /// Gets what the block of <paramref name="subject"/> allows, all false when it has no block on this page. It reads the
    /// children of the page, so a component that renders it renders again after every reload.
    /// </summary>
    public MarkdownBlockActions GetBlockActions(IInterceptorSubject subject)
    {
        // The snapshot is not tracked, and the bar of a selected widget that a move kept would show the buttons of its old
        // place. Do not remove it, although its result is not used.
        _ = Children;

        var snapshot = _parseResult;
        // ... unchanged
    }
```

- [ ] **Step 6: The editor reselects, and guards its dialogs**

Replace `MarkdownPageStructureEditor.cs`:

```csharp
using HomeBlaze.Components;
using HomeBlaze.Components.Dialogs;
using HomeBlaze.Services;
using HomeBlaze.Storage.Blazor.Models;
using HomeBlaze.Storage.Files;
using HomeBlaze.Storage.Pages;
using Microsoft.Extensions.Logging;
using MudBlazor;
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Blazor.Files;

/// <summary>Changes the blocks of a markdown page that is edited inline, with the dialogs that ask for them.</summary>
internal sealed class MarkdownPageStructureEditor : PageStructureEditor
{
    private const string ChangedWhileAskingMessage = "The page changed while the dialog was open. Try again.";

    private readonly MarkdownFile _file;
    private readonly WidgetSelection _selection;
    private readonly SubjectComponent _pageComponent;
    private readonly IDialogService _dialogService;
    private readonly ISnackbar _snackbar;
    private readonly SubjectFactory _subjectFactory;
    private readonly ILogger _logger;

    public MarkdownPageStructureEditor(
        MarkdownFile file,
        WidgetSelection selection,
        SubjectComponent pageComponent,
        IDialogService dialogService,
        ISnackbar snackbar,
        SubjectFactory subjectFactory,
        ILogger logger)
    {
        _file = file;
        _selection = selection;
        _pageComponent = pageComponent;
        _dialogService = dialogService;
        _snackbar = snackbar;
        _subjectFactory = subjectFactory;
        _logger = logger;
    }

    /// <summary>Gets the page whose blocks this editor changes.</summary>
    public MarkdownFile File => _file;

    /// <summary>Gets the selection that the editor sets after an edit.</summary>
    public WidgetSelection Selection => _selection;

    /// <summary>Gets the widget of the page, inside which the editor finds the widget to select.</summary>
    public SubjectComponent PageComponent => _pageComponent;

    public override PageBlockActions GetActions(IInterceptorSubject subject)
    {
        var actions = _file.GetBlockActions(subject);
        return new PageBlockActions(actions.CanDelete, actions.CanMoveUp, actions.CanMoveDown);
    }

    public override IReadOnlyList<PageListSlot> GetListSlots(IInterceptorSubject owner)
    {
        var slots = _file.GetListSlots(owner);
        var result = new PageListSlot[slots.Count];
        for (var index = 0; index < slots.Count; index++)
        {
            result[index] = new PageListSlot(slots[index].Name, slots[index].ItemType);
        }

        return result;
    }

    public override string? GetBlockName(IInterceptorSubject subject) => _file.GetBlockName(subject);

    public override bool CanInsertInto(IInterceptorSubject content) => _file.IsContentOfPage(content);

    public override async Task DeleteAsync(IInterceptorSubject subject)
    {
        var actions = _file.GetBlockActions(subject);
        if (!actions.CanDelete)
        {
            return;
        }

        var name = _file.GetBlockName(subject) ?? SubjectTypeCatalog.Describe(subject.GetType()).DisplayName;
        var message = actions.ContainedBlockCount switch
        {
            0 => $"Delete {name}?",
            1 => $"Delete {name} and the block inside it?",
            var count => $"Delete {name} and the {count} blocks inside it?"
        };

        var content = _file.Content;
        var confirmed = await _dialogService.ShowMessageBoxAsync("Delete block", message, yesText: "Delete", cancelText: "Cancel");
        if (confirmed == true && IsUnchangedSince(content))
        {
            await RunAsync(() => _file.DeleteBlockAsync(subject, CancellationToken.None));
        }
    }

    public override async Task MoveAsync(IInterceptorSubject subject, int direction) =>
        await RunAsync(() => _file.MoveBlockAsync(subject, direction, CancellationToken.None));

    public override async Task InsertWidgetAsync(IInterceptorSubject content, IInterceptorSubject? before)
    {
        var pageContent = _file.Content;
        var result = await SubjectSetupDialog.ShowAsync(_dialogService, new SubjectSetupOptions
        {
            NameField = MarkdownBlockNames.CreateNameOptions(pageContent ?? string.Empty)
        });

        if (result is null)
        {
            return;
        }

        // Only the text of the subject goes into the page, and the reload creates the instance that lives on.
        try
        {
            if (IsUnchangedSince(pageContent))
            {
                await RunAsync(() => _file.InsertWidgetAsync(content, before, result.Subject, result.Name, CancellationToken.None));
            }
        }
        finally
        {
            _subjectFactory.DiscardSubject(result.Subject);
        }
    }

    public override async Task AddListItemAsync(IInterceptorSubject owner, PageListSlot slot)
    {
        var pageContent = _file.Content;
        IInterceptorSubject? item = null;
        if (slot.ItemType is { } itemType)
        {
            var result = await SubjectSetupDialog.ShowAsync(_dialogService, new SubjectSetupOptions { Type = itemType });
            if (result is null)
            {
                return;
            }

            item = result.Subject;
        }

        try
        {
            if (IsUnchangedSince(pageContent))
            {
                await RunAsync(() => _file.AddSlotItemAsync(owner, slot.Name, item, CancellationToken.None));
            }
        }
        finally
        {
            if (item is not null)
            {
                _subjectFactory.DiscardSubject(item);
            }
        }
    }

    // A dialog asked about the blocks as they were. A reload while it was open can have given an unnamed subject another
    // block, so the edit would hit a block the user did not choose. The same text means the same blocks.
    private bool IsUnchangedSince(string? content)
    {
        if (string.Equals(content, _file.Content, StringComparison.Ordinal))
        {
            return true;
        }

        _snackbar.Add(ChangedWhileAskingMessage, Severity.Warning);
        return false;
    }

    // A write that the storage cannot make must not end the circuit: a file that another process holds open or that
    // may not be written, a storage that does not answer in time (a TimeoutException) or that is not connected (an
    // InvalidOperationException, the base of the internal refusal of the storage). Null when the edit threw.
    private async Task<MarkdownEditResult?> RunAsync(Func<Task<MarkdownEditResult>> edit, bool reportsRefusal = true)
    {
        MarkdownEditResult result;
        try
        {
            result = await edit();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TimeoutException or InvalidOperationException)
        {
            _logger.Log(
                IsStorageFailure(exception) ? LogLevel.Warning : LogLevel.Error,
                exception,
                "The structure edit of the page {Path} could not be saved",
                _file.FullPath);
            _snackbar.Add($"The page could not be saved. {exception.Message}", Severity.Error);
            return null;
        }

        if (!result.IsWritten)
        {
            if (reportsRefusal)
            {
                _snackbar.Add($"The page was not changed. {result.Refusal}", Severity.Warning);
            }
        }
        else if (result.Subject is { } subject)
        {
            _selection.SelectSubject(subject, _pageComponent);
        }

        return result;
    }

    // The refusal and the timeout of the storage are internal types of its assembly. Any other invalid operation or
    // timeout is a defect, such as an edit called from within a write of the page.
    private static bool IsStorageFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException ||
        exception.GetType().Assembly == typeof(MarkdownFile).Assembly;
}
```

In `MarkdownFilePageComponent.razor`, take the page component and build the editor from the file, the selection and the page component:

```csharp
    [CascadingParameter]
    public WidgetSelection? Selection { get; set; }

    // The widget of the page, inside which the editor finds the widget of a subject to select.
    [CascadingParameter(Name = "ParentSubjectComponent")]
    public SubjectComponent? PageComponent { get; set; }

    private MarkdownPageStructureEditor? _structureEditor;

    private MarkdownFile? File => Subject as MarkdownFile;

    // Inline mode is editing with a selection. The Split preview is not editing, so it never changes the page.
    private PageStructureEditor? StructureEditor
    {
        get
        {
            if (!IsEditing || Selection is not { } selection || PageComponent is not { } pageComponent || File is not { } file)
            {
                return null;
            }

            // A navigation to another page keeps this component and gives it the other file.
            if (_structureEditor is null || _structureEditor.File != file || _structureEditor.Selection != selection ||
                _structureEditor.PageComponent != pageComponent)
            {
                _structureEditor = new MarkdownPageStructureEditor(
                    file, selection, pageComponent, DialogService, Snackbar, SubjectFactory, StructureEditorLogger);
            }

            return _structureEditor;
        }
    }
```


- [ ] **Step 7: Build, run, accept the API snapshot**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: 0 warnings, 0 errors.

Run: `dotnet test src/HomeBlaze/HomeBlaze.Components.Tests`
Expected: `VerifyChecksTests.PublicApi` FAILS (interface became abstract class, `WidgetSelection.SelectSubject`). Accept the `.received.txt`, rerun: PASS.

Run: `dotnet test src/HomeBlaze/HomeBlaze.E2E.Tests --filter "FullyQualifiedName~InlineStructureEditingTests|FullyQualifiedName~WidgetSelectionTests|FullyQualifiedName~LayoutItemActionsTests"`
Expected: PASS, including the two new tests.

- [ ] **Step 8: Commit**

```bash
git add src/HomeBlaze
git commit -m "feat: select the moved or inserted widget and keep the action bar in step with the page"
```

---

### Task 6: The top-level text of every content (`MarkdownTextBlocks`)

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Storage/Pages/MarkdownTextBlocks.cs`
- Test: `src/HomeBlaze/HomeBlaze.Storage.Tests/Pages/MarkdownTextBlocksTests.cs`

**Interfaces:**
- Consumes: `MarkdownPage.Document`, `MarkdownPage.GetBlock(MarkdownBlockContainer)`, `MarkdownPageParser.GetCodeBlockEnd`, `MarkdownSegmentBuilder.IsOldSubjectFence`.
- Produces:
  - `internal sealed record MarkdownTextBlock(Block FirstBlock, int Line, int EndLine, MarkdownBlock? Owner, bool IsEditable)`.
  - `internal static List<MarkdownTextBlock> MarkdownTextBlocks.Find(MarkdownPage page)`: ordered by `Line`.

- [ ] **Step 1: Write the failing tests**

```csharp
using HomeBlaze.Storage.Pages;

namespace HomeBlaze.Storage.Tests.Pages;

public class MarkdownTextBlocksTests
{
    [Fact]
    public void WhenAPageHasTextOfEveryKind_ThenEachTopLevelBlockIsATextBlockWithItsLines()
    {
        // Arrange
        const string source =
            "# Title\n" +
            "\n" +
            "A paragraph\non two lines.\n" +
            "\n" +
            "- one\n- two\n  - nested\n" +
            "\n" +
            "> quote\n" +
            "\n" +
            "```\ncode\n```\n" +
            "\n" +
            "| a | b |\n|---|---|\n| 1 | 2 |\n" +
            "\n" +
            "---\n";
        var page = MarkdownPageParser.Parse(source);

        // Act
        var textBlocks = MarkdownTextBlocks.Find(page);

        // Assert
        Assert.Equal(
            [(1, 1), (3, 4), (6, 8), (10, 10), (12, 14), (16, 18), (20, 20)],
            textBlocks.Select(textBlock => (textBlock.Line, textBlock.EndLine)));
        Assert.All(textBlocks, textBlock => Assert.True(textBlock.IsEditable));
        Assert.All(textBlocks, textBlock => Assert.Null(textBlock.Owner));
    }

    [Fact]
    public void WhenABlockHasContent_ThenItsTextNamesTheBlockAndTheJsonBlockAndTheFrontMatterAreLeftOut()
    {
        // Arrange
        const string source =
            "---\ntitle: T\n---\n\n" +
            ":: Demo.Card\n```json\n{ \"Title\": \"A\" }\n```\nInside.\n" +
            "::: Footer\nFoot.\n:::\n" +
            "::\n";
        var page = MarkdownPageParser.Parse(source);
        var card = page.Blocks[0];

        // Act
        var textBlocks = MarkdownTextBlocks.Find(page);

        // Assert
        Assert.Equal([(9, 9), (11, 11)], textBlocks.Select(textBlock => (textBlock.Line, textBlock.EndLine)));
        Assert.Same(card, textBlocks[0].Owner);
        Assert.Same(card.Children[0], textBlocks[1].Owner);
    }

    [Fact]
    public void WhenARunOfTextHoldsRawHtml_ThenTheRunIsOneTextBlockThatCannotBeEdited()
    {
        // Arrange
        var page = MarkdownPageParser.Parse("Before.\n\n<div>\n\nInside.\n\n</div>\n\n:: Demo.One\n::\n\nAfter.\n");

        // Act
        var textBlocks = MarkdownTextBlocks.Find(page);

        // Assert
        Assert.Equal(
            [(1, 7, false), (12, 12, true)],
            textBlocks.Select(textBlock => (textBlock.Line, textBlock.EndLine, textBlock.IsEditable)));
    }

    [Fact]
    public void WhenAPageHasAStyleBlockAnErrorAndFootnotes_ThenTheyAreTextBlocksThatCannotBeEdited()
    {
        // Arrange: a closing line that closes no block is an error block of one line.
        var page = MarkdownPageParser.Parse(":: {.ab}\nStyled.\n::\n\nText[^1].\n\n::\n\n[^1]: Note.\n");

        // Act
        var textBlocks = MarkdownTextBlocks.Find(page);

        // Assert
        Assert.Equal(
            [(1, 3, false), (5, 5, true), (7, 7, false), (9, 9, false)],
            textBlocks.Select(textBlock => (textBlock.Line, textBlock.EndLine, textBlock.IsEditable)));
    }

    [Fact]
    public void WhenAnIndentedCodeBlockIsFollowedByEmptyLines_ThenItEndsAtItsLastCodeLine()
    {
        // Arrange
        var page = MarkdownPageParser.Parse("Text.\n\n    code\n    more\n\n\n:: Demo.One\n::\n");

        // Act
        var textBlocks = MarkdownTextBlocks.Find(page);

        // Assert
        Assert.Equal([(1, 1), (3, 4)], textBlocks.Select(textBlock => (textBlock.Line, textBlock.EndLine)));
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests --filter "FullyQualifiedName~MarkdownTextBlocksTests"`
Expected: FAIL to compile (`MarkdownTextBlocks` does not exist).

- [ ] **Step 3: Implement**

`src/HomeBlaze/HomeBlaze.Storage/Pages/MarkdownTextBlocks.cs`:

```csharp
using Markdig.Extensions.Footnotes;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;

namespace HomeBlaze.Storage.Pages;

/// <summary>
/// A top-level piece of the content of a page, or of a widget or slot block, that is not a block: a Markdig block of text,
/// such as a paragraph, a heading, a list or a table, or other content, such as raw HTML, a style block or an error.
/// </summary>
/// <param name="FirstBlock">The Markdig block that it starts with.</param>
/// <param name="Line">Its first 1-based line.</param>
/// <param name="EndLine">Its last 1-based line that is not empty.</param>
/// <param name="Owner">The block whose content it is, or null for the page.</param>
/// <param name="IsEditable">
/// Whether it can be edited as text: false for a run of content with raw HTML, a style block, an error, the footnotes, an
/// old subject fence and a Markdig container with a block inside.
/// </param>
internal sealed record MarkdownTextBlock(Block FirstBlock, int Line, int EndLine, MarkdownBlock? Owner, bool IsEditable);

/// <summary>Finds the text blocks of a page, see <see cref="MarkdownTextBlock"/>.</summary>
internal static class MarkdownTextBlocks
{
    /// <summary>
    /// Gets the text blocks of the page and of every widget and slot block, ordered by line. Content that renders nothing,
    /// such as empty lines, link reference definitions and the front matter, has none.
    /// </summary>
    public static List<MarkdownTextBlock> Find(MarkdownPage page)
    {
        var result = new List<MarkdownTextBlock>();
        AddContent(page, page.Document, owner: null, result);
        Visit(page, page.Document, result);
        result.Sort(static (first, second) => first.Line.CompareTo(second.Line));
        return result;
    }

    // Finds the widget and slot blocks at every depth, also inside a list or a style block, and adds their content.
    private static void Visit(MarkdownPage page, ContainerBlock container, List<MarkdownTextBlock> result)
    {
        foreach (var child in container)
        {
            if (child is MarkdownBlockContainer blockContainer &&
                page.GetBlock(blockContainer) is { Kind: MarkdownBlockKind.Widget or MarkdownBlockKind.Slot } block)
            {
                AddContent(page, blockContainer, block, result);
            }

            if (child is ContainerBlock inner)
            {
                Visit(page, inner, result);
            }
        }
    }

    private static void AddContent(MarkdownPage page, ContainerBlock container, MarkdownBlock? owner, List<MarkdownTextBlock> result)
    {
        var run = new List<Block>();

        // The json block of a block is its configuration and is not rendered, as in MarkdownSegmentBuilder.
        var first = owner?.ConfigurationSpan is not null ? 1 : 0;
        for (var index = first; index < container.Count; index++)
        {
            var child = container[index];
            if (child is MarkdownBlockContainer blockContainer &&
                page.GetBlock(blockContainer).Kind is MarkdownBlockKind.Widget or MarkdownBlockKind.Slot)
            {
                AddRun(page, run, owner, result);
                run.Clear();
            }
            else if (child is not (BlankLineBlock or LinkReferenceDefinitionGroup or YamlFrontMatterBlock))
            {
                run.Add(child);
            }
        }

        AddRun(page, run, owner, result);
    }

    // Raw HTML can open an element in one block and close it in a later one, so a run with raw HTML stays one piece.
    private static void AddRun(MarkdownPage page, List<Block> run, MarkdownBlock? owner, List<MarkdownTextBlock> result)
    {
        if (run.Count == 0)
        {
            return;
        }

        if (run.Exists(static block => block is HtmlBlock))
        {
            result.Add(new MarkdownTextBlock(run[0], GetLine(run[0]), GetEndLine(page, run[^1]), owner, IsEditable: false));
            return;
        }

        foreach (var block in run)
        {
            result.Add(new MarkdownTextBlock(block, GetLine(block), GetEndLine(page, block), owner, IsEditable(page, block)));
        }
    }

    private static bool IsEditable(MarkdownPage page, Block block) =>
        block is not (MarkdownBlockContainer or FootnoteGroup) &&
        !MarkdownSegmentBuilder.IsOldSubjectFence(block) &&
        !(block is ContainerBlock container && HoldsBlock(container));

    private static bool HoldsBlock(ContainerBlock container)
    {
        foreach (var child in container)
        {
            if (child is MarkdownBlockContainer || (child is ContainerBlock inner && HoldsBlock(inner)))
            {
                return true;
            }
        }

        return false;
    }

    // The group of the footnotes has no line of its own, so its footnotes give its lines.
    private static int GetLine(Block block) =>
        (block is FootnoteGroup { Count: > 0 } group ? group[0] : block).Line + 1;

    // The line of the last character that is not white space: the span of an indented code block takes the empty lines
    // after it, and a block of the page knows its own last line, also without a closing line.
    private static int GetEndLine(MarkdownPage page, Block block)
    {
        if (block is FootnoteGroup { Count: > 0 } group)
        {
            block = group[group.Count - 1];
        }

        if (block is MarkdownBlockContainer blockContainer)
        {
            return page.GetBlock(blockContainer).EndLine;
        }

        var source = page.Source;
        var end = Math.Clamp(block is CodeBlock code ? MarkdownPageParser.GetCodeBlockEnd(source, code) : block.Span.End + 1, 0, source.Length);
        var start = Math.Clamp(block.Span.Start, 0, end);
        var lastCharacter = start + source.AsSpan(start, end - start).TrimEnd().Length - 1;
        return source.AsSpan(0, Math.Max(lastCharacter, 0)).Count('\n') + 1;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests --filter "FullyQualifiedName~MarkdownTextBlocksTests"`
Expected: PASS. If Markdig reports a line or span differently than a test expects (for example for the footnote group or the table), check the source lines by hand first: the test states the contract (first line, last non-empty line), so fix `GetLine` or `GetEndLine` rather than the expectation.

- [ ] **Step 5: Commit**

```bash
git add src/HomeBlaze
git commit -m "feat: find the top-level text of every content of a page"
```

---

### Task 7: Move a block by cutting it and inserting it at the next insert point

> Note: the `isSkipped` parameter of the move was later removed in Task 8; a chip is an item like any other, so a move passes it one step per chip.

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.Storage/Pages/MarkdownBlockWriter.cs:235-241` (and new helpers)
- Modify: `src/HomeBlaze/HomeBlaze.Storage/Pages/MarkdownPageOperations.cs:41-134,233-246`
- Modify: `src/HomeBlaze/HomeBlaze.Storage/Files/MarkdownFile.cs` (`MoveBlockAsync`, `GetBlockActions`)
- Modify: `src/HomeBlaze/HomeBlaze.Storage/Pages/MarkdownBlockActions.cs` (doc)
- Modify: `src/HomeBlaze/HomeBlaze.Storage.Blazor/Files/MarkdownPageStructureEditor.cs`, `src/HomeBlaze/HomeBlaze.Storage.Blazor/Files/MarkdownFilePageComponent.razor`
- Modify: `src/HomeBlaze/HomeBlaze.Storage.Tests/VerifyChecksTests.PublicApi.verified.txt`
- Test: `src/HomeBlaze/HomeBlaze.Storage.Tests/Pages/MarkdownPageOperationsTests.cs`, `Pages/MarkdownBlockWriterTests.cs`, `Pages/MarkdownStructureEditTests.cs`, `src/HomeBlaze/HomeBlaze.E2E.Tests/InlineStructureEditingTests.cs`

**Interfaces:**
- Consumes: `MarkdownTextBlocks.Find` (Task 6), `MarkdownStructureChange.ResultBlockIndex` (Task 4).
- Produces:
  - `internal static string MarkdownBlockWriter.GetLines(string source, int line, int endLine)` (replaces `GetBlockText`).
  - `internal static string MarkdownBlockWriter.CutLines(string source, int line, int endLine, out MarkdownLineEdit edit)`.
  - `internal static bool MarkdownBlockWriter.HasEmptyLineAround(string source, int line, int endLine)`.
  - `internal static string MarkdownBlockWriter.InsertMovedLines(string source, int line, string text, string lineEnding, bool standsApart, out MarkdownLineEdit edit)`.
  - `public static MarkdownStructureChange? MarkdownPageOperations.TryMove(MarkdownPage page, IReadOnlyDictionary<int, IInterceptorSubject> blockSubjects, MarkdownBlock block, int direction, Func<MarkdownBlock, bool> isSkipped, out string? refusal)`.
  - `public static int? MarkdownPageOperations.FindMoveLine(MarkdownPage page, MarkdownBlock block, int direction, Func<MarkdownBlock, bool> isSkipped)` (replaces `FindMoveTarget`).
  - `public Task<MarkdownEditResult> MarkdownFile.MoveBlockAsync(IInterceptorSubject subject, int direction, Func<IInterceptorSubject, bool>? isSkipped, CancellationToken cancellationToken)`.
  - `public MarkdownBlockActions MarkdownFile.GetBlockActions(IInterceptorSubject subject, Func<IInterceptorSubject, bool>? isSkipped = null)`.
  - `MarkdownPageStructureEditor(MarkdownFile file, WidgetSelection selection, SubjectComponent pageComponent, IDialogService dialogService, ISnackbar snackbar, SubjectFactory subjectFactory, SubjectComponentRegistry componentRegistry, ILogger logger)`.

- [ ] **Step 1: Write the failing tests**

`MarkdownBlockWriterTests`: replace `WhenTheTextOfABlockIsAskedFor_ThenItHasItsLinesWithoutTheLastLineEnding` with:

```csharp
    [Fact]
    public void WhenLinesAreAskedFor_ThenTheyComeWithoutTheLastLineEnding()
    {
        // Arrange
        const string source = "intro\r\n\r\n:: Demo.Card\r\n::: Row Label=A\r\n:::\r\n::\r\n\r\noutro\r\n";

        // Act
        var text = MarkdownBlockWriter.GetLines(source, 3, 6);

        // Assert
        Assert.Equal(":: Demo.Card\r\n::: Row Label=A\r\n:::\r\n::", text);
    }

    [Theory]
    [InlineData("a\n\nB\n\nc\n", 3, 3, "a\n\nc\n")]
    [InlineData("a\n\nB\n", 3, 3, "a\n")]
    [InlineData("B\n\nc\n", 1, 1, "c\n")]
    [InlineData("a\nB\nc\n", 2, 2, "a\n\nc\n")]
    [InlineData(":: X\nB\n::\n", 2, 2, ":: X\n::\n")]
    public void WhenLinesAreCut_ThenTheTextAroundThemStaysApartAsBefore(string source, int line, int endLine, string expected)
    {
        // Act
        var result = MarkdownBlockWriter.CutLines(source, line, endLine, out _);

        // Assert
        Assert.Equal(expected, result);
    }
```

`MarkdownPageOperationsTests`: add a predicate and replace the move tests. Add at the top of the class:

```csharp
    private static readonly Func<MarkdownBlock, bool> NoSkip = static _ => false;
```

Change every `MarkdownPageOperations.TryMove(page, subjects, block, direction, out ...)` call to `TryMove(page, subjects, block, direction, NoSkip, out ...)`. Then change these tests:

```csharp
    [Fact]
    public void WhenAWidgetIsMovedUpPastText_ThenItGoesBeforeTheTextAndTheTextStays()
    {
        // Arrange
        const string source = "a\n\n:: Demo.One\n::\n\ntext\n\n:: Demo.Two\n::\n";
        var page = MarkdownPageParser.Parse(source);

        // Act
        var change = MarkdownPageOperations.TryMove(page, NoSubjects, page.Blocks[1], -1, NoSkip, out var refusal);

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
        var change = MarkdownPageOperations.TryMove(page, NoSubjects, page.Blocks[0].Children[rowPosition], direction, NoSkip, out var refusal);

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
        var change = MarkdownPageOperations.TryMove(page, NoSubjects, page.Blocks[0].Children[0], 1, NoSkip, out _);

        // Assert
        Assert.Null(change);
        Assert.Null(MarkdownPageOperations.FindMoveLine(page, page.Blocks[0].Children[0], 1, NoSkip));
    }

    [Fact]
    public void WhenARowWithoutClosingLineIsMovedPastARow_ThenTheTextAfterTheOtherRowStaysThere()
    {
        // Arrange
        var page = MarkdownPageParser.Parse(":: Demo.Card\n::: Row Label=A\n:::\nbetween\n::: Row Label=B\nsecond\n::\n");

        // Act
        var change = MarkdownPageOperations.TryMove(page, NoSubjects, page.Blocks[0].Children[1], -1, NoSkip, out var refusal);

        // Assert
        Assert.Null(refusal);
        Assert.Equal(":: Demo.Card\n::: Row Label=B\nsecond\n::: Row Label=A\n:::\nbetween\n::\n", change!.Write.Page.Source);
    }

    [Fact]
    public void WhenAWidgetIsMovedDownPastASkippedWidget_ThenItPassesItAndTheNextWidget()
    {
        // Arrange
        var page = MarkdownPageParser.Parse("intro\n\n:: Demo.One\n::\n\n:: Demo.Map\n::\n\n:: Demo.Two\n::\n");

        // Act
        var change = MarkdownPageOperations.TryMove(
            page, NoSubjects, page.Blocks[0], 1, block => block.Head!.Name == "Demo.Map", out var refusal);

        // Assert
        Assert.Null(refusal);
        Assert.Equal("intro\n\n:: Demo.Map\n::\n\n:: Demo.Two\n::\n\n:: Demo.One\n::\n", change!.Write.Page.Source);
        Assert.Equal(change.Write.Page.Blocks[2].Index, change.ResultBlockIndex);
    }

    [Fact]
    public void WhenAWidgetIsMovedUpPastAWidget_ThenItStandsBeforeItWithAnEmptyLineBetween()
    {
        // Arrange
        var page = MarkdownPageParser.Parse("a\n\n:: Demo.One\n::\n\n:: Demo.Two\n::\n");

        // Act
        var change = MarkdownPageOperations.TryMove(page, NoSubjects, page.Blocks[1], -1, NoSkip, out var refusal);

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
        Assert.Equal(1, MarkdownPageOperations.FindMoveLine(page, page.Blocks[0], -1, NoSkip));
        Assert.Null(MarkdownPageOperations.FindMoveLine(page, page.Blocks[0], 1, NoSkip));
    }

    [Fact]
    public void WhenThePageHasALoneCarriageReturn_ThenMovingIsRefused()
    {
        // Arrange
        var page = MarkdownPageParser.Parse("a\r:: Demo.One\n::\n\n:: Demo.Two\n::\n");

        // Act
        var change = MarkdownPageOperations.TryMove(page, NoSubjects, page.Blocks[1], -1, NoSkip, out var refusal);

        // Assert
        Assert.Null(change);
        Assert.Contains("carriage return", refusal);
    }
```

Delete `WhenTheNeighborIsOfAnotherSlot_ThenTheMoveIsRefused`, `WhenThereIsNoSiblingInTheDirection_ThenTheMoveIsRefused` and `WhenTheTextBetweenTwoBlocksWouldGoToABlockWithoutClosingLine_ThenTheMoveIsRefused`, which the tests above replace. Keep `WhenARowIsMovedDown_ThenItSwapsWithTheNextRowAndKeepsItsSubject` (rename it to `WhenARowIsMovedDown_ThenItGoesAfterTheNextRowAndKeepsItsSubject`), `WhenTheLastBlockEndsTheTextWithoutALineEnding_ThenTheMoveKeepsTheTextEndingWithout`, `WhenRowsHaveNoClosingLines_ThenTheMoveSwapsTheirContent`, `WhenWindowsLineEndingsAreUsed_ThenTheMoveKeepsThem` and `WhenAWidgetWithASlotChildIsMovedPastAnotherWidget_ThenEverySubjectFollowsItsBlock` with their expected texts: the cut and insert gives the same text for them.

`MarkdownStructureEditTests`: change `MoveBlockAsync(x, 1, CancellationToken.None)` calls to `MoveBlockAsync(x, 1, isSkipped: null, CancellationToken.None)`, and rename and change the actions test:

```csharp
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
```

E2E, add to `InlineStructureEditingTests`:

```csharp
    [Fact]
    public async Task WhenAWidgetIsMovedDownTwice_ThenItPassesTwoItemsAndStaysSelected()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();
        var actionBar = await GetWrapperOf(page, "switch").SelectWidgetAsync();

        // Act: the second move passes the map, which shows nothing on the viewed page, together with the Responsive block.
        await actionBar.GetByTestId("move-down-button").ClickAsync();
        await _fixture.WaitForDemoPageTextAsync(
            PageName, text => IndexOf(text, "ValueList") < IndexOf(text, "Switch"), "The switch was not moved below the list.");
        await page.GetWidgetActionBar().GetByTestId("move-down-button").ClickAsync();

        // Assert
        await _fixture.WaitForDemoPageTextAsync(
            PageName,
            text => IndexOf(text, "ValueMap#structuremap") < IndexOf(text, "Responsive") && IndexOf(text, "Responsive") < IndexOf(text, "Switch"),
            "The switch was not moved below the Responsive block.");
        await Assertions.Expect(GetWrapperOf(page, "switch")).ToHaveClassAsync(new Regex(@"\bselected\b"), new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenAListIsMovedDownBeforeAValueMap_ThenItPassesTheMapAndTheNextBlock()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();
        var actionBar = await GetValueListWrapper(page).SelectWidgetAsync();

        // Act
        await actionBar.GetByTestId("move-down-button").ClickAsync();

        // Assert
        await _fixture.WaitForDemoPageTextAsync(
            PageName,
            text => IndexOf(text, "ValueMap#structuremap") < IndexOf(text, "Responsive") && IndexOf(text, "Responsive") < IndexOf(text, "ValueList"),
            "The list did not pass the map and the Responsive block.");
    }

    private static int IndexOf(string text, string typeName) =>
        text.IndexOf("HomeBlaze.Components." + typeName, StringComparison.Ordinal);
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests --filter "FullyQualifiedName~MarkdownPageOperationsTests|FullyQualifiedName~MarkdownBlockWriterTests|FullyQualifiedName~MarkdownStructureEditTests"`
Expected: FAIL to compile (`GetLines`, `CutLines`, `FindMoveLine`, the new `TryMove` and `MoveBlockAsync` signatures).

- [ ] **Step 3: The line helpers**

In `MarkdownBlockWriter.cs`, replace `GetBlockText` with:

```csharp
    /// <summary>Gets the lines from <paramref name="line"/> to <paramref name="endLine"/>, without the line ending of the last one.</summary>
    internal static string GetLines(string source, int line, int endLine)
    {
        var start = GetLineStart(source, line);
        var end = GetLineStart(source, endLine + 1);
        return source[start..end].TrimEnd('\n').TrimEnd('\r');
    }

    /// <summary>Gets whether an empty line stands right before or right after the lines, as between blocks that stand apart.</summary>
    internal static bool HasEmptyLineAround(string source, int line, int endLine)
    {
        var start = GetLineStart(source, line);
        var end = GetLineStart(source, endLine + 1);
        return (line > 1 && IsEmptyLine(source, GetLineStart(source, line - 1), start)) ||
               (end < source.Length && IsEmptyLine(source, end, GetLineStart(source, endLine + 2)));
    }

    /// <summary>
    /// Removes the lines from <paramref name="line"/> to <paramref name="endLine"/> so that the text around them stays apart
    /// as before. Between two lines of text one empty line stays (see <see cref="RemoveBlock"/>). Of the empty lines around
    /// them, the one after goes with them when the one before is empty too or they start the text, and the one before when
    /// they end the text.
    /// </summary>
    /// <remarks><paramref name="source"/> must have no lone carriage return, see <see cref="HasLoneCarriageReturn"/>.</remarks>
    internal static string CutLines(string source, int line, int endLine, out MarkdownLineEdit edit)
    {
        var start = GetLineStart(source, line);
        var end = GetLineStart(source, endLine + 1);
        var previousStart = GetLineStart(source, line - 1);
        var nextEnd = GetLineStart(source, endLine + 2);

        if (line > 1 && IsTextLine(source, previousStart, start) && IsTextLine(source, end, nextEnd))
        {
            edit = new MarkdownLineEdit(line, endLine - line + 1, 1);
            return source.Remove(start, end - start).Insert(start, GetLineEnding(source, start));
        }

        var isEmptyBefore = line > 1 && IsEmptyLine(source, previousStart, start);
        var isEmptyAfter = end < source.Length && IsEmptyLine(source, end, nextEnd);
        if (isEmptyAfter && (isEmptyBefore || line == 1))
        {
            end = nextEnd;
            endLine++;
        }
        else if (isEmptyBefore && end == source.Length)
        {
            start = previousStart;
            line--;
        }

        edit = new MarkdownLineEdit(line, endLine - line + 1, 0);
        return source.Remove(start, end - start);
    }

    /// <summary>
    /// Inserts <paramref name="text"/>, lines that <see cref="CutLines"/> cut, before the 1-based <paramref name="line"/>. An
    /// empty line separates them from a line before or after them that is not empty when they stood apart before
    /// (<paramref name="standsApart"/>) or that line is text, which they would join otherwise.
    /// </summary>
    /// <remarks><paramref name="source"/> must have no lone carriage return, see <see cref="HasLoneCarriageReturn"/>.</remarks>
    internal static string InsertMovedLines(
        string source, int line, string text, string lineEnding, bool standsApart, out MarkdownLineEdit edit)
    {
        var lineStart = GetLineStart(source, line);
        var nextLineStart = GetLineStart(source, line + 1);
        if (line > 1)
        {
            var previousLineStart = GetLineStart(source, line - 1);
            if (!IsEmptyLine(source, previousLineStart, lineStart) && (standsApart || IsTextLine(source, previousLineStart, lineStart)))
            {
                text = lineEnding + text;
            }
        }

        if (!IsEmptyLine(source, lineStart, nextLineStart) && (standsApart || IsTextLine(source, lineStart, nextLineStart)))
        {
            text += lineEnding;
        }

        return InsertLines(source, line, text, lineEnding, out edit);
    }
```

- [ ] **Step 4: The move**

In `MarkdownPageOperations.cs`, replace `TryMove` and `FindMoveTarget`, and remove `EndsAt`:

```csharp
    /// <summary>
    /// Moves the block one place in <paramref name="direction"/> (-1 for up, 1 for down): it is cut out and inserted before the
    /// item before it, or after the item after it. An item is a widget block or a text block of the content of the block, or
    /// for a slot block a block of the same slot. A block for which <paramref name="isSkipped"/> is true is passed over. The
    /// text around the block stays where it is.
    /// </summary>
    /// <returns>Null with a refusal when there is no item in that direction or the change would change other blocks of the page.</returns>
    public static MarkdownStructureChange? TryMove(
        MarkdownPage page,
        IReadOnlyDictionary<int, IInterceptorSubject> blockSubjects,
        MarkdownBlock block,
        int direction,
        Func<MarkdownBlock, bool> isSkipped,
        out string? refusal)
    {
        if (FindMoveLine(page, block, direction, isSkipped) is not { } targetLine)
        {
            refusal = $"The block at line {block.Line} has nothing {(direction < 0 ? "before" : "after")} it to move past.";
            return null;
        }

        if (!CanInsertOrRemoveBlocks(page, out refusal))
        {
            return null;
        }

        FindSiblings(page, block, out var parent);
        var source = page.Source;
        var lineEnding = MarkdownBlockWriter.GetLineEnding(source, block.HeadSpan.End);
        var blockText = MarkdownBlockWriter.GetLines(source, block.Line, block.EndLine);
        var standsApart = MarkdownBlockWriter.HasEmptyLineAround(source, block.Line, block.EndLine);

        // Each step is aligned on its own, so that together they change nothing outside the cut and the inserted lines.
        var cutSource = MarkdownBlockWriter.CutLines(source, block.Line, block.EndLine, out var cut);
        var cutPage = MarkdownPageParser.Parse(cutSource);
        var cutMap = MarkdownBlockAlignment.TryAlign(page, cutPage, cut, expectedInsertedBlockCount: 0, owner: null);

        var line = targetLine >= cut.RemovedEnd ? targetLine - cut.RemovedLineCount + cut.InsertedLineCount : targetLine;
        var newSource = MarkdownBlockWriter.InsertMovedLines(cutSource, line, blockText, lineEnding, standsApart, out var insert);
        var after = MarkdownPageParser.Parse(newSource);
        var cutParent = parent is null ? null : FindBlockAt(cutPage, parent.Line);
        var insertMap = cutMap is null
            ? null
            : MarkdownBlockAlignment.TryAlign(cutPage, after, insert, CountBlocks(block), cutParent, block.Kind);
        if (cutMap is null || insertMap is null || FindFirstBlockFrom(after, insert) is not { } moved)
        {
            refusal = $"Moving the block at line {block.Line} would change other blocks of the page.";
            return null;
        }

        var subjects = new Dictionary<int, IInterceptorSubject>(blockSubjects.Count);
        foreach (var (index, subject) in blockSubjects)
        {
            var cutIndex = cutMap[index];
            if (cutIndex >= 0 && insertMap[cutIndex] >= 0)
            {
                subjects[insertMap[cutIndex]] = subject;
            }
        }

        // The moved blocks are new to the alignment. Their text is the same, so their blocks are in the same order.
        CarrySubjects(blockSubjects, block, moved, subjects);
        return new MarkdownStructureChange(
            new MarkdownBlockWrite(after, subjects, [], HasInsertedOrRemovedBlocks: true), parent, moved.Index);
    }

    /// <summary>
    /// Gets the line of the page before the move before which <see cref="TryMove"/> inserts the block: the first line of the
    /// item before it, or the line after the item after it. Null when the block has no such item.
    /// </summary>
    public static int? FindMoveLine(MarkdownPage page, MarkdownBlock block, int direction, Func<MarkdownBlock, bool> isSkipped)
    {
        if (direction is not (-1 or 1) ||
            block.Kind is not (MarkdownBlockKind.Widget or MarkdownBlockKind.Slot) ||
            FindSiblings(page, block, out var parent) is not { } siblings)
        {
            return null;
        }

        var items = block.Kind == MarkdownBlockKind.Slot ? GetSlotItems(siblings, block.Head!.Name) : GetContentItems(page, siblings, parent);
        var position = items.FindIndex(item => ReferenceEquals(item.Block, block));
        for (var index = position + direction; index >= 0 && index < items.Count; index += direction)
        {
            var item = items[index];
            if (item.Block is not { } neighbor || !isSkipped(neighbor))
            {
                return direction < 0 ? item.Line : item.EndLine + 1;
            }
        }

        return null;
    }

    // A place that a moved block passes: a block, or text without a block, with its lines.
    private readonly record struct MoveItem(int Line, int EndLine, MarkdownBlock? Block);

    private static List<MoveItem> GetSlotItems(IReadOnlyList<MarkdownBlock> siblings, string slotName)
    {
        var items = new List<MoveItem>();
        foreach (var sibling in siblings)
        {
            if (sibling.Kind == MarkdownBlockKind.Slot && sibling.Head!.Name == slotName)
            {
                items.Add(new MoveItem(sibling.Line, sibling.EndLine, sibling));
            }
        }

        return items;
    }

    // The widget blocks of the content and its text blocks, which also stand for its style and error blocks.
    private static List<MoveItem> GetContentItems(MarkdownPage page, IReadOnlyList<MarkdownBlock> siblings, MarkdownBlock? owner)
    {
        var items = new List<MoveItem>();
        foreach (var sibling in siblings)
        {
            if (sibling.Kind == MarkdownBlockKind.Widget)
            {
                items.Add(new MoveItem(sibling.Line, sibling.EndLine, sibling));
            }
        }

        foreach (var textBlock in MarkdownTextBlocks.Find(page))
        {
            if (ReferenceEquals(textBlock.Owner, owner))
            {
                items.Add(new MoveItem(textBlock.Line, textBlock.EndLine, null));
            }
        }

        items.Sort(static (first, second) => first.Line.CompareTo(second.Line));
        return items;
    }
```

`FindBlockAt` stays (it finds the parent in the cut page, and a parent always starts before the cut). `FindSiblings` returns a style block as parent for a widget inside a style block, whose content has no text blocks; its widgets then move among themselves.

- [ ] **Step 5: The file and the editor**

In `MarkdownFile.cs`:

```csharp
    /// <summary>
    /// Moves the block of <paramref name="subject"/> one place up (-1) or down (1) among the content around it: before the
    /// item before it, or after the item after it, where an item is a widget block or a top-level block of text of that
    /// content, or for a slot block a block of the same slot. A block whose subject <paramref name="isSkipped"/> is true
    /// for, such as a widget that renders nothing, is passed over. Saves the page.
    /// Refused when the file differs from the text the page last read; the page then reads it.
    /// </summary>
    /// <exception cref="InvalidOperationException">The call comes from work on the worker of the storage or from within a write of this page.</exception>
    public Task<MarkdownEditResult> MoveBlockAsync(
        IInterceptorSubject subject, int direction, Func<IInterceptorSubject, bool>? isSkipped, CancellationToken cancellationToken) =>
        EditStructureAsync(
            snapshot => FindOwnBlock(snapshot.Page!, snapshot.BlockSubjects, subject) is { } block
                ? (MarkdownPageOperations.TryMove(
                    snapshot.Page!, snapshot.BlockSubjects, block, direction, ToBlockPredicate(snapshot.BlockSubjects, isSkipped), out var refusal), refusal)
                : (null, NoBlockRefusal),
            cancellationToken);
```

```csharp
    // A block is skipped when it has a subject that the predicate skips.
    private static Func<MarkdownBlock, bool> ToBlockPredicate(
        IReadOnlyDictionary<int, IInterceptorSubject> blockSubjects, Func<IInterceptorSubject, bool>? isSkipped) =>
        isSkipped is null
            ? static _ => false
            : block => blockSubjects.TryGetValue(block.Index, out var subject) && isSkipped(subject);
```

`GetBlockActions` gets the parameter `Func<IInterceptorSubject, bool>? isSkipped = null` (document it like `MoveBlockAsync`) and computes:

```csharp
        var isBlockSkipped = ToBlockPredicate(snapshot.BlockSubjects, isSkipped);
        return new MarkdownBlockActions(
            CanDelete: true,
            CanMoveUp: MarkdownPageOperations.FindMoveLine(page, block, -1, isBlockSkipped) is not null,
            CanMoveDown: MarkdownPageOperations.FindMoveLine(page, block, 1, isBlockSkipped) is not null,
            ContainedBlockCount: MarkdownPageOperations.CountBlocks(block) - 1);
```

In `MarkdownBlockActions.cs` change the docs of `CanMoveUp` and `CanMoveDown` to "Whether the block can move past the item before it." and "...after it.".

In `MarkdownPageStructureEditor.cs`, add the registry and the predicate (with `using HomeBlaze.Components.Abstractions.Attributes;` and `using HomeBlaze.Services.Components;`):

```csharp
    private readonly SubjectComponentRegistry _componentRegistry;

    // The widgets that render nothing on a viewed page, which a move passes over. Created once, as a method group would
    // allocate a delegate on every call.
    private readonly Func<IInterceptorSubject, bool> _isInvisible;
```

The constructor takes `SubjectComponentRegistry componentRegistry` after `SubjectFactory subjectFactory` and sets `_componentRegistry = componentRegistry;` and `_isInvisible = IsInvisible;`. In `MarkdownFilePageComponent.razor`, add `@using HomeBlaze.Services.Components` and `@inject SubjectComponentRegistry ComponentRegistry`, and pass `ComponentRegistry` after `SubjectFactory` to the constructor. Then:

```csharp
    public override PageBlockActions GetActions(IInterceptorSubject subject)
    {
        var actions = _file.GetBlockActions(subject, _isInvisible);
        return new PageBlockActions(actions.CanDelete, actions.CanMoveUp, actions.CanMoveDown);
    }

    public override async Task MoveAsync(IInterceptorSubject subject, int direction) =>
        await RunAsync(() => _file.MoveBlockAsync(subject, direction, _isInvisible, CancellationToken.None));

    private bool IsInvisible(IInterceptorSubject subject) =>
        _componentRegistry.GetComponent(subject.GetType(), SubjectComponentType.Widget)?.ComponentType is { } componentType &&
        typeof(IInvisibleWidget).IsAssignableFrom(componentType);
```

- [ ] **Step 6: Run the tests and accept the API snapshot**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests`
Expected: PASS after accepting `VerifyChecksTests.PublicApi` (`FindMoveLine` is internal to a static internal class, so only `MoveBlockAsync` and `GetBlockActions` change in the snapshot).

Run: `dotnet test src/HomeBlaze/HomeBlaze.E2E.Tests --filter "FullyQualifiedName~InlineStructureEditingTests"`
Expected: PASS, including the two new tests. `WhenABlockIsMovedDown_ThenItSwapsWithTheNextBlockInTheFile` still holds (the switch passes the list); rename it to `WhenABlockIsMovedDown_ThenItGoesAfterTheNextBlockInTheFile`.

- [ ] **Step 7: Commit**

```bash
git add src/HomeBlaze
git commit -m "feat: move a block by cutting it and inserting it past the next visible item"
```

---

### Task 8: Show the chips of invisible widgets where their blocks are

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.Storage.Blazor/Files/MarkdownContentView.razor:11,14-47,209-225`
- Modify: `src/HomeBlaze/HomeBlaze.Storage.Blazor/Files/MarkdownFilePageComponent.razor.css:17-21,100-127`
- Modify: `src/HomeBlaze/HomeBlaze.Components/SubjectComponent.razor.css:239-255`
- Modify: `src/HomeBlaze/HomeBlaze.Components/IInvisibleWidget.cs`
- Test: `src/HomeBlaze/HomeBlaze.E2E.Tests/InlineStructureEditingTests.cs:261-304`

**Interfaces:**
- Produces: `MarkdownContentView` renders every widget in its place in every mode; nothing outside it knows the chip stack any more.

- [ ] **Step 1: Write the failing E2E test**

Replace `WhenThePageIsEditedInline_ThenTheChipCoversNoContent` with:

```csharp
    [Fact]
    public async Task WhenThePageIsEditedInline_ThenTheValueMapChipStandsWhereItsBlockIs()
    {
        // Arrange & Act
        var page = await OpenPageInInlineModeAsync();
        var chip = page.GetByTestId("invisible-widget-chip").Filter(new() { HasText = "structuremap" });
        await Assertions.Expect(chip).ToBeVisibleAsync(new() { Timeout = Timeout });

        // Assert: the map stands between the list and the Responsive block on the page, and so does its chip.
        var list = await PageGeometry.GetBoxAsync(GetValueListWrapper(page));
        var map = await PageGeometry.GetBoxAsync(chip);
        var responsive = await PageGeometry.GetBoxAsync(page.GetByTestId("responsive-chip"));
        Assert.True(list.Bottom <= map.Top, $"The chip at {map.Top} is not below the list, which ends at {list.Bottom}.");
        Assert.True(map.Bottom <= responsive.Top, $"The chip, which ends at {map.Bottom}, is not above the Responsive chip at {responsive.Top}.");
    }
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test src/HomeBlaze/HomeBlaze.E2E.Tests --filter "FullyQualifiedName~TheValueMapChipStandsWhereItsBlockIs"`
Expected: FAIL: the chip floats at the top right, above the list.

- [ ] **Step 3: Render invisible widgets in place**

In `MarkdownContentView.razor`:
- Delete the `@if (isEditingInline && Children is not null && HasInvisibleWidget()) { ... invisible-widget-stack ... }` block and the variable `isEditingInline`.
- Change `else if (segment.Widget != null && !(isEditingInline && IsInvisibleWidget(segment.Widget)))` to `else if (segment.Widget != null)`.
- Delete `HasInvisibleWidget` and `IsInvisibleWidget`, the `@inject SubjectComponentRegistry ComponentRegistry` line and `@using HomeBlaze.Services.Components` if nothing else uses them.

In `MarkdownFilePageComponent.razor.css`:
- Delete the rules from the comment "The widgets that show nothing, such as a ValueMap, stand as chips..." to the end of the file (the `.invisible-widget-stack` rules, the `flow-root` rule and the container rule).
- Change the comment of the `:empty` rule to: "A widget that shows nothing, such as a ValueMap on a viewed page, takes no space (see SubjectComponent.razor.css of HomeBlaze.Components). Edited inline, it shows a chip in its place."

In `SubjectComponent.razor.css`, the case list of a map now opens below a chip at the left of its line: in `::deep .value-map-cases` replace `right: 0;` with `left: 0;` and change the comment to "The cases of a selected value map overlay the content below its chip, so that the page does not move when they open. The wrapper of the map is the containing block."

`IInvisibleWidget.cs`:

```csharp
namespace HomeBlaze.Components;

/// <summary>
/// Marks the widget component of a type that shows nothing on a viewed page, such as a value map. While the page is edited
/// inline, such a widget shows a chip in its place, and a move of another block passes over it.
/// </summary>
public interface IInvisibleWidget;
```

- [ ] **Step 4: Run the tests**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: 0 warnings.

Run: `dotnet test src/HomeBlaze/HomeBlaze.E2E.Tests --filter "FullyQualifiedName~InlineStructureEditingTests"`
Expected: PASS, including `WhenAValueMapChipIsSelected_ThenItsCasesAreListedAndACaseCanBeAdded` and `WhenThePageIsViewed_ThenNoChipIsShown`.

- [ ] **Step 5: Commit**

```bash
git add src/HomeBlaze
git commit -m "feat: show the chip of a widget that renders nothing where its block is"
```

---

### Task 9: Insert lines that take no room, with names of their own

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.Components/Components/BlockInsertLine.razor`
- Modify: `src/HomeBlaze/HomeBlaze.Components/SubjectComponent.razor:264` (`GetAccessibleName` becomes public)
- Modify: `src/HomeBlaze/HomeBlaze.Components/SubjectComponent.razor.css:257-281`
- Modify: `src/HomeBlaze/HomeBlaze.Storage.Blazor/Files/MarkdownContentView.razor`
- Modify: `src/HomeBlaze/HomeBlaze.Storage.Blazor/Files/MarkdownFilePageComponent.razor.css` (card footer rule)
- Modify: `src/HomeBlaze/HomeBlaze.Components.Tests/VerifyChecksTests.PublicApi.verified.txt`
- Test: `src/HomeBlaze/HomeBlaze.E2E.Tests/InlineStructureEditingTests.cs`

**Interfaces:**
- Produces: `BlockInsertLine` parameters `OnInsert` (EventCallback) and `Label` (string, required); `public string SubjectComponent.GetAccessibleName()`.

- [ ] **Step 1: Write the failing E2E tests**

```csharp
    [Fact]
    public async Task WhenACardIsSelected_ThenNoBlockOfThePageMoves()
    {
        // Arrange: the card has text, a widget and a footer, whose lines show only while the card is selected.
        var content = Content
            + "\n"
            + ":: HomeBlaze.Components.Card Title=\"Layout Card\"\n"
            + "Card text.\n"
            + "\n"
            + "::: HomeBlaze.Components.Progress Path=structuremotor/TargetSpeed Minimum=0 Maximum=1600\n"
            + ":::\n"
            + "::: Footer\n"
            + ":::: HomeBlaze.Components.Switch Path=structuremotor/IsPowered\n"
            + "::::\n"
            + ":::\n"
            + "::\n";
        var page = await OpenPageInInlineModeAsync(content);
        var card = page.GetCard("Layout Card");
        await Assertions.Expect(card).ToBeVisibleAsync(new() { Timeout = Timeout });
        var before = await GetBlockBoxesAsync(page);

        // Act
        await card.Locator(".mud-card-header").SelectWidgetAsync();
        await Assertions.Expect(card.Locator("[data-testid='block-insert-line']").First).ToBeVisibleAsync(new() { Timeout = Timeout });

        // Assert
        var after = await GetBlockBoxesAsync(page);
        Assert.Equal(before.Length, after.Length);
        for (var index = 0; index < before.Length; index++)
        {
            Assert.InRange(after[index], before[index] - 0.5, before[index] + 0.5);
        }
    }

    [Fact]
    public async Task WhenInsertLinesAreShown_ThenEachHasANameOfItsOwn()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();
        var buttons = page.Locator("[data-testid='block-insert-line'] button");
        await Assertions.Expect(buttons.First).ToBeVisibleAsync(new() { Timeout = Timeout });

        // Act
        var labels = await buttons.EvaluateAllAsync<string[]>("buttons => buttons.map(button => button.getAttribute('aria-label'))");

        // Assert
        Assert.Equal(labels.Length, labels.Distinct().Count());
        Assert.Contains("Insert at the end of the page", labels);
        Assert.Contains("Insert at position 1 of the page", labels);
    }

    // The top, left, width and height of every block and paragraph of the page, one after the other.
    private static Task<double[]> GetBlockBoxesAsync(IPage page) =>
        page.Locator(".markdown-content").EvaluateAsync<double[]>(
            """
            content => [...content.querySelectorAll('.subject-component-wrapper, p')].flatMap(element => {
                const box = element.getBoundingClientRect();
                return [box.top, box.left, box.width, box.height];
            })
            """);
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.E2E.Tests --filter "FullyQualifiedName~NoBlockOfThePageMoves|FullyQualifiedName~EachHasANameOfItsOwn"`
Expected: FAIL: the lines of the card push its content down, and every line is named "Insert block here".

- [ ] **Step 3: The line**

`BlockInsertLine.razor`:

```razor
@* A place between the blocks of a page that is edited inline, where a click inserts a new block. It takes no room, so that
   the content does not move when lines show or hide with the selection; its bar is drawn over the gap above the block that
   follows. A click on it must not reach the widget around it, which would take it for a click that selects the widget. *@
<div class="block-insert-line edit-mode-interactive" data-testid="block-insert-line" @onclick:stopPropagation="true">
    <div class="block-insert-line-bar">
        <MudIconButton Icon="@Icons.Material.Filled.Add"
                       Size="Size.Small"
                       Variant="Variant.Outlined"
                       Color="Color.Primary"
                       title="@Label"
                       aria-label="@Label"
                       OnClick="OnInsert" />
    </div>
</div>

@code {
    /// <summary>Raised when the line is clicked.</summary>
    [Parameter]
    public EventCallback OnInsert { get; set; }

    /// <summary>The accessible name of the line, which tells it apart from the other lines of the page.</summary>
    [Parameter, EditorRequired]
    public string Label { get; set; } = string.Empty;
}
```

In `SubjectComponent.razor.css`, replace the four `block-insert-line` rules with:

```css
/* The line where a new block goes, between the blocks of the content that is edited. It takes no room, so that the content
   does not move when the selection shows or hides the lines, and margins collapse through it as if it were not there: its
   bar is drawn over the gap above the block that follows. */
::deep .block-insert-line {
    position: relative;
    height: 0;
    z-index: 4;
}

::deep .block-insert-line-bar {
    position: absolute;
    left: 0;
    right: 0;
    top: -0.5rem;
    transform: translateY(-50%);
    display: flex;
    align-items: center;
    gap: 6px;
    opacity: 0.5;
}

::deep .block-insert-line-bar:hover,
::deep .block-insert-line-bar:focus-within {
    opacity: 1;
}

::deep .block-insert-line-bar::before,
::deep .block-insert-line-bar::after {
    content: "";
    flex: 1;
    height: 1px;
    background: rgba(var(--mud-palette-primary-rgb), 0.7);
}

::deep .block-insert-line-bar .mud-icon-button {
    padding: 2px;
    background: var(--mud-palette-surface);
}
```

At the end of `MarkdownFilePageComponent.razor.css`:

```css
/* An insert line in the row of a card footer stands between two blocks of the row and takes none of its gap. */
.markdown-content ::deep .card-widget > .mud-card-actions > .subject-component-wrapper > .markdown-fragment > .block-insert-line {
    margin: 0 -4px;
}

.markdown-content ::deep .card-widget > .mud-card-actions > .subject-component-wrapper > .markdown-fragment > .block-insert-line > .block-insert-line-bar {
    right: auto;
    top: 50%;
    transform: translate(-50%, -50%);
}
```

The `-4px` is half of the `gap: 8px` of the footer row on each side. If the E2E test still measures a shift in the footer, measure the gap the line adds and cancel exactly that.

In `SubjectComponent.razor`, rename `private string GetAccessibleName()` to `public string GetAccessibleName()` and give it the doc "Gets the name that assistive technology reads for the widget: its title, its path or the name of its type."

- [ ] **Step 4: The names in the content**

Replace `MarkdownContentView.razor` from the top down to `@code {` with:

```razor
@using System.Text
@using System.Web
@using HomeBlaze.Components
@using HomeBlaze.Components.Abstractions.Attributes
@using HomeBlaze.Components.Abstractions.TimeZones
@using HomeBlaze.Components.Components
@using HomeBlaze.Storage.Internal
@using Namotion.Interceptor
@inject ITimeZoneDisplay TimeZone
@implements IDisposable

@{
    var showsInsertLines = ShowsInsertLines();
    var contentName = showsInsertLines ? GetContentName() : null;
    var position = 0;
}
@foreach (var segment in GetRenderSegments())
{
    @if (segment.Html != null)
    {
        @((MarkupString)segment.Html)
    }
    else if (segment.Widget != null)
    {
        @if (showsInsertLines)
        {
            position++;
            <BlockInsertLine Label="@($"Insert at position {position} of {contentName}")"
                             OnInsert="@(() => StructureEditor!.InsertWidgetAsync(Content!, segment.Widget))" />
        }

        @* Keyed, so that the state of a component stays with its subject when a reload inserts or removes a block. *@
        <SubjectComponent @key="segment.Widget" Subject="@segment.Widget" Type="SubjectComponentType.Widget" />
    }
}
@if (showsInsertLines)
{
    <BlockInsertLine Label="@($"Insert at the end of {contentName}")"
                     OnInsert="@(() => StructureEditor!.InsertWidgetAsync(Content!, null))" />
}
```

Add to its `@code`:

```csharp
    // The name of the content in the names of its insert lines: the name of the widget it belongs to, or the page.
    private string GetContentName() =>
        Owner?.GetSelectableSelfOrAncestor() is { } owner ? owner.GetAccessibleName() : "the page";
```

- [ ] **Step 5: Run the tests and accept the API snapshot**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Components.Tests`
Expected: `VerifyChecksTests.PublicApi` FAILS (`BlockInsertLine.Label`, `SubjectComponent.GetAccessibleName`). Accept the `.received.txt`, rerun: PASS.

Run: `dotnet test src/HomeBlaze/HomeBlaze.E2E.Tests --filter "FullyQualifiedName~InlineStructureEditingTests|FullyQualifiedName~CardLayoutTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/HomeBlaze
git commit -m "feat: draw insert lines over the gaps between blocks and name each line"
```

---

### Task 10: An inert Split preview that passes clicks to the widget under the pointer

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze/Components/Pages/Pages.razor:14-15,55-70,193-194,240-247,284-291`
- Modify: `src/HomeBlaze/HomeBlaze/Components/Pages/Pages.razor.js`
- Modify: `src/HomeBlaze/HomeBlaze.Components/SubjectComponent.razor:180-182`
- Modify: `src/HomeBlaze/HomeBlaze.Components/SubjectComponent.razor.css:283-304`
- Delete: `src/HomeBlaze/HomeBlaze.Components/SubjectRevealer.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Components.Tests/VerifyChecksTests.PublicApi.verified.txt`
- Test: `src/HomeBlaze/HomeBlaze.E2E.Tests/InlineStructureEditingTests.cs` and the other E2E classes that click into the preview

**Interfaces:**
- Consumes: `ISubjectRevealTarget` (phase 1).
- Produces: `[CascadingParameter] public ISubjectRevealTarget? SubjectComponent.Revealer`; `Pages` implements `ISubjectRevealTarget` and cascades itself to the preview; `forwardPreviewClicks()` in `Pages.razor.js`.

- [ ] **Step 1: Write the failing E2E test**

```csharp
    [Fact]
    public async Task WhenThePageIsSplit_ThenThePreviewIsInertAndItsControlsTakeNoFocus()
    {
        // Arrange
        var page = await OpenPageInSplitModeAsync();
        var powerSwitch = page.Locator(".page-split-preview [role='switch']").First;
        await Assertions.Expect(powerSwitch).ToBeVisibleAsync(new() { Timeout = Timeout });

        // Act
        await powerSwitch.EvaluateAsync("element => element.focus()");

        // Assert
        await Assertions.Expect(page.Locator(".page-split-preview-content")).ToHaveAttributeAsync("inert", "");
        Assert.False(await powerSwitch.EvaluateAsync<bool>("element => element === document.activeElement"));
    }
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test src/HomeBlaze/HomeBlaze.E2E.Tests --filter "FullyQualifiedName~ThePreviewIsInert"`
Expected: FAIL: there is no `.page-split-preview-content`, and the switch takes the focus.

- [ ] **Step 3: Cascade the page as the reveal target and make the preview inert**

In `Pages.razor`, add `@implements ISubjectRevealTarget` below `@implements IAsyncDisposable`, and replace the preview:

```razor
                        <div class="page-split-preview" style="width: 100%; height: 100%; overflow-y: auto;">
                            @* The preview shows the saved page and never writes it, so that it cannot overwrite the
                               unsaved text of the editor. Its content is inert, so that neither the pointer nor the
                               keyboard reaches a control of any widget. Pages.razor.js passes a click on the preview to
                               the widget under the pointer, which reveals its block in the editor. The controls that read
                               IsReadOnly disable themselves as well. *@
                            <div class="page-split-preview-content" style="height: 100%;" inert>
                                <CascadingValue Value="false" Name="IsEditing">
                                    <CascadingValue Value="true" Name="IsReadOnly" IsFixed="true">
                                        <CascadingValue Value="@((ISubjectRevealTarget)this)" IsFixed="true">
                                            <SubjectComponent @key="_componentKey"
                                                              Subject="@_subject"
                                                              Type="SubjectComponentType.Page"/>
                                        </CascadingValue>
                                    </CascadingValue>
                                </CascadingValue>
                            </div>
                        </div>
```

In the `@code` block: delete the field `_revealer`, the line `_revealer = new SubjectRevealer(RevealInEditorAsync);` in `OnInitialized` and the method `RevealInEditorAsync`, and add:

```csharp
    /// <summary>Shows the block of the subject in the source editor of the Split mode and returns whether it found it.</summary>
    public Task<bool> RevealAsync(IInterceptorSubject subject) =>
        _editPanel?.EditComponent is ISubjectRevealTarget target ? target.RevealAsync(subject) : Task.FromResult(false);
```

In `FitPageHeadAsync`, start the forwarding when the Split mode gets its bar:

```csharp
            if (editorBar is { } bar)
            {
                _module ??= await JsRuntime.InvokeAsync<IJSObjectReference>("import", ModulePath);
                if (mode == PageEditMode.Split)
                {
                    await _module.InvokeVoidAsync("forwardPreviewClicks");
                }

                _pageHeadFit = await _module.InvokeAsync<IJSObjectReference>("fitToEditorBar", _pageHeadContent, bar);
            }
```

Append to `Pages.razor.js`:

```js
let isForwardingPreviewClicks = false;

// The content of the preview next to the editor is inert, so a click lands on the preview itself. It goes on to the
// revealable widget under the pointer, which reveals its block in the editor. While the content is not inert for this one
// call, the pointer-events rules of the wrappers make elementFromPoint find the innermost revealable widget.
export function forwardPreviewClicks() {
    if (isForwardingPreviewClicks) {
        return;
    }

    isForwardingPreviewClicks = true;
    document.addEventListener('click', event => {
        const preview = event.target;
        if (!(preview instanceof Element) || !preview.classList.contains('page-split-preview')) {
            return;
        }

        const content = preview.querySelector(':scope > .page-split-preview-content');
        if (!content) {
            return;
        }

        content.inert = false;
        const element = document.elementFromPoint(event.clientX, event.clientY);
        content.inert = true;

        const wrapper = element?.closest('.subject-component-wrapper.revealable');
        if (wrapper && content.contains(wrapper)) {
            wrapper.dispatchEvent(new MouseEvent('click', {
                bubbles: true,
                cancelable: true,
                clientX: event.clientX,
                clientY: event.clientY
            }));
        }
    });
}
```

In `SubjectComponent.razor`:

```csharp
    /// <summary>Cascaded to the preview next to an editor, where a click reveals the widget in the editor.</summary>
    [CascadingParameter]
    public ISubjectRevealTarget? Revealer { get; set; }
```

`RevealAsync` keeps calling `Revealer!.RevealAsync(subject)`. Delete `SubjectRevealer.cs`.

In `SubjectComponent.razor.css`, replace the comment above `.subject-component-wrapper.revealable` with "In the preview next to the editor, the content is inert (see Pages.razor), and a script finds the widget under a click with elementFromPoint. The content of a revealable widget takes no pointer input, so that its wrapper is found; a nested widget takes the pointer again and is found itself.", and delete the `@media (hover: hover)` rule of `.revealable:hover`, which an inert preview never shows.

- [ ] **Step 4: Update the tests that click into the preview**

An inert element fails the actionability checks of Playwright, and role locators leave inert content out:
- Every `ClickAsync` on an element inside `.page-split-preview` gets `Force = true` (for example `switchWrapper.ClickAsync(new() { Position = new() { X = 2, Y = 2 }, Force = true })`).
- Every `GetByRole` inside the preview gets `IncludeHidden = true` (for example `GetByRole(AriaRole.Switch, new() { IncludeHidden = true })` and the slider of `WhenASliderInTheSplitPreviewIsOperatedByKeyboard_ThenItWritesNothing`).

Find them with `grep -rn "page-split-preview" src/HomeBlaze/HomeBlaze.E2E.Tests` (InlineStructureEditingTests, MarkdownEditorTests, PageEditTests, CanvasLayoutTests, LayoutBlockSaveTests, MarkdownBlockTests, PageMenuTests).

- [ ] **Step 5: Run the tests and accept the API snapshot**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Components.Tests`
Expected: `VerifyChecksTests.PublicApi` FAILS (`SubjectRevealer` gone, `Revealer` type). Accept the `.received.txt`, rerun: PASS.

Run: `dotnet test src/HomeBlaze/HomeBlaze.E2E.Tests`
Expected: PASS for the whole project, including the new test and the Split tests of phase 1.

- [ ] **Step 6: Commit**

```bash
git add -A src/HomeBlaze
git commit -m "fix: make the Split preview inert and pass its clicks to the widget under the pointer"
```

---

### Task 11: A golden file of the HTML that View mode renders for a page of text

This task runs on the code before text markers exist (Task 12), so that the golden file is the HTML of today.

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.E2E.Tests/TestData/Demo/TextBlocks.md`
- Create: `src/HomeBlaze/HomeBlaze.E2E.Tests/ViewHtmlGoldenTests.cs`
- Create: `src/HomeBlaze/HomeBlaze.E2E.Tests/Golden/TextBlocks.view.html` (written by the test, then checked in)
- Modify: `src/HomeBlaze/HomeBlaze.E2E.Tests/HomeBlaze.E2E.Tests.csproj` (copy `Golden\**` to the output)

**Interfaces:**
- Produces: `ViewHtmlGoldenTests.WhenAPageOfTextIsViewed_ThenItsHtmlIsTheGoldenHtml`, which every later task keeps green.

- [ ] **Step 1: The page**

`TestData/Demo/TextBlocks.md` (no expressions and no live values, so that its HTML does not change between runs):

~~~~markdown
---
title: Text Blocks
---

# Golden Text

A paragraph with *emphasis*, **strong text**, `code` and a [link](https://example.com).
A second line of the same paragraph.

## A Heading

- First item
- Second item
  - Nested item

1. One
2. Two

> A quote
> on two lines.

```csharp
var answer = 42;
```

| Name | Value |
|------|-------|
| A    | 1     |

---

<div class="golden-note">
Raw HTML.
</div>

Text with a footnote[^1].

:: HomeBlaze.Components.Card Title="Golden Card"
Text inside the card.

- A list inside the card
::

Closing paragraph.

[^1]: The footnote.
~~~~

- [ ] **Step 2: The test**

In `HomeBlaze.E2E.Tests.csproj`, next to the `TestData` item:

```xml
    <None Update="Golden\**\*">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </None>
```

`ViewHtmlGoldenTests.cs`:

```csharp
using System.Text.RegularExpressions;
using HomeBlaze.E2E.Tests.Infrastructure;
using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// Pins the HTML that View mode renders for a page of text, which the text editing of Inline mode must not change.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public partial class ViewHtmlGoldenTests
{
    private const int Timeout = 30000;
    private const string PageName = "TextBlocks.md";
    private const string GoldenFileName = "TextBlocks.view.html";

    private readonly PlaywrightFixture _fixture;

    public ViewHtmlGoldenTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task WhenAPageOfTextIsViewed_ThenItsHtmlIsTheGoldenHtml()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync($"{_fixture.ServerAddress}pages/Demo/{PageName}");
        await page.WaitUntilInteractiveAsync(Timeout);
        var content = page.Locator(".markdown-content");
        await Assertions.Expect(content.GetByText("Closing paragraph.")).ToBeVisibleAsync(new() { Timeout = Timeout });

        // Act
        var html = BlazorMarker().Replace(await content.InnerHTMLAsync(), string.Empty);

        // Assert
        var goldenPath = Path.Combine(AppContext.BaseDirectory, "Golden", GoldenFileName);
        if (!File.Exists(goldenPath))
        {
            var receivedPath = Path.Combine(AppContext.BaseDirectory, "Golden", "TextBlocks.view.received.html");
            Directory.CreateDirectory(Path.GetDirectoryName(receivedPath)!);
            await File.WriteAllTextAsync(receivedPath, html);
            Assert.Fail($"There is no golden file yet. Review {receivedPath} and copy it to src/HomeBlaze/HomeBlaze.E2E.Tests/Golden/{GoldenFileName}.");
        }

        Assert.Equal(await File.ReadAllTextAsync(goldenPath), html);
    }

    // The comments that Blazor keeps in the document as boundaries of components.
    [GeneratedRegex("<!--!-->")]
    private static partial Regex BlazorMarker();
}
```

- [ ] **Step 3: Capture the golden file**

Run: `dotnet test src/HomeBlaze/HomeBlaze.E2E.Tests --filter "FullyQualifiedName~ViewHtmlGoldenTests"`
Expected: FAIL with "There is no golden file yet". Review the received file: it must hold the headings, the lists, the table box, the raw HTML, the card and the footnotes. Copy it to `src/HomeBlaze/HomeBlaze.E2E.Tests/Golden/TextBlocks.view.html`.

Run the same command twice more.
Expected: PASS both times. If a part differs between runs (an id that MudBlazor generates, for example), replace exactly that part in `html` before the comparison with a regular expression, name it in a comment, and capture again.

- [ ] **Step 4: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.E2E.Tests
git commit -m "test: pin the HTML that View mode renders for a page of text"
```

---

### Task 12: Start an HTML segment of its own for every text block

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.Storage/Pages/MarkdownSegmentBuilder.cs:14,37-38,101-146,148-210`
- Modify: `src/HomeBlaze/HomeBlaze.Storage/Pages/MarkdownParseResult.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Storage/Internal/HtmlSegment.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Storage/Internal/MarkdownContentParser.cs:270-345,576-612`
- Modify: `src/HomeBlaze/HomeBlaze.Storage/Files/MarkdownFile.cs` (`PublishAndWriteAsync`)
- Modify: `src/HomeBlaze/HomeBlaze.Storage.Tests/VerifyChecksTests.PublicApi.verified.txt`
- Test: `src/HomeBlaze/HomeBlaze.Storage.Tests/Pages/MarkdownSegmentBuilderTests.cs`, `Pages/RepositoryPagesTests.cs`, `Pages/MarkdownWidgetBlockTests.cs`

**Interfaces:**
- Consumes: `MarkdownTextBlocks.Find`, `MarkdownTextBlock` (Task 6).
- Produces:
  - `internal static IReadOnlyList<MarkdownSegment> MarkdownSegmentBuilder.Build(MarkdownPage page, Func<string, string>? linkRewriter, bool markTextBlocks)`; the public `Build(page, linkRewriter)` passes false.
  - `HtmlMarkdownSegment.TextBlock` (`internal MarkdownTextBlock? { get; init; }`).
  - `public enum HtmlSegmentStart { None, Text, Other }` and `public HtmlSegmentStart HtmlSegment.Start`; `HtmlSegment(string html, HtmlSegmentStart start = HtmlSegmentStart.None)`.
  - `internal IReadOnlyDictionary<HtmlSegment, MarkdownTextBlock> MarkdownParseResult.TextBlocks { get; init; }` and `internal static readonly ... MarkdownParseResult.NoTextBlocks`.

- [ ] **Step 1: Write the failing tests**

`MarkdownSegmentBuilderTests`:

```csharp
    [Fact]
    public void WhenTextBlocksAreMarked_ThenEachStartsAnHtmlSegmentWithItsLines()
    {
        // Arrange
        var page = MarkdownPageParser.Parse("# Title\n\nFirst {{ a/B }} paragraph.\n\n:: Demo.Card\nInside.\n::\n");

        // Act
        var segments = MarkdownSegmentBuilder.Build(page, linkRewriter: null, markTextBlocks: true);

        // Assert
        var starts = segments.OfType<HtmlMarkdownSegment>().Where(segment => segment.TextBlock is not null).ToList();
        Assert.Equal([1, 3], starts.Select(segment => segment.TextBlock!.Line));
        Assert.StartsWith("<h1", starts[0].Html);
        Assert.StartsWith("<p>First ", starts[1].Html.TrimStart());
        var all = segments.ToList();
        Assert.IsType<ExpressionMarkdownSegment>(all[all.IndexOf(starts[1]) + 1]);
        var inside = segments.OfType<BlockMarkdownSegment>().Single().Content
            .OfType<HtmlMarkdownSegment>().Single(segment => segment.TextBlock is not null);
        Assert.Same(page.Blocks[0], inside.TextBlock!.Owner);
        Assert.Contains("Inside.", inside.Html);
    }

    [Fact]
    public void WhenThePageHasACommentShapedLikeATextMarker_ThenItStaysInTheHtml()
    {
        // Arrange
        var page = MarkdownPageParser.Parse("<!--TEXT:00000000000000000000000000000000:0-->\n\nText.\n");

        // Act
        var segments = MarkdownSegmentBuilder.Build(page, linkRewriter: null, markTextBlocks: true);

        // Assert
        Assert.Contains(
            "<!--TEXT:00000000000000000000000000000000:0-->",
            string.Concat(segments.OfType<HtmlMarkdownSegment>().Select(segment => segment.Html)));
    }
```

`RepositoryPagesTests` (add `using System.Text;`):

```csharp
    [Theory]
    [MemberData(nameof(PagePaths))]
    public void WhenTheTextBlocksOfARepositoryPageAreMarked_ThenItsHtmlStaysTheSame(string relativePath)
    {
        // Arrange
        var page = MarkdownPageParser.Parse(ReadPage(relativePath));

        // Act
        var marked = DescribeAsViewed(MarkdownSegmentBuilder.Build(page, linkRewriter: null, markTextBlocks: true));
        var unmarked = DescribeAsViewed(MarkdownSegmentBuilder.Build(page));

        // Assert
        Assert.Equal(unmarked, marked);
    }

    // The segments as a viewed page joins them: the HTML between two other segments in one piece, and every block with its
    // content.
    private static List<string> DescribeAsViewed(IReadOnlyList<MarkdownSegment> segments)
    {
        var result = new List<string>();
        var html = new StringBuilder();
        foreach (var segment in segments)
        {
            if (segment is HtmlMarkdownSegment htmlSegment)
            {
                html.Append(htmlSegment.Html);
                continue;
            }

            if (html.Length > 0)
            {
                result.Add("html:" + html);
                html.Clear();
            }

            switch (segment)
            {
                case ExpressionMarkdownSegment expression:
                    result.Add($"expression:{expression.Path}|{expression.Format}|{expression.Error}");
                    break;
                case BlockMarkdownSegment block:
                    result.Add($"block:{block.Block.Index}");
                    result.AddRange(DescribeAsViewed(block.Content));
                    result.Add($"end:{block.Block.Index}");
                    break;
            }
        }

        if (html.Length > 0)
        {
            result.Add("html:" + html);
        }

        return result;
    }
```

`MarkdownWidgetBlockTests`:

```csharp
    [Fact]
    public async Task WhenAPageIsRead_ThenEveryTopLevelBlockOfTextStartsAnHtmlSegmentOfItsOwn()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();

        // Act
        var page = await fixture.AddPageAsync(
            "page.md", $"# Title\n\nText {{{{ missing/Path }}}}.\n\n<div>raw</div>\n\n:: {LeafType} Name=a\n::\n\nAfter.\n");

        // Assert: the title, the text and the raw HTML before the block are one run with raw HTML in it.
        var starts = page.Children.Values.OfType<HtmlSegment>()
            .Select(segment => segment.Start)
            .Where(start => start != HtmlSegmentStart.None);
        Assert.Equal([HtmlSegmentStart.Other, HtmlSegmentStart.Text], starts);
    }
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests --filter "FullyQualifiedName~MarkdownSegmentBuilderTests|FullyQualifiedName~RepositoryPagesTests|FullyQualifiedName~MarkdownWidgetBlockTests"`
Expected: FAIL to compile (`markTextBlocks`, `TextBlock`, `HtmlSegmentStart`).

- [ ] **Step 3: Markers in the builder**

In `MarkdownSegmentBuilder.cs` (add `using System.Globalization;`):

```csharp
public sealed record HtmlMarkdownSegment(string Html) : MarkdownSegment
{
    /// <summary>The text block that this HTML starts, when the page was built with its text blocks marked, or null.</summary>
    internal MarkdownTextBlock? TextBlock { get; init; }
}
```

```csharp
    [GeneratedRegex(@"<!--(/?)BLOCK:([0-9a-f]{32}):(\d+)-->|<!--TEXT:([0-9a-f]{32}):(\d+)-->|" + ExpressionPattern)]
    private static partial Regex MarkerRegex();
```

```csharp
    public static IReadOnlyList<MarkdownSegment> Build(MarkdownPage page, Func<string, string>? linkRewriter = null) =>
        Build(page, linkRewriter, markTextBlocks: false);

    /// <summary>
    /// Renders the page and cuts it into segments per block. With <paramref name="markTextBlocks"/>, every text block of the
    /// page (see <see cref="MarkdownTextBlocks"/>) starts an <see cref="HtmlMarkdownSegment"/> of its own that carries it,
    /// and the HTML of all segments together is the same as without.
    /// </summary>
    internal static IReadOnlyList<MarkdownSegment> Build(MarkdownPage page, Func<string, string>? linkRewriter, bool markTextBlocks)
    {
        // ... the existing body up to and including the replacement of the table renderer ...

        List<MarkdownTextBlock>? textBlocks = null;
        if (markTextBlocks)
        {
            textBlocks = MarkdownTextBlocks.Find(page);
            var markers = new Dictionary<Block, int>(textBlocks.Count, ReferenceEqualityComparer.Instance);
            for (var index = 0; index < textBlocks.Count; index++)
            {
                markers[textBlocks[index].FirstBlock] = index;
            }

            // Straight to the writer and not through the renderer, which would take the marker for text on the line and write
            // other line breaks around the block, so that the HTML would differ once Split takes the markers out.
            renderer.ObjectWriteBefore += (_, markdownObject) =>
            {
                if (markdownObject is Block block && markers.TryGetValue(block, out var markerIndex))
                {
                    writer.Write($"<!--TEXT:{token}:{markerIndex.ToString(CultureInfo.InvariantCulture)}-->");
                }
            };
        }

        renderer.Render(page.Document);
        writer.Flush();

        return Split(writer.ToString(), page, token, textBlocks);
    }
```

Replace `Split`:

```csharp
    internal static List<MarkdownSegment> Split(
        string html, MarkdownPage page, string token, IReadOnlyList<MarkdownTextBlock>? textBlocks = null)
    {
        var root = new List<MarkdownSegment>();
        var stack = new Stack<(MarkdownBlock Block, List<MarkdownSegment> Outer)>();
        var current = root;
        var lastIndex = 0;

        // The text block whose marker came last, which the next segment starts.
        MarkdownTextBlock? pendingTextBlock = null;

        foreach (Match match in MarkerRegex().Matches(html))
        {
            var isForgedMarker =
                (match.Groups[2].Success && match.Groups[2].Value != token) ||
                (match.Groups[4].Success && (match.Groups[4].Value != token || textBlocks is null));
            if (isForgedMarker)
            {
                continue;
            }

            // The path is HTML here, so a quote or an ampersand in it is a character reference.
            var path = match.Groups[6].Success ? WebUtility.HtmlDecode(match.Groups[6].Value) : null;

            // The pattern cannot see a '<' or a brace that the page text has literally, because the HTML has
            // a character reference for it. In the text such braces enclose no expression, so they stay text here.
            if (path is not null && path.AsSpan().IndexOfAny(ExcludedFromPath) >= 0)
            {
                continue;
            }

            if (match.Index > lastIndex)
            {
                current.Add(CreateHtmlSegment(html[lastIndex..match.Index], ref pendingTextBlock));
            }

            lastIndex = match.Index + match.Length;

            if (match.Groups[4].Success)
            {
                pendingTextBlock = textBlocks![int.Parse(match.Groups[5].Value, CultureInfo.InvariantCulture)];
                continue;
            }

            // A text block that starts without HTML of its own still starts a segment, so that its content can tell it.
            if (pendingTextBlock is not null)
            {
                current.Add(CreateHtmlSegment(string.Empty, ref pendingTextBlock));
            }

            if (path is not null)
            {
                var text = MarkdownExpressionText.Parse(path);
                current.Add(new ExpressionMarkdownSegment(text.Path, text.Format, text.Error));
            }
            else if (match.Groups[1].Length == 0)
            {
                stack.Push((page.AllBlocks[int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture)], current));
                current = [];
            }
            else if (stack.Count > 0)
            {
                var (block, outer) = stack.Pop();
                outer.Add(new BlockMarkdownSegment(block, current));
                current = outer;
            }
        }

        if (lastIndex < html.Length || pendingTextBlock is not null)
        {
            current.Add(CreateHtmlSegment(html[lastIndex..], ref pendingTextBlock));
        }

        // A closing marker that was not found must not take the content of its block out of the result.
        while (stack.Count > 0)
        {
            var (block, outer) = stack.Pop();
            outer.Add(new BlockMarkdownSegment(block, current));
            current = outer;
        }

        return root;
    }

    private static HtmlMarkdownSegment CreateHtmlSegment(string html, ref MarkdownTextBlock? pendingTextBlock)
    {
        var segment = new HtmlMarkdownSegment(html) { TextBlock = pendingTextBlock };
        pendingTextBlock = null;
        return segment;
    }
```

- [ ] **Step 4: Segments and the snapshot**

`HtmlSegment.cs`:

```csharp
using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// Represents a raw HTML segment within a MarkdownFile.
/// Rendered directly without a component wrapper to allow split HTML tags.
/// </summary>
[InterceptorSubject]
[ExcludeFrom(ExclusionTarget.All)]
[DisplayName("Text")]
public partial class HtmlSegment
{
    public string Html { get; }

    /// <summary>What the HTML starts among the top-level content of its page or block.</summary>
    public HtmlSegmentStart Start { get; }

    public HtmlSegment(string html, HtmlSegmentStart start = HtmlSegmentStart.None)
    {
        Html = html;
        Start = start;
    }
}

/// <summary>What an <see cref="HtmlSegment"/> starts among the top-level content of its page or block.</summary>
public enum HtmlSegmentStart
{
    /// <summary>Nothing: it goes on with the content before it, such as the text after an expression.</summary>
    None,

    /// <summary>A text block that can be edited, such as a paragraph, a heading, a list or a table.</summary>
    Text,

    /// <summary>Other content that cannot be edited as text, such as raw HTML, a style block or an error.</summary>
    Other
}
```

`MarkdownParseResult.cs` (add `using HomeBlaze.Storage.Internal;`), inside the record:

```csharp
    /// <summary>A snapshot without text blocks, such as one that a write published before the read of its text.</summary>
    internal static readonly IReadOnlyDictionary<HtmlSegment, MarkdownTextBlock> NoTextBlocks =
        new Dictionary<HtmlSegment, MarkdownTextBlock>();

    /// <summary>The text block that each HTML segment of the parse starts, by the segment.</summary>
    internal IReadOnlyDictionary<HtmlSegment, MarkdownTextBlock> TextBlocks { get; init; } = NoTextBlocks;
```

In `MarkdownContentParser.ParseAsync` (the internal overload), build with markers and publish the text blocks:

```csharp
        var segments = MarkdownSegmentBuilder.Build(page, url => RewriteLink(url, basePath, pageName), markTextBlocks: true);
        // ...
            var children = await BuildChildrenAsync(segments, existingChildren, state, cancellationToken);
            return new MarkdownParseResult(children, page, state.BlockSubjects, state.NamedSubjects) { TextBlocks = state.TextBlocks };
```

In `BuildChildrenAsync`, pass the text block: `AddHtml(children, existingChildren, index, html.Html, html.TextBlock, state);` and for the error box `AddHtml(children, existingChildren, index, error!.Render(), textBlock: null, state);`. Replace `AddHtml`:

```csharp
    private static void AddHtml(
        Dictionary<string, IInterceptorSubject> children,
        IDictionary<string, IInterceptorSubject> existingChildren,
        int index,
        string html,
        MarkdownTextBlock? textBlock,
        ParseState state)
    {
        var start = textBlock is null ? HtmlSegmentStart.None
            : textBlock.IsEditable ? HtmlSegmentStart.Text
            : HtmlSegmentStart.Other;

        // The index is part of the key because the same HTML can occur several times.
        var key = $"{HtmlKeyPrefix}{index}_{ComputeHash(html)}";
        var segment = existingChildren.TryGetValue(key, out var existing) && existing is HtmlSegment reused && reused.Start == start
            ? reused
            : new HtmlSegment(html, start);
        children[key] = segment;
        if (textBlock is not null)
        {
            state.TextBlocks[segment] = textBlock;
        }
    }
```

In `ParseState`:

```csharp
        /// <summary>The text block that each HTML segment of this parse starts.</summary>
        public Dictionary<HtmlSegment, MarkdownTextBlock> TextBlocks { get; } = new(ReferenceEqualityComparer.Instance);
```

In `MarkdownFile.PublishAndWriteAsync`, publish without text blocks:

```csharp
        // The text blocks have the lines of the text before the write; the read inside the write finds the new ones.
        var published = snapshot with
        {
            Page = write.Page,
            BlockSubjects = write.BlockSubjects,
            TextBlocks = MarkdownParseResult.NoTextBlocks
        };
```

`MarkdownSubjectKeys.Walk` and `MarkdownWriteRules` keep the public `Build` without markers. The widget keys count only blocks (Task 1), so the extra HTML segments change no key of a widget.

- [ ] **Step 5: Run the tests and accept the API snapshot**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests`
Expected: PASS after accepting `VerifyChecksTests.PublicApi` (`HtmlSegment.Start`, its constructor, `HtmlSegmentStart`, the `DisplayName` attribute).

Run: `dotnet test src/HomeBlaze/HomeBlaze.E2E.Tests --filter "FullyQualifiedName~ViewHtmlGoldenTests|FullyQualifiedName~MarkdownBlockTests|FullyQualifiedName~InlineStructureEditingTests"`
Expected: PASS. The golden test proves that View mode renders the same HTML.

- [ ] **Step 6: Commit**

```bash
git add src/HomeBlaze
git commit -m "feat: start an HTML segment of its own for every block of text of a page"
```

---

### Task 13: Text operations

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.Storage/Pages/MarkdownBlockWriter.cs` (add `ReplaceLines`, `RemoveIndentation`)
- Modify: `src/HomeBlaze/HomeBlaze.Storage/Pages/MarkdownBlockAlignment.cs:353-354`
- Modify: `src/HomeBlaze/HomeBlaze.Storage/Pages/MarkdownPageOperations.cs:5-10,261-313` (and new methods)
- Test: `src/HomeBlaze/HomeBlaze.Storage.Tests/Pages/MarkdownPageOperationsTests.cs`, `Pages/MarkdownBlockWriterTests.cs`

**Interfaces:**
- Consumes: `MarkdownTextBlock`, `MarkdownTextBlocks.Find` (Task 6), `MarkdownBlockWriter.CutLines`, `GetLines` (Task 7).
- Produces:
  - `internal sealed record MarkdownStructureChange(MarkdownBlockWrite Write, MarkdownBlock? ChangedBlock, int ResultBlockIndex = -1, int ResultTextLine = 0)`.
  - `public static int MarkdownPageOperations.GetInsertLine(MarkdownPage page, MarkdownBlock? owner, int? beforeLine)`.
  - `public static MarkdownStructureChange? MarkdownPageOperations.TryInsertWidget(MarkdownPage page, IReadOnlyDictionary<int, IInterceptorSubject> blockSubjects, MarkdownBlock? owner, int line, string typeName, string? id, string configurationJson, out string? refusal)` (a line instead of the block to insert before).
  - `public static string MarkdownPageOperations.GetText(MarkdownPage page, MarkdownTextBlock textBlock)`.
  - `public static MarkdownStructureChange? TryReplaceText(MarkdownPage page, IReadOnlyDictionary<int, IInterceptorSubject> blockSubjects, MarkdownTextBlock textBlock, string text, out string? refusal)`.
  - `public static MarkdownStructureChange? TryRemoveText(MarkdownPage page, IReadOnlyDictionary<int, IInterceptorSubject> blockSubjects, MarkdownTextBlock textBlock, out string? refusal)`.
  - `public static MarkdownStructureChange? TryInsertText(MarkdownPage page, IReadOnlyDictionary<int, IInterceptorSubject> blockSubjects, MarkdownBlock? owner, int line, string text, out string? refusal)`.
  - `public static bool CanInsertTextAt(MarkdownPage page, MarkdownBlock? owner, int line)`.
  - `internal static string MarkdownBlockWriter.ReplaceLines(string source, int line, int endLine, string text, string lineEnding, out MarkdownLineEdit edit)`, `internal static string MarkdownBlockWriter.RemoveIndentation(string text, string indentation)`.

- [ ] **Step 1: Write the failing tests**

In `MarkdownPageOperationsTests`, add a helper and change the calls of `TryInsertWidget`: every `before: X` (or the positional block argument) becomes `InsertLine(page, owner, X)`, where `owner` is the owner argument of the same call.

```csharp
    private static int InsertLine(MarkdownPage page, MarkdownBlock? owner, MarkdownBlock? before) =>
        MarkdownPageOperations.GetInsertLine(page, owner, before?.Line);

    private static MarkdownTextBlock TextAt(MarkdownPage page, int line) =>
        MarkdownTextBlocks.Find(page).Single(textBlock => textBlock.Line == line);
```

Replace `WhenAnInsertedWidgetWouldTakeOverTheTextAfterIt_ThenTheInsertIsRefused` (a line is no block that can stand outside the owner any more; `MarkdownFile` checks that in Task 14) with:

```csharp
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
```

Add the text tests:

```csharp
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

    [Theory]
    [InlineData("```")]
    [InlineData("::: Demo.X\n:::")]
    public void WhenInsertedTextWouldChangeTheRestOfThePageOrAddABlock_ThenTheInsertIsRefused(string text)
    {
        // Arrange
        var page = MarkdownPageParser.Parse("intro\n\n:: Demo.One\n::\n\nafter\n");

        // Act
        var change = MarkdownPageOperations.TryInsertText(page, NoSubjects, null, 3, text, out var refusal);

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
```

In `MarkdownBlockWriterTests`:

```csharp
    [Theory]
    [InlineData("a\n\nOld\n\nb\n", 3, 3, "New", "a\n\nNew\n\nb\n")]
    [InlineData("a\nOld\nb\n", 2, 2, "New", "a\n\nNew\n\nb\n")]
    [InlineData(":: X\nOld\n::\n", 2, 2, "New", ":: X\nNew\n::\n")]
    [InlineData("a\n\nOld", 3, 3, "New", "a\n\nNew")]
    public void WhenLinesAreReplaced_ThenTheNewLinesStandApartFromTextAroundThem(string source, int line, int endLine, string text, string expected)
    {
        // Act
        var result = MarkdownBlockWriter.ReplaceLines(source, line, endLine, text, "\n", out _);

        // Assert
        Assert.Equal(expected, result);
    }
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests --filter "FullyQualifiedName~MarkdownPageOperationsTests|FullyQualifiedName~MarkdownBlockWriterTests"`
Expected: FAIL to compile.

- [ ] **Step 3: The writer helpers**

In `MarkdownBlockWriter.cs`:

```csharp
    /// <summary>
    /// Replaces the lines from <paramref name="line"/> to <paramref name="endLine"/> with <paramref name="text"/>, which has
    /// no line ending after its last line. An empty line keeps it apart from a line of text right before or after it.
    /// </summary>
    /// <remarks><paramref name="source"/> must have no lone carriage return, see <see cref="HasLoneCarriageReturn"/>.</remarks>
    internal static string ReplaceLines(string source, int line, int endLine, string text, string lineEnding, out MarkdownLineEdit edit)
    {
        var start = GetLineStart(source, line);
        var end = GetLineStart(source, endLine + 1);
        if (line > 1 && IsTextLine(source, GetLineStart(source, line - 1), start))
        {
            text = lineEnding + text;
        }

        if (IsTextLine(source, end, GetLineStart(source, endLine + 2)))
        {
            text += lineEnding;
        }

        // Lines that end the text without a line ending give the new last line none either.
        var replacement = end == source.Length && source.Length > 0 && source[^1] != '\n' ? text : text + lineEnding;
        edit = new MarkdownLineEdit(line, endLine - line + 1, text.AsSpan().Count('\n') + 1);
        return string.Concat(source.AsSpan(0, start), replacement, source.AsSpan(end));
    }

    /// <summary>Takes <paramref name="indentation"/> off the start of every line of <paramref name="text"/>, which has line feeds, that starts with it.</summary>
    internal static string RemoveIndentation(string text, string indentation)
    {
        var lines = text.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            if (lines[index].StartsWith(indentation, StringComparison.Ordinal))
            {
                lines[index] = lines[index][indentation.Length..];
            }
        }

        return string.Join('\n', lines);
    }
```

- [ ] **Step 4: The alignment of a changed last content**

In `MarkdownBlockAlignment.IsSameBlock`, replace the last statement:

```csharp
        // Around a change that took its last line of content, so it now ends before the change. The block that the lines were
        // inserted into ends on the new lines instead when no closing line ends it.
        return newBlock.EndLine >= oldBlock.Line &&
               (newBlock.EndLine < edit.Line ||
                (isOwnerOrAroundIt && !oldBlock.HasClosingLine && newBlock.EndLine < edit.InsertedEnd));
```

A removal passes no owner, so `isOwnerOrAroundIt` is false for it and removals keep their rule.

- [ ] **Step 5: The operations**

In `MarkdownPageOperations.cs`, extend the record:

```csharp
/// <param name="ResultBlockIndex">The index in the page of <paramref name="Write"/> of the block that the change moved or inserted, or -1.</param>
/// <param name="ResultTextLine">The first line in the page of <paramref name="Write"/> of the text that the change wrote, or 0.</param>
internal sealed record MarkdownStructureChange(
    MarkdownBlockWrite Write, MarkdownBlock? ChangedBlock, int ResultBlockIndex = -1, int ResultTextLine = 0);
```

Replace `TryInsertWidget` with `GetInsertLine` and the line-based `TryInsertWidget`:

```csharp
    /// <summary>
    /// Gets the line before which new content goes into the content of <paramref name="owner"/>, or of the page when it is
    /// null: <paramref name="beforeLine"/>, the first line of an item of that content, or else the end of the content, which
    /// is before the first slot block of the owner. Never before or inside the json block of the block around the line.
    /// </summary>
    public static int GetInsertLine(MarkdownPage page, MarkdownBlock? owner, int? beforeLine)
    {
        var siblings = owner?.Children ?? page.Blocks;
        var firstSlotBlock = siblings.FirstOrDefault(block => block.Kind == MarkdownBlockKind.Slot);
        var line = beforeLine
            ?? firstSlotBlock?.Line
            ?? (owner is null ? MarkdownBlockWriter.GetAppendLine(page.Source) : owner.HasClosingLine ? owner.EndLine : owner.EndLine + 1);
        return page.GetLineForNewBlock(line);
    }

    /// <summary>
    /// Inserts a new widget block into the content of <paramref name="owner"/>, or of the page when it is null, before the
    /// 1-based <paramref name="line"/> from <see cref="GetInsertLine"/>. The block takes the fence of the widget block at or
    /// after the line, or else of the last widget block, and closing lines unless the page has none.
    /// </summary>
    public static MarkdownStructureChange? TryInsertWidget(
        MarkdownPage page,
        IReadOnlyDictionary<int, IInterceptorSubject> blockSubjects,
        MarkdownBlock? owner,
        int line,
        string typeName,
        string? id,
        string configurationJson,
        out string? refusal)
    {
        if (!CanInsertOrRemoveBlocks(page, out refusal))
        {
            return null;
        }

        var source = page.Source;
        var siblings = owner?.Children ?? page.Blocks;
        var reference = siblings.FirstOrDefault(block => block.Kind == MarkdownBlockKind.Widget && block.Line >= line)
            ?? siblings.LastOrDefault(block => block.Kind == MarkdownBlockKind.Widget);
        var fenceLength = reference?.FenceLength ?? (owner is null ? MarkdownBlockFence.MinimumLength : owner.FenceLength + 1);
        var indentation = reference is not null ? MarkdownBlockWriter.GetIndentation(source, reference)
            : owner is not null ? MarkdownBlockWriter.GetIndentation(source, owner)
            : string.Empty;
        var lineEnding = MarkdownBlockWriter.GetLineEnding(source, reference?.HeadSpan.End ?? owner?.HeadSpan.End ?? 0);
        var hasClosingLine = !page.AllBlocks.Any(
            block => block.Kind is MarkdownBlockKind.Widget or MarkdownBlockKind.Slot && !block.HasClosingLine);

        var text = MarkdownBlockWriter.Indent(
            MarkdownBlockWriter.CreateBlock(typeName, id, configurationJson, fenceLength, lineEnding, hasClosingLine),
            indentation,
            lineEnding);
        var newSource = MarkdownBlockWriter.InsertBlockText(source, line, text, lineEnding, out var edit);
        var after = MarkdownPageParser.Parse(newSource);
        var map = MarkdownBlockAlignment.TryAlign(page, after, edit, expectedInsertedBlockCount: 1, owner, MarkdownBlockKind.Widget);
        if (map is null)
        {
            refusal = $"Inserting a block at line {line} would change other blocks of the page.";
            return null;
        }

        return new MarkdownStructureChange(
            new MarkdownBlockWrite(after, MapSubjects(blockSubjects, map), [], HasInsertedOrRemovedBlocks: true),
            owner,
            FindFirstBlockFrom(after, edit)?.Index ?? -1);
    }
```

Add the text operations:

```csharp
    /// <summary>Gets the Markdown of a text block: its lines without the indentation of its content, with line feeds.</summary>
    public static string GetText(MarkdownPage page, MarkdownTextBlock textBlock)
    {
        var lines = MarkdownBlockWriter.GetLines(page.Source, textBlock.Line, textBlock.EndLine).ReplaceLineEndings("\n");
        var indentation = GetContentIndentation(page, textBlock.Owner);
        return indentation.Length == 0 ? lines : MarkdownBlockWriter.RemoveIndentation(lines, indentation);
    }

    /// <summary>
    /// Replaces the lines of the text block with <paramref name="text"/>, or removes them when it is empty or white space.
    /// The text gets the line endings of the page and the indentation of its content, and an empty line keeps it apart from
    /// a line of text before or after it.
    /// </summary>
    /// <returns>Null with a refusal when the change would change other blocks of the page or the text around it.</returns>
    public static MarkdownStructureChange? TryReplaceText(
        MarkdownPage page, IReadOnlyDictionary<int, IInterceptorSubject> blockSubjects, MarkdownTextBlock textBlock, string text, out string? refusal)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return TryRemoveText(page, blockSubjects, textBlock, out refusal);
        }

        if (!CanInsertOrRemoveBlocks(page, out refusal))
        {
            return null;
        }

        var source = page.Source;
        var lineEnding = MarkdownBlockWriter.GetLineEnding(source, MarkdownBlockWriter.GetLineStart(source, textBlock.Line));
        var newSource = MarkdownBlockWriter.ReplaceLines(
            source, textBlock.Line, textBlock.EndLine, PrepareText(page, text, textBlock.Owner, lineEnding), lineEnding, out var edit);
        return AlignText(page, blockSubjects, newSource, edit, textBlock.Owner, $"Changing the text at line {textBlock.Line}", out refusal);
    }

    /// <summary>Removes the lines of the text block so that the text around it stays apart as before.</summary>
    /// <returns>Null with a refusal when the change would change other blocks of the page or the text around it.</returns>
    public static MarkdownStructureChange? TryRemoveText(
        MarkdownPage page, IReadOnlyDictionary<int, IInterceptorSubject> blockSubjects, MarkdownTextBlock textBlock, out string? refusal)
    {
        if (!CanInsertOrRemoveBlocks(page, out refusal))
        {
            return null;
        }

        var newSource = MarkdownBlockWriter.CutLines(page.Source, textBlock.Line, textBlock.EndLine, out var edit);
        var after = MarkdownPageParser.Parse(newSource);
        if (MarkdownBlockAlignment.TryAlign(page, after, edit, expectedInsertedBlockCount: 0, owner: null) is not { } map)
        {
            refusal = $"Removing the text at line {textBlock.Line} would change other blocks of the page.";
            return null;
        }

        return new MarkdownStructureChange(
            new MarkdownBlockWrite(after, MapSubjects(blockSubjects, map), [], HasInsertedOrRemovedBlocks: false), textBlock.Owner);
    }

    /// <summary>
    /// Inserts <paramref name="text"/> into the content of <paramref name="owner"/>, or of the page when it is null, before
    /// the 1-based <paramref name="line"/> from <see cref="GetInsertLine"/>, with empty lines around it like a block.
    /// </summary>
    /// <returns>Null with a refusal when the text is empty or the change would change other blocks of the page or the text around it.</returns>
    public static MarkdownStructureChange? TryInsertText(
        MarkdownPage page, IReadOnlyDictionary<int, IInterceptorSubject> blockSubjects, MarkdownBlock? owner, int line, string text, out string? refusal)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            refusal = "The text is empty.";
            return null;
        }

        if (!CanInsertOrRemoveBlocks(page, out refusal))
        {
            return null;
        }

        var source = page.Source;
        var lineEnding = MarkdownBlockWriter.GetLineEnding(source, MarkdownBlockWriter.GetLineStart(source, Math.Max(line - 1, 1)));
        var newSource = MarkdownBlockWriter.InsertBlockText(source, line, PrepareText(page, text, owner, lineEnding), lineEnding, out var edit);
        return AlignText(page, blockSubjects, newSource, edit, owner, $"Inserting text at line {line}", out refusal);
    }

    /// <summary>
    /// Gets whether text can go into the content of <paramref name="owner"/>, or of the page when it is null, before the
    /// 1-based <paramref name="line"/>: not right after a block without a closing line, which would take the text.
    /// </summary>
    public static bool CanInsertTextAt(MarkdownPage page, MarkdownBlock? owner, int line)
    {
        MarkdownBlock? previous = null;
        foreach (var sibling in owner?.Children ?? page.Blocks)
        {
            if (sibling.Line < line)
            {
                previous = sibling;
            }
        }

        return previous is not { HasClosingLine: false };
    }

    // The change of a text holds no block, and the Markdown document around it stays as it was.
    private static MarkdownStructureChange? AlignText(
        MarkdownPage page,
        IReadOnlyDictionary<int, IInterceptorSubject> blockSubjects,
        string newSource,
        MarkdownLineEdit edit,
        MarkdownBlock? owner,
        string description,
        out string? refusal)
    {
        var after = MarkdownPageParser.Parse(newSource);
        if (MarkdownBlockAlignment.TryAlign(page, after, edit, expectedInsertedBlockCount: 0, owner) is not { } map)
        {
            refusal = $"{description} would change other blocks of the page.";
            return null;
        }

        refusal = null;
        return new MarkdownStructureChange(
            new MarkdownBlockWrite(after, MapSubjects(blockSubjects, map), [], HasInsertedOrRemovedBlocks: false),
            owner,
            ResultTextLine: FindFirstTextLine(newSource, edit));
    }

    // The text as lines of the page: its line endings, the indentation of its content, and no empty lines around it.
    private static string PrepareText(MarkdownPage page, string text, MarkdownBlock? owner, string lineEnding)
    {
        var lines = text.ReplaceLineEndings("\n").Trim('\n').TrimEnd();
        return MarkdownBlockWriter.Indent(lines.ReplaceLineEndings(lineEnding), GetContentIndentation(page, owner), lineEnding);
    }

    // The content of a block is indented like its head line, and the page has none.
    private static string GetContentIndentation(MarkdownPage page, MarkdownBlock? owner) =>
        owner is null ? string.Empty : MarkdownBlockWriter.GetIndentation(page.Source, owner);

    // The first line of the change that is not empty: where the new text starts.
    private static int FindFirstTextLine(string source, MarkdownLineEdit edit)
    {
        for (var line = edit.Line; line < edit.InsertedEnd; line++)
        {
            if (!string.IsNullOrWhiteSpace(MarkdownBlockWriter.GetLines(source, line, line)))
            {
                return line;
            }
        }

        return 0;
    }
```

Update the two calls in `MarkdownFile` that pass a block to `TryInsertWidget` so that the solution builds; Task 14 replaces them. For now: `MarkdownPageOperations.TryInsertWidget(page, subjects, owner, MarkdownPageOperations.GetInsertLine(page, owner, beforeBlock?.Line), typeName, name, configurationJson, out var refusal)`.

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests`
Expected: PASS (the API snapshot does not change: `MarkdownPageOperations` is internal).

- [ ] **Step 7: Commit**

```bash
git add src/HomeBlaze
git commit -m "feat: replace, remove and insert the text blocks of a page"
```

---

### Task 14: Text queries and edits of `MarkdownFile`

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.Storage/Files/MarkdownFile.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Storage.Tests/VerifyChecksTests.PublicApi.verified.txt`
- Test: `src/HomeBlaze/HomeBlaze.Storage.Tests/Pages/MarkdownStructureEditTests.cs`, `MarkdownFileWriteTests.cs`

**Interfaces:**
- Consumes: Task 13 operations, `MarkdownParseResult.TextBlocks` (Task 12).
- Produces:
  - `public string? MarkdownFile.GetTextBlockText(HtmlSegment segment)`.
  - `public Task<MarkdownEditResult> MarkdownFile.ReplaceTextBlockAsync(HtmlSegment segment, string expectedText, string newText, CancellationToken cancellationToken)`: empty or white space `newText` removes the block; refused with "The text changed since you opened it." when the segment starts no editable text block of the snapshot or its text differs from `expectedText`.
  - `public Task<MarkdownEditResult> MarkdownFile.InsertTextAsync(IInterceptorSubject content, IInterceptorSubject? before, string text, CancellationToken cancellationToken)`.
  - `public bool MarkdownFile.CanInsertTextAt(IInterceptorSubject content, IInterceptorSubject? before)`.
  - `InsertWidgetAsync` accepts as `before` the `HtmlSegment` of a text block, and refuses a block that is not in the content ("The block to insert before is not in this content.").
  - `MarkdownEditResult.Subject` of a text edit is the `HtmlSegment` that starts the written text, after the read inside the write.

- [ ] **Step 1: Write the failing tests**

`MarkdownStructureEditTests` (add `using HomeBlaze.Storage.Internal;`):

```csharp
    private static readonly string TextPage = $"# Title\n\nOld text.\n\n:: {LeafType}#leaf\n::\n";

    private static HtmlSegment FindText(MarkdownFile page, string text) =>
        page.Children.Values.OfType<HtmlSegment>().Single(segment => segment.Start == HtmlSegmentStart.Text && segment.Html.Contains(text));

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
```

`MarkdownFileWriteTests`:

```csharp
    [Fact]
    public async Task WhenAParagraphOfAPageOfTheStorageIsReplaced_ThenTheResultNamesTheSegmentOfTheNewText()
    {
        // Arrange
        WriteFile("Notes.md", "# Notes\n\nOld text.\n");
        var storage = await ConnectAsync();
        var markdown = Assert.IsType<MarkdownFile>(storage.Children["Notes.md"]);
        var segment = markdown.Children.Values.OfType<HtmlSegment>().Single(candidate => candidate.Html.Contains("Old text."));

        // Act
        var result = await markdown.ReplaceTextBlockAsync(segment, "Old text.", "New text.", CancellationToken.None);

        // Assert
        Assert.Equal("# Notes\n\nNew text.\n", ReadFile("Notes.md"));
        var written = Assert.IsType<HtmlSegment>(result.Subject);
        Assert.Contains("New text.", written.Html);
        Assert.Contains(written, markdown.Children.Values);
    }
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests --filter "FullyQualifiedName~MarkdownStructureEditTests|FullyQualifiedName~MarkdownFileWriteTests"`
Expected: FAIL to compile.

- [ ] **Step 3: Implement**

In `MarkdownFile.cs` (add `using HomeBlaze.Storage.Internal;` if missing), add the refusal:

```csharp
    private const string TextChangedRefusal = "The text changed since you opened it.";
```

Replace `InsertWidgetAsync`:

```csharp
    /// <summary>
    /// Inserts a block for <paramref name="widget"/>, with its configuration and <paramref name="name"/> after <c>#</c>,
    /// into <paramref name="content"/>: this page or a fragment of one of its blocks. It goes before <paramref name="before"/>,
    /// a block of that content or the <see cref="HtmlSegment"/> of one of its text blocks, or after the content when that is
    /// null. The page then creates its own subject for it.
    /// Refused when the file differs from the text the page last read; the page then reads it.
    /// </summary>
    /// <exception cref="InvalidOperationException">The call comes from work on the worker of the storage or from within a write of this page.</exception>
    public Task<MarkdownEditResult> InsertWidgetAsync(
        IInterceptorSubject content, IInterceptorSubject? before, IInterceptorSubject widget, string? name, CancellationToken cancellationToken)
    {
        var typeName = widget.GetType().FullName!;
        var configurationJson = MarkdownBlockConfiguration.Serialize(_serializer, widget, block: null);
        return EditStructureAsync(
            snapshot => TryGetInsertPoint(snapshot, content, before, out var owner, out var line, out var refusal)
                ? (MarkdownPageOperations.TryInsertWidget(
                    snapshot.Page!, snapshot.BlockSubjects, owner, line, typeName, name, configurationJson, out refusal), refusal)
                : (null, refusal),
            cancellationToken);
    }
```

Add the text members and the insert point:

```csharp
    /// <summary>
    /// Gets the Markdown of the text block that <paramref name="segment"/> starts, without the indentation of its content and
    /// with line feeds, or null when the segment starts no text block that can be edited.
    /// </summary>
    public string? GetTextBlockText(HtmlSegment segment)
    {
        var snapshot = _parseResult;
        return snapshot.Page is { } page && snapshot.TextBlocks.TryGetValue(segment, out var textBlock) && textBlock.IsEditable
            ? MarkdownPageOperations.GetText(page, textBlock)
            : null;
    }

    /// <summary>
    /// Replaces the text block that <paramref name="segment"/> starts with <paramref name="newText"/>, or removes it when that
    /// is empty or white space, and saves the page. Refused when the segment starts no text block that can be edited any more
    /// or its text is no longer <paramref name="expectedText"/>, as <see cref="GetTextBlockText"/> returned it, and when the
    /// file differs from the text the page last read; the page then reads it.
    /// </summary>
    /// <exception cref="InvalidOperationException">The call comes from work on the worker of the storage or from within a write of this page.</exception>
    public Task<MarkdownEditResult> ReplaceTextBlockAsync(
        HtmlSegment segment, string expectedText, string newText, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expectedText);
        ArgumentNullException.ThrowIfNull(newText);
        var expected = expectedText.ReplaceLineEndings("\n");
        return EditStructureAsync(
            snapshot =>
            {
                if (!snapshot.TextBlocks.TryGetValue(segment, out var textBlock) || !textBlock.IsEditable ||
                    MarkdownPageOperations.GetText(snapshot.Page!, textBlock) != expected)
                {
                    return (null, TextChangedRefusal);
                }

                return (MarkdownPageOperations.TryReplaceText(snapshot.Page!, snapshot.BlockSubjects, textBlock, newText, out var refusal), refusal);
            },
            cancellationToken);
    }

    /// <summary>
    /// Inserts <paramref name="text"/>, Markdown without blocks, into <paramref name="content"/> before <paramref name="before"/>,
    /// as <see cref="InsertWidgetAsync"/> places a widget, and saves the page.
    /// </summary>
    /// <exception cref="InvalidOperationException">The call comes from work on the worker of the storage or from within a write of this page.</exception>
    public Task<MarkdownEditResult> InsertTextAsync(
        IInterceptorSubject content, IInterceptorSubject? before, string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        return EditStructureAsync(
            snapshot => TryGetInsertPoint(snapshot, content, before, out var owner, out var line, out var refusal)
                ? (MarkdownPageOperations.TryInsertText(snapshot.Page!, snapshot.BlockSubjects, owner, line, text, out refusal), refusal)
                : (null, refusal),
            cancellationToken);
    }

    /// <summary>
    /// Gets whether text can go into <paramref name="content"/> before <paramref name="before"/>, or at its end: not right after
    /// a block without a closing line, which would take the text.
    /// </summary>
    public bool CanInsertTextAt(IInterceptorSubject content, IInterceptorSubject? before)
    {
        var snapshot = _parseResult;
        return snapshot.Page is { } page &&
               TryGetInsertPoint(snapshot, content, before, out var owner, out var line, out _) &&
               MarkdownPageOperations.CanInsertTextAt(page, owner, line);
    }

    // The block whose content `content` is, or null for the page, and the line before which new content goes: the first line
    // of `before`, a block or a text block of that content, or the end of the content.
    private bool TryGetInsertPoint(
        MarkdownParseResult snapshot,
        IInterceptorSubject content,
        IInterceptorSubject? before,
        out MarkdownBlock? owner,
        out int line,
        out string? refusal)
    {
        var page = snapshot.Page!;
        owner = null;
        line = 0;
        if (!ReferenceEquals(content, this) &&
            (owner = MarkdownPageOperations.FindContentBlock(page, snapshot.BlockSubjects, content)) is null)
        {
            refusal = "The content is not on this page.";
            return false;
        }

        int? beforeLine = null;
        if (before is HtmlSegment segment)
        {
            if (!snapshot.TextBlocks.TryGetValue(segment, out var textBlock) || !ReferenceEquals(textBlock.Owner, owner))
            {
                refusal = StructureChangedRefusal;
                return false;
            }

            beforeLine = textBlock.Line;
        }
        else if (before is not null)
        {
            if (FindOwnBlock(page, snapshot.BlockSubjects, before) is not { } beforeBlock)
            {
                refusal = NoBlockRefusal;
                return false;
            }

            MarkdownPageOperations.FindSiblings(page, beforeBlock, out var parent);
            if (!ReferenceEquals(parent, owner))
            {
                refusal = "The block to insert before is not in this content.";
                return false;
            }

            beforeLine = beforeBlock.Line;
        }

        line = MarkdownPageOperations.GetInsertLine(page, owner, beforeLine);
        refusal = null;
        return true;
    }
```

Extend `FindResultSubject`:

```csharp
        if (change.ResultBlockIndex >= 0)
        {
            return snapshot.BlockSubjects.GetValueOrDefault(change.ResultBlockIndex);
        }

        if (change.ResultTextLine > 0)
        {
            foreach (var (segment, textBlock) in snapshot.TextBlocks)
            {
                if (textBlock.Line == change.ResultTextLine)
                {
                    return segment;
                }
            }
        }

        return null;
```

`CanInsertTextAt` and `GetTextBlockText` read the snapshot like `IsContentOfPage`; none of them writes.

- [ ] **Step 4: Run the tests and accept the API snapshot**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests`
Expected: PASS after accepting `VerifyChecksTests.PublicApi` (the four new members of `MarkdownFile`).

- [ ] **Step 5: Commit**

```bash
git add src/HomeBlaze
git commit -m "feat: query, replace and insert the text of a page through its file"
```

---

### Task 15: Selectable text blocks with Edit and Delete

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Components/ITextBlockWidget.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Components/PageBlockActions.cs`, `PageStructureEditor.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Components/SubjectComponent.razor`
- Create: `src/HomeBlaze/HomeBlaze.Storage.Blazor/Files/MarkdownTextWidget.razor`, `src/HomeBlaze/HomeBlaze.Storage.Blazor/Files/MarkdownTextDialog.razor`
- Modify: `src/HomeBlaze/HomeBlaze.Storage.Blazor/Files/MarkdownContentView.razor`
- Modify: `src/HomeBlaze/HomeBlaze.Storage.Blazor/Files/MarkdownPageStructureEditor.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Storage.Blazor/Files/MarkdownFilePageComponent.razor.css`
- Modify: `src/HomeBlaze/HomeBlaze.Components.Tests/VerifyChecksTests.PublicApi.verified.txt`
- Create: `src/HomeBlaze/HomeBlaze.E2E.Tests/TestData/Demo/TextEditing.md`, `src/HomeBlaze/HomeBlaze.E2E.Tests/InlineTextEditingTests.cs`

**Interfaces:**
- Consumes: `MarkdownFile.GetTextBlockText`, `ReplaceTextBlockAsync` (Task 14), `HtmlSegment.Start` (Task 12), `WidgetSelection.SelectSubject` (Task 5).
- Produces:
  - `public interface ITextBlockWidget` (marker).
  - `public readonly record struct PageBlockActions(bool CanDelete, bool CanMoveUp, bool CanMoveDown, bool CanEditText = false)`.
  - `public virtual Task PageStructureEditor.EditTextAsync(IInterceptorSubject subject)` (default does nothing).
  - `MarkdownTextDialog.ShowAsync(IDialogService dialogService, string title, string text, Func<string, Task<string?>> save)`: the save returns the refusal, or null when it saved.
  - Test ids: `markdown-text-input`, `markdown-text-save`, `markdown-text-cancel`, `markdown-text-refusal`; text wrappers are `.subject-component-wrapper:has(> .markdown-text)`.

- [ ] **Step 1: The demo page and the failing E2E tests**

`TestData/Demo/TextEditing.md`:

```markdown
---
title: Text Editing Demo
---

# Text Editing Demo

First paragraph on the page.

:: HomeBlaze.Components.Card Title="Text Card"
Card body text.
::

Last paragraph on the page.
```

`InlineTextEditingTests.cs`:

```csharp
using System.Text.RegularExpressions;
using HomeBlaze.E2E.Tests.Infrastructure;
using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for the editing of the text of a markdown page in inline edit mode: its blocks of text are selected, edited in
/// a dialog, deleted and inserted.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class InlineTextEditingTests
{
    private const int Timeout = 30000;
    private const string PageName = "TextEditing.md";

    // The server writes the page in its output folder, so a run that ends with a change of the page would change the
    // baseline of the next run if it were read from there.
    private const string Content =
        "---\n"
        + "title: Text Editing Demo\n"
        + "---\n"
        + "\n"
        + "# Text Editing Demo\n"
        + "\n"
        + "First paragraph on the page.\n"
        + "\n"
        + ":: HomeBlaze.Components.Card Title=\"Text Card\"\n"
        + "Card body text.\n"
        + "::\n"
        + "\n"
        + "Last paragraph on the page.\n";

    private readonly PlaywrightFixture _fixture;

    public InlineTextEditingTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task WhenAParagraphOnThePageIsEdited_ThenTheFileHasTheNewTextAndItStaysSelected()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();
        var actionBar = await GetTextWrapper(page, "First paragraph").SelectWidgetAsync();

        // Act
        await actionBar.GetByTestId("edit-subject-button").ClickAsync();
        var input = page.GetByTestId("markdown-text-input");
        await Assertions.Expect(input).ToHaveValueAsync("First paragraph on the page.", new() { Timeout = Timeout });
        await input.FillAsync("Changed *paragraph*.");
        await page.GetByTestId("markdown-text-save").ClickAsync();

        // Assert
        await _fixture.WaitForDemoPageTextAsync(
            PageName,
            text => text.Contains("# Text Editing Demo\n\nChanged *paragraph*.\n\n:: HomeBlaze.Components.Card"),
            "The paragraph was not changed.");
        await Assertions.Expect(GetTextWrapper(page, "Changed paragraph")).ToHaveClassAsync(new Regex(@"\bselected\b"), new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenAParagraphInACardBodyIsEdited_ThenOnlyTheCardBodyChanges()
    {
        // Arrange: the first click selects the card, and the text of its body is selectable once the card is selected.
        var page = await OpenPageInInlineModeAsync();
        await page.GetCard("Text Card").Locator(".mud-card-header").SelectWidgetAsync();
        var actionBar = await GetTextWrapper(page, "Card body text").SelectWidgetAsync();

        // Act
        await actionBar.GetByTestId("edit-subject-button").ClickAsync();
        await page.GetByTestId("markdown-text-input").FillAsync("Changed body.");
        await page.GetByTestId("markdown-text-save").ClickAsync();

        // Assert
        await _fixture.WaitForDemoPageTextAsync(
            PageName,
            text => text.Contains(":: HomeBlaze.Components.Card Title=\"Text Card\"\nChanged body.\n::\n") &&
                    text.Contains("\nFirst paragraph on the page.\n"),
            "The card body was not changed.");
    }

    [Fact]
    public async Task WhenATextIsDeleted_ThenItIsRemovedFromTheFile()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();
        var actionBar = await GetTextWrapper(page, "Last paragraph").SelectWidgetAsync();

        // Act: the bar has a button named Delete as well, so the one of the dialog is looked up in the dialog.
        await actionBar.GetByTestId("delete-block-button").ClickAsync();
        await page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "Delete", Exact = true }).ClickAsync();

        // Assert
        await _fixture.WaitForDemoPageTextAsync(
            PageName, text => !text.Contains("Last paragraph") && text.EndsWith("::\n", StringComparison.Ordinal), "The text was not deleted.");
    }

    [Fact]
    public async Task WhenTheTextChangedWhileItsDialogWasOpen_ThenTheSaveIsRefusedAndTheDialogKeepsTheInput()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();
        var actionBar = await GetTextWrapper(page, "Last paragraph").SelectWidgetAsync();
        await actionBar.GetByTestId("edit-subject-button").ClickAsync();
        var input = page.GetByTestId("markdown-text-input");
        await Assertions.Expect(input).ToHaveValueAsync("Last paragraph on the page.", new() { Timeout = Timeout });
        await _fixture.WriteDemoPageAsync(PageName, Content.Replace("Last paragraph on the page.", "Changed elsewhere."));

        // Act
        await input.FillAsync("Mine.");
        await page.GetByTestId("markdown-text-save").ClickAsync();

        // Assert
        await Assertions.Expect(page.GetByTestId("markdown-text-refusal")).ToContainTextAsync("changed", new() { Timeout = Timeout });
        await Assertions.Expect(input).ToHaveValueAsync("Mine.");
        var text = (await _fixture.ReadFileAsync(_fixture.GetDemoPagePath(PageName))).Replace("\r\n", "\n");
        Assert.Contains("Changed elsewhere.", text);
        Assert.DoesNotContain("Mine.", text);
    }

    [Fact]
    public async Task WhenThePageIsViewed_ThenItsTextIsNotWrapped()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();

        // Act
        await page.SelectEditModeAsync("View", Timeout);

        // Assert
        await Assertions.Expect(page.Locator(".markdown-text")).ToHaveCountAsync(0, new() { Timeout = Timeout });
    }

    private async Task<IPage> OpenPageInInlineModeAsync()
    {
        await _fixture.WriteDemoPageAsync(PageName, Content);
        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync($"{_fixture.ServerAddress}pages/Demo/{PageName}");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        // The edit menu is prerendered, and a click before the circuit is connected is lost.
        await page.WaitUntilInteractiveAsync(Timeout);
        await page.SwitchToInlineModeAsync();
        return page;
    }

    private static ILocator GetTextWrapper(IPage page, string text) =>
        page.Locator(".subject-component-wrapper.selectable:has(> .markdown-text)").Filter(new() { HasText = text });
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.E2E.Tests --filter "FullyQualifiedName~InlineTextEditingTests"`
Expected: FAIL: no text wrapper exists (the View test passes already and guards View mode).

- [ ] **Step 3: The component library**

`ITextBlockWidget.cs`:

```csharp
namespace HomeBlaze.Components;

/// <summary>
/// Marks the widget component of a block of text of a page, which can be selected while the page is edited inline although
/// its subject has no configuration.
/// </summary>
public interface ITextBlockWidget;
```

`PageBlockActions.cs`:

```csharp
namespace HomeBlaze.Components;

/// <summary>The structure actions that the block of a widget allows.</summary>
/// <param name="CanDelete">Whether the block can be deleted.</param>
/// <param name="CanMoveUp">Whether the block can move past the item before it.</param>
/// <param name="CanMoveDown">Whether the block can move past the item after it.</param>
/// <param name="CanEditText">Whether the block is text that <see cref="PageStructureEditor.EditTextAsync"/> edits.</param>
public readonly record struct PageBlockActions(bool CanDelete, bool CanMoveUp, bool CanMoveDown, bool CanEditText = false);
```

Add to `PageStructureEditor`:

```csharp
    /// <summary>Asks for the new text of the text block <paramref name="subject"/>, saves it and selects it. Does nothing by default.</summary>
    public virtual Task EditTextAsync(IInterceptorSubject subject) => Task.CompletedTask;
```

In `SubjectComponent.razor`:
- Add the field and set it where the subject changes in `OnParametersSet` (inside `if (Subject != _previousSubject)`):

```csharp
    // Whether the widget of the subject is a block of text of its page, which can be selected without a configuration.
    private bool _isTextBlock;
```

```csharp
                _isTextBlock = Subject is not null &&
                               ComponentRegistry.GetComponent(Subject.GetType(), Type)?.ComponentType is { } componentType &&
                               typeof(ITextBlockWidget).IsAssignableFrom(componentType);
```

- `IsSelectable` becomes:

```csharp
    // Every block of a page is configurable, also one without configuration properties of its own, and a block of text is
    // selectable as well. The content of a slot, such as the body of a card, and an expression in the text are not.
    internal bool IsSelectable =>
        IsEditing &&
        IsSelectionEnabled &&
        Type == SubjectComponentType.Widget &&
        Selection != null &&
        (Subject is IConfigurable || _isTextBlock || HasEditComponent);
```

- In the markup, compute the actions before the edit action:

```razor
            @{
                var selectableParent = isSelected ? SelectableAncestor : null;
                var structureEditor = isSelected ? StructureEditor : null;
                var actions = structureEditor?.GetActions(Subject) ?? default;
                var hasEditAction = isSelected && (actions.CanEditText || HasEditComponent);
                var listSlots = structureEditor?.GetListSlots(Subject) ?? [];
            }
```

- `OpenEditDialog` edits text through the editor:

```csharp
    private async Task OpenEditDialog()
    {
        if (Subject == null)
            return;

        if (StructureEditor is { } editor && editor.GetActions(Subject).CanEditText)
        {
            await editor.EditTextAsync(Subject);
            return;
        }

        var title = SubjectNames.TryGetTitle(Subject, _labelProperty);
        await SubjectEditDialog.ShowAsync(DialogService, ComponentRegistry, Subject, SubjectEditDialog.GetTitle(null, title));
    }
```

- In `OnKeyDown`, replace `else if (HasEditComponent)` with `else if (HasEditComponent || StructureEditor?.GetActions(Subject!).CanEditText == true)`.

- [ ] **Step 4: The text widget and the dialog**

`MarkdownTextWidget.razor`:

```razor
@using HomeBlaze.Components
@using HomeBlaze.Components.Abstractions
@using HomeBlaze.Components.Abstractions.Attributes
@using HomeBlaze.Storage.Internal
@using Namotion.Interceptor

@attribute [SubjectComponent(SubjectComponentType.Widget, typeof(HtmlSegment))]
@implements ISubjectComponent
@implements ITextBlockWidget

@* A block of text of a page that is edited inline, with the HTML and the values of the expressions of the block, which the
   content joined. The class is what the page styles use, see MarkdownFilePageComponent.razor.css. *@
<div class="markdown-text">@((MarkupString)(Html ?? string.Empty))</div>

@code {
    [Parameter]
    public IInterceptorSubject? Subject { get; set; }

    /// <summary>The markup of the text block.</summary>
    [Parameter]
    public string? Html { get; set; }
}
```

`MarkdownTextDialog.razor`:

```razor
@using MudBlazor

@* The Markdown of a block of text in a field that grows with it. A refusal, such as text that changed meanwhile, shows here
   and keeps what was typed. *@
<MudDialog>
    <DialogContent>
        @if (_refusal is not null)
        {
            <MudAlert Severity="Severity.Warning" Dense="true" Class="mb-2" data-testid="markdown-text-refusal">@_refusal</MudAlert>
        }

        <MudTextField T="string"
                      @bind-Value="_text"
                      Label="Markdown"
                      Variant="Variant.Outlined"
                      AutoGrow="true"
                      Lines="6"
                      MaxLines="24"
                      AutoFocus="true"
                      Immediate="true"
                      Class="markdown-text-field"
                      data-testid="markdown-text-input" />
    </DialogContent>
    <DialogActions>
        <MudButton OnClick="Cancel" data-testid="markdown-text-cancel">Cancel</MudButton>
        <MudButton Color="Color.Primary" Variant="Variant.Filled" Disabled="_isSaving" OnClick="SaveAsync" data-testid="markdown-text-save">Save</MudButton>
    </DialogActions>
</MudDialog>

<style>
    .markdown-text-field textarea {
        font-family: monospace;
    }
</style>

@code {
    [CascadingParameter]
    private IMudDialogInstance Dialog { get; set; } = default!;

    /// <summary>The Markdown that the field starts with.</summary>
    [Parameter]
    public string Text { get; set; } = string.Empty;

    /// <summary>Saves the text and returns why it was not saved, or null when it was.</summary>
    [Parameter]
    public Func<string, Task<string?>> Save { get; set; } = default!;

    private string _text = string.Empty;
    private string? _refusal;
    private bool _isSaving;

    protected override void OnInitialized() => _text = Text;

    private void Cancel() => Dialog.Cancel();

    private async Task SaveAsync()
    {
        _isSaving = true;
        try
        {
            _refusal = await Save(_text);
            if (_refusal is null)
            {
                Dialog.Close(DialogResult.Ok(true));
            }
        }
        finally
        {
            _isSaving = false;
        }
    }

    /// <summary>Shows the dialog until the text is saved or the dialog is canceled.</summary>
    public static async Task ShowAsync(IDialogService dialogService, string title, string text, Func<string, Task<string?>> save)
    {
        var parameters = new DialogParameters<MarkdownTextDialog>
        {
            { dialog => dialog.Text, text },
            { dialog => dialog.Save, save }
        };
        var options = new DialogOptions { MaxWidth = MaxWidth.Medium, FullWidth = true, CloseOnEscapeKey = true };
        var reference = await dialogService.ShowAsync<MarkdownTextDialog>(title, parameters, options);
        await reference.Result;
    }
}
```

If MudBlazor's `MudTextField` puts `data-testid` on its root rather than on the `textarea`, use `InputProps`/`UserAttributes` as `SubjectSetupDialog` does for `create-name-input`, so that `GetByTestId("markdown-text-input")` finds the `textarea`.

- [ ] **Step 5: Group the text of a content**

In `MarkdownContentView.razor`, replace the markup from the first `@{` down to `@code {` with:

```razor
@{
    var showsInsertLines = ShowsInsertLines();
    var contentName = showsInsertLines ? GetContentName() : null;
    var position = 0;
}
@foreach (var segment in GetRenderSegments(groupsText: IsGroupingText()))
{
    var item = segment.Widget ?? segment.Start;
    @if (showsInsertLines && item is not null)
    {
        position++;
        <BlockInsertLine Label="@($"Insert at position {position} of {contentName}")"
                         OnInsert="@(() => StructureEditor!.InsertWidgetAsync(Content!, item))" />
    }

    @if (segment.Widget is not null)
    {
        @* Keyed, so that the state of a component stays with its subject when a reload inserts or removes a block. *@
        <SubjectComponent @key="segment.Widget" Subject="@segment.Widget" Type="SubjectComponentType.Widget" />
    }
    else if (segment.Start is { Start: HtmlSegmentStart.Text } text)
    {
        @* A block of text is selectable like a row of a selected list: once its content is at the selection. *@
        <SubjectComponent @key="text"
                          Subject="@text"
                          Type="SubjectComponentType.Widget"
                          IsSelectionEnabled="@_isAtSelection"
                          AdditionalParameters="@(new Dictionary<string, object?> { ["Html"] = segment.Html })" />
    }
    else if (segment.Html is not null)
    {
        @((MarkupString)segment.Html)
    }
}
@if (showsInsertLines)
{
    <BlockInsertLine Label="@($"Insert at the end of {contentName}")"
                     OnInsert="@(() => StructureEditor!.InsertWidgetAsync(Content!, null))" />
}
```

Replace `RenderSegment` and `GetRenderSegments` in `@code`:

```csharp
    // Html is the markup of a stretch of HTML and expressions; Start is the segment that starts it as a text block or other
    // content, when the content groups its text; Widget is a block.
    private record RenderSegment(string? Html, IInterceptorSubject? Widget, HtmlSegment? Start = null);

    // Runs on every render and keeps nothing: the values of expressions are read here so that the render records them, and
    // a reload replaces the dictionary and its subjects. Without grouping, as on a viewed page, the HTML and the expressions
    // between two widgets are one piece of markup, so that an expression can sit inside an HTML attribute.
    private List<RenderSegment> GetRenderSegments(bool groupsText)
    {
        var result = new List<RenderSegment>();
        if (Children is null)
            return result;

        StringBuilder? builder = null;
        HtmlSegment? start = null;

        foreach (var child in Children.Values)
        {
            if (child is HtmlSegment html)
            {
                if (groupsText && html.Start != HtmlSegmentStart.None)
                {
                    AddMarkup(result, builder, start);
                    start = html;
                }

                builder ??= new StringBuilder();
                builder.Append(html.Html);
            }
            else if (child is RenderExpression expression)
            {
                builder ??= new StringBuilder();
                if (expression.Error is not null)
                {
                    builder.Append("<span class=\"mud-error-text\" data-testid=\"expression-error\">");
                    builder.Append(HttpUtility.HtmlEncode(expression.Error));
                    builder.Append("</span>");
                }
                else
                {
                    builder.Append(HttpUtility.HtmlEncode(expression.GetText(TimeZone)));
                }
            }
            else
            {
                AddMarkup(result, builder, start);
                start = null;
                result.Add(new RenderSegment(null, child));
            }
        }

        AddMarkup(result, builder, start);
        return result;
    }

    // Adds the markup collected so far and empties the builder.
    private static void AddMarkup(List<RenderSegment> result, StringBuilder? builder, HtmlSegment? start)
    {
        if (builder is { Length: > 0 } || start is not null)
        {
            result.Add(new RenderSegment(builder?.ToString() ?? string.Empty, null, start));
            builder?.Clear();
        }
    }

    // Text is selectable in Inline mode, except in the content of a grid cell or a canvas node, which must stay the only
    // block of its place to fill it.
    private bool IsGroupingText() =>
        IsEditing && Selection is not null && StructureEditor is not null && Content is not null &&
        !ReferenceEquals(Content, PlaceContent);
```

Without grouping, `start` stays null and `AddMarkup` adds exactly what the loop added before, so View mode renders the same markup (the golden test of Task 11 checks it).

In `MarkdownFilePageComponent.razor.css`, at the end:

```css
/* A block of text that is edited inline stands in a wrapper (see MarkdownTextWidget). On the page the wrapper takes no
   margin, so that the margins of the text apply as on the viewed page. In a container the wrapper has the margin of a
   block, which the margin of its text collapses into, as on the viewed page. */
.markdown-content > ::deep .subject-component-wrapper:has(> .markdown-text) {
    margin: 0;
}

/* The title of a page that is edited inline makes room for the button of the page menu, as the title does above. */
.markdown-content > ::deep .subject-component-wrapper > .markdown-text > h1:first-child {
    padding-right: 3rem;
}
```

- [ ] **Step 6: The editor**

In `MarkdownPageStructureEditor.cs` (add `using HomeBlaze.Storage.Internal;`):

```csharp
    public override PageBlockActions GetActions(IInterceptorSubject subject)
    {
        if (subject is HtmlSegment segment)
        {
            return _file.GetTextBlockText(segment) is null
                ? default
                : new PageBlockActions(CanDelete: true, CanMoveUp: false, CanMoveDown: false, CanEditText: true);
        }

        var actions = _file.GetBlockActions(subject, _isInvisible);
        return new PageBlockActions(actions.CanDelete, actions.CanMoveUp, actions.CanMoveDown);
    }

    public override async Task EditTextAsync(IInterceptorSubject subject)
    {
        if (subject is not HtmlSegment segment || _file.GetTextBlockText(segment) is not { } text)
        {
            return;
        }

        await MarkdownTextDialog.ShowAsync(
            _dialogService,
            "Edit text",
            text,
            newText => SaveTextAsync(() => _file.ReplaceTextBlockAsync(segment, text, newText, CancellationToken.None)));
    }

    // The dialog shows a refusal and keeps the text, so that nothing that was typed is lost.
    private async Task<string?> SaveTextAsync(Func<Task<MarkdownEditResult>> edit)
    {
        var result = await RunAsync(edit, reportsRefusal: false);
        return result switch
        {
            null => "The page could not be saved.",
            { IsWritten: false } refused => refused.Refusal,
            _ => null
        };
    }
```

At the start of `DeleteAsync`:

```csharp
        if (subject is HtmlSegment segment)
        {
            if (_file.GetTextBlockText(segment) is not { } text)
            {
                return;
            }

            var isConfirmed = await _dialogService.ShowMessageBoxAsync("Delete text", "Delete this text?", yesText: "Delete", cancelText: "Cancel");
            if (isConfirmed == true)
            {
                await RunAsync(() => _file.ReplaceTextBlockAsync(segment, text, string.Empty, CancellationToken.None));
            }

            return;
        }
```

The text that the dialog showed is the expected text, so a delete or an edit of text that changed meanwhile is refused by the file.

- [ ] **Step 7: Run all E2E tests and adapt the ones that meet selectable text**

Run: `dotnet build src/Namotion.Interceptor.slnx`, then `dotnet test src/HomeBlaze/HomeBlaze.Components.Tests` and accept the public API snapshot (`ITextBlockWidget`, `PageBlockActions.CanEditText`, `PageStructureEditor.EditTextAsync`).

Run: `dotnet test src/HomeBlaze/HomeBlaze.E2E.Tests`
Expected: the new tests PASS. A test of an earlier phase can now meet a selectable block of text:
- A test that clicks text of a page to clear the selection: clear it with `await page.Keyboard.PressAsync("Escape")` instead (Pages.razor clears the selection on Escape outside dialogs).
- A test that counts `.subject-component-wrapper.selectable` or takes `.First` of it: exclude text with `:not(:has(> .markdown-text))`.
- A test that counts the insert lines of a page or picks one by its index: lines now stand before text as well; pick the line by `GetInsertLineBefore` or by its name.
Change only the locators, not what a test checks. `ViewHtmlGoldenTests` must stay green unchanged.

- [ ] **Step 8: Commit**

```bash
git add src/HomeBlaze
git commit -m "feat: select, edit and delete the blocks of text of a page inline"
```

---

### Task 16: Insert text from the plus line

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.Components/Components/BlockInsertLine.razor`
- Modify: `src/HomeBlaze/HomeBlaze.Components/PageStructureEditor.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Storage.Blazor/Files/MarkdownContentView.razor`
- Modify: `src/HomeBlaze/HomeBlaze.Storage.Blazor/Files/MarkdownPageStructureEditor.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Components.Tests/VerifyChecksTests.PublicApi.verified.txt`
- Test: `src/HomeBlaze/HomeBlaze.E2E.Tests/InlineTextEditingTests.cs`, `InlineStructureEditingTests.cs` (`OpenWidgetDialogAtAsync`)

**Interfaces:**
- Consumes: `MarkdownFile.InsertTextAsync`, `CanInsertTextAt` (Task 14), `MarkdownTextDialog` (Task 15).
- Produces:
  - `BlockInsertLine` parameters `OnInsertWidget`, `OnInsertText` (the text item shows only with a delegate) and `Label`; test ids `insert-widget-item`, `insert-text-item`.
  - `public virtual bool PageStructureEditor.CanInsertText(IInterceptorSubject content, IInterceptorSubject? before)` (default false) and `public virtual Task PageStructureEditor.InsertTextAsync(IInterceptorSubject content, IInterceptorSubject? before)` (default does nothing).

- [ ] **Step 1: Write the failing E2E test**

In `InlineTextEditingTests`:

```csharp
    [Fact]
    public async Task WhenTextIsInsertedAtAnInsertLine_ThenItStandsThereInTheFileAndIsSelected()
    {
        // Arrange: the lines of the page stand before the title, the first paragraph, the card and the last paragraph.
        var page = await OpenPageInInlineModeAsync();
        var lineButton = page.GetByRole(AriaRole.Button, new() { Name = "Insert at position 3 of the page", Exact = true });

        // Act
        await lineButton.ClickAsync();
        await page.GetByTestId("insert-text-item").ClickAsync();
        await page.GetByTestId("markdown-text-input").FillAsync("Inserted text.");
        await page.GetByTestId("markdown-text-save").ClickAsync();

        // Assert
        await _fixture.WaitForDemoPageTextAsync(
            PageName,
            text => text.Contains("First paragraph on the page.\n\nInserted text.\n\n:: HomeBlaze.Components.Card"),
            "The text was not inserted before the card.");
        await Assertions.Expect(GetTextWrapper(page, "Inserted text")).ToHaveClassAsync(new Regex(@"\bselected\b"), new() { Timeout = Timeout });
    }
```

In `InlineStructureEditingTests`, the line opens a menu now:

```csharp
    // Opens the dialog that inserts a widget at the line, through the menu of the line.
    private static async Task OpenWidgetDialogAtAsync(IPage page, ILocator line)
    {
        await line.GetByRole(AriaRole.Button).ClickAsync();
        await page.GetByTestId("insert-widget-item").ClickAsync();
        await Assertions.Expect(page.GetByTestId("create-subject-dialog")).ToBeVisibleAsync(new() { Timeout = Timeout });
    }
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.E2E.Tests --filter "FullyQualifiedName~InlineTextEditingTests|FullyQualifiedName~InlineStructureEditingTests"`
Expected: FAIL: there is no `insert-text-item` or `insert-widget-item`.

- [ ] **Step 3: The menu**

`BlockInsertLine.razor`:

```razor
@* A place between the blocks of a page that is edited inline, where a new block or new text goes. It takes no room, so that
   the content does not move when lines show or hide with the selection; its bar is drawn over the gap above the block that
   follows. A click on it must not reach the widget around it, which would take it for a click that selects the widget. The
   menu opens in the provider of the popovers, outside the widget. *@
<div class="block-insert-line edit-mode-interactive" data-testid="block-insert-line" @onclick:stopPropagation="true">
    <div class="block-insert-line-bar">
        <MudMenu Icon="@Icons.Material.Filled.Add"
                 Size="Size.Small"
                 Variant="Variant.Outlined"
                 Color="Color.Primary"
                 Dense="true"
                 AriaLabel="@Label"
                 title="@Label">
            <MudMenuItem OnClick="@(() => OnInsertWidget.InvokeAsync())" data-testid="insert-widget-item">Widget...</MudMenuItem>
            @if (OnInsertText.HasDelegate)
            {
                <MudMenuItem OnClick="@(() => OnInsertText.InvokeAsync())" data-testid="insert-text-item">Text...</MudMenuItem>
            }
        </MudMenu>
    </div>
</div>

@code {
    /// <summary>Raised when a widget is to be inserted at the line.</summary>
    [Parameter]
    public EventCallback OnInsertWidget { get; set; }

    /// <summary>Raised when text is to be inserted at the line. Without a delegate, the menu offers no text.</summary>
    [Parameter]
    public EventCallback OnInsertText { get; set; }

    /// <summary>The accessible name of the line, which tells it apart from the other lines of the page.</summary>
    [Parameter, EditorRequired]
    public string Label { get; set; } = string.Empty;
}
```

If the MudBlazor version names the activator label differently, keep the name of the line on the button that opens the menu, which the E2E tests find by role and name.

Add to `PageStructureEditor`:

```csharp
    /// <summary>Gets whether text can be inserted into <paramref name="content"/> before <paramref name="before"/>, or at its end. False by default.</summary>
    public virtual bool CanInsertText(IInterceptorSubject content, IInterceptorSubject? before) => false;

    /// <summary>
    /// Asks for text, inserts it into <paramref name="content"/> before <paramref name="before"/>, or at its end, and selects
    /// it. Does nothing by default.
    /// </summary>
    public virtual Task InsertTextAsync(IInterceptorSubject content, IInterceptorSubject? before) => Task.CompletedTask;
```

- [ ] **Step 4: The content and the editor**

In `MarkdownContentView.razor`, the two lines become:

```razor
        <BlockInsertLine Label="@($"Insert at position {position} of {contentName}")"
                         OnInsertWidget="@(() => StructureEditor!.InsertWidgetAsync(Content!, item))"
                         OnInsertText="@GetInsertTextCallback(item)" />
```

```razor
    <BlockInsertLine Label="@($"Insert at the end of {contentName}")"
                     OnInsertWidget="@(() => StructureEditor!.InsertWidgetAsync(Content!, null))"
                     OnInsertText="@GetInsertTextCallback(null)" />
```

```csharp
    // No text item where the block before the line has no closing line, which would take the text.
    private EventCallback GetInsertTextCallback(IInterceptorSubject? before) =>
        StructureEditor!.CanInsertText(Content!, before)
            ? EventCallback.Factory.Create(this, () => StructureEditor!.InsertTextAsync(Content!, before))
            : default;
```

In `MarkdownPageStructureEditor.cs`:

```csharp
    public override bool CanInsertText(IInterceptorSubject content, IInterceptorSubject? before) =>
        _file.CanInsertTextAt(content, before);

    public override async Task InsertTextAsync(IInterceptorSubject content, IInterceptorSubject? before)
    {
        var pageContent = _file.Content;
        await MarkdownTextDialog.ShowAsync(
            _dialogService,
            "Insert text",
            string.Empty,
            text => string.Equals(pageContent, _file.Content, StringComparison.Ordinal)
                ? SaveTextAsync(() => _file.InsertTextAsync(content, before, text, CancellationToken.None))
                : Task.FromResult<string?>(ChangedWhileAskingMessage));
    }
```

- [ ] **Step 5: Run the tests and accept the API snapshot**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Components.Tests`
Expected: accept `VerifyChecksTests.PublicApi` (`BlockInsertLine` parameters, the two `PageStructureEditor` members), rerun: PASS.

Run: `dotnet test src/HomeBlaze/HomeBlaze.E2E.Tests`
Expected: PASS for the whole project.

- [ ] **Step 6: Commit**

```bash
git add src/HomeBlaze
git commit -m "feat: insert text from the menu of a plus line"
```

---

### Task 17: One base class for the widgets that follow the selection, and one slot lookup

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Components/SelectionContainingWidgetBase.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Components/Components/ValueListWidget.razor`, `Components/ValueMapWidget.razor`, `Components/ResponsiveWidget.razor`
- Modify: `src/HomeBlaze/HomeBlaze.Components/PageListSlot.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Components.Tests/VerifyChecksTests.PublicApi.verified.txt`
- Test: the existing E2E tests of rows, cases and the Responsive chip (behavior does not change)

**Interfaces:**
- Produces: `public abstract class SelectionContainingWidgetBase : ComponentBase, IDisposable` with the cascades `IsEditing`, `Selection`, `Owner` and `protected bool ContainsSelection`; `public static PageListSlot? PageListSlot.Find(IReadOnlyList<PageListSlot> slots, string name)`.

`MarkdownContentView` keeps its own subscription: it gates on the editor, yields before it reads the selection and has another predicate.

- [ ] **Step 1: The base class**

```csharp
using Microsoft.AspNetCore.Components;

namespace HomeBlaze.Components;

/// <summary>
/// A widget that renders more while its page is edited and the selection is on it or on a widget inside it, such as a list
/// whose rows become selectable. It renders again when that changes. A widget that overrides
/// <see cref="OnParametersSet"/> calls the base first.
/// </summary>
public abstract class SelectionContainingWidgetBase : ComponentBase, IDisposable
{
    private WidgetSelection? _subscribedSelection;

    [CascadingParameter(Name = "IsEditing")]
    public bool IsEditing { get; set; }

    [CascadingParameter]
    public WidgetSelection? Selection { get; set; }

    [CascadingParameter(Name = "ParentSubjectComponent")]
    public SubjectComponent? Owner { get; set; }

    /// <summary>Gets whether the page is edited and its selected widget is this widget or a widget inside it.</summary>
    protected bool ContainsSelection { get; private set; }

    protected override void OnParametersSet()
    {
        // The flag follows the selection, which changes without new parameters.
        var selection = IsEditing ? Selection : null;
        if (!ReferenceEquals(selection, _subscribedSelection))
        {
            Unsubscribe();
            _subscribedSelection = selection;
            if (selection is not null)
            {
                selection.Changed += OnSelectionChanged;
            }
        }

        ContainsSelection = selection is not null && Owner?.ContainsSelection == true;
    }

    private void OnSelectionChanged()
    {
        // Every such widget of the page is notified, and only one that gains or loses the selection renders again.
        var containsSelection = Owner?.ContainsSelection == true;
        if (containsSelection != ContainsSelection)
        {
            ContainsSelection = containsSelection;
            _ = InvokeAsync(StateHasChanged);
        }
    }

    private void Unsubscribe()
    {
        if (_subscribedSelection is not null)
        {
            _subscribedSelection.Changed -= OnSelectionChanged;
            _subscribedSelection = null;
        }
    }

    public void Dispose() => Unsubscribe();
}
```

- [ ] **Step 2: The slot lookup**

```csharp
namespace HomeBlaze.Components;

/// <summary>A slot of a block that holds a list of blocks.</summary>
/// <param name="Name">The name of its blocks, such as Value or Tab.</param>
/// <param name="ItemType">The type of an item, or null when an item holds page content.</param>
public sealed record PageListSlot(string Name, Type? ItemType)
{
    /// <summary>Gets the slot named <paramref name="name"/> among <paramref name="slots"/>, or null.</summary>
    public static PageListSlot? Find(IReadOnlyList<PageListSlot> slots, string name)
    {
        foreach (var slot in slots)
        {
            if (slot.Name == name)
            {
                return slot;
            }
        }

        return null;
    }
}
```

- [ ] **Step 3: The widgets**

`ValueListWidget.razor`: replace `@implements IDisposable` with `@inherits SelectionContainingWidgetBase`; delete the cascades `IsEditing`, `Selection`, `Owner`, the fields `_subscribedSelection` and `_containsSelection`, `OnParametersSet`, `OnSelectionChanged`, `FindValueSlot`, `Unsubscribe` and `Dispose`; keep `Subject` and `StructureEditor`. Use `ContainsSelection` where the markup used `_containsSelection`, and `PageListSlot.Find(editor.GetListSlots(list), "Value")` where it used `FindValueSlot(editor, list)`.

`ValueMapWidget.razor`: the same, with `ContainsSelection` for `_isShowingCases` and `PageListSlot.Find(editor.GetListSlots(map), "Case")` for `FindCaseSlot(editor, map)`. Keep `@implements IInvisibleWidget`.

`ResponsiveWidget.razor`: the same, with `ContainsSelection` for `_containsSelection`.

- [ ] **Step 4: Run the tests and accept the API snapshot**

Run: `dotnet build src/Namotion.Interceptor.slnx`, then `dotnet test src/HomeBlaze/HomeBlaze.Components.Tests`
Expected: accept `VerifyChecksTests.PublicApi` (the base class; the three widgets inherit their cascades), rerun: PASS.

Run: `dotnet test src/HomeBlaze/HomeBlaze.E2E.Tests --filter "FullyQualifiedName~InlineStructureEditingTests|FullyQualifiedName~ValueListTests|FullyQualifiedName~ResponsiveTests"`
Expected: PASS (filters that match no class run nothing).

- [ ] **Step 5: Commit**

```bash
git add src/HomeBlaze
git commit -m "refactor: share the selection tracking of list, map and Responsive widgets"
```

---

### Task 18: A table of the hidden classes of a Responsive block

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.Components/Responsive.cs:27-30,98-125,136-157`
- Test: `src/HomeBlaze/HomeBlaze.Components.Tests/ResponsiveTests.cs`

**Interfaces:**
- Produces: `Responsive.GetHiddenCssClasses(Breakpoint from, Breakpoint to)` unchanged in signature and result, looked up in a table instead of built per render.

- [ ] **Step 1: Write the test of every range**

```csharp
    public static TheoryData<Breakpoint, Breakpoint> EveryRange()
    {
        var data = new TheoryData<Breakpoint, Breakpoint>();
        for (var from = Breakpoint.Xs; from <= Breakpoint.Xxl; from++)
        {
            for (var to = from; to <= Breakpoint.Xxl; to++)
            {
                data.Add(from, to);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryRange))]
    public void WhenTheHiddenClassesOfAnyRangeAreAskedFor_ThenTheyShowAnElementExactlyWhereTheContentIsHidden(Breakpoint from, Breakpoint to)
    {
        // Arrange: a class of a breakpoint applies from its lower edge, so a class is needed only where that changes.
        string[] names = ["xs", "sm", "md", "lg", "xl", "xxl"];
        string? expected = null;
        if (from != Breakpoint.Xs || to != Breakpoint.Xxl)
        {
            var isShown = from > Breakpoint.Xs;
            expected = isShown ? "d-block" : "d-none";
            for (var breakpoint = 1; breakpoint < names.Length; breakpoint++)
            {
                var isContentHidden = breakpoint < (int)from || breakpoint > (int)to;
                if (isContentHidden != isShown)
                {
                    expected += $" d-{names[breakpoint]}{(isContentHidden ? "-block" : "-none")}";
                    isShown = isContentHidden;
                }
            }
        }

        // Act
        var classes = Responsive.GetHiddenCssClasses(from, to);

        // Assert
        Assert.Equal(expected, classes);
    }
```

The reference is the algorithm of today, so the test passes before and after the change.

- [ ] **Step 2: Run it**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Components.Tests --filter "FullyQualifiedName~ResponsiveTests"`
Expected: PASS (21 ranges).

- [ ] **Step 3: Build both tables at once**

In `Responsive.cs`, replace `CssClassTable` and its factory:

```csharp
    // Declared before CssClassTables, whose initializer reads it.
    private static readonly string[] BreakpointNames = ["xs", "sm", "md", "lg", "xl", "xxl"];

    // The classes of the content and of the element that stands where the content is hidden, by From and To.
    private static readonly (string[] Shown, string?[] Hidden) CssClassTables = CreateCssClassTables();
```

`TryGetCssClasses` reads `CssClassTables.Shown[(int)from * BreakpointCount + (int)to]`. Replace `GetHiddenCssClasses` and delete `IsHidden`:

```csharp
    /// <summary>
    /// Gets the MudBlazor display classes that show an element exactly at the widths where the content is hidden, or
    /// null when the content shows at every width. Both arguments must be valid, see <see cref="TryGetCssClasses"/>.
    /// </summary>
    public static string? GetHiddenCssClasses(Breakpoint from, Breakpoint to) =>
        CssClassTables.Hidden[(int)from * BreakpointCount + (int)to];
```

```csharp
    private static (string[] Shown, string?[] Hidden) CreateCssClassTables()
    {
        var shown = new string[BreakpointCount * BreakpointCount];
        var hidden = new string?[BreakpointCount * BreakpointCount];
        for (var from = 0; from < BreakpointCount; from++)
        {
            for (var to = from; to < BreakpointCount; to++)
            {
                // A display class of a breakpoint applies from its lower edge, so the content hides from the lower edge of
                // the breakpoint after To. MudBlazor declares the classes of larger breakpoints later, so they win.
                var index = from * BreakpointCount + to;
                var show = from == 0 ? "d-block" : $"d-none d-{BreakpointNames[from]}-block";
                var hide = to == BreakpointCount - 1 ? "" : $" d-{BreakpointNames[to + 1]}-none";
                shown[index] = $"responsive-widget {show}{hide}";

                // The element where the content is hidden takes the same classes the other way round.
                var showElement = from == 0 ? "d-none" : $"d-block d-{BreakpointNames[from]}-none";
                var hideElement = to == BreakpointCount - 1 ? "" : $" d-{BreakpointNames[to + 1]}-block";
                hidden[index] = from == 0 && to == BreakpointCount - 1 ? null : showElement + hideElement;
            }
        }

        return (shown, hidden);
    }
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Components.Tests --filter "FullyQualifiedName~ResponsiveTests"`
Expected: PASS, including the five cases of phase 1 and the 21 ranges.

- [ ] **Step 5: Commit**

```bash
git add src/HomeBlaze
git commit -m "perf: look up the hidden classes of a Responsive block in a table"
```

---

### Task 19: Docs and the full verification

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze/Data/Files/Docs/administration/pages.md:973-985`
- Modify: `src/HomeBlaze/HomeBlaze/Data/Files/Docs/development/building-subjects.md` (only where it names `IPageStructureEditor`, the bar or the chips)
- Modify: `docs/superpowers/specs/2026-10-22-markdown-inline-editing-design.md` (only if the implementation deviated)

- [ ] **Step 1: The user docs**

Replace the section "Editing the blocks of a page inline" of `pages.md`, from its heading to the paragraph about the Split mode, with:

```markdown
### Editing the blocks of a page inline

In "Inline" mode, a click selects a widget and shows its bar. Next to "Edit", the bar moves the block up or down, deletes it after asking, and adds an item to each list slot of the block, such as "Add Value" for a `ValueList` or "Add Tab" for a `TabLayout`. A move passes one item of the content around the block, a block or a block of text, and the text stays where it is. A widget that shows nothing on the viewed page, such as a `ValueMap`, is passed together with the next item, so that every move changes what the page shows. The rows of a `ValueList` and the cases of a `ValueMap` are blocks of their own and move among the rows or cases of their list. The rows of a list can be selected once the list or one of its rows is selected, so the first click selects the list and a further click selects a row. The cases of a selected `ValueMap` work in the same way, and the map offers "Add case" both in its bar and in its list of cases. A moved or inserted block stays selected, and every other block keeps its subject, so a running device keeps running.

The text of a page is made of blocks as well: every paragraph, heading, list, table, quote and code block is one. A block of text can be selected once the content it stands in is at the selection, like a row of a list: on the page while nothing or another block of the page is selected, in a card once the card or a block in it is selected. Its bar has "Edit", which opens its Markdown in a dialog, and "Delete". Raw HTML and the text around it up to the next block, the text of a style block, footnotes and the text in a cell of a `GridLayout` or a node of a `CanvasLayout` are edited in the page editor.

A plus line between the blocks inserts there, with "Widget..." for a new widget and "Text..." for new text. The page shows these lines while nothing is selected, and otherwise the content of the selected widget and the content that directly holds it show them. The lines are drawn over the space between the blocks, so the page does not move when they appear. "Text..." is missing where the block before the line has no closing line, which would take the text. The content of a cell of a `GridLayout` and of a node of a `CanvasLayout` has no plus lines, because these layouts have their own buttons.

A widget that shows nothing, such as a `ValueMap`, is a chip with its name where its block is, and selecting it lists its cases below the chip. The content of a `Responsive` block that is hidden at the current width is a chip in its place, and selecting the chip shows the content.

Changes in this mode only change the text of the page, so a change that the page refuses leaves the page as it was. When the file changed since the page read it, for example because it was edited in another editor, the change is refused and the page shows the file. The dialog of a text that changed in the meantime says so and keeps what you typed. When the file cannot be written, for example because another process holds it open, the page shows an error message instead.

In "Split" mode the page next to the editor only shows the saved page and cannot be operated, neither with the mouse nor with the keyboard: a click on a widget shows its block in the editor instead of selecting the widget, so that nothing changes the file while the editor has text that is not saved. When the text before that block has changes that are not saved, a warning asks to save first. Grid and canvas editing and the bar of a widget therefore work in "Inline" mode only.
```

In `building-subjects.md`, replace any mention of `IPageStructureEditor` with `PageStructureEditor`, and make the sentence on the bar list Select parent, Move up, Move down, Add, Edit and Delete; leave everything else.

- [ ] **Step 2: Check the spec against what was built**

Read `docs/superpowers/specs/2026-10-22-markdown-inline-editing-design.md` once more. Where the implementation chose differently (a refusal text, a test id, a CSS detail such as the footer gap), change the spec so that the table and the body say what the code does. Do not add new decisions there.

- [ ] **Step 3: The full verification**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: 0 warnings, 0 errors.

Run: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`
Expected: PASS, except the six `StorageMetadataWriteTests` that fail on macOS before this work.

Run: `dotnet test src/HomeBlaze/HomeBlaze.E2E.Tests`
Expected: PASS for the whole project, including `ViewHtmlGoldenTests`, `InlineTextEditingTests` and `InlineStructureEditingTests`.

- [ ] **Step 4: Commit**

```bash
git add src/HomeBlaze docs
git commit -m "docs: describe the moves, the chips, the insert lines and the text editing of the Inline mode"
```
