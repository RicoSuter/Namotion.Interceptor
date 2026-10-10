# Markdown Blocks and Slots Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the `subject(name)` code fence in HomeBlaze markdown pages with `:::` widget blocks whose content fills slots declared on the widget type, and ship `Card`, `FlowLayout` and `TabLayout`.

**Architecture:** One pure parser (`MarkdownPageParser`) turns page text into a block tree with source spans. `MarkdownSegmentBuilder` renders the page once and cuts the HTML into segments per block. `MarkdownContentParser` turns segments into subjects and fills `[Slot]` properties, with open slots holding a `MarkdownFragment`. `MarkdownBlockWriter` writes configuration back into the head line and the `json` fence only. The editor and the write-back use the same parser and writer.

**Tech Stack:** C# 14, .NET 10, Markdig 0.45.0, Blazor Server, MudBlazor, BlazorMonaco, xUnit, Playwright (.NET E2E tests and the Playwright MCP for interactive checks).

**Design spec:** `docs/superpowers/specs/2026-10-10-markdown-blocks-design.md`. Where the spec and this plan disagree, the plan wins.

**Verified prototype:** `docs/superpowers/plans/2026-10-10-markdown-blocks-prototype/` holds four files that were compiled and exercised against Markdig 0.45.0 on .NET 10: `MarkdownBlockHead.cs`, `MarkdownPageParser.cs`, `MarkdownSegmentBuilder.cs`, `MarkdownBlockWriter.cs`. `PrototypeChecks.cs.txt` is the throwaway harness that passed. Tasks 1 to 3 copy these files verbatim. Everything from Task 4 on was written against the code as read on master `c58b011be` and has not been compiled: when a snippet does not compile, fix the snippet, keep the behaviour and the tests.

## Global Constraints

- Never include AI attribution in commit messages, PR descriptions or GitHub comments: no agent names, no `Co-Authored-By` trailers, no "Generated with" footers. This overrides any tool default.
- No em dashes in docs, READMEs or PR descriptions. No hard wrapping in markdown.
- Avoid abbreviations in names (`attribute`, not `attr`).
- Comments explain only what a reader cannot derive. XML docs state the contract.
- Code, comments and docs outside `src/HomeBlaze` never mention HomeBlaze. This work stays inside `src/HomeBlaze`.
- Test naming: `When<Condition>_Then<ExpectedBehavior>`. Explicit `// Arrange`, `// Act`, `// Assert` comments (`// Act & Assert` for exception tests).
- No hardcoded waits in tests. Use `AsyncTestHelpers.WaitUntilAsync` or Playwright's `Assertions.Expect(...)` with a timeout.
- Build is warnings-as-errors with nullable enabled. Do not add analyzer suppressions without asking.
- `InternalsVisibleTo` only for test projects. `HomeBlaze.Storage.Blazor` must use the new types through their public API.
- All names in the page syntax are case-sensitive: type names, slot names, argument keys.
- The working tree of the main checkout has two modified files that belong to the user: `src/HomeBlaze/HomeBlaze/Data/Files/Devices/Luxtronik.json` and `Shelly.json`. Never stage, revert or commit them. Stage files by explicit path, never `git add -A` or `git add .`.
- The live server at `192.168.1.200` is read-only for this work. Do not write to `/Volumes/HomeBlaze2` and do not invoke device operations, neither on the live server nor on a local instance.
- Unit tests: `dotnet test <project> --filter "FullyQualifiedName~<Class>"`. E2E tests: `dotnet test src/HomeBlaze/HomeBlaze.E2E.Tests`. The E2E run is required after every task marked **UI**.

## Execution Order

Tasks run in this order: 0, 1, 2, 3, 4, 5, 6, 7, 8, 10, 9, 11, 12. Task 10 (page scope) comes before Task 9 (rendering and `Card`), because the E2E page of Task 9 uses an expression inside a card that refers to a named subject inside the same card.

## Verification with Playwright

Two layers, both required.

**E2E tests (automated).** `src/HomeBlaze/HomeBlaze.E2E.Tests` starts the application against `TestData/` and drives it with Playwright. Tasks 5, 6, 9 and 11 add or change tests there. Run the whole project, not a filter, at the end of each of these tasks.

**Playwright MCP (interactive, done by the controlling session).** After Tasks 6, 9, 11 and 12, start the application locally and look at the result:

```bash
dotnet run --project src/HomeBlaze/HomeBlaze --launch-profile http
```

It listens on `http://localhost:5192`. Start it as a background process and stop it when the check is done. For each page named in the task:

1. `browser_navigate` to the page, then `browser_snapshot` and compare the structure with what the task says must be visible.
2. `browser_resize` to 390 x 844 and take a screenshot, then 1280 x 800 and take a screenshot. Look at both.
3. `browser_console_messages` and confirm there are no errors.
4. Search the snapshot for `markdown-block-error`. There must be none unless the task expects one.

Screenshots can only be saved inside the repository (`.playwright-mcp/`). Delete them after looking. Never commit them.

A subagent that has no Playwright MCP tools reports that and leaves the interactive check to the controlling session. It still runs the E2E tests.

## File Structure

| File | Responsibility |
|---|---|
| `src/HomeBlaze/HomeBlaze.Storage/Pages/MarkdownBlockHead.cs` (create) | Head line grammar: parse and format. |
| `src/HomeBlaze/HomeBlaze.Storage/Pages/MarkdownPageParser.cs` (create) | `MarkdownSpan`, `MarkdownBlock`, `MarkdownPage`, the parser. |
| `src/HomeBlaze/HomeBlaze.Storage/Pages/MarkdownSegmentBuilder.cs` (create) | Segment records, error box HTML, render and split. |
| `src/HomeBlaze/HomeBlaze.Storage/Pages/MarkdownBlockWriter.cs` (create) | Write configuration back into one block. Create a new block. |
| `src/HomeBlaze/HomeBlaze.Storage/Pages/SlotMap.cs` (create) | Reads `[Slot]` declarations of a type and validates them. |
| `src/HomeBlaze/HomeBlaze.Storage/Pages/MarkdownFragment.cs` (create) | Subject that holds the content of an open slot. |
| `src/HomeBlaze/HomeBlaze.Storage/Pages/MarkdownParseResult.cs` (create) | What the content parser returns to `MarkdownFile`. |
| `src/HomeBlaze/HomeBlaze.Storage/Internal/MarkdownContentParser.cs` (rewrite) | Segments to subjects, reuse, slots, name table. |
| `src/HomeBlaze/HomeBlaze.Storage/Files/MarkdownFile.cs` (modify) | Keeps page, block subjects and names. Write-back. `IPathScope`. |
| `src/HomeBlaze/HomeBlaze.Storage/Internal/RenderExpression.cs` (modify) | Scoped resolution. |
| `src/HomeBlaze/HomeBlaze.Abstractions/Attributes/SlotAttribute.cs` (create) | The attribute. |
| `src/HomeBlaze/HomeBlaze.Abstractions/IPathScope.cs` (create) | Scope for names and relative paths. |
| `src/HomeBlaze/HomeBlaze.Services/SubjectPathResolverScopeExtensions.cs` (create) | `ResolveScopedSubject`, `ResolveScopedValue`. |
| `src/HomeBlaze/HomeBlaze.Components/Widget.cs` (modify) | Scoped resolution. |
| `src/HomeBlaze/HomeBlaze.Components/MudBlazorIcons.cs` (create) | Icon name to MudBlazor icon, moved from the host. |
| `src/HomeBlaze/HomeBlaze.Components/Card.cs`, `FlowLayout.cs`, `TabLayout.cs`, `Tab.cs` (create) | Slot widgets. |
| `src/HomeBlaze/HomeBlaze.Components/Components/CardWidget.razor`, `FlowLayoutWidget.razor`, `TabLayoutWidget.razor` (create) | Their widget components. |
| `src/HomeBlaze/HomeBlaze.Storage.Blazor/Files/MarkdownContentView.razor` (create) | Shared render loop. |
| `src/HomeBlaze/HomeBlaze.Storage.Blazor/Files/MarkdownFragmentWidget.razor` (create) | Widget component of `MarkdownFragment`. |
| `src/HomeBlaze/HomeBlaze.Storage.Blazor/Files/MarkdownFilePageComponent.razor` (modify) | Uses the shared view. Error box style. |
| `src/HomeBlaze/HomeBlaze.Storage.Blazor/Models/MarkdownDecorationParser.cs`, `DecorationRegion.cs` (modify) | Regions from the page parser. |
| `src/HomeBlaze/HomeBlaze.Storage.Blazor/Files/MarkdownFileEditComponent.razor` (modify) | Edit and insert through the block writer. |
| `src/HomeBlaze/HomeBlaze.Storage.Tests/Pages/*` (create) | Unit tests and test subjects. |
| `src/HomeBlaze/HomeBlaze.E2E.Tests/MarkdownBlockTests.cs`, `TestData/Demo/Blocks.md` (create) | E2E for blocks and slots. |
| Pages under `src/HomeBlaze/HomeBlaze/Data/Files` and `HomeBlaze.E2E.Tests/TestData` (modify) | Migration and documentation. |

---

### Task 0: Branch and temporary design notes

**Files:**
- Commit: `docs/superpowers/specs/2026-10-10-markdown-blocks-design.md`, `docs/superpowers/plans/2026-10-10-markdown-blocks.md`, `docs/superpowers/plans/2026-10-10-markdown-blocks-prototype/*`

- [ ] **Step 1: Create the branch from current master**

Use superpowers:using-git-worktrees. Branch name `feat/markdown-blocks`, based on `origin/master`. If you work in the main checkout instead, the user's two modified JSON files travel along untouched: do not stash or reset them.

- [ ] **Step 2: Bring the design notes onto the branch**

The three paths above are untracked in the main checkout. Copy them into the branch working tree at the same paths if it is a separate worktree.

- [ ] **Step 3: Commit them**

```bash
git add docs/superpowers/specs/2026-10-10-markdown-blocks-design.md docs/superpowers/plans/2026-10-10-markdown-blocks.md docs/superpowers/plans/2026-10-10-markdown-blocks-prototype
git commit -m "chore: add temporary design notes for markdown blocks"
```

Task 12 removes them again before the pull request.

---

### Task 1: Head line grammar and page parser

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Storage/Pages/MarkdownBlockHead.cs`
- Create: `src/HomeBlaze/HomeBlaze.Storage/Pages/MarkdownPageParser.cs`
- Test: `src/HomeBlaze/HomeBlaze.Storage.Tests/Pages/MarkdownBlockHeadTests.cs`
- Test: `src/HomeBlaze/HomeBlaze.Storage.Tests/Pages/MarkdownPageParserTests.cs`

**Interfaces:**
- Consumes: Markdig 0.45.0, already referenced by `HomeBlaze.Storage`.
- Produces (namespace `HomeBlaze.Storage.Pages`):
  - `public sealed record MarkdownBlockArgument(string Key, string Value, bool IsQuoted)`
  - `public sealed partial record MarkdownBlockHead(int FenceLength, string Name, string? Id, IReadOnlyList<MarkdownBlockArgument> Arguments)` with `bool IsWidget`, `static bool IsValidId(string)`, `static bool TryParse(string line, out MarkdownBlockHead? head, out string? error)`, `string Format()`
  - `public readonly record struct MarkdownSpan(int Start, int Length)` with `int End`
  - `public enum MarkdownBlockKind { Widget, Slot, Style, Invalid }`
  - `public sealed class MarkdownBlock` with `Kind`, `Index`, `Head`, `Error`, `Line`, `HeadSpan`, `ConfigurationJson`, `ConfigurationSpan`, `Children`
  - `public sealed class MarkdownPage` with `Source`, `Blocks`, `AllBlocks`
  - `public static class MarkdownPageParser` with `static MarkdownPage Parse(string source)` and `internal static readonly MarkdownPipeline Pipeline`

The namespace is `HomeBlaze.Storage.Pages` on purpose. A namespace called `Markdown` under `HomeBlaze.Storage` would shadow Markdig's static `Markdown` class in the existing parser.

- [ ] **Step 1: Write the failing head tests**

Create `src/HomeBlaze/HomeBlaze.Storage.Tests/Pages/MarkdownBlockHeadTests.cs`:

```csharp
using HomeBlaze.Storage.Pages;

namespace HomeBlaze.Storage.Tests.Pages;

public class MarkdownBlockHeadTests
{
    [Fact]
    public void WhenHeadHasTypeIdAndArguments_ThenAllPartsAreParsed()
    {
        // Arrange
        const string line = ":::::HomeBlaze.Components.Card#main title=\"Heizung {x}\" icon=Thermostat speed=3000";

        // Act
        var success = MarkdownBlockHead.TryParse(line, out var head, out var error);

        // Assert
        Assert.True(success, error);
        Assert.Equal(5, head!.FenceLength);
        Assert.Equal("HomeBlaze.Components.Card", head.Name);
        Assert.Equal("main", head.Id);
        Assert.True(head.IsWidget);
        Assert.Equal(
            [new MarkdownBlockArgument("title", "Heizung {x}", true), new MarkdownBlockArgument("icon", "Thermostat", false), new MarkdownBlockArgument("speed", "3000", false)],
            head.Arguments);
    }

    [Fact]
    public void WhenNameHasNoDot_ThenHeadIsASlot()
    {
        // Act
        var success = MarkdownBlockHead.TryParse("::::Tab title=Strom", out var head, out _);

        // Assert
        Assert.True(success);
        Assert.False(head!.IsWidget);
        Assert.Null(head.Id);
    }

    [Fact]
    public void WhenQuotedValueHasEscapes_ThenTheyAreUnescaped()
    {
        // Act
        MarkdownBlockHead.TryParse(":::A.B text=\"a \\\"b\\\" \\\\ c\"", out var head, out _);

        // Assert
        Assert.Equal("a \"b\" \\ c", head!.Arguments[0].Value);
    }

    [Fact]
    public void WhenBareValueContainsColonsAndDots_ThenItIsKeptWhole()
    {
        // Act
        MarkdownBlockHead.TryParse(":::A.B interval=00:00:01.500", out var head, out _);

        // Assert
        Assert.Equal("00:00:01.500", head!.Arguments[0].Value);
    }

    [Theory]
    [InlineData(":::A.B {.extra}", "key=value")]
    [InlineData(":::A.B text=\"open", "closing quote")]
    [InlineData(":::A.B a=1 a=2", "twice")]
    [InlineData(":::A..B", "not a valid block name")]
    [InlineData(":::A.B#1x", "not a valid name after '#'")]
    [InlineData("::A.B", "three colons")]
    public void WhenHeadIsMalformed_ThenErrorNamesTheProblem(string line, string expectedPart)
    {
        // Act
        var success = MarkdownBlockHead.TryParse(line, out _, out var error);

        // Assert
        Assert.False(success);
        Assert.Contains(expectedPart, error);
    }

    [Fact]
    public void WhenHeadIsFormatted_ThenItParsesBackToTheSameHead()
    {
        // Arrange
        const string line = ":::A.B#m title=\"x y\" path=\"/a/{b}\" count=3 quote=\"say \\\"hi\\\"\"";
        MarkdownBlockHead.TryParse(line, out var head, out _);

        // Act
        var formatted = head!.Format();

        // Assert
        Assert.Equal(line, formatted);
    }

