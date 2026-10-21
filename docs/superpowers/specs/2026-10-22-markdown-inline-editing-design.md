# Inline editing of markdown pages: structure, hidden blocks and the split preview

## Goal

Make the Inline mode of a markdown page a complete editor for its blocks: every block can be reached, edited, deleted, moved and inserted on the page itself, including blocks that render nothing and blocks that are hidden at the current width, and the items of list slots (ValueList rows, ValueMap cases, tabs, flow items, history chart series). The Split mode stops writing from its preview, so that it can no longer overwrite the unsaved text of its editor. The source editor shows the names of blocks in bold.

Editing the markdown text between blocks (paragraphs, headings, lists) is out of scope and gets its own spec: it needs the source positions of the text segments, which the page does not keep today.

## Current state

- Inline mode makes every block widget selectable (`SubjectComponent`, `WidgetSelection`). A selected widget gets an action bar with "Select parent" and "Edit". The edit dialog saves through `MarkdownFile.WriteConfigurationAsync`, which runs `MarkdownBlockReconciler` under the write lock of the page.
- There is no delete, move or insert in Inline mode. Inserting is possible in the source editor (the Create button, `SubjectSetupDialog`) and in the empty cells of a grid.
- A widget that renders nothing, such as ValueMap, has an empty wrapper that CSS hides, so it cannot be selected. The content of a Responsive block is hidden by display classes at the widths it is not for, so it cannot be selected at those widths.
- ValueList rows and ValueMap cases are blocks of the page (`::: Value`, `::: Case`), but they are not selectable on their own.
- In Split mode the preview receives `IsEditing` and the widget selection like Inline mode. A save of the edit dialog writes the file, while the Monaco editor keeps its own unsaved copy of the text. The preview shows the saved file, not that copy. Whichever is saved last overwrites the other.
- The source editor makes the fence of a head line bold and colors it by depth (`.block-fence`). The name of the block has no style of its own.

## Decisions

| Topic | Decision |
| --- | --- |
| Delete | In the action bar. A confirmation dialog names the block and, for a container, the number of blocks inside it. |
| Move | Up and down buttons in the action bar, within the same slot. No drag and drop. |
| Invisible widgets | A chip with icon and name (the `#id`, otherwise the type name), stacked vertically at the top right of the content area the block stands in. At page level the stack starts below the page menu button. |
| Hidden Responsive content | Collapsed to a chip in place, shown only at the widths where the content is hidden. Clicking the chip selects the Responsive block and shows its content dimmed while the selection is on it or inside it. |
| Insert between blocks | Plus lines between the blocks of the current scope: the page when nothing is selected, otherwise the innermost content area that holds the selection. |
| Insert into list slots | An "Add ..." button per list slot of the selected container (Add tab, Add item, Add value, Add case, Add series). |
| ValueList rows | Selectable inside a selected list, with their own action bar. "Add value" at the end of the list. |
| ValueMap cases | Listed in a panel below the selected ValueMap chip, each with edit, move and delete. "Add case" at the end of the panel. |
| Split preview | View-only: no selection and no structure actions. A click on a widget reveals its block in the source editor and never writes. |
| Block names in the source editor | The name after the head fence is bold. The fence keeps its depth color and is no longer bold. A `#name` and the arguments stay regular. |
| Write path | One transaction pipeline in `MarkdownFile` for every change of the page from the graph or the page UI, with pure text operations shared with the source editor. |

## Architecture

```
Inline buttons (delete, move, insert)      Edit dialog save
                 \                             /
                  v                           v
          MarkdownFile.EditAsync(operation)     <- lock, snapshot, stale check,
                         |                         reload bookkeeping, write
                         v
       operation(page, blockSubjects) -> MarkdownBlockWrite or refusal
                         |
       MarkdownPageOperations (text in, text out)   <- also used by the
         Remove, Move, Insert, WriteConfiguration      source editor's Create
```

### Write pipeline: `MarkdownFile.EditAsync`

