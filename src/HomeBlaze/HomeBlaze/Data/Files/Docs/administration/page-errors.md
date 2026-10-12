---
title: Errors on Markdown Pages
navTitle: Page Errors
position: 6
---

# Errors on Markdown Pages

This page lists every message that a [markdown page](pages.md) shows in place of a block or of the value of an expression, with its cause and its fix. A block that cannot be shown is replaced by a red box with the line number of the block and a message, and the rest of the page still renders. [Errors](pages.md#errors) on that page has the general rules: what a box replaces and where it stands.

The names, numbers and lines in the messages below are examples.

---

## Colon Line Errors

| Message | Cause | Fix |
|---------|-------|-----|
| `This line of 3 ':' characters closes no block. A closing line needs as many characters as the head line of its block. Open here: line 1 (2 characters).` | A closing line whose number of colons fits no open block. The message lists the open blocks from the outside in | Give the line the number of colons of the block it should close, or remove it |
| `This line of 2 ':' characters closes no block. No block is open here.` | A closing line where no block is open. Often the block ended earlier: a closing line with too few colons closed it, or a head line with as many colons ended it | Remove the line, or give the line that ended the block too early the right number of colons |
| `This line of 2 ':' characters closes no block. A closing line inside a quote or a list item closes no block outside of it.` | A closing line inside a list item or a quote, while a block is open around the list item or quote. The line does not reach that block | Move the line out of the list item or quote |
| `A block is only allowed directly on the page or directly inside another block.` | A widget block or a slot block in a list item, a quote or a style block | Move the block out |

## Head Line Errors

| Message | Cause | Fix |
|---------|-------|-----|
| `'Note:' is not a valid block name.` | The text after the colons is no name. Often a line of text that starts with colons and a letter, or a style block whose braces are not accepted | For text, write a backslash before the first colon. For a block, write a name of letters, digits, `_` and dots |
| `'1st' is not a valid name after '#'.` | The `#name` does not follow the rule for names | Start the name with a letter or `_` and use only letters, digits, `_` and `-` |
| `Expected key=value but found 'Title = Motor'.` | Something after the name is no argument: spaces around the `=`, a space before the `#name`, or a word without `=` | Write each argument as `Key=Value` without spaces, and put a value with spaces in double quotes |
| `Argument 'Title' is set twice.` | The same key stands twice on the head line | Remove one of the two |
| `The value of 'Title' has no closing quote.` | A value starts with a double quote and never ends | Add the closing double quote |
| `Expected a space after the value of 'Title'.` | Something follows the closing double quote directly | Put a space between two arguments |
| `The value of 'Title' contains a quote. Put the whole value in quotes.` | A value without quotes has a double quote in it | Put the value in double quotes and write `\"` for the double quote inside |

## Type Errors

| Message | Cause | Fix |
|---------|-------|-----|
| `Unknown type 'HomeBlaze.Components.Crad'. Use the full type name including the namespace.` | The type does not exist, is misspelled or written in another case, or its plugin is not loaded | Write the full type name as it stands in the `$type` of a JSON file |
| `'Card' is not a type name. A slot block is only allowed directly inside a widget block.` | A name without a dot directly on the page: a type name without its namespace, a slot block outside of a widget, or a line of text like `:: note` | Write the full type name, move the slot block into its widget, or write a backslash before the colons of a text line |
| `The type 'HomeBlaze.Storage.Files.MarkdownFile' cannot be used as a block because it does not implement IConfigurable.` | The type exists but is not made to be configured, so a page cannot create it | Use a type that can be configured |
| `The type 'My.Plugin.DeviceBase' cannot be created because it is abstract, an interface or an open generic type.` | The type is a base for other types and not a type of its own | Use one of the types that are built on it |
| `The type 'My.Plugin.Device' could not be created. It must be a registered configurable subject type.` | The type could not be created by HomeBlaze | Check that the plugin of the type is loaded |
| `The type 'My.Plugin.Device' could not be created or updated: Connection refused` | The subject failed while it was created or configured. The text after the colon is the message of the failure | Fix the configuration that the message is about, and read the log for details |

## Configuration Errors

| Message | Cause | Fix |
|---------|-------|-----|
| `Card has no configuration property 'Titel'. Available: Elevation, Href, Icon, IconColor, Image, Outlined, Subtitle, Title.` | The key of an argument, or a name at the top level of the `json` block, is no configuration property of the type | Use one of the names that the message lists |
| `Card has no configuration property 'title'. Did you mean 'Title'? Available: Elevation, Href, Icon, IconColor, Image, Outlined, Subtitle, Title.` | The key differs from the name of a property only in upper and lower case, for example because it was copied from a JSON file | Write the name that the message suggests |
| `TabLayout has no configuration properties, so 'Title' cannot be set.` | An argument, or a name at the top level of the `json` block, on a type that has no configuration at all | Remove it. The title of a tab belongs on its `Tab` block |
| `'fast' is not a valid value for 'TargetSpeed'. Expected a value of type Int32.` | The value of an argument, or a single value at the top level of the `json` block, does not fit the property | Write a value of the kind that the property takes, see [Values](pages.md#values) |
| `The value of 'Cells' in the json block cannot be read at Cells[1].Child: Read unrecognized type discriminator id 'HomeBlaze.Components.Widgt'.` | A list or an object at the top level of the `json` block cannot be read. The message names the place inside the value where the reader of the JSON stopped, when the mistake is inside the value, and goes on with what the reader found. Here the `$type` of the subject in the second cell names no type | Fix the value at the place that the message names |
| `The value of 'Cells' in the json block cannot be read at Cells[0].Child: A subject in the json block needs a '$type' with its full type name, as the first name in its braces.` | A subject inside a value of the `json` block has no `$type`, or its `$type` stands after another name. The message names the place of that subject | Write `"$type"` with the full type name as the first name of that subject |
| `The json block must contain a valid JSON object without duplicate keys.` | The `json` block is no valid JSON, is empty, or has a name twice. The message goes on with what the reader of the JSON found and where | Fix the JSON |
| `The json block must contain a JSON object.` | The `json` block holds a list or a single value | Write an object in braces with the property names in it |

## Slot and Name Errors

| Message | Cause | Fix |
|---------|-------|-----|
| `Motor has no default slot, so it cannot have content.` | Content in a widget block whose type takes none, or content outside of the `Item` and `Tab` blocks of a layout | Move the content out of the block, or into a slot block |
| `Widget has no default slot, so it cannot have content. The content is inside this block because the block is still open there. A closing line ':::' before the content ends the block.` | The same content, in a block that has no closing line and no slot block after the content. Everything after its head line belongs to it up to the next head line with as many colons or fewer, so most often the closing line before a text was forgotten | Write the closing line that the message shows before the content, see [When a Closing Line Is Needed](pages.md#when-a-closing-line-is-needed) |
| `This block has as many ':' characters as the slot block 'Item' at line 2, so it is not inside that block but directly inside FlowLayout, which has no default slot. Write '::::' here to put it inside 'Item'.` | A widget block in a `FlowLayout` or a `TabLayout` whose head line has no more colons than the `Item` or `Tab` before it. The head line ended that slot block, so the widget block stands beside it. With fewer colons the message says so, and for a block with a closing line it says "here and on its closing line" | Write the colons that the message shows on the head line of the widget block |
| `Motor has no slots, so 'Footer' cannot be used inside it.` | A slot block in a widget that has no slots | Remove the slot block |
| `Card has no slot 'Footr'. Available: Content, Footer, HeaderRight.` | The widget has no slot of that name. Also a type name without its namespace inside a widget | Use one of the names that the message lists, or write the full type name |
| `'Footer' is not a slot at this position. A slot block must be directly inside the widget that declares it.` | A slot block inside another slot block | Move the slot block directly into its widget block |
| `The slot 'Footer' takes one block, but there are 2.` | A second block for a slot that takes one | Remove one of them, or put both contents into one block |
| `The slot 'Footer' holds content only and takes no arguments, name or json block.` | Arguments, a `#name` or a `json` block on a slot block that only holds content | Remove them |
| `The slot 'Content' is filled by a block and by loose content.` | The default slot has a slot block with its name, and the widget block has other content as well | Move all the content into the slot block, or remove the slot block lines |
| `The name 'mymotor' is used twice on this page.` | Two blocks with the same `#name`. The first one keeps the name. A name can also collide with the internal key of a block without a name, which is its short type name, `_` and a number, like `Card_3` | Give one of the blocks another name |

## Expression Errors

An expression with a wrong format does not take a block down. The page shows the message in red in place of the value, and the rest of the page is shown as usual.

| Message | Cause | Fix |
|---------|-------|-----|
| `Unknown format 'rounded'` | The format after the pipe character of an expression is neither `display` nor `raw`, like `{{ motor/Speed \| rounded }}`, where the backslash is only needed inside a table. The message shows the format as written | Use `display` or `raw`, or write no format, see [Expression Formats](pages.md#expression-formats) |

## Value Widget Errors

A map that cannot be found does not take a block down either. The widget shows the message in a small red alert in place of its value, without a line number, and the rest of the page is shown as usual.

| Message | Cause | Fix |
|---------|-------|-----|
| `Cannot resolve map: motorstat` | The `Map` of a `ValueList` row, a `ValueTile` or an `Indicator` leads to no subject, or to a subject that is no `ValueMap`. The message shows the map as written | Check the name or the path in `Map`. The name is that of the block of the map, and a map on another page is reached through that page, see [ValueMap](page-widgets.md#valuemap) |

## Other Messages

| Message | Cause | Fix |
|---------|-------|-----|
| `The subject(name) fence is no longer supported. Write the subject as a block: a line with :: Full.Type.Name#name key=value, then a closing :: line.` | The page still has a fenced code block of the old form | Convert it, see [The Old subject(name) Form](pages.md#the-old-subjectname-form) |
| A message that starts with the short type name and a colon, like `Gauge: the slot 'Content' needs a setter.` | The widget type declares its slots in a way that cannot be used | This is a mistake in the code of the widget, see [Building Custom Subjects](../development/building-subjects.md) |

## Mistakes That Show No Box

A head line ends the blocks before it that have as many colons or more, and a block without a closing line takes everything up to the line that ends it. Three mistakes are therefore not reported as what they are: the page shows a block or a text at the wrong place, and no box says why. [Closing and Nesting](pages.md#closing-and-nesting) has a sample of each.

| Mistake | What the page shows | Fix |
|---------|---------------------|-----|
| One colon too few on a block inside a card, like a motor with two colons in a card with two colons | The head line of the block ends the card, so the block is shown below the card and not in it. A closing line that was written for the card then closes the block and gives no box. When the block has a closing line of its own as well, the line of the card finds no open block and gets a box | Give the block one colon more than the card |
| A closing line that is forgotten before a text, when the block that is still open takes content | The text is shown inside that block, for example in the card above it | Write the closing line of the block before the text |
| A line of text that starts with two colons and a word, like `:: note that the pump is new` | The line is read as a head line. It gets a box for what it lacks as a head line, and it ends the blocks before it that have as many colons or more. No box says that these blocks ended there, and the box of the line hides the text up to the line that ends it | Write a backslash before the first colon: `\:: note that the pump is new` |

## Notices That Are No Errors

These notices are no error boxes. They come from a widget while it is shown:

| Notice | Cause |
|--------|-------|
| Cannot resolve path | The `Path` of a `Widget` block leads to no subject |
| Cannot show path | The `Path` of a `Widget` block leads to the widget itself or to a block that contains it |
| No Widget component for | The type of the block, or of the subject that a `Widget` shows, has no widget |
| A dash in place of a value | The `Path` of a `ValueList` row, a `ValueTile`, an `Indicator` or a `Progress` leads nowhere, or the value is not set. The path is the tooltip of the dash, see [Value Widgets](page-widgets.md#value-widgets). A `Progress` shows it also for a value that is no number |

---

## Related Documentation

- [Markdown Pages](pages.md) - The syntax of pages: expressions, blocks, slots and the `json` block
- [Page Widgets](page-widgets.md) - Reference of the widgets for pages
- [Building Custom Subjects](../development/building-subjects.md) - The rules for the slots of a widget type
