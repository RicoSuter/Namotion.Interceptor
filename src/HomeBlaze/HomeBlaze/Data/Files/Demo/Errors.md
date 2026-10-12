---
title: Errors Demo
navTitle: Errors Demo
position: 10
---

# Errors Demo

Each section of this page has a block that is broken on purpose, and says what is wrong and how to fix it. Below the text of the block is the red box that replaces it on the page, with the line of the block and a message.

## A type that does not exist

The type name is misspelled, `Crad` instead of `Card`. Write the full type name with its namespace, as it stands in the `$type` of a JSON file.

~~~~markdown
:: HomeBlaze.Components.Crad Title="Typing Mistake"
This card is never shown.
::
~~~~

:: HomeBlaze.Components.Crad Title="Typing Mistake"
This card is never shown.
::

## A type that a page cannot create

The type exists, but it is not made to be configured, so a block cannot create it. Use a type that can be configured, like the widgets and the motor of the other demo pages.

~~~~markdown
:: HomeBlaze.Storage.Files.MarkdownFile
::
~~~~

:: HomeBlaze.Storage.Files.MarkdownFile
::

## A type name without its namespace

A name without a dot is the name of a slot, and a slot block is only allowed directly inside a widget block. Write the full name `HomeBlaze.Components.Card`.

~~~~markdown
:: Card Title="Short Name"
::
~~~~

:: Card Title="Short Name"
::

## An argument that the type does not have

The key is misspelled, `Titel` instead of `Title`. The message lists the keys that the type has. A key is the name of a property.

~~~~markdown
:: HomeBlaze.Components.Card Titel="Motor Card"
The body of the card.
::
~~~~

:: HomeBlaze.Components.Card Titel="Motor Card"
The body of the card.
::

## An argument in another case

Keys are case-sensitive, and the key here is `title` instead of `Title`. When a key differs from the name of a property only in upper and lower case, the message names that property. A JSON file of a subject writes the same property as `title`, a page writes it as `Title`.

~~~~markdown
:: HomeBlaze.Components.Card title="Lower Case"
The body of the card.
::
~~~~

:: HomeBlaze.Components.Card title="Lower Case"
The body of the card.
::

## A value that does not fit

The target speed of a motor is a number, and `fast` is not one. Write a value of the type that the message names.

~~~~markdown
:: HomeBlaze.Samples.Motor#fastmotor Name="Fast Motor" TargetSpeed=fast
::
~~~~

:: HomeBlaze.Samples.Motor#fastmotor Name="Fast Motor" TargetSpeed=fast
::

## A slot that the widget does not have

The slot name is misspelled, `Footr` instead of `Footer`. The message lists the slots that the widget has. The box replaces the whole card and names the line of the slot block.

~~~~markdown
:: HomeBlaze.Components.Card Title="Misspelled Footer"
The body of the card.

::: Footr
The footer of the card.
:::
::
~~~~

:: HomeBlaze.Components.Card Title="Misspelled Footer"
The body of the card.

::: Footr
The footer of the card.
:::
::

## Content in a widget that takes none

A motor declares no slot, so its block must stay empty. Move the text out of the block, or put the text and the motor into a card.

~~~~markdown
:: HomeBlaze.Samples.Motor#contentmotor Name="Content Motor"
A motor has no place for this text.
::
~~~~

:: HomeBlaze.Samples.Motor#contentmotor Name="Content Motor"
A motor has no place for this text.
::

## Two blocks for a slot that takes one

A card has one footer. The box names the line of the second `Footer` block. Remove one of the two, or put both texts into one block.

~~~~markdown
:: HomeBlaze.Components.Card Title="Two Footers"
The body of the card.

::: Footer
The first footer.
:::

::: Footer
The second footer.
:::
::
~~~~

:: HomeBlaze.Components.Card Title="Two Footers"
The body of the card.

::: Footer
The first footer.
:::

::: Footer
The second footer.
:::
::

## A name that is used twice

A name is unique on the page. The first block with the name keeps it and is shown, and every other block with that name is an error. Give the second motor a name of its own.

~~~~markdown
:: HomeBlaze.Samples.Motor#twin Name="First Twin" TargetSpeed=500
::

:: HomeBlaze.Samples.Motor#twin Name="Second Twin" TargetSpeed=700
::
~~~~

:: HomeBlaze.Samples.Motor#twin Name="First Twin" TargetSpeed=500
::

:: HomeBlaze.Samples.Motor#twin Name="Second Twin" TargetSpeed=700
::

## A closing line that closes nothing

A closing line has as many colons as the head line of its block. The card starts with two colons, and the line of three colons after the first paragraph matches no open block. The message names the blocks that are open at that line. The card itself is fine and still shows the text before and after the line. Remove the line, or give it two colons if it was meant to close the card.

