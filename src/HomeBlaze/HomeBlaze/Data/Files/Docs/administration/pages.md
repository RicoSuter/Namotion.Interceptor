---
title: Markdown Pages
navTitle: Pages
position: 4
---

# Markdown Pages

HomeBlaze transforms markdown files into interactive pages with live data binding. This guide explains how to create dynamic pages using frontmatter, live expressions, and blocks.

---

## How It Works

When you add a `.md` file to your data folder:
1. HomeBlaze detects it via file watching
2. The file becomes a `MarkdownFile` subject in the object graph
3. Frontmatter is parsed for navigation and display metadata
4. Expressions become live values, and blocks become widgets with the subjects behind them
5. The page renders with live data updates

---

## Frontmatter

Use YAML frontmatter at the top of your markdown file to control how it appears in navigation:

```yaml
---
title: My Dashboard
navTitle: Dashboard
icon: Dashboard
position: 1
---
```

### Frontmatter Options

| Property | Type | Description |
|----------|------|-------------|
| `title` | string | Full page title displayed in the header |
| `navTitle` | string | Short title for navigation menu (optional, defaults to `title`) |
| `icon` | string | MudBlazor icon name (e.g., `Dashboard`, `Settings`, `Article`) |
| `position` | number | Sort order in navigation (lower = higher in list) |
| `location` | string | Where to show in navigation: `NavBar` (sidebar, default) or `AppBar` (top bar) |
| `alignment` | string | AppBar alignment: `Left` (default) or `Right`. Only used when `location` is `AppBar` |

---

## Navigation Updates

The navigation follows pages that are added, removed or changed while a browser tab is open:

- **Sidebar.** The menu re-reads its items every 5 seconds, including the contents of every expanded folder. A folder appears once it contains a page.
- **Top bar.** Pages with `location: AppBar` are read when the layout renders, which happens on navigation. Only pages at the top level of the data folder appear there.

### Known Limitations and Follow-ups

- A page with `location: AppBar` that is added or removed while a tab is open appears in or disappears from the top bar only after the next navigation in that tab. The 5 second refresh re-renders the sidebar menu, not the layout that owns the top bar.

---

## Live Expressions

An expression is a path in double curly braces, like `{{ mymotor/CurrentSpeed }}`. The page shows the value of the property at that path and updates it when the value changes.

~~~~markdown
Temperature: {{ mymotor/Temperature }}
Speed: {{ mymotor/CurrentSpeed }} RPM
~~~~

### Expression Paths

A path in an expression starts at the page. Its first segment can be the name of a block on the page:

| Path | Resolution |
|------|------------|
| `mymotor/CurrentSpeed` | Property of the block named `mymotor` on this page |
| `../Conveyor/CurrentSpeed` | Property of a subject in the same folder as this page |
| `/Demo/Conveyor/CurrentSpeed` | Absolute path from root |
| `Title` | Property of the page itself |

See [Paths](paths.md) for full path documentation including `./`, `../`, brackets for collection indices, and more.

### Expression Features

- **Live updates**: Values update automatically when the source property changes
- **HTML-safe**: Values are automatically HTML-encoded
- **Null handling**: Missing or null values render as empty strings
- **Anywhere in the text**: An expression works in running text, inside the content of a block and inside an HTML attribute

### Expression Limitations

- **No formatting**: An expression shows the value as it is. Units, rounding and conditions are not supported.
- **Not evaluated inside markdown code**: An expression inside a fenced code block, an indented code block or inline code is shown as written, with its braces. Use this to show an expression as an example. It also means that a live value cannot stand inside markdown code.
- **Evaluated inside HTML**: An expression inside HTML that you write yourself is evaluated, also inside a `<code>` or a `<pre>` element. Only markdown code keeps an expression as written.

---

## Blocks

A block puts a widget on the page. It starts with a head line that names a type and ends with a line of colons:

~~~~markdown
:::HomeBlaze.Samples.Motor#mymotor name="My Motor" targetSpeed=2000 simulationInterval=00:00:01
:::

The motor runs at {{ mymotor/CurrentSpeed }} RPM.
~~~~

This block creates a subject of the type `HomeBlaze.Samples.Motor`, configures it with the three arguments and shows its widget at this place. The name `mymotor` makes the subject available to expressions and to other blocks.

