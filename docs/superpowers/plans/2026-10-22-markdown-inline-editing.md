# Inline Structure Editing of Markdown Pages Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the Inline mode of a markdown page edit the structure of the page on the page itself (delete, move, insert, list slot items, hidden blocks), make the Split preview view-only with click-to-reveal, and show block names in bold in the source editor.

**Architecture:** Pure text operations (`MarkdownPageOperations`) remove, swap and insert blocks in the source of a parsed page and check the result with `MarkdownBlockAlignment`. `MarkdownFile` runs them through the same publish-and-write step as the existing configuration write-back. The UI talks to the page through `IPageStructureEditor` (HomeBlaze.Components), which `MarkdownPageStructureEditor` (HomeBlaze.Storage.Blazor) implements and `MarkdownFilePageComponent` cascades in Inline mode only.

**Tech Stack:** .NET 10, C# 14, Blazor Server, MudBlazor 9.11, BlazorMonaco 3.4, Markdig, xUnit, Playwright (E2E).

**Spec:** [docs/superpowers/specs/2026-10-22-markdown-inline-editing-design.md](../specs/2026-10-22-markdown-inline-editing-design.md)

## Global Constraints

- Warnings are errors (`Directory.Build.props`). Every task ends with a clean `dotnet build src/Namotion.Interceptor.slnx`.
- Test names follow `When<Condition>_Then<ExpectedBehavior>` with `// Arrange`, `// Act`, `// Assert` (`// Act & Assert` for exceptions).
- No `Task.Delay` or `Thread.Sleep` in tests. E2E tests wait with Playwright `Assertions.Expect` or `PlaywrightFixture.WaitForDemoPageTextAsync`.
- Descriptive names, no abbreviations. US English. No em dashes in docs or comments.
- Comments explain only the why a reader cannot derive. XML docs state the contract.
- Allocations matter: event callbacks that render on every widget are created once (see `SubjectComponent`), lambdas are fine in markup that renders only in edit mode.
- Public API snapshots: HomeBlaze.Storage and HomeBlaze.Components have `VerifyChecksTests.PublicApi`. When a task changes their public API, replace `VerifyChecksTests.PublicApi.verified.txt` with the `.received.txt` the test writes.
- Commit messages: conventional prefix (`feat:`, `refactor:`, `test:`, `docs:`), no AI attribution of any kind.
- UI behavior is tested end to end in `HomeBlaze.E2E.Tests` (the repository has no bUnit). Pure logic is unit tested. The spec's bUnit bullet is replaced by these E2E tests (see Task 13, which updates the spec).
- E2E tests are `[Trait("Category", "Integration")]` and run per project: `dotnet test src/HomeBlaze/HomeBlaze.E2E.Tests --filter "FullyQualifiedName~InlineStructureEditingTests"`.

## File Structure

**HomeBlaze.Storage**
- Modify `Pages/MarkdownBlockWriter.cs`: make `GetLineStart` and `GetIndentation` internal, add public `InsertBlockText` and internal `GetBlockText`.
- Modify `Pages/MarkdownBlockAlignment.cs`: accept inserted widget blocks (`insertedKind`).
- Create `Pages/MarkdownPageOperations.cs`: `TryRemove`, `TryMove`, `TryInsertWidget`, `TryAppendSlotBlock`, `FindMoveTarget`, `FindContentBlock`, `CountBlocks` and the `MarkdownStructureChange` record.
- Create `Pages/MarkdownEditResult.cs`, `Pages/MarkdownBlockActions.cs`, `Pages/MarkdownListSlot.cs`: public result and query types.
- Modify `Files/MarkdownFile.cs`: extract `PublishAndWriteAsync`, add the structure edit methods and queries.

**HomeBlaze.Storage.Blazor**
- Modify `Models/BlockOutline.cs`, `Models/MarkdownDecorationParser.cs`, `Models/BlockOutlineDecorations.cs`, `Files/MarkdownFileEditComponent.razor`, `Files/MarkdownFileEditComponent.razor.js`: bold names, shared insert, reveal.
- Create `Files/MarkdownPageStructureEditor.cs`: implements `IPageStructureEditor` on a `MarkdownFile`.
- Modify `Files/MarkdownFilePageComponent.razor` and `.razor.css`: cascade the editor, chip stack and insert line styles.
- Modify `Files/MarkdownContentView.razor`, `Files/MarkdownFragmentWidget.razor`: chips and insert lines.

**HomeBlaze.Components**
- Create `IPageStructureEditor.cs`, `PageBlockActions.cs`, `PageListSlot.cs`, `IInvisibleWidget.cs`, `ISubjectRevealTarget.cs`, `SubjectRevealer.cs`.
- Modify `WidgetSelection.cs` (`Changed` event), `SubjectComponent.razor` (action bar, selection helpers, reveal click), `SubjectComponent.razor.css`.
- Modify `Dialogs/SubjectSetupOptions.cs`, `Dialogs/SubjectSetupDialog.razor` (fixed type).
- Modify `Editors/SubjectEditPanel.razor` (expose the edit component).
- Create `Components/BlockInsertLine.razor`, `Components/ValueMapCaseWidget.razor`, `Components/ValueRowWidget.razor`.
- Modify `Components/ValueMapWidget.razor`, `Components/ValueListWidget.razor`, `Components/ResponsiveWidget.razor`, `Responsive.cs`.

**HomeBlaze (app)**
- Modify `Components/Pages/Pages.razor`: view-only Split preview with reveal.
- Modify `Data/Files/Docs/administration/pages.md`: document the new editing.

**Tests**
- Create `HomeBlaze.Storage.Tests/Pages/MarkdownPageOperationsTests.cs`, `HomeBlaze.Storage.Tests/Pages/MarkdownStructureEditTests.cs`.
- Modify `HomeBlaze.Storage.Tests/Pages/MarkdownBlockWriterTests.cs`, `HomeBlaze.Storage.Blazor.Tests/BlockOutlineTests.cs`, `HomeBlaze.Components.Tests/ResponsiveTests.cs`.
- Create `HomeBlaze.E2E.Tests/InlineStructureEditingTests.cs`, `HomeBlaze.E2E.Tests/TestData/Demo/Structure.md`.

---

