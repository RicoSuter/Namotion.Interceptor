---
title: Configuration Demo
navTitle: Configuration Demo
---

# Configuration Demo

This page shows how a block is configured: with values on its head line, with a `json` block for what does not fit there, and with the page editor that writes both. It also shows the cells of a grid, style blocks, expressions in code and what an edit of the page keeps.

## Values on the head line

Each `Key=Value` pair on the head line is an argument and sets one configuration property of the subject. The key is the name of the property, written exactly as in a path: `TargetSpeed` on the head line and in `{{ valuemotor/TargetSpeed }}`. The motor below gets a text with a space in double quotes, a number and a duration.

~~~~markdown
:: HomeBlaze.Samples.Motor#valuemotor Name="Value Motor" TargetSpeed=1500 SimulationInterval=00:00:00.500
::
~~~~

:: HomeBlaze.Samples.Motor#valuemotor Name="Value Motor" TargetSpeed=1500 SimulationInterval=00:00:00.500
::

The page reads two of the values back: the motor is called {{ valuemotor/Name }} and its target speed is {{ valuemotor/TargetSpeed }} RPM.

A yes or no is written as `true` or `false`. The block below is a `CanvasLayout`, an area on which widgets are placed at any position, and it is empty here. Its `IsSnapToGridEnabled` is such a value: with `true`, a widget that is placed or moved on the canvas snaps to a grid. `GridSize=20` gives that grid a step of 20 pixels, and `MinHeight=120` makes the canvas 120 pixels high. The page reads the yes or no back as {{ canvas/IsSnapToGridEnabled }}.

~~~~markdown
:: HomeBlaze.Components.CanvasLayout#canvas MinHeight=120 IsSnapToGridEnabled=true GridSize=20
::
~~~~

:: HomeBlaze.Components.CanvasLayout#canvas MinHeight=120 IsSnapToGridEnabled=true GridSize=20
::

## Configuration in a json block

A list, an object, a subject or a text with a line break does not fit on the head line. Such a value is written in a JSON object in a `json` block: a fenced code block with the language `json` that is the first thing inside the block. The names in the `json` block are property names like the keys on the head line. The `Subtitle` of the `Card` below is a text with a line break. No widget that ships with HomeBlaze needs a `json` block for anything else. The `json` block itself is not shown on the page, and the card shows its subtitle as running text, so the line break is a space there.

~~~~markdown
:: HomeBlaze.Components.Card#note Title="Note"
```json
{ "Subtitle": "First line\nSecond line" }
```
::
~~~~

:: HomeBlaze.Components.Card#note Title="Note"
```json
{ "Subtitle": "First line\nSecond line" }
```
::

