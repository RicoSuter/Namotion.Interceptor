---
title: Configuration Demo
navTitle: Configuration Demo
position: 9
---

# Configuration Demo

This page shows how a block is configured: with values on its head line, with a `json` fence for what does not fit there, and with the page editor that writes both. It also shows style blocks, expressions in code and what an edit of the page keeps.

## Values on the head line

Each `key=value` pair on the head line sets one configuration property of the subject. The motor below gets a text with a space in double quotes, a number and a duration.

~~~~markdown
:::HomeBlaze.Samples.Motor#valuemotor name="Value Motor" targetSpeed=1500 simulationInterval=00:00:00.500
:::
~~~~

:::HomeBlaze.Samples.Motor#valuemotor name="Value Motor" targetSpeed=1500 simulationInterval=00:00:00.500
:::

The page reads two of the values back: the motor is called {{ valuemotor/Name }} and its target speed is {{ valuemotor/TargetSpeed }} RPM.

A yes or no is written as `true` or `false`. The block below is a `CanvasLayout`, an area on which widgets are placed at any position, and it is empty here. Its `isSnapToGridEnabled` is such a value: with `true`, a widget that is placed or moved on the canvas snaps to a grid. `gridSize=20` gives that grid a step of 20 pixels, and `minHeight=120` makes the canvas 120 pixels high. The page reads the yes or no back as {{ canvas/IsSnapToGridEnabled }}.

~~~~markdown
:::HomeBlaze.Components.CanvasLayout#canvas minHeight=120 isSnapToGridEnabled=true gridSize=20
:::
~~~~

:::HomeBlaze.Components.CanvasLayout#canvas minHeight=120 isSnapToGridEnabled=true gridSize=20
:::

## Configuration in a json fence

A list or an object does not fit on the head line. The cells of a `GridLayout` are a list, so they are written as a JSON object in a `json` fence that is the first thing inside the block. Each cell here holds a `Widget` that refers to a motor next to this page. The fence itself is not shown on the page.

~~~~markdown
:::HomeBlaze.Components.GridLayout#grid rows=1 columns=2
```json
{
  "cells": [
    {
      "row": 0,
      "column": 0,
      "child": { "$type": "HomeBlaze.Components.Widget", "path": "../Compressor" }
    },
    {
      "row": 0,
      "column": 1,
      "child": { "$type": "HomeBlaze.Components.Widget", "path": "../WaterPump" }
    }
  ]
}
```
:::
~~~~

:::HomeBlaze.Components.GridLayout#grid rows=1 columns=2
```json
{
  "cells": [
    {
      "row": 0,
      "column": 0,
      "child": { "$type": "HomeBlaze.Components.Widget", "path": "../Compressor" }
    },
    {
      "row": 0,
      "column": 1,
      "child": { "$type": "HomeBlaze.Components.Widget", "path": "../WaterPump" }
    }
  ]
}
```
:::

## Style blocks

A block without a type and with attributes in braces is not a widget. It becomes a plain `<div>` with those attributes around its content. This page has a small `<style>` element that gives the class `demo-note` a coloured line at its left, so that the block can be seen.

~~~~markdown
<style>
.demo-note { border-left: 4px solid var(--mud-palette-primary); padding: 0.25rem 1rem; }
</style>

::: {.demo-note}
This text is inside a `div` with the class `demo-note`. The value motor runs at {{ valuemotor/CurrentSpeed }} RPM.
:::
~~~~

<style>
.demo-note { border-left: 4px solid var(--mud-palette-primary); padding: 0.25rem 1rem; }
</style>

::: {.demo-note}
This text is inside a `div` with the class `demo-note`. The value motor runs at {{ valuemotor/CurrentSpeed }} RPM.
:::

## Try editing

The page editor writes the configuration of a block for you. Try it on this motor:

:::HomeBlaze.Samples.Motor#editmotor name="Edit Me" targetSpeed=900 simulationInterval=00:00:01
:::

1. Open the menu with the eye icon at the top right of the page and choose "Split". The text of the page opens in an editor next to the page.
2. Find the line that starts with `:::HomeBlaze.Samples.Motor#editmotor`. The editor marks it, because it is the head line of a block.
3. Put the cursor on that line. The toolbar above the editor now shows the button "Edit editmotor".
4. Click "Edit editmotor", change "Target Speed (RPM)" and click "Save" in the dialog.
5. Look at the text of the page again. Only the head line of this block changed, and the page is saved already.
6. Put the cursor on an empty line and click "Create". The dialog "Step 1: Select Type" opens.
7. Type a name for the block into the field "Name", for example `trymotor`. It starts with a letter or `_`, contains only letters, digits, `_` and `-`, and is not used on this page yet. Then click the type "Motor" in the group "Samples". The dialog goes on to "Step 2: Create Motor" by itself. If you click the type before the name is valid, the dialog stays where it is, and you go on with "Next" once the name is valid.
8. Fill in the field "Name" of this second step. It is the name that the motor shows, it is required, and it may contain spaces. Then click "Create", which is disabled as long as that field is empty. The new block stands at the line of the cursor with three colons, the name from step 7 is its `#name`, and the page is saved already.

Steps 4 and 8 change the file `Demo/Configuration.md` of your checkout. The same menu, which is now in the toolbar of the editor, has "View" to leave the editor.

## Expressions in code

An expression is a live value in running text. Inside markdown code it is shown as written, which is how these pages show their examples. The same expression three times:

Live: {{ valuemotor/CurrentSpeed }}

In inline code: `{{ valuemotor/CurrentSpeed }}`

In a fence:

~~~~markdown
{{ valuemotor/CurrentSpeed }}
~~~~

## What keeps its instance

When the text of a page changes, the page is read again. A subject that is still on the page is updated and not created again, as long as the page can tell that it is the same one.

:::HomeBlaze.Samples.Motor#keptmotor name="Named Motor" targetSpeed=2000 simulationInterval=00:00:01
:::

The motor above has the name `keptmotor`. It keeps running when the page is edited, as long as its block keeps its name and its type and stays directly on the page. Changing or adding an argument updates it, and removing an argument creates it again.

:::HomeBlaze.Samples.Motor name="Unnamed Motor" targetSpeed=2000 simulationInterval=00:00:01
:::

The motor above has no name, so the page knows it by its position. Add a block or an expression anywhere before it and save the page: the motor is created again and starts at a speed of zero, while the named motor keeps its speed.