### Task 1: Shared text helpers in `MarkdownBlockWriter`

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.Storage/Pages/MarkdownBlockWriter.cs`
- Test: `src/HomeBlaze/HomeBlaze.Storage.Tests/Pages/MarkdownBlockWriterTests.cs`

**Interfaces:**
- Produces:
  - `public static string MarkdownBlockWriter.InsertBlockText(string source, int line, string blockText, string lineEnding)`
  - `internal static string MarkdownBlockWriter.InsertBlockText(string source, int line, string blockText, string lineEnding, out MarkdownLineEdit edit)`
  - `internal static int MarkdownBlockWriter.GetAppendLine(string source)`: the 1-based line that appends after the last line.
  - `internal static string MarkdownBlockWriter.GetBlockText(string source, MarkdownBlock block)`: the lines from the head line to the last line, without the line ending after the last line.
  - `internal static int GetLineStart(string source, int line)` and `internal static string GetIndentation(string source, MarkdownBlock block)` (were private).

- [ ] **Step 1: Write the failing tests**

Add to `MarkdownBlockWriterTests`:

```csharp
    [Theory]
    [InlineData("intro\n\n:: Demo.One\n::\n", 3, "intro\n\n:: Demo.New\n::\n\n:: Demo.One\n::\n")]
    [InlineData("intro\n:: Demo.One\n::\n", 2, "intro\n\n:: Demo.New\n::\n\n:: Demo.One\n::\n")]
    [InlineData("intro\n", 2, "intro\n\n:: Demo.New\n::\n")]
    [InlineData("intro", 2, "intro\n\n:: Demo.New\n::")]
    [InlineData("", 1, ":: Demo.New\n::")]
    public void WhenBlockTextIsInserted_ThenAnEmptyLineSeparatesItFromLinesThatAreNotEmpty(string source, int line, string expected)
    {
        // Act
        var result = MarkdownBlockWriter.InsertBlockText(source, line, ":: Demo.New\n::", "\n");

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void WhenBlockTextIsInsertedIntoAPageWithWindowsLineEndings_ThenTheResultHasOnlyWindowsLineEndings()
    {
        // Arrange
        const string source = "intro\r\n:: Demo.One\r\n::\r\n";

        // Act
        var result = MarkdownBlockWriter.InsertBlockText(source, 2, ":: Demo.New\r\n::", "\r\n");

        // Assert
        Assert.Equal("intro\r\n\r\n:: Demo.New\r\n::\r\n\r\n:: Demo.One\r\n::\r\n", result);
    }

    [Theory]
    [InlineData("intro\n", 2)]
    [InlineData("intro", 2)]
    [InlineData("a\nb\n", 3)]
    [InlineData("", 1)]
    public void WhenTheAppendLineIsAskedFor_ThenItIsTheLineAfterTheLastLineOfText(string source, int expected)
    {
        // Act & Assert
        Assert.Equal(expected, MarkdownBlockWriter.GetAppendLine(source));
    }

    [Fact]
    public void WhenTheTextOfABlockIsAskedFor_ThenItHasItsLinesWithoutTheLastLineEnding()
    {
        // Arrange
        const string source = "intro\n\n:: Demo.Card\n::: Row Label=A\n:::\n::\n\noutro\n";
        var card = MarkdownPageParser.Parse(source).Blocks[0];

        // Act
        var text = MarkdownBlockWriter.GetBlockText(source, card);

        // Assert
        Assert.Equal(":: Demo.Card\n::: Row Label=A\n:::\n::", text);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests --filter "FullyQualifiedName~MarkdownBlockWriterTests"`
Expected: build error, `InsertBlockText`, `GetAppendLine` and `GetBlockText` do not exist.

- [ ] **Step 3: Implement the helpers**

In `MarkdownBlockWriter.cs`, change `private static string GetIndentation` to `internal static string GetIndentation` and `private static int GetLineStart` to `internal static int GetLineStart`. Add after `InsertLines`:

```csharp
    /// <summary>
    /// Inserts the lines of a block before the 1-based <paramref name="line"/>, with an empty line between the block
    /// and a line before or after it that is not empty. A line after the last line of the text appends the block.
    /// <paramref name="blockText"/> has no line ending after its last line.
    /// </summary>
    public static string InsertBlockText(string source, int line, string blockText, string lineEnding) =>
        InsertBlockText(source, line, blockText, lineEnding, out _);

    /// <inheritdoc cref="InsertBlockText(string, int, string, string)"/>
    internal static string InsertBlockText(string source, int line, string blockText, string lineEnding, out MarkdownLineEdit edit)
    {
        var lineStart = GetLineStart(source, line);
        var text = blockText;
        if (line > 1 && !IsEmptyLine(source, GetLineStart(source, line - 1), lineStart))
        {
            text = lineEnding + text;
        }

        if (!IsEmptyLine(source, lineStart, GetLineStart(source, line + 1)))
        {
            text += lineEnding;
        }

        return InsertLines(source, line, text, lineEnding, out edit);
    }

    /// <summary>Gets the 1-based line that <see cref="InsertLines"/> takes to append after the last line of text.</summary>
    internal static int GetAppendLine(string source)
    {
        var lineFeedCount = source.AsSpan().Count('\n');
        return source.Length == 0 || source[^1] == '\n' ? lineFeedCount + 1 : lineFeedCount + 2;
    }

    /// <summary>Gets the lines of the block from its head line to its last line, without the line ending of the last line.</summary>
    internal static string GetBlockText(string source, MarkdownBlock block)
    {
        var start = GetLineStart(source, block.Line);
        var end = GetLineStart(source, block.EndLine + 1);
        return source[start..end].TrimEnd('\n').TrimEnd('\r');
    }

    // Whether the line between the two offsets has nothing but white space. The text after the last line ending is a
    // line of its own, and it is empty when the text ends with a line ending.
    private static bool IsEmptyLine(string source, int lineStart, int nextLineStart) =>
        source.AsSpan(lineStart, nextLineStart - lineStart).IsWhiteSpace();
```

Note: `TrimEnd('\n').TrimEnd('\r')` removes exactly one line ending because a block's last line is not empty.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests --filter "FullyQualifiedName~MarkdownBlockWriterTests"`
Expected: PASS.

- [ ] **Step 5: Update the public API snapshot and commit**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests --filter "FullyQualifiedName~VerifyChecksTests"`. It fails with the new `InsertBlockText`. Copy `VerifyChecksTests.PublicApi.received.txt` over `VerifyChecksTests.PublicApi.verified.txt` after checking that only that method was added, then run it again (PASS).

```bash
git add src/HomeBlaze/HomeBlaze.Storage/Pages/MarkdownBlockWriter.cs src/HomeBlaze/HomeBlaze.Storage.Tests
git commit -m "feat: insert the text of a block with empty lines around it"
```

---

### Task 2: Remove and move blocks in the text of a page

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Storage/Pages/MarkdownPageOperations.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Storage/Pages/MarkdownBlockAlignment.cs`
- Test: `src/HomeBlaze/HomeBlaze.Storage.Tests/Pages/MarkdownPageOperationsTests.cs`

**Interfaces:**
- Consumes: `MarkdownBlockWriter.RemoveBlock`, `GetLineStart`, `GetLineEnding`, `HasLoneCarriageReturn` (Task 1 and existing), `MarkdownBlockWrite` (existing record in `MarkdownBlockReconciler.cs`).
- Produces:
  - `internal sealed record MarkdownStructureChange(MarkdownBlockWrite Write, MarkdownBlock? ChangedBlock)`
  - `internal static MarkdownStructureChange? MarkdownPageOperations.TryRemove(MarkdownPage page, IReadOnlyDictionary<int, IInterceptorSubject> blockSubjects, MarkdownBlock block, out string? refusal)`
  - `internal static MarkdownStructureChange? MarkdownPageOperations.TryMove(MarkdownPage page, IReadOnlyDictionary<int, IInterceptorSubject> blockSubjects, MarkdownBlock block, int direction, out string? refusal)` where `direction` is -1 (up) or 1 (down)
  - `internal static MarkdownBlock? MarkdownPageOperations.FindMoveTarget(MarkdownPage page, MarkdownBlock block, int direction)`
  - `internal static int MarkdownPageOperations.CountBlocks(MarkdownBlock block)`: the block and every block inside it, of every kind.
  - `MarkdownBlockAlignment.TryAlign(..., MarkdownBlock? owner, MarkdownBlockKind insertedKind = MarkdownBlockKind.Slot)`

- [ ] **Step 1: Write the failing tests**

Create `MarkdownPageOperationsTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests --filter "FullyQualifiedName~MarkdownPageOperationsTests"`
Expected: build error, `MarkdownPageOperations` does not exist.

- [ ] **Step 3: Let the alignment accept inserted widget blocks**

In `MarkdownBlockAlignment.cs`, add the parameter to both methods and pass it through:

```csharp
    /// <param name="insertedKind">
    /// The kind of the blocks that the inserted lines put directly into <paramref name="owner"/>, or directly onto the
    /// page for <see cref="MarkdownBlockKind.Widget"/> without an owner.
    /// </param>
    public static int[]? TryAlign(
        MarkdownPage before, MarkdownPage after, MarkdownLineEdit edit, int expectedInsertedBlockCount, MarkdownBlock? owner,
        MarkdownBlockKind insertedKind = MarkdownBlockKind.Slot)
    {
        var map = TryMapBlocks(before, after, edit, expectedInsertedBlockCount, owner, insertedKind);
        return map is not null && HasSameDocument(before, after, edit) ? map : null;
    }
```

Change the signature of `TryMapBlocks` the same way (`MarkdownBlockKind insertedKind = MarkdownBlockKind.Slot` as the last parameter), update its `<returns>` sentence to "Every inserted block must be a block of the inserted kind directly inside <paramref name="owner"/>, or a widget block directly on the page without an owner, or stand inside another inserted block", and replace `isSlotBlockOfOwner` in `TakeInsertedBlocks`:

```csharp
                var isInsertedIntoOwner =
                    block.Kind == insertedKind &&
                    (owner is null
                        ? insertedKind == MarkdownBlockKind.Widget && parent is null
                        : counterparts.TryGetValue(owner, out var newOwner) && ReferenceEquals(parent, newOwner));

                // A block that goes on after the inserted lines has taken over what follows it.
                if (block.EndLine >= edit.InsertedEnd || (!isInsideInsertedBlock && !isInsertedIntoOwner))
                {
                    return false;
                }
```

A removal passes no owner and expects no inserted block, so it is unaffected. The reconciler keeps the default (`Slot`).

- [ ] **Step 4: Implement `MarkdownPageOperations` with remove and move**

Create `MarkdownPageOperations.cs`:

```csharp
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Pages;

/// <summary>What a change of the blocks of a page did.</summary>
/// <param name="Write">The page with the new text and the subjects of its blocks.</param>
/// <param name="ChangedBlock">
/// The block of the page before the change whose content changed, or null when the change is directly on the page.
/// </param>
internal sealed record MarkdownStructureChange(MarkdownBlockWrite Write, MarkdownBlock? ChangedBlock);

/// <summary>
/// Removes, swaps and inserts whole blocks in the text of a page. Every change is checked with
/// <see cref="MarkdownBlockAlignment"/>, so that it does nothing to the blocks and the text it did not mean to change.
/// </summary>
internal static class MarkdownPageOperations
{
    /// <summary>Removes the block with everything in it.</summary>
    /// <returns>Null with a refusal when the change would change other blocks of the page.</returns>
    public static MarkdownStructureChange? TryRemove(
        MarkdownPage page, IReadOnlyDictionary<int, IInterceptorSubject> blockSubjects, MarkdownBlock block, out string? refusal)
    {
        if (!CanInsertOrRemoveBlocks(page, out refusal))
        {
            return null;
        }

        FindSiblings(page, block, out var parent);
        var source = MarkdownBlockWriter.RemoveBlock(page.Source, block, out var edit);
        var after = MarkdownPageParser.Parse(source);
        if (MarkdownBlockAlignment.TryAlign(page, after, edit, expectedInsertedBlockCount: 0, owner: null) is not { } map)
        {
            refusal = $"Removing the block at line {block.Line} would change other blocks of the page.";
            return null;
        }

        return new MarkdownStructureChange(
            new MarkdownBlockWrite(after, MapSubjects(blockSubjects, map), [], HasInsertedOrRemovedBlocks: true), parent);
    }

    /// <summary>
    /// Swaps the block with the block next to it in <paramref name="direction"/> (-1 for up, 1 for down). The text
    /// between the two stays where it is.
    /// </summary>
    /// <returns>Null with a refusal when there is no such block or the change would change other blocks of the page.</returns>
    public static MarkdownStructureChange? TryMove(
        MarkdownPage page, IReadOnlyDictionary<int, IInterceptorSubject> blockSubjects, MarkdownBlock block, int direction, out string? refusal)
    {
        if (FindMoveTarget(page, block, direction) is not { } neighbor)
        {
            refusal = $"The block at line {block.Line} has no block of the same slot {(direction < 0 ? "before" : "after")} it.";
            return null;
        }

        if (!CanInsertOrRemoveBlocks(page, out refusal))
        {
            return null;
        }

        FindSiblings(page, block, out var parent);
        var (first, second) = direction < 0 ? (neighbor, block) : (block, neighbor);
        var source = page.Source;
        var lineEnding = MarkdownBlockWriter.GetLineEnding(source, first.HeadSpan.End);
        var start = MarkdownBlockWriter.GetLineStart(source, first.Line);
        var firstEnd = MarkdownBlockWriter.GetLineStart(source, first.EndLine + 1);
        var secondStart = MarkdownBlockWriter.GetLineStart(source, second.Line);
        var end = MarkdownBlockWriter.GetLineStart(source, second.EndLine + 1);

        var firstText = source[start..firstEnd];
        var middle = source[firstEnd..secondStart];
        var secondText = source[secondStart..end];

        // The second block can end the text without a line ending. It moves in front, so it gets one, and the first
        // block that ends the text now gives up its own.
        if (!secondText.EndsWith('\n'))
        {
            secondText += lineEnding;
            firstText = firstText.TrimEnd('\n').TrimEnd('\r');
        }

        var newSource = string.Concat(source.AsSpan(0, start), secondText, middle, firstText, source.AsSpan(end));
        var lineCount = second.EndLine - first.Line + 1;
        var edit = new MarkdownLineEdit(first.Line, lineCount, lineCount);
        var after = MarkdownPageParser.Parse(newSource);
        var map = MarkdownBlockAlignment.TryAlign(
            page, after, edit, CountBlocks(first) + CountBlocks(second), parent, block.Kind);
        if (map is null)
        {
            refusal = $"Moving the block at line {block.Line} would change other blocks of the page.";
            return null;
        }

        // The swapped blocks are new to the alignment. Their text is the same, so their blocks are in the same order.
        var subjects = MapSubjects(blockSubjects, map);
        var secondLineCount = second.EndLine - second.Line + 1;
        var middleLineCount = second.Line - first.EndLine - 1;
        CarrySubjects(blockSubjects, second, FindBlockAt(after, first.Line), subjects);
        CarrySubjects(blockSubjects, first, FindBlockAt(after, first.Line + secondLineCount + middleLineCount), subjects);

        return new MarkdownStructureChange(new MarkdownBlockWrite(after, subjects, [], HasInsertedOrRemovedBlocks: true), parent);
    }

    /// <summary>
    /// Gets the block that <see cref="TryMove"/> swaps the block with: the block right next to it among the blocks of
    /// the same parent, when it is of the same kind and, for a slot block, of the same slot. Otherwise null.
    /// </summary>
    public static MarkdownBlock? FindMoveTarget(MarkdownPage page, MarkdownBlock block, int direction)
    {
        if (direction is not (-1 or 1) ||
            block.Kind is not (MarkdownBlockKind.Widget or MarkdownBlockKind.Slot) ||
            FindSiblings(page, block, out _) is not { } siblings)
        {
            return null;
        }

        var position = IndexOf(siblings, block) + direction;
        if (position < 0 || position >= siblings.Count)
        {
            return null;
        }

        var neighbor = siblings[position];
        return neighbor.Kind == block.Kind &&
               (block.Kind == MarkdownBlockKind.Widget || neighbor.Head!.Name == block.Head!.Name)
            ? neighbor
            : null;
    }

    /// <summary>Counts the block and every block inside it, of every kind.</summary>
    public static int CountBlocks(MarkdownBlock block)
    {
        var count = 1;
        foreach (var child in block.Children)
        {
            count += CountBlocks(child);
        }

        return count;
    }

    // The blocks beside the block, with the block around them, which is null directly on the page.
    internal static IReadOnlyList<MarkdownBlock>? FindSiblings(MarkdownPage page, MarkdownBlock block, out MarkdownBlock? parent)
    {
        parent = null;
        return Find(page.Blocks, null, block, ref parent);

        static IReadOnlyList<MarkdownBlock>? Find(
            IReadOnlyList<MarkdownBlock> siblings, MarkdownBlock? around, MarkdownBlock target, ref MarkdownBlock? parent)
        {
            foreach (var sibling in siblings)
            {
                if (ReferenceEquals(sibling, target))
                {
                    parent = around;
                    return siblings;
                }

                if (Find(sibling.Children, sibling, target, ref parent) is { } found)
                {
                    return found;
                }
            }

            return null;
        }
    }

    // Lines are counted by line feeds, and Markdig ends a line at a lone carriage return as well.
    private static bool CanInsertOrRemoveBlocks(MarkdownPage page, out string? refusal)
    {
        refusal = MarkdownBlockWriter.HasLoneCarriageReturn(page.Source)
            ? "Blocks cannot be inserted or removed on a page with a line ending that is a carriage return alone."
            : null;
        return refusal is null;
    }

    private static Dictionary<int, IInterceptorSubject> MapSubjects(IReadOnlyDictionary<int, IInterceptorSubject> subjects, int[] map)
    {
        var result = new Dictionary<int, IInterceptorSubject>(subjects.Count);
        foreach (var (index, subject) in subjects)
        {
            if (map[index] >= 0)
            {
                result[map[index]] = subject;
            }
        }

        return result;
    }

    private static void CarrySubjects(
        IReadOnlyDictionary<int, IInterceptorSubject> oldSubjects, MarkdownBlock oldBlock, MarkdownBlock newBlock, Dictionary<int, IInterceptorSubject> newSubjects)
    {
        var oldIndices = CollectIndices(oldBlock);
        var newIndices = CollectIndices(newBlock);
        for (var position = 0; position < oldIndices.Count && position < newIndices.Count; position++)
        {
            if (oldSubjects.TryGetValue(oldIndices[position], out var subject))
            {
                newSubjects[newIndices[position]] = subject;
            }
        }
    }

    // The indices of the block and of the blocks inside it in document order. A style block has none.
    private static List<int> CollectIndices(MarkdownBlock block)
    {
        var result = new List<int>();
        Add(block);
        return result;

        void Add(MarkdownBlock current)
        {
            if (current.Index >= 0)
            {
                result.Add(current.Index);
            }

            foreach (var child in current.Children)
            {
                Add(child);
            }
        }
    }

    private static MarkdownBlock FindBlockAt(MarkdownPage page, int line)
    {
        foreach (var block in page.AllBlocks)
        {
            if (block.Line == line)
            {
                return block;
            }
        }

        throw new InvalidOperationException($"The page has no block at line {line}.");
    }

    private static int IndexOf(IReadOnlyList<MarkdownBlock> blocks, MarkdownBlock block)
    {
        for (var index = 0; index < blocks.Count; index++)
        {
            if (ReferenceEquals(blocks[index], block))
            {
                return index;
            }
        }

        return -1;
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests --filter "FullyQualifiedName~MarkdownPageOperationsTests|FullyQualifiedName~MarkdownBlockStructureTests|FullyQualifiedName~MarkdownSlotWriteBackTests"`
Expected: PASS. The structure and write-back tests confirm that the reconciler, which keeps the default kind, is unchanged.

- [ ] **Step 6: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Storage/Pages src/HomeBlaze/HomeBlaze.Storage.Tests/Pages/MarkdownPageOperationsTests.cs
git commit -m "feat: remove and swap blocks in the text of a page"
```

---

### Task 3: Insert widget blocks and slot blocks in the text of a page

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.Storage/Pages/MarkdownPageOperations.cs`
- Test: `src/HomeBlaze/HomeBlaze.Storage.Tests/Pages/MarkdownPageOperationsTests.cs`

**Interfaces:**
- Consumes: Task 1 helpers, Task 2 `MarkdownStructureChange`, `MapSubjects`, `CanInsertOrRemoveBlocks`.
- Produces:
  - `internal static MarkdownStructureChange? MarkdownPageOperations.TryInsertWidget(MarkdownPage page, IReadOnlyDictionary<int, IInterceptorSubject> blockSubjects, MarkdownBlock? owner, MarkdownBlock? before, string typeName, string? id, string configurationJson, out string? refusal)`: `owner` null is the page itself; `before` null appends to the content of the owner.
  - `internal static MarkdownStructureChange? MarkdownPageOperations.TryAppendSlotBlock(MarkdownPage page, IReadOnlyDictionary<int, IInterceptorSubject> blockSubjects, MarkdownBlock owner, string slotName, string configurationJson, out string? refusal)`
  - `internal static MarkdownBlock? MarkdownPageOperations.FindContentBlock(MarkdownPage page, IReadOnlyDictionary<int, IInterceptorSubject> blockSubjects, IInterceptorSubject fragment)`

- [ ] **Step 1: Write the failing tests**

Add to `MarkdownPageOperationsTests`:

```csharp
    [Fact]
    public void WhenAWidgetIsInsertedBeforeABlockOnThePage_ThenItStandsBeforeItWithEmptyLinesAround()
    {
        // Arrange
        var page = MarkdownPageParser.Parse("intro\n\n:: Demo.One\n::\n");

        // Act
        var change = MarkdownPageOperations.TryInsertWidget(
            page, NoSubjects, owner: null, before: page.Blocks[0], "Demo.New", "fresh", """{ "Min": 1 }""", out var refusal);

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
        var change = MarkdownPageOperations.TryInsertWidget(page, NoSubjects, null, null, "Demo.New", null, "{}", out _);

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
        var change = MarkdownPageOperations.TryInsertWidget(page, NoSubjects, card, null, "Demo.New", null, "{}", out var refusal);

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
        var change = MarkdownPageOperations.TryInsertWidget(page, NoSubjects, page.Blocks[0], null, "Demo.New", null, "{}", out var refusal);

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
        var change = MarkdownPageOperations.TryInsertWidget(page, NoSubjects, null, page.Blocks[1], "Demo.New", null, "{}", out var refusal);

        // Assert
        Assert.Null(refusal);
        Assert.Equal(":: Demo.One\n\n:: Demo.New\n\n:: Demo.Two\n", change!.Write.Page.Source);
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
    public void WhenAnInsertedWidgetWouldTakeOverTheTextAfterIt_ThenTheInsertIsRefused()
    {
        // Arrange: the block to insert before stands outside of the card, so the new block would not be in the card.
        var page = MarkdownPageParser.Parse(":: Demo.Card\none\ntwo\n:: Demo.Other\n");
        var card = page.Blocks[0];

        // Act
        var change = MarkdownPageOperations.TryInsertWidget(page, NoSubjects, card, page.Blocks[1], "Demo.New", null, "{}", out var refusal);

        // Assert
        Assert.Null(change);
        Assert.Contains("would change other blocks", refusal);
    }
```

The alignment refuses the last one because the parent of the inserted block is not the owner.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests --filter "FullyQualifiedName~MarkdownPageOperationsTests"`
Expected: build error, `TryInsertWidget` and `TryAppendSlotBlock` do not exist.

- [ ] **Step 3: Implement the inserts and `FindContentBlock`**

Add to `MarkdownPageOperations`:

```csharp
    /// <summary>
    /// Inserts a new widget block into the content of <paramref name="owner"/>, or of the page when it is null: before
    /// <paramref name="before"/>, or after the content when it is null, which is before the first slot block of the
    /// owner. The block takes the fence of the block beside it and closing lines unless the page has none.
    /// </summary>
    public static MarkdownStructureChange? TryInsertWidget(
        MarkdownPage page,
        IReadOnlyDictionary<int, IInterceptorSubject> blockSubjects,
        MarkdownBlock? owner,
        MarkdownBlock? before,
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
        var firstSlotBlock = siblings.FirstOrDefault(block => block.Kind == MarkdownBlockKind.Slot);
        var line = before?.Line
            ?? firstSlotBlock?.Line
            ?? (owner is null ? MarkdownBlockWriter.GetAppendLine(source) : owner.HasClosingLine ? owner.EndLine : owner.EndLine + 1);
        line = page.GetLineForNewBlock(line);

        var reference = before ?? siblings.LastOrDefault(block => block.Kind == MarkdownBlockKind.Widget);
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
            new MarkdownBlockWrite(after, MapSubjects(blockSubjects, map), [], HasInsertedOrRemovedBlocks: true), owner);
    }

    /// <summary>
    /// Appends a new block of the slot <paramref name="slotName"/> to <paramref name="owner"/>, after the last block of
    /// that slot and written like it, as <see cref="MarkdownBlockWriter.GetSlotBlockPlacement"/> places it.
    /// </summary>
    public static MarkdownStructureChange? TryAppendSlotBlock(
        MarkdownPage page,
        IReadOnlyDictionary<int, IInterceptorSubject> blockSubjects,
        MarkdownBlock owner,
        string slotName,
        string configurationJson,
        out string? refusal)
    {
        if (!CanInsertOrRemoveBlocks(page, out refusal))
        {
            return null;
        }

        var placement = MarkdownBlockWriter.GetSlotBlockPlacement(page, owner, slotName);
        var text = MarkdownBlockWriter.Indent(
            MarkdownBlockWriter.CreateBlock(
                slotName, null, configurationJson, placement.FenceLength, placement.LineEnding, placement.HasClosingLine),
            placement.Indentation,
            placement.LineEnding);
        var source = MarkdownBlockWriter.InsertLines(page.Source, placement.Line, text, placement.LineEnding, out var edit);
        var after = MarkdownPageParser.Parse(source);
        if (MarkdownBlockAlignment.TryAlign(page, after, edit, expectedInsertedBlockCount: 1, owner) is not { } map)
        {
            refusal = $"Inserting a '{slotName}' block at line {placement.Line} would change other blocks of the page.";
            return null;
        }

        return new MarkdownStructureChange(
            new MarkdownBlockWrite(after, MapSubjects(blockSubjects, map), [], HasInsertedOrRemovedBlocks: true), owner);
    }

    /// <summary>
    /// Gets the block whose content <paramref name="fragment"/> shows: the block of the subject that holds it in its
    /// default slot, or the slot block that holds it for a named or a list slot. Null when no block of the page holds it.
    /// </summary>
    public static MarkdownBlock? FindContentBlock(
        MarkdownPage page, IReadOnlyDictionary<int, IInterceptorSubject> blockSubjects, IInterceptorSubject fragment)
    {
        foreach (var (index, subject) in blockSubjects)
        {
            foreach (var slot in SlotMap.For(subject.GetType()).Slots)
            {
                if (!slot.IsOpen)
                {
                    continue;
                }

                var value = slot.GetValue(subject);
                var position = value is Array values ? Array.IndexOf(values, fragment) : ReferenceEquals(value, fragment) ? 0 : -1;
                if (position < 0)
                {
                    continue;
                }

                var block = page.AllBlocks[index];
                return slot.IsDefault && !slot.IsList
                    ? block
                    : MarkdownBlockWriter.GetSlotBlocks(block, slot.Name).ElementAtOrDefault(position);
            }
        }

        return null;
    }
```

`CreateBlock(string, string?, string, int, string, bool, string? content = null)` is the existing internal overload.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests --filter "FullyQualifiedName~MarkdownPageOperationsTests"`
Expected: PASS. If `WhenThePageHasNoClosingLines_ThenTheInsertedWidgetHasNoneEither` fails on the empty line after the new block, check that `InsertBlockText` adds one only when the line at the insert position is not empty.

- [ ] **Step 5: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Storage/Pages/MarkdownPageOperations.cs src/HomeBlaze/HomeBlaze.Storage.Tests/Pages/MarkdownPageOperationsTests.cs
git commit -m "feat: insert widget and slot blocks in the text of a page"
```

---

### Task 4: Structure edits and queries on `MarkdownFile`

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Storage/Pages/MarkdownEditResult.cs`, `MarkdownBlockActions.cs`, `MarkdownListSlot.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Storage/Files/MarkdownFile.cs`
- Test: `src/HomeBlaze/HomeBlaze.Storage.Tests/Pages/MarkdownStructureEditTests.cs`, `VerifyChecksTests.PublicApi.verified.txt`

**Interfaces:**
- Consumes: Tasks 2 and 3 operations; `MarkdownBlockConfiguration.Serialize(ConfigurableSubjectSerializer, IInterceptorSubject, MarkdownBlock?)`; `MarkdownContentParser.FindType` is not needed.
- Produces (all public on `MarkdownFile`):
  - `Task<MarkdownEditResult> DeleteBlockAsync(IInterceptorSubject subject, CancellationToken cancellationToken)`
  - `Task<MarkdownEditResult> MoveBlockAsync(IInterceptorSubject subject, int direction, CancellationToken cancellationToken)`
  - `Task<MarkdownEditResult> InsertWidgetAsync(IInterceptorSubject content, IInterceptorSubject? before, IInterceptorSubject widget, string? name, CancellationToken cancellationToken)`
  - `Task<MarkdownEditResult> AddSlotItemAsync(IInterceptorSubject owner, string slotName, IInterceptorSubject? item, CancellationToken cancellationToken)`
  - `MarkdownBlockActions GetBlockActions(IInterceptorSubject subject)`
  - `IReadOnlyList<MarkdownListSlot> GetListSlots(IInterceptorSubject owner)`
  - `string? GetBlockName(IInterceptorSubject subject)`
  - `bool IsContentOfPage(IInterceptorSubject content)`
  - `public readonly record struct MarkdownEditResult(string? Refusal)` with `IsWritten`, `Written`, `Refused(string)`
  - `public readonly record struct MarkdownBlockActions(bool CanDelete, bool CanMoveUp, bool CanMoveDown, int ContainedBlockCount)`
  - `public sealed record MarkdownListSlot(string Name, Type? ItemType)`

- [ ] **Step 1: Write the failing tests**

Create `MarkdownStructureEditTests.cs`:

```csharp
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
```

`panel.Rows[0].Content` is the `MarkdownFragment` of the row body: the parser gives every open default slot a fragment, also an empty one. If the row has no content and `Content` is null, change the page to `::: Row Label=A\ntext\n:::` for that assertion.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests --filter "FullyQualifiedName~MarkdownStructureEditTests"`
Expected: build error, the methods do not exist.

- [ ] **Step 3: Add the result and query types**

`MarkdownEditResult.cs`:

```csharp
namespace HomeBlaze.Storage.Pages;

/// <summary>Whether a change of the blocks of a page was written, and why not when it was refused.</summary>
/// <param name="Refusal">Why nothing was written, or null when the change was written.</param>
public readonly record struct MarkdownEditResult(string? Refusal)
{
    /// <summary>Gets whether the change was written.</summary>
    public bool IsWritten => Refusal is null;

    /// <summary>A change that was written.</summary>
    public static MarkdownEditResult Written => default;

    /// <summary>A change that was refused for <paramref name="refusal"/>.</summary>
    public static MarkdownEditResult Refused(string refusal) => new(refusal);
}
```

`MarkdownBlockActions.cs`:

```csharp
namespace HomeBlaze.Storage.Pages;

/// <summary>What the block of a subject allows on its page, all false for a subject without a block.</summary>
/// <param name="CanDelete">Whether the subject has a block that can be deleted.</param>
/// <param name="CanMoveUp">Whether the block can swap with the block before it.</param>
/// <param name="CanMoveDown">Whether the block can swap with the block after it.</param>
/// <param name="ContainedBlockCount">How many blocks are inside the block and go with it.</param>
public readonly record struct MarkdownBlockActions(bool CanDelete, bool CanMoveUp, bool CanMoveDown, int ContainedBlockCount);
```

`MarkdownListSlot.cs`:

```csharp
namespace HomeBlaze.Storage.Pages;

/// <summary>A slot of a block that holds a list of blocks, such as the rows of a list.</summary>
/// <param name="Name">The name of the slot blocks.</param>
/// <param name="ItemType">The type of a value of the slot, or null for a slot whose blocks hold page content.</param>
public sealed record MarkdownListSlot(string Name, Type? ItemType);
```

- [ ] **Step 4: Extract the publish step of `WriteBlockAsync`**

In `MarkdownFile.cs`, replace everything in `WriteBlockAsync` from the `Thread.MemoryBarrier();` comment to the end of the method with a call, and add the helper. Keep the existing refusal texts, which tests assert:

```csharp
        return await PublishAndWriteAsync(snapshot, write, page.AllBlocks[blockIndex], ConfigurationChangedRefusal) is null;
    }

    private const string ReloadRefusal = "Blocks cannot be inserted or removed until the page has been read again.";
    private const string ConfigurationChangedRefusal = "The page changed while the configuration was written; save again.";
    private const string StructureChangedRefusal = "The page changed while it was edited. Try again.";

    // Under _writeLock: publishes the write as the snapshot that follows `snapshot` and writes its text. Returns why
    // nothing was written, or null. `changedBlock` is the block of the snapshot whose text changed, null for the page.
    private async Task<string?> PublishAndWriteAsync(
        MarkdownParseResult snapshot, MarkdownBlockWrite write, MarkdownBlock? changedBlock, string changedPageRefusal)
    {
        // After the graph was read and before anything is published: the blocks were compared with slot values
        // that a reload may have changed since the snapshot was parsed. A reload that published since then fails
        // the exchange below. A full fence, because a volatile read lets the earlier reads of the graph pass it
        // (see OnFileChangedAsync).
        Thread.MemoryBarrier();
        if (write.HasInsertedOrRemovedBlocks && _isGraphAheadOfSnapshot)
        {
            _parser.LogRefusedWrite(FullPath, ReloadRefusal);
            return ReloadRefusal;
        }

        // The text was computed from the snapshot, so it would undo what a reload published since then.
        var published = snapshot with { Page = write.Page, BlockSubjects = write.BlockSubjects };
        if (!ReferenceEquals(Interlocked.CompareExchange(ref _parseResult, published, snapshot), snapshot))
        {
            _parser.LogRefusedWrite(FullPath, changedPageRefusal);
            return changedPageRefusal;
        }

        // The text of the block and of the blocks around it changes in the file, so a later read of the text that
        // they had before is a change that must be applied, also when the read inside the write is skipped or fails.
        // The blocks inside it keep their text.
        if (changedBlock is not null)
        {
            ForgetAppliedBlocksAround(snapshot.Page!, changedBlock, snapshot.BlockSubjects);
        }

        // Before the write, because the storage reloads this file from inside the write. The reload
        // must find the keys that are now in the file, or a value that became null would recreate the subject.
        foreach (var (writtenSubject, configurationJson) in write.Configurations)
        {
            _parser.RememberConfiguration(writtenSubject, configurationJson);
        }

        var content = write.Page.Source;
        Content = content;

        await WriteUnlockedAsync(content);
        return null;
    }
