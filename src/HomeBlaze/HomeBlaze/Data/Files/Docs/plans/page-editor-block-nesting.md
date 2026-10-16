---
title: Block Nesting in the Page Editor
navTitle: Editor Block Nesting
status: Implemented
---

# Block Nesting in the Page Editor

## Problem

The text editor of a page tints the head line and the `json` block of a block in one royal blue, and expressions in the same blue. The closing line of a block gets no color. With cells, nodes, tabs and value lists inside each other, a page nests six levels deep and more, and it is hard to see which block a line belongs to and which block a closing line closes.

## Goal

Make the nesting of blocks readable in the editor with Monaco features only, without changing how pages are parsed, saved or edited.

- The head line and the closing line of a block share a color, and the color says the depth.
- A closing line says which block it closes.
- Expressions are told apart from blocks.
- A block can be collapsed, and the head lines of the blocks around the visible lines stay visible while scrolling.

Not in scope: selecting a block by clicking it, and changing the rendered page.

## Design

### Block outlines

`MarkdownBlock` already knows the last line of a block (`EndLine`) and whether it has a closing line of its own (`HasClosingLine`). Without a closing line, the last line is the last line of its content that is not empty, so siblings such as `::: Cell` lines that end each other get the right range. Both properties become public, because the editor lives in another assembly and only test projects get `InternalsVisibleTo`. The public API snapshot of `HomeBlaze.Storage` is updated.

`MarkdownDecorationParser.ParseDecorations` parses the page once and returns the existing regions, which the edit buttons hit-test against and which do not change, together with one outline per block. `Parse` still returns the regions alone.

- the first and the last line of the block, which the folding range uses,
- the depth of the block in the block tree, not its number of fence characters, because fences can skip lengths,
- the columns of the fence characters of the head line and of the closing line, when the block has one,
- the lines where the `json` block of the block starts and ends, when it has one,
- the name to show: the `#name` of the block, or else its type name without namespace, or the attributes of a style block.

The fence of a closing line is found from the end of the line, so a closing line inside a quote or a list item gets its color and label too.

Widget, slot and style blocks get an outline. Invalid blocks do not. A slot block gets an outline also when it created no subject, because only the edit button needs the subject.

### Decorations

All decorations are applied with the existing `DeltaDecorations` call.

- **Fence colors:** the fence characters of the head line and of the closing line get `InlineClassName` with the color of the depth, in bold. A closing line belongs to the one block with as many fence characters, also when it ends blocks inside it as well.
- **Head tint:** the head line and the `json` block keep their background, now in the color of the depth at low alpha.
- **Closer label:** the closing line shows the name of its block after the fence, dimmed and in italics, as injected text (`after`) on the fence decoration. A label needs a non-empty range, which the fence provides.
- **Expressions:** `{{ }}` get an amber tint of their own.
- **Colors:** five hues by depth (blue, teal, violet, pink, green), repeating from the sixth level, defined for the `vs-dark` and the `vs` theme. The CSS lives in the style block of `MarkdownFileEditComponent`.

### Folding and sticky scroll

The markdown language of Monaco has no folding for blocks. A folding range provider for `markdown` returns one range per block from its head line to its last line, and one per `json` block. BlazorMonaco does not wrap `registerFoldingRangeProvider`, so the JavaScript module next to the component (`MarkdownFileEditComponent.razor.js`) registers it once. The component sends the ranges of its model to the module when they change, and the module raises the `onDidChange` event of the provider, so the block rules stay in C# and are not written twice. A disposed component sends nothing more, because the next page editor gets the same editor id.

The editor turns on folding with the controls always shown and sticky scroll with `foldingProviderModel`, so the head lines of the blocks around the visible lines stay at the top.

The provider is registered per language and so applies to every markdown editor of the app. It answers for the models that a page editor sent ranges for and forgets a model when Monaco disposes it. For any other model, and for a page without blocks, it returns no result and not an empty list, so Monaco keeps folding by indentation there.

## Testing

- Unit tests of the outlines and the folding lines in `HomeBlaze.Storage.Blazor.Tests`: a closing line of its own, siblings that end each other, one closing line ending several levels, fences that skip lengths, fence characters inside code, a closing line inside a quote, Windows line endings, the `json` block right after the head line and after an empty line, and the label names.
- The existing tests of the regions and the editor E2E tests (`MarkdownEditorTests`, `MarkdownBlockTests`, `LayoutBlockSaveTests`) stay unchanged and pass.
- The page editor is checked by hand on the demo pages in both themes. A preview of the look ran in plain Monaco before the change. Rails in the gutter, one per open block, were tried and left out: the fence colors, the labels and sticky scroll show the nesting with less noise.

## Risk

The change only adds decorations, editor options and a folding provider. Parsing, saving and the edit buttons are unchanged. The visible API change is two read-only properties of `MarkdownBlock`.