The subject belongs to the page. It is created when the page is loaded, and a subject that is a background service starts then. It is stopped and removed when its block or the page is removed.

The sample data of a development checkout has a page `Demo/Blocks.md` that shows every widget of this section at work. The same folder has one page per area to try things out: layouts, references, configuration and errors. An installed HomeBlaze does not ship these pages.

### Block Syntax

- **Fence**: The head line starts with at least three colons. The block ends with a line of colons only, with at least as many colons as the head line. Write the same number. Every block needs its closing line, also an empty one. Without it, the block takes the rest of the page as its content.
- **Type**: The full type name with its namespace follows the colons without a space. It is the name that the `$type` property has in the JSON file of a subject.
- **Name**: An optional `#name` follows the type without a space. It starts with a letter or `_` and continues with letters, digits, `_` or `-`. A name is unique on the page, however deeply the blocks are nested.
- **Arguments**: Any number of `key=value` pairs follow, separated by spaces. Each one sets a configuration property of the subject. The keys are the property names in camelCase, the same as in the JSON file of a subject.
- **Case**: Type names, slot names and argument keys are case-sensitive.

### Argument Values

| Value | Written | Example |
|-------|---------|---------|
| Text without spaces | As it is | `name=CardMotor` |
| Text with spaces, or empty text | In double quotes | `title="Motor Card"` |
| Number | As it is | `targetSpeed=1200` |
| Yes or no | `true` or `false` | `isSnapToGridEnabled=true` |
| Duration | As it is | `simulationInterval=00:00:01` |

- Inside double quotes, write `\"` for a double quote and `\\` for a backslash. A value without quotes cannot contain a double quote.
- A value is converted to the type of its property. A value that does not fit, like `targetSpeed=fast`, is an error.
- A key that the type does not have is an error, and so is a key that is set twice.

### Configuration That Is Not a Single Value

A list or an object does not fit on the head line. Write such configuration as a JSON object in a `json` code fence that is the first thing inside the block:

~~~~markdown
:::HomeBlaze.Components.GridLayout#grid rows=1 columns=4
```json
{ "cells": [] }
```
:::
~~~~

The keys of the object are configuration properties like the arguments, and an unknown key is an error here too. When an argument and the fence set the same key, the argument wins. The fence is not shown on the page.

### Content and Slots

A widget can take page content: text, expressions and other blocks. Its type declares the places for content as slots. There is at most one default slot and any number of named slots.

~~~~markdown
::::HomeBlaze.Components.Card title="Motor Card" icon=Settings
Card body with a live value: speed is {{ cardmotor/CurrentSpeed }} RPM.

:::HomeBlaze.Samples.Motor#cardmotor name=CardMotor targetSpeed=1200 simulationInterval=00:00:01
:::

:::Footer
Card footer text
:::
::::
~~~~

- **Default slot**: Everything inside a widget block that is not a slot block goes to the default slot of the widget. Here that is the text, the expression and the motor.
- **Slot block**: A block whose name has no dot is a slot block. It fills the slot of that name on the widget directly around it. Here `Footer` fills the footer of the card.
- **One block, one value**: A slot for one value takes one block. A slot for a list takes one value per block, in the order of the page, like the `Item` blocks of a `FlowLayout` and the `Tab` blocks of a `TabLayout`.
- **Slot blocks with arguments**: Some slot blocks create a subject and take arguments and a `#name`, like `Tab` with `title` and `icon`. All others hold content only and take no arguments, no name and no `json` fence, like `Footer` and `Item`.
- **Widgets without a default slot**: A type that declares no default slot cannot have content. The motor above is such a type, so its block stays empty.

A block that contains other blocks needs a longer fence than every block inside it. Count from the inside out: the innermost block has three colons, and each block around it has at least one more. With fences of equal length, the first closing line closes the outer block, and the line that was meant to close it is shown as an error. That line opens a block without a name, which takes everything up to the next line of colons with it, also when that line is inside a code fence, and none of it is shown.

### Card

A titled box around page content.

| Part | Kind | Description |
|------|------|-------------|
| `title` | Argument | Title at the top of the card |
| `icon` | Argument | Icon next to the title |
| Content | Default slot | The body of the card |
| `Footer` | Slot block | Content below a divider at the bottom of the card |