```

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests --filter "FullyQualifiedName~MarkdownSlotWriteBackTests|FullyQualifiedName~MarkdownFileConcurrentWriteTests|FullyQualifiedName~MarkdownFileWriteTests"`
Expected: PASS (pure refactor).

- [ ] **Step 5: Add the structure edits and queries**

Add to `MarkdownFile.cs` after `WriteConfigurationAsync`:

```csharp
    /// <summary>Deletes the block of <paramref name="subject"/> with everything in it and saves the page.</summary>
    /// <exception cref="InvalidOperationException">The call comes from work on the worker of the storage or from within a write of this page.</exception>
    public Task<MarkdownEditResult> DeleteBlockAsync(IInterceptorSubject subject, CancellationToken cancellationToken) =>
        EditStructureAsync(
            (page, subjects) => FindOwnBlock(page, subjects, subject) is { } block
                ? (MarkdownPageOperations.TryRemove(page, subjects, block, out var refusal), refusal)
                : (null, NoBlockRefusal),
            cancellationToken);

    /// <summary>
    /// Swaps the block of <paramref name="subject"/> with the block of the same slot before it (<paramref name="direction"/>
    /// -1) or after it (1) and saves the page. The text between the two blocks stays in place.
    /// </summary>
    /// <exception cref="InvalidOperationException">The call comes from work on the worker of the storage or from within a write of this page.</exception>
    public Task<MarkdownEditResult> MoveBlockAsync(IInterceptorSubject subject, int direction, CancellationToken cancellationToken) =>
        EditStructureAsync(
            (page, subjects) => FindOwnBlock(page, subjects, subject) is { } block
                ? (MarkdownPageOperations.TryMove(page, subjects, block, direction, out var refusal), refusal)
                : (null, NoBlockRefusal),
            cancellationToken);

    /// <summary>
    /// Inserts a block for <paramref name="widget"/>, with its configuration and <paramref name="name"/> after <c>#</c>,
    /// into <paramref name="content"/>: this page or a fragment of one of its blocks. It goes before the block of
    /// <paramref name="before"/>, or after the content when that is null. The page then creates its own subject for it.
    /// </summary>
    /// <exception cref="InvalidOperationException">The call comes from work on the worker of the storage or from within a write of this page.</exception>
    public Task<MarkdownEditResult> InsertWidgetAsync(
        IInterceptorSubject content, IInterceptorSubject? before, IInterceptorSubject widget, string? name, CancellationToken cancellationToken)
    {
        var typeName = widget.GetType().FullName!;
        var configurationJson = MarkdownBlockConfiguration.Serialize(_serializer, widget, block: null);
        return EditStructureAsync(
            (page, subjects) =>
            {
                MarkdownBlock? owner = null;
                if (!ReferenceEquals(content, this) &&
                    (owner = MarkdownPageOperations.FindContentBlock(page, subjects, content)) is null)
                {
                    return (null, "The content is not on this page.");
                }

                MarkdownBlock? beforeBlock = null;
                if (before is not null && (beforeBlock = FindOwnBlock(page, subjects, before)) is null)
                {
                    return (null, NoBlockRefusal);
                }

                return (MarkdownPageOperations.TryInsertWidget(page, subjects, owner, beforeBlock, typeName, name, configurationJson, out var refusal), refusal);
            },
            cancellationToken);
    }

    /// <summary>
    /// Appends a block to the list slot <paramref name="slotName"/> of the block of <paramref name="owner"/>: with the
    /// configuration of <paramref name="item"/>, or empty for a slot whose blocks hold page content (null item).
    /// </summary>
    /// <exception cref="InvalidOperationException">The call comes from work on the worker of the storage or from within a write of this page.</exception>
    public Task<MarkdownEditResult> AddSlotItemAsync(
        IInterceptorSubject owner, string slotName, IInterceptorSubject? item, CancellationToken cancellationToken)
    {
        var configurationJson = item is null ? "{}" : MarkdownBlockConfiguration.Serialize(_serializer, item, block: null);
        return EditStructureAsync(
            (page, subjects) =>
            {
                if (FindOwnBlock(page, subjects, owner) is not { } block)
                {
                    return (null, NoBlockRefusal);
                }

                if (!SlotMap.For(owner.GetType()).Named.TryGetValue(slotName, out var slot) || !slot.IsList ||
                    (item is null ? !slot.IsOpen : item.GetType() != slot.ValueType))
                {
                    return (null, $"The block has no list slot '{slotName}' for this item.");
                }

                return (MarkdownPageOperations.TryAppendSlotBlock(page, subjects, block, slotName, configurationJson, out var refusal), refusal);
            },
            cancellationToken);
    }

    /// <summary>Gets what the block of <paramref name="subject"/> allows, all false when it has no block on this page.</summary>
    public MarkdownBlockActions GetBlockActions(IInterceptorSubject subject)
    {
        var snapshot = _parseResult;
        if (snapshot.Page is not { } page || FindOwnBlock(page, snapshot.BlockSubjects, subject) is not { } block)
        {
            return default;
        }

        return new MarkdownBlockActions(
            CanDelete: true,
            CanMoveUp: MarkdownPageOperations.FindMoveTarget(page, block, -1) is not null,
            CanMoveDown: MarkdownPageOperations.FindMoveTarget(page, block, 1) is not null,
            ContainedBlockCount: MarkdownPageOperations.CountBlocks(block) - 1);
    }

    /// <summary>Gets the list slots of the block of <paramref name="owner"/>, none when it has no block on this page.</summary>
    public IReadOnlyList<MarkdownListSlot> GetListSlots(IInterceptorSubject owner)
    {
        var snapshot = _parseResult;
        if (snapshot.Page is not { } page || FindOwnBlock(page, snapshot.BlockSubjects, owner) is null)
        {
            return [];
        }

        var result = new List<MarkdownListSlot>();
        foreach (var slot in SlotMap.For(owner.GetType()).Slots)
        {
            if (slot.IsList)
            {
                result.Add(new MarkdownListSlot(slot.Name, slot.IsOpen ? null : slot.ValueType));
            }
        }

        return result;
    }

    /// <summary>Gets the name after <c>#</c> of the block of <paramref name="subject"/>, or null without one.</summary>
    public string? GetBlockName(IInterceptorSubject subject)
    {
        var snapshot = _parseResult;
        return snapshot.Page is { } page ? FindOwnBlock(page, snapshot.BlockSubjects, subject)?.Head?.Id : null;
    }

    /// <summary>Gets whether <paramref name="content"/> is this page or the content of one of its blocks.</summary>
    public bool IsContentOfPage(IInterceptorSubject content)
    {
        var snapshot = _parseResult;
        return ReferenceEquals(content, this) ||
               (snapshot.Page is { } page && MarkdownPageOperations.FindContentBlock(page, snapshot.BlockSubjects, content) is not null);
    }

    private const string NoBlockRefusal = "The subject has no block on the page.";

    private async Task<MarkdownEditResult> EditStructureAsync(
        Func<MarkdownPage, IReadOnlyDictionary<int, IInterceptorSubject>, (MarkdownStructureChange? Change, string? Refusal)> operation,
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

            // The operations change only text, so a refusal leaves the graph as the snapshot has it.
            var (change, refusal) = operation(page, snapshot.BlockSubjects);
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

    // The block whose own subject the subject is. Unlike FindBlock, not the block that holds the subject in its
    // configuration, such as the grid of a cell, which a delete must never take.
    private static MarkdownBlock? FindOwnBlock(
        MarkdownPage page, IReadOnlyDictionary<int, IInterceptorSubject> blockSubjects, IInterceptorSubject subject)
    {
        foreach (var (index, candidate) in blockSubjects)
        {
            if (ReferenceEquals(candidate, subject))
            {
                return page.AllBlocks[index];
            }
        }

        return null;
    }
```