    [Fact]
    public void WhenUnquotedValueNeedsQuotes_ThenFormatAddsThem()
    {
        // Arrange
        var head = new MarkdownBlockHead(3, "A.B", null, [new MarkdownBlockArgument("name", "My Motor", false), new MarkdownBlockArgument("empty", "", false)]);

        // Act
        var formatted = head.Format();

        // Assert
        Assert.Equal(":::A.B name=\"My Motor\" empty=\"\"", formatted);
    }
}
```

- [ ] **Step 2: Write the failing page parser tests**

Create `src/HomeBlaze/HomeBlaze.Storage.Tests/Pages/MarkdownPageParserTests.cs`:

````csharp
using HomeBlaze.Storage.Pages;

namespace HomeBlaze.Storage.Tests.Pages;

public class MarkdownPageParserTests
{
    private static string Normalize(string text) => text.Replace("\r\n", "\n");

    [Fact]
    public void WhenPageHasNestedBlocks_ThenTreeFollowsTheFences()
    {
        // Arrange
        var source = Normalize("""
            ---
            title: Test
            ---

            text

            :::::Demo.TabLayout#tabs
            ::::Tab title="Heizung {x}"
            Body

            :::Demo.Widget path="/House/{Heizung}"
            :::
            ::::
            ::::Tab title="Strom"
            ::::
            :::::
            """);

        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        var tabs = Assert.Single(page.Blocks);
        Assert.Equal(MarkdownBlockKind.Widget, tabs.Kind);
        Assert.Equal(7, tabs.Line);
        Assert.Equal("tabs", tabs.Head!.Id);
        Assert.Equal(2, tabs.Children.Count);
        Assert.All(tabs.Children, tab => Assert.Equal(MarkdownBlockKind.Slot, tab.Kind));
        var widget = Assert.Single(tabs.Children[0].Children);
        Assert.Equal("/House/{Heizung}", widget.Head!.Arguments[0].Value);
        Assert.Equal([0, 1, 2, 3], page.AllBlocks.Select(block => block.Index));
    }

    [Fact]
    public void WhenBlockHasHeadLine_ThenHeadSpanCoversExactlyThatLine()
    {
        // Arrange
        var source = Normalize("""
            before

            :::Demo.Motor#m speed=3000
            :::
            """);

        // Act
        var block = MarkdownPageParser.Parse(source).Blocks[0];

        // Assert
        Assert.Equal(":::Demo.Motor#m speed=3000", source.Substring(block.HeadSpan.Start, block.HeadSpan.Length));
    }

    [Fact]
    public void WhenFirstChildIsJsonFence_ThenItIsTheConfiguration()
    {
        // Arrange
        var source = Normalize("""
            :::Demo.Grid rows=1
            ```json
            { "cells": [1, 2] }
            ```
            :::
            """);

        // Act
        var block = MarkdownPageParser.Parse(source).Blocks[0];

        // Assert
        Assert.Equal("{ \"cells\": [1, 2] }", block.ConfigurationJson);
        var span = block.ConfigurationSpan!.Value;
        Assert.StartsWith("```json", source.Substring(span.Start, span.Length));
        Assert.EndsWith("```", source.Substring(span.Start, span.Length));
    }

    [Fact]
    public void WhenBlockIsInsideListItem_ThenItIsInvalid()
    {
        // Arrange
        var source = Normalize("""
            - item
              :::Demo.Card
              text
              :::
            """);

        // Act
        var block = Assert.Single(MarkdownPageParser.Parse(source).AllBlocks);

        // Assert
        Assert.Equal(MarkdownBlockKind.Invalid, block.Kind);
        Assert.Contains("only allowed directly on the page", block.Error);
    }

    [Fact]
    public void WhenUnnamedBlockHasAttributes_ThenItIsAStyleBlock()
    {
        // Arrange
        var source = Normalize("""
            ::: {.band}
            text
            :::
            """);

        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        Assert.Equal(MarkdownBlockKind.Style, Assert.Single(page.Blocks).Kind);
        Assert.Empty(page.AllBlocks);
    }

    [Fact]
    public void WhenWidgetIsInsideStyleBlock_ThenItIsInvalid()
    {
        // Arrange
        var source = Normalize("""
            :::: {.band}
            :::Demo.Card
            :::
            ::::
            """);

        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        Assert.Equal(MarkdownBlockKind.Invalid, Assert.Single(page.AllBlocks).Kind);
    }

    [Fact]
    public void WhenInnerFenceIsAsLongAsOuter_ThenAnUnnamedBlockIsReported()
    {
        // Arrange
        var source = Normalize("""
            :::Demo.Card
            :::Demo.Widget path=/x
            :::
            after
            :::
            rest
            """);

        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        var unnamed = page.AllBlocks.Single(block => block.Kind == MarkdownBlockKind.Invalid);
        Assert.Contains("outer block must be longer", unnamed.Error);
    }

    [Fact]
    public void WhenSlotBlockIsAtPageLevel_ThenItIsInvalid()
    {
        // Arrange
        var source = Normalize("""
            :::Footer
            text
            :::
            """);

        // Act
        var block = Assert.Single(MarkdownPageParser.Parse(source).AllBlocks);

        // Assert
        Assert.Equal(MarkdownBlockKind.Invalid, block.Kind);
        Assert.Contains("not a type name", block.Error);
    }

    [Fact]
    public void WhenHeadLineIsMalformed_ThenBlockCarriesTheGrammarError()
    {
        // Arrange
        var source = Normalize("""
            :::Demo.Card title="open
            :::
            """);

        // Act
        var block = Assert.Single(MarkdownPageParser.Parse(source).AllBlocks);

        // Assert
        Assert.Equal(MarkdownBlockKind.Invalid, block.Kind);
        Assert.Contains("closing quote", block.Error);
    }
}
````

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests --filter "FullyQualifiedName~HomeBlaze.Storage.Tests.Pages"`
Expected: build error, `HomeBlaze.Storage.Pages` does not exist.

- [ ] **Step 4: Add the implementation from the prototype**

```bash
mkdir -p src/HomeBlaze/HomeBlaze.Storage/Pages
cp docs/superpowers/plans/2026-10-10-markdown-blocks-prototype/MarkdownBlockHead.cs src/HomeBlaze/HomeBlaze.Storage/Pages/
cp docs/superpowers/plans/2026-10-10-markdown-blocks-prototype/MarkdownPageParser.cs src/HomeBlaze/HomeBlaze.Storage/Pages/
```

Do not change the logic. If an analyzer rejects something under warnings-as-errors, make the smallest change that satisfies it.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests --filter "FullyQualifiedName~HomeBlaze.Storage.Tests.Pages"`
Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Storage/Pages/MarkdownBlockHead.cs src/HomeBlaze/HomeBlaze.Storage/Pages/MarkdownPageParser.cs src/HomeBlaze/HomeBlaze.Storage.Tests/Pages/MarkdownBlockHeadTests.cs src/HomeBlaze/HomeBlaze.Storage.Tests/Pages/MarkdownPageParserTests.cs
git commit -m "feat: parse block syntax of markdown pages into a tree"
```

---

### Task 2: Segment builder

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Storage/Pages/MarkdownSegmentBuilder.cs`
- Test: `src/HomeBlaze/HomeBlaze.Storage.Tests/Pages/MarkdownSegmentBuilderTests.cs`

**Interfaces:**
- Consumes: `MarkdownPage`, `MarkdownBlock`, `MarkdownPageParser.Pipeline` from Task 1.
- Produces (namespace `HomeBlaze.Storage.Pages`):
  - `public abstract record MarkdownSegment`
  - `public sealed record HtmlMarkdownSegment(string Html) : MarkdownSegment`
  - `public sealed record ExpressionMarkdownSegment(string Path) : MarkdownSegment` (path is trimmed)
  - `public sealed record BlockMarkdownSegment(MarkdownBlock Block, IReadOnlyList<MarkdownSegment> Content) : MarkdownSegment`
  - `public static class MarkdownErrorBox` with `static string Render(int line, string message)`, which produces `<div class="markdown-block-error" role="alert">Line N: message</div>`
  - `public static partial class MarkdownSegmentBuilder` with `const string ExpressionPattern` and `static IReadOnlyList<MarkdownSegment> Build(MarkdownPage page, Func<string, string>? linkRewriter = null)`

Only widget and slot blocks become `BlockMarkdownSegment`. Invalid blocks and old `subject(name)` fences are rendered as error boxes inside the HTML. Style blocks are rendered as Markdig's `<div>`.

- [ ] **Step 1: Write the failing tests**

Create `src/HomeBlaze/HomeBlaze.Storage.Tests/Pages/MarkdownSegmentBuilderTests.cs`:

````csharp
using HomeBlaze.Storage.Pages;

namespace HomeBlaze.Storage.Tests.Pages;

public class MarkdownSegmentBuilderTests
{
    private static IReadOnlyList<MarkdownSegment> Build(string source, Func<string, string>? linkRewriter = null) =>
        MarkdownSegmentBuilder.Build(MarkdownPageParser.Parse(source.Replace("\r\n", "\n")), linkRewriter);

    private static string AllHtml(IEnumerable<MarkdownSegment> segments) =>
        string.Concat(segments.Select(segment => segment switch
        {
            HtmlMarkdownSegment html => html.Html,
            BlockMarkdownSegment block => AllHtml(block.Content),
            _ => string.Empty
        }));

    [Fact]
    public void WhenPageHasFrontmatterAndExpression_ThenFrontmatterIsDroppedAndPathIsTrimmed()
    {
        // Act
        var segments = Build("""
            ---
            title: Test
            ---

            Value {{   a/b   }} end
            """);

        // Assert
        Assert.Equal(new HtmlMarkdownSegment("<p>Value "), segments[0]);
        Assert.Equal(new ExpressionMarkdownSegment("a/b"), segments[1]);
        Assert.DoesNotContain("title", AllHtml(segments));
    }

    [Fact]
    public void WhenBlocksAreNested_ThenSegmentsAreNestedTheSameWay()
    {
        // Act
        var segments = Build("""
            ::::Demo.Card
            Text {{ /A/B }}

            :::Demo.Widget path=/x
            :::
            ::::
            """);

        // Assert
        var card = Assert.Single(segments.OfType<BlockMarkdownSegment>());
        Assert.Equal("Demo.Card", card.Block.Head!.Name);
        Assert.Contains(card.Content, segment => segment is ExpressionMarkdownSegment { Path: "/A/B" });
        var widget = Assert.Single(card.Content.OfType<BlockMarkdownSegment>());
        Assert.Equal("Demo.Widget", widget.Block.Head!.Name);
        Assert.Empty(widget.Content);
    }

    [Fact]
    public void WhenBlockHasJsonFence_ThenFenceIsNotRendered()
    {
        // Act
        var segments = Build("""
            :::Demo.Grid
            ```json
            { "cells": [] }
            ```
            Visible text
            :::
            """);

        // Assert
        var html = AllHtml(segments);
        Assert.DoesNotContain("cells", html);
        Assert.Contains("Visible text", html);
    }

    [Fact]
    public void WhenBlockIsInvalid_ThenAnErrorBoxWithTheLineIsRendered()
    {
        // Act
        var segments = Build("""
            first

            :::
            unnamed
            :::
            """);

        // Assert
        Assert.Empty(segments.OfType<BlockMarkdownSegment>());
        Assert.Contains("<div class=\"markdown-block-error\" role=\"alert\">Line 3:", AllHtml(segments));
    }

    [Fact]
    public void WhenPageHasOldSubjectFence_ThenAnErrorBoxExplainsTheNewSyntax()
    {
        // Act
        var segments = Build("""
            ```subject(old)
            { "$type": "X" }
            ```
            """);

        // Assert
        var html = AllHtml(segments);
        Assert.Contains("no longer supported", html);
        Assert.DoesNotContain("$type", html);
    }

    [Fact]
    public void WhenPageHasOrdinaryAndMermaidFences_ThenTheyRenderAsBefore()
    {
        // Act
        var segments = Build("""
            ```csharp
            var x = 1;
            ```

            ```mermaid
            graph TD;
            ```
            """);

        // Assert
        var html = AllHtml(segments);
        Assert.Contains("<pre><code class=\"language-csharp\">", html);
        Assert.Contains("<pre class=\"mermaid\">", html);
    }

    [Fact]
    public void WhenStyleBlockIsUsed_ThenItRendersAsDivWithItsClass()
    {
        // Act
        var segments = Build("""
            ::: {.band}
            text
            :::
            """);

        // Assert
        Assert.Contains("<div class=\"band\">", AllHtml(segments));
    }

    [Fact]
    public void WhenLinkRewriterIsGiven_ThenLinksInsideBlocksAreRewritten()
    {
        // Act
        var segments = Build("""
            :::Demo.Card
            [Notes](notes.md)
            :::
            """, url => "/pages/" + url);

        // Assert
        Assert.Contains("href=\"/pages/notes.md\"", AllHtml(segments));
    }
}
````

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests --filter "FullyQualifiedName~MarkdownSegmentBuilderTests"`
Expected: build error, `MarkdownSegmentBuilder` does not exist.

- [ ] **Step 3: Add the implementation from the prototype**

```bash
cp docs/superpowers/plans/2026-10-10-markdown-blocks-prototype/MarkdownSegmentBuilder.cs src/HomeBlaze/HomeBlaze.Storage/Pages/
```

One part of this file is easy to break. Markdig's diagram extension configures the code block renderer by exact type during `Pipeline.Setup`. The file therefore runs `Setup` first, then replaces the renderer and copies `OutputAttributesOnPre`, `BlocksAsDiv`, `BlockMapping` and `TryWriters` from the original. Replacing before `Setup` throws a `NullReferenceException` inside Markdig.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests --filter "FullyQualifiedName~MarkdownSegmentBuilderTests"`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Storage/Pages/MarkdownSegmentBuilder.cs src/HomeBlaze/HomeBlaze.Storage.Tests/Pages/MarkdownSegmentBuilderTests.cs
git commit -m "feat: split rendered markdown pages into segments per block"
```

---

### Task 3: Block writer

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Storage/Pages/MarkdownBlockWriter.cs`
- Test: `src/HomeBlaze/HomeBlaze.Storage.Tests/Pages/MarkdownBlockWriterTests.cs`

**Interfaces:**
- Consumes: `MarkdownBlock`, `MarkdownBlockHead` from Task 1.
- Produces (namespace `HomeBlaze.Storage.Pages`):
  - `public static string MarkdownBlockWriter.WriteConfiguration(string source, MarkdownBlock block, string configurationJson)`
  - `public static string MarkdownBlockWriter.CreateBlock(string typeName, string? id, string configurationJson)`

`configurationJson` is the JSON object the configuration serializer produces. `$type` and `null` values are dropped. Strings, numbers and booleans go on the head line. Objects and arrays go into the `json` fence, which is added, replaced or removed as needed.

- [ ] **Step 1: Write the failing tests**

Create `src/HomeBlaze/HomeBlaze.Storage.Tests/Pages/MarkdownBlockWriterTests.cs`:

````csharp
using HomeBlaze.Storage.Pages;

namespace HomeBlaze.Storage.Tests.Pages;

public class MarkdownBlockWriterTests
{
    private const string Source =
        "before\n\n::::Demo.Card#main title=Old\nBody **text** {{ /A/B }}\n\n:::Demo.Motor speed=1\n```json\n{ \"cells\": [1] }\n```\n:::\n::::\n\nafter\n";

    private static MarkdownBlock Card => MarkdownPageParser.Parse(Source).Blocks[0];

    private static MarkdownBlock Motor => Card.Children[0];

    [Fact]
    public void WhenScalarsChange_ThenOnlyTheHeadLineChanges()
    {
        // Act
        var result = MarkdownBlockWriter.WriteConfiguration(
            Source, Card, """{ "$type": "Demo.Card", "title": "New Title", "count": 2, "enabled": true, "nothing": null }""");

        // Assert
        Assert.Equal(
            Source.Replace("::::Demo.Card#main title=Old", "::::Demo.Card#main title=\"New Title\" count=2 enabled=true"),
            result);
    }

    [Fact]
    public void WhenComplexValueChanges_ThenTheJsonFenceIsReplaced()
    {
        // Act
        var result = MarkdownBlockWriter.WriteConfiguration(Source, Motor, """{ "speed": 5, "cells": [3] }""");

        // Assert
        var motor = MarkdownPageParser.Parse(result).Blocks[0].Children[0];
        Assert.Equal(":::Demo.Motor speed=5", result.Substring(motor.HeadSpan.Start, motor.HeadSpan.Length));
        Assert.Equal("{\"cells\":[3]}", motor.ConfigurationJson!.Replace(" ", "").Replace("\n", ""));
    }

    [Fact]
    public void WhenNoComplexValueIsLeft_ThenTheJsonFenceIsRemoved()
    {
        // Act
        var result = MarkdownBlockWriter.WriteConfiguration(Source, Motor, """{ "speed": 5 }""");

        // Assert
        Assert.Contains(":::Demo.Motor speed=5\n:::\n::::", result);
        Assert.DoesNotContain("```", result);
    }

    [Fact]
    public void WhenBlockHadNoFence_ThenAComplexValueAddsOneAfterTheHead()
    {
        // Act
        var result = MarkdownBlockWriter.WriteConfiguration(Source, Card, """{ "title": "Old", "tags": ["a"] }""");

        // Assert
        Assert.Contains("::::Demo.Card#main title=Old\n```json\n{\n  \"tags\": [\n    \"a\"\n  ]\n}\n```\nBody **text**", result);
    }

    [Fact]
    public void WhenBlockIsCreated_ThenItIsAClosedThreeColonBlock()
    {
        // Act
        var block = MarkdownBlockWriter.CreateBlock("Demo.Motor", "m1", """{ "$type": "Demo.Motor", "name": "My Motor", "speed": 3 }""");

        // Assert
        Assert.Equal(":::Demo.Motor#m1 name=\"My Motor\" speed=3\n:::", block);
    }

    [Fact]
    public void WhenCreatedBlockHasComplexValue_ThenItContainsAJsonFence()
    {
        // Act
        var block = MarkdownBlockWriter.CreateBlock("Demo.Grid", null, """{ "rows": 1, "cells": [] }""");

        // Assert
        Assert.Equal(":::Demo.Grid rows=1\n```json\n{\n  \"cells\": []\n}\n```\n:::", block);
    }
}
````

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests --filter "FullyQualifiedName~MarkdownBlockWriterTests"`
Expected: build error, `MarkdownBlockWriter` does not exist.

- [ ] **Step 3: Add the implementation from the prototype**

```bash
cp docs/superpowers/plans/2026-10-10-markdown-blocks-prototype/MarkdownBlockWriter.cs src/HomeBlaze/HomeBlaze.Storage/Pages/
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests --filter "FullyQualifiedName~MarkdownBlockWriterTests"`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Storage/Pages/MarkdownBlockWriter.cs src/HomeBlaze/HomeBlaze.Storage.Tests/Pages/MarkdownBlockWriterTests.cs
git commit -m "feat: write block configuration back into markdown pages"
```

---

### Task 4: Widget blocks replace the fence in the content parser and the write-back

After this task a page creates subjects from `:::Full.Type.Name` blocks. Content inside a block is an error until Task 8. The old fence shows an error box.

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Storage/Pages/MarkdownParseResult.cs`
- Rewrite: `src/HomeBlaze/HomeBlaze.Storage/Internal/MarkdownContentParser.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Storage/Files/MarkdownFile.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Storage.Blazor/Files/MarkdownFilePageComponent.razor` (style block only)
- Modify: `src/HomeBlaze/HomeBlaze.Storage.Tests/HomeBlaze.Storage.Tests.csproj`
- Create: `src/HomeBlaze/HomeBlaze.Storage.Tests/Pages/MarkdownPageFixture.cs`
- Create: `src/HomeBlaze/HomeBlaze.Storage.Tests/Pages/TestSubjects.cs`
- Test: `src/HomeBlaze/HomeBlaze.Storage.Tests/Pages/MarkdownWidgetBlockTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 1 to 3. `ConfigurableSubjectSerializer.Deserialize(string)`, `UpdateConfiguration(IInterceptorSubject, string)`, `Serialize(IInterceptorSubject)`. `TypeProvider.Types`.
- Produces:
  - `public sealed record MarkdownParseResult(IDictionary<string, IInterceptorSubject> Children, MarkdownPage? Page, IReadOnlyDictionary<int, IInterceptorSubject> BlockSubjects, IReadOnlyDictionary<string, IInterceptorSubject> NamedSubjects)` with `static MarkdownParseResult Empty`
  - `MarkdownContentParser(ConfigurableSubjectSerializer serializer, SubjectPathResolver pathResolver, TypeProvider typeProvider)` and `Task<MarkdownParseResult> ParseAsync(string? content, MarkdownFile parent, IDictionary<string, IInterceptorSubject> existingChildren, CancellationToken cancellationToken)`
  - `public IInterceptorSubject? MarkdownFile.GetBlockSubject(int blockIndex)`
  - Child keys: `#id` when given, otherwise `{Type.Name}_{segmentIndex}`.
  - Private `MarkdownContentParser.ApplyContentAsync(IInterceptorSubject subject, Type type, IReadOnlyList<MarkdownSegment> content, ParseState state, CancellationToken cancellationToken)` returning `Task<string?>` (an error message or null). Task 8 replaces its body.
  - Private `MarkdownContentParser.TryBuildConfiguration(Type type, MarkdownBlock block, out string json, out string? error)`. Task 8 reuses it.

- [ ] **Step 1: Let the test project declare subjects**

In `src/HomeBlaze/HomeBlaze.Storage.Tests/HomeBlaze.Storage.Tests.csproj` add next to the other project references:

```xml
<ProjectReference Include="..\..\Namotion.Interceptor.Generator\Namotion.Interceptor.Generator.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
```

- [ ] **Step 2: Add test subjects**

Create `src/HomeBlaze/HomeBlaze.Storage.Tests/Pages/TestSubjects.cs`:

```csharp
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Storage.Tests.Pages;

[InterceptorSubject]
public partial class TestLeaf : IConfigurable
{
    [Configuration]
    public partial string? Name { get; set; }

    [Configuration]
    public partial int Count { get; set; }

    [Configuration]
    public partial bool Enabled { get; set; }

    [Configuration]
    public partial TimeSpan Interval { get; set; }

    [Configuration]
    public partial List<string>? Tags { get; set; }

    public int ApplyCount { get; private set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default)
    {
        ApplyCount++;
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 3: Add the fixture**

It builds a real context with registry and parents and attaches an in-memory storage as the graph root, so that paths, parents and registered properties work.

Create `src/HomeBlaze/HomeBlaze.Storage.Tests/Pages/MarkdownPageFixture.cs`:

```csharp
using System.Text;
using HomeBlaze.Services;
using HomeBlaze.Storage.Files;
using HomeBlaze.Storage.Internal;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Tests.Pages;

public sealed class MarkdownPageFixture
{
    private MarkdownPageFixture(FluentStorageContainer storage, IServiceProvider services)
    {
        Storage = storage;
        Services = services;
    }

    public FluentStorageContainer Storage { get; }

    public IServiceProvider Services { get; }

    public static async Task<MarkdownPageFixture> CreateAsync()
    {
        var typeProvider = new TypeProvider();
        typeProvider.AddAssembly(typeof(FluentStorageContainer).Assembly);
        typeProvider.AddAssembly(typeof(MarkdownPageFixture).Assembly);
        var typeRegistry = new SubjectTypeRegistry(typeProvider);

        var services = new ServiceCollection();
        var context = SubjectContextFactory.Create(services);
        FluentStorageContainer? storage = null;

        services.AddSingleton(typeProvider);
        services.AddSingleton(typeRegistry);
        services.AddSingleton(context);
        services.AddSingleton<SubjectFactory>();
        services.AddSingleton<ConfigurableSubjectSerializer>();
        services.AddSingleton(_ => new SubjectPathResolver(() => storage));
        services.AddSingleton<RootManager>();
        services.AddSingleton<MarkdownContentParser>();
        var serviceProvider = services.BuildServiceProvider();

        // RootManager registers itself and the path resolver in the context from its constructor.
        serviceProvider.GetRequiredService<RootManager>();

        storage = new FluentStorageContainer(
            typeRegistry, serviceProvider.GetRequiredService<ConfigurableSubjectSerializer>(), serviceProvider)
        {
            StorageType = "inmemory"
        };
        ((IInterceptorSubject)storage).Context.AddFallbackContext(context);
        await storage.ConnectAsync(CancellationToken.None);

        return new MarkdownPageFixture(storage, serviceProvider);
    }

    public async Task<MarkdownFile> AddPageAsync(string name, string content)
    {
        await WriteAsync(name, content);
        var file = new MarkdownFile(
            Storage,
            name,
            Services.GetRequiredService<MarkdownContentParser>(),
            Services.GetRequiredService<ConfigurableSubjectSerializer>());

        Storage.Children = new Dictionary<string, IInterceptorSubject>(Storage.Children) { [name] = file };
        await file.OnFileChangedAsync(CancellationToken.None);
        return file;
    }

    public async Task UpdatePageAsync(MarkdownFile file, string content)
    {
        await WriteAsync(file.Name, content);
        await file.OnFileChangedAsync(CancellationToken.None);
    }

    public async Task<string> ReadAsync(string name)
    {
        await using var stream = await Storage.ReadBlobAsync(name, CancellationToken.None);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    public static string Html(IEnumerable<KeyValuePair<string, IInterceptorSubject>> children) =>
        string.Concat(children.Select(child => child.Value is HtmlSegment html ? html.Html : string.Empty));

    private async Task WriteAsync(string name, string content)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content.Replace("\r\n", "\n")));
        await Storage.WriteBlobAsync(name, stream, CancellationToken.None);
    }
}
```

If `SubjectContextFactory.Create(services)` starts hosted services that the test does not want, keep it anyway: the test subjects are not background services.

- [ ] **Step 4: Write the failing tests**

Create `src/HomeBlaze/HomeBlaze.Storage.Tests/Pages/MarkdownWidgetBlockTests.cs`:

````csharp
using HomeBlaze.Storage.Internal;

namespace HomeBlaze.Storage.Tests.Pages;

public class MarkdownWidgetBlockTests
{
    private const string LeafType = "HomeBlaze.Storage.Tests.Pages.TestLeaf";

    [Fact]
    public async Task WhenPageHasWidgetBlock_ThenSubjectIsCreatedWithTypedArguments()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();

        // Act
        var page = await fixture.AddPageAsync("page.md", $$"""
            text

            :::{{LeafType}}#leaf name=123 count=5 enabled=true interval=00:00:01.500
            :::
            """);

        // Assert
        var leaf = Assert.IsType<TestLeaf>(page.Children["leaf"]);
        Assert.Equal("123", leaf.Name);
        Assert.Equal(5, leaf.Count);
        Assert.True(leaf.Enabled);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), leaf.Interval);
        Assert.Same(leaf, page.GetBlockSubject(0));
    }

    [Fact]
    public async Task WhenBlockHasNoId_ThenKeyIsTypeNameAndPosition()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();

        // Act
        var page = await fixture.AddPageAsync("page.md", $$"""
            :::{{LeafType}} name=a
            :::
            """);

        // Assert
        Assert.IsType<TestLeaf>(page.Children["TestLeaf_0"]);
    }

    [Fact]
    public async Task WhenBlockHasJsonFence_ThenArgumentsWinOverTheFence()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();

        // Act
        var page = await fixture.AddPageAsync("page.md", $$"""
            :::{{LeafType}}#leaf count=7
            ```json
            { "count": 1, "tags": ["a", "b"] }
            ```
            :::
            """);

        // Assert
        var leaf = Assert.IsType<TestLeaf>(page.Children["leaf"]);
        Assert.Equal(7, leaf.Count);
        Assert.Equal(["a", "b"], leaf.Tags);
    }

    [Fact]
    public async Task WhenPageIsParsedAgain_ThenTheSameInstanceIsUpdatedInPlace()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync("page.md", $$"""
            :::{{LeafType}}#leaf count=1
            :::
            """);
        var first = page.Children["leaf"];

        // Act
        await fixture.UpdatePageAsync(page, $$"""
            new text above

            :::{{LeafType}}#leaf count=2
            :::
            """);

        // Assert
        var leaf = Assert.IsType<TestLeaf>(page.Children["leaf"]);
        Assert.Same(first, leaf);
        Assert.Equal(2, leaf.Count);
        Assert.True(leaf.ApplyCount >= 1);
    }

    [Theory]
    [InlineData(":::Missing.Type\n:::", "Unknown type 'Missing.Type'")]
    [InlineData(":::" + LeafType + " cont=1\n:::", "has no configuration property 'cont'")]
    [InlineData(":::" + LeafType + " count=abc\n:::", "'abc' is not a valid value for 'count'")]
    [InlineData(":::" + LeafType + "\n```json\n[1]\n```\n:::", "must contain a JSON object")]
    [InlineData(":::" + LeafType + "\ncontent\n:::", "cannot have content")]
    [InlineData(":::" + LeafType + "#a\n:::\n\n:::" + LeafType + "#a\n:::", "used twice")]
    public async Task WhenBlockIsWrong_ThenAnErrorBoxReplacesIt(string content, string expectedMessage)
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();

        // Act
        var page = await fixture.AddPageAsync("page.md", content);

        // Assert
        var html = MarkdownPageFixture.Html(page.Children);
        Assert.Contains("markdown-block-error", html);
        Assert.Contains(System.Net.WebUtility.HtmlEncode(expectedMessage), html);
    }

    [Fact]
    public async Task WhenPageHasOldFence_ThenNoSubjectIsCreated()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();

        // Act
        var page = await fixture.AddPageAsync("page.md", $$"""
            ```subject(leaf)
            { "$type": "{{LeafType}}" }
            ```
            """);

        // Assert
        Assert.All(page.Children.Values, child => Assert.IsType<HtmlSegment>(child));
        Assert.Contains("no longer supported", MarkdownPageFixture.Html(page.Children));
    }

    [Fact]
    public async Task WhenConfigurationIsWritten_ThenOnlyTheHeadLineOfThatBlockChanges()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var content = $"intro\n\n:::{LeafType}#first count=1\n:::\n\n:::{LeafType}#second count=2\n:::\n\noutro {{{{ /x }}}}\n";
        var page = await fixture.AddPageAsync("page.md", content);
        var second = (TestLeaf)page.Children["second"];
        second.Count = 9;
        second.Name = "New Name";

        // Act
        var handled = await page.WriteConfigurationAsync(second, CancellationToken.None);

        // Assert
        Assert.True(handled);
        var written = await fixture.ReadAsync("page.md");
        Assert.Equal(
            content.Replace(
                $":::{LeafType}#second count=2",
                $":::{LeafType}#second name=\"New Name\" count=9 enabled=false interval=00:00:00"),
            written);
    }

    [Fact]
    public async Task WhenSubjectIsNotFromThePage_ThenWriteConfigurationReturnsFalse()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync("page.md", "text");

        // Act
        var handled = await page.WriteConfigurationAsync(new TestLeaf(), CancellationToken.None);

        // Assert
        Assert.False(handled);
    }
}
````

The exact argument order in `WhenConfigurationIsWritten_...` is the property order the serializer emits. If the serializer emits a different order or omits default values, adjust the expected head line to what it really writes. The point of the test is that nothing outside that head line differs.

- [ ] **Step 5: Run the tests to verify they fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests --filter "FullyQualifiedName~MarkdownWidgetBlockTests"`
Expected: build errors (`GetBlockSubject` missing), then failures because the blocks are not parsed.

- [ ] **Step 6: Add the parse result**

Create `src/HomeBlaze/HomeBlaze.Storage/Pages/MarkdownParseResult.cs`:

```csharp
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Pages;

/// <summary>What parsing a page produces: its children and the lookups the page keeps for write-back and names.</summary>
public sealed record MarkdownParseResult(
    IDictionary<string, IInterceptorSubject> Children,
    MarkdownPage? Page,
    IReadOnlyDictionary<int, IInterceptorSubject> BlockSubjects,
    IReadOnlyDictionary<string, IInterceptorSubject> NamedSubjects)
{
    public static MarkdownParseResult Empty { get; } = new(
        new Dictionary<string, IInterceptorSubject>(),
        null,
        new Dictionary<int, IInterceptorSubject>(),
        new Dictionary<string, IInterceptorSubject>());
}
```

- [ ] **Step 7: Rewrite the content parser**

In `src/HomeBlaze/HomeBlaze.Storage/Internal/MarkdownContentParser.cs` keep these members unchanged: `RewriteLink`, `GetLinkBasePath`, `ResolvePath`, `ComputeHash`, `RenderContent`. Remove: the `Pipeline` field, `ToHtml`, `SubjectMarkerPrefix`, `SubjectMarkerSuffix`, `SubjectBlockRegex`, `SegmentMarkerRegex`, `ExtractSubjectBlocks`, `ParseHtmlSegments`, `ReconcileChildrenAsync`, `ExtractTypeName` and the four `ParsedSegment` records. Change `ExpressionPattern` to reuse the shared constant, and replace the constructor and `ParseAsync` with the code below.

```csharp
using System.Buffers;
using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Services;
using HomeBlaze.Storage.Files;
using HomeBlaze.Storage.Pages;
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Internal;

public sealed partial class MarkdownContentParser
{
    private const string HtmlKeyPrefix = "Html_";
    private const string ExpressionKeyPrefix = "Expression_";

    private static readonly ConcurrentDictionary<Type, IReadOnlyDictionary<string, Type>> ConfigurationPropertyCache = new();

    private readonly ConfigurableSubjectSerializer _serializer;
    private readonly SubjectPathResolver _pathResolver;
    private readonly TypeProvider _typeProvider;

    public MarkdownContentParser(
        ConfigurableSubjectSerializer serializer,
        SubjectPathResolver pathResolver,
        TypeProvider typeProvider)
    {
        _serializer = serializer;
        _pathResolver = pathResolver;
        _typeProvider = typeProvider;
    }

    [GeneratedRegex(MarkdownSegmentBuilder.ExpressionPattern)]
    private static partial Regex ExpressionRegex();

    /// <summary>
    /// Parses page content and reconciles it with the existing children.
    /// </summary>
    public async Task<MarkdownParseResult> ParseAsync(
        string? content,
        MarkdownFile parent,
        IDictionary<string, IInterceptorSubject> existingChildren,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(content))
        {
            return MarkdownParseResult.Empty;
        }

        var page = MarkdownPageParser.Parse(content);
        var basePath = GetLinkBasePath(parent);
        var segments = MarkdownSegmentBuilder.Build(page, url => RewriteLink(url, basePath));

        var state = new ParseState(parent);
        var children = await BuildChildrenAsync(segments, existingChildren, state, cancellationToken);
        return new MarkdownParseResult(children, page, state.BlockSubjects, state.NamedSubjects);
    }

    private async Task<IDictionary<string, IInterceptorSubject>> BuildChildrenAsync(
        IReadOnlyList<MarkdownSegment> segments,
        IDictionary<string, IInterceptorSubject> existingChildren,
        ParseState state,
        CancellationToken cancellationToken)
    {
        var children = new Dictionary<string, IInterceptorSubject>();

        for (var index = 0; index < segments.Count; index++)
        {
            switch (segments[index])
            {
                case HtmlMarkdownSegment html:
                    AddHtml(children, existingChildren, index, html.Html);
                    break;

                case ExpressionMarkdownSegment expression:
                    var expressionKey = $"{ExpressionKeyPrefix}{index}_{ComputeHash(expression.Path)}";
                    children[expressionKey] =
                        existingChildren.TryGetValue(expressionKey, out var existingExpression) && existingExpression is RenderExpression
                            ? existingExpression
                            : new RenderExpression(expression.Path, state.Page, _pathResolver);
                    break;

                case BlockMarkdownSegment block:
                    var (key, subject, error) = await CreateWidgetAsync(block, existingChildren, index, state, cancellationToken);
                    if (subject is null)
                    {
                        AddHtml(children, existingChildren, index, MarkdownErrorBox.Render(block.Block.Line, error!));
                    }
                    else
                    {
                        children[key!] = subject;
                    }

                    break;
            }
        }

        return children;
    }

    private static void AddHtml(
        Dictionary<string, IInterceptorSubject> children,
        IDictionary<string, IInterceptorSubject> existingChildren,
        int index,
        string html)
    {
        // The index is part of the key because the same HTML can occur several times.
        var key = $"{HtmlKeyPrefix}{index}_{ComputeHash(html)}";
        children[key] = existingChildren.TryGetValue(key, out var existing) && existing is HtmlSegment
            ? existing
            : new HtmlSegment(html);
    }

    private async Task<(string? Key, IInterceptorSubject? Subject, string? Error)> CreateWidgetAsync(
        BlockMarkdownSegment segment,
        IDictionary<string, IInterceptorSubject> existingChildren,
        int index,
        ParseState state,
        CancellationToken cancellationToken)
    {
        var block = segment.Block;
        var head = block.Head!;

        if (block.Kind == MarkdownBlockKind.Slot)
        {
            return (null, null, $"'{head.Name}' is not a slot at this position. A slot block must be directly inside the widget that declares it.");
        }

        var type = _typeProvider.Types.FirstOrDefault(candidate => candidate.FullName == head.Name);
        if (type is null || !typeof(IConfigurable).IsAssignableFrom(type))
        {
            return (null, null, $"Unknown type '{head.Name}'. Use the full type name including the namespace.");
        }

        if (head.Id is not null && state.NamedSubjects.ContainsKey(head.Id))
        {
            return (null, null, $"The name '{head.Id}' is used twice on this page.");
        }

        if (!TryBuildConfiguration(type, block, out var json, out var configurationError))
        {
            return (null, null, configurationError);
        }

        var key = head.Id ?? $"{type.Name}_{index}";
        existingChildren.TryGetValue(key, out var existing);
        var subject = await CreateOrUpdateAsync(type, existing, json, cancellationToken);
        if (subject is null)
        {
            return (null, null, $"The type '{head.Name}' could not be created.");
        }

        var contentError = await ApplyContentAsync(subject, type, segment.Content, state, cancellationToken);
        if (contentError is not null)
        {
            return (null, null, contentError);
        }

        state.Register(block, head.Id, subject);
        return (key, subject, null);
    }

    private async Task<IInterceptorSubject?> CreateOrUpdateAsync(
        Type type, IInterceptorSubject? existing, string json, CancellationToken cancellationToken)
    {
        if (existing is not null && existing.GetType() == type)
        {
            _serializer.UpdateConfiguration(existing, json);
            if (existing is IConfigurable configurable)
            {
                await configurable.ApplyConfigurationAsync(cancellationToken);
            }

            return existing;
        }

        return _serializer.Deserialize(json) as IInterceptorSubject;
    }

    // Replaced in the slots task. Until then a widget block cannot have content.
    private Task<string?> ApplyContentAsync(
        IInterceptorSubject subject,
        Type type,
        IReadOnlyList<MarkdownSegment> content,
        ParseState state,
        CancellationToken cancellationToken)
    {
        var hasContent = content.Any(segment => segment is not HtmlMarkdownSegment html || !string.IsNullOrWhiteSpace(html.Html));
        return Task.FromResult(hasContent ? $"{type.Name} cannot have content." : null);
    }

    private static bool TryBuildConfiguration(Type type, MarkdownBlock block, out string json, out string? error)
    {
        json = string.Empty;
        error = null;

        JsonObject configuration;
        if (block.ConfigurationJson is { } text)
        {
            try
            {
                configuration = JsonNode.Parse(text) as JsonObject
                    ?? throw new JsonException("Not an object.");
            }
            catch (JsonException)
            {
                error = "The json block must contain a JSON object.";
                return false;
            }
        }
        else
        {
            configuration = new JsonObject();
        }

        var properties = GetConfigurationProperties(type);
        foreach (var argument in block.Head!.Arguments)
        {
            if (!properties.TryGetValue(argument.Key, out var propertyType))
            {
                error = $"{type.Name} has no configuration property '{argument.Key}'. Available: {string.Join(", ", properties.Keys.Order())}.";
                return false;
            }

            if (!TryConvertArgument(argument, propertyType, out var node))
            {
                error = $"'{argument.Value}' is not a valid value for '{argument.Key}'.";
                return false;
            }

            configuration[argument.Key] = node;
        }

        configuration["$type"] = type.FullName;
        json = configuration.ToJsonString();
        return true;
    }

    private static IReadOnlyDictionary<string, Type> GetConfigurationProperties(Type type) =>
        ConfigurationPropertyCache.GetOrAdd(type, static key => key
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.GetCustomAttribute<ConfigurationAttribute>(true) is not null)
            .ToDictionary(property => JsonNamingPolicy.CamelCase.ConvertName(property.Name), property => property.PropertyType));

    private static bool TryConvertArgument(MarkdownBlockArgument argument, Type propertyType, out JsonNode? node)
    {
        var target = Nullable.GetUnderlyingType(propertyType) ?? propertyType;

        if (target == typeof(bool))
        {
            var isBoolean = bool.TryParse(argument.Value, out var boolean);
            node = JsonValue.Create(boolean);
            return isBoolean;
        }

        var isNumeric = target == typeof(decimal) || (target.IsPrimitive && target != typeof(char));
        if (isNumeric)
        {
            try
            {
                node = JsonNode.Parse(argument.Value);
                return node is not null && node.GetValueKind() == JsonValueKind.Number;
            }
            catch (JsonException)
            {
                node = null;
                return false;
            }
        }

        node = JsonValue.Create(argument.Value);
        return true;
    }

    private sealed class ParseState(MarkdownFile page)
    {
        public MarkdownFile Page { get; } = page;

        public Dictionary<int, IInterceptorSubject> BlockSubjects { get; } = new();

        public Dictionary<string, IInterceptorSubject> NamedSubjects { get; } = new();

        public void Register(MarkdownBlock block, string? id, IInterceptorSubject subject)
        {
            BlockSubjects[block.Index] = subject;
            if (id is not null)
            {
                NamedSubjects[id] = subject;
            }
        }
    }
}
```