~~~~markdown
:: HomeBlaze.Components.Card Title="Stray Closing Line"
The first paragraph of the card.
:::
The second paragraph of the card.
::
~~~~

:: HomeBlaze.Components.Card Title="Stray Closing Line"
The first paragraph of the card.
:::
The second paragraph of the card.
::

## Content in a block without a closing line

The widget inside the card has no closing line, so the sentence after it belongs to the widget and not to the card, and a widget takes no content. The box says which closing line ends the widget. Add a line of three colons before the sentence.

~~~~markdown
:: HomeBlaze.Components.Card Title="Open Widget"
::: HomeBlaze.Components.Widget Path=../Conveyor
This sentence was meant for the body of the card.
::
~~~~

:: HomeBlaze.Components.Card Title="Open Widget"
::: HomeBlaze.Components.Widget Path=../Conveyor
This sentence was meant for the body of the card.
::

## A block with one colon too few in a layout

The card has three colons like the `Item` before it. Its head line ends the item, so the card stands directly in the layout, which takes no content outside of its items. The box replaces the layout and names both lines. Give the card four colons.

~~~~markdown
:: HomeBlaze.Components.FlowLayout MinWidth=320
::: Item
::: HomeBlaze.Components.Card Title="Short Card"
The card was meant for the first cell.
::
~~~~

:: HomeBlaze.Components.FlowLayout MinWidth=320
::: Item
::: HomeBlaze.Components.Card Title="Short Card"
The card was meant for the first cell.
::

## A block with one colon too few in a card

This mistake shows no box. The motor has two colons like the card. Its head line ends the card, so the motor is shown below the card and not in it. The page cannot tell this from two blocks that are meant to follow each other. Give the motor three colons on both of its lines.

~~~~markdown
:: HomeBlaze.Components.Card Title="Outer Card"
The body of the card.
:: HomeBlaze.Samples.Motor#shortmotor Name="Short Motor" TargetSpeed=800
::
~~~~

:: HomeBlaze.Components.Card Title="Outer Card"
The body of the card.
:: HomeBlaze.Samples.Motor#shortmotor Name="Short Motor" TargetSpeed=800
::

## A forgotten closing line before text

This mistake shows no box. The first card has no closing line, so the sentence that was meant for the page is shown in the card. The card ends at the head line of the next card. Add a line of two colons after the first sentence.

~~~~markdown
:: HomeBlaze.Components.Card Title="Open Card"
The body of the card.

This sentence was meant for the page and is shown in the card.

:: HomeBlaze.Components.Card Title="Next Card"
The head line of this card ends the card above.
::
~~~~

:: HomeBlaze.Components.Card Title="Open Card"
The body of the card.

This sentence was meant for the page and is shown in the card.

:: HomeBlaze.Components.Card Title="Next Card"
The head line of this card ends the card above.
::

## A line of text that starts with colons and a word

The second line is meant as text, but two colons and a letter make it a head line. It gets a box, because `note` is no type name and the words after it are no arguments. No box says the rest: the line ends the card, so the card is shown empty, and the box hides the text up to the closing line. Write a backslash before the first colon, `\:: note that this line is text`.

~~~~markdown
:: HomeBlaze.Components.Card Title="Lunch Plan"
:: note that this line is text
The body of the card.
::
~~~~

:: HomeBlaze.Components.Card Title="Lunch Plan"
:: note that this line is text
The body of the card.
::

## A block in a quote

A block is allowed directly on the page, directly inside another widget block and directly inside a slot block. Inside a quote, a list item or a style block it is an error. Move the block out of the quote.

~~~~markdown
> :: HomeBlaze.Samples.Motor#quotedmotor Name="Quoted Motor"
> ::
~~~~

> :: HomeBlaze.Samples.Motor#quotedmotor Name="Quoted Motor"
> ::

## A json block with broken JSON

The list of cells is opened and never closed. The message ends with what the reader of the JSON found and where. The `json` block must hold one valid JSON object.

~~~~markdown
:: HomeBlaze.Components.GridLayout#brokengrid Rows=1 Columns=2
```json
{ "Cells": [ }
```
::
~~~~

:: HomeBlaze.Components.GridLayout#brokengrid Rows=1 Columns=2
```json
{ "Cells": [ }
```
::

## The old subject(name) form

Earlier versions embedded a subject with a fenced code block that had `subject(name)` as its language and held JSON. This form is no longer supported. Write the subject as a block: the name in parentheses becomes the `#name`, the `$type` becomes the type name and every value becomes an argument. The key of an argument is the name of the property, so `"targetSpeed": 2000` becomes `TargetSpeed=2000`.

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

:: HomeBlaze.Samples.Motor#healthy Name="Healthy Motor" TargetSpeed=1000 SimulationInterval=00:00:01
::