Move the three `const` refusal strings next to the other constants at the top of the class.

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests --filter "FullyQualifiedName~MarkdownStructureEditTests"`
Expected: PASS. If `WhenARowIsMovedDown_ThenTheFileAndTheSlotHaveTheNewOrder` shows the old order after the reload, inspect how `MarkdownContentParser` reuses list slot values by position. The fix belongs in the reload, not in the test.

- [ ] **Step 7: Update the public API snapshot and run the whole project**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests --filter "FullyQualifiedName~VerifyChecksTests"`, check that only the members listed under Interfaces were added, copy the `.received.txt` over the `.verified.txt`.
Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Storage src/HomeBlaze/HomeBlaze.Storage.Tests
git commit -m "feat: delete, move and insert blocks of a markdown page"
```

---

### Task 5: Bold block names and the shared insert in the source editor

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.Storage.Blazor/Models/BlockOutline.cs`, `Models/MarkdownDecorationParser.cs`, `Models/BlockOutlineDecorations.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Storage.Blazor/Files/MarkdownFileEditComponent.razor`, `Files/MarkdownFileEditComponent.razor.js`
- Test: `src/HomeBlaze/HomeBlaze.Storage.Blazor.Tests/BlockOutlineTests.cs`

**Interfaces:**
- Consumes: `MarkdownBlockWriter.InsertBlockText(string, int, string, string)` (Task 1).
- Produces: `BlockOutline.NamePosition` (`FencePosition?`, the line, column and length of the name), `BlockOutlineDecorations.NameKind = 6`.

- [ ] **Step 1: Write the failing tests**

Add to `BlockOutlineTests`:

```csharp
    [Theory]
    [InlineData("Text\n\n:: Test.Alert#info\n::", 3, 4, 10)]
    [InlineData("::Demo.Card Title=A\n::", 1, 3, 9)]
    [InlineData(":: Demo.Card\n::: Tab#cooling Title=\"Cooling Fan\"\n:::\n::", 2, 5, 3)]
    public void WhenABlockHasAName_ThenTheOutlineHasThePositionOfTheNameWithoutIdAndArguments(string content, int line, int column, int length)
    {
        // Act
        var outline = MarkdownDecorationParser.ParseDecorations(content).Outlines.Last();

        // Assert
        Assert.Equal(new FencePosition(line, column, length), outline.NamePosition);
    }

    [Fact]
    public void WhenDecorationsAreCreated_ThenEveryNamedHeadLineHasANameRecord()
    {
        // Arrange
        var parsed = MarkdownDecorationParser.ParseDecorations(":: Demo.Card\n::: Tab\n:::\n::");

        // Act
        var values = BlockOutlineDecorations.Create(parsed.Regions, parsed.Outlines).Values;

        // Assert
        var nameRecords = values.Chunk(BlockOutlineDecorations.RecordLength)
            .Where(record => record[0] == BlockOutlineDecorations.NameKind)
            .Select(record => (record[2], record[3], record[5]))
            .ToList();
        Assert.Equal([(1, 4, 13), (2, 5, 8)], nameRecords);
    }
```

A name record has the start line, the start column and the end column (exclusive) in positions 2, 3 and 5.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Blazor.Tests --filter "FullyQualifiedName~BlockOutlineTests"`
Expected: build error, `NamePosition` and `NameKind` do not exist.

- [ ] **Step 3: Record the position of the name**

In `BlockOutline.cs`, add a last parameter and its doc:

```csharp
/// <param name="NamePosition">Where the type name of a widget block or the name of a slot block stands on the head line, without its <c>#</c> name and arguments, or null for a block without a head.</param>
public record BlockOutline(
    int StartLine,
    int EndLine,
    int? ConfigurationStartLine,
    int HeadEndLine,
    int HeadEndColumn,
    int Depth,
    string Name,
    FencePosition HeadFence,
    FencePosition? ClosingFence,
    FencePosition? NamePosition = null);
```

Update the summary of `FencePosition` to "The place of characters on a line, such as the fence characters or the name of a head line."

In `MarkdownDecorationParser.AddOutlines`, before `outlines.Add`, compute the name position and pass it as the last argument:

```csharp
            FencePosition? namePosition = null;
            if (block.Head is { } blockHead)
            {
                var nameStart = block.HeadSpan.Start + block.FenceLength;
                while (nameStart < block.HeadSpan.End && content[nameStart] is ' ' or '\t')
                {
                    nameStart++;
                }

                if (string.CompareOrdinal(content, nameStart, blockHead.Name, 0, blockHead.Name.Length) == 0)
                {
                    var (nameLine, nameColumn) = GetLineAndColumn(lineStarts, nameStart);
                    namePosition = new FencePosition(nameLine, nameColumn, blockHead.Name.Length);
                }
            }
```

In `BlockOutlineDecorations`:

```csharp
    /// <summary>The name on a head line, in bold.</summary>
    public const int NameKind = 6;
```

Count one more record for every outline with a name (`recordCount += 2 + (outline.NamePosition is not null ? 1 : 0) + ...`) and write it after the fence:

```csharp
            if (outline.NamePosition is { } namePosition)
            {
                WriteFence(values, ref index, NameKind, color, namePosition);
            }
```

- [ ] **Step 4: Style the name in the editor**

In `MarkdownFileEditComponent.razor.js`, add `const Name = 6;` next to the other kinds (the comment there says they must match) and a case in `update`:

```js
            case Name:
                decorations.push({ range, options: { description: "block-name", inlineClassName: "block-name" } });
                break;
```

In the `<style>` of `MarkdownFileEditComponent.razor`, replace the `.block-fence` rule and add the name rule:

```css
    .monaco-editor .block-fence { color: rgb(var(--block-color)) !important; }
    .monaco-editor .block-name { font-weight: bold; }
```

- [ ] **Step 5: Use the shared insert for Create**

In `OnCreateClick` of `MarkdownFileEditComponent.razor`, replace the lines from `var lines = _currentContent.Split(lineEnding);` up to and including `SetCurrentContent(string.Join(lineEnding, newLines));` with:

```csharp
        // Inside a block, the new block goes after the json block of that block and gets one colon more
        // than the block that it is then inside of. It always gets a closing line, so that the lines
        // after it are not inside it.
        var editorPage = MarkdownPageParser.Parse(_currentContent);
        var lineCount = _currentContent.Split(lineEnding).Length;
        var insertLine = Math.Min(editorPage.GetLineForNewBlock(position?.LineNumber ?? lineCount), lineCount + 1);
        var fenceLength = editorPage.GetFenceLengthForNewBlock(
            insertLine, name => TypeProvider.Types.FirstOrDefault(type => type.FullName == name));
        var blockText = MarkdownBlockWriter.CreateBlock(typeName, result.Name, json, fenceLength, lineEnding);

        SetCurrentContent(MarkdownBlockWriter.InsertBlockText(_currentContent, insertLine, blockText, lineEnding));
```

Keep the `var position = await _editor.GetPosition();` line above it.

- [ ] **Step 6: Run the tests**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Blazor.Tests`
Expected: PASS.
Run: `dotnet test src/HomeBlaze/HomeBlaze.E2E.Tests --filter "FullyQualifiedName~MarkdownEditorTests|FullyQualifiedName~PageEditTests"`
Expected: PASS. A test that asserted the exact text after Create keeps passing, because `InsertBlockText` adds an empty line wherever the old splicing did (next to any line that is not empty).

- [ ] **Step 7: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Storage.Blazor src/HomeBlaze/HomeBlaze.Storage.Blazor.Tests
git commit -m "feat: show block names in bold in the page editor"
```

---

### Task 6: The structure editor abstraction and the action bar

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Components/IPageStructureEditor.cs`, `PageBlockActions.cs`, `PageListSlot.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Components/WidgetSelection.cs`, `SubjectComponent.razor`
- Modify: `src/HomeBlaze/HomeBlaze.Components/Dialogs/SubjectSetupOptions.cs`, `Dialogs/SubjectSetupDialog.razor`
- Test: `src/HomeBlaze/HomeBlaze.Components.Tests/VerifyChecksTests.PublicApi.verified.txt`

**Interfaces:**
- Produces:

```csharp
public interface IPageStructureEditor
{
    PageBlockActions GetActions(IInterceptorSubject subject);
    IReadOnlyList<PageListSlot> GetListSlots(IInterceptorSubject owner);
    string? GetBlockName(IInterceptorSubject subject);
    bool CanInsertInto(IInterceptorSubject content);
    Task DeleteAsync(IInterceptorSubject subject);
    Task MoveAsync(IInterceptorSubject subject, int direction);
    Task InsertWidgetAsync(IInterceptorSubject content, IInterceptorSubject? before);
    Task AddListItemAsync(IInterceptorSubject owner, PageListSlot slot);
}
public readonly record struct PageBlockActions(bool CanDelete, bool CanMoveUp, bool CanMoveDown);
public sealed record PageListSlot(string Name, Type? ItemType);
```

  - `WidgetSelection.Changed` (`event Action?`), raised after every change of the selection.
  - `SubjectComponent.ContainsSelection` (`bool`): the selected widget is this one or inside it.
  - `SubjectComponent.GetSelectableSelfOrAncestor()` (`SubjectComponent?`).
  - `SubjectComponent.SelectableAncestor` (`SubjectComponent?`, the former private `SelectableParent`, now public).
  - `SubjectSetupOptions.Type` (`Type?`): the type to create, which skips the picker.
  - Test ids: `move-up-button`, `move-down-button`, `delete-block-button`, `add-slot-item-button-{slot name}`.

- [ ] **Step 1: Add the abstraction**

`IPageStructureEditor.cs`:

```csharp
using Namotion.Interceptor;

namespace HomeBlaze.Components;

/// <summary>
/// Changes the blocks of the page that is edited in place. A page cascades it while it is edited inline, and the
/// widgets show their structure actions only then.
/// </summary>
public interface IPageStructureEditor
{
    /// <summary>Gets what the block of the subject allows, all false when the subject has no block of its own.</summary>
    PageBlockActions GetActions(IInterceptorSubject subject);

    /// <summary>Gets the slots of the block of <paramref name="owner"/> that hold lists of blocks.</summary>
    IReadOnlyList<PageListSlot> GetListSlots(IInterceptorSubject owner);

    /// <summary>Gets the name of the block of the subject on the page, or null without one.</summary>
    string? GetBlockName(IInterceptorSubject subject);

    /// <summary>Gets whether new blocks can be inserted into <paramref name="content"/>, the page or a fragment of it.</summary>
    bool CanInsertInto(IInterceptorSubject content);

    /// <summary>Asks for confirmation and deletes the block of the subject.</summary>
    Task DeleteAsync(IInterceptorSubject subject);

    /// <summary>Swaps the block of the subject with the block before it (-1) or after it (1).</summary>
    Task MoveAsync(IInterceptorSubject subject, int direction);

    /// <summary>Asks for a widget and inserts it into <paramref name="content"/> before <paramref name="before"/>, or at its end.</summary>
    Task InsertWidgetAsync(IInterceptorSubject content, IInterceptorSubject? before);

    /// <summary>Asks for the item when the slot has a type and appends it to the slot of <paramref name="owner"/>.</summary>
    Task AddListItemAsync(IInterceptorSubject owner, PageListSlot slot);
}
```

`PageBlockActions.cs`:

```csharp
namespace HomeBlaze.Components;

/// <summary>The structure actions that the block of a widget allows.</summary>
public readonly record struct PageBlockActions(bool CanDelete, bool CanMoveUp, bool CanMoveDown);
```

`PageListSlot.cs`:

```csharp
namespace HomeBlaze.Components;

/// <summary>A slot of a block that holds a list of blocks.</summary>
/// <param name="Name">The name of its blocks, such as Value or Tab.</param>
/// <param name="ItemType">The type of an item, or null when an item holds page content.</param>
public sealed record PageListSlot(string Name, Type? ItemType);
```

- [ ] **Step 2: Tell when the selection changes**

In `WidgetSelection.cs`:

```csharp
    /// <summary>
    /// Raised after the selection changed, for components that depend on where the selection is, such as a widget
    /// that shows more while the selection is inside it.
    /// </summary>
    public event Action? Changed;
```

and at the end of `Select`, after `component?.OnSelectionChanged();`, add `Changed?.Invoke();`. In `Remove`, after `Selected = null;`, add `Changed?.Invoke();`.

- [ ] **Step 3: Add the selection helpers and the structure buttons to `SubjectComponent`**

In the `@code` of `SubjectComponent.razor`:

```csharp
    /// <summary>
    /// The editor of the structure of the page, cascaded while the page is edited inline. Without it no structure action
    /// is shown.
    /// </summary>
    [CascadingParameter]
    public IPageStructureEditor? StructureEditor { get; set; }

    /// <summary>Gets whether the selected widget is this one or a widget inside it.</summary>
    public bool ContainsSelection
    {
        get
        {
            for (var component = Selection?.Selected; component is not null; component = component.ParentComponent)
            {
                if (ReferenceEquals(component, this))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Gets this widget when it can be selected, otherwise the nearest widget around it that can, or null.</summary>
    public SubjectComponent? GetSelectableSelfOrAncestor() => IsSelectable ? this : SelectableAncestor;
```