`RenderContent` stays, but its lambda must use the trimmed group: the shared `ExpressionPattern` already trims, so `match.Groups[1].Value` is the path.

- [ ] **Step 8: Update MarkdownFile**

In `src/HomeBlaze/HomeBlaze.Storage/Files/MarkdownFile.cs`:

Remove the `SubjectBlockRegex` member, `RebuildMarkdownContent` and the `System.Text.RegularExpressions` using if nothing else needs it. Add `using HomeBlaze.Storage.Pages;` and `using Namotion.Interceptor.Registry;`.

Add fields:

```csharp
private MarkdownPage? _page;
private IReadOnlyDictionary<int, IInterceptorSubject> _blockSubjects = new Dictionary<int, IInterceptorSubject>();
private IReadOnlyDictionary<string, IInterceptorSubject> _namedSubjects = new Dictionary<string, IInterceptorSubject>();
```

In `LoadFileAsync` replace the last line with:

```csharp
var result = await _parser.ParseAsync(Content, this, Children, cancellationToken);
_page = result.Page;
_blockSubjects = result.BlockSubjects;
_namedSubjects = result.NamedSubjects;
Children = result.Children;
```

Add the lookup and replace `WriteConfigurationAsync`:

```csharp
/// <summary>
/// Gets the subject that the block with this index created, or null when the block created none.
/// The index is <see cref="MarkdownBlock.Index"/> of the page as last parsed.
/// </summary>
public IInterceptorSubject? GetBlockSubject(int blockIndex) =>
    _blockSubjects.GetValueOrDefault(blockIndex);

public async Task<bool> WriteConfigurationAsync(
    IInterceptorSubject subject,
    CancellationToken cancellationToken)
{
    if (_page is null || Content is null || FindBlock(subject) is not (var blockIndex, var blockSubject))
    {
        return false;
    }

    Content = MarkdownBlockWriter.WriteConfiguration(
        Content, _page.AllBlocks[blockIndex], _serializer.Serialize(blockSubject));

    // The spans moved, the block order did not, so the block subjects stay valid.
    _page = MarkdownPageParser.Parse(Content);

    var bytes = Encoding.UTF8.GetBytes(Content);
    using var stream = new MemoryStream(bytes);
    await WriteAsync(stream, cancellationToken);
    return true;
}

private (int BlockIndex, IInterceptorSubject Subject)? FindBlock(IInterceptorSubject subject)
{
    // The edited subject can be nested in the configuration of a block subject, for example a grid cell.
    for (var current = subject; current is not null && !ReferenceEquals(current, this); current = GetSingleParent(current))
    {
        foreach (var (blockIndex, blockSubject) in _blockSubjects)
        {
            if (ReferenceEquals(blockSubject, current))
            {
                return (blockIndex, current);
            }
        }
    }

    return null;
}

private static IInterceptorSubject? GetSingleParent(IInterceptorSubject subject) =>
    subject.TryGetRegisteredSubject()?.Parents is { Length: 1 } parents ? parents[0].Property.Subject : null;
```

Update the XML comment on `Children` to say "blocks" instead of "embedded subjects".

- [ ] **Step 9: Add the error box style**

In `src/HomeBlaze/HomeBlaze.Storage.Blazor/Files/MarkdownFilePageComponent.razor`, add to the `<style>` block:

```css
.markdown-content .markdown-block-error { border: 1px solid var(--mud-palette-error); color: var(--mud-palette-error); border-radius: 4px; padding: 0.5rem 0.75rem; margin: 0.75rem 0; font-family: monospace; font-size: 0.85rem; }
```

- [ ] **Step 10: Build and fix what the removed members broke**

Run: `dotnet build src/HomeBlaze/HomeBlaze.Storage.Blazor`
Expected: errors only in `MarkdownFileEditComponent.razor` and `MarkdownDecorationParser.cs` if they used removed members. They do not use parser internals today, so the build should pass. If the existing `MarkdownFileTests` helper constructs the parser through DI it keeps working because `TypeProvider` is registered there.

- [ ] **Step 11: Run the tests**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests`
Expected: all pass, including the 21 existing `MarkdownFileTests`.

- [ ] **Step 12: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Storage/Pages/MarkdownParseResult.cs src/HomeBlaze/HomeBlaze.Storage/Internal/MarkdownContentParser.cs src/HomeBlaze/HomeBlaze.Storage/Files/MarkdownFile.cs src/HomeBlaze/HomeBlaze.Storage.Blazor/Files/MarkdownFilePageComponent.razor src/HomeBlaze/HomeBlaze.Storage.Tests/HomeBlaze.Storage.Tests.csproj src/HomeBlaze/HomeBlaze.Storage.Tests/Pages/MarkdownPageFixture.cs src/HomeBlaze/HomeBlaze.Storage.Tests/Pages/TestSubjects.cs src/HomeBlaze/HomeBlaze.Storage.Tests/Pages/MarkdownWidgetBlockTests.cs
git commit -m "feat: create page subjects from widget blocks instead of subject fences"
```

---

### Task 5: Editor (**UI**)

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.Storage.Blazor/Models/DecorationRegion.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Storage.Blazor/Models/DecorationRegionType.cs` (comment only)
- Rewrite: `src/HomeBlaze/HomeBlaze.Storage.Blazor/Models/MarkdownDecorationParser.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Storage.Blazor/Files/MarkdownFileEditComponent.razor`
- Test: `src/HomeBlaze/HomeBlaze.Storage.Blazor.Tests/MarkdownDecorationParserTests.cs`

**Interfaces:**
- Consumes: `MarkdownPageParser.Parse`, `MarkdownBlock`, `MarkdownBlockWriter`, `MarkdownBlockHead.IsValidId`, `MarkdownSegmentBuilder.ExpressionPattern`, `MarkdownFile.GetBlockSubject(int)`.
- Produces:
  - `DecorationRegion(int StartLine, int StartColumn, int EndLine, int EndColumn, DecorationRegionType Type, string Name, IInterceptorSubject? Subject = null, int? BlockIndex = null)`
  - `static List<DecorationRegion> MarkdownDecorationParser.Parse(string? content, Func<int, IInterceptorSubject?>? getBlockSubject = null)`

A block region covers the head line and, when present, the `json` fence. It does not cover the block content, so regions of nested blocks never overlap.

- [ ] **Step 1: Replace the fence tests**

In `src/HomeBlaze/HomeBlaze.Storage.Blazor.Tests/MarkdownDecorationParserTests.cs` delete every test whose content contains `subject(` and every test that passes a children dictionary. Keep the expression tests. Add:

````csharp
[Fact]
public void WhenContentHasWidgetBlock_ThenRegionCoversTheHeadLine()
{
    // Arrange
    const string content = "Line 1\n\n:::Demo.Motor#mymotor speed=3000\n:::\n";

    // Act
    var region = Assert.Single(MarkdownDecorationParser.Parse(content));

    // Assert
    Assert.Equal(DecorationRegionType.SubjectBlock, region.Type);
    Assert.Equal("mymotor", region.Name);
    Assert.Equal(0, region.BlockIndex);
    Assert.Equal((3, 1, 3, 33), (region.StartLine, region.StartColumn, region.EndLine, region.EndColumn));
}

[Fact]
public void WhenBlockHasNoId_ThenNameIsTheShortTypeName()
{
    // Act
    var region = Assert.Single(MarkdownDecorationParser.Parse(":::Demo.Samples.Motor\n:::\n"));

    // Assert
    Assert.Equal("Motor", region.Name);
}

[Fact]
public void WhenBlockHasJsonFence_ThenRegionEndsAfterTheFence()
{
    // Arrange
    const string content = ":::Demo.Grid rows=1\n```json\n{ \"cells\": [] }\n```\n:::\n";

    // Act
    var region = Assert.Single(MarkdownDecorationParser.Parse(content));

    // Assert
    Assert.Equal(1, region.StartLine);
    Assert.Equal(4, region.EndLine);
}

[Fact]
public void WhenBlocksAreNested_ThenRegionsDoNotOverlap()
{
    // Arrange
    const string content = "::::Demo.Card title=A\ntext\n\n:::Demo.Widget path=/x\n:::\n::::\n";

    // Act
    var regions = MarkdownDecorationParser.Parse(content);

    // Assert
    Assert.Equal(2, regions.Count);
    Assert.True(regions[0].ContainsPosition(1, 5));
    Assert.False(regions[0].ContainsPosition(4, 5));
    Assert.True(regions[1].ContainsPosition(4, 5));
}

[Fact]
public void WhenSlotBlockHasNoSubject_ThenItGetsNoRegion()
{
    // Arrange
    const string content = "::::Demo.Card\n:::Footer\ntext\n:::\n::::\n";

    // Act
    var regions = MarkdownDecorationParser.Parse(content, _ => null);

    // Assert
    Assert.Equal("Card", Assert.Single(regions).Name);
}

[Fact]
public void WhenBlockIsInvalid_ThenItGetsNoRegion()
{
    // Act
    var regions = MarkdownDecorationParser.Parse(":::\ntext\n:::\n");

    // Assert
    Assert.Empty(regions);
}
````

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Blazor.Tests --filter "FullyQualifiedName~MarkdownDecorationParserTests"`
Expected: build error, `BlockIndex` does not exist.

- [ ] **Step 3: Extend the region record**

In `DecorationRegion.cs` add the parameter and its doc line:

```csharp
/// <param name="BlockIndex">For subject blocks, the index of the block on the page.</param>
public record DecorationRegion(
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn,
    DecorationRegionType Type,
    string Name,
    IInterceptorSubject? Subject = null,
    int? BlockIndex = null)
```

In `DecorationRegionType.cs` change the comment of `SubjectBlock` to `A widget block such as :::Full.Type.Name key=value.`

- [ ] **Step 4: Rewrite the decoration parser**

Replace the body of `src/HomeBlaze/HomeBlaze.Storage.Blazor/Models/MarkdownDecorationParser.cs`. Keep `GetLineAndColumn` as it is.

```csharp
using System.Text.RegularExpressions;
using HomeBlaze.Storage.Pages;
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Blazor.Models;

/// <summary>
/// Finds the regions of a page that the editor decorates: widget blocks and expressions.
/// </summary>
public static partial class MarkdownDecorationParser
{
    [GeneratedRegex(MarkdownSegmentBuilder.ExpressionPattern)]
    private static partial Regex ExpressionRegex();

    /// <param name="content">The page text.</param>
    /// <param name="getBlockSubject">Returns the subject a block created, by block index.</param>
    public static List<DecorationRegion> Parse(
        string? content,
        Func<int, IInterceptorSubject?>? getBlockSubject = null)
    {
        var regions = new List<DecorationRegion>();
        if (string.IsNullOrEmpty(content))
        {
            return regions;
        }

        foreach (var block in MarkdownPageParser.Parse(content).AllBlocks)
        {
            if (block.Head is null || block.Kind is not (MarkdownBlockKind.Widget or MarkdownBlockKind.Slot))
            {
                continue;
            }

            var subject = getBlockSubject?.Invoke(block.Index);
            if (block.Kind == MarkdownBlockKind.Slot && subject is null)
            {
                continue;
            }

            var end = block.ConfigurationSpan?.End ?? block.HeadSpan.End;
            var (startLine, startColumn) = GetLineAndColumn(content, block.HeadSpan.Start);
            var (endLine, endColumn) = GetLineAndColumn(content, end);

            regions.Add(new DecorationRegion(
                startLine,
                startColumn,
                endLine,
                endColumn,
                DecorationRegionType.SubjectBlock,
                block.Head.Id ?? block.Head.Name[(block.Head.Name.LastIndexOf('.') + 1)..],
                subject,
                block.Index));
        }

        foreach (Match match in ExpressionRegex().Matches(content))
        {
            var (startLine, startColumn) = GetLineAndColumn(content, match.Index);
            var (endLine, endColumn) = GetLineAndColumn(content, match.Index + match.Length);

            regions.Add(new DecorationRegion(
                startLine, startColumn, endLine, endColumn, DecorationRegionType.Expression, match.Groups[1].Value));
        }

        return regions;
    }
}
```

- [ ] **Step 5: Run the unit tests to verify they pass**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Blazor.Tests`
Expected: all pass.

- [ ] **Step 6: Switch the edit component to blocks**

In `src/HomeBlaze/HomeBlaze.Storage.Blazor/Files/MarkdownFileEditComponent.razor`:

Add `@using HomeBlaze.Storage.Pages` and remove `@using System.Text.RegularExpressions`.

In `RefreshDecorationsAsync` change the parse call:

```csharp
Func<int, IInterceptorSubject?>? getBlockSubject = File is null ? null : File.GetBlockSubject;
_decorationRegions = MarkdownDecorationParser.Parse(_currentContent, getBlockSubject);
```

Replace the first half of `OnEditSubjectClick`, up to and including the `Regex.Replace` line, with:

```csharp
if (File == null || _editor == null || region.Subject is not { } subject || region.BlockIndex is not { } blockIndex)
    return;

var dialog = await SubjectEditDialog.ShowAsync(DialogService, ComponentRegistry, subject, $"Edit {region.Name}");
var result = await dialog.Result;

if (result?.Canceled != false)
    return;

// The editor content can differ from what the page last parsed. The block is only
// written when it is still the block the subject came from.
var blocks = MarkdownPageParser.Parse(_currentContent).AllBlocks;
if (blockIndex >= blocks.Count || blocks[blockIndex].Head is null)
    return;

_currentContent = MarkdownBlockWriter.WriteConfiguration(
    _currentContent, blocks[blockIndex], Serializer.Serialize(subject));
```

The rest of the method (set editor value, refresh, save, reload, apply configuration, dirty state) stays.

In `OnEditExpressionClick` the `LocalSubjects` dictionary still filters `HtmlSegment` and `RenderExpression`. Leave it.

In `OnCreateClick` replace the block creation and the insertion arithmetic:

```csharp
var typeName = result.Subject.GetType().FullName!;
var id = MarkdownBlockHead.IsValidId(result.Name) ? result.Name : null;
var blockLines = MarkdownBlockWriter.CreateBlock(typeName, id, json).Split('\n');

var position = await _editor.GetPosition();
var lines = _currentContent.Split('\n');
var insertLine = position?.LineNumber ?? lines.Length;
var newLines = new List<string>(lines);

// A block needs a blank line before and after it to be parsed as a block.
if (insertLine > 1 && !string.IsNullOrWhiteSpace(lines[insertLine - 2]))
{
    newLines.Insert(insertLine - 1, "");
    insertLine++;
}

newLines.InsertRange(insertLine - 1, blockLines);

var lineAfterBlock = insertLine - 1 + blockLines.Length;
if (lineAfterBlock < newLines.Count && !string.IsNullOrWhiteSpace(newLines[lineAfterBlock]))
{
    newLines.Insert(lineAfterBlock, "");
}

_currentContent = string.Join('\n', newLines);
```

Remove the now unused `subjectBlock` variable.

- [ ] **Step 7: Build**

Run: `dotnet build src/HomeBlaze/HomeBlaze.Storage.Blazor`
Expected: success.

- [ ] **Step 8: Commit**

The E2E run for this task happens at the end of Task 6, because the E2E pages still use the old fence.

```bash
git add src/HomeBlaze/HomeBlaze.Storage.Blazor/Models/DecorationRegion.cs src/HomeBlaze/HomeBlaze.Storage.Blazor/Models/DecorationRegionType.cs src/HomeBlaze/HomeBlaze.Storage.Blazor/Models/MarkdownDecorationParser.cs src/HomeBlaze/HomeBlaze.Storage.Blazor/Files/MarkdownFileEditComponent.razor src/HomeBlaze/HomeBlaze.Storage.Blazor.Tests/MarkdownDecorationParserTests.cs
git commit -m "feat: edit and insert widget blocks in the markdown editor"
```

---

### Task 6: Migrate the pages (**UI**)

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze/Data/Files/Demo/Inline.md`
- Modify: `src/HomeBlaze/HomeBlaze/Data/Files/Devices.md`
- Modify: `src/HomeBlaze/HomeBlaze.E2E.Tests/TestData/Demo/Inline.md`
- Modify: `src/HomeBlaze/HomeBlaze.E2E.Tests/TestData/Demo/Grid.md`
- Modify: `src/HomeBlaze/HomeBlaze.E2E.Tests/MarkdownEditorTests.cs`
- Test: `src/HomeBlaze/HomeBlaze.Storage.Tests/Pages/RepositoryPagesTests.cs`

**Interfaces:**
- Consumes: `MarkdownPageParser`, `MarkdownSegmentBuilder`.
- Produces: nothing new.

- [ ] **Step 1: Write a guard test over every page in the repository**

Create `src/HomeBlaze/HomeBlaze.Storage.Tests/Pages/RepositoryPagesTests.cs`:

```csharp
using HomeBlaze.Storage.Pages;

namespace HomeBlaze.Storage.Tests.Pages;

public class RepositoryPagesTests
{
    public static TheoryData<string> PagePaths()
    {
        var root = FindRepositoryRoot();
        var data = new TheoryData<string>();
        foreach (var folder in new[] { "src/HomeBlaze/HomeBlaze/Data/Files", "src/HomeBlaze/HomeBlaze.E2E.Tests/TestData" })
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, folder), "*.md", SearchOption.AllDirectories))
            {
                data.Add(Path.GetRelativePath(root, file));
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(PagePaths))]
    public void WhenRepositoryPageIsParsed_ThenItHasNoBlockErrors(string relativePath)
    {
        // Arrange
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), relativePath)).Replace("\r\n", "\n");
        var page = MarkdownPageParser.Parse(source);

        // Act
        var html = Flatten(MarkdownSegmentBuilder.Build(page));

        // Assert
        Assert.DoesNotContain("markdown-block-error", html);
    }

    private static string Flatten(IEnumerable<MarkdownSegment> segments) =>
        string.Concat(segments.Select(segment => segment switch
        {
            HtmlMarkdownSegment html => html.Html,
            BlockMarkdownSegment block => Flatten(block.Content),
            _ => string.Empty
        }));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "src", "Namotion.Interceptor.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
