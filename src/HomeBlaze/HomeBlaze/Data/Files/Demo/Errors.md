---
title: Errors Demo
navTitle: Errors Demo
position: 10
---

# Errors Demo

Each section of this page has a block that is broken on purpose. A block that cannot be shown is replaced by a red box with the line of the block and a message. Each section says what is wrong and how to fix it, then shows the text of the block and below it the box that the block becomes.

## A type that does not exist

The type name is misspelled, `Crad` instead of `Card`. Write the full type name with its namespace, as it stands in the `$type` of a JSON file.

~~~~markdown
:::HomeBlaze.Components.Crad title="Typing Mistake"
This card is never shown.
:::
~~~~

:::HomeBlaze.Components.Crad title="Typing Mistake"
This card is never shown.
:::

## A type that a page cannot create

The type exists, but it is not made to be configured, so a block cannot create it. Use a type that can be configured, like the widgets and the motor of the other demo pages.

~~~~markdown
:::HomeBlaze.Storage.Files.MarkdownFile
:::
~~~~

:::HomeBlaze.Storage.Files.MarkdownFile
:::

## A type name without its namespace

A name without a dot is the name of a slot, and a slot block is only allowed directly inside a widget block. Write the full name `HomeBlaze.Components.Card`.

~~~~markdown
:::Card title="Short Name"
:::
~~~~

:::Card title="Short Name"
:::

## An argument that the type does not have

The key is misspelled, `titel` instead of `title`. The message lists the keys that the type has. Keys are case-sensitive.

~~~~markdown
:::HomeBlaze.Components.Card titel="Motor Card"
The body of the card.
:::
~~~~

:::HomeBlaze.Components.Card titel="Motor Card"
The body of the card.
:::

## A value that does not fit

The target speed of a motor is a number, and `fast` is not one. Write a value of the type that the message names.

~~~~markdown
:::HomeBlaze.Samples.Motor#fastmotor name="Fast Motor" targetSpeed=fast
:::
~~~~

:::HomeBlaze.Samples.Motor#fastmotor name="Fast Motor" targetSpeed=fast
:::

## A slot that the widget does not have

The slot name is misspelled, `Footr` instead of `Footer`. The message lists the slots that the widget has. The box replaces the whole card and names the line of the slot block.

~~~~markdown
::::HomeBlaze.Components.Card title="Misspelled Footer"
The body of the card.

:::Footr
The footer of the card.
:::
::::
~~~~

::::HomeBlaze.Components.Card title="Misspelled Footer"
The body of the card.

:::Footr
The footer of the card.
:::
::::

## Content in a widget that takes none

A motor declares no slot, so its block must stay empty. Move the text out of the block, or put the text and the motor into a card.

~~~~markdown
:::HomeBlaze.Samples.Motor#contentmotor name="Content Motor"
A motor has no place for this text.
:::
~~~~

:::HomeBlaze.Samples.Motor#contentmotor name="Content Motor"
A motor has no place for this text.
:::

## Two blocks for a slot that takes one

A card has one footer. The box names the line of the second `Footer` block. Remove one of the two, or put both texts into one block.

~~~~markdown
::::HomeBlaze.Components.Card title="Two Footers"
The body of the card.

:::Footer
The first footer.
:::

:::Footer
The second footer.
:::
::::
~~~~

::::HomeBlaze.Components.Card title="Two Footers"
The body of the card.

:::Footer
The first footer.
:::

:::Footer
The second footer.
:::
::::

## A name that is used twice

A name is unique on the page. The first block with the name keeps it and is shown, and every other block with that name is an error. Give the second motor a name of its own.

~~~~markdown
:::HomeBlaze.Samples.Motor#twin name="First Twin" targetSpeed=500
:::

:::HomeBlaze.Samples.Motor#twin name="Second Twin" targetSpeed=700
:::
~~~~

:::HomeBlaze.Samples.Motor#twin name="First Twin" targetSpeed=500
:::

:::HomeBlaze.Samples.Motor#twin name="Second Twin" targetSpeed=700
:::

## A miscounted fence

The card and the motor inside it have fences of the same length. The first line of colons closes the card and the motor with it, so the card is shown. The second line of colons is then a block without a name. Give the card a longer fence than the motor: four colons on its first and on its last line.

~~~~markdown
:::HomeBlaze.Components.Card title="Outer Card"
:::HomeBlaze.Samples.Motor#fencemotor name="Fence Motor" targetSpeed=800
:::
:::
~~~~

:::HomeBlaze.Components.Card title="Outer Card"
:::HomeBlaze.Samples.Motor#fencemotor name="Fence Motor" targetSpeed=800
:::
:::
:::

A block without a name takes everything up to the next line of colons with it, and nothing of that is shown. This page has one more line of colons after the example, so that the sections below are still there.

## A block in a quote

A block is allowed directly on the page, directly inside another widget block and directly inside a slot block. Inside a quote, a list item or a style block it is an error. Move the block out of the quote.

~~~~markdown
> :::HomeBlaze.Samples.Motor#quotedmotor name="Quoted Motor"
> :::
~~~~

> :::HomeBlaze.Samples.Motor#quotedmotor name="Quoted Motor"
> :::

## A json fence with broken JSON

The list of cells is opened and never closed. The message ends with what the reader of the JSON found and where. The fence must hold one valid JSON object.

~~~~markdown
:::HomeBlaze.Components.GridLayout#brokengrid rows=1 columns=2
```json
{ "cells": [ }
```
:::
~~~~

:::HomeBlaze.Components.GridLayout#brokengrid rows=1 columns=2
```json
{ "cells": [ }
```
:::

## The old subject fence

Earlier versions embedded a subject with a code fence that held JSON. This fence is no longer supported. Write the subject as a block: the name in parentheses becomes the `#name`, the `$type` becomes the type name and every value becomes an argument.

~~~~markdown
```subject(oldmotor)
{
  "$type": "HomeBlaze.Samples.Motor",
  "name": "Old Motor",
  "targetSpeed": 2000
}
```
~~~~

```subject(oldmotor)
{
  "$type": "HomeBlaze.Samples.Motor",
  "name": "Old Motor",
  "targetSpeed": 2000
}
```

## The rest of the page still works

A block that cannot be shown does not take the page down. This text, the expression in it and the motor below come after all the errors above. The motor runs at {{ healthy/CurrentSpeed }} RPM.

:::HomeBlaze.Samples.Motor#healthy name="Healthy Motor" targetSpeed=1000 simulationInterval=00:00:01
:::