Rename the private property `SelectableParent` to `public SubjectComponent? SelectableAncestor` with the doc "Gets the nearest widget around this one that can be selected, or null." and update its two uses.

Create the callbacks once in `OnInitialized`:

```csharp
        _moveUpCallback = EventCallback.Factory.Create(this, () => StructureEditor!.MoveAsync(Subject!, -1));
        _moveDownCallback = EventCallback.Factory.Create(this, () => StructureEditor!.MoveAsync(Subject!, 1));
        _deleteCallback = EventCallback.Factory.Create(this, () => StructureEditor!.DeleteAsync(Subject!));
```

with the fields `private EventCallback _moveUpCallback; private EventCallback _moveDownCallback; private EventCallback _deleteCallback;` next to `_editCallback`.

In the markup, replace the `@{ var selectableParent ... }` block and the condition of the bar:

```razor
            @{
                var selectableParent = isSelected ? SelectableAncestor : null;
                var hasEditAction = isSelected && HasEditComponent;
                var structureEditor = isSelected ? StructureEditor : null;
                var actions = structureEditor?.GetActions(Subject) ?? default;
                var listSlots = structureEditor?.GetListSlots(Subject) ?? [];
            }
            @if (isSelected && (selectableParent != null || hasEditAction || actions.CanDelete || listSlots.Count > 0))
```

Inside `subject-action-bar-content`, after the select parent button:

```razor
                            @if (actions.CanMoveUp)
                            {
                                <MudIconButton Icon="@Icons.Material.Filled.KeyboardArrowUp"
                                               Size="Size.Small"
                                               title="Move up"
                                               aria-label="Move up"
                                               OnClick="_moveUpCallback"
                                               data-testid="move-up-button" />
                            }

                            @if (actions.CanMoveDown)
                            {
                                <MudIconButton Icon="@Icons.Material.Filled.KeyboardArrowDown"
                                               Size="Size.Small"
                                               title="Move down"
                                               aria-label="Move down"
                                               OnClick="_moveDownCallback"
                                               data-testid="move-down-button" />
                            }

                            @foreach (var slot in listSlots)
                            {
                                @* Only the selected widget renders its bar, so the lambda allocates for one widget. *@
                                <MudIconButton Icon="@Icons.Material.Filled.Add"
                                               Size="Size.Small"
                                               title="@($"Add {slot.Name}")"
                                               aria-label="@($"Add {slot.Name}")"
                                               OnClick="@(() => structureEditor!.AddListItemAsync(Subject!, slot))"
                                               data-testid="@($"add-slot-item-button-{slot.Name}")" />
                            }
```

and after the edit button:

```razor
                            @if (actions.CanDelete)
                            {
                                <MudIconButton Icon="@Icons.Material.Filled.Delete"
                                               Size="Size.Small"
                                               Color="Color.Error"
                                               title="Delete"
                                               aria-label="Delete"
                                               OnClick="_deleteCallback"
                                               data-testid="delete-block-button" />
                            }
```

- [ ] **Step 4: Let the setup dialog create a given type**

In `SubjectSetupOptions.cs`:

```csharp
    /// <summary>
    /// The type to create, which skips the picker and has no way back to it, or null to pick the type in the dialog.
    /// </summary>
    public Type? Type { get; init; }
```

In `SubjectSetupDialog.razor`, at the end of `OnInitialized`:

```csharp
        if (Options.Type is { } type)
        {
            SelectType(SubjectTypeCatalog.Describe(type));
        }
```

and change the condition of the Back button from `@if (_selectedType is not null)` to `@if (_selectedType is not null && Options.Type is null)`. Keep the `<MudSpacer />` inside the same condition.

- [ ] **Step 5: Build, update the public API snapshot and run the selection tests**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: success.
Run: `dotnet test src/HomeBlaze/HomeBlaze.Components.Tests --filter "FullyQualifiedName~VerifyChecksTests"`, check that only the members listed under Interfaces were added, copy `.received.txt` over `.verified.txt`, run again (PASS).
Run: `dotnet test src/HomeBlaze/HomeBlaze.E2E.Tests --filter "FullyQualifiedName~WidgetSelectionTests|FullyQualifiedName~SubjectSetupDialogTests"`
Expected: PASS. Nothing cascades an editor yet, so the bar is unchanged.

- [ ] **Step 6: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Components src/HomeBlaze/HomeBlaze.Components.Tests
git commit -m "feat: structure actions in the bar of a selected widget"
```

---

### Task 7: The markdown structure editor in Inline mode

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Storage.Blazor/Files/MarkdownPageStructureEditor.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Storage.Blazor/Files/MarkdownFilePageComponent.razor`
- Create: `src/HomeBlaze/HomeBlaze.E2E.Tests/TestData/Demo/Structure.md`, `src/HomeBlaze/HomeBlaze.E2E.Tests/InlineStructureEditingTests.cs`

**Interfaces:**
- Consumes: `MarkdownFile` methods (Task 4), `IPageStructureEditor` (Task 6), `SubjectSetupDialog.ShowAsync`, `SubjectSetupOptions.Type`, `MarkdownBlockNames.CreateNameOptions(string)`, `SubjectFactory.DiscardSubject`.
- Produces: the cascaded `IPageStructureEditor` in Inline mode; the E2E page `Structure.md` and test class used by Tasks 8 to 12.

- [ ] **Step 1: Write the E2E test page and the failing tests**

`TestData/Demo/Structure.md` (the tests write it again before each test, this file only makes the page exist):

```markdown
---
title: Structure Demo
---

# Structure Demo

:: HomeBlaze.Samples.Motor#structuremotor Name=StructureMotor TargetSpeed=1000 SimulationInterval=00:00:01
::

:: HomeBlaze.Components.Switch Path=structuremotor/IsPowered
::

:: HomeBlaze.Components.ValueList
::: Value Path=structuremotor/TargetSpeed Label="Row A"
:::
::: Value Path=structuremotor/CurrentSpeed Label="Row B"
:::
::

:: HomeBlaze.Components.ValueMap#structuremap
::: Case Value=Stopped Text="Off"
:::
::

:: HomeBlaze.Components.Responsive To=Xs
::: HomeBlaze.Components.Card Title="Phone Card"
Only on phones.
:::
::
```

`InlineStructureEditingTests.cs`:

```csharp
using HomeBlaze.E2E.Tests.Infrastructure;
using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for the structure editing of a markdown page in inline edit mode: delete, move and insert blocks, the items
/// of list slots, the widgets that render nothing, the content of a Responsive block and the preview of the Split mode.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class InlineStructureEditingTests
{
    private const int Timeout = 30000;
    private const string PageName = "Structure.md";

    private static readonly string Content = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "Demo", PageName))
        .Replace("\r\n", "\n");

    private readonly PlaywrightFixture _fixture;

    public InlineStructureEditingTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task WhenDeleteIsConfirmed_ThenTheBlockIsRemovedFromThePage()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();
        var actionBar = await GetWrapperOf(page, "switch").SelectWidgetAsync();

        // Act: the bar has a button named Delete as well, so the one of the dialog is looked up in the dialog.
        await actionBar.GetByTestId("delete-block-button").ClickAsync();
        await page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "Delete", Exact = true }).ClickAsync();

        // Assert
        await _fixture.WaitForDemoPageTextAsync(
            PageName, text => !text.Contains("HomeBlaze.Components.Switch"), "The switch block was not deleted.");
        await Assertions.Expect(page.GetByTestId("switch")).ToHaveCountAsync(0, new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenDeleteIsCanceled_ThenThePageIsUnchanged()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();
        var actionBar = await GetWrapperOf(page, "switch").SelectWidgetAsync();

        // Act
        await actionBar.GetByTestId("delete-block-button").ClickAsync();
        await page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();

        // Assert
        await Assertions.Expect(page.GetByRole(AriaRole.Dialog)).ToHaveCountAsync(0, new() { Timeout = Timeout });
        Assert.Equal(Content, (await _fixture.ReadFileAsync(_fixture.GetDemoPagePath(PageName))).Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task WhenABlockIsMovedDown_ThenItSwapsWithTheNextBlockInTheFile()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();
        var actionBar = await GetWrapperOf(page, "switch").SelectWidgetAsync();

        // Act
        await actionBar.GetByTestId("move-down-button").ClickAsync();

        // Assert
        await _fixture.WaitForDemoPageTextAsync(
            PageName,
            text => text.IndexOf("HomeBlaze.Components.ValueList", StringComparison.Ordinal) <
                    text.IndexOf("HomeBlaze.Components.Switch", StringComparison.Ordinal),
            "The switch was not moved below the value list.");
    }

    [Fact]
    public async Task WhenTheInlineModeIsLeft_ThenTheStructureActionsAreGone()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();
        await GetWrapperOf(page, "switch").SelectWidgetAsync();

        // Act
        await page.SelectEditModeAsync("View", Timeout);

        // Assert
        await Assertions.Expect(page.GetByTestId("delete-block-button")).ToHaveCountAsync(0, new() { Timeout = Timeout });
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

    // The innermost widget around the element with the test id.
    private static ILocator GetWrapperOf(IPage page, string testId) =>
        page.Locator($".subject-component-wrapper.selectable:has([data-testid='{testId}'])")
            .Filter(new() { HasNot = page.Locator($".subject-component-wrapper.selectable:has([data-testid='{testId}'])") })
            .First;
}
```

`ReadFileAsync` and `GetDemoPagePath` exist on `PlaywrightFixture`. Check the test id of the switch in `SwitchView.razor` (`data-testid="switch"`, as `WidgetSelectionTests` uses it).

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.E2E.Tests --filter "FullyQualifiedName~InlineStructureEditingTests"`
Expected: FAIL, no `delete-block-button` or `move-down-button` in the bar.

- [ ] **Step 3: Implement `MarkdownPageStructureEditor`**

```csharp
using HomeBlaze.Components;
using HomeBlaze.Components.Dialogs;
using HomeBlaze.Services;
using HomeBlaze.Storage.Blazor.Models;
using HomeBlaze.Storage.Files;
using HomeBlaze.Storage.Pages;
using MudBlazor;
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Blazor.Files;

/// <summary>Changes the blocks of a markdown page that is edited inline, with the dialogs that ask for them.</summary>
internal sealed class MarkdownPageStructureEditor : IPageStructureEditor
{
    private readonly MarkdownFile _file;
    private readonly IDialogService _dialogService;
    private readonly ISnackbar _snackbar;
    private readonly SubjectFactory _subjectFactory;

    public MarkdownPageStructureEditor(MarkdownFile file, IDialogService dialogService, ISnackbar snackbar, SubjectFactory subjectFactory)
    {
        _file = file;
        _dialogService = dialogService;
        _snackbar = snackbar;
        _subjectFactory = subjectFactory;
    }

    public PageBlockActions GetActions(IInterceptorSubject subject)
    {
        var actions = _file.GetBlockActions(subject);
        return new PageBlockActions(actions.CanDelete, actions.CanMoveUp, actions.CanMoveDown);
    }

    public IReadOnlyList<PageListSlot> GetListSlots(IInterceptorSubject owner)
    {
        var slots = _file.GetListSlots(owner);
        var result = new PageListSlot[slots.Count];
        for (var index = 0; index < slots.Count; index++)
        {
            result[index] = new PageListSlot(slots[index].Name, slots[index].ItemType);
        }

        return result;
    }

    public string? GetBlockName(IInterceptorSubject subject) => _file.GetBlockName(subject);

    public bool CanInsertInto(IInterceptorSubject content) => _file.IsContentOfPage(content);

    public async Task DeleteAsync(IInterceptorSubject subject)
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

        var confirmed = await _dialogService.ShowMessageBoxAsync("Delete block", message, yesText: "Delete", cancelText: "Cancel");
        if (confirmed == true)
        {
            Report(await _file.DeleteBlockAsync(subject, CancellationToken.None));
        }
    }

    public async Task MoveAsync(IInterceptorSubject subject, int direction) =>
        Report(await _file.MoveBlockAsync(subject, direction, CancellationToken.None));

    public async Task InsertWidgetAsync(IInterceptorSubject content, IInterceptorSubject? before)
    {
        var result = await SubjectSetupDialog.ShowAsync(_dialogService, new SubjectSetupOptions
        {
            NameField = MarkdownBlockNames.CreateNameOptions(_file.Content ?? string.Empty)
        });

        if (result is null)
        {
            return;
        }

        // Only the text of the subject goes into the page, and the reload creates the instance that lives on.
        try
        {
            Report(await _file.InsertWidgetAsync(content, before, result.Subject, result.Name, CancellationToken.None));
        }
        finally
        {
            _subjectFactory.DiscardSubject(result.Subject);
        }
    }

    public async Task AddListItemAsync(IInterceptorSubject owner, PageListSlot slot)
    {
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
            Report(await _file.AddSlotItemAsync(owner, slot.Name, item, CancellationToken.None));
        }
        finally
        {
            if (item is not null)
            {
                _subjectFactory.DiscardSubject(item);
            }
        }
    }

    private void Report(MarkdownEditResult result)
    {
        if (!result.IsWritten)
        {
            _snackbar.Add($"The page was not changed. {result.Refusal}", Severity.Warning);
        }
    }
}
```

Check the namespace of `MarkdownFilePageComponent` (`@namespace` or folder default) and use the same for this class. `SubjectTypeCatalog` lives in `HomeBlaze.Services`.

- [ ] **Step 4: Cascade it in Inline mode**

In `MarkdownFilePageComponent.razor`, add `@inject IDialogService DialogService`, `@inject ISnackbar Snackbar`, `@inject SubjectFactory SubjectFactory`, `@using HomeBlaze.Components`, and wrap the content:

```razor
<CascadingValue Value="@IsEditing" Name="IsEditing">
    <CascadingValue Value="@StructureEditor" TValue="IPageStructureEditor">
        <div class="markdown-content" style="position: relative;">
            @if (IsLoading)
            {
                <MudProgressCircular Indeterminate="true" />
            }
            else
            {
                <MarkdownContentView Content="@File" Children="@File?.Children" />
            }
        </div>
    </CascadingValue>
</CascadingValue>
```

and in `@code`:

```csharp
    [CascadingParameter]
    public WidgetSelection? Selection { get; set; }

    private MarkdownPageStructureEditor? _structureEditor;

    // Inline mode is editing with a selection. The Split preview is not editing, so it never changes the page.
    private IPageStructureEditor? StructureEditor =>
        IsEditing && Selection is not null && File is { } file
            ? _structureEditor ??= new MarkdownPageStructureEditor(file, DialogService, Snackbar, SubjectFactory)
            : null;
```

In `MarkdownContentView.razor`, add the parameter now (Tasks 8 and 10 use it):

```csharp
    /// <summary>The page or the fragment whose segments these are, into which new blocks are inserted.</summary>
    [Parameter]
    public IInterceptorSubject? Content { get; set; }