```

- [ ] **Step 2: Run it to see which pages fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests --filter "FullyQualifiedName~RepositoryPagesTests"`
Expected: failures for `Demo/Inline.md` (both copies), `Devices.md`, `Demo/Grid.md`, and for documentation pages whose old fence is not nested in another fence.

- [ ] **Step 3: Convert the demo page**

In `src/HomeBlaze/HomeBlaze/Data/Files/Demo/Inline.md` replace the two fences. The motor:

```markdown
:::HomeBlaze.Samples.Motor#mymotor name=MyMotor targetSpeed=3000 simulationInterval=00:00:01.500
:::
```

The widget reference. The path stays `../../Conveyor` until Task 10 changes how `Widget` resolves:

```markdown
:::HomeBlaze.Components.Widget#conveyorRef path=../../Conveyor
:::
```

- [ ] **Step 4: Convert the devices page**

In `src/HomeBlaze/HomeBlaze/Data/Files/Devices.md`:

```markdown
:::HomeBlaze.Components.Widget#widget1 path=/Devices/SampleDevice1
:::

:::HomeBlaze.Components.Widget#widget2 path=/Devices/SampleDevice2
:::
```

- [ ] **Step 5: Convert the E2E pages**

`src/HomeBlaze/HomeBlaze.E2E.Tests/TestData/Demo/Inline.md`: same motor block as in Step 3. This page uses `{{ mymotor.Temperature }}` with a dot, which never resolved. Change both expressions to `{{ mymotor/Temperature }}` and `{{ mymotor/CurrentSpeed }}`.

`src/HomeBlaze/HomeBlaze.E2E.Tests/TestData/Demo/Grid.md`:

````markdown
:::HomeBlaze.Components.GridLayout#grid rows=1 columns=4
```json
{ "cells": [] }
```
:::
````

- [ ] **Step 6: Convert old fences in the documentation pages that the guard test reports**

For each documentation page the guard test still fails on, convert the reported fence to the block form, or wrap the example in a four-tilde `~~~~markdown` fence when it is meant as an example of page source. Task 12 rewrites the prose. This step only makes the pages render without error boxes.

- [ ] **Step 7: Run the guard test to verify it passes**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests --filter "FullyQualifiedName~RepositoryPagesTests"`
Expected: all pass.

- [ ] **Step 8: Make the editor E2E tests place the cursor deliberately**

The tests click the middle of the editor today, which only worked while the fence happened to be there. In `src/HomeBlaze/HomeBlaze.E2E.Tests/MarkdownEditorTests.cs`, in `SubjectBlock_ShouldShowEditButton_WhenCursorInside`, replace the click on `.view-lines` with a click on the head line and assert the button text:

```csharp
// Click on the head line of the motor block to put the cursor inside it
var headLine = page.Locator(".monaco-editor .view-line", new() { HasTextString = "HomeBlaze.Samples.Motor" });
await Assertions.Expect(headLine).ToBeVisibleAsync(new() { Timeout = 30000 });
await headLine.ClickAsync();

// Assert: the edit button names the block
var editButton = page.GetByRole(AriaRole.Button, new() { Name = "Edit mymotor" });
await Assertions.Expect(editButton).ToBeVisibleAsync(new() { Timeout = 10000 });
```

Update the comments in this file that mention `subject(mymotor)`. Rename the test to `WhenCursorIsOnBlockHead_ThenEditButtonNamesTheBlock` and give the other tests in this file the same `When..._Then...` form while you are there.

- [ ] **Step 9: Run the E2E tests**

Run: `dotnet test src/HomeBlaze/HomeBlaze.E2E.Tests`
Expected: all pass. If the grid tests fail on saving, check that `MarkdownFile.WriteConfigurationAsync` found the grid block: the grid passes itself as the subject.

- [ ] **Step 10: Interactive check with the Playwright MCP**

Follow "Verification with Playwright". Pages: `/pages/Demo/Inline.md` and `/pages/Devices.md`.

Must be visible: the motor widget with a live speed that changes, the two expression values with numbers, the conveyor widget, both device widgets. Then open split mode on `Inline.md`, click the motor head line, press "Edit mymotor", change the target speed, confirm, and check in the editor that only the head line changed. Revert the change afterwards with `git checkout -- src/HomeBlaze/HomeBlaze/Data/Files/Demo/Inline.md`.

- [ ] **Step 11: Commit**

```bash
git add src/HomeBlaze/HomeBlaze/Data/Files/Demo/Inline.md src/HomeBlaze/HomeBlaze/Data/Files/Devices.md src/HomeBlaze/HomeBlaze.E2E.Tests/TestData/Demo/Inline.md src/HomeBlaze/HomeBlaze.E2E.Tests/TestData/Demo/Grid.md src/HomeBlaze/HomeBlaze.E2E.Tests/MarkdownEditorTests.cs src/HomeBlaze/HomeBlaze.Storage.Tests/Pages/RepositoryPagesTests.cs
git add src/HomeBlaze/HomeBlaze/Data/Files/Docs
git commit -m "feat: migrate pages from subject fences to widget blocks"
```

---

### Task 7: Slot attribute and slot map

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Abstractions/Attributes/SlotAttribute.cs`
- Create: `src/HomeBlaze/HomeBlaze.Storage/Pages/SlotMap.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Storage.Tests/Pages/TestSubjects.cs`
- Test: `src/HomeBlaze/HomeBlaze.Storage.Tests/Pages/SlotMapTests.cs`

**Interfaces:**
- Produces:
  - `public sealed class SlotAttribute : Attribute` in `HomeBlaze.Abstractions.Attributes`, with constructors `SlotAttribute()` and `SlotAttribute(string name)`, `string? Name { get; }` and `bool IsDefault { get; set; }`
  - `internal sealed record SlotDefinition(string Name, PropertyInfo Property, Type ValueType, bool IsList, bool IsDefault)` with `bool IsOpen`
  - `internal sealed class SlotMap` with `SlotDefinition? Default`, `IReadOnlyDictionary<string, SlotDefinition> Named` (contains the default slot too), `string? Error`, `static SlotMap For(Type type)`

- [ ] **Step 1: Add the attribute**

Create `src/HomeBlaze/HomeBlaze.Abstractions/Attributes/SlotAttribute.cs`:

```csharp
namespace HomeBlaze.Abstractions.Attributes;

/// <summary>
/// Marks a property that a markdown page fills with block content.
/// The property is not configuration and is never serialized.
/// </summary>
/// <remarks>
/// The property type decides what a block produces: <c>IInterceptorSubject</c> takes the content
/// as a fragment, a concrete subject type gets an instance configured from the block's arguments,
/// and an array takes one value per block.
/// </remarks>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SlotAttribute : Attribute
{
    public SlotAttribute()
    {
    }

    /// <param name="name">What one block for this slot is called on a page. Contains no dot.</param>
    public SlotAttribute(string name)
    {
        Name = name;
    }

    /// <summary>
    /// The block name. Defaults to the property name.
    /// </summary>
    public string? Name { get; }

    /// <summary>
    /// Whether content that is not inside a slot block goes to this slot.
    /// At most one per type, and its type must be <c>IInterceptorSubject</c>.
    /// </summary>
    public bool IsDefault { get; set; }
}
```

- [ ] **Step 2: Add test subjects with slots**

Append to `src/HomeBlaze/HomeBlaze.Storage.Tests/Pages/TestSubjects.cs` (add `using Namotion.Interceptor;`):

```csharp
[InterceptorSubject]
public partial class TestPanel : IConfigurable
{
    public TestPanel()
    {
        Rows = [];
        Items = [];
    }

    [Configuration]
    public partial string? Title { get; set; }

    [Slot(IsDefault = true)]
    public partial IInterceptorSubject? Content { get; set; }

    [Slot]
    public partial IInterceptorSubject? Footer { get; set; }

    [Slot("Row")]
    public partial TestRow[] Rows { get; set; }

    [Slot("Item")]
    public partial IInterceptorSubject[] Items { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

[InterceptorSubject]
public partial class TestRow : IConfigurable
{
    [Configuration]
    public partial string? Label { get; set; }

    [Slot(IsDefault = true)]
    public partial IInterceptorSubject? Content { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public class TwoDefaults
{
    [Slot(IsDefault = true)]
    public IInterceptorSubject? First { get; set; }

    [Slot(IsDefault = true)]
    public IInterceptorSubject? Second { get; set; }
}

public class TypedDefault
{
    [Slot(IsDefault = true)]
    public TestRow? Content { get; set; }
}

public class DottedName
{
    [Slot("A.B")]
    public IInterceptorSubject? Content { get; set; }
}

public class DuplicateNames
{
    [Slot("Item")]
    public IInterceptorSubject? First { get; set; }

    [Slot("Item")]
    public IInterceptorSubject? Second { get; set; }
}
```

- [ ] **Step 3: Write the failing tests**

Create `src/HomeBlaze/HomeBlaze.Storage.Tests/Pages/SlotMapTests.cs`:

```csharp
using HomeBlaze.Storage.Pages;
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Tests.Pages;

public class SlotMapTests
{
    [Fact]
    public void WhenTypeDeclaresSlots_ThenMapDescribesEachOne()
    {
        // Act
        var map = SlotMap.For(typeof(TestPanel));

        // Assert
        Assert.Null(map.Error);
        Assert.Equal("Content", map.Default!.Name);
        Assert.Equal(["Content", "Footer", "Item", "Row"], map.Named.Keys.Order());

        var row = map.Named["Row"];
        Assert.True(row.IsList);
        Assert.False(row.IsOpen);
        Assert.Equal(typeof(TestRow), row.ValueType);

        var item = map.Named["Item"];
        Assert.True(item.IsList);
        Assert.True(item.IsOpen);
        Assert.Equal(typeof(IInterceptorSubject), item.ValueType);

        Assert.False(map.Named["Footer"].IsList);
    }

    [Fact]
    public void WhenTypeHasNoSlots_ThenMapIsEmpty()
    {
        // Act
        var map = SlotMap.For(typeof(TestLeaf));

        // Assert
        Assert.Null(map.Error);
        Assert.Null(map.Default);
        Assert.Empty(map.Named);
    }

    [Theory]
    [InlineData(typeof(TwoDefaults), "more than one default slot")]
    [InlineData(typeof(TypedDefault), "default slot 'Content' must be of type IInterceptorSubject")]
    [InlineData(typeof(DottedName), "'A.B' must not contain a dot")]
    [InlineData(typeof(DuplicateNames), "'Item' is declared twice")]
    public void WhenDeclarationIsInvalid_ThenMapCarriesAnError(Type type, string expectedPart)
    {
        // Act
        var map = SlotMap.For(type);

        // Assert
        Assert.Contains(expectedPart, map.Error);
    }
}
```

- [ ] **Step 4: Run the tests to verify they fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests --filter "FullyQualifiedName~SlotMapTests"`
Expected: build error, `SlotMap` does not exist.

- [ ] **Step 5: Implement the slot map**

Create `src/HomeBlaze/HomeBlaze.Storage/Pages/SlotMap.cs`:

```csharp
using System.Collections.Concurrent;
using System.Reflection;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Pages;

internal sealed record SlotDefinition(string Name, PropertyInfo Property, Type ValueType, bool IsList, bool IsDefault)
{
    /// <summary>An open slot takes block content as a fragment, any other slot takes an instance of its type.</summary>
    public bool IsOpen => ValueType == typeof(IInterceptorSubject);
}

/// <summary>The slots a type declares with <see cref="SlotAttribute"/>.</summary>
internal sealed class SlotMap
{
    private static readonly ConcurrentDictionary<Type, SlotMap> Cache = new();

    private SlotMap(SlotDefinition? defaultSlot, IReadOnlyDictionary<string, SlotDefinition> named, string? error)
    {
        Default = defaultSlot;
        Named = named;
        Error = error;
    }

    public SlotDefinition? Default { get; }

    /// <summary>All slots by block name, including the default slot.</summary>
    public IReadOnlyDictionary<string, SlotDefinition> Named { get; }

    /// <summary>Set when the type declares its slots in a way that cannot be used.</summary>
    public string? Error { get; }

    public static SlotMap For(Type type) => Cache.GetOrAdd(type, Create);

    private static SlotMap Create(Type type)
    {
        var named = new Dictionary<string, SlotDefinition>();
        SlotDefinition? defaultSlot = null;

        SlotMap Invalid(string error) => new(null, new Dictionary<string, SlotDefinition>(), $"{type.Name}: {error}");

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var attribute = property.GetCustomAttribute<SlotAttribute>(true);
            if (attribute is null)
            {
                continue;
            }

            var name = attribute.Name ?? property.Name;
            if (name.Contains('.'))
            {
                return Invalid($"the slot name '{name}' must not contain a dot.");
            }

            var isList = property.PropertyType.IsArray;
            var valueType = isList ? property.PropertyType.GetElementType()! : property.PropertyType;
            var slot = new SlotDefinition(name, property, valueType, isList, attribute.IsDefault);

            if (!typeof(IInterceptorSubject).IsAssignableFrom(valueType))
            {
                return Invalid($"the slot '{name}' must hold subjects.");
            }

            if (!named.TryAdd(name, slot))
            {
                return Invalid($"the slot '{name}' is declared twice.");
            }

            if (attribute.IsDefault)
            {
                if (defaultSlot is not null)
                {
                    return Invalid("there is more than one default slot.");
                }

                if (isList || !slot.IsOpen)
                {
                    return Invalid($"the default slot '{name}' must be of type IInterceptorSubject.");
                }

                defaultSlot = slot;
            }
        }

        return new SlotMap(defaultSlot, named, null);
    }
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests --filter "FullyQualifiedName~SlotMapTests"`
Expected: all pass. If the generated partial properties of `TestPanel` do not carry the attribute through reflection, read it from the registry attribute path the serializer uses and keep the tests.

- [ ] **Step 7: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Abstractions/Attributes/SlotAttribute.cs src/HomeBlaze/HomeBlaze.Storage/Pages/SlotMap.cs src/HomeBlaze/HomeBlaze.Storage.Tests/Pages/TestSubjects.cs src/HomeBlaze/HomeBlaze.Storage.Tests/Pages/SlotMapTests.cs
git commit -m "feat: declare content slots on subjects with a slot attribute"
```

---

