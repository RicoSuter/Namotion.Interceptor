# Static Markdown Blocks Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A value change on a page replaces only the HTML of the top-level Markdown block that shows it.

**Architecture:** `MarkdownSegmentBuilder` writes a break marker between top-level blocks (none inside a raw HTML span); `Split` turns it into `StartsBlock` on the next HTML segment; `MarkdownContentParser` carries the flag onto `HtmlSegment`; `MarkdownContentView` starts a new `MarkupString` at a flagged segment, so Blazor's diff leaves unchanged blocks alone.

**Tech Stack:** Markdig, Blazor Server, xUnit, Playwright.

**Spec:** [static-markdown-blocks.md](static-markdown-blocks.md)

**Rules (AGENTS.md):** tests `When<Condition>_Then<Expected>` with `// Arrange` / `// Act` / `// Assert`; no `Task.Delay`; comments only for the why; no AI attribution in commits; commit only the paths you changed (never `git commit -a`). `HomeBlaze.Storage` has a public API snapshot (`HomeBlaze.Storage.Tests/VerifyChecksTests.PublicApi.verified.txt`); accept an intended change by replacing it with the `.received.txt`.

All paths are relative to the repository root.

---

### Task 1: Break markers and `StartsBlock` in the segment builder

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.Storage/Pages/MarkdownSegmentBuilder.cs`
- Test: `src/HomeBlaze/HomeBlaze.Storage.Tests/Pages/MarkdownSegmentBuilderTests.cs`

- [ ] **Step 1: Write the failing tests** (in `MarkdownSegmentBuilderTests`, using its `Build` and `AllHtml` helpers)

```csharp
    [Fact]
    public void WhenPageHasTwoParagraphs_ThenTheSecondStartsABlock()
    {
        // Act
        var segments = Build("First.\n\nSecond.");

        // Assert
        var html = segments.Cast<HtmlMarkdownSegment>().ToArray();
        Assert.Equal(2, html.Length);
        Assert.False(html[0].StartsBlock);
        Assert.Equal("<p>First.</p>\n", html[0].Html);
        Assert.True(html[1].StartsBlock);
        Assert.Equal("<p>Second.</p>\n", html[1].Html);
    }

    [Fact]
    public void WhenRawHtmlSpansSeveralBlocks_ThenNoBlockStartsInsideTheSpan()
    {
        // Act
        var segments = Build("Before.\n\n<div class=\"box\">\n\nInside.\n\n</div>\n\nAfter.");

        // Assert
        var html = segments.Cast<HtmlMarkdownSegment>().ToArray();
        Assert.Equal(3, html.Length);
        Assert.Equal("<p>Before.</p>\n", html[0].Html);
        Assert.True(html[1].StartsBlock);
        Assert.StartsWith("<div class=\"box\">", html[1].Html);
        Assert.Contains("<p>Inside.</p>", html[1].Html);
        Assert.EndsWith("</div>\n", html[1].Html);
        Assert.True(html[2].StartsBlock);
        Assert.Equal("<p>After.</p>\n", html[2].Html);
    }

    [Fact]
    public void WhenInlineHtmlIsOpenedAndClosedInDifferentParagraphs_ThenNoBlockStartsBetweenThem()
    {
        // Act
        var segments = Build("One <span class=\"x\">open\n\nTwo close</span>\n\nThree.");

        // Assert
        var html = segments.Cast<HtmlMarkdownSegment>().ToArray();
        Assert.Equal(2, html.Length);
        Assert.Contains("One", html[0].Html);
        Assert.Contains("Two", html[0].Html);
        Assert.True(html[1].StartsBlock);
        Assert.Equal("<p>Three.</p>\n", html[1].Html);
    }

    [Fact]
    public void WhenPageTextContainsABreakMarker_ThenItIsNoBreak()
    {
        // Act
        var segments = Build("One <!--BREAK:00000000000000000000000000000000--> two.");

        // Assert
        var html = Assert.IsType<HtmlMarkdownSegment>(Assert.Single(segments));
        Assert.False(html.StartsBlock);
    }

    [Fact]
    public void WhenAWidgetBlockFollowsAParagraph_ThenAnEmptySegmentStartsIt()
    {
        // Act
        var segments = Build("First.\n\n:: Demo.Card\ninside\n::");

        // Assert
        Assert.Collection(segments,
            segment => Assert.False(Assert.IsType<HtmlMarkdownSegment>(segment).StartsBlock),
            segment => Assert.True(Assert.IsType<HtmlMarkdownSegment>(segment) is { Html: "", StartsBlock: true }),
            segment => Assert.IsType<BlockMarkdownSegment>(segment));
    }