```

and in `MarkdownFragmentWidget.razor` pass it: `<MarkdownContentView Content="@Subject" Children="@((Subject as MarkdownFragment)?.Children)" />`.

- [ ] **Step 5: Run the tests**

Run: `dotnet test src/HomeBlaze/HomeBlaze.E2E.Tests --filter "FullyQualifiedName~InlineStructureEditingTests|FullyQualifiedName~WidgetSelectionTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Storage.Blazor src/HomeBlaze/HomeBlaze.E2E.Tests
git commit -m "feat: delete and move blocks of a markdown page inline"
```

---

### Task 8: Chips for invisible widgets and the cases of a ValueMap

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Components/IInvisibleWidget.cs`, `Components/ValueMapCaseWidget.razor`
- Modify: `src/HomeBlaze/HomeBlaze.Components/Components/ValueMapWidget.razor`, `SubjectComponent.razor.css`
- Modify: `src/HomeBlaze/HomeBlaze.Storage.Blazor/Files/MarkdownContentView.razor`, `Files/MarkdownFilePageComponent.razor.css`
- Test: `src/HomeBlaze/HomeBlaze.E2E.Tests/InlineStructureEditingTests.cs`

**Interfaces:**
- Consumes: `IPageStructureEditor.GetBlockName`, `GetListSlots`, `AddListItemAsync`; `WidgetSelection.Changed`; `SubjectComponent.ContainsSelection`.
- Produces: `public interface IInvisibleWidget` (marker for a widget component that renders nothing outside of edit mode); test ids `invisible-widget-chip`, `value-map-cases`, `value-map-case`, `add-case-button`.

- [ ] **Step 1: Write the failing E2E tests**

Add to `InlineStructureEditingTests`:

```csharp
    [Fact]
    public async Task WhenThePageIsEditedInline_ThenAValueMapIsAChipWithItsName()
    {
        // Arrange & Act
        var page = await OpenPageInInlineModeAsync();

        // Assert
        var chip = page.GetByTestId("invisible-widget-chip").Filter(new() { HasText = "structuremap" });
        await Assertions.Expect(chip).ToBeVisibleAsync(new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenAValueMapChipIsSelected_ThenItsCasesAreListedAndACaseCanBeAdded()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();
        var chip = page.GetByTestId("invisible-widget-chip").Filter(new() { HasText = "structuremap" });
        await chip.SelectWidgetAsync();
        var cases = page.GetByTestId("value-map-cases");
        await Assertions.Expect(cases.GetByTestId("value-map-case")).ToHaveCountAsync(1, new() { Timeout = Timeout });

        // Act
        await cases.GetByTestId("add-case-button").ClickAsync();
        var dialog = page.GetByTestId("create-subject-dialog");
        await dialog.GetByLabel("Value", new() { Exact = true }).FillAsync("Running");
        await dialog.GetByTestId("create-button").ClickAsync();

        // Assert
        await _fixture.WaitForDemoPageTextAsync(
            PageName, text => text.Contains("::: Case Value=Stopped Text=\"Off\"\n:::\n::: Case Value=Running"), "The case was not added.");
    }

    [Fact]
    public async Task WhenThePageIsViewed_ThenNoChipIsShown()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();

        // Act
        await page.SelectEditModeAsync("View", Timeout);

        // Assert
        await Assertions.Expect(page.GetByTestId("invisible-widget-chip")).ToHaveCountAsync(0, new() { Timeout = Timeout });
    }
```

Check the label of the `Value` property of `ValueMapCase` in its `[Configuration]` attributes (`HomeBlaze.Components/ValueMapCase.cs`) and use it in `GetByLabel`.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.E2E.Tests --filter "FullyQualifiedName~InlineStructureEditingTests"`
Expected: the three new tests FAIL (no chip).

- [ ] **Step 3: Add the marker and the case widget**

`IInvisibleWidget.cs`:

```csharp
namespace HomeBlaze.Components;

/// <summary>
/// Marks the widget component of a type that shows nothing on a viewed page, such as a value map. A page that is edited
/// inline shows such a widget as a chip at the top right of the content it stands in.
/// </summary>
public interface IInvisibleWidget;
```

`Components/ValueMapCaseWidget.razor`:

```razor
@attribute [SubjectComponent(SubjectComponentType.Widget, typeof(ValueMapCase))]
@implements ISubjectComponent

@* A case of a value map, which only the case list of a map that is edited inline shows. *@
@if (Subject is ValueMapCase mapCase)
{
    <div class="d-flex align-center gap-2 value-map-case" data-testid="value-map-case">
        <span>@mapCase.Value</span>
        <MudIcon Icon="@Icons.Material.Filled.ArrowForward" Size="Size.Small" aria-hidden="true" />
        <span>@mapCase.Text</span>
    </div>
}

@code {
    [Parameter]
    public IInterceptorSubject? Subject { get; set; }
}
```

Check the property names of `ValueMapCase` (`Value`, `Text`) before writing it.

- [ ] **Step 4: Show the chip and the cases in edit mode**

Replace `Components/ValueMapWidget.razor`:

```razor
@attribute [SubjectComponent(SubjectComponentType.Widget, typeof(ValueMap))]
@implements ISubjectComponent
@implements IInvisibleWidget
@implements IDisposable

@* A map shows nothing on a viewed page. Edited inline, it is a chip, and while it or one of its cases is selected,
   the list of its cases. *@
@if (IsEditing && Selection is not null && Subject is ValueMap map)
{
    <div class="invisible-widget-chip" data-testid="invisible-widget-chip">
        <MudIcon Icon="@Icons.Material.Filled.SwapHoriz" Size="Size.Small" aria-hidden="true" />
        <span>@(StructureEditor?.GetBlockName(map) ?? nameof(ValueMap))</span>
    </div>

    @if (Owner?.ContainsSelection == true)
    {
        <div class="value-map-cases" data-testid="value-map-cases">
            @foreach (var mapCase in map.Cases)
            {
                <SubjectComponent @key="mapCase" Subject="@mapCase" Type="SubjectComponentType.Widget" />
            }

            @if (StructureEditor is { } editor && FindCaseSlot(editor, map) is { } slot)
            {
                <MudButton Size="Size.Small"
                           StartIcon="@Icons.Material.Filled.Add"
                           Class="edit-mode-interactive"
                           OnClick="@(() => editor.AddListItemAsync(map, slot))"
                           data-testid="add-case-button">Add case</MudButton>
            }
        </div>
    }
}

@code {
    [Parameter]
    public IInterceptorSubject? Subject { get; set; }

    [CascadingParameter(Name = "IsEditing")]
    public bool IsEditing { get; set; }

    [CascadingParameter]
    public WidgetSelection? Selection { get; set; }

    [CascadingParameter]
    public IPageStructureEditor? StructureEditor { get; set; }

    [CascadingParameter(Name = "ParentSubjectComponent")]
    public SubjectComponent? Owner { get; set; }

    private WidgetSelection? _subscribedSelection;

    protected override void OnParametersSet()
    {
        // The list follows the selection, which changes without new parameters.
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
    }

    private void OnSelectionChanged() => _ = InvokeAsync(StateHasChanged);

    private static PageListSlot? FindCaseSlot(IPageStructureEditor editor, ValueMap map)
    {
        foreach (var slot in editor.GetListSlots(map))
        {
            if (slot.Name == "Case")
            {
                return slot;
            }
        }

        return null;
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

Check `_Imports.razor` of HomeBlaze.Components for the usings (`MudBlazor`, `Namotion.Interceptor`, `HomeBlaze.Components.Abstractions`); add `@using` lines if one is missing.

- [ ] **Step 5: Gather the invisible widgets into a chip stack**

In `MarkdownContentView.razor`, add `@inject SubjectComponentRegistry ComponentRegistry`, `@using HomeBlaze.Components.Abstractions` (for `SubjectComponentType`, already used), the cascading parameters

```csharp
    [CascadingParameter(Name = "IsEditing")]
    public bool IsEditing { get; set; }

    [CascadingParameter]
    public WidgetSelection? Selection { get; set; }
```

and render the stack before the segments:

```razor
@{
    var isEditingInline = IsEditing && Selection is not null;
}
@if (isEditingInline && Children is not null && HasInvisibleWidget())
{
    @* Keyed, so that a chip keeps its state when a block is inserted or removed. *@
    <div class="invisible-widget-stack">
        @foreach (var child in Children!.Values)
        {
            if (IsInvisibleWidget(child))
            {
                <SubjectComponent @key="child" Subject="@child" Type="SubjectComponentType.Widget" />
            }
        }
    </div>
}
```

In the loop over the segments, skip invisible widgets while editing inline:

```razor
    else if (segment.Widget != null && !(isEditingInline && IsInvisibleWidget(segment.Widget)))
```

and add to `@code`:

```csharp
    private bool HasInvisibleWidget()
    {
        foreach (var child in Children!.Values)
        {
            if (IsInvisibleWidget(child))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsInvisibleWidget(IInterceptorSubject subject) =>
        subject is not (HtmlSegment or RenderExpression) &&
        ComponentRegistry.GetComponent(subject.GetType(), SubjectComponentType.Widget)?.ComponentType is { } componentType &&
        typeof(IInvisibleWidget).IsAssignableFrom(componentType);
```

Check the namespace of `SubjectComponentRegistry` (`HomeBlaze.Services.Components`, as `Pages.razor` and `MarkdownFileEditComponent.razor` use it).

- [ ] **Step 6: Style the stack, the chip and the case list**

Append to `MarkdownFilePageComponent.razor.css`:

```css
/* The widgets that show nothing, such as a ValueMap, stand as chips at the top right of the content they belong to
   while the page is edited inline. On the page itself they start below the button of the page menu. */
.markdown-content ::deep .markdown-fragment:has(> .invisible-widget-stack) {
    position: relative;
}

.markdown-content ::deep .invisible-widget-stack {
    position: absolute;
    top: 0;
    right: 0;
    z-index: 5;
    display: flex;
    flex-direction: column;
    align-items: flex-end;
    gap: 6px;
}

.markdown-content > ::deep .invisible-widget-stack {
    top: 3.5rem;
}
```

Append to `SubjectComponent.razor.css` (the chip and the case list are rendered by widgets in HomeBlaze.Components):

```css
/* A widget that shows nothing on a viewed page, shown while the page is edited inline. */
::deep .invisible-widget-chip {
    display: inline-flex;
    align-items: center;
    gap: 6px;
    height: 28px;
    padding: 0 10px;
    border: 1px dashed rgba(var(--mud-palette-primary-rgb), 0.7);
    border-radius: 14px;
    background: var(--mud-palette-surface);
    font-size: 0.8125rem;
    color: var(--mud-palette-text-primary);
}

::deep .value-map-cases {
    display: flex;
    flex-direction: column;
    gap: 6px;
    margin-top: 6px;
    padding: 8px;
    min-width: 14rem;
    border-radius: 8px;
    background: var(--mud-palette-surface);
    box-shadow: var(--mud-elevation-4);
}
```

- [ ] **Step 7: Run the tests**

Run: `dotnet test src/HomeBlaze/HomeBlaze.E2E.Tests --filter "FullyQualifiedName~InlineStructureEditingTests|FullyQualifiedName~WidgetSelectionTests|FullyQualifiedName~SpacingLayoutTests"`
Expected: PASS. `Selection.md` has a ValueMap too, so a `WidgetSelectionTests` test that counted selectable widgets may now see the chip; update such a count rather than hiding the chip.

- [ ] **Step 8: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Components src/HomeBlaze/HomeBlaze.Storage.Blazor src/HomeBlaze/HomeBlaze.E2E.Tests
git commit -m "feat: show value maps and their cases while a page is edited inline"
```

---

### Task 9: Selectable ValueList rows

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Components/Components/ValueRowWidget.razor`
- Modify: `src/HomeBlaze/HomeBlaze.Components/Components/ValueListWidget.razor`, `SubjectComponent.razor.css`
- Test: `src/HomeBlaze/HomeBlaze.E2E.Tests/InlineStructureEditingTests.cs`

**Interfaces:**
- Consumes: `ValueListRow` (existing), `IPageStructureEditor`, `WidgetSelection.Changed`, `SubjectComponent.ContainsSelection`.
- Produces: test id `add-value-button`; a row is a selectable widget while the page is edited inline.

- [ ] **Step 1: Write the failing E2E tests**

```csharp
    [Fact]
    public async Task WhenARowIsMovedDown_ThenTheRowsSwapInTheFile()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();
        var row = page.Locator(".subject-component-wrapper.selectable:has(> [data-testid='value-row'])").Filter(new() { HasText = "Row A" });
        var actionBar = await row.SelectWidgetAsync();

        // Act
        await actionBar.GetByTestId("move-down-button").ClickAsync();

        // Assert
        await _fixture.WaitForDemoPageTextAsync(
            PageName,
            text => text.IndexOf("Label=\"Row B\"", StringComparison.Ordinal) < text.IndexOf("Label=\"Row A\"", StringComparison.Ordinal),
            "The rows were not swapped.");
    }

    [Fact]
    public async Task WhenAValueIsAddedToASelectedList_ThenItsRowFollowsTheLastRow()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();
        var list = page.Locator(".subject-component-wrapper.selectable:has(> [data-testid='value-list'])");
        await list.SelectWidgetAsync();

        // Act
        await page.GetByTestId("add-value-button").ClickAsync();
        var dialog = page.GetByTestId("create-subject-dialog");
        await dialog.GetByLabel("Label", new() { Exact = true }).FillAsync("Row C");
        await dialog.GetByTestId("create-button").ClickAsync();

        // Assert
        await _fixture.WaitForDemoPageTextAsync(
            PageName, text => text.Contains("Label=\"Row B\"\n:::\n::: Value Label=\"Row C\""), "The value was not added.");
    }
```

Check the label of the `Label` property of `ValueRow` and the order in which the serializer writes its arguments (the expected text assumes `Label` is the only argument that is set). When the dialog requires `Path` before Create is enabled, also fill it with `structuremotor/TargetSpeed` and match the written head line with `Contains("Label=\"Row C\"")` after `Label=\"Row B\"\n:::\n::: Value` instead.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.E2E.Tests --filter "FullyQualifiedName~InlineStructureEditingTests"`
Expected: the two new tests FAIL.

- [ ] **Step 3: Render a row as a widget while editing inline**

`Components/ValueRowWidget.razor`:

```razor
@attribute [SubjectComponent(SubjectComponentType.Widget, typeof(ValueRow))]
@implements ISubjectComponent

@* A row of a value list as a widget of its own, so that it can be selected while the page is edited inline. The list
   reads the values of all rows and passes them in. *@
@if (Subject is ValueRow row)
{
    <ValueListRow Row="@row" Path="@Path" Reading="@Reading" Icon="@Icon" AnyIcon="@AnyIcon" />
}

@code {
    [Parameter]
    public IInterceptorSubject? Subject { get; set; }

    [Parameter]
    public string? Path { get; set; }

    [Parameter]
    public ValueReading? Reading { get; set; }

    [Parameter]
    public string? Icon { get; set; }

    [Parameter]
    public bool AnyIcon { get; set; }
}
```

In `ValueListWidget.razor`, add the cascading parameters `IsEditing` (named), `Selection`, `StructureEditor`, `Owner` (named `ParentSubjectComponent`) as in `ValueMapWidget`, the same subscription to `Selection.Changed` with `IDisposable`, and render the rows:

```razor
<MudList T="string" Dense="true" ReadOnly="true" Padding="false" Class="value-list" data-testid="value-list">
    @for (var index = 0; index < rows.Length; index++)
    {
        var row = rows[index];
        var (path, reading, icon) = items[index];
        if (IsEditing && Selection is not null)
        {
            @* Only while the page is edited inline, where a row is a widget of its own that can be selected. *@
            <SubjectComponent @key="row"
                              Subject="@row"
                              Type="SubjectComponentType.Widget"
                              AdditionalParameters="@(new Dictionary<string, object?> { ["Path"] = path, ["Reading"] = reading, ["Icon"] = icon, ["AnyIcon"] = anyIcon })" />
        }
        else
        {
            <ValueListRow @key="row" Row="@row" Path="@path" Reading="@reading" Icon="@icon" AnyIcon="@anyIcon" />
        }
    }
</MudList>
@if (IsEditing && Owner?.ContainsSelection == true && StructureEditor is { } editor && Subject is ValueList list && FindValueSlot(editor, list) is { } slot)
{
    <MudButton Size="Size.Small"
               StartIcon="@Icons.Material.Filled.Add"
               Class="edit-mode-selected-interactive"
               OnClick="@(() => editor.AddListItemAsync(list, slot))"
               data-testid="add-value-button">Add value</MudButton>
}
```

with `FindValueSlot` like `FindCaseSlot` of Task 8 for the slot name `Value`. The button uses `edit-mode-selected-interactive`, which takes the pointer while the list is selected (`SubjectComponent.razor.css`). While a row is selected, the list is not, so the button has no pointer then: select the list to add a value.

- [ ] **Step 4: Keep the row spacing in edit mode**

The rules for the first and the last row in `SubjectComponent.razor.css` match `.value-list > .value-list-item`. Add the same for a row in its wrapper:

```css
::deep .value-list > .subject-component-wrapper:first-child > .value-list-item {
    padding-top: 0;
}

::deep .value-list > .subject-component-wrapper:first-child > .value-list-item > .mud-list-item-text {
    margin-top: 0;
}

::deep .value-list > .subject-component-wrapper:last-child > .value-list-item {
    padding-bottom: 0;
}

::deep .value-list > .subject-component-wrapper:last-child > .value-list-item > .mud-list-item-text {
    margin-bottom: 0;
}
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test src/HomeBlaze/HomeBlaze.E2E.Tests --filter "FullyQualifiedName~InlineStructureEditingTests|FullyQualifiedName~WidgetSelectionTests|FullyQualifiedName~DisplayWidgetTests"`
Expected: PASS. `WidgetSelectionTests.GetValueListWrapper` selects the wrapper of the list, which still has the list as a direct child.

- [ ] **Step 6: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Components src/HomeBlaze/HomeBlaze.E2E.Tests
git commit -m "feat: select, move and add the rows of a value list inline"
```

---

### Task 10: Insert lines between blocks

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Components/Components/BlockInsertLine.razor`
- Modify: `src/HomeBlaze/HomeBlaze.Storage.Blazor/Files/MarkdownContentView.razor`, `Files/MarkdownFilePageComponent.razor.css`
- Test: `src/HomeBlaze/HomeBlaze.E2E.Tests/InlineStructureEditingTests.cs`

**Interfaces:**
- Consumes: `IPageStructureEditor.CanInsertInto`, `InsertWidgetAsync`; `SubjectComponent.GetSelectableSelfOrAncestor`, `SelectableAncestor`; `WidgetSelection.Changed`; the `ParentSubjectComponent` cascade.
- Produces: test id `block-insert-line`.

- [ ] **Step 1: Write the failing E2E tests**

```csharp
    [Fact]
    public async Task WhenNothingIsSelected_ThenThePageHasInsertLinesAndOneInsertsAWidgetThere()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();
        var lines = page.Locator(".markdown-content > [data-testid='block-insert-line']");
        await Assertions.Expect(lines.First).ToBeVisibleAsync(new() { Timeout = Timeout });

        // Act: the second line stands before the switch.
        await lines.Nth(1).ClickAsync();
        var dialog = page.GetByTestId("create-subject-dialog");
        await dialog.GetByTestId("type-search-input").FillAsync("Alert");
        await dialog.GetByTestId("type-row-alert").ClickAsync();
        await dialog.GetByTestId("create-name-input").Locator("input").FillAsync("structurealert");
        await dialog.GetByTestId("create-button").ClickAsync();

        // Assert
        await _fixture.WaitForDemoPageTextAsync(
            PageName,
            text => text.IndexOf("HomeBlaze.Components.Alert#structurealert", StringComparison.Ordinal) is var alert and >= 0 &&
                    alert < text.IndexOf("HomeBlaze.Components.Switch", StringComparison.Ordinal),
            "The alert was not inserted before the switch.");
    }

    [Fact]
    public async Task WhenAWidgetIsSelected_ThenOnlyTheContentAroundItHasInsertLines()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();

        // Act
        await GetWrapperOf(page, "switch").SelectWidgetAsync();

        // Assert: the switch stands on the page, so the page keeps its lines and no content inside a block has any.
        await Assertions.Expect(page.Locator(".markdown-content > [data-testid='block-insert-line']").First).ToBeVisibleAsync(new() { Timeout = Timeout });
        await Assertions.Expect(page.Locator(".markdown-fragment [data-testid='block-insert-line']")).ToHaveCountAsync(0);
    }