### Task 8: Fragments and slot filling

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Storage/Pages/MarkdownFragment.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Storage/Internal/MarkdownContentParser.cs`
- Test: `src/HomeBlaze/HomeBlaze.Storage.Tests/Pages/MarkdownSlotTests.cs`

**Interfaces:**
- Consumes: `SlotMap`, `SlotDefinition` from Task 7. `BuildChildrenAsync`, `TryBuildConfiguration`, `CreateOrUpdateAsync`, `ParseState` from Task 4.
- Produces:
  - `public partial class MarkdownFragment` in `HomeBlaze.Storage.Pages` with `[InlinePaths] IDictionary<string, IInterceptorSubject> Children { get; internal set; }`
  - The final `ApplyContentAsync`. Its rules are the five slot rules of the spec.

- [ ] **Step 1: Write the failing tests**

Create `src/HomeBlaze/HomeBlaze.Storage.Tests/Pages/MarkdownSlotTests.cs`:

````csharp
using HomeBlaze.Storage.Internal;
using HomeBlaze.Storage.Pages;

namespace HomeBlaze.Storage.Tests.Pages;

public class MarkdownSlotTests
{
    private const string Namespace = "HomeBlaze.Storage.Tests.Pages";

    private static string Html(MarkdownFragment fragment) => MarkdownPageFixture.Html(fragment.Children);

    [Fact]
    public async Task WhenWidgetHasContent_ThenDefaultSlotHoldsItAsFragment()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();

        // Act
        var page = await fixture.AddPageAsync("page.md", $$$"""
            :::{{{Namespace}}}.TestPanel#panel title=A
            Body **bold** {{ /x/y }}
            :::
            """);

        // Assert
        var panel = Assert.IsType<TestPanel>(page.Children["panel"]);
        var content = Assert.IsType<MarkdownFragment>(panel.Content);
        Assert.Contains("<strong>bold</strong>", Html(content));
        var expression = Assert.Single(content.Children.Values.OfType<RenderExpression>());
        Assert.Equal("/x/y", expression.Path);
        Assert.Same(page, expression.Parent);
        Assert.Null(panel.Footer);
    }

    [Fact]
    public async Task WhenWidgetHasNoContent_ThenDefaultSlotIsNull()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();

        // Act
        var page = await fixture.AddPageAsync("page.md", $$"""
            :::{{Namespace}}.TestPanel#panel
            :::
            """);

        // Assert
        Assert.Null(((TestPanel)page.Children["panel"]).Content);
    }

    [Fact]
    public async Task WhenNamedSlotBlockIsGiven_ThenItFillsThatPropertyAndTheRestIsDefault()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();

        // Act
        var page = await fixture.AddPageAsync("page.md", $$"""
            ::::{{Namespace}}.TestPanel#panel
            one

            :::Footer
            foot
            :::

            two
            ::::
            """);

        // Assert
        var panel = (TestPanel)page.Children["panel"];
        var content = Html(Assert.IsType<MarkdownFragment>(panel.Content));
        Assert.Contains("one", content);
        Assert.Contains("two", content);
        Assert.DoesNotContain("foot", content);
        Assert.Contains("foot", Html(Assert.IsType<MarkdownFragment>(panel.Footer)));
    }

    [Fact]
    public async Task WhenWidgetIsNestedInContent_ThenItIsAChildOfTheFragment()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();

        // Act
        var page = await fixture.AddPageAsync("page.md", $$"""
            ::::{{Namespace}}.TestPanel#panel
            text

            :::{{Namespace}}.TestLeaf#leaf count=3
            :::
            ::::
            """);

        // Assert
        var content = (MarkdownFragment)((TestPanel)page.Children["panel"]).Content!;
        var leaf = Assert.IsType<TestLeaf>(content.Children["leaf"]);
        Assert.Equal(3, leaf.Count);
        Assert.Same(leaf, page.GetBlockSubject(1));
    }

    [Fact]
    public async Task WhenListSlotOfConcreteTypeIsFilled_ThenEachBlockCreatesOneConfiguredValue()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();

        // Act
        var page = await fixture.AddPageAsync("page.md", $$"""
            ::::{{Namespace}}.TestPanel#panel
            :::Row label="First row"
            row one
            :::
            :::Row label=Second
            :::
            ::::
            """);

        // Assert
        var panel = (TestPanel)page.Children["panel"];
        Assert.Equal(["First row", "Second"], panel.Rows.Select(row => row.Label));
        Assert.Contains("row one", Html(Assert.IsType<MarkdownFragment>(panel.Rows[0].Content)));
        Assert.Null(panel.Rows[1].Content);
        Assert.Same(panel.Rows[0], page.GetBlockSubject(1));
    }

    [Fact]
    public async Task WhenListSlotOfOpenTypeIsFilled_ThenEachBlockIsOneFragment()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();

        // Act
        var page = await fixture.AddPageAsync("page.md", $$"""
            :::::{{Namespace}}.TestPanel#panel
            ::::Item
            :::{{Namespace}}.TestLeaf#a
            :::
            :::{{Namespace}}.TestLeaf#b
            :::
            ::::
            ::::Item
            plain
            ::::
            :::::
            """);

        // Assert
        var panel = (TestPanel)page.Children["panel"];
        Assert.Equal(2, panel.Items.Length);
        var first = Assert.IsType<MarkdownFragment>(panel.Items[0]);
        Assert.Equal(2, first.Children.Values.OfType<TestLeaf>().Count());
        Assert.Contains("plain", Html(Assert.IsType<MarkdownFragment>(panel.Items[1])));
    }

    [Fact]
    public async Task WhenPageIsParsedAgain_ThenSlotValuesAreReusedRecursively()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var source = $$"""
            :::::{{Namespace}}.TestPanel#panel
            ::::Row label=A
            :::{{Namespace}}.TestLeaf#leaf count=1
            :::
            ::::
            :::::
            """;
        var page = await fixture.AddPageAsync("page.md", source);
        var panel = (TestPanel)page.Children["panel"];
        var row = panel.Rows[0];
        var fragment = row.Content;
        var leaf = ((MarkdownFragment)fragment!).Children["leaf"];

        // Act
        await fixture.UpdatePageAsync(page, source.Replace("label=A", "label=B").Replace("count=1", "count=2"));

        // Assert
        Assert.Same(panel, page.Children["panel"]);
        Assert.Same(row, panel.Rows[0]);
        Assert.Equal("B", panel.Rows[0].Label);
        Assert.Same(fragment, panel.Rows[0].Content);
        Assert.Same(leaf, ((MarkdownFragment)panel.Rows[0].Content!).Children["leaf"]);
        Assert.Equal(2, ((TestLeaf)leaf).Count);
    }

    [Fact]
    public async Task WhenSlotBlockIsRemoved_ThenThePropertyIsCleared()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync("page.md", $$"""
            ::::{{Namespace}}.TestPanel#panel
            text

            :::Footer
            foot
            :::
            :::Row label=A
            :::
            ::::
            """);

        // Act
        await fixture.UpdatePageAsync(page, $$"""
            :::{{Namespace}}.TestPanel#panel
            :::
            """);

        // Assert
        var panel = (TestPanel)page.Children["panel"];
        Assert.Null(panel.Content);
        Assert.Null(panel.Footer);
        Assert.Empty(panel.Rows);
    }

    [Theory]
    [InlineData("::::{0}.TestPanel\n:::Footr\nx\n:::\n::::", "TestPanel has no slot 'Footr'. Available: Content, Footer, Item, Row")]
    [InlineData("::::{0}.TestPanel\n:::Footer\na\n:::\n:::Footer\nb\n:::\n::::", "The slot 'Footer' takes one block, but there are 2")]
    [InlineData("::::{0}.TestPanel\n:::Footer title=x\na\n:::\n::::", "The slot 'Footer' holds content only")]
    [InlineData("::::{0}.TestPanel\nloose\n\n:::Content\nexplicit\n:::\n::::", "The slot 'Content' is filled by a block and by loose content")]
    [InlineData("::::{0}.TestPanel\n:::Row labl=x\n:::\n::::", "TestRow has no configuration property 'labl'")]
    [InlineData(":::::{0}.TestPanel\n::::Item\n:::Footer\nx\n:::\n::::\n:::::", "'Footer' is not a slot at this position")]
    [InlineData(":::{0}.TestLeaf\ncontent\n:::", "TestLeaf has no default slot, so it cannot have content")]
    public async Task WhenSlotUsageIsWrong_ThenAnErrorBoxIsShown(string template, string expectedMessage)
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();

        // Act
        var page = await fixture.AddPageAsync("page.md", string.Format(template, Namespace));

        // Assert
        var html = Flatten(page.Children.Values);
        Assert.Contains(System.Net.WebUtility.HtmlEncode(expectedMessage), html);
    }

    [Fact]
    public async Task WhenConcreteSlotValueIsWrittenBack_ThenOnlyItsHeadLineChanges()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var content = $"::::{Namespace}.TestPanel#panel title=T\n:::Row label=A\nrow text\n:::\n::::\n";
        var page = await fixture.AddPageAsync("page.md", content);
        var row = ((TestPanel)page.Children["panel"]).Rows[0];
        row.Label = "New Label";

        // Act
        var handled = await page.WriteConfigurationAsync(row, CancellationToken.None);

        // Assert
        Assert.True(handled);
        Assert.Equal(content.Replace(":::Row label=A", ":::Row label=\"New Label\""), await fixture.ReadAsync("page.md"));
    }

    private static string Flatten(IEnumerable<Namotion.Interceptor.IInterceptorSubject?> subjects) =>
        string.Concat(subjects.Select(subject => subject switch
        {
            HtmlSegment html => html.Html,
            MarkdownFragment fragment => Flatten(fragment.Children.Values),
            TestPanel panel => Flatten([panel.Content, panel.Footer, .. panel.Items, .. panel.Rows]),
            TestRow row => Flatten([row.Content]),
            _ => string.Empty
        }));
}
````

Update the `"cannot have content"` case in `MarkdownWidgetBlockTests.WhenBlockIsWrong_ThenAnErrorBoxReplacesIt` to `"has no default slot, so it cannot have content"`.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests --filter "FullyQualifiedName~MarkdownSlotTests"`
Expected: build error, `MarkdownFragment` does not exist.

- [ ] **Step 3: Add the fragment subject**

Create `src/HomeBlaze/HomeBlaze.Storage/Pages/MarkdownFragment.cs`:

```csharp
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace HomeBlaze.Storage.Pages;

/// <summary>
/// The content of one open slot: HTML, expressions and widgets in page order.
/// </summary>
[InterceptorSubject]
public partial class MarkdownFragment
{
    public MarkdownFragment()
    {
        Children = new Dictionary<string, IInterceptorSubject>();
    }

    /// <summary>
    /// The segments of the content, keyed like the children of a page.
    /// </summary>
    [InlinePaths]
    public partial IDictionary<string, IInterceptorSubject> Children { get; internal set; }
}
```

- [ ] **Step 4: Replace ApplyContentAsync**

In `MarkdownContentParser.cs` replace the stub `ApplyContentAsync` with the code below and add the helpers.

```csharp
private async Task<string?> ApplyContentAsync(
    IInterceptorSubject subject,
    Type type,
    IReadOnlyList<MarkdownSegment> content,
    ParseState state,
    CancellationToken cancellationToken)
{
    var slots = SlotMap.For(type);
    if (slots.Error is not null)
    {
        return slots.Error;
    }

    var defaultContent = new List<MarkdownSegment>();
    var slotBlocks = new List<BlockMarkdownSegment>();
    foreach (var segment in content)
    {
        if (segment is BlockMarkdownSegment { Block.Kind: MarkdownBlockKind.Slot } slotBlock)
        {
            slotBlocks.Add(slotBlock);
        }
        else
        {
            defaultContent.Add(segment);
        }
    }

    var filled = new HashSet<SlotDefinition>();
    foreach (var group in slotBlocks.GroupBy(block => block.Block.Head!.Name))
    {
        if (!slots.Named.TryGetValue(group.Key, out var slot))
        {
            return slots.Named.Count == 0
                ? $"{type.Name} has no slots, so '{group.Key}' cannot be used inside it."
                : $"{type.Name} has no slot '{group.Key}'. Available: {string.Join(", ", slots.Named.Keys.Order())}.";
        }

        var blocks = group.ToList();
        if (!slot.IsList && blocks.Count > 1)
        {
            return $"The slot '{slot.Name}' takes one block, but there are {blocks.Count}.";
        }

        var existingValues = GetSlotValues(subject, slot);
        var values = new List<IInterceptorSubject>(blocks.Count);
        for (var index = 0; index < blocks.Count; index++)
        {
            var existing = index < existingValues.Count ? existingValues[index] : null;
            var (value, error) = await CreateSlotValueAsync(slot, blocks[index], existing, state, cancellationToken);
            if (value is null)
            {
                return error;
            }

            values.Add(value);
        }

        SetSlotValues(subject, slot, values);
        filled.Add(slot);
    }

    var hasDefaultContent = defaultContent.Any(
        segment => segment is not HtmlMarkdownSegment html || !string.IsNullOrWhiteSpace(html.Html));

    if (hasDefaultContent)
    {
        if (slots.Default is null)
        {
            return $"{type.Name} has no default slot, so it cannot have content.";
        }

        if (filled.Contains(slots.Default))
        {
            return $"The slot '{slots.Default.Name}' is filled by a block and by loose content.";
        }

        var fragment = slots.Default.Property.GetValue(subject) as MarkdownFragment ?? new MarkdownFragment();
        fragment.Children = await BuildChildrenAsync(defaultContent, fragment.Children, state, cancellationToken);
        slots.Default.Property.SetValue(subject, fragment);
        filled.Add(slots.Default);
    }

    foreach (var slot in slots.Named.Values)
    {
        if (!filled.Contains(slot))
        {
            SetSlotValues(subject, slot, []);
        }
    }

    return null;
}

private async Task<(IInterceptorSubject? Value, string? Error)> CreateSlotValueAsync(
    SlotDefinition slot,
    BlockMarkdownSegment segment,
    IInterceptorSubject? existing,
    ParseState state,
    CancellationToken cancellationToken)
{
    var block = segment.Block;
    var head = block.Head!;

    if (slot.IsOpen)
    {
        if (head.Arguments.Count > 0 || head.Id is not null || block.ConfigurationJson is not null)
        {
            return (null, $"The slot '{slot.Name}' holds content only and takes no arguments, name or json block.");
        }

        var fragment = existing as MarkdownFragment ?? new MarkdownFragment();
        fragment.Children = await BuildChildrenAsync(segment.Content, fragment.Children, state, cancellationToken);
        return (fragment, null);
    }

    if (head.Id is not null && state.NamedSubjects.ContainsKey(head.Id))
    {
        return (null, $"The name '{head.Id}' is used twice on this page.");
    }

    if (!TryBuildConfiguration(slot.ValueType, block, out var json, out var configurationError))
    {
        return (null, configurationError);
    }

    var value = await CreateOrUpdateAsync(slot.ValueType, existing, json, cancellationToken);
    if (value is null)
    {
        return (null, $"The type '{slot.ValueType.FullName}' of the slot '{slot.Name}' could not be created. It must be a registered configurable subject type.");
    }

    var contentError = await ApplyContentAsync(value, slot.ValueType, segment.Content, state, cancellationToken);
    if (contentError is not null)
    {
        return (null, contentError);
    }

    state.Register(block, head.Id, value);
    return (value, null);
}

private static IReadOnlyList<IInterceptorSubject?> GetSlotValues(IInterceptorSubject subject, SlotDefinition slot)
{
    var value = slot.Property.GetValue(subject);
    if (slot.IsList)
    {
        return value is Array array ? array.Cast<IInterceptorSubject?>().ToList() : [];
    }

    return [value as IInterceptorSubject];
}

private static void SetSlotValues(IInterceptorSubject subject, SlotDefinition slot, IReadOnlyList<IInterceptorSubject> values)
{
    if (slot.IsList)
    {
        var array = Array.CreateInstance(slot.ValueType, values.Count);
        for (var index = 0; index < values.Count; index++)
        {
            array.SetValue(values[index], index);
        }

        slot.Property.SetValue(subject, array);
    }
    else
    {
        slot.Property.SetValue(subject, values.Count > 0 ? values[0] : null);
    }
}
```

Slot blocks that reach `BuildChildrenAsync` are the ones no owner claimed, and `CreateWidgetAsync` already turns those into the "not a slot at this position" error.

A widget that reports an error has already had some of its slots assigned. That is harmless because the widget is replaced by the error box and dropped.

- [ ] **Step 5: Run the tests**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests`
Expected: all pass.

Two things to check if they do not. If setting an array slot does not attach the subjects in it, compare with how `GridLayout.Cells` (a `List<GridCell>`) behaves and change the slot property type to a list if arrays are not tracked as subject collections. If `TryBuildConfiguration` reports `TestRow` as unknown, the test assembly is missing from the `TypeProvider` in the fixture.

- [ ] **Step 6: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Storage/Pages/MarkdownFragment.cs src/HomeBlaze/HomeBlaze.Storage/Internal/MarkdownContentParser.cs src/HomeBlaze/HomeBlaze.Storage.Tests/Pages/MarkdownSlotTests.cs src/HomeBlaze/HomeBlaze.Storage.Tests/Pages/MarkdownWidgetBlockTests.cs
git commit -m "feat: fill widget slots from block content on markdown pages"
```

---

### Task 9: Rendering and the Card widget (**UI**)

Runs after Task 10.

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Storage.Blazor/Files/MarkdownContentView.razor`
- Create: `src/HomeBlaze/HomeBlaze.Storage.Blazor/Files/MarkdownFragmentWidget.razor`
- Modify: `src/HomeBlaze/HomeBlaze.Storage.Blazor/Files/MarkdownFilePageComponent.razor`
- Create: `src/HomeBlaze/HomeBlaze.Components/MudBlazorIcons.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Host/Display/SubjectIconExtensions.cs`
- Create: `src/HomeBlaze/HomeBlaze.Components/Card.cs`
- Create: `src/HomeBlaze/HomeBlaze.Components/Components/CardWidget.razor`
- Create: `src/HomeBlaze/HomeBlaze.E2E.Tests/TestData/Demo/Blocks.md`
- Test: `src/HomeBlaze/HomeBlaze.E2E.Tests/MarkdownBlockTests.cs`

**Interfaces:**
- Consumes: `MarkdownFragment.Children`, `HtmlSegment`, `RenderExpression`, `SubjectComponent`, `SlotAttribute`.
- Produces:
  - `MarkdownContentView` with `[Parameter] IDictionary<string, IInterceptorSubject>? Children`
  - `public static string MudBlazorIcons.Resolve(string? iconName)` in `HomeBlaze.Components`
  - `HomeBlaze.Components.Card` with `Title`, `Icon` (configuration), `Content` (default slot), `Footer` (slot)

- [ ] **Step 1: Add the E2E page**

Create `src/HomeBlaze/HomeBlaze.E2E.Tests/TestData/Demo/Blocks.md`:

```markdown
---
title: Blocks Demo
---

# Blocks Demo

::::HomeBlaze.Components.Card title="Motor Card" icon=Settings
Card body with a live value: speed is {{ cardmotor/CurrentSpeed }} RPM.

:::HomeBlaze.Samples.Motor#cardmotor name=CardMotor targetSpeed=1200 simulationInterval=00:00:01
:::

:::Footer
Card footer text
:::
::::
```

The fence lengths matter: the card is the only four-colon block.

- [ ] **Step 2: Write the failing E2E tests**

Create `src/HomeBlaze/HomeBlaze.E2E.Tests/MarkdownBlockTests.cs`:

```csharp
using System.Text.RegularExpressions;
using HomeBlaze.E2E.Tests.Infrastructure;
using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for widget blocks and slots on markdown pages.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class MarkdownBlockTests
{
    private readonly PlaywrightFixture _fixture;

    public MarkdownBlockTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<IPage> OpenBlocksPageAsync()
    {
        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync($"{_fixture.ServerAddress}pages/Demo/Blocks.md");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        return page;
    }

    [Fact]
    public async Task WhenPageHasCard_ThenTitleContentAndFooterAreInsideIt()
    {
        // Arrange & Act
        var page = await OpenBlocksPageAsync();

        // Assert
        var card = page.GetByTestId("card").First;
        await Assertions.Expect(card).ToBeVisibleAsync(new() { Timeout = 30000 });
        await Assertions.Expect(card).ToContainTextAsync("Motor Card");
        await Assertions.Expect(card).ToContainTextAsync("Card body with a live value");
        await Assertions.Expect(card).ToContainTextAsync("Card footer text");
    }

    [Fact]
    public async Task WhenCardContainsWidgetAndExpression_ThenBothRenderLiveInsideTheCard()
    {
        // Arrange & Act
        var page = await OpenBlocksPageAsync();

        // Assert
        var card = page.GetByTestId("card").First;
        await Assertions.Expect(card).ToContainTextAsync("CardMotor", new() { Timeout = 30000 });
        await Assertions.Expect(card).ToContainTextAsync(new Regex(@"speed is [1-9]\d* RPM"), new() { Timeout = 30000 });
    }

    [Fact]
    public async Task WhenPageRenders_ThenItShowsNoBlockErrors()
    {
        // Arrange & Act
        var page = await OpenBlocksPageAsync();

        // Assert
        await Assertions.Expect(page.GetByTestId("card").First).ToBeVisibleAsync(new() { Timeout = 30000 });
        await Assertions.Expect(page.Locator(".markdown-block-error")).ToHaveCountAsync(0);
    }
}
```

`GetByTestId` uses the `data-testid` attribute, which is what the existing components use.

- [ ] **Step 3: Run the E2E tests to verify they fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.E2E.Tests --filter "FullyQualifiedName~MarkdownBlockTests"`
Expected: fail. The page shows an error box because `HomeBlaze.Components.Card` is unknown.

- [ ] **Step 4: Move the icon lookup to the components project**

Create `src/HomeBlaze/HomeBlaze.Components/MudBlazorIcons.cs`:

```csharp
using System.Collections.Concurrent;
using System.Reflection;
using MudBlazor;

namespace HomeBlaze.Components;

/// <summary>
/// Resolves icon names such as "Thermostat" to MudBlazor icons.
/// </summary>
public static class MudBlazorIcons
{
    private static readonly ConcurrentDictionary<string, string> IconCache = new();

    /// <summary>
    /// Returns the filled Material icon with this name, or the default icon when the name is empty or unknown.
    /// </summary>
    public static string Resolve(string? iconName)
    {
        if (string.IsNullOrEmpty(iconName))
            return Icons.Material.Filled.Article;

        return IconCache.GetOrAdd(iconName, name =>
        {
            var field = typeof(Icons.Material.Filled).GetField(name,
                BindingFlags.Public | BindingFlags.Static);

            return field?.GetValue(null) as string ?? Icons.Material.Filled.Article;
        });
    }
}
```