```

Check the actual HTML Markdig gives for each input before trusting an expected string (for example the trailing newlines); fix an expectation only to match the same segmentation, never weaken it.

Also update existing tests in this file that assert exact segment counts or `Assert.Single` on multi-block pages, if any now fail because of the extra segments: verify each such failure is only the new split (same concatenated HTML via `AllHtml`) and adjust the assertion to the new segmentation.

- [ ] **Step 2: Run to see them fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests --filter "FullyQualifiedName~MarkdownSegmentBuilderTests"`
Expected: compile error (`StartsBlock` does not exist).

- [ ] **Step 3: Implement**

1. `public sealed record HtmlMarkdownSegment(string Html, bool StartsBlock = false) : MarkdownSegment;` with an XML doc on `StartsBlock`: "Whether the segment starts a top-level block of the page, so that its HTML does not depend on the HTML before it."
2. In `Build`, replace `renderer.Render(page.Document);` with a loop over the top-level blocks that writes `renderer.Write($"<!--BREAK:{token}-->")` before block `index` when `index > 0` and the break is allowed: `lastRawHtml < 0 || index <= firstRawHtml || index > lastRawHtml`, where `firstRawHtml`/`lastRawHtml` are the indexes of the first and last top-level block that is an `HtmlBlock` or has an `HtmlBlock` or `HtmlInline` among its descendants (`-1` when none). Comment why (Blazor parses each markup string on its own, so a tag opened in one block and closed in a later one would be closed early).
3. Extend `MarkerRegex` with an alternative `|<!--BREAK:([0-9a-f]{32})-->` (group 7), include group 7 in the forged-marker check, and in `Split` keep a `startsBlock` flag: on a break, set it; when adding an `HtmlMarkdownSegment`, pass and reset it; before adding an expression segment or pushing a widget block while it is set, add `new HtmlMarkdownSegment(string.Empty, StartsBlock: true)` and reset it. A break at the very end adds nothing.

Verify that rendering the blocks one by one gives the same HTML as rendering the document (footnotes, link reference definitions, front matter): the whole `HomeBlaze.Storage.Tests` suite covers pages and must stay green apart from segment-count assertions handled in Step 1.

- [ ] **Step 4: Run the tests**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests`
Expected: all pass, except `VerifyChecksTests.PublicApi`, which changes for the new record parameter; accept it by replacing `VerifyChecksTests.PublicApi.verified.txt` with the `.received.txt` and check the diff is only `StartsBlock`.

- [ ] **Step 5: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Storage/Pages/MarkdownSegmentBuilder.cs src/HomeBlaze/HomeBlaze.Storage.Tests
git commit -m "perf: mark where a top-level block of a page starts"
```

---

### Task 2: Carry `StartsBlock` to `HtmlSegment` and split the markup in the view

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.Storage/Internal/HtmlSegment.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Storage/Internal/MarkdownContentParser.cs` (`BuildChildrenAsync`, `AddHtml`)
- Modify: `src/HomeBlaze/HomeBlaze.Storage.Blazor/Files/MarkdownContentView.razor`
- Test: a parser test in `src/HomeBlaze/HomeBlaze.Storage.Tests` next to existing `HtmlSegment` assertions (e.g. `MarkdownFileTests.cs`)

- [ ] **Step 1: Write the failing test**

A test `WhenAPageHasTwoParagraphs_ThenTheSecondHtmlSegmentStartsABlock` that loads a page `"First.\n\nSecond."` the way existing tests in `MarkdownFileTests.cs` do, and asserts the `HtmlSegment` children in order have `StartsBlock` false then true. And `WhenOnlyStartsBlockChanges_ThenTheHtmlSegmentIsReplaced` if the existing tests have a pattern for reloading a page and comparing child instances (look for tests that check a child subject is kept across a reload); otherwise assert that the child keys of the two segments differ from the keys an unflagged segment with the same HTML and index would have, by reloading the page from `"First.\nSecond."` style content. Keep it to what the existing test patterns support.

- [ ] **Step 2: Run to see it fail** (compile error on `StartsBlock`).

- [ ] **Step 3: Implement**

1. `HtmlSegment`: add `public bool StartsBlock { get; }` and a constructor parameter `bool startsBlock = false`; doc: "Whether the segment starts a top-level block, so that the view can render it as markup of its own."
2. `MarkdownContentParser.BuildChildrenAsync`: `AddHtml(children, existingChildren, index, html.Html, html.StartsBlock)`; other `AddHtml` callers pass `false`.
3. `AddHtml`: include the flag in the key, e.g. `var key = $"{HtmlKeyPrefix}{index}_{(startsBlock ? "b" : "")}{ComputeHash(html)}";`, and create `new HtmlSegment(html, startsBlock)`.
4. `MarkdownContentView.razor`, in `GetRenderSegments`, for an `HtmlSegment`: when `html.StartsBlock` and the builder has content, add the builder's text as a `RenderSegment` and clear it before appending. Extend the existing comment: each top-level block is its own markup string, so a value change replaces only the block that shows it.

- [ ] **Step 4: Run**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests` and `dotnet test src/HomeBlaze/HomeBlaze.Storage.Blazor.Tests`
Expected: all pass (accept the public API snapshot again if `HtmlSegment` is in it).