```

Check the test ids of the picker (`type-search-input`, `type-row-{type name in lower case}`) and how `SubjectSetupDialogTests` fills `create-name-input` (a `MudTextField` with the test id on its root); use the same locator.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.E2E.Tests --filter "FullyQualifiedName~InlineStructureEditingTests"`
Expected: the two new tests FAIL (no insert line).

- [ ] **Step 3: Add the insert line component**

`Components/BlockInsertLine.razor`:

```razor
@* A place between the blocks of a page that is edited inline, where a click inserts a new block. *@
<div class="block-insert-line edit-mode-interactive" data-testid="block-insert-line">
    <MudIconButton Icon="@Icons.Material.Filled.Add"
                   Size="Size.Small"
                   Variant="Variant.Outlined"
                   Color="Color.Primary"
                   title="Insert block here"
                   aria-label="Insert block here"
                   OnClick="OnInsert" />
</div>

@code {
    /// <summary>Raised when the line is clicked.</summary>
    [Parameter]
    public EventCallback OnInsert { get; set; }
}
```

Append to `SubjectComponent.razor.css`:

```css
/* The line where a new block goes, between the blocks of the content that is edited. */
::deep .block-insert-line {
    display: flex;
    align-items: center;
    gap: 6px;
    margin: 4px 0;
    opacity: 0.5;
}

::deep .block-insert-line:hover,
::deep .block-insert-line:focus-within {
    opacity: 1;
}

::deep .block-insert-line::before,
::deep .block-insert-line::after {
    content: "";
    flex: 1;
    height: 1px;
    background: rgba(var(--mud-palette-primary-rgb), 0.7);
}

::deep .block-insert-line .mud-icon-button {
    padding: 2px;
}
```

- [ ] **Step 4: Render the lines for the active content**

In `MarkdownContentView.razor`, add

```csharp
    [CascadingParameter]
    public IPageStructureEditor? StructureEditor { get; set; }

    [CascadingParameter(Name = "ParentSubjectComponent")]
    public SubjectComponent? Owner { get; set; }
```

subscribe to `Selection.Changed` while `StructureEditor` is not null (same pattern as `ValueMapWidget`, with `IDisposable`), and add:

```csharp
    // The content shows insert lines when it is where the selection is: the page when nothing is selected, otherwise
    // the content of the selected widget or the content that holds it directly.
    private bool ShowsInsertLines()
    {
        if (StructureEditor is not { } editor || Content is null || !editor.CanInsertInto(Content))
        {
            return false;
        }

        var contentOwner = Owner?.GetSelectableSelfOrAncestor();
        var selected = Selection?.Selected;
        return selected is null
            ? contentOwner is null
            : ReferenceEquals(selected, contentOwner) || ReferenceEquals(selected.SelectableAncestor, contentOwner);
    }
```

In the markup, compute `var showsInsertLines = ShowsInsertLines();` once next to `isEditingInline`, render `<BlockInsertLine OnInsert="@(() => StructureEditor!.InsertWidgetAsync(Content!, segment.Widget))" />` right before each widget segment that is rendered (not before an invisible widget) when `showsInsertLines`, and one after the loop:

```razor
@if (showsInsertLines)
{
    <BlockInsertLine OnInsert="@(() => StructureEditor!.InsertWidgetAsync(Content!, null))" />
}
```

The page content view has the page `SubjectComponent` (a page, not selectable, without selectable ancestors) as `Owner`, so its content owner is null.

- [ ] **Step 5: Run the tests**

Run: `dotnet test src/HomeBlaze/HomeBlaze.E2E.Tests --filter "FullyQualifiedName~InlineStructureEditingTests|FullyQualifiedName~SpacingLayoutTests|FullyQualifiedName~WidgetSelectionTests"`
Expected: PASS. If a spacing test measures a page in inline mode, the insert lines add height; measure in view mode instead or exclude the lines, whichever the test meant.

- [ ] **Step 6: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Components src/HomeBlaze/HomeBlaze.Storage.Blazor src/HomeBlaze/HomeBlaze.E2E.Tests
git commit -m "feat: insert widgets between the blocks of a page inline"
```

---

### Task 11: The collapsed content of a Responsive block

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.Components/Responsive.cs`, `Components/ResponsiveWidget.razor`, `SubjectComponent.razor.css`
- Test: `src/HomeBlaze/HomeBlaze.Components.Tests/ResponsiveTests.cs`, `src/HomeBlaze/HomeBlaze.E2E.Tests/InlineStructureEditingTests.cs`

**Interfaces:**
- Produces:
  - `public static string? Responsive.GetHiddenCssClasses(Breakpoint from, Breakpoint to)`: the display classes that show an element exactly where the content is hidden, null when it shows everywhere. The arguments must be valid (`TryGetCssClasses` returned true).
  - `public static string Responsive.GetRangeLabel(Breakpoint from, Breakpoint to)`
  - Test ids `responsive-chip`, class `responsive-widget-preview`.

- [ ] **Step 1: Write the failing unit tests**

Add to `ResponsiveTests`:

```csharp
    [Theory]
    [InlineData(Breakpoint.Xs, Breakpoint.Xxl, null)]
    [InlineData(Breakpoint.Xs, Breakpoint.Xs, "d-none d-sm-block")]
    [InlineData(Breakpoint.Sm, Breakpoint.Md, "d-block d-sm-none d-lg-block")]
    [InlineData(Breakpoint.Lg, Breakpoint.Xxl, "d-block d-lg-none")]
    [InlineData(Breakpoint.Xxl, Breakpoint.Xxl, "d-block d-xxl-none")]
    public void WhenTheHiddenClassesAreAskedFor_ThenTheyShowAnElementExactlyWhereTheContentIsHidden(Breakpoint from, Breakpoint to, string? expected)
    {
        // Act & Assert
        Assert.Equal(expected, Responsive.GetHiddenCssClasses(from, to));
    }

    [Theory]
    [InlineData(Breakpoint.Xs, Breakpoint.Xs, "Xs only")]
    [InlineData(Breakpoint.Xs, Breakpoint.Md, "Up to Md")]
    [InlineData(Breakpoint.Lg, Breakpoint.Xxl, "Lg and up")]
    [InlineData(Breakpoint.Sm, Breakpoint.Md, "Sm to Md")]
    public void WhenTheRangeLabelIsAskedFor_ThenItNamesTheBreakpointsThatShowTheContent(Breakpoint from, Breakpoint to, string expected)
    {
        // Act & Assert
        Assert.Equal(expected, Responsive.GetRangeLabel(from, to));
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Components.Tests --filter "FullyQualifiedName~ResponsiveTests"`
Expected: build error.

- [ ] **Step 3: Implement the helpers**

Add to `Responsive.cs`:

```csharp
    private static readonly string[] BreakpointNames = ["xs", "sm", "md", "lg", "xl", "xxl"];

    /// <summary>
    /// Gets the MudBlazor display classes that show an element exactly at the widths where the content is hidden, or
    /// null when the content shows at every width. Both arguments must be valid, see <see cref="TryGetCssClasses"/>.
    /// </summary>
    public static string? GetHiddenCssClasses(Breakpoint from, Breakpoint to)
    {
        if (from == Breakpoint.Xs && to == Breakpoint.Xxl)
        {
            return null;
        }

        // The element shows where the content is hidden. A class of a breakpoint applies from its lower edge, so a class
        // is added only where that changes.
        var builder = new System.Text.StringBuilder();
        var isElementShown = IsHidden(0, from, to);
        builder.Append(isElementShown ? "d-block" : "d-none");
        for (var breakpoint = 1; breakpoint < BreakpointCount; breakpoint++)
        {
            var isContentHidden = IsHidden(breakpoint, from, to);
            if (isContentHidden != isElementShown)
            {
                builder.Append(" d-").Append(BreakpointNames[breakpoint]).Append(isContentHidden ? "-block" : "-none");
                isElementShown = isContentHidden;
            }
        }

        return builder.ToString();
    }

    /// <summary>Gets a short text for the widths that show the content, such as "Xs only" or "Lg and up".</summary>
    public static string GetRangeLabel(Breakpoint from, Breakpoint to) =>
        from == to ? $"{from} only"
        : from == Breakpoint.Xs ? $"Up to {to}"
        : to == Breakpoint.Xxl ? $"{from} and up"
        : $"{from} to {to}";

    private static bool IsHidden(int breakpoint, Breakpoint from, Breakpoint to) =>
        breakpoint < (int)from || breakpoint > (int)to;
```

Replace the local `names` array in `CreateCssClassTable` with `BreakpointNames`.

- [ ] **Step 4: Run the unit tests**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Components.Tests --filter "FullyQualifiedName~ResponsiveTests"`
Expected: PASS.

- [ ] **Step 5: Write the failing E2E test**

```csharp
    [Fact]
    public async Task WhenTheContentOfAResponsiveBlockIsHidden_ThenAChipShowsItAndSelectingItShowsTheContent()
    {
        // Arrange: the browser of the tests is wider than Xs, so the phone card is hidden.
        var page = await OpenPageInInlineModeAsync();
        var chip = page.GetByTestId("responsive-chip");
        await Assertions.Expect(chip).ToContainTextAsync("Xs only", new() { Timeout = Timeout });

        // Act
        await chip.SelectWidgetAsync();

        // Assert
        await Assertions.Expect(page.Locator(".responsive-widget-preview").GetByText("Phone Card")).ToBeVisibleAsync(new() { Timeout = Timeout });
    }
```

Run: `dotnet test src/HomeBlaze/HomeBlaze.E2E.Tests --filter "FullyQualifiedName~WhenTheContentOfAResponsiveBlockIsHidden"`
Expected: FAIL.

- [ ] **Step 6: Render the chip and the preview**

Replace the body of `ResponsiveWidget.razor`:

```razor
@attribute [SubjectComponent(SubjectComponentType.Widget, typeof(Responsive))]
@implements ISubjectComponent
@implements IDisposable

@{
    var responsive = Subject as Responsive;
}