The example under "Content and Slots" above is a card with a footer.

### FlowLayout

Lays its cells out from left to right and wraps them to as many rows as the width of the screen needs. All cells are equally wide. This gives one column on a phone and several on a desktop.

| Part | Kind | Description |
|------|------|-------------|
| `minWidth` | Argument | The narrowest a cell may get, in pixels. The default is 320, and a value below 50 counts as 50 |
| `Item` | Slot block, any number | One cell. Holds any page content |

~~~~markdown
::::::HomeBlaze.Components.FlowLayout minWidth=320
:::::Item
::::HomeBlaze.Components.Card title="Compressor" icon=Bolt
:::HomeBlaze.Components.Widget path=../Compressor
:::
::::
:::::
:::::Item
::::HomeBlaze.Components.Card title="Notes"
A cell can hold any page content, not only widgets.

The compressor runs at {{ ../Compressor/CurrentSpeed }} RPM.
::::
:::::
::::::
~~~~

The fences show the nesting rule: a flow with items that hold cards that hold widgets needs six, five, four and three colons.

Choose `minWidth` at least as large as the widest widget in the cells needs. A cell can get as narrow as `minWidth`, and a widget that needs more room then paints over the next cell. The sample motor widget needs 280 pixels, and 312 pixels inside a card.

### TabLayout

Shows one of several tabs at a time.

| Part | Kind | Description |
|------|------|-------------|
| `Tab` | Slot block, any number | One tab. Takes the arguments `title` and `icon`, an optional `#name`, and any page content |

~~~~markdown
:::::HomeBlaze.Components.TabLayout
::::Tab#cooling title="Cooling Fan" icon=Home
The cooling fan has a temperature of {{ ../CoolingFan/Temperature }} °C.

:::HomeBlaze.Components.Widget path=../CoolingFan
:::
::::
::::Tab#exhaust title="Exhaust Fan"
The exhaust fan runs at {{ ../ExhaustFan/CurrentSpeed }} RPM.

:::HomeBlaze.Components.Widget path=../ExhaustFan
:::
::::
:::::
~~~~

- All tabs are rendered and kept up to date, also the hidden ones. A subject in a hidden tab keeps running.
- When the page text changes while the page is open, a tab with a name stays selected, also when tabs are added, removed or moved. A tab without a name is known by its position only, so the selection stays at the position. When the selected tab is gone, the first tab is selected.

### Widget

Shows the widget of a subject that exists somewhere else: in a file of its own, or in another block of the page.

| Part | Kind | Description |
|------|------|-------------|
| `path` | Argument | Path to the subject to show. The section "Paths on Markdown Pages" in [Paths](paths.md) explains where it starts |

The example continues the card under "Content and Slots" above, which declares the motor `cardmotor`. On a page without that block, the first widget shows "Cannot resolve path".

~~~~markdown
::::HomeBlaze.Components.Card title="Reference Card" icon=Link
The motor of the first card, referenced by its name:

:::HomeBlaze.Components.Widget path=cardmotor
:::

A motor next to this page, referenced by a relative path:

:::HomeBlaze.Components.Widget path=../Conveyor
:::
::::
~~~~

- A path that leads to no subject shows the message "Cannot resolve path".
- A `Widget` that refers to itself or to a block that contains it shows the message "Cannot show path" in place of the content. Two blocks that show each other end with the same message.

### Icons

The `icon` of a card and of a tab is the name of a MudBlazor Material icon, for example `Settings`, `Bolt` or `WaterDrop`. An unknown name shows a default icon.

### Style Blocks

A block without a name and with attributes in braces is not a widget. It becomes a plain `<div>` with those attributes around its content:

~~~~markdown
::: {.my-class}
This text is inside a `div` with the class `my-class`.
:::
~~~~

A style block can hold text and expressions. A widget block inside a style block is an error.

### Where a Block Is Allowed

A widget block is allowed in three places: directly on the page, directly inside another widget block and directly inside a slot block. A slot block is allowed directly inside the widget block that declares the slot.