- [ ] **Step 5: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Storage/Internal src/HomeBlaze/HomeBlaze.Storage.Blazor/Files/MarkdownContentView.razor src/HomeBlaze/HomeBlaze.Storage.Tests
git commit -m "perf: render each top-level block of a page as markup of its own"
```

---

### Task 3: E2E test that a static block keeps its element

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.E2E.Tests/LiveUpdateTests.cs`

- [ ] **Step 1: Write the test** using the file's `OpenPageAsync(content, initialize)` helper:

```csharp
    [Fact]
    public async Task WhenAValueChanges_ThenAStaticBlockOfThePageKeepsItsElement()
    {
        // Arrange
        var content = "Static paragraph.\n\n| Value |\n|---|\n| {{ $P/Text }} |\n";
        var (subject, page) = await OpenPageAsync(content, live => live.Text = "Before");
        var markdown = page.Locator(".markdown-content");
        await Assertions.Expect(markdown).ToContainTextAsync("Before", new() { Timeout = Timeout });
        var paragraph = markdown.Locator("p", new() { HasText = "Static paragraph." });
        await paragraph.EvaluateAsync("element => element.dataset.probe = 'kept'");

        // Act
        subject.Text = "After";
        await Assertions.Expect(markdown).ToContainTextAsync("After", new() { Timeout = Timeout });

        // Assert
        await Assertions.Expect(paragraph).ToHaveAttributeAsync("data-probe", "kept");
    }
```

(`dataset` is set from the browser, not by Blazor, so it only survives when the element is not replaced.)

- [ ] **Step 2: Verify it fails without the change**: temporarily revert `MarkdownContentView.razor` (`git stash push -m static-blocks-check -- src/HomeBlaze/HomeBlaze.Storage.Blazor/Files/MarkdownContentView.razor` is NOT allowed; instead copy the file aside, `git show HEAD~1:<path> > <path>`, run, then restore the copy), run `dotnet test src/HomeBlaze/HomeBlaze.E2E.Tests --filter "FullyQualifiedName~WhenAValueChanges_ThenAStaticBlockOfThePageKeepsItsElement"`, expect failure, restore, and confirm `git status` shows no change to the view.

- [ ] **Step 3: Run** `dotnet test src/HomeBlaze/HomeBlaze.E2E.Tests --filter "FullyQualifiedName~LiveUpdateTests|FullyQualifiedName~MarkdownBlockTests|FullyQualifiedName~MarkdownImageTests|FullyQualifiedName~PageEditTests|FullyQualifiedName~SpacingLayoutTests|FullyQualifiedName~AnchorScrollTests"`; all pass.

- [ ] **Step 4: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.E2E.Tests/LiveUpdateTests.cs
git commit -m "test: keep the element of a static block when a value changes"
```

---

### Task 4: Docs, plan removal, full verification and numbers

- [ ] Remove this plan and the spec (`git rm` both files under `src/HomeBlaze/HomeBlaze/Data/Files/Docs/plans/`) and their line in `src/HomeBlaze/HomeBlaze/Data/Files/Docs/home.md`. If `architecture/design/storage.md` describes how page HTML and expressions are joined into markup, add one sentence that each top-level block outside a raw HTML span is markup of its own. Commit `docs: static markdown blocks`.
- [ ] `dotnet build src/Namotion.Interceptor.slnx` (0 warnings) and `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"` (all pass).
- [ ] The whole HomeBlaze E2E suite: `dotnet test src/HomeBlaze/HomeBlaze.E2E.Tests` (all pass), since every page renders through the changed view.
- [ ] Numbers for the pull request, measured by the controller: re-inserts of a static block on the sample dashboard in 10 seconds, and received WebSocket bytes per 10 seconds, on master and on this branch.