@if (responsive is not null)
{
    @* Not MudHidden, which renders nothing on other screens: the content is rendered and kept live everywhere, and the classes alone hide it, without JavaScript. *@
    if (Responsive.TryGetCssClasses(responsive.From, responsive.To, out var cssClasses, out var error))
    {
        var isEditingInline = IsEditing && Selection is not null;
        var isPreview = isEditingInline && Owner?.ContainsSelection == true;
        @if (isEditingInline && !isPreview && Responsive.GetHiddenCssClasses(responsive.From, responsive.To) is { } hiddenClasses)
        {
            @* Edited inline, a chip stands where the content is hidden, and selecting it shows the content. *@
            <div class="@hiddenClasses">
                <div class="invisible-widget-chip" data-testid="responsive-chip">
                    <MudIcon Icon="@Icons.Material.Filled.Devices" Size="Size.Small" aria-hidden="true" />
                    <span>Responsive · @Responsive.GetRangeLabel(responsive.From, responsive.To)</span>
                </div>
            </div>
        }

        <div class="@(isPreview ? "responsive-widget responsive-widget-preview" : cssClasses)" data-testid="responsive">
            <SubjectComponent Subject="@responsive.Content" Type="SubjectComponentType.Widget" />
        </div>
    }
    else
    {
        <WidgetArgumentError Message="@error" />
    }
}
```

Add the cascading parameters and the `Selection.Changed` subscription as in `ValueMapWidget` (Task 8), and to `SubjectComponent.razor.css`:

```css
/* The content of a Responsive block that the page shows at every width while the block or a widget in it is selected. */
::deep .responsive-widget-preview {
    display: block;
    opacity: 0.55;
}
```

- [ ] **Step 7: Run the tests**

Run: `dotnet test src/HomeBlaze/HomeBlaze.E2E.Tests --filter "FullyQualifiedName~InlineStructureEditingTests|FullyQualifiedName~CardLayoutTests|FullyQualifiedName~SpacingLayoutTests"`
Expected: PASS.

- [ ] **Step 8: Update the public API snapshot and commit**

Update `HomeBlaze.Components.Tests/VerifyChecksTests.PublicApi.verified.txt` as in Task 6 (`GetHiddenCssClasses`, `GetRangeLabel`).

```bash
git add src/HomeBlaze/HomeBlaze.Components src/HomeBlaze/HomeBlaze.Components.Tests src/HomeBlaze/HomeBlaze.E2E.Tests
git commit -m "feat: show the hidden content of a responsive block inline"
```

---

### Task 12: A view-only Split preview that reveals blocks in the editor

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Components/ISubjectRevealTarget.cs`, `SubjectRevealer.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Components/SubjectComponent.razor`, `SubjectComponent.razor.css`, `Editors/SubjectEditPanel.razor`
- Modify: `src/HomeBlaze/HomeBlaze/Components/Pages/Pages.razor`
- Modify: `src/HomeBlaze/HomeBlaze.Storage.Blazor/Files/MarkdownFileEditComponent.razor`, `Files/MarkdownFileEditComponent.razor.js`
- Test: `src/HomeBlaze/HomeBlaze.E2E.Tests/InlineStructureEditingTests.cs`

**Interfaces:**
- Produces:
  - `public interface ISubjectRevealTarget { Task<bool> RevealAsync(IInterceptorSubject subject); }`
  - `public sealed class SubjectRevealer` with `public SubjectRevealer(Func<IInterceptorSubject, Task<bool>> reveal)` and `public Task<bool> RevealAsync(IInterceptorSubject subject)`
  - `SubjectEditPanel.EditComponent` (`ISubjectComponent?`)
  - Class `revealable` on a widget wrapper in the Split preview.

- [ ] **Step 1: Write the failing E2E tests**

```csharp
    [Fact]
    public async Task WhenAWidgetIsClickedInTheSplitPreview_ThenNothingIsSelectedAndTheEditorSelectsItsBlock()
    {
        // Arrange
        await _fixture.WriteDemoPageAsync(PageName, Content);
        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync($"{_fixture.ServerAddress}pages/Demo/{PageName}");
        await page.WaitUntilInteractiveAsync(Timeout);
        await page.SelectEditModeAsync("Split", Timeout);
        var switchWrapper = page.Locator(".page-split-preview .subject-component-wrapper.revealable:has([data-testid='switch'])").Last;
        await Assertions.Expect(switchWrapper).ToBeVisibleAsync(new() { Timeout = Timeout });

        // Act
        await switchWrapper.ClickAsync(new() { Position = new() { X = 2, Y = 2 } });

        // Assert
        await Assertions.Expect(page.GetWidgetActionBar()).ToHaveCountAsync(0);
        await Assertions.Expect(page.Locator(".page-split-preview .subject-component-wrapper.selectable")).ToHaveCountAsync(0);
        var switchLine = Content.Split('\n').ToList().FindIndex(line => line.Contains("HomeBlaze.Components.Switch")) + 1;
        await AsyncTestHelpers.WaitUntilAsync(
            async () => await page.EvaluateAsync<int>("() => monaco.editor.getEditors()[0].getSelection().startLineNumber") == switchLine,
            message: "The editor did not select the block of the switch.");
    }

    [Fact]
    public async Task WhenThePageIsSplit_ThenTheSourceEditorShowsBlockNamesInBold()
    {
        // Arrange
        await _fixture.WriteDemoPageAsync(PageName, Content);
        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync($"{_fixture.ServerAddress}pages/Demo/{PageName}");
        await page.WaitUntilInteractiveAsync(Timeout);

        // Act
        await page.SelectEditModeAsync("Split", Timeout);

        // Assert
        var name = page.Locator(".monaco-editor .block-name", new() { HasTextString = "HomeBlaze.Components.Switch" });
        await Assertions.Expect(name).ToBeVisibleAsync(new() { Timeout = Timeout });
        await Assertions.Expect(name).ToHaveCSSAsync("font-weight", "700");
    }
```

Check whether `AsyncTestHelpers.WaitUntilAsync` has an overload that takes `Func<Task<bool>>`. If it takes only `Func<bool>`, poll with `Assertions.Expect(page.Locator(...)).ToHaveAttributeAsync` on a value the page writes, or add the async overload to the helper in the test project.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.E2E.Tests --filter "FullyQualifiedName~InlineStructureEditingTests"`
Expected: the Split test FAILS (no `revealable` wrapper). The bold test passes already (Task 5).

- [ ] **Step 3: Add the reveal abstraction**

`ISubjectRevealTarget.cs`:

```csharp
using Namotion.Interceptor;

namespace HomeBlaze.Components;

/// <summary>An editor that can show where a subject is defined, such as the block of a widget in the text of its page.</summary>
public interface ISubjectRevealTarget
{
    /// <summary>Shows where the subject is defined and returns whether it found it.</summary>
    Task<bool> RevealAsync(IInterceptorSubject subject);
}
```

`SubjectRevealer.cs`:

```csharp
using Namotion.Interceptor;

namespace HomeBlaze.Components;

/// <summary>
/// Cascaded to a page that is shown next to its editor. A click on a widget there asks the editor to show where the
/// widget is defined instead of selecting it.
/// </summary>
public sealed class SubjectRevealer
{
    private readonly Func<IInterceptorSubject, Task<bool>> _reveal;

    public SubjectRevealer(Func<IInterceptorSubject, Task<bool>> reveal)
    {
        _reveal = reveal;
    }

    /// <summary>Asks the editor to show where the subject is defined and returns whether it found it.</summary>
    public Task<bool> RevealAsync(IInterceptorSubject subject) => _reveal(subject);
}
```

- [ ] **Step 4: Reveal on a click in `SubjectComponent`**

In `SubjectComponent.razor` `@code`:

```csharp
    /// <summary>Cascaded to the preview next to an editor, where a click reveals the widget in the editor.</summary>
    [CascadingParameter]
    public SubjectRevealer? Revealer { get; set; }

    private EventCallback<MouseEventArgs> _revealCallback;
```

In `OnInitialized`: `_revealCallback = EventCallback.Factory.Create<MouseEventArgs>(this, RevealAsync);` and the method:

```csharp
    // The innermost widget takes the click. A widget that has no block of its own, such as a subject that a Widget
    // block shows, reveals the nearest widget around it that has one.
    private async Task RevealAsync(MouseEventArgs args)
    {
        for (var component = this; component is not null; component = component.ParentComponent)
        {
            if (component.Subject is { } subject && await Revealer!.RevealAsync(subject))
            {
                return;
            }
        }
    }
```

In the markup, compute `var isRevealable = !isSelectable && Revealer is not null && Type == SubjectComponentType.Widget;` next to `isSelectable`, add ` revealable` to the class when it is true, and on the wrapper:

```razor
             @onclick="@(isSelectable ? _clickCallback : isRevealable ? _revealCallback : default)"
             @onclick:stopPropagation="@(isSelectable || isRevealable)"
```

Append to `SubjectComponent.razor.css`:

```css
/* In the preview next to the editor, a click on a widget shows its block in the editor. */
@media (hover: hover) {
    .subject-component-wrapper.revealable:hover:not(:has(.subject-component-wrapper.revealable:hover)) {
        outline: 1px dashed rgba(var(--mud-palette-primary-rgb), 0.5);
        outline-offset: 1px;
        cursor: pointer;
    }
}
```

- [ ] **Step 5: Expose the edit component and make the preview view-only**

In `SubjectEditPanel.razor` `@code`:

```csharp
    /// <summary>Gets the edit component of the subject once it has rendered, or null.</summary>
    public ISubjectComponent? EditComponent => _subjectComponent;
```

In `Pages.razor`, wrap the preview `SubjectComponent` of the Split mode:

```razor
                        <div class="page-split-preview" style="width: 100%; height: 100%; overflow-y: auto;">
                            @* The preview shows the saved page and never writes it, so that it cannot overwrite the
                               unsaved text of the editor. A click on a widget reveals its block in the editor. *@
                            <CascadingValue Value="false" Name="IsEditing">
                                <CascadingValue Value="_revealer" IsFixed="true">
                                    <SubjectComponent @key="_componentKey"
                                                      Subject="@_subject"
                                                      Type="SubjectComponentType.Page"/>
                                </CascadingValue>
                            </CascadingValue>
                        </div>
```

(the `@onclick="ClearSelectionCallback"` of the preview div goes away, nothing can be selected there), and in `@code`:

```csharp
    private SubjectRevealer? _revealer;

    private Task<bool> RevealInEditorAsync(IInterceptorSubject subject) =>
        _editPanel?.EditComponent is ISubjectRevealTarget target ? target.RevealAsync(subject) : Task.FromResult(false);
```

Create `_revealer = new SubjectRevealer(RevealInEditorAsync);` in `OnInitialized` (create one if the component has none). Add `@using HomeBlaze.Components` if needed.

- [ ] **Step 6: Reveal the block in the source editor**

In `MarkdownFileEditComponent.razor.js`, add:

```js
/**
 * Selects the lines of a block, scrolls them into the middle of the editor and gives the editor the focus.
 * @param {string} editorId The id of the editor of BlazorMonaco.
 * @param {number} startLine The 1-based head line of the block.
 * @param {number} endLine The 1-based last line of the block.
 */
export function reveal(editorId, startLine, endLine) {
    const editor = window.blazorMonaco.editor.getEditor(editorId, true);
    const model = editor?.getModel();
    if (!model || model.isDisposed()) {
        return;
    }

    const range = new monaco.Range(startLine, 1, endLine, model.getLineMaxColumn(endLine));
    editor.setSelection(range);
    editor.revealRangeInCenter(range);
    editor.focus();
}
```

In `MarkdownFileEditComponent.razor`, add `@implements ISubjectRevealTarget`, keep the outlines of the last decoration run (`private List<BlockOutline> _outlines = new();`, assigned next to `_decorationRegions = parsed.Regions;` as `_outlines = parsed.Outlines;`), and add:

```csharp
    public async Task<bool> RevealAsync(IInterceptorSubject subject)
    {
        if (_editor is null || File is null)
        {
            return false;
        }

        var region = _decorationRegions.FirstOrDefault(
            candidate => candidate.Type == DecorationRegionType.SubjectBlock && ReferenceEquals(candidate.Subject, subject));
        if (region?.BlockIndex is not { } blockIndex)
        {
            return false;
        }

        // The subject belongs to the block of the saved page. Text that changed before the block makes the region point
        // at another block, so it is only revealed while the text up to the block is saved.
        if (MarkdownBlockMatcher.FindSavedBlock(await _editor.GetValue(), File.Content, blockIndex) is null ||
            await GetModuleAsync() is not { } module)
        {
            return false;
        }

        var endLine = _outlines.FirstOrDefault(outline => outline.StartLine == region.StartLine)?.EndLine ?? region.EndLine;
        await module.InvokeVoidAsync("reveal", _editor.Id, region.StartLine, endLine);
        return true;
    }
```

- [ ] **Step 7: Run the tests**

Run: `dotnet test src/HomeBlaze/HomeBlaze.E2E.Tests --filter "FullyQualifiedName~InlineStructureEditingTests|FullyQualifiedName~PageEditTests|FullyQualifiedName~LayoutItemActionsTests|FullyQualifiedName~WidgetSelectionTests"`
Expected: PASS. A test that selected widgets in the Split preview now fails by design: move its selection to Inline mode.

- [ ] **Step 8: Update the public API snapshot and commit**

Update `HomeBlaze.Components.Tests/VerifyChecksTests.PublicApi.verified.txt` as in Task 6.

```bash
git add src/HomeBlaze
git commit -m "feat: reveal a block in the editor from the split preview instead of editing it"
```

---

### Task 13: Documentation, spec alignment and the full test run

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze/Data/Files/Docs/administration/pages.md`
- Modify: `docs/superpowers/specs/2026-10-22-markdown-inline-editing-design.md`

- [ ] **Step 1: Document the editing**

In `pages.md`, near the paragraph that starts "While the page is in edit mode", add a section in the style of the page (plain sentences, no em dashes, one paragraph per line):

```markdown
### Editing the blocks of a page inline

In "Inline" mode, a click selects a widget and shows its bar. Next to "Edit", the bar moves the block up or down among the blocks of its content or its slot, deletes it after asking, and adds an item to each list slot of the block, such as "Add Value" for a `ValueList` or "Add Tab" for a `TabLayout`. The rows of a `ValueList` and the cases of a `ValueMap` are blocks of their own and can be selected, moved and deleted in the same way.

A plus line between the blocks inserts a new widget there. The page shows these lines between its own blocks while nothing is selected, and the content of a block shows them while the block or a widget directly in it is selected.

A widget that shows nothing, such as a `ValueMap`, is a chip with its name at the top right of the content it stands in, and selecting it lists its cases. The content of a `Responsive` block that is hidden at the current width is a chip in its place, and selecting the chip shows the content.

In "Split" mode the page next to the editor only shows the saved page: a click on a widget selects its block in the editor instead of selecting the widget, so that nothing changes the file while the editor has text that is not saved.
```

Replace the sentence "Use these buttons with "Inline". In "Split", the editor next to the page does not show what the buttons wrote until the page is opened again, so do not save the text of the editor after using them, because it would write its older text over theirs." with "Use these buttons in "Inline" mode. The page next to the editor in "Split" mode does not edit."

- [ ] **Step 2: Align the spec with the plan**

In the spec, replace the bUnit bullet under "Testing" with:

```markdown
- **E2E**, in HomeBlaze.E2E.Tests, for the behavior of the components, since the repository has no bUnit: the action bar shows move and delete only for blocks that allow them; ValueMap is a chip in Inline mode and nothing in View mode; the Responsive chip shows only where the content is hidden; the insert lines follow the scope of the selection; the Split preview selects nothing.
```

and under "Refusals and errors" replace "An operation refuses, writes nothing, and the page reads its file again, when:" with "An operation refuses and writes nothing when:" and add after the list: "The structure operations change only text, so a refusal leaves the graph as it was and needs no reload. The configuration write keeps its reload after a refusal."

- [ ] **Step 3: Run the whole verification**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: success, no warnings.
Run: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`
Expected: PASS.
Run: `dotnet test src/HomeBlaze/HomeBlaze.E2E.Tests`
Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add src/HomeBlaze/HomeBlaze/Data/Files/Docs/administration/pages.md docs/superpowers/specs/2026-10-22-markdown-inline-editing-design.md
git commit -m "docs: describe inline structure editing of pages"
```