`WriteBlockAsync` of `MarkdownFile` becomes a generic transaction that takes an operation. The operation receives the snapshot (the parsed page and the subjects of its blocks) and returns a `MarkdownBlockWrite` or a refusal. The transaction keeps what `WriteBlockAsync` does today, unchanged in order:

1. Takes the write lock of the page and rejects calls from within a write or from the worker of the storage.
2. Reads the snapshot and runs the operation.
3. On a refusal, logs it, forgets the applied state of the affected blocks and reads the page again, so that the graph matches the text.
4. Refuses an insert or removal while the graph is ahead of the snapshot.
5. Publishes the new snapshot with a compare-exchange, and refuses when a reload published in the meantime.
6. Forgets the applied state of the blocks around the change, remembers the written configurations, and writes the file.

`WriteConfigurationAsync` becomes `EditAsync` with the reconciler as its operation, and keeps its documented behavior. Three new public methods run the new operations: `DeleteBlockAsync(subject)`, `MoveBlockAsync(subject, direction)` and `InsertBlockAsync(target, subject)`. They return a result with the refusal reason, so that the UI can show it. `WriteConfigurationAsync` keeps returning a bool.

The pipeline also offers read-only queries over the current snapshot for the UI: whether a subject has a block, whether that block has a previous or a next sibling in the same slot, how many blocks it contains, and which slots its block offers for inserting.

### Text operations: `MarkdownPageOperations`

Pure functions over a parsed `MarkdownPage`, in HomeBlaze.Storage next to `MarkdownBlockWriter`, which already has most of the parts (`RemoveBlock`, `CreateBlock`, `GetSlotBlockPlacement`, `InsertLines`, `GetLineEnding`).

- **Remove(block)**: removes the block with its content and the blank line that separated it, as `RemoveBlock` does today.
- **Move(block, direction)**: swaps the text of the block with the text of the adjacent block of the same slot of the same owner. The text between them stays where it is. Refuses at the first or the last sibling and when the adjacent block belongs to another slot.
- **Insert(target, block text)**: inserts a new block into a slot. The target is the owner (a block, or the page itself), the slot name (null for the content of the owner) and the sibling to insert after (null for the start). The fence length follows the depth and the style of the page (closing lines or not), as `GetSlotBlockPlacement` and `GetFenceLengthForNewBlock` decide today. Blank lines keep the text readable, as the source editor does today.
- Every operation parses its result and checks that exactly the expected blocks were removed, moved or inserted, and refuses otherwise ("would change other blocks of the page"), as the reconciler does.

The Create button of the source editor keeps writing into its unsaved buffer at the cursor (`GetLineForNewBlock`), but its line splicing and blank line handling move into a helper of `MarkdownPageOperations` that **Insert** uses as well, so that both paths write a new block the same way.

### Addressing

The UI addresses a block by its subject. A widget, a ValueList row (a `ValueRow`), a ValueMap case, a tab and a flow item are each the subject of a block. The pipeline finds the block of a subject in the snapshot, as `FindBlock` does today. An insert addresses a slot by its owner subject (or the page), the slot name and the sibling subject to insert after.

## UI

### `IPageStructureEditor`

A new interface in HomeBlaze.Components, cascaded to the widgets of a page. It answers `CanDelete(subject)`, `CanMove(subject, direction)` and `GetInsertSlots(owner)` and performs `DeleteAsync`, `MoveAsync` and `InsertAsync(owner, slot, after)`. Where no editor is cascaded, no structure button renders: in view mode, in the Split preview and on pages that are not markdown. Grid cells and canvas nodes keep their own `LayoutItemActions`.

`MarkdownPageStructureEditor` in HomeBlaze.Storage.Blazor implements it on top of a `MarkdownFile`. `MarkdownFilePageComponent` creates one per page and cascades it in Inline mode only. Its queries read the snapshot of the file. Its commands show the delete confirmation, open `SubjectSetupDialog` for an insert, call the file, and show a refusal as a warning snackbar.

### `SelectableFrame`

