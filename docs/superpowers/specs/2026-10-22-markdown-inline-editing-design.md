# Inline editing of markdown pages: structure, text, hidden blocks and the split preview

## Goal

Make the Inline mode of a markdown page a complete editor for its blocks and its text: every block can be reached, edited, deleted, moved and inserted on the page itself, including blocks that render nothing and blocks that are hidden at the current width, and the items of list slots (ValueList rows, ValueMap cases, tabs, flow items, history chart series). The top-level blocks of Markdown text (paragraphs, headings, lists, tables, quotes, code) can be selected, edited, deleted and inserted as well. The Split mode stops writing from its preview, so that it can no longer overwrite the unsaved text of its editor. The source editor shows the names of blocks in bold.

The work lands in two phases of one pull request (#785). Phase 1 delivered delete, move, insert, list slot items, chips, the Responsive chip, the view-only Split preview and bold block names. Phase 2 changes how subjects are keyed and kept across the reloads of a page, turns a move into a cut and an insert, puts chips in place, makes insert lines overlays, makes the Split preview inert, adds text editing, and simplifies what phase 1 left duplicated. This document describes the result of both phases.

## Current state (before phase 2)

- Inline mode makes every block widget selectable (`SubjectComponent`, `WidgetSelection`). A selected widget gets an action bar with Select parent, Move up, Move down, an Add button per list slot, Edit and Delete. The structure edits run through `MarkdownFile` (`DeleteBlockAsync`, `MoveBlockAsync`, `InsertWidgetAsync`, `AddSlotItemAsync`) and `IPageStructureEditor`.
- A widget without a name is keyed by its type and its position among all segments of its content: every stretch of HTML, every expression and every block counts. Adding text before such a block creates its subject again.
- The reload inside the write of a structure edit finds subjects by those keys, so a moved or inserted unnamed block hands its subject to a neighbor: after a move the selection stays at the old place and shows the other block, and a second Move puts the block back.
- The action bar reads the parse snapshot, which is not tracked, so its Move buttons go stale after a structure change until the widget is selected again.
- A structure edit computes the new text from the snapshot. A change of the file that was not read yet (an external editor, a `git pull`) is overwritten.
- Move swaps the block with its sibling, and the alignment cannot check the text between the two.
- A ValueMap chip floats at the top right of its content, which changes the layout of Inline mode against View mode.
- Insert lines take about 36 pixels of height and appear with the selection, so the content moves under the pointer.
- The Split preview stops the pointer with CSS and disables the controls that read `IsReadOnly`, but widgets that ignore it (Sonos, FritzBox, Shelly, Ecowitt, motor) still take the keyboard.
- The text between blocks cannot be edited on the page.

## Decisions

| Topic | Decision |
| --- | --- |
| Delete | In the action bar. A confirmation dialog names the block and, for a container, the number of blocks inside it. A text block is deleted the same way ("Delete this text?"). |
| Move | Up and down buttons in the action bar. A move cuts the block and inserts it at the previous or the next insert point of its content, which are the places of the plus lines: before the item before it, or after the item after it, where an item is a block or a text block of the same content between the slot blocks around the block, so that a block never passes a slot block of its owner. A chip of a widget that renders nothing, or of a Responsive whose content is hidden, is an item like any other, so one click passes one chip. The text around the block stays in place relative to its neighbors. A slot block (row, case, tab, panel) moves among the blocks of its slot. The cut and the insert are each checked by the alignment, so together everything outside the cut and the insert lines is compared. No drag and drop, no move into another content. |
| Subject keys | A widget without a `#name` is keyed by its type and its position among the block segments of its content. Text and expressions around it do not count. The rule of the write operations that refused adding text or expressions before such a block is no longer needed and is removed, which makes the agent rule on unnamed blocks looser. |
| Subject identity | A reload of exactly the text that the current snapshot describes, which is the read inside the write of an edit, takes the subjects of that snapshot by block index instead of by key. A structure edit has already mapped them to their new blocks, so a moved block keeps its subject, and the blocks after an inserted or removed block keep theirs. Every other reload finds subjects by key. |
| Results and reselection | The structure and text edits of `MarkdownFile` return a `MarkdownEditResult` that names the subject of the moved or inserted block, or the first text block that was written, once the page has read the written text. The editor of the page selects it. The action bar renders again after every reload of the page, so its buttons match the page. |
| Editor API | `IPageStructureEditor` becomes the abstract class `PageStructureEditor`. Members that later work adds, such as the text members, have a default that does nothing, so that another implementer does not break. |
| Changed file | A structure or text edit reads the file under the write lock and refuses when it differs from the source of the snapshot, and reads the page again. The refusal is "The text changed since you opened it." for a text edit, "The page changed since you opened it." for an insert before a text block, and "The page changed while it was edited. Try again." for every other edit. No version argument is added (see "Addressing"). |
| Invisible widgets | A chip with icon and name (the `#id`, otherwise the type name) in place, where its block is, like the Responsive chip. |
| Hidden Responsive content | Collapsed to a chip in place, shown only at the widths where the content is hidden. Clicking the chip selects the Responsive block and shows its content dimmed while the selection is on it or inside it. |
| Insert between blocks | Plus lines in the content where the selection is: the page while nothing is selected, otherwise the content of the selected widget and the content that directly holds it. A line stands before every item and at the end. Lines are overlays that take no height, so the content does not move when the selection changes. Each line has a unique accessible name. A click opens a menu with "Widget..." and "Text...". No plus lines in the content of a grid cell or a canvas node, which keep their own buttons and whose content fills the place. |
| Insert into list slots | An "Add ..." button per list slot of the selected container, named after the slot (Add Tab, Add Item, Add Value, Add Case, Add Series). |
| ValueList rows | Selectable once the list or one of its rows is selected (the first click selects the list, a further click a row), with their own action bar. "Add value" at the end of the list. |
| ValueMap cases | Listed in an overlay below the chip of the selected ValueMap, selectable once the map or one of its cases is selected, each with edit, move and delete. "Add case" is offered both in the action bar of the map and at the end of the case list. |
| Text blocks | Every top-level Markdig block of a content (the page, or the content of a widget or slot block) is a text block: a paragraph, a heading, a list, a table, a quote, a code block, a thematic break. In Inline mode it is selectable like a ValueList row: once its content is at the selection. Its bar has Edit and Delete. Edit opens a dialog with a monospace text field with the Markdown of the block, and "Text..." of a plus line opens the same dialog empty. |
| Split preview | View-only: its content is `inert`, so neither the pointer nor the keyboard reaches a control of any widget. A click on the preview is passed to the widget under the pointer, which reveals its block in the source editor and never writes. When the text before that block has unsaved changes, a warning asks to save first. `IsReadOnly` stays as a second guard on the controls. Grid and canvas editing happens in Inline mode only. |
| Block names in the source editor | The name after the head fence is bold. The fence keeps its depth color and is no longer bold. A `#name` and the arguments stay regular. |
| Write path | One transaction pipeline in `MarkdownFile` for every change of the page from the graph or the page UI, with pure text operations shared with the source editor. |

## Architecture

```
DeleteBlockAsync, MoveBlockAsync,              WriteConfigurationAsync
InsertWidgetAsync, AddSlotItemAsync,           (edit dialog save)
ReplaceTextBlockAsync, InsertTextAsync                 |
                 |                                     v
                 v                              WriteBlockAsync
  EditStructureAsync(operation)                 MarkdownBlockReconciler
  file check, operation(snapshot)               -> MarkdownBlockWrite or refusal
  -> MarkdownPageOperations                            |
     TryRemove, TryMove, TryInsertWidget,              |
     TryAppendSlotBlock, TryReplaceText,               |
     TryRemoveText, TryInsertText                      |
                  \                                    /
                   v                                  v
          MarkdownFile.PublishAndWriteAsync     <- stale check, publish,
                                                   reload bookkeeping, write
```

### Write pipeline: the shared `PublishAndWriteAsync` step

`MarkdownFile` runs every change of the page from the graph or the page UI through one step, `PublishAndWriteAsync`. The structure and text edits run an operation over the snapshot (the parsed page, the subjects of its blocks and its text blocks) in `EditStructureAsync`, and `WriteConfigurationAsync` runs the reconciler in `WriteBlockAsync`. Both produce a `MarkdownBlockWrite` or a refusal and keep this order:

1. Takes the write lock of the page and rejects calls from within a write or from the worker of the storage.
2. A structure or text edit reads the file and refuses when it differs from the source of the snapshot, after reading the page again. A configuration write keeps working from the snapshot.
3. Reads the snapshot and runs the operation.
4. On a refusal, logs it. A configuration write also forgets the applied state of the affected blocks and reads the page again, so that the graph matches the text. A structure or text operation changes only text, so its refusal needs no reload.
5. Refuses an insert or removal while the graph is ahead of the snapshot.
6. Publishes the new snapshot with a compare-exchange, and refuses when a reload published in the meantime. The published snapshot has no text blocks: their lines are those of the old text, and the read inside the write finds the new ones.
7. Forgets the applied state of the blocks around the change, remembers the written configurations, and writes the file.
8. When the storage fails the write, puts the snapshot and the content back while no reload replaced them, forgets the applied state of the subjects whose configuration it wrote, reads the page again from its file, and lets the exception of the storage propagate.

Steps 1 to 4 are in `EditStructureAsync` and `WriteBlockAsync`, steps 5 to 8 in `PublishAndWriteAsync`. `WriteConfigurationAsync` keeps returning a bool. The structure and text edits return a `MarkdownEditResult` with the refusal reason, and, once the page describes the written text, the subject of the moved or inserted block or the first segment of the written text, so that the UI can select it.

The file also offers read-only queries over the current snapshot for the UI: `GetBlockActions` (whether a subject has a block, whether it can move up or down, how many blocks it contains), `GetListSlots`, `GetBlockName`, `IsContentOfPage`, `GetTextBlockText` (the Markdown of a text block, null when it cannot be edited) and `CanInsertTextAt` (false after a block without a closing line, which would take the text). `GetBlockActions` reads the tracked `Children` of the page, so a component that renders it renders again after every reload.

### Subject keys and identity

A parse finds the subject of a block again, instead of creating it anew, in one of two ways:

- **By key.** The key of a widget block is its `#name`, or its type name and its position among the block segments of its content (`MarkdownSubjectKeys.GetContentKey`). The values of a typed list slot are found by their name, and without one by their position among the values without a name. `MarkdownSubjectKeys.Walk` and the write rules use the same keys. Because only blocks count, the write rules no longer need to keep text and expressions away from the place before an unnamed block: the units of the page (its blocks, in order) already keep every block position.
- **By the snapshot.** When the text to parse equals the source of the current snapshot, the parse takes the subject of every block from the snapshot by block index, and creates a subject only for a block that has none there. A structure edit publishes such a snapshot before it writes, with the subjects carried to their new blocks by the alignment, so the read inside the write keeps the subject of every block that the edit kept or moved. A subject whose type does not match is not reused. HTML segments and expressions are still found by key, and the fragments of open list slots by position.

The lifecycle of the page keeps a subject that changes its key in `Children`, so a block that moves keeps running.

### Text operations: `MarkdownPageOperations`

Pure functions over a parsed `MarkdownPage`, in HomeBlaze.Storage next to `MarkdownBlockWriter`.

- **Remove(block)**: removes the block with its content and the blank line that separated it.
- **Move(block, direction)**: `FindMoveLine` lists the items of the content of the block (its widget blocks and its text blocks between the slot blocks around the block, or for a slot block the blocks of the same slot) and gets the first line of the item before, or the line after the item after. The block is cut (`CutLines`, which keeps the text around it apart as before) and inserted at that line (`InsertMovedLines`, which separates it with empty lines when it stood apart before, or when its neighbor is text). Each step is checked with `MarkdownBlockAlignment`, and the maps of the two steps are composed, with the subjects of the moved blocks carried to their new indices.
- **Insert widget(owner, line, block text)**: inserts a new block into the content of the owner (a block, or the page itself) before a line: the first line of the item to insert before, or the end of the content, which is before its first slot block (`GetInsertLine`). The fence length and the closing lines follow the block beside it and the style of the page.
- **Append slot block(owner, slot)**: as before, after the last block of the slot.
- **Replace text(text block, text)**: replaces the lines of the text block with the text, with the line endings of the page and the indentation of its owner, separated by an empty line from a line of text before or after it. Empty text removes the block.
- **Remove text(text block)**: cuts its lines like a moved block.
- **Insert text(owner, line, text)**: inserts the text before the line, with empty lines around it, like a widget.
- Every operation parses its result and checks that exactly the expected blocks were removed, moved or inserted and that the Markdown document outside the changed lines is the same, and refuses otherwise ("would change other blocks of the page"). A text operation inserts no block. The alignment accepts that an owner without a closing line ends on the new lines when the change replaced its last content.

The Create button of the source editor keeps writing into its unsaved buffer at the cursor with the same `InsertBlockText` helper that an insert uses.

### Addressing

The UI addresses a block by its subject. A widget, a ValueList row (a `ValueRow`), a ValueMap case and a tab are each the subject of a block. A text block is addressed by the `HtmlSegment` that starts it in the children of its content. An insert addresses a content by its owner subject (or the page) and the item to insert before, a block subject or the segment of a text block, or null for the end.

No version argument is added to the public methods. The file check of step 2 keeps an edit from writing over text it has not seen. The subject of a block stays with its block across the reloads of the page's own edits (see "Subject keys and identity"). An external change can give an unnamed subject the block of its new position, but the page renders again with it, and the selected widget then shows that block, so a command acts on what the selection shows at the click. The one window that is left, a confirmation or setup dialog that stays open while the page changes, is closed by the editor: it remembers the text of the page when it opens the dialog and refuses ("The page changed while the dialog was open. Try again.") when the text differs after it. A text edit carries the text that the dialog showed, which the file compares under the write lock ("The text changed since you opened it."). The dialog of "Text..." remembers the text of the page when it opens and does not save when the text differs after it.

## Text editing

### Text blocks

`MarkdownTextBlocks.Find` lists the top-level content of the page and of every widget and slot block that is not a block, in the order of the page. Each entry has its first Markdig block, its first line, its last line that is not empty, its owner block and whether it can be edited. The json block of a block is its configuration and is left out. Content that renders nothing (blank lines, link reference definitions, the front matter) has no entry. A run of content between two blocks that holds raw HTML is one entry that cannot be edited, because raw HTML can open an element in one block and close it in a later one. Style blocks, error blocks, footnotes, old `subject(name)` fences and Markdig containers with a block inside (a list or a quote) are entries that cannot be edited.

### Segments and the snapshot

`MarkdownSegmentBuilder.Build` takes `markTextBlocks`. With it, the renderer writes a marker before every entry, directly to its writer, so that the HTML it renders around it does not change. `Split` takes the markers out and starts a new `HtmlMarkdownSegment` at each, which carries its entry. The content parser builds with markers, gives every such segment an `HtmlSegment` whose `Start` is `Text` or `Other`, and every other one `None`, and keeps the entries by their `HtmlSegment` in the snapshot (`MarkdownParseResult.TextBlocks`). A view of the page joins the HTML of its segments as before, so View mode renders the same HTML byte for byte. Unnamed widgets are keyed by their block position, so the extra segments change no key of a widget.

### Edit mode

`MarkdownContentView` groups its children in Inline mode, outside a grid cell or a canvas node: an `HtmlSegment` with a `Start` other than `None` starts a group, and the HTML and expressions after it, up to the next start or widget, belong to it. A text group renders as a `SubjectComponent` of its first segment with `MarkdownTextWidget`, which shows the joined HTML and marks itself with `ITextBlockWidget`, so that the wrapper can select it although it has no configuration. It is selectable while its content is at the selection, like a row in a selected list. Another group renders as plain markup. On the page the wrapper of a text block takes no margin, so the margins of its HTML apply as in View mode. In a container the wrapper takes the margins that the text takes in View mode, and the text takes none.

### UI

- The action bar of a selected text block has Select parent, Edit and Delete (`PageBlockActions.CanEditText`). Enter on a selected text block opens the dialog as well.
- `MarkdownTextDialog` shows the Markdown in a monospace text field that grows with its text. Save writes through the file and closes the dialog. A refusal or a failed write shows in the dialog and keeps what was typed. When the text of the block changed, the dialog says "The text changed since you opened it. Copy your text, close the dialog and open the text again.", and when the page changed while "Text..." was open, "The page changed since you opened the dialog. Copy your text, close the dialog and insert it again."
- A plus line opens a menu with "Widget..." and "Text...". "Text..." is left out where `CanInsertTextAt` is false.
- After a text edit or insert, the editor selects the text block that the result names.

### Rules

An edit in the UI follows the rules of the source editor: the content size limit of `MarkdownFile`, which refuses an edit that makes the page larger than the limit and larger than it was, and none of the write rules that the Markdown tools of agents follow. The Markdown tools are unchanged.

### Not in v1

Editing a single list item, raw HTML and the text around it, footnote definitions, the text of style blocks and of grid cells and canvas nodes, moving text blocks, in-place editing, drag and drop, and undo.

## UI

### `PageStructureEditor`

An abstract class in HomeBlaze.Components, cascaded to the widgets of a page. It answers `GetActions(subject)`, `GetListSlots(owner)`, `GetBlockName(subject)`, `CanInsertInto(content)` and `CanInsertText(content, before)`, and performs `DeleteAsync(subject)`, `MoveAsync(subject, direction)`, `InsertWidgetAsync(content, before)`, `AddListItemAsync(owner, slot)`, `EditTextAsync(subject)` and `InsertTextAsync(content, before)`. The text members are virtual with a default that does nothing. Where no editor is cascaded, no structure button renders: in View mode, in the Split preview and on pages that are not markdown.

`MarkdownPageStructureEditor` in HomeBlaze.Storage.Blazor implements it on top of a `MarkdownFile`. `MarkdownFilePageComponent` creates one per page, selection and page component, and cascades it in Inline mode only. Its queries read the snapshot of the file. Its commands show the dialogs, call the file, show a refusal as a warning snackbar (or in the text dialog), and select the subject that the result names with `WidgetSelection.SelectSubject`, which selects the widget of the subject once it renders when the reload has not rendered it yet.

### Selectable rows and cases

There is no separate selectable frame. The selection outline, the keyboard handling, the action bar and the chain to the selectable parent stay in `SubjectComponent`, and a row, a case and a text block are selected like any widget. The action bar holds, each only when it applies: Select parent, Move up, Move down, an Add button per list slot, Edit and Delete.

### Chips of invisible widgets

A widget that renders nothing on a viewed page, today only `ValueMapWidget`, renders a chip in place in edit mode, where its block is, and its wrapper is as wide as the chip. In view mode its wrapper is empty and hidden. A selected ValueMap opens the overlay with its cases below the chip.

### Collapsed Responsive content

In edit mode `ResponsiveWidget` renders a chip in place with the inverse display classes of its content, for example "Responsive · Xs only", so that it shows exactly at the widths where the content is hidden. Clicking the chip selects the Responsive block. While the selection is on it or inside it, its content shows dimmed at every width and its blocks are selectable.

### Insert points

`MarkdownContentView` renders a plus line before every item of its content and one at the end, in the content where the selection is. A line is a zero-height element whose bar is drawn over the gap above the next item, so the content does not move when lines show or hide. Its accessible name is its position and its content, for example "Insert at position 3 of the page" or "Insert at the end of Phone Card". In the row of a card footer a line takes none of the gap of the row.

### Split preview

`Pages.razor` gives the preview `IsEditing=false`, no structure editor and `IsReadOnly=true`, wraps the page in an element with `inert`, and cascades itself as the `ISubjectRevealTarget` of the preview. A click lands on the preview itself, and a script passes it to the revealable wrapper under the pointer. The wrapper asks the source editor to reveal the block of its subject, which puts the cursor on the head line, scrolls it into view and highlights the block. When the unsaved text no longer has that block, the click does nothing.

### Block names in the source editor

`MarkdownDecorationParser` records where the name of a head line starts and ends: after the fence and its spaces, up to the first `#`, space or end of line. `BlockOutlineDecorations` writes a `NameKind` record for it, and the editor module turns it into a `.block-name` decoration with `font-weight: bold`. `.block-fence` keeps its color and loses `font-weight: bold`.

## Refusals and errors

An operation refuses and writes nothing when:

- the file differs from the source of the snapshot,
- the snapshot changed while the edit ran (the compare-exchange fails),
- the subject has no block in the snapshot, or the segment starts no text block that can be edited,
- the text of the text block is not the text that the dialog showed,
- blocks would be inserted or removed while the graph is ahead of the snapshot,
- a move has no item in its direction,
- the page would be larger than the content size limit and larger than it was,
- the parsed result differs from the expected change in other blocks or in the document outside the changed lines.

The structure and text operations change only text, so a refusal leaves the graph as it was. A write that the storage cannot make, for example a file held open by another process, a storage that does not answer or is not connected, or a file that may not be written, is logged and shows an error message instead of breaking the page. The page then describes its file again.

The UI shows the reason as a warning snackbar after "The page was not changed.", for example "The page was not changed. The page changed while it was edited. Try again.", and a failed write as an error snackbar after "The page could not be saved.". The text dialog shows either above the text it keeps. Because the Split preview never writes, it cannot race the editor buffer.

## Testing

- **Keys and identity**, in HomeBlaze.Storage.Tests: text and expressions added before an unnamed block keep its subject; after a move, insert and delete of unnamed blocks through the structure edits every other subject is the same instance (`Assert.Same`); a reload of a changed file still finds subjects by key; the Markdown tools accept text before an unnamed block.
- **`MarkdownPageOperations`**, text in and text out: remove, move (past a widget, past text, past a skipped widget, slot blocks, blocks without closing lines, CRLF, the end of the text without a line ending) and insert for top-level blocks, blocks in content slots and list slots, and the text operations (replace on the page and in a card, a closing-less owner, several paragraphs, removal, insert, refusals for a code fence and a head line).
- **`MarkdownTextBlocks`**: the entries and their lines for every kind of text, raw HTML runs, front matter, footnotes and blocks inside lists.
- **Segments**: the markers and their entries, and for every page of the repository the same HTML with and without markers.
- **The edits of `MarkdownFile`**: a changed file is refused and read again, results name the moved and the inserted subject, stale and gone text is refused, text is inserted before a widget and before text, and `CanInsertTextAt` is false after a block without a closing line.
- **Decorations**: `BlockOutlineDecorations` writes the name range for `Tab`, `Tab#cooling` and a head line with arguments.
- **E2E**, in HomeBlaze.E2E.Tests, since the repository has no bUnit: everything of phase 1, and a moved row stays selected and its bar offers Move up; a widget moved down twice passes two items and stays selected; a move passes a chip; an inserted widget is selected; the ValueMap chip stands where its block is; selecting a card moves nothing on the page; insert lines have unique names; the Split preview content is inert; View mode renders a seeded page byte-identical to a golden file; a paragraph on the page and in a card body is edited through the dialog; a text block is deleted; text is inserted from a plus line; a stale text edit is refused in the dialog.

## Out of scope and follow-ups

- Drag and drop, undo, moving a block into another slot or another container.
- The text editing exclusions of v1 (see "Not in v1").
- The source editor writes its buffer without comparing it to the file, so a Split save can revert an inline edit or an agent edit made since the buffer was loaded. It should compare on save and refresh a clean buffer (challenger finding 5).
- A write that fails part way leaves a half-written file that the read after it trusts. The storage should write to a temporary file and rename it, and retry a write that meets a sharing violation (challenger finding 6).
- Keyboard shortcuts beyond Enter (Escape, Delete, Alt+Arrow) and keeping the focus on a moved block (challenger finding 10). Unique accessible names of the insert lines are done.
- Pages without closing lines show Move and insert actions that the alignment then refuses. A dry run per snapshot, or closing lines for new blocks, would hide them (challenger finding 7).
- `PageListSlot` (HomeBlaze.Components) and `MarkdownListSlot` (HomeBlaze.Storage) stay two records with a mapping. Unifying them moves a public type and needs approval.
