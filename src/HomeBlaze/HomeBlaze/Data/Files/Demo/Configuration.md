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

A yes or no is written as `true` or `false`. The canvas below is empty, and its `isSnapToGridEnabled` is such a value. The page reads it back as {{ canvas/IsSnapToGridEnabled }}.

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
6. Put the cursor on an empty line and click "Create". Enter a name, choose a type, click "Next", and then "Create" in the dialog. The new block stands at the line of the cursor with three colons, and the name you entered is its `#name`.

Steps 4 and 6 change the file `Demo/Configuration.md` of your checkout. The same menu, which is now in the toolbar of the editor, has "View" to leave the editor.

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
