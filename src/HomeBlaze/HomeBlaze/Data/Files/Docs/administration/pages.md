---
title: Markdown Pages
navTitle: Pages
position: 4
---

# Markdown Pages

HomeBlaze transforms markdown files into interactive pages with live data binding. This page is the reference for people who write pages: frontmatter, live expressions and blocks. People who write widget types find their side in [Building Custom Subjects](../development/building-subjects.md), section "Content Slots".

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
The conveyor runs at {{ /Demo/Conveyor/CurrentSpeed }} RPM and has a temperature of {{ /Demo/Conveyor/Temperature }} °C.
~~~~

The reader sees the sentence with the two numbers in place of the expressions, and the numbers change while the page is open. The spaces inside the braces are optional.

### Expression Paths

The last segment of the path is the name of a property. Everything before it leads to the subject that has the property. A relative path starts at the page, and its first segment can be the name of a block on the page:

| Path | Shows |
|------|-------|
| `mymotor/CurrentSpeed` | A property of the block named `mymotor` on this page |
| `../Conveyor/CurrentSpeed` | A property of a subject in the same folder as this page |
| `/Demo/Conveyor/CurrentSpeed` | A property at an absolute path from the root |
| `/Devices[0]/Temperature` | A property of an entry of a collection, with the index or key in brackets |
| `Title` | A property of the page itself |

[Paths](paths.md) describes the path syntax in full. Its section "Paths on Markdown Pages" explains where a relative path starts and when a name is used.

### Where an Expression Works

- **In text**: In running text, in a heading, in a table cell, inside bold or italic text and in the text of a link.
- **Inside blocks**: In the content of a block, however deeply the block is nested. The path still starts at the page.
- **Inside HTML that you write**: In the text of an element and inside an attribute. This also holds for a `<code>` and a `<pre>` element.

~~~~markdown
<span title="Target: {{ /Demo/Conveyor/TargetSpeed }} RPM">Speed: {{ /Demo/Conveyor/CurrentSpeed }} RPM</span>
~~~~

The reader sees "Speed: 600 RPM", and the tooltip of the text shows the target speed.

- **Live**: The value updates when the property changes.
- **HTML-safe**: The value is HTML-encoded, so a value can never add markup to the page.
- **Empty**: A value that is not set shows nothing.

### Where an Expression Is Not Evaluated

- **In markdown code**: An expression in inline code, in an indented code block or in a fenced code block is shown as written, with its braces. A fenced code block is markdown's way to show code: a line of three or more backticks or tildes, the lines of code, and a line of the same characters. This page shows all its samples that way.
- **On the head line of a block and in its `json` block**: The value of an argument is taken as written. `Title="{{ mymotor/Name }}"` gives a title with braces in it.
- **In the frontmatter**.
- **In the address of a markdown link**: `[text](https://example.com/{{ path }})` gives a broken link. Write the link as an HTML `<a>` element, where an expression works inside the `href` attribute.

### Expression Limits

- **No formatting**: An expression shows the value as it is. Units, rounding and conditions are not supported.
- **Single values**: An expression is made for text, numbers, yes or no, durations and times. For a property that holds a subject or a list, it shows a technical type name.
- **No error for a wrong path**: An expression whose path leads to nothing shows nothing. Check the spelling of the path when a value is missing.
- **No braces and no `<` in a path**: The path cannot contain `{`, `}` or `<`. [Paths](paths.md) says how to write a file name that contains one of them.
- **No live value in markdown code**: Markdown code keeps an expression as written, so a live value cannot stand inside it. Use a `<code>` element when a live value should look like code.

---

## Blocks

A block puts a widget on the page. It starts with a head line of two colons and a type name, and ends with a closing line of two colons:

~~~~markdown
:: HomeBlaze.Samples.Motor#mymotor Name="My Motor" TargetSpeed=2000 SimulationInterval=00:00:01
::

The motor runs at {{ mymotor/CurrentSpeed }} RPM.
~~~~

This block creates a subject of the type `HomeBlaze.Samples.Motor`, sets three of its properties and shows its widget at this place. The name `mymotor` makes the subject available to expressions and to other blocks. The reader sees the motor widget and below it the sentence with the live speed.

The subject belongs to the page. It is created when the page is loaded, and a subject that is a background service starts then. It is stopped and removed when its block or the page is removed.

The sections below are the reference:

- [The Syntax at a Glance](pages.md#glance): one sample with every line explained, and every form a line can take
- [The Head Line](pages.md#head-line): colons, type, name and arguments, and how each kind of value is written
- [Names](pages.md#names): type names, property names, block names and slot names
- [Closing and Nesting](pages.md#nesting): how many colons a line has, and what a wrong number does
- [The `json` Block](pages.md#json-block): configuration that is not a single value
- [Content and Slots](pages.md#slots): text, expressions and blocks inside a widget
- [Style Blocks](pages.md#style-blocks): a plain `<div>` around content
- [Names and the Page](pages.md#page-scope): what a name and a relative path refer to
- [Where a Block Is Allowed](pages.md#where)
- [Widgets](pages.md#widgets): `Card`, `FlowLayout`, `TabLayout`, `Widget`, `GridLayout` and `CanvasLayout` with their arguments and slots
- [Errors](pages.md#errors): every message, its cause and its fix
- [What an Edit of the Page Keeps](pages.md#identity), [Editing in the Page Editor](pages.md#editor), [Not Supported](pages.md#not-supported) and [The Old `subject(name)` Form](pages.md#old-form)

The sample data of a development checkout has demo pages to try each area: `Demo/Blocks.md` for an overview, `Demo/Layouts.md` for cards, flows and tabs, `Demo/References.md` for names and paths, `Demo/Configuration.md` for arguments, the `json` block, style blocks and the page editor, and `Demo/Errors.md` for the common error boxes. An installed HomeBlaze does not ship these pages.

### The Syntax at a Glance {#glance}

A card with content, a widget inside it and a footer:

~~~~markdown
:: HomeBlaze.Components.Card#overview Title="Motor Card" Icon=Settings
The motor runs at {{ cardmotor/CurrentSpeed }} RPM.

::: HomeBlaze.Samples.Motor#cardmotor Name="Card Motor" TargetSpeed=1200
:::

::: Footer
Target speed: {{ cardmotor/TargetSpeed }} RPM
:::
::
~~~~

| Line | What it is |
|------|------------|
| `:: HomeBlaze.Components.Card#overview Title="Motor Card" Icon=Settings` | The head line of a widget block: two colons, the full type name, the name `overview` and two arguments |
| `The motor runs at {{ cardmotor/CurrentSpeed }} RPM.` | Content of the card, with an expression that reads from the block named `cardmotor` |
| `::: HomeBlaze.Samples.Motor#cardmotor Name="Card Motor" TargetSpeed=1200` | The head line of a widget block inside the card. It has three colons, one more than the card |
| `:::` | The closing line of the motor: as many colons as its head line |
| `::: Footer` | The head line of a slot block. `Footer` has no dot, so it is no type but the name of a slot of the card |
| `Target speed: {{ cardmotor/TargetSpeed }} RPM` | Content of the footer |
| `:::` | The closing line of the footer |
| `::` | The closing line of the card |

The reader sees a box with the title "Motor Card" and an icon, in it the sentence with the live speed and the motor widget, and below a divider the footer with the target speed.

Every form a line of the syntax can take:

| Form | Written as | Meaning |
|------|------------|---------|
| Head line of a widget block | `:: Full.Type.Name#name Key=Value Key="Some text"` | Opens a block that creates a subject of that type and shows its widget. The name and the arguments are optional |
| Head line of a slot block | `::: Footer` or `::: Tab#name Title="Some text"` | Opens a block that fills a slot of the widget block directly around it. Only some slots take a name and arguments |
| Head line of a style block | `:: {.some-class}` | Opens a block that becomes a plain `<div>` with these attributes |
| Closing line | `::` | Closes the block that was opened with the same number of colons. Colons only |
| `json` block | A fenced code block with the language `json`, as the first thing inside a block | Configuration of the block that is not a single value. It is not shown |
| Expression | `{{ path/Property }}` | A live value in the text |
| Text that starts with colons | `\:: some text` | A backslash before the first colon makes the line ordinary text |

A line that starts with two or more colons is called a colon line on this page. A colon line with something after the colons is a head line, and a colon line with nothing after them is a closing line.

### The Head Line {#head-line}

The head line has four parts in this order: the colons, the type name or slot name, an optional `#name`, and any number of arguments. All of it stands on one line.

~~~~markdown
:: HomeBlaze.Components.Card
A card without a name and without arguments.
::

::HomeBlaze.Components.Card
The same head line without the space after the colons.
::

:: HomeBlaze.Components.Card#notes
A card with a name.
::

:: HomeBlaze.Components.Card Title="Daily Summary" Icon=Bolt
A card with two arguments.
::

:: HomeBlaze.Components.Card#summary Title="Daily Summary" Icon=Bolt
A card with a name and two arguments.
::
~~~~

- **Colons**: Two for a block directly on the page, more for a block inside another block. See [Closing and Nesting](pages.md#nesting). The colons may be indented by up to three spaces. With four or more spaces the line is no colon line: it is code or plain text, as everywhere in markdown.
- **Space after the colons**: Write one space. The page also reads a head line without that space, and with several spaces or a tab.
- **Type name or slot name**: A name with a dot is a type name and makes a widget block. A name without a dot is a slot name and makes a slot block. See [Names](pages.md#names).
- **`#name`**: Follows the type name or slot name directly, without a space. A space before the `#` is an error.
- **Arguments**: Each argument is `Key=Value` without spaces around the `=`. Arguments are separated by spaces, their order does not matter, and each key may stand once. The key is the name of a configuration property of the type, and the argument sets that property.
- **Arguments and names on slot blocks**: A slot block takes a name and arguments only when its slot creates a subject, like the `Tab` of a `TabLayout`. See [Content and Slots](pages.md#slots).

#### Values {#values}

| Kind of value | Written | Example | A property that takes it |
|---------------|---------|---------|--------------------------|
| Text without spaces | As it is | `Icon=Bolt` | `Icon` of `Card` |
| Text with spaces, or empty text | In double quotes | `Title="Motor Card"`, `Title=""` | `Title` of `Card` |
| Path | As it is | `Path=/Devices/Sonos/Players[RINCON_000E58A0B1C201400]` | `Path` of `Widget` |
| Whole number | Digits, with a `-` before a negative number | `MinWidth=320` | `MinWidth` of `FlowLayout` |
| Yes or no | `true` or `false` | `IsSnapToGridEnabled=true` | `IsSnapToGridEnabled` of `CanvasLayout` |
| Duration | Hours, minutes and seconds with colons, optionally with a fraction of a second | `SimulationInterval=00:00:01`, `SimulationInterval=00:00:00.500` | `SimulationInterval` of `Motor` |
| One of a fixed set of values | The name of the value | `Mode=Input` | None of the widgets that ship has one. `Mode` of the device type `GpioPin` is an example |

- **Without quotes**: A value without quotes runs up to the next space. It can contain any character except a space and a double quote, so a path with slashes and brackets needs no quotes.
- **With quotes**: Inside double quotes, write `\"` for a double quote and `\\` for a backslash. Every other character stands for itself, so `\n` is a backslash and an `n` and no line break. A text with a line break goes into the [`json` block](pages.md#json-block).
- **Quotes do not change the kind**: A value is converted to the type of its property. `TargetSpeed="1200"` sets the same number as `TargetSpeed=1200`, and `Name=123` sets the text `123`.
- **Whole numbers**: A property for whole numbers takes digits only. `1200.0`, `1e3`, `+5` and `007` are errors.
- **Durations**: Write a duration in full as `hours:minutes:seconds`. Days go in front with a dot, as in `1.00:00:00`. A number alone is read as days, so `SimulationInterval=1` is one day.
- **A value that is not set**: To leave a property at its default, leave the argument out. An empty value like `MinHeight=` is an error for a number.
- **A value that does not fit**: A value that cannot be converted, like `TargetSpeed=fast`, is an error. The box names the type that the property expects.

~~~~markdown
:: HomeBlaze.Components.Card Title="A \"quoted\" word and a back\\slash" Icon=Bolt
The title of this card is: A "quoted" word and a back\slash
::
~~~~

<!-- sample: error -->
~~~~markdown
:: HomeBlaze.Samples.Motor#fastmotor Name="Fast Motor" TargetSpeed=fast
::
~~~~

~~~~text
Line 1: 'fast' is not a valid value for 'TargetSpeed'. Expected a value of type Int32.
~~~~

`Int32` is the technical name for a whole number, `Boolean` for yes or no and `TimeSpan` for a duration.

### Names {#names}

All names are case-sensitive: `HomeBlaze.Components.Card` is a type, `homeblaze.components.card` is not.

- **Type names**: A widget block has the full .NET type name with its namespace, like `HomeBlaze.Components.Card`. It is the name that the `$type` property has in the JSON file of a subject. There are no short names.
- **Configuration names**: The key of an argument is the name of a property of the type, written exactly as the type declares it in C#, like `TargetSpeed`. It is the same name that a path uses, so the argument `TargetSpeed=1200` and the expression `{{ mymotor/TargetSpeed }}` spell the property the same way. Only the properties that the type marks as configuration can be set, and the error for an unknown key lists them.
- **Block names**: A `#name` starts with a letter or `_` and continues with letters, digits, `_` or `-`. Letters are `a` to `z` and `A` to `Z`. A name is unique on the page, however deeply the blocks are nested. See [Names and the Page](pages.md#page-scope) for what a name is good for.
- **Slot names**: The widget type decides the names of its slots, like `Footer`, `Item` and `Tab`. The section of each widget under [Widgets](pages.md#widgets) lists them.

A configuration file spells the same properties in camelCase, and a page does not. The file `Conveyor.json` of a motor:

```json
{
    "$type": "HomeBlaze.Samples.Motor",
    "name": "Conveyor Belt",
    "targetSpeed": 600,
    "simulationInterval": "00:00:02"
}
```

The same motor as a block on a page:

~~~~markdown
:: HomeBlaze.Samples.Motor#conveyor Name="Conveyor Belt" TargetSpeed=600 SimulationInterval=00:00:02
::
~~~~

A key in the spelling of a file is an error on a page. When a key differs from the name of a property only in upper and lower case, the box names the property:

<!-- sample: error -->
~~~~markdown
:: HomeBlaze.Components.Card title="Lower Case"
The body of the card.
::
~~~~

~~~~text
Line 1: Card has no configuration property 'title'. Did you mean 'Title'? Available: Icon, Title.
~~~~

### Closing and Nesting {#nesting}

The rule has three parts:

1. A block directly on the page has two colons.
2. A block inside another block has more colons than the block around it. Write one more for each level.
3. A closing line has exactly as many colons as the head line of its block. Every block needs its closing line, also an empty block.

Four levels, counted from the outside in: a flow layout with two colons, its item with three, the card in the item with four and the widget in the card with five.

~~~~markdown
:: HomeBlaze.Components.FlowLayout MinWidth=320
::: Item
:::: HomeBlaze.Components.Card Title="Compressor" Icon=Bolt
The compressor runs at {{ ../Compressor/CurrentSpeed }} RPM.

::::: HomeBlaze.Components.Widget Path=../Compressor
:::::
::::
:::
::
~~~~

- **What a closing line closes**: A closing line closes the innermost open block that has the same number of colons. A block that is still open inside that block is closed with it and is shown as an error, because its own closing line is missing.
- **More is enough**: The page only requires more colons than the block around it, so a block with four colons directly inside a block with two is fine. One more for each level is the form that is easy to read, and the form that the page editor writes.
- **No empty lines needed**: A head line and a closing line can follow a line of text directly, and text can follow them directly.
- **Nothing disappears silently**: A colon line that fits no rule is shown as an error at its line. [Errors](pages.md#errors) lists the messages.

#### What a Wrong Number of Colons Does

**A closing line with a number of colons that no open block has.** The line gets an error box, and the block around it is still shown with the text before and after the line. Remove the line, or give it the number of colons of the block that it should close.

<!-- sample: error -->
~~~~markdown
:: HomeBlaze.Components.Card Title="Stray Closing Line"
The first paragraph of the card.
:::
The second paragraph of the card.
::
~~~~

~~~~text
Line 3: This line of 3 ':' characters closes no block. A closing line needs as many characters as the head line of its block. Open here: line 1 (2 characters).
~~~~

**A closing line with too few colons.** It closes a block further out, and the block that it was meant for is shown as not closed. The line that was meant to close the outer block then finds no open block. Here line 4 should have three colons:

<!-- sample: error -->
~~~~markdown
:: HomeBlaze.Components.Card Title="Outer Card"
::: HomeBlaze.Components.Card Title="Inner Card"
The body of the inner card.
::
The body of the outer card.
::
~~~~

~~~~text
Line 2: This block is not closed. The line ':::' that ends it is missing.
Line 6: This line of 2 ':' characters closes no block. No block is open here.
~~~~

**A closing line that is missing.** The block ends where the block around it ends, or at the end of the page. Its box replaces the block with everything in it. For a block that is still open at the end of the page, that is the whole rest of the page. Here the motor has no closing line, and the card is still shown:

<!-- sample: error -->
~~~~markdown
:: HomeBlaze.Components.Card Title="Open Motor"
::: HomeBlaze.Samples.Motor#openmotor Name="Open Motor" TargetSpeed=800
::
~~~~

~~~~text
Line 2: This block is not closed. The line ':::' that ends it is missing.
~~~~

**A head line with too few colons for its place.** The block is shown as an error and still ends at its own closing line, so the block around it is shown with the rest of its content. Here the motor needs three colons on both of its lines:

<!-- sample: error -->
~~~~markdown
:: HomeBlaze.Components.Card Title="Outer Card"
:: HomeBlaze.Samples.Motor#shortmotor Name="Short Motor" TargetSpeed=800
::
The text after the motor.
::
~~~~

~~~~text
Line 2: A block inside another block needs more ':' characters than the block around it. This block has 2 and the block around it, at line 1, has 2. Write ':::' here and on its closing line.
~~~~

#### Colon Lines That Are Text {#text-with-colons}

- **Inside markdown code**: A colon line inside a fenced code block or an indented code block is text. It opens and closes nothing, also when the code block is inside a block. One case differs: a code block directly after an item of a definition list, without an empty line between them, is cut short by markdown at the first line that starts with a colon. A colon line there is then read as a colon line of the page: it can open a block, or close the block around the code without any error box. A code block directly after a definition list needs an empty line before it.
- **Text after the colons that cannot be a name**: A colon line is a head line only when the text after the colons starts with a letter, `_` or `{`. A line like `:: 12:30 lunch` or `::)` is ordinary text.
- **Text after the colons that starts with a letter**: A line like `:: note` is read as a head line and is shown as an error, because `note` is no type name. Inside a block such a line also takes the next closing line for itself, so the block around it is shown as not closed.
- **The backslash**: To start a line of text with colons in every case, write a backslash before the first colon: `\:: note`.
- **One colon**: A line that starts with one colon is never a colon line of a block, and colons inside a line have no meaning.

~~~~markdown
:: HomeBlaze.Components.Card Title="Lunch Plan"
:: 12:30 lunch

\:: note that both lines of this card are text
::
~~~~

### The `json` Block {#json-block}

A list or an object does not fit on the head line. Write such configuration as a JSON object in a fenced code block with the language `json` that is the first thing inside the block. This page calls it the `json` block.

The cells of a `GridLayout` are a list. Each cell here holds a subject of its own, a `Widget` that shows a motor:

~~~~markdown
:: HomeBlaze.Components.GridLayout#grid Rows=1 Columns=2
```json
{
  "Cells": [
    { "Row": 0, "Column": 0, "Child": { "$type": "HomeBlaze.Components.Widget", "Path": "../Compressor" } },
    { "Row": 0, "Column": 1, "Child": { "$type": "HomeBlaze.Components.Widget", "Path": "../WaterPump" } }
  ]
}
```
::
~~~~

The reader sees a grid of one row with the two motors next to each other. The `json` block itself is not shown.

- **Where**: The `json` block is the first thing inside its block. Empty lines before it do not count. It is written with three or more backticks or tildes and the language `json` in lower case. A fenced code block with `json` anywhere else is ordinary content and is shown as code.
- **Which blocks**: A widget block can have a `json` block, and so can a slot block that creates a subject, like a `Tab`.
- **Names**: The names in the object are property names like the keys of the arguments. This holds at every depth: here for `Cells` of the grid, for `Row`, `Column` and `Child` of a cell, and for `Path` of the widget in the cell.
- **`$type`**: A subject inside the object names its type with `$type`, which is spelled the same as in a JSON file. At the top level of the object, a `$type` is ignored, because the type of the block is on its head line.
- **Argument and `json` block**: Both can be used together. When both set the same property, the argument wins.

What the page checks in a `json` block, and what it does not:

- **Checked**: The text must be one valid JSON object without a name that stands twice. A name at the top level of the object that the type does not have is an error like an unknown argument, with the same hint when only the case differs.
- **Not checked below the top level**: A name deeper in the object that is no property of its type is skipped without a message, and the property keeps its default. This is the trap when you paste from a `*.json` configuration file, which spells the names in camelCase: `"path"` in the widget of a cell is skipped, the widget has no path and shows "Cannot resolve path". When a nested value is missing on the page, check the spelling and the case of its name.
- **Values are not checked**: A value in the `json` block that does not fit its property is skipped without a message, also at the top level, and the property keeps its default. If the `$type` of a nested subject names no type, the whole top level value that contains it is skipped. On the head line the same mistakes are errors, so write single values as arguments.

The page editor writes configuration in a fixed way, see [Editing in the Page Editor](pages.md#editor): text without a line break, numbers, yes or no and durations go on the head line, and lists, objects and text with a line break go into the `json` block.

### Content and Slots {#slots}

A widget can take page content: text, expressions and other blocks. Its type declares the places for content as slots. A type has at most one default slot and any number of named slots, and each widget under [Widgets](pages.md#widgets) lists its slots.

**The default slot.** Everything inside a widget block that is not a slot block goes to the default slot of the widget. For a card that is its body:

~~~~markdown
:: HomeBlaze.Components.Card Title="Conveyor Belt"
The conveyor runs at {{ ../Conveyor/CurrentSpeed }} RPM.

::: HomeBlaze.Components.Widget Path=../Conveyor
:::
::
~~~~

**A named slot.** A block whose name has no dot is a slot block. It fills the slot of that name on the widget block directly around it. A card has the slot `Footer`:

~~~~markdown
:: HomeBlaze.Components.Card Title="Conveyor Belt"
The body of the card.

::: Footer
The footer of the card, with a value: {{ ../Conveyor/TargetSpeed }} RPM.
:::
::
~~~~

**A slot whose blocks create a subject.** Some slots do not hold content directly. Each of their blocks creates a subject of a type that the widget chose, and the block takes the arguments of that type, an optional `#name` and a `json` block, like a widget block. The content of the block goes to the default slot of that subject. The `Tab` of a `TabLayout` is such a slot: each `Tab` block creates a tab with a `Title` and an `Icon`.

~~~~markdown
:: HomeBlaze.Components.TabLayout
::: Tab#cooling Title="Cooling Fan" Icon=AcUnit
The content of the first tab.
:::
::: Tab#exhaust Title="Exhaust Fan"
The content of the second tab.
:::
::
~~~~

**A slot for a list.** A slot for one value takes one block. A slot for a list takes any number of blocks, one value per block in the order of the page. The `Tab` blocks above are one list, and the `Item` blocks of a `FlowLayout` are another:

~~~~markdown
:: HomeBlaze.Components.FlowLayout
::: Item
The first cell.
:::
::: Item
The second cell.
:::
::: Item
The third cell.
:::
::
~~~~

**The default slot by its name.** The default slot has a name too, and a slot block with that name fills it. For a card the two forms `::: Content` and loose content give the same result. A widget takes one of the two forms, not both.

~~~~markdown
:: HomeBlaze.Components.Card Title="Conveyor Belt"
::: Content
The body of the card, written as a slot block.
:::
::: Footer
The footer of the card.
:::
::
~~~~

Rules for all slots:

- **Directly inside the widget**: A slot block stands directly inside the widget block that declares the slot. One level deeper, for example a `Footer` inside an `Item`, it is an error.
- **Content only**: A slot that holds content takes no arguments, no `#name` and no `json` block. `Footer`, `Content` and `Item` are such slots.
- **Empty lines are no content**: A block with nothing but empty lines in it has no content. Everything else is content, also an HTML comment. One case differs: where a widget has no place for loose content, for example between the `Item` blocks of a `FlowLayout`, an HTML comment is left out and is no error.
- **An empty slot block**: A slot block without content still fills its slot. A card with an empty `Footer` block shows the divider of the footer with nothing below it.

#### Slot Errors

A mistake in how the slots of a widget are filled replaces the whole widget with one box. The box names the line that has to be fixed.

**Content in a widget without a default slot.** A motor declares no slot, so its block must stay empty. `FlowLayout` and `TabLayout` have no default slot either: all their content stands in `Item` or `Tab` blocks.

<!-- sample: error -->
~~~~markdown
:: HomeBlaze.Samples.Motor#contentmotor Name="Content Motor"
A motor has no place for this text.
::
~~~~

~~~~text
Line 1: Motor has no default slot, so it cannot have content.
~~~~

**A second block for a slot that takes one.** A card has one footer. The box names the line of the second block.

<!-- sample: error -->
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

~~~~text
Line 8: The slot 'Footer' takes one block, but there are 2.
~~~~

**A slot name that the widget does not have.** The box lists the slots of the widget. A type name without its namespace gives the same error inside a widget, because a name without a dot is a slot name.

<!-- sample: error -->
~~~~markdown
:: HomeBlaze.Components.Card Title="Misspelled Footer"
The body of the card.

::: Footr
The footer of the card.
:::
::
~~~~

~~~~text
Line 4: Card has no slot 'Footr'. Available: Content, Footer.
~~~~

**Arguments on a slot that holds content.** The footer of a card is content and has no properties.

<!-- sample: error -->
~~~~markdown
:: HomeBlaze.Components.Card Title="Footer With Argument"
The body of the card.

::: Footer Title="Not possible"
The footer of the card.
:::
::
~~~~

~~~~text
Line 4: The slot 'Footer' holds content only and takes no arguments, name or json block.
~~~~

### Style Blocks {#style-blocks}

A block with attributes in braces in place of a type name is not a widget. It becomes a plain `<div>` with those attributes around its content:

~~~~markdown
<style>
.demo-note { border-left: 4px solid var(--mud-palette-primary); padding: 0.25rem 1rem; }
</style>

:: {.demo-note}
This text is inside a `div` with the class `demo-note`. The conveyor runs at {{ ../Conveyor/CurrentSpeed }} RPM.
::
~~~~

The reader sees the sentence with a coloured line at its left, which comes from the `<style>` element above the block.

| Written | Becomes |
|---------|---------|
| `:: {.demo-note}` | `<div class="demo-note">` |
| `:: {.demo-note .wide}` | `<div class="demo-note wide">` |
| `:: {#summary}` | `<div id="summary">` |
| `:: {#summary .demo-note}` | `<div id="summary" class="demo-note">` |
| `:: {data-kind=info}` | `<div data-kind="info">` |
| `:: {.demo-note style="color: red"}` | `<div class="demo-note" style="color: red">` |

- **Content**: A style block can hold text, expressions, lists, quotes and other style blocks. A widget block inside a style block is an error.
- **Colons**: The rules are the same as for every block. A style block inside another block has more colons than the block around it.
- **Where**: A style block is allowed everywhere, also inside a list item or a quote.
- **Two characters at least**: A class and an id need at least two characters. `:: {.a}` and `:: {#a}` are not read as a style block and are shown as an error, while `:: {.ab}` and `:: {#ab}` work.
- **Nothing after the braces**: The head line of a style block ends with the closing brace. Empty braces and text after the braces are an error.

### Names and the Page {#page-scope}

A block gets a name with `#name` on its head line. Expressions and `Widget` blocks use the name as the first segment of a path:

~~~~markdown
The motor in the card below runs at {{ pagemotor/CurrentSpeed }} RPM.

:: HomeBlaze.Components.Card Title="Motor Card"
::: HomeBlaze.Samples.Motor#pagemotor Name="Page Motor" TargetSpeed=1200
:::
::

:: HomeBlaze.Components.Card Title="The Same Motor Again"
::: HomeBlaze.Components.Widget Path=pagemotor
:::

The conveyor next to this page runs at {{ ../Conveyor/CurrentSpeed }} RPM.
::
~~~~

- **A name is known on the whole page**: It can be used above the block that declares it, and inside any other block, however deeply nested. The first sentence of the sample reads from a motor that is declared below it and inside a card.
- **A name is unique on the page**: The first block with a name keeps it. Every other block with that name is shown as an error, also a slot block like a `Tab`.
- **Relative paths start at the page**: A path without `/` at its start begins at the page, not at the block that it is written in. `../Conveyor` is a subject in the folder of the page, wherever on the page the path stands. Moving a block into a card or a tab does not change where its paths lead.
- **A name is known on its own page only**: Another page cannot use the name. [Paths](paths.md) says how another page reaches a block.
- **Names come first**: In a path without a prefix, the first segment is looked up as the name of a block before anything else. A path that starts with `/`, `./` or `../` never uses names.

### Where a Block Is Allowed {#where}

A widget block is allowed in three places: directly on the page, directly inside another widget block and directly inside a slot block. A slot block is allowed directly inside the widget block that declares the slot. A style block is allowed everywhere.

- **Lists and quotes**: A widget block inside a list item or a quote is shown as an error. A closing line inside a list item or a quote never closes a block outside of it, and it is shown as an error when nothing is open inside the list item or quote. A colon line that is indented like the text of a list item belongs to that list item. Without indentation it ends the list.
- **Style blocks**: A widget block inside a style block is shown as an error.
- **Markdown code**: A block inside a fenced code block or an indented code block is shown as text and creates nothing, which is how this page shows its samples.
- **Between HTML tags**: Do not put a block between an opening HTML tag and its closing tag, for example between `<div>` and `</div>`. The page does not report this, but the widget ends up outside the element and the layout breaks. Without an empty line between the tag and the block, the lines of the block are shown as text. Use a `Card`, a `FlowLayout` or a `TabLayout` to arrange widgets.
- **HTML inside a block**: HTML inside the content of a block does not need an empty line before the closing line of the block. A colon line with exactly as many colons as an open block around it closes that block also inside a multi-line HTML comment and inside a `<pre>`, a `<script>` or a `<style>` element. Keep such lines out of HTML.
- **Other colon lines after HTML**: A colon line of another length directly after HTML, without an empty line, is part of the HTML. It is shown as text and gives no error.

---

## Widgets {#widgets}

These widgets ship with HomeBlaze and are made for pages. Every other type that can be configured works as a block as well, for example the sample motor or a device: its arguments are its configuration properties, and it shows the widget that its type has. A type without a widget shows the notice "No Widget component for" with its name.

### Card

A titled box around page content. Type name: `HomeBlaze.Components.Card`.

| Argument | Kind of value | Default | Meaning |
|----------|---------------|---------|---------|
| `Title` | Text | None | The title at the top of the card |
| `Icon` | Text | None | The [icon](pages.md#icons) next to the title |

| Slot | Blocks | Holds |
|------|--------|-------|
| `Content` | Default slot | The body of the card: any page content |
| `Footer` | One | Content below a divider at the bottom of the card |

~~~~markdown
:: HomeBlaze.Components.Card Title="Conveyor Belt" Icon=Settings
The conveyor runs at {{ ../Conveyor/CurrentSpeed }} RPM.

::: HomeBlaze.Components.Widget Path=../Conveyor
:::

::: Footer
Target speed: {{ ../Conveyor/TargetSpeed }} RPM
:::
::

:: HomeBlaze.Components.Card
This card has no title, no icon and no footer. It is a plain box around its content.
::
~~~~

- The title row is shown when the card has a title or an icon.
- A card is as wide as the place it stands in. Put cards into a `FlowLayout` to place them next to each other.
- A card has one footer and no other parts, such as buttons or a subtitle.

### FlowLayout

Lays its cells out from left to right and wraps them to as many rows as the width of the screen needs. All cells are equally wide. This gives one column on a phone and several on a desktop. Type name: `HomeBlaze.Components.FlowLayout`.

| Argument | Kind of value | Default | Meaning |
|----------|---------------|---------|---------|
| `MinWidth` | Whole number | `320` | The narrowest a cell may get, in pixels. A value below 50 counts as 50 |

| Slot | Blocks | Holds |
|------|--------|-------|
| `Item` | Any number | One cell per block: any page content |

~~~~markdown
:: HomeBlaze.Components.FlowLayout MinWidth=320
::: Item
:::: HomeBlaze.Components.Card Title="Compressor" Icon=Bolt
::::: HomeBlaze.Components.Widget Path=../Compressor
:::::
::::
:::
::: Item
:::: HomeBlaze.Components.Card Title="Water Pump" Icon=WaterDrop
::::: HomeBlaze.Components.Widget Path=../WaterPump
:::::
::::
:::
::: Item
:::: HomeBlaze.Components.Card Title="Notes"
A cell can hold any page content, not only widgets.

The compressor runs at {{ ../Compressor/CurrentSpeed }} RPM.
::::
:::
::
~~~~

How `MinWidth` works and how to choose it:

- **A number of pixels**: `MinWidth` is a plain number, without a unit. The layout puts as many columns into a row as fit with at least that width each, and then makes all columns equally wide to fill the row. There are 16 pixels between two cells.
- **Smaller means more columns**: With `MinWidth=120` many cells fit into one row. With `MinWidth=600` two cells stand next to each other only on a wide screen.
- **At least as wide as the widest widget**: A cell can get as narrow as `MinWidth`, and a widget that needs more room then paints over the next cell. The sample motor widget needs 280 pixels, and 312 pixels inside a card, so 320 fits it.
- **Narrow screens**: On a screen that is narrower than `MinWidth` there is one column, as wide as the screen.

Limits:

- All cells have the same width. A cell cannot span two columns.
- A row is not stretched: with fewer cells than columns fit, the rest of the row stays empty.
- An `Item` holds content only. It takes no arguments and no `#name`.
- A `FlowLayout` has no default slot. Content outside of an `Item` block is an error.

### TabLayout

Shows one of several tabs at a time. Type name: `HomeBlaze.Components.TabLayout`. It has no arguments.

| Slot | Blocks | Holds |
|------|--------|-------|
| `Tab` | Any number | One tab per block. A `Tab` block creates a subject, so it takes arguments, an optional `#name` and any page content |

The arguments of a `Tab` block:

| Argument | Kind of value | Default | Meaning |
|----------|---------------|---------|---------|
| `Title` | Text | None | The label of the tab |
| `Icon` | Text | None | The [icon](pages.md#icons) on the tab |

~~~~markdown
:: HomeBlaze.Components.TabLayout
::: Tab#cooling Title="Cooling Fan" Icon=AcUnit
The cooling fan has a temperature of {{ ../CoolingFan/Temperature }} °C.

:::: HomeBlaze.Components.Widget Path=../CoolingFan
::::
:::
::: Tab#exhaust Title="Exhaust Fan" Icon=Air
The exhaust fan runs at {{ ../ExhaustFan/CurrentSpeed }} RPM.

:::: HomeBlaze.Components.Widget Path=../ExhaustFan
::::
:::
::: Tab Title="Notes"
A tab without a name and without an icon.
:::
::
~~~~

- **All tabs are rendered**: Every tab is rendered and kept up to date, also the hidden ones. A subject in a hidden tab keeps running, and a hidden tab costs as much as a visible one.
- **The first tab is selected**: When the page opens, the first tab is shown. The selection is not saved and there is no argument for it.
- **Names keep the selection**: When the page text changes while the page is open, a tab with a name stays selected, also when tabs are added, removed or moved. A tab without a name is known by its position only, so the selection stays at the position. When the selected tab is gone, the first tab is selected.
- **Tabs only**: A `TabLayout` has no default slot. Content outside of a `Tab` block is an error.

### Widget

Shows the widget of a subject that exists somewhere else: in a file of its own, or in another block of the page. Type name: `HomeBlaze.Components.Widget`. It has no slots, so its block stays empty.

| Argument | Kind of value | Default | Meaning |
|----------|---------------|---------|---------|
| `Path` | Text | Empty | The path to the subject to show |

~~~~markdown
:: HomeBlaze.Samples.Motor#localmotor Name="Local Motor" TargetSpeed=1500
::

:: HomeBlaze.Components.Card Title="Three Ways to Refer to a Subject" Icon=Link
By the name of a block on this page:

::: HomeBlaze.Components.Widget Path=localmotor
:::

By a relative path, which starts at the page. This is the file `Conveyor.json` next to the page:

::: HomeBlaze.Components.Widget Path=../Conveyor
:::

By an absolute path, which leads to the same subject from every page:

::: HomeBlaze.Components.Widget Path=/Demo/Conveyor
:::
::
~~~~

- **Not a copy**: The widget shows the subject itself. A motor that is shown twice is one motor.
- **A path that leads nowhere**: The widget shows the message "Cannot resolve path" with the path. This is no error box, and a `Widget` without a `Path` shows it too.
- **A path that leads back**: A `Widget` that refers to itself or to a block that contains it shows the message "Cannot show path" in place of the content. Two blocks that show each other end with the same message.
- **A subject without a widget**: When the type of the subject has no widget, the notice "No Widget component for" with the type name is shown.

### GridLayout

A grid with a fixed number of rows and columns. Type name: `HomeBlaze.Components.GridLayout`. It has no slots: its cells are configuration and stand in the `json` block, and each cell holds one subject.

| Argument | Kind of value | Default | Meaning |
|----------|---------------|---------|---------|
| `Rows` | Whole number | `2` | The number of rows |
| `Columns` | Whole number | `2` | The number of columns |
| `Cells` | List, in the `json` block | Empty | The cells of the grid |

One cell in `Cells`:

| Name | Kind of value | Default | Meaning |
|------|---------------|---------|---------|
| `Row` | Whole number | Not set | The row of the cell, counted from 0. Without it the grid places the cell itself |
| `Column` | Whole number | Not set | The column of the cell, counted from 0. Without it the grid places the cell itself |
| `RowSpan` | Whole number | `1` | How many rows the cell covers |
| `ColumnSpan` | Whole number | `1` | How many columns the cell covers |
| `Child` | Subject with `$type` | Not set | The subject that the cell shows |

~~~~markdown
:: HomeBlaze.Components.GridLayout#motorgrid Rows=2 Columns=2
```json
{
  "Cells": [
    { "Row": 0, "Column": 0, "Child": { "$type": "HomeBlaze.Components.Widget", "Path": "../Compressor" } },
    { "Row": 0, "Column": 1, "Child": { "$type": "HomeBlaze.Components.Widget", "Path": "../WaterPump" } },
    { "Row": 1, "Column": 0, "ColumnSpan": 2, "Child": { "$type": "HomeBlaze.Samples.Motor", "Name": "Grid Motor", "TargetSpeed": 900 } }
  ]
}
```
::
~~~~

- All rows have the same height and all columns the same width, and the grid is at least 400 pixels high.
- A cell holds a subject, not page content. Text, expressions and blocks cannot stand in a cell. Use a `FlowLayout` with cards for that.
- The subjects in the cells are created again on every reload of the page, see [What an Edit of the Page Keeps](pages.md#identity). Show a subject that must keep running through a `Widget` in the cell.
- The names inside `Cells` are not checked, see [The json Block](pages.md#json-block).
- While the page is in edit mode, the grid has its own buttons to add, edit and remove cells. They write the `json` block for you.

### CanvasLayout

An area on which subjects are placed at any position. Type name: `HomeBlaze.Components.CanvasLayout`. It has no slots: its nodes are configuration and stand in the `json` block.

| Argument | Kind of value | Default | Meaning |
|----------|---------------|---------|---------|
| `MinHeight` | Whole number | Not set, which gives 400 | The height of the canvas in pixels |
| `IsSnapToGridEnabled` | Yes or no | `false` | Whether a node that is placed or moved snaps to a grid |
| `GridSize` | Whole number | `100` | The step of that grid in pixels |
| `Nodes` | List, in the `json` block | Empty | The nodes on the canvas |

One node in `Nodes`:

| Name | Kind of value | Default | Meaning |
|------|---------------|---------|---------|
| `X` | Whole number | `0` | The distance from the left edge in pixels |
| `Y` | Whole number | `0` | The distance from the top edge in pixels |
| `Width` | Whole number | `200` | The width in pixels |
| `Height` | Whole number | `150` | The height in pixels |
| `Child` | Subject with `$type` | Not set | The subject that the node shows |

~~~~markdown
:: HomeBlaze.Components.CanvasLayout#plan MinHeight=320 IsSnapToGridEnabled=true GridSize=20
```json
{
  "Nodes": [
    { "X": 20, "Y": 20, "Width": 320, "Height": 260, "Child": { "$type": "HomeBlaze.Components.Widget", "Path": "../Conveyor" } }
  ]
}
```
::
~~~~

- While the page is in edit mode, the nodes can be moved with the mouse, and the canvas has its own buttons to add, edit and remove nodes. They write the `json` block for you.
- The limits of the `GridLayout` hold here as well: a node holds a subject and no page content, the subjects are created again on every reload, and the names inside `Nodes` are not checked.

### Icons {#icons}

The `Icon` of a card and of a tab is the name of a MudBlazor Material icon in its filled form, for example `Settings`, `Bolt`, `WaterDrop`, `AcUnit` or `Link`. The name is case-sensitive. An unknown name shows a default icon and no error.

---

## Errors {#errors}

A block that cannot be shown is replaced by a red box with the line number of the block and a message. A closing line that closes nothing gets a box at its own line. The rest of the page still renders.

- **One box per block**: The box replaces the block with everything in it. The blocks inside it are not created, and they are created again once the error is fixed.
- **Two messages in one box**: A block that has an error and is also not closed shows both messages. For a block that is not closed, "everything in it" is everything up to the end of the block around it, or up to the end of the page.
- **Errors in slots**: An error in a slot block, such as a `Tab`, a `Footer` or an `Item`, is shown in a box that names the line of the slot block. The box replaces the whole widget around it. One case differs: when the colon lines or the head line of the slot block are wrong and the widget has a default slot that takes loose content, like a `Card`, the widget is still shown and the box stands in its content where the block was. A widget without a default slot, like a `FlowLayout` or a `TabLayout`, has no place for the box, so it is replaced, with one box for each such block in it.
- **Failures of a subject**: When the subject of a block fails while it is created or configured, the box shows the message of that failure, and the log of HomeBlaze has the details.

The names, numbers and lines in the messages below are examples.

### Colon Line Errors

| Message | Cause | Fix |
|---------|-------|-----|
| `This line of 3 ':' characters closes no block. A closing line needs as many characters as the head line of its block. Open here: line 1 (2 characters).` | A closing line whose number of colons fits no open block. The message lists the open blocks from the outside in | Give the line the number of colons of the block it should close, or remove it |
| `This line of 2 ':' characters closes no block. No block is open here.` | A closing line where no block is open. Often the block was closed earlier by a line with too few colons | Remove the line, or fix the closing line before it |
| `This line of 2 ':' characters closes no block. A closing line inside a quote or a list item closes no block outside of it.` | A closing line inside a list item or a quote, while a block is open around the list item or quote. The line does not reach that block | Move the line out of the list item or quote |
| `This block is not closed. The line ':::' that ends it is missing.` | A block without its closing line, or with a closing line that has another number of colons | Add the line that the message names after the content of the block |
| `A block inside another block needs more ':' characters than the block around it. This block has 2 and the block around it, at line 1, has 2. Write ':::' here and on its closing line.` | A block inside another block that does not have more colons | Write the number of colons that the message names on the head line and on the closing line |
| `A block is only allowed directly on the page or directly inside another block.` | A widget block in a list item, a quote or a style block | Move the block out |

### Head Line Errors

| Message | Cause | Fix |
|---------|-------|-----|
| `'Note:' is not a valid block name.` | The text after the colons is no name. Often a line of text that starts with colons and a letter, or a style block whose braces are not accepted | For text, write a backslash before the first colon. For a block, write a name of letters, digits, `_` and dots |
| `'1st' is not a valid name after '#'.` | The `#name` does not follow the rule for names | Start the name with a letter or `_` and use only letters, digits, `_` and `-` |
| `Expected key=value but found 'Title = Motor'.` | Something after the name is no argument: spaces around the `=`, a space before the `#name`, or a word without `=` | Write each argument as `Key=Value` without spaces, and put a value with spaces in double quotes |
| `Argument 'Title' is set twice.` | The same key stands twice on the head line | Remove one of the two |
| `The value of 'Title' has no closing quote.` | A value starts with a double quote and never ends | Add the closing double quote |
| `Expected a space after the value of 'Title'.` | Something follows the closing double quote directly | Put a space between two arguments |
| `The value of 'Title' contains a quote. Put the whole value in quotes.` | A value without quotes has a double quote in it | Put the value in double quotes and write `\"` for the double quote inside |

### Type Errors

| Message | Cause | Fix |
|---------|-------|-----|
| `Unknown type 'HomeBlaze.Components.Crad'. Use the full type name including the namespace.` | The type does not exist, is misspelled or written in another case, or its plugin is not loaded | Write the full type name as it stands in the `$type` of a JSON file |
| `'Card' is not a type name. A slot block is only allowed directly inside a widget block.` | A name without a dot directly on the page: a type name without its namespace, a slot block outside of a widget, or a line of text like `:: note` | Write the full type name, move the slot block into its widget, or write a backslash before the colons of a text line |
| `The type 'HomeBlaze.Storage.Files.MarkdownFile' cannot be used as a block because it does not implement IConfigurable.` | The type exists but is not made to be configured, so a page cannot create it | Use a type that can be configured |
| `The type 'My.Plugin.DeviceBase' cannot be created because it is abstract, an interface or an open generic type.` | The type is a base for other types and not a type of its own | Use one of the types that are built on it |
| `The type 'My.Plugin.Device' could not be created. It must be a registered configurable subject type.` | The type could not be created by HomeBlaze | Check that the plugin of the type is loaded |
| `The type 'My.Plugin.Device' could not be created or updated: Connection refused` | The subject failed while it was created or configured. The text after the colon is the message of the failure | Fix the configuration that the message is about, and read the log for details |

### Configuration Errors

| Message | Cause | Fix |
|---------|-------|-----|
| `Card has no configuration property 'Titel'. Available: Icon, Title.` | The key of an argument, or a name at the top level of the `json` block, is no configuration property of the type | Use one of the names that the message lists |
| `Card has no configuration property 'title'. Did you mean 'Title'? Available: Icon, Title.` | The key differs from the name of a property only in upper and lower case, for example because it was copied from a JSON file | Write the name that the message suggests |
| `'fast' is not a valid value for 'TargetSpeed'. Expected a value of type Int32.` | The value of an argument does not fit the property | Write a value of the kind that the property takes, see [Values](pages.md#values) |
| `The json block must contain a valid JSON object without duplicate keys.` | The `json` block is no valid JSON, is empty, or has a name twice. The message goes on with what the reader of the JSON found and where | Fix the JSON |
| `The json block must contain a JSON object.` | The `json` block holds a list or a single value | Write an object in braces with the property names in it |

### Slot and Name Errors

| Message | Cause | Fix |
|---------|-------|-----|
| `Motor has no default slot, so it cannot have content.` | Content in a widget block whose type takes none, or content outside of the `Item` and `Tab` blocks of a layout | Move the content out of the block, or into a slot block |
| `Motor has no slots, so 'Footer' cannot be used inside it.` | A slot block in a widget that has no slots | Remove the slot block |
| `Card has no slot 'Footr'. Available: Content, Footer.` | The widget has no slot of that name. Also a type name without its namespace inside a widget | Use one of the names that the message lists, or write the full type name |
| `'Footer' is not a slot at this position. A slot block must be directly inside the widget that declares it.` | A slot block inside another slot block | Move the slot block directly into its widget block |
| `The slot 'Footer' takes one block, but there are 2.` | A second block for a slot that takes one | Remove one of them, or put both contents into one block |
| `The slot 'Footer' holds content only and takes no arguments, name or json block.` | Arguments, a `#name` or a `json` block on a slot block that only holds content | Remove them |
| `The slot 'Content' is filled by a block and by loose content.` | The default slot has a slot block with its name, and the widget block has other content as well | Move all the content into the slot block, or remove the slot block lines |
| `The name 'mymotor' is used twice on this page.` | Two blocks with the same `#name`. The first one keeps the name. A name can also collide with the internal key of a block without a name, which is its short type name, `_` and a number, like `Card_3` | Give one of the blocks another name |

### Other Messages

| Message | Cause | Fix |
|---------|-------|-----|
| `The subject(name) fence is no longer supported. Write the subject as a block: a line with :: Full.Type.Name#name key=value, then a closing :: line.` | The page still has a fenced code block of the old form | Convert it, see [The Old subject(name) Form](pages.md#old-form) |
| A message that starts with the short type name and a colon, like `Gauge: the slot 'Content' needs a setter.` | The widget type declares its slots in a way that cannot be used | This is a mistake in the code of the widget, see [Building Custom Subjects](../development/building-subjects.md) |

Three notices are no error boxes. They come from a widget while it is shown:

| Notice | Cause |
|--------|-------|
| Cannot resolve path | The `Path` of a `Widget` block leads to no subject |
| Cannot show path | The `Path` of a `Widget` block leads to the widget itself or to a block that contains it |
| No Widget component for | The type of the block, or of the subject that a `Widget` shows, has no widget |

---

## What an Edit of the Page Keeps {#identity}

When the text of a page changes, the page is read again. A subject that is still on the page is not created again, it is updated in place. This is what keeps a running motor running while you edit the text around it. A subject that is created again starts from its configuration, the same as after a restart of HomeBlaze.

~~~~markdown
:: HomeBlaze.Samples.Motor#keptmotor Name="Named Motor" TargetSpeed=2000
::

:: HomeBlaze.Samples.Motor Name="Unnamed Motor" TargetSpeed=2000
::
~~~~

Add a sentence above both motors and save the page: the named motor keeps its speed, and the unnamed motor starts again at zero, because its position on the page changed.

The page knows a block by its key. The key is the `#name` of the block. A block without a name has its type and its position as key. The rule: a block keeps its subject when its key and its type are unchanged, no argument was removed, and the block around it kept its subject.

- **With a name**: A subject with a `#name` is kept as long as its block keeps its name and its type and stays directly inside the same block, or directly on the page. Changing or adding an argument updates it.
- **Without a name**: The position counts the pieces of content before the block at the same level, which is the page or the block around it: every block, every expression and every stretch of text between them is one piece. Adding or removing a block or an expression before the block creates its subject again. So does a first line of text before a block that had none.
- **Removed arguments**: When an argument, or a name at the top level of the `json` block, is removed, the subject is created again, so that the property gets its default value like after a restart.
- **Nested blocks**: A subject inside another block is kept only when the block around it is kept. A named motor inside a card without a name is created again when the card is.
- **Subjects in the `json` block**: A subject that is nested in the `json` block, for example a widget in a cell of a `GridLayout`, is created again on every reload of the page. The block itself is kept, but its configuration is applied again, and that creates the subjects in it anew.
- **Tabs**: A `Tab` with a name keeps itself and everything inside it when tabs are added, removed or moved. Tabs without a name are matched by their position among the tabs without a name. Adding, renaming or removing the name of a tab creates the tab again once.
- **Flow items**: An `Item` cannot have a name, so items are matched by their position. A subject inside an item is created again when an item is added or removed before it, also when the subject has a name.
- **Errors**: Everything inside a block that was shown as an error is created again once the error is fixed.

Give every subject that has state a name, and name the blocks around it too: the card, the tab and the layout. A subject that must keep its state under all changes belongs in a file of its own, shown on the page through a `Widget` block.

---

## Editing in the Page Editor {#editor}

The page editor shows the text of the page. It marks the head line of every widget block and of every slot block that creates a subject, together with its `json` block, and it marks every expression.

- **Edit a block**: With the cursor on a marked head line, the toolbar shows a button "Edit" followed by the name of the block, or by its type when it has no name. It opens the edit dialog of the subject. Saving the dialog writes the configuration back into the block and saves the page. Only the head line and the `json` block change, the content of the block stays as it is.
- **Edit an expression**: With the cursor in an expression, the toolbar shows the button "Edit Expression". It opens a dialog to choose another path, writes the path into the expression and saves the page.
- **Create a block**: The "Create" button adds a new block at the line of the cursor and saves the page. The block gets two colons directly on the page, and one colon more than the block around it when the cursor is inside the content of a block. A `json` block stays the first thing inside its block: with the cursor before it, on it or inside it, the new block goes after it. The editor keeps an empty line before and after the new block.
- **Save first**: "Edit" needs the saved page and the text in the editor to be the same from the start of the page to the end of the block's head line and `json` block. When that part has unsaved changes, the editor asks you to save the page first. Unsaved text after the block is saved together with the edit.

The steps of "Create":

1. Put the cursor on the line where the block should go and click "Create". The dialog "Step 1: Select Type" opens.
2. Type the name of the block into the field "Name". It becomes the `#name`. The dialog checks it while you type: it starts with a letter or `_`, contains only letters, digits, `_` and `-`, and is not used on the page yet.
3. Click a type. The dialog goes on to the second step by itself. If you click the type before the name is valid, the dialog stays where it is, and you go on with "Next" once the name is valid.
4. Fill in the fields of the second step. Fill in every required field before you click "Create", because a click on "Create" while a required field is empty creates nothing.
5. Click "Create". The new block stands at the line of the cursor, and the page is saved already.

What the editor writes:

- **On the head line**: Text without a line break, numbers, yes or no and durations, each as one argument. A text is put in double quotes only when it needs them.
- **In the `json` block**: Lists, objects and text with a line break. When nothing of that kind is left, the `json` block is removed.
- **Names**: Both are written with the property names.
- **All values**: Every configuration property that has a value is written, also one that the block did not set before. Values that are not set are left out.
- **Colons**: The head line keeps its number of colons and its `#name` and gets one space after the colons. The arguments are written anew, so arguments that you wrote by hand can change their order.

Editing a subject elsewhere in the UI, for example through the edit button of its widget, writes its block in the same way.

---

## Not Supported {#not-supported}

- Formatting in expressions, like units, rounding and conditions.
- Expressions in arguments. The value of an argument is fixed text.
- Short names for types. A block always has the full type name.
- The cells of a `GridLayout` and the nodes of a `CanvasLayout` as blocks. They are part of the configuration and stand in the `json` block.
- A name for an `Item` of a `FlowLayout`, and with it a subject inside an item that survives a change of the items before it.
- A check of the names and values below the top level of a `json` block.
- A widget block inside a list item, a quote, a style block or an HTML element.
- Blocks of one page that are shared with another page. Keep a shared subject in a file of its own and show it with a `Widget` block.

---

## The Old `subject(name)` Form {#old-form}

Earlier versions embedded a subject with a fenced code block that had `subject(name)` as its language and held JSON. This form is no longer supported. A page that still has one shows an error box in its place. Convert each one into a block by hand.

Before:

<!-- sample: error -->
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

~~~~text
Line 1: The subject(name) fence is no longer supported. Write the subject as a block: a line with :: Full.Type.Name#name key=value, then a closing :: line.
~~~~

After:

~~~~markdown
:: HomeBlaze.Samples.Motor#mymotor Name="My Motor" TargetSpeed=2000 SimulationInterval=00:00:01
::
~~~~

- The name in parentheses becomes the `#name`.
- The value of `$type` becomes the type name.
- Every single value becomes an argument. Lists and objects go into a `json` block inside the block.
- The old form spelled its names in camelCase like a JSON file. A block uses the property names, so `targetSpeed` becomes `TargetSpeed`, on the head line and in the `json` block.

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
| `[Link](sibling.md#section)` | A section of a page in the same folder |

---

## Example: A Dashboard Page {#example}

A complete page. Two motors that have files of their own are shown in cards side by side, a tab layout shows two more, and one motor belongs to the page itself and is read by name:

~~~~markdown
---
title: Factory Dashboard
navTitle: Dashboard
icon: Dashboard
position: 0
---

# Factory Status

The test motor of this page runs at **{{ testmotor/CurrentSpeed }}** of {{ testmotor/TargetSpeed }} RPM.

:: HomeBlaze.Components.FlowLayout#overview MinWidth=320
::: Item
:::: HomeBlaze.Components.Card#conveyorcard Title="Conveyor Belt" Icon=Settings
::::: HomeBlaze.Components.Widget Path=/Demo/Conveyor
:::::

::::: Footer
Current speed: **{{ /Demo/Conveyor/CurrentSpeed }}** RPM
:::::
::::
:::
::: Item
:::: HomeBlaze.Components.Card#testcard Title="Test Motor" Icon=Bolt
::::: HomeBlaze.Samples.Motor#testmotor Name="Test Motor" TargetSpeed=1800 SimulationInterval=00:00:01
:::::

::::: Footer
This motor belongs to the page.
:::::
::::
:::
::

## Fans

:: HomeBlaze.Components.TabLayout#fans
::: Tab#cooling Title="Cooling Fan" Icon=AcUnit
Temperature: **{{ /Demo/CoolingFan/Temperature }}** °C

:::: HomeBlaze.Components.Widget Path=/Demo/CoolingFan
::::
:::
::: Tab#exhaust Title="Exhaust Fan" Icon=Air
Speed: **{{ /Demo/ExhaustFan/CurrentSpeed }}** RPM

:::: HomeBlaze.Components.Widget Path=/Demo/ExhaustFan
::::
:::
::: Tab#test Title="Test Motor"
The test motor again, shown by its name:

:::: HomeBlaze.Components.Widget Path=testmotor
::::
:::
::

For more details, see the [setup guide](Demo/Setup.md).
~~~~

What the sample uses:

- **Cards in a flow**: Two cards next to each other on a wide screen and below each other on a phone.
- **Tabs**: Three tabs with names, so that the selected tab stays selected while the page is edited.
- **A named subject**: The motor `testmotor` is created by the page. The card and the flow layout around it have names as well, so the motor keeps running while other parts of the page are edited. The `Item` between them cannot have a name: an item that is added before it creates the motor again.
- **References**: The `Widget` blocks show subjects by an absolute path and by the name `testmotor`.
- **Expressions**: In running text, in the footer of a card and inside tabs, with a name and with absolute paths.

---

## Tips

1. **Name the subjects that have state**: A subject with a `#name` survives edits elsewhere on the page. Names like `primaryMotor` are clearer than `m1`
2. **Count the colons from the outside in**: Two for a block directly on the page, one more for each level inside it
3. **Use property names, not the names of a JSON file**: `TargetSpeed`, not `targetSpeed`, also deep inside a `json` block
4. **Show shared subjects by path**: Keep a device in a file of its own and show it with a `Widget` block. Create a subject in a block only when it belongs to this page
5. **Fit `MinWidth` to the widgets**: A flow layout needs a `MinWidth` that is at least as wide as its widest widget
6. **Order your pages**: Use `position` in the frontmatter to control the place in the navigation
7. **Keep pages focused**: One topic per page makes navigation cleaner

---

## Related Documentation

- [Subjects, Storage & Files](subjects.md) - Configure subjects and file layout
- [Paths](paths.md) - Path syntax reference
- [Building Custom Subjects](../development/building-subjects.md) - Create subject types and widgets with content slots