This is the body of `SubjectIconExtensions.ResolveMudBlazorIcon`, moved.

Then make the host method delegate, so its eight call sites stay as they are:

```csharp
public static string ResolveMudBlazorIcon(string iconName) => MudBlazorIcons.Resolve(iconName);
```

Remove the now unused `IconCache` field and usings from `SubjectIconExtensions.cs`. `HomeBlaze.Host` already references `HomeBlaze.Components`.

- [ ] **Step 5: Add the shared content view**

Create `src/HomeBlaze/HomeBlaze.Storage.Blazor/Files/MarkdownContentView.razor`:

```razor
@using System.Text
@using System.Web
@using HomeBlaze.Components
@using HomeBlaze.Components.Abstractions.Attributes
@using HomeBlaze.Storage.Internal
@using Namotion.Interceptor

@foreach (var segment in GetRenderSegments())
{
    @if (segment.Html != null)
    {
        @((MarkupString)segment.Html)
    }
    else if (segment.Widget != null)
    {
        <SubjectComponent Subject="@segment.Widget" Type="SubjectComponentType.Widget" />
    }
}

@code {
    /// <summary>
    /// The segments of a page or of a fragment, in order.
    /// </summary>
    [Parameter]
    public IDictionary<string, IInterceptorSubject>? Children { get; set; }

    private record RenderSegment(string? Html, IInterceptorSubject? Widget);

    private List<RenderSegment> GetRenderSegments()
    {
        var result = new List<RenderSegment>();
        if (Children is null)
            return result;

        StringBuilder? builder = null;

        foreach (var child in Children.Values)
        {
            if (child is HtmlSegment html)
            {
                builder ??= new StringBuilder();
                builder.Append(html.Html);
            }
            else if (child is RenderExpression expression)
            {
                builder ??= new StringBuilder();
                builder.Append(HttpUtility.HtmlEncode(expression.Value?.ToString() ?? string.Empty));
            }
            else
            {
                // HTML and expressions are joined into one markup string so that an
                // expression can sit inside an HTML attribute.
                if (builder is { Length: > 0 })
                {
                    result.Add(new RenderSegment(builder.ToString(), null));
                    builder.Clear();
                }

                result.Add(new RenderSegment(null, child));
            }
        }

        if (builder is { Length: > 0 })
        {
            result.Add(new RenderSegment(builder.ToString(), null));
        }

        return result;
    }
}
```

- [ ] **Step 6: Use it in the page component**

In `MarkdownFilePageComponent.razor` replace the `@foreach (var segment in GetRenderSegments())` block with:

```razor
<MarkdownContentView Children="@File?.Children" />
```

Delete the `RenderSegment` record and `GetRenderSegments` from its `@code` block, and the usings that are no longer needed.

- [ ] **Step 7: Add the fragment widget**

Create `src/HomeBlaze/HomeBlaze.Storage.Blazor/Files/MarkdownFragmentWidget.razor`:

```razor
@using HomeBlaze.Components.Abstractions
@using HomeBlaze.Components.Abstractions.Attributes
@using HomeBlaze.Storage.Pages
@using Namotion.Interceptor

@attribute [SubjectComponent(SubjectComponentType.Widget, typeof(MarkdownFragment))]
@implements ISubjectComponent

<MarkdownContentView Children="@((Subject as MarkdownFragment)?.Children)" />

@code {
    [Parameter]
    public IInterceptorSubject? Subject { get; set; }
}
```

- [ ] **Step 8: Add the Card subject**

Create `src/HomeBlaze/HomeBlaze.Components/Card.cs`:

```csharp
using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Components;

/// <summary>
/// A titled card that wraps page content.
/// </summary>
[Category("Components")]
[Description("A titled card that wraps page content")]
[InterceptorSubject]
public partial class Card : IConfigurable, ITitleProvider
{
    /// <summary>
    /// Title shown at the top of the card.
    /// </summary>
    [Configuration]
    public partial string? Title { get; set; }

    /// <summary>
    /// Name of the icon shown next to the title, for example "Thermostat".
    /// </summary>
    [Configuration]
    public partial string? Icon { get; set; }

    /// <summary>
    /// The body of the card.
    /// </summary>
    [Slot(IsDefault = true)]
    public partial IInterceptorSubject? Content { get; set; }

    /// <summary>
    /// Content shown below a divider at the bottom of the card.
    /// </summary>
    [Slot]
    public partial IInterceptorSubject? Footer { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
```

- [ ] **Step 9: Add the Card widget**

Create `src/HomeBlaze/HomeBlaze.Components/Components/CardWidget.razor`:

```razor
@attribute [SubjectComponent(SubjectComponentType.Widget, typeof(Card))]
@implements ISubjectComponent

<MudPaper Class="pa-4" Elevation="2" data-testid="card">
    @if (!string.IsNullOrEmpty(CardSubject?.Title) || !string.IsNullOrEmpty(CardSubject?.Icon))
    {
        <div class="d-flex align-center mb-2">
            @if (!string.IsNullOrEmpty(CardSubject?.Icon))
            {
                <MudIcon Icon="@MudBlazorIcons.Resolve(CardSubject.Icon)" Class="mr-2" />
            }
            <MudText Typo="Typo.h6">@CardSubject?.Title</MudText>
        </div>
    }

    <SubjectComponent Subject="@CardSubject?.Content" Type="SubjectComponentType.Widget" />

    @if (CardSubject?.Footer is not null)
    {
        <MudDivider Class="my-2" />
        <SubjectComponent Subject="@CardSubject.Footer" Type="SubjectComponentType.Widget" />
    }
</MudPaper>

@code {
    [Parameter]
    public IInterceptorSubject? Subject { get; set; }

    private Card? CardSubject => Subject as Card;
}
```

- [ ] **Step 10: Run all E2E tests**

Run: `dotnet test src/HomeBlaze/HomeBlaze.E2E.Tests`
Expected: all pass.

If the live value test fails while the others pass, the fragment does not re-render when an expression changes. Find how `MarkdownFilePageComponent` gets re-rendered on a `RenderExpression` change (search the host for the component that renders pages and for `TrackingScope`) and make sure the fragment widget renders inside the same mechanism.

- [ ] **Step 11: Interactive check with the Playwright MCP**

Copy the E2E page to the application data folder first so the local instance has it: `cp src/HomeBlaze/HomeBlaze.E2E.Tests/TestData/Demo/Blocks.md src/HomeBlaze/HomeBlaze/Data/Files/Demo/Blocks.md`. Task 11 turns this copy into the real demo page.

Follow "Verification with Playwright". Page: `/pages/Demo/Blocks.md`.

Must be visible: one card with icon and title, body text, a number in "speed is N RPM" that changes between two snapshots taken a few seconds apart, the motor widget inside the card, a divider and the footer text. At 390 px width the card must fit without horizontal scrolling.

- [ ] **Step 12: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Storage.Blazor/Files/MarkdownContentView.razor src/HomeBlaze/HomeBlaze.Storage.Blazor/Files/MarkdownFragmentWidget.razor src/HomeBlaze/HomeBlaze.Storage.Blazor/Files/MarkdownFilePageComponent.razor src/HomeBlaze/HomeBlaze.Components/MudBlazorIcons.cs src/HomeBlaze/HomeBlaze.Host/Display/SubjectIconExtensions.cs src/HomeBlaze/HomeBlaze.Components/Card.cs src/HomeBlaze/HomeBlaze.Components/Components/CardWidget.razor src/HomeBlaze/HomeBlaze.E2E.Tests/TestData/Demo/Blocks.md src/HomeBlaze/HomeBlaze.E2E.Tests/MarkdownBlockTests.cs src/HomeBlaze/HomeBlaze/Data/Files/Demo/Blocks.md
git commit -m "feat: render slot content and add a card widget for markdown pages"
```

---

### Task 10: Page scope for names and relative paths

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Abstractions/IPathScope.cs`
- Create: `src/HomeBlaze/HomeBlaze.Services/SubjectPathResolverScopeExtensions.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Storage/Files/MarkdownFile.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Storage/Internal/RenderExpression.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Storage/Internal/MarkdownContentParser.cs` (`RenderContent` only)
- Modify: `src/HomeBlaze/HomeBlaze.Components/Widget.cs`
- Modify: `src/HomeBlaze/HomeBlaze/Data/Files/Demo/Inline.md`
- Test: `src/HomeBlaze/HomeBlaze.Storage.Tests/Pages/MarkdownPageScopeTests.cs`

**Interfaces:**
- Consumes: `SubjectPathResolver.ResolveSubject(string, PathStyle, IInterceptorSubject?)`, `TryGetFirstParent<T>()` (the extension `RootManagerExtensions.GetConfigurationWriter` uses), `TryGetRegisteredProperty(string)`.
- Produces:
  - `public interface IPathScope { IInterceptorSubject? TryGetNamedSubject(string name); }` in `HomeBlaze.Abstractions`
  - `public static IInterceptorSubject? ResolveScopedSubject(this SubjectPathResolver resolver, string path, PathStyle style, IInterceptorSubject from)`
  - `public static object? ResolveScopedValue(this SubjectPathResolver resolver, string path, PathStyle style, IInterceptorSubject from)`
  - `MarkdownFile : IPathScope`

Behaviour: the scope is `from` itself when it implements `IPathScope`, otherwise its nearest parent that does. Without a scope both methods behave exactly like the existing `ResolveSubject` and `ResolveValue` with `relativeTo: from`. With a scope, an absolute path is unchanged, a path whose first segment is a name in the scope continues from that named subject, and every other relative path resolves from the scope.

- [ ] **Step 1: Write the failing tests**

Create `src/HomeBlaze/HomeBlaze.Storage.Tests/Pages/MarkdownPageScopeTests.cs`:

```csharp
using HomeBlaze.Services;
using HomeBlaze.Storage.Internal;
using HomeBlaze.Storage.Pages;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Tests.Pages;

public class MarkdownPageScopeTests
{
    private const string Namespace = "HomeBlaze.Storage.Tests.Pages";

    private static RenderExpression FindExpression(IEnumerable<IInterceptorSubject> subjects, string path) =>
        Flatten(subjects).OfType<RenderExpression>().Single(expression => expression.Path == path);

    private static IEnumerable<IInterceptorSubject> Flatten(IEnumerable<IInterceptorSubject> subjects)
    {
        foreach (var subject in subjects)
        {
            yield return subject;

            var nested = subject switch
            {
                MarkdownFragment fragment => fragment.Children.Values,
                TestPanel panel => new[] { panel.Content, panel.Footer }.Where(value => value is not null).Cast<IInterceptorSubject>(),
                _ => []
            };

            foreach (var child in Flatten(nested))
            {
                yield return child;
            }
        }
    }

    [Fact]
    public async Task WhenNamedSubjectIsNestedInASlot_ThenExpressionOnThePageResolvesItByName()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();

        // Act
        var page = await fixture.AddPageAsync("page.md", $$$"""
            ::::{{{Namespace}}}.TestPanel#panel
            :::{{{Namespace}}}.TestLeaf#leaf count=42
            :::
            ::::

            Count: {{ leaf/Count }}
            """);

        // Assert
        Assert.Equal(42, FindExpression(page.Children.Values, "leaf/Count").Value);
    }

    [Fact]
    public async Task WhenExpressionIsNestedInASlot_ThenItResolvesNamesAndPagePropertiesFromThePage()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();

        // Act
        var page = await fixture.AddPageAsync("page.md", $$$"""
            :::{{{Namespace}}}.TestLeaf#leaf name=Top
            :::

            :::{{{Namespace}}}.TestPanel#panel
            Nested {{ leaf/Name }} and {{ Title }}
            :::
            """);

        // Assert
        Assert.Equal("Top", FindExpression(page.Children.Values, "leaf/Name").Value);
        Assert.Equal("Page", FindExpression(page.Children.Values, "Title").Value);
    }

    [Fact]
    public async Task WhenSubjectIsNestedInASlot_ThenRelativePathResolvesFromThePage()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var other = await fixture.AddPageAsync("other.md", "other");
        var page = await fixture.AddPageAsync("page.md", $$"""
            ::::{{Namespace}}.TestPanel#panel
            :::{{Namespace}}.TestLeaf#leaf
            :::
            ::::
            """);
        var leaf = Flatten(page.Children.Values).OfType<TestLeaf>().Single();
        var resolver = fixture.Services.GetRequiredService<SubjectPathResolver>();

        // Act
        var sibling = resolver.ResolveScopedSubject("../other.md", PathStyle.Canonical, leaf);
        var named = resolver.ResolveScopedSubject("panel", PathStyle.Canonical, leaf);
        var absolute = resolver.ResolveScopedSubject("/other.md", PathStyle.Canonical, leaf);

        // Assert
        Assert.Same(other, sibling);
        Assert.Same(page.Children["panel"], named);
        Assert.Same(other, absolute);
    }

    [Fact]
    public async Task WhenSubjectHasNoScope_ThenScopedResolutionBehavesLikeTheUnscopedOne()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync("page.md", "text");
        var resolver = fixture.Services.GetRequiredService<SubjectPathResolver>();

        // Act
        var scoped = resolver.ResolveScopedSubject("page.md", PathStyle.Canonical, fixture.Storage);
        var unscoped = resolver.ResolveSubject("page.md", PathStyle.Canonical, fixture.Storage);

        // Assert
        Assert.Same(page, unscoped);
        Assert.Same(unscoped, scoped);
    }

    [Fact]
    public async Task WhenEvaluatedContentIsRead_ThenNamesInsideBlocksAreResolved()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync("page.md", $$$"""
            ::::{{{Namespace}}}.TestPanel#panel
            :::{{{Namespace}}}.TestLeaf#leaf count=7
            :::
            ::::

            Count: {{ leaf/Count }}
            """);

        // Act
        var evaluated = page.ReadEvaluatedContent();

        // Assert
        Assert.Contains("Count: 7", evaluated);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests --filter "FullyQualifiedName~MarkdownPageScopeTests"`
Expected: build error, `ResolveScopedSubject` does not exist.

- [ ] **Step 3: Add the scope interface**

Create `src/HomeBlaze/HomeBlaze.Abstractions/IPathScope.cs`:

```csharp
using Namotion.Interceptor;

namespace HomeBlaze.Abstractions;

/// <summary>
/// A subject that is the starting point for the relative paths of everything it contains,
/// and that knows some of the subjects inside it by name.
/// </summary>
public interface IPathScope
{
    /// <summary>
    /// Returns the subject with this name inside the scope, or null when there is none.
    /// </summary>
    IInterceptorSubject? TryGetNamedSubject(string name);
}
```

- [ ] **Step 4: Add the scoped resolution**

Create `src/HomeBlaze/HomeBlaze.Services/SubjectPathResolverScopeExtensions.cs`:

```csharp
using HomeBlaze.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Parent;

namespace HomeBlaze.Services;

/// <summary>
/// Path resolution that starts at the nearest <see cref="IPathScope"/> of a subject.
/// </summary>
public static class SubjectPathResolverScopeExtensions
{
    /// <summary>
    /// Resolves a subject from a path. A relative path resolves from the scope of
    /// <paramref name="from"/>, and its first segment can be a name the scope knows.
    /// Without a scope this is <c>ResolveSubject</c> relative to <paramref name="from"/>.
    /// </summary>
    public static IInterceptorSubject? ResolveScopedSubject(
        this SubjectPathResolver resolver,
        string path,
        PathStyle style,
        IInterceptorSubject from)
    {
        var scope = FindScope(from);
        if (scope is not IInterceptorSubject scopeSubject)
        {
            return resolver.ResolveSubject(path, style, from);
        }

        if (string.IsNullOrEmpty(path))
        {
            return scopeSubject;
        }

        var hasPrefix = path.StartsWith('/') || path.StartsWith("./") || path.StartsWith("../") || path == "..";
        if (hasPrefix)
        {
            return resolver.ResolveSubject(path, style, scopeSubject);
        }

        var separator = path.IndexOf('/');
        var named = scope.TryGetNamedSubject(separator < 0 ? path : path[..separator]);
        if (named is null)
        {
            return resolver.ResolveSubject(path, style, scopeSubject);
        }

        return separator < 0 ? named : resolver.ResolveSubject("./" + path[(separator + 1)..], style, named);
    }

    /// <summary>
    /// Resolves a property value from a path whose last segment is the property name,
    /// with the subject part resolved by <see cref="ResolveScopedSubject"/>.
    /// </summary>
    public static object? ResolveScopedValue(
        this SubjectPathResolver resolver,
        string path,
        PathStyle style,
        IInterceptorSubject from)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var lastSlash = path.LastIndexOf('/');
        if (lastSlash < 0)
        {
            var scopeSubject = FindScope(from) as IInterceptorSubject ?? from;
            return scopeSubject.TryGetRegisteredProperty(path)?.GetValue();
        }

        var propertyName = path[(lastSlash + 1)..];
        if (propertyName.Length == 0)
        {
            return null;
        }

        var subject = resolver.ResolveScopedSubject(lastSlash == 0 ? "/" : path[..lastSlash], style, from);
        return subject?.TryGetRegisteredProperty(propertyName)?.GetValue();
    }

    private static IPathScope? FindScope(IInterceptorSubject subject) =>
        subject as IPathScope ?? subject.TryGetFirstParent<IPathScope>();
}
```

If `TryGetFirstParent` lives in another namespace, take the using from `RootManagerExtensions.cs`.

- [ ] **Step 5: Make the page a scope**

In `MarkdownFile.cs` add `IPathScope` to the interface list and:

```csharp
IInterceptorSubject? IPathScope.TryGetNamedSubject(string name) =>
    _namedSubjects.GetValueOrDefault(name);
```

- [ ] **Step 6: Use scoped resolution in expressions**

In `RenderExpression.ResolveValue` replace the resolver call:

```csharp
return _pathResolver.ResolveScopedValue(Path, PathStyle.Canonical, Parent);
```

In `MarkdownContentParser.RenderContent` replace the resolver call the same way:

```csharp
var value = _pathResolver.ResolveScopedValue(match.Groups[1].Value, PathStyle.Canonical, parent);
```

- [ ] **Step 7: Use scoped resolution in Widget**

In `src/HomeBlaze/HomeBlaze.Components/Widget.cs`, in `ResolveSubject()`:

```csharp
return _pathResolver.ResolveScopedSubject(Path, PathStyle.Canonical, this);
```

Update the XML comment of `Path`: a relative path resolves from the page that contains the widget, or from the widget itself when it is not on a page.

- [ ] **Step 8: Update the demo page path**