- A block inside a list item, a quote or a style block is shown as an error.
- A block inside a fenced code block is shown as text and creates nothing, which is how this page shows its examples.
- Do not put a block between an opening HTML tag and its closing tag, for example between `<div>` and `</div>`. The page does not report this, but the widget ends up outside the element and the layout breaks. Without an empty line between the tag and the block, the lines of the block are shown as text. Use a `Card`, a `FlowLayout` or a `TabLayout` to arrange widgets.

### Errors

A block that cannot be shown is replaced by a red box with the line number of the block and a message. The rest of the page still renders. The common errors:

| Situation | Message |
|-----------|---------|
| A line of colons without a type name or a slot name. This is what a miscounted fence produces | A block needs a name. If this line was meant to close a block, the fence of the outer block must be longer than the fences inside it. |
| The type does not exist or is misspelled | Unknown type 'HomeBlaze.Components.Crad'. Use the full type name including the namespace. |
| The type exists but is not made to be configured, so a page cannot create it | The type 'HomeBlaze.Storage.Files.MarkdownFile' cannot be used as a block because it does not implement IConfigurable. |
| A type name without its namespace at the top level of the page, or a slot block there | 'Card' is not a type name. A slot block is only allowed directly inside a widget block. |
| The widget has no slot of that name | Card has no slot 'Footr'. Available: Content, Footer. |
| The type has no configuration property of that name | Card has no configuration property 'titel'. Available: icon, title. |
| The value does not fit the property | 'wide' is not a valid value for 'minWidth'. Expected a value of type Int32. |
| Content in a widget without a default slot | Motor has no default slot, so it cannot have content. |
| A second block for a slot that takes one | The slot 'Footer' takes one block, but there are 2. |
| Two blocks with the same name | The name 'mymotor' is used twice on this page. |
| A block in a list item, a quote or a style block | A block is only allowed directly on the page or directly inside another block. |

When the subject of a block fails while it is created or configured, the box shows the message of that failure.

An error in a slot block, such as a `Tab`, a `Footer` or an `Item`, replaces the whole widget around it with one box. The box names the line of the slot block. Everything inside that widget is created again once the error is fixed.

### What a Change of the Page Keeps

When the text of a page changes, the page is read again. A subject that is still on the page is not created again, it is updated. This is what keeps a running motor running while you edit the text around it. The rules:

- **With a name**: A subject with a `#name` is kept as long as its block keeps its name and its type and stays directly inside the same block, or directly on the page. Changing or adding an argument updates it.
- **Without a name**: A subject without a name is known by its position. Adding or removing a block or an expression before it creates it again.
- **Removed arguments**: When an argument is removed from a block, its subject is created again, so that the property gets its default value like after a restart.
- **Nested blocks**: A subject inside another block is kept only when the block around it is kept. A named motor inside a card without a name is created again when the card is.
- **Subjects in the configuration**: A subject that is nested in the JSON configuration of a block, for example a widget in a cell of a `GridLayout`, is created again on every reload of the page. The block itself is kept, but its configuration is applied again, and that creates the subjects in it anew.
- **Tabs**: A `Tab` with a name keeps itself and everything inside it when tabs are added, removed or moved. Tabs without a name are matched by their position among the tabs without a name. Adding, renaming or removing the name of a tab creates the tab again once.
- **Flow items**: An `Item` cannot have a name. A subject inside an item is created again when an item is added or removed before it, also when the subject has a name.
- **Errors**: Everything inside a block that was shown as an error is created again once the error is fixed.

A subject that is created again starts from its configuration, the same as after a restart of HomeBlaze. Give every subject that has state a name, and name the blocks around it too: the card, the tab and the layout. A subject that must keep its state under all changes belongs in a file of its own, shown on the page through a `Widget` block.

### Editing Blocks

The page editor marks the head line of every widget block and of every slot block that creates a subject, together with its `json` fence.

- **Edit**: With the cursor on a marked head line, the toolbar shows a button that opens the edit dialog of the subject. Saving the dialog writes the configuration back into the block and saves the page. Only the head line and the `json` fence change, the content of the block stays as it is.
- **Create**: The "Create" button adds a new block with three colons at the line of the cursor and saves the page. The name you enter becomes the `#name` of the block. The wizard checks it while you type: it must start with a letter or `_`, contain only letters, digits, `_` and `-`, and not be used on the page yet.
- **Save first**: "Edit" needs the saved page and the text in the editor to be the same from the start of the page to the end of the block's head line and `json` fence. When that part has unsaved changes, the editor asks you to save the page first. Unsaved text after the block is saved together with the edit.
- **What is written**: Single values are written on the head line. Lists, objects and text with a line break are written into the `json` fence. Values that are not set are left out.