The selection outline, the keyboard handling, the action bar and the chain to the selectable parent move out of `SubjectComponent` into a component of their own, so that a row, a case and a chip can be selected like a widget. `SubjectComponent` uses it itself, so its behavior does not change. The action bar holds, each only when it applies: Select parent, Move up, Move down, Edit and Delete.

### Chips of invisible widgets

A widget component declares that it renders nothing. Today only `ValueMapWidget` does. In edit mode `MarkdownContentView` leaves such blocks out of the flow and renders them as a chip stack at the top right of its content area, whose element gets `position: relative` in edit mode. In view mode nothing changes. A selected ValueMap chip opens the panel of its cases.

### Collapsed Responsive content

In edit mode `ResponsiveWidget` renders a chip in place with the inverse display classes of its content, for example "Responsive · Xs only", so that it shows exactly at the widths where the content is hidden. Clicking the chip selects the Responsive block. While the selection is on it or inside it, its content shows dimmed at every width and its blocks are selectable.

### Rows and cases

`ValueListRow` renders inside a `SelectableFrame` while its list or one of its rows is selected. The ValueMap panel lists the cases of the map, each inside a `SelectableFrame`.

### Insert points

`MarkdownContentView` renders a plus line between its blocks, and before the first and after the last, when its content area is the current scope. A click opens `SubjectSetupDialog` with any widget type and inserts the result after the block above the line. The selected container shows an "Add ..." button for each of its list slots, which opens the dialog with the type of the slot directly.

### Split preview

`Pages.razor` gives the preview `IsEditing=false` and no structure editor, and cascades a reveal callback instead. A click on a widget of the preview passes its subject to the source editor, which looks for its block among its decoration regions, puts the cursor on the head line, scrolls it into view and highlights the block. When the unsaved text no longer has that block, the click does nothing.

### Block names in the source editor

`MarkdownDecorationParser` records where the name of a head line starts and ends: after the fence and its spaces, up to the first `#`, space or end of line. `BlockOutlineDecorations` writes a new `NameKind` record for it, and the editor module turns it into a `.block-name` decoration with `font-weight: bold`. `.block-fence` keeps its color and loses `font-weight: bold`.

## Refusals and errors

An operation refuses, writes nothing, and the page reads its file again, when:

- the file changed since the snapshot (the compare-exchange fails),
- the subject has no block in the snapshot,
- blocks would be inserted or removed while the graph is ahead of the snapshot,
- a move would leave its slot or has no sibling in its direction,
- the parsed result differs from the expected change in other blocks.

The UI shows the reason as a warning snackbar, for example "The page changed while you edited it. Try again." Because the Split preview never writes, it cannot race the editor buffer any more.

## Testing

- **`MarkdownPageOperations`**, unit tests in HomeBlaze.Storage.Tests, text in and text out in the style of `MarkdownBlockWriterTests`: remove, move and insert for top-level blocks, blocks in content slots (card body, tab), list slots (value rows, cases, tabs, flow items), blocks without closing lines, CRLF text and fences of several depths, and every refusal.
- **`MarkdownFile.EditAsync`**, in HomeBlaze.Storage.Tests: a stale snapshot is refused, an edit reloads with the right subjects, and the configuration write keeps its behavior. The existing reconciler and write tests are the regression net.
- **Decorations**: `BlockOutlineDecorations` writes the name range for `Tab`, `Tab#cooling` and a head line with arguments.
- **bUnit**, in HomeBlaze.Components.Tests and HomeBlaze.Storage.Blazor.Tests: the action bar shows move and delete only when the editor allows them; ValueMap renders a chip in edit mode and nothing in view mode; the Responsive chip renders in edit mode only; the insert lines follow the scope of the selection; the Split preview is not editing and has no structure editor.
- **E2E**, in HomeBlaze.E2E.Tests on a seeded page: delete a block with its confirmation, move a ValueList row, add a ValueMap case in the panel, insert a widget between two blocks, and in Split mode reveal a block with a click without selecting it.

## Out of scope

- Editing the markdown text between blocks, its own spec.
- Drag and drop.
- Undo.
- Moving a block into another slot or another container.