In `src/HomeBlaze/HomeBlaze/Data/Files/Demo/Inline.md` the widget path was relative to the widget and is now relative to the page:

```markdown
:::HomeBlaze.Components.Widget#conveyorRef path=../Conveyor
:::
```

- [ ] **Step 9: Run the tests**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests && dotnet test src/HomeBlaze/HomeBlaze.Services.Tests && dotnet test src/HomeBlaze/HomeBlaze.Components.Tests`
Expected: all pass. The existing resolver tests are untouched because the existing methods did not change.

If `WhenNamedSubjectIsNestedInASlot_...` returns null although the name table has the subject, the `[Derived]` value of the expression was computed before the page finished parsing and was not recomputed. Assign `_namedSubjects` in `LoadFileAsync` before `Children`, which it already does in Task 4. If it still fails, read `Value` through a freshly created `RenderExpression` in the test to confirm, then make the name table a tracked property of `MarkdownFile` so the derived value depends on it.

- [ ] **Step 10: Run the E2E tests**

Run: `dotnet test src/HomeBlaze/HomeBlaze.E2E.Tests`
Expected: all pass. `Demo/Inline.md` proves the changed widget path. The nested name is proven end to end by `Blocks.md` in Task 9, which runs after this task.

- [ ] **Step 11: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Abstractions/IPathScope.cs src/HomeBlaze/HomeBlaze.Services/SubjectPathResolverScopeExtensions.cs src/HomeBlaze/HomeBlaze.Storage/Files/MarkdownFile.cs src/HomeBlaze/HomeBlaze.Storage/Internal/RenderExpression.cs src/HomeBlaze/HomeBlaze.Storage/Internal/MarkdownContentParser.cs src/HomeBlaze/HomeBlaze.Components/Widget.cs src/HomeBlaze/HomeBlaze/Data/Files/Demo/Inline.md src/HomeBlaze/HomeBlaze.Storage.Tests/Pages/MarkdownPageScopeTests.cs
git commit -m "feat: resolve names and relative paths from the page for nested blocks"
```

---

### Task 11: FlowLayout and TabLayout (**UI**)

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Components/FlowLayout.cs`
- Create: `src/HomeBlaze/HomeBlaze.Components/TabLayout.cs`
- Create: `src/HomeBlaze/HomeBlaze.Components/Tab.cs`
- Create: `src/HomeBlaze/HomeBlaze.Components/Components/FlowLayoutWidget.razor`
- Create: `src/HomeBlaze/HomeBlaze.Components/Components/TabLayoutWidget.razor`
- Modify: `src/HomeBlaze/HomeBlaze.E2E.Tests/TestData/Demo/Blocks.md`
- Modify: `src/HomeBlaze/HomeBlaze/Data/Files/Demo/Blocks.md`
- Test: `src/HomeBlaze/HomeBlaze.E2E.Tests/MarkdownBlockTests.cs`

**Interfaces:**
- Consumes: `SlotAttribute`, `MudBlazorIcons.Resolve`, `SubjectComponent`.
- Produces:
  - `FlowLayout` with `[Configuration] int MinWidth` (default 320) and `[Slot("Item")] IInterceptorSubject[] Items`
  - `TabLayout` with `[Slot("Tab")] Tab[] Tabs`
  - `Tab` with `[Configuration] string? Title`, `[Configuration] string? Icon`, `[Slot(IsDefault = true)] IInterceptorSubject? Content`

- [ ] **Step 1: Extend the E2E page**

Append to `src/HomeBlaze/HomeBlaze.E2E.Tests/TestData/Demo/Blocks.md`:

```markdown

## Flow

:::::HomeBlaze.Components.FlowLayout minWidth=240
::::Item
:::HomeBlaze.Components.Card title="Flow One"
First cell
:::
::::
::::Item
:::HomeBlaze.Components.Card title="Flow Two"
Second cell
:::
::::
::::Item
Plain text cell
::::
:::::

## Tabs

::::HomeBlaze.Components.TabLayout
:::Tab title="First Tab" icon=Home
Content of the first tab
:::
:::Tab title="Second Tab"
Content of the second tab
:::
::::
```

- [ ] **Step 2: Write the failing E2E tests**

Add to `MarkdownBlockTests.cs`:

```csharp
[Fact]
public async Task WhenPageHasFlowLayout_ThenEachItemBlockIsOneCell()
{
    // Arrange & Act
    var page = await OpenBlocksPageAsync();

    // Assert
    var items = page.GetByTestId("flow-item");
    await Assertions.Expect(items).ToHaveCountAsync(3, new() { Timeout = 30000 });
    await Assertions.Expect(items.Nth(0)).ToContainTextAsync("Flow One");
    await Assertions.Expect(items.Nth(1)).ToContainTextAsync("Second cell");
    await Assertions.Expect(items.Nth(2)).ToContainTextAsync("Plain text cell");
}

[Fact]
public async Task WhenViewportIsNarrow_ThenFlowItemsStackInOneColumn()
{
    // Arrange
    var page = await OpenBlocksPageAsync();
    var items = page.GetByTestId("flow-item");
    await Assertions.Expect(items).ToHaveCountAsync(3, new() { Timeout = 30000 });

    // Act
    await page.SetViewportSizeAsync(390, 844);

    // Assert
    await Assertions.Expect(items.Nth(1)).ToBeVisibleAsync();
    var first = await items.Nth(0).BoundingBoxAsync();
    var second = await items.Nth(1).BoundingBoxAsync();
    Assert.NotNull(first);
    Assert.NotNull(second);
    Assert.Equal(first.X, second.X, 1.0);
    Assert.True(second.Y >= first.Y + first.Height);
}

[Fact]
public async Task WhenSecondTabIsClicked_ThenItsContentIsShown()
{
    // Arrange
    var page = await OpenBlocksPageAsync();
    var tabs = page.GetByTestId("tab-layout");
    await Assertions.Expect(tabs).ToContainTextAsync("Content of the first tab", new() { Timeout = 30000 });

    // Act
    await tabs.GetByText("Second Tab").ClickAsync();

    // Assert
    await Assertions.Expect(tabs).ToContainTextAsync("Content of the second tab");
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.E2E.Tests --filter "FullyQualifiedName~MarkdownBlockTests"`
Expected: the three new tests fail with error boxes for the unknown types.

- [ ] **Step 4: Add the subjects**

Create `src/HomeBlaze/HomeBlaze.Components/FlowLayout.cs`:

```csharp
using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Components;

/// <summary>
/// Lays its items out in equal columns that wrap to as many rows as the width needs.
/// </summary>
[Category("Components")]
[Description("Lays items out in columns that wrap by screen width")]
[InterceptorSubject]
public partial class FlowLayout : IConfigurable, ITitleProvider
{
    public FlowLayout()
    {
        MinWidth = 320;
        Items = [];
    }

    /// <summary>
    /// The narrowest a column may get, in pixels, before the items wrap.
    /// </summary>
    [Configuration]
    public partial int MinWidth { get; set; }

    /// <summary>
    /// The cells of the layout, one per <c>Item</c> block.
    /// </summary>
    [Slot("Item")]
    public partial IInterceptorSubject[] Items { get; set; }

    public string? Title => null;

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
```

Create `src/HomeBlaze/HomeBlaze.Components/Tab.cs`:

```csharp
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Components;

/// <summary>
/// One tab of a <see cref="TabLayout"/>.
/// </summary>
[InterceptorSubject]
public partial class Tab : IConfigurable
{
    /// <summary>
    /// The label of the tab.
    /// </summary>
    [Configuration]
    public partial string? Title { get; set; }

    /// <summary>
    /// Name of the icon shown on the tab, for example "Bolt".
    /// </summary>
    [Configuration]
    public partial string? Icon { get; set; }

    /// <summary>
    /// What the tab shows when it is selected.
    /// </summary>
    [Slot(IsDefault = true)]
    public partial IInterceptorSubject? Content { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
```

Create `src/HomeBlaze/HomeBlaze.Components/TabLayout.cs`:

```csharp
using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Components;

/// <summary>
/// Shows one of several tabs at a time.
/// </summary>
[Category("Components")]
[Description("Shows page content in tabs")]
[InterceptorSubject]
public partial class TabLayout : IConfigurable, ITitleProvider
{
    public TabLayout()
    {
        Tabs = [];
    }

    /// <summary>
    /// The tabs, one per <c>Tab</c> block.
    /// </summary>
    [Slot("Tab")]
    public partial Tab[] Tabs { get; set; }

    public string? Title => null;

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
```

- [ ] **Step 5: Add the widget components**

Create `src/HomeBlaze/HomeBlaze.Components/Components/FlowLayoutWidget.razor`:

```razor
@attribute [SubjectComponent(SubjectComponentType.Widget, typeof(FlowLayout))]
@implements ISubjectComponent

<div class="flow-layout"
     data-testid="flow-layout"
     style="display: grid; grid-template-columns: repeat(auto-fill, minmax(min(@(Flow?.MinWidth ?? 320)px, 100%), 1fr)); gap: 16px; margin: 1rem 0;">
    @foreach (var item in Flow?.Items ?? [])
    {
        <div class="flow-layout-item" data-testid="flow-item" style="min-width: 0;">
            <SubjectComponent Subject="@item" Type="SubjectComponentType.Widget" />
        </div>
    }
</div>

@code {
    [Parameter]
    public IInterceptorSubject? Subject { get; set; }

    private FlowLayout? Flow => Subject as FlowLayout;
}
```

`min(..., 100%)` keeps a column from being wider than a phone screen when `MinWidth` is larger than the viewport.

Create `src/HomeBlaze/HomeBlaze.Components/Components/TabLayoutWidget.razor`:

```razor
@attribute [SubjectComponent(SubjectComponentType.Widget, typeof(TabLayout))]
@implements ISubjectComponent

<div data-testid="tab-layout" style="margin: 1rem 0;">
    <MudTabs Elevation="1" Rounded="true" PanelClass="pa-4">
        @foreach (var tab in Layout?.Tabs ?? [])
        {
            <MudTabPanel Text="@tab.Title"
                         Icon="@(string.IsNullOrEmpty(tab.Icon) ? null : MudBlazorIcons.Resolve(tab.Icon))">
                <SubjectComponent Subject="@tab.Content" Type="SubjectComponentType.Widget" />
            </MudTabPanel>
        }
    </MudTabs>
</div>

@code {
    [Parameter]
    public IInterceptorSubject? Subject { get; set; }

    private TabLayout? Layout => Subject as TabLayout;
}
```

- [ ] **Step 6: Run all E2E tests**

Run: `dotnet test src/HomeBlaze/HomeBlaze.E2E.Tests`
Expected: all pass.

- [ ] **Step 7: Make the application demo page the real one**

Replace `src/HomeBlaze/HomeBlaze/Data/Files/Demo/Blocks.md` with the content of the E2E page and add a short introduction under the heading that says what the page demonstrates: a card with a footer, a flow layout and tabs. Add `navTitle: Blocks Demo` and `position: 6` to its frontmatter so it sits after the widgets demo.

- [ ] **Step 8: Interactive check with the Playwright MCP**

Follow "Verification with Playwright". Page: `/pages/Demo/Blocks.md`.

Must be visible: three flow cells side by side at 1280 px and stacked in one column at 390 px, with no horizontal scrolling at 390 px. The tab bar with two tabs, the first with an icon. Click the second tab and confirm its content replaces the first. Then open split mode, put the cursor on the `:::Tab title="First Tab"` line and confirm the edit button appears for it, because a `Tab` is a concrete slot value with its own configuration.

- [ ] **Step 9: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Components/FlowLayout.cs src/HomeBlaze/HomeBlaze.Components/TabLayout.cs src/HomeBlaze/HomeBlaze.Components/Tab.cs src/HomeBlaze/HomeBlaze.Components/Components/FlowLayoutWidget.razor src/HomeBlaze/HomeBlaze.Components/Components/TabLayoutWidget.razor src/HomeBlaze/HomeBlaze.E2E.Tests/TestData/Demo/Blocks.md src/HomeBlaze/HomeBlaze/Data/Files/Demo/Blocks.md src/HomeBlaze/HomeBlaze.E2E.Tests/MarkdownBlockTests.cs
git commit -m "feat: add flow and tab layout widgets for markdown pages"
```

---

### Task 12: Documentation, cleanup and pull request (**UI**)

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze/Data/Files/Docs/administration/pages.md`
- Modify: `src/HomeBlaze/HomeBlaze/Data/Files/Docs/administration/paths.md`
- Modify: `src/HomeBlaze/HomeBlaze/Data/Files/Docs/development/building-subjects.md`
- Modify: `src/HomeBlaze/HomeBlaze/Data/Files/Docs/architecture/design/storage.md`
- Modify: `src/HomeBlaze/HomeBlaze/Data/Files/Docs/glossary.md`
- Delete: `docs/superpowers/specs/2026-10-10-markdown-blocks-design.md`, `docs/superpowers/plans/2026-10-10-markdown-blocks.md`, `docs/superpowers/plans/2026-10-10-markdown-blocks-prototype/`

The documentation is what remains of the design. It describes the result, not the history.

- [ ] **Step 1: Rewrite the "Embedded Subjects" part of pages.md as "Blocks"**

The section must cover, each with a short example taken from the spec:

- A block is a head line, content and a closing line of colons. At least three colons. An outer block needs a longer fence than the blocks inside it.
- The head line: full type name, optional `#name`, `key=value` arguments. Keys are the camelCase configuration property names. Values with spaces are quoted. All names are case-sensitive.
- The `json` fence as first child for configuration that is not a single value.
- Content and slots: content goes to the default slot, a block with a name without a dot fills the slot of that name on the surrounding widget, a list slot takes one value per block.
- The widgets that ship: `Card` (`title`, `icon`, `Footer`), `FlowLayout` (`minWidth`, `Item`), `TabLayout` (`Tab` with `title`, `icon`), and `Widget` (`path`) for an existing subject.
- Style blocks: `::: {.class}` for a plain `<div>`.
- Where a block is allowed, and that a block between raw HTML tags breaks the layout.
- Errors appear as a red box with the line number. List the common ones: unnamed block from a miscounted fence, unknown type, unknown slot, unknown argument.
- Identity: a subject with `#name` survives edits elsewhere on the page. Without a name it is tied to its position.
- The old `subject(name)` fence is no longer supported, with a before and after.

Every example of page source must be inside a `~~~~markdown` fence so the page does not execute its own examples. Rewrite the "Example: Creating a Dashboard" section with blocks and the tips at the end.

- [ ] **Step 2: Update paths.md**

- The `motor/Speed` row and the "Resolution Order" section: names are declared with `#name` on a block and are known anywhere on the page, also inside other blocks.
- Add: every relative path on a page starts at the page, however deeply the block is nested, including the `path` of a `Widget`.

- [ ] **Step 3: Add "Content slots" to building-subjects.md**

Explain `[Slot]` for authors of widget types, with the `Card` subject and its razor component as the example:

- `[Slot(IsDefault = true)]` on an `IInterceptorSubject?` property for the body.
- `[Slot]` and `[Slot("Name")]` for named slots, where the name is what one block is called.
- Property type decides: `IInterceptorSubject` holds content, a concrete configurable subject type is created from the block's arguments, an array takes one value per block.
- The widget renders a slot with `<SubjectComponent Subject="..." Type="SubjectComponentType.Widget" />` and does not need to know what the content is.
- A slot property is not configuration and is not saved.

Update the place around line 861 that shows `HtmlSegment` if it describes the old parsing.

- [ ] **Step 4: Update storage.md and the glossary**

`storage.md`: replace the description of how a markdown file is parsed with the pipeline: page parser, segment builder, content parser, block writer, and that the page keeps a map from block to subject for write-back. Keep it to what a maintainer needs to find the code.

`glossary.md`: add "Block", "Slot" and "Fragment". Change the entry for embedded subjects to refer to blocks.

- [ ] **Step 5: Run the guard test and all HomeBlaze tests**

```bash
dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests
dotnet test src/HomeBlaze/HomeBlaze.Storage.Blazor.Tests
dotnet test src/HomeBlaze/HomeBlaze.Services.Tests
dotnet test src/HomeBlaze/HomeBlaze.Components.Tests
dotnet test src/HomeBlaze/HomeBlaze.Host.Services.Tests
dotnet test src/HomeBlaze/HomeBlaze.E2E.Tests
```

Expected: all pass. `RepositoryPagesTests` covers every documentation page.

- [ ] **Step 6: Run the default unit test suite of the solution**

Run: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`
Expected: all pass.

- [ ] **Step 7: Interactive check with the Playwright MCP**

Follow "Verification with Playwright". Pages: `/pages/Docs/administration/pages.md`, `/pages/Docs/administration/paths.md`, `/pages/Docs/development/building-subjects.md`, `/pages/Demo/Blocks.md`, `/pages/Demo/Inline.md`, `/pages/Devices.md`.

Must hold: no error boxes on any of them, the examples in the documentation show as code and are not executed, and the three demo pages look as in the earlier checks.

- [ ] **Step 8: Commit the documentation**

```bash
git add src/HomeBlaze/HomeBlaze/Data/Files/Docs
git commit -m "docs: describe widget blocks and slots for HomeBlaze pages"
```

- [ ] **Step 9: Remove the temporary design notes**

```bash
git rm -r docs/superpowers/specs/2026-10-10-markdown-blocks-design.md docs/superpowers/plans/2026-10-10-markdown-blocks.md docs/superpowers/plans/2026-10-10-markdown-blocks-prototype
git commit -m "chore: remove temporary design notes for markdown blocks"
```

If copies exist untracked in the main checkout, delete those too. Confirm with `git status --short` that only the user's two JSON files remain modified there.

- [ ] **Step 10: Open the pull request**

Read `.github/pull_request_template.md` and fill it in by hand, because `--body-file` bypasses the template. Pick one `area:` and one `type:` label from `gh label list`.

Title: `feat: add widget blocks and content slots to HomeBlaze markdown pages`

The description must state:

- That the page syntax changes and the `subject(name)` fence is no longer supported, with one before and after example.
- The slot rules in five lines.
- The three new widgets.
- What was verified: unit tests, E2E tests, and the interactive checks at phone and desktop width.
- What is not included: formatting in expressions, `GridLayout` cells as blocks, mixed item types.
- That pages outside the repository need to be converted when a server is updated.

No AI attribution anywhere. Ask the user before pushing and before creating the pull request.

---

## After the merge

Not part of the pull request:

- Convert the live dashboard page once the server runs the new version. It uses the old fence for the heat pump and Hue widgets, and its hand-written HTML cards can become `Card` and `FlowLayout` blocks. This needs the user's go-ahead because it writes to the live share.
- Follow-ups that came up during the design: unit-aware formatting in `{{ }}`, `GridLayout` cells as blocks, and expressions inside code fences being evaluated in rendered documentation.
