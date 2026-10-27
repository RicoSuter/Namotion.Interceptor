# Static Markdown Blocks

A value change on a page replaces only the HTML of the top-level Markdown block that shows it, not every block between two widgets.

## Problem

`MarkdownContentView` joins the HTML segments and expressions between two widgets into one `MarkupString`, because an expression can sit inside an HTML attribute and a segment can be a partial tag. Blazor replaces a markup frame whole when its string changes. So on a page whose live table and a picture, a long text or a diagram are in the same run, every value change re-sends that whole run over the circuit and replaces all of it in the DOM. Measured on a dashboard with a diagram next to a live table: the diagram's element is replaced about 4 times per second.

## Design

1. **Break markers.** `MarkdownSegmentBuilder.Build` renders the top-level blocks of the document one by one instead of the document at once, and writes `<!--BREAK:{token}-->` between two blocks where a break is allowed. The token is the one the block and expression markers already carry, so a marker-shaped comment in the page text does not count.
2. **Segments.** `Split` turns a break marker into a flag on the next `HtmlMarkdownSegment`: `HtmlMarkdownSegment(string Html, bool StartsBlock = false)`. When the next segment is an expression or a widget block, it emits an empty `HtmlMarkdownSegment("", StartsBlock: true)` so the break is not lost. Slot logic already ignores empty HTML segments.
3. **Subjects.** `HtmlSegment` gets `StartsBlock`, set from the segment. The flag is part of the child key in `MarkdownContentParser.AddHtml`, so a change of the flag replaces the subject.
4. **View.** `MarkdownContentView` flushes its string builder before an `HtmlSegment` with `StartsBlock`. Each top-level block then becomes its own `MarkupString`, and Blazor leaves a block whose string did not change untouched.
5. **Raw HTML.** Blazor parses each `MarkupString` on its own, so a `<div>` opened in one block and closed in a later one would be closed early if a break fell between them. No break is written between the first and the last top-level block that contains raw HTML (an HTML block, or inline HTML in a paragraph or another leaf). A page without raw HTML gets a break between every two top-level blocks; a page with raw HTML gets them only before the first and after the last such block.

Out of scope: breaks inside the content of a widget block (`MarkdownFragment`), which keeps today's behavior. It can reuse the same mechanism later.

## Correctness

- The HTML of the page is the same as today, apart from the break comments, which are removed in `Split`. Rendering block by block must give the same HTML as rendering the document; the existing segment builder and page tests cover this.
- An expression inside an HTML attribute stays inside its block, so its block is still one string.
- Widgets already end a run, so their behavior does not change.

## Tests

Unit tests in `HomeBlaze.Storage.Tests/Pages/MarkdownSegmentBuilderTests.cs`:

- Two paragraphs give two HTML segments, the second with `StartsBlock`.
- A paragraph, a raw `<div>` block, a paragraph and a `</div>` block give no break between the `<div>` and the `</div>`, and a break before the `<div>` block.
- A forged `<!--BREAK:...-->` in the page text is kept as text and is no break.
- A block that starts with an expression gets an empty segment with `StartsBlock` before the expression.

A unit test in `HomeBlaze.Storage.Tests` that the parser gives `HtmlSegment.StartsBlock` from the segment and a new subject when only the flag changes.

E2E test in `HomeBlaze.E2E.Tests/LiveUpdateTests.cs`: on a page with a static paragraph before a table with a live value, the paragraph's DOM element is the same element after the value has changed (an expando property set on it before the change is still there).

Measurement for the pull request: the dashboard probe (re-inserts of a static block in 10 seconds, before and after) and the bytes per value update on the circuit, before and after.