A type with a list or a subject in its configuration, for example a device, takes those in the `json` block as well. [The json Block](../Docs/administration/pages.md#the-json-block) has the rules for them.

## Cells of a grid

Each cell of a `GridLayout` is a `Cell` block inside the grid: its arguments say where the cell is, and its content is what the cell shows. The grid below has one row and two columns. The first cell shows the value motor from the top of this page with a `Widget` block, and the second cell holds text with expressions.

~~~~markdown
:: HomeBlaze.Components.GridLayout#cellgrid Rows=1 Columns=2
::: Cell Row=0 Column=0
:::: HomeBlaze.Components.Widget Path=valuemotor
::: Cell#speed Row=0 Column=1
The **value motor** is set to {{ valuemotor/TargetSpeed }} RPM and runs at {{ valuemotor/CurrentSpeed }} RPM.
::
~~~~

:: HomeBlaze.Components.GridLayout#cellgrid Rows=1 Columns=2
::: Cell Row=0 Column=0
:::: HomeBlaze.Components.Widget Path=valuemotor
::: Cell#speed Row=0 Column=1
The **value motor** is set to {{ valuemotor/TargetSpeed }} RPM and runs at {{ valuemotor/CurrentSpeed }} RPM.
::

The second cell has the name `speed`. A cell with a name keeps itself and everything in it when cells are added or removed before it. The nodes of a `CanvasLayout` are written the same way, as `Node` blocks with `X`, `Y`, `Width` and `Height`. In edit mode the grid, once it is selected, has buttons that write these blocks for you, see [Editing in the Page Editor](../Docs/administration/pages.md#editing-in-the-page-editor).

## Style blocks

A block without a type and with attributes in braces is not a widget. It becomes a plain `<div>` with those attributes around its content. This page has a small `<style>` element that gives the class `demo-note` a coloured line at its left, so that the block can be seen.

~~~~markdown
<style>
.demo-note { border-left: 4px solid var(--mud-palette-primary); padding: 0.25rem 1rem; }
</style>

:: {.demo-note}
This text is inside a `div` with the class `demo-note`. The value motor runs at {{ valuemotor/CurrentSpeed }} RPM.
::
~~~~

<style>
.demo-note { border-left: 4px solid var(--mud-palette-primary); padding: 0.25rem 1rem; }
</style>

:: {.demo-note}
This text is inside a `div` with the class `demo-note`. The value motor runs at {{ valuemotor/CurrentSpeed }} RPM.
::

## Try editing

The page editor writes the configuration of a block for you. Try it on this motor:

:: HomeBlaze.Samples.Motor#editmotor Name="Edit Me" TargetSpeed=900 SimulationInterval=00:00:01
::

1. Open the menu with the eye icon at the top right of the page and choose "Split". The text of the page opens in an editor next to the page.
2. Find the line that starts with `:: HomeBlaze.Samples.Motor#editmotor`. The editor marks it, because it is the head line of a block.
3. Put the cursor on that line. The toolbar above the editor now shows the button "Edit editmotor".
4. Click "Edit editmotor", change "Target Speed (RPM)" and click "Save" in the dialog.
5. Look at the text of the page again. Only the head line of this block changed, and the page is saved already.
6. Put the cursor on an empty line outside of every block, for example the last line of the page, and click "Create". The dialog "Create subject" opens with the list of the types.
7. Click the type "Motor" in the group "Samples". The dialog is now called "Create Motor", and its field "Block name" holds the suggestion `Motor`. Replace it with a name of your own, for example `trymotor`. A block name starts with a letter or `_`, contains only letters, digits, `_` and `-`, and is not used on this page yet.
8. Fill in the field "Name". It is the name that the motor shows, it is required, and it may contain spaces. "Create" stays disabled while it is empty. Then click "Create". The new block stands at the line of the cursor with two colons, the block name from step 7 is its `#name`, and the page is saved already. With the cursor inside the content of a block, the new block gets one colon more than the block around it.

Steps 4 and 8 change the file `Demo/Configuration.md` of your checkout. The same menu, which is now in the toolbar of the editor, has "View" to leave the editor.

## Expressions in code

An expression is a live value in running text. Inside markdown code it is shown as written, which is how these pages show their examples. The same expression three times:

Live: {{ valuemotor/CurrentSpeed }}

In inline code: `{{ valuemotor/CurrentSpeed }}`

In a fenced code block:

~~~~markdown
{{ valuemotor/CurrentSpeed }}
~~~~

## What keeps its instance

When the text of a page changes, the page is read again. A subject that is still on the page is updated and not created again, as long as the page can tell that it is the same one.

:: HomeBlaze.Samples.Motor#keptmotor Name="Named Motor" TargetSpeed=2000 SimulationInterval=00:00:01
::

The motor above has the name `keptmotor`. It keeps running when the page is edited, as long as its block keeps its name and its type and stays directly on the page. Changing or adding an argument updates it, and removing an argument creates it again.

:: HomeBlaze.Samples.Motor Name="Unnamed Motor" TargetSpeed=2000 SimulationInterval=00:00:01
::

The motor above has no name, so the page knows it by its position. Add a block or an expression anywhere before it and save the page: the motor is created again and starts at a speed of zero, while the named motor keeps its speed.