Editing a subject elsewhere in the UI, for example through the edit button of its widget, writes its block in the same way.

### The Old `subject(name)` Fence

Earlier versions embedded a subject with a code fence that held JSON. This fence is no longer supported. A page that still has one shows an error box in its place. Convert each fence into a block by hand.

Before:

~~~~markdown
```subject(mymotor)
{
  "$type": "HomeBlaze.Samples.Motor",
  "name": "My Motor",
  "targetSpeed": 2000,
  "simulationInterval": "00:00:01"
}
```
~~~~

After:

~~~~markdown
:::HomeBlaze.Samples.Motor#mymotor name="My Motor" targetSpeed=2000 simulationInterval=00:00:01
:::
~~~~

The name in parentheses becomes the `#name`, the value of `$type` becomes the type name, and every single value becomes an argument. Lists and objects go into a `json` fence inside the block.

### Not Supported

- Formatting in expressions, like units, rounding and conditions.
- The cells of a `GridLayout` as blocks. Its cells are part of its configuration and are edited through its own editor.
- Short names for types. A block always has the full type name.

---

## Relative Links

Links between markdown pages use relative paths that HomeBlaze converts to proper routes:

~~~~markdown
See the [Project Structure](../architecture/project-structure.md) for details.

Check the [installation guide](../administration/installation.md) for examples.
~~~~

### Link Resolution

| Markdown Link | Route |
|--------------|-------|
| `[Link](sibling.md)` | Same folder |
| `[Link](subfolder/page.md)` | Nested folder |
| `[Link](../parent.md)` | Parent folder |
| `[Link](../../root.md)` | Two levels up |

---

## Example: Creating a Dashboard

Here's a complete example of a dashboard page. Two existing motors are shown side by side in cards, and one motor belongs to the page itself:

~~~~markdown
---
title: Factory Dashboard
navTitle: Dashboard
icon: Dashboard
position: 0
---

# Factory Status

::::::HomeBlaze.Components.FlowLayout minWidth=320
:::::Item
::::HomeBlaze.Components.Card title="Conveyor Belt" icon=Settings
:::HomeBlaze.Components.Widget path=/Demo/Conveyor
:::

:::Footer
Current speed: **{{ /Demo/Conveyor/CurrentSpeed }}** RPM
:::
::::
:::::
:::::Item
::::HomeBlaze.Components.Card title="Cooling Fan" icon=Bolt
:::HomeBlaze.Components.Widget path=/Demo/CoolingFan
:::

:::Footer
Temperature: **{{ /Demo/CoolingFan/Temperature }}** °C
:::
::::
:::::
::::::

## Test Motor

:::HomeBlaze.Samples.Motor#testmotor name="Test Motor" targetSpeed=1800 simulationInterval=00:00:01
:::

The test motor runs at **{{ testmotor/CurrentSpeed }}** RPM.

---

For more details, see [Motor Setup Guide](../samples/MotorSetup.md).
~~~~

---

## Tips

1. **Name the subjects that have state**: A subject with a `#name` survives edits elsewhere on the page. Names like `primaryMotor` are clearer than `m1`
2. **Count the colons from the inside out**: Three for the innermost block, one more for each block around it
3. **Show shared subjects by path**: Keep a device in a file of its own and show it with a `Widget` block. Create a subject in a block only when it belongs to this page
4. **Fit `minWidth` to the widgets**: A flow layout needs a `minWidth` that is at least as wide as its widest widget
5. **Order your pages**: Use `position` in the frontmatter to control the place in the navigation
6. **Keep pages focused**: One topic per page makes navigation cleaner

---

## Related Documentation

- [Subjects, Storage & Files](subjects.md) - Configure subjects and file layout
- [Paths](paths.md) - Path syntax reference
- [Building Custom Subjects](../development/building-subjects.md) - Create subject types and widgets with content slots
