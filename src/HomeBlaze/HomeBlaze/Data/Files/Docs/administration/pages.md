---
title: Markdown Pages
navTitle: Pages
position: 4
---

# Markdown Pages

HomeBlaze transforms markdown files into interactive pages with live data binding. This page is the guide for people who write pages: frontmatter, links, live expressions and blocks. Two pages next to it are the reference: [Page Widgets](page-widgets.md) has the widgets that are made for pages, and [Page Errors](page-errors.md) has every error message with its cause and its fix. People who write widget types find their side in [Building Custom Subjects](../development/building-subjects.md), section "Content Slots".

On this page:

- [How It Works](#how-it-works): what happens to a markdown file in the data folder
- [Frontmatter](#frontmatter): the title, the icon and the place in the navigation
- [Relative Links](#relative-links): links to other pages and to the sections of a page
- [Navigation Updates](#navigation-updates): when the menu follows a change
- [Live Expressions](#live-expressions): values in the text that update while the page is open
- [Blocks](#blocks): the syntax that puts widgets on a page, from the head line to nesting, slots and the `json` block
- [Widgets](#widgets): the widgets that are made for pages
- [Errors](#errors): what a page shows when a block is wrong
- [What an Edit of the Page Keeps](#what-an-edit-of-the-page-keeps): which subjects keep running when the text changes
- [Editing in the Page Editor](#editing-in-the-page-editor): creating and editing blocks with dialogs
- [Not Supported](#not-supported): what the syntax does not do
- [Cells and Nodes From the `json` Block](#cells-and-nodes-from-the-json-block): converting the grids and canvases of earlier versions
- [The Old `subject(name)` Form](#the-old-subjectname-form): converting the pages of earlier versions
- [Example: A Dashboard Page](#example-a-dashboard-page): a complete page
- [Tips](#tips)
- [Related Documentation](#related-documentation)

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
| `[Link](#section)` | A section of the page itself |

A section is addressed by the id of its heading. Every heading gets its id from its text: the letters in lower case, the digits, a hyphen for the spaces between two words, and no other punctuation than `-`, `_` and `.`. The id starts at the first letter of the heading, and the second heading with the same text gets `-1` at its end. `## Live Expressions` gets the id `live-expressions`. A link that is only `#` and an id, like `[Link](#live-expressions)`, leads to that section of the page it stands on.

---

## Pictures

A picture is written as in any markdown text, with the address of a picture on the web:

~~~~markdown
![The heat pump](https://example.com/heat-pump.jpg)
~~~~

A picture is shown in a size that fits the page, and it always keeps its proportions:

- **Width**: never wider than the place it stands in, such as the text of the page, a narrow screen or a card.
- **Height**: at most four fifths of the height of the browser window.
- **Your own width**: write the picture as HTML with a `width` to give it a size of its own, as in `<img src="https://example.com/heat-pump.jpg" width="300">`. Such a picture is still never wider than its place, and it has no limit of its height.
- **Not available**: a picture that cannot be loaded is replaced by a placeholder with its text. The address of the picture is the tooltip of the placeholder.

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
The conveyor runs at {{ /Demo/Conveyor/CurrentSpeed }} RPM and has a temperature of {{ /Demo/Conveyor/Temperature }}.
~~~~

The reader sees the sentence with the two values in place of the expressions, and the values change while the page is open. The temperature comes with its unit, so the sentence does not write one. The spaces inside the braces are optional.

- **Live**: The value updates when the property changes.
- **HTML-safe**: The value is HTML-encoded, so a value can never add markup to the page.
- **Empty**: A value that is not set shows nothing.

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

### Expression Formats

An expression shows the formatted value by default. A format after a `|` at the end of the expression chooses the form:

| Written | Shows | A temperature of 21.5 degrees Celsius |
|---------|-------|---------------------------------------|
| `{{ mymotor/Temperature }}` | The formatted value | `21.5°C` |
| `{{ mymotor/Temperature \| display }}` | The formatted value, with the format written out | `21.5°C` |
| `{{ mymotor/Temperature \| raw }}` | The raw value | `21.5` |

Inside a markdown table the `|` of an expression is written `\|`, as in the table above, because a cell of a table ends at every `|` that is not escaped.

- **Formatted value**: The text that the property panel shows for the property. It has the unit of the property, scaled where the unit allows it: 1234 W shows as `1.23 kW`. A number has at most two decimals, a boolean shows `Yes` or `No`, and a date is written in the time zone of the viewer.
- **Raw value**: The plain value with a dot as the decimal separator on every system, and a date as ISO 8601. It has no unit, no scaling and no rounding, which makes it the form for an HTML attribute and for CSS, where a unit or a `Yes` would be wrong.
- **Missing value**: A value that is not set shows nothing in both forms.
- **Unknown format**: A format other than `display` and `raw` shows `Unknown format 'name'` in place of the value. [Page Errors](page-errors.md#expression-errors) has the message.

A `style` attribute needs the raw value. In this sample the width of the bar is the speed of the conveyor in RPM divided by ten, in pixels:

~~~~markdown
<div style="height: 8px; background: var(--mud-palette-primary); width: calc({{ /Demo/Conveyor/CurrentSpeed | raw }} * 0.1px)"></div>
~~~~

### Where an Expression Works

- **In text**: In running text, in a heading, in a table cell, inside bold or italic text and in the text of a link.
- **Inside blocks**: In the content of a block, however deeply the block is nested. The path still starts at the page.
- **Inside HTML that you write**: In the text of an element and inside an attribute. This also holds for a `<code>` and a `<pre>` element.

~~~~markdown
<span title="Target: {{ /Demo/Conveyor/TargetSpeed | raw }} RPM">Speed: {{ /Demo/Conveyor/CurrentSpeed }} RPM</span>
~~~~

The reader sees "Speed: 600 RPM", and the tooltip of the text shows the target speed.

### Where an Expression Is Not Evaluated

- **In markdown code**: An expression in inline code, in an indented code block or in a fenced code block is shown as written, with its braces. A fenced code block is markdown's way to show code: a line of three or more backticks or tildes, the lines of code, and a line of the same characters. This page shows all its samples that way.
- **On the head line of a block and in its `json` block**: The value of an argument is taken as written. `Title="{{ mymotor/Name }}"` gives a title with braces in it.
- **In the frontmatter**.
- **In the address of a markdown link**: `[text](https://example.com/{{ path }})` gives a broken link. Write the link as an HTML `<a>` element, where an expression works inside the `href` attribute.

### Expression Limits

- **Two formats**: An expression has the formatted value and the raw value. Another unit, another number of decimals and conditions are not supported.
- **Single values**: An expression is made for text, numbers, yes or no, durations and times. For a property that holds a subject or a list of subjects, it shows a technical type name. A list of plain values is shown on one line with a space between the values. The form `raw` shows a technical type name for every list.
- **No error for a wrong path**: An expression whose path leads to nothing shows nothing. Check the spelling of the path when a value is missing.
- **No braces and no `<` in a path**: The path cannot contain `{`, `}` or `<`. [Paths](paths.md) says how to write a file name that contains one of them.
- **Single braces in text can disappear**: The markdown renderer reads single braces anywhere in the text of a page as attributes, and removes them from the text, when they are empty or when everything between them reads as attributes: a word like `{id}`, a class like `{.wide}`, an id like `{#top}` or a pair like `{key=value}`, also several of them with spaces between. `Use /api/{id}` is then shown as "Use /api/". To show such braces, put the text in inline code, or write a backslash before the opening brace, as in `/api/\{id}`. A heading that starts with `#` is the exception: such braces cut the heading off, so that everything from the opening brace to the end of the line is lost, also inside inline code and after a backslash. `## Path {id} syntax` is shown as "Path". Keep such braces out of these headings.
- **No live value in markdown code**: Markdown code keeps an expression as written, so a live value cannot stand inside it. Use a `<code>` element when a live value should look like code.

---

## Blocks

A block puts a widget on the page: it creates a subject of the type that its head line names and shows the widget of that subject at this place. It starts with a head line of two colons and a type name. It ends with a closing line of two colons, or without one at the next head line of two colons or at the end of the page.

### The Syntax at a Glance

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

The three closing lines are written out here, and none of them is needed in this card: the head line of the footer ends the motor, because it has as many colons, and the end of the page ends the footer and the card. [Closing and Nesting](#closing-and-nesting) has the rule and says when a closing line is needed.

The reader sees a box with the title "Motor Card" and an icon, in it the sentence with the live speed and the motor widget, and below a divider the footer with the target speed.

Each of the two widget blocks creates a subject of its type, sets the properties that its arguments name and shows the widget of that subject. The name `cardmotor` makes the motor available to expressions and to other blocks. A subject that a block creates belongs to the page. It is created when the page is loaded, and a subject that is a background service starts then. It is stopped and removed when its block or the page is removed.

Every form a line of the syntax can take:

| Form | Written as | Meaning |
|------|------------|---------|
| Head line of a widget block | `:: Full.Type.Name#name Key=Value Key="Some text"` | Opens a block that creates a subject of that type and shows its widget. The name and the arguments are optional |
| Head line of a slot block | `::: Footer` or `::: Tab#name Title="Some text"` | Opens a block that fills a slot of the widget block directly around it. Only some slots take a name and arguments |
| Head line of a style block | `:: {.some-class}` | Opens a block that becomes a plain `<div>` with these attributes |
| Closing line | `::` | Closes the block that was opened with the same number of colons. Colons only. It is optional where the next head line or the end of the page ends the block |
| `json` block | A fenced code block with the language `json`, as the first thing inside a block | Configuration of the block that does not fit on the head line. It is not shown |
| Expression | `{{ path/Property }}` | A live value in the text |
| Text that starts with colons | `\:: some text` | A backslash before the first colon makes the line ordinary text |

A line that starts with two or more colons is called a colon line on this page. A colon line with something after the colons is a head line, and a colon line with nothing after them is a closing line. A head line also ends every block before it that has as many colons or more.

The rest of this section is the reference:

- [The Head Line](#the-head-line): colons, type, name and arguments, and how each kind of value is written
- [Names](#names): type names, property names, block names and slot names
- [Closing and Nesting](#closing-and-nesting): how many colons a line has, when a closing line is needed, and what a wrong number does
- [Content and Slots](#content-and-slots): text, expressions and blocks inside a widget
- [The `json` Block](#the-json-block): configuration that does not fit on the head line
- [Style Blocks](#style-blocks): a plain `<div>` around content
- [Names and the Page](#names-and-the-page): what a name and a relative path refer to
- [Where a Block Is Allowed](#where-a-block-is-allowed)

[Page Widgets](page-widgets.md) shows every widget for pages live, with the source of each sample above its result. The sample data of a development checkout also has demo pages to try each area: `Demo/Blocks.md` for an overview, `Demo/Dashboard.md` as the full example of a dashboard, `Demo/References.md` for names and paths, `Demo/Configuration.md` for arguments, the `json` block, a grid with `Cell` blocks, style blocks and the page editor, and `Demo/Errors.md` for the common error boxes. An installed HomeBlaze does not ship the demo pages.

### The Head Line

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

- **Colons**: Two for a block directly on the page, more for a block inside another block. See [Closing and Nesting](#closing-and-nesting). The colons may be indented by up to three spaces. With four or more spaces the line is no colon line: it is code or plain text, as everywhere in markdown.
- **Space after the colons**: Write one space. The page also reads a head line without that space, and with several spaces or a tab.
- **Type name or slot name**: A name with a dot is a type name and makes a widget block. A name without a dot is a slot name and makes a slot block. See [Names](#names).
- **`#name`**: Follows the type name or slot name directly, without a space. A space before the `#` is an error.
- **Arguments**: Each argument is `Key=Value` without spaces around the `=`. Arguments are separated by spaces, their order does not matter, and each key may stand once. The key is the name of a configuration property of the type, and the argument sets that property.
- **Arguments and names on slot blocks**: A slot block takes a name and arguments only when its slot creates a subject, like the `Tab` of a `TabLayout`. See [Content and Slots](#content-and-slots).

#### Values

| Kind of value | Written | Example | A property that takes it |
|---------------|---------|---------|--------------------------|
| Text without spaces | As it is | `Icon=Bolt` | `Icon` of `Card` |
| Text with spaces, or empty text | In double quotes | `Title="Motor Card"`, `Title=""` | `Title` of `Card` |
| Path | As it is | `Path=/Devices/Sonos/Players[RINCON_000E58A0B1C201400]` | `Path` of `Widget` |
| Whole number | Digits, with a `-` before a negative number | `MinWidth=320` | `MinWidth` of `FlowLayout` |
| Number with a fraction | Digits, a dot and the digits of the fraction, with a `-` before a negative number | `Maximum=2.5` | `Maximum` of `Progress` |
| Yes or no | `true` or `false`. Upper and lower case do not matter for these two words, so `True` works as well | `IsSnapToGridEnabled=true` | `IsSnapToGridEnabled` of `CanvasLayout` |
| Duration | Hours, minutes and seconds with colons, optionally with a fraction of a second | `SimulationInterval=00:00:01`, `SimulationInterval=00:00:00.500` | `SimulationInterval` of `Motor` |
| One of a fixed set of values | The name of the value | `IconColor=Warning`, `Severity=Error`, `Kind=Icon` | `IconColor` of `Card`, `Severity` of `Alert`, `Kind` of `Indicator` |

- **Without quotes**: A value without quotes runs up to the next space. It can contain any character except a space and a double quote, so a path with slashes and brackets needs no quotes.
- **With quotes**: Inside double quotes, write `\"` for a double quote and `\\` for a backslash. Every other character stands for itself, so `\n` is a backslash and an `n` and no line break. A text with a line break goes into the [`json` block](#the-json-block).
- **Quotes do not change the kind**: A value is converted to the type of its property. `TargetSpeed="1200"` sets the same number as `TargetSpeed=1200`, and `Name=123` sets the text `123`.
- **Whole numbers**: A property for whole numbers takes digits only. `1200.0`, `1e3`, `+5` and `007` are errors.
- **Numbers with a fraction**: The fraction follows a dot, with a digit on both sides of the dot, as in `12.5`, `0.25` and `-0.5`. A whole number is fine as well, and `1e3` is read as 1000. `.5`, `5.`, `12,5` and `+1.5` are errors.
- **Durations**: Write a duration in full as `hours:minutes:seconds`. Days go in front with a dot, as in `1.00:00:00`. Two parts are hours and minutes, so `SimulationInterval=00:05` is five minutes and not five seconds. A number alone is read as days, so `SimulationInterval=1` is one day.
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

#### Links in Block Options

The `Href` and the `Image` of a `Card` are links, and they are written like a link in the text of the page. A link to a page is relative to the page, as in `Href="Blocks.md"` or `Href="../Docs/administration/pages.md#blocks"`. A route of the application, like `/pages/Demo/Blocks.md`, and a web address, like `https://example.com/picture.png`, are used as written. Any other relative link, such as `picture.png`, is used as written too, because only links to pages are resolved. The page keeps the value as you wrote it, and the card resolves it when it is shown. Editing the block in the page editor therefore writes your text back and not the resolved route.

### Names

All names are case-sensitive: `HomeBlaze.Components.Card` is a type, `homeblaze.components.card` is not.

- **Type names**: A widget block has the full .NET type name with its namespace, like `HomeBlaze.Components.Card`. It is the name that the `$type` property has in the JSON file of a subject. There are no short names.
- **Configuration names**: The key of an argument is the name of a property of the type, written exactly as the type declares it in C#, like `TargetSpeed`. It is the same name that a path uses, so the argument `TargetSpeed=1200` and the expression `{{ mymotor/TargetSpeed }}` spell the property the same way. Only the properties that the type marks as configuration can be set, and the error for an unknown key lists them.
- **Block names**: A `#name` starts with a letter or `_` and continues with letters, digits, `_` or `-`. Letters are `a` to `z` and `A` to `Z`. A name is unique on the page, however deeply the blocks are nested. See [Names and the Page](#names-and-the-page) for what a name is good for.
- **Slot names**: The widget type decides the names of its slots, like `Footer`, `Item` and `Tab`. The section of each widget on [Page Widgets](page-widgets.md) lists them.

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
Line 1: Card has no configuration property 'title'. Did you mean 'Title'? Available: Elevation, Href, Icon, IconColor, Image, Outlined, Subtitle, Title.
~~~~

### Closing and Nesting

A block ends at the first of four things: its closing line, the next head line that has as many colons as its own head line or fewer, the closing line of a block around it, or the end of the page. A closing line is therefore needed only before something that belongs further out: text of the block around it, or text of the page.

The rule in full:

1. A block directly on the page has two colons.
2. A block inside another block has more colons than the block around it. Write one more for each level.
3. A head line ends every open block that has as many colons as it has, or more, together with everything that is open inside those blocks. Its own block then stands in the block that is still open, or directly on the page.
4. A closing line has exactly as many colons as the head line of its block. It ends that block and everything that is open inside it.
5. The end of the page ends every block that is still open.

Headings in markdown work the same way: a heading ends the sections of its own level and of every level below it.

**A layout in the short form.** A layout has no text that is followed by something further out, so it needs no closing line at all. The colons count up from two to seven here: a tab layout, its tabs, a flow layout, its items, the cards and the widgets. The second `Item` ends the first item with its card and its widget, and the second `Tab` ends the first tab with everything in it:

~~~~markdown
:: HomeBlaze.Components.TabLayout
::: Tab#pumps Title="Pumps" Icon=WaterDrop
:::: HomeBlaze.Components.FlowLayout MinWidth=320
::::: Item
:::::: HomeBlaze.Components.Card Title="Water Pump" Icon=WaterDrop
::::::: HomeBlaze.Components.Widget Path=../WaterPump
::::: Item
:::::: HomeBlaze.Components.Card Title="Compressor" Icon=Bolt
::::::: HomeBlaze.Components.Widget Path=../Compressor
::: Tab#belt Title="Conveyor Belt" Icon=Settings
The conveyor runs at {{ ../Conveyor/CurrentSpeed }} RPM.
~~~~

With every closing line the same page has twenty-one lines in place of eleven and gives the same result.

**A card in the short form.** The head line of the footer ends the motor, because both have three colons. The head line of the next card ends the footer and the first card:

~~~~markdown
:: HomeBlaze.Components.Card#overview Title="Motor Card" Icon=Settings
The motor runs at {{ cardmotor/CurrentSpeed }} RPM.

::: HomeBlaze.Samples.Motor#cardmotor Name="Card Motor" TargetSpeed=1200

::: Footer
Target speed: {{ cardmotor/TargetSpeed }} RPM

:: HomeBlaze.Components.Card Title="The Next Card"
This card stands below the first one, because its head line ended the footer and the card.
~~~~

- **What a closing line closes**: A closing line closes the innermost open block that has the same number of colons. A block that is still open inside that block ends with it.
- **More is enough**: The page only requires more colons than the block around it, so a block with four colons directly inside a block with two is fine. One more for each level is the form that is easy to read, and the form that the page editor writes.
- **No empty lines needed**: A head line and a closing line can follow a line of text directly, and text can follow them directly. Raw HTML is the exception: directly after raw HTML, a head line needs an empty line before it when it stands outside of every block, and inside a `<pre>`, a `<script>` or a `<style>` element it is part of the element up to the closing tag.
- **Nothing disappears silently**: A closing line that fits no open block is shown as an error at its line. [Page Errors](page-errors.md) lists the messages, and the mistakes that give a valid page and show no box.

#### When a Closing Line Is Needed

Two cases need a closing line.

**Text of the page after a block.** The closing line ends the card, so the sentence after it is text of the page:

~~~~markdown
:: HomeBlaze.Components.Card Title="Conveyor Belt"
The conveyor runs at {{ ../Conveyor/CurrentSpeed }} RPM.
::

This sentence is text of the page, below the card.
~~~~

**Text of a block after a block inside it.** The closing line ends the widget, so the sentence after it is in the body of the card:

~~~~markdown
:: HomeBlaze.Components.Card Title="Conveyor Belt"
::: HomeBlaze.Components.Widget Path=../Conveyor
:::
This sentence is in the body of the card, below the widget.
~~~~

**A closing line that is forgotten.** The text then belongs to the block that is still open. When that block takes no content, the page shows a box that names the closing line to add. Here the closing line of the widget is missing:

<!-- sample: error -->
~~~~markdown
:: HomeBlaze.Components.Card Title="Conveyor Belt"
::: HomeBlaze.Components.Widget Path=../Conveyor
This sentence was meant for the body of the card.
~~~~

~~~~text
Line 2: Widget has no default slot, so it cannot have content. The content is inside this block because the block is still open there. A closing line ':::' before the content ends the block.
~~~~

When the open block takes content, the text is shown inside that block and the page shows no box. Here the closing line of the card is missing, so the last sentence is shown in the card:

~~~~markdown
:: HomeBlaze.Components.Card Title="Conveyor Belt"
The conveyor runs at {{ ../Conveyor/CurrentSpeed }} RPM.

This sentence was meant for the page and is shown in the card.
~~~~

What to write:

- **Layouts in the short form**: Write a `FlowLayout`, a `TabLayout`, a `GridLayout`, a `CanvasLayout` and the cards and widgets in them without closing lines.
- **A closing line after text**: Write the closing line whenever a block contains text and something follows that belongs further out, like the text of the page after a card.
- **Every closing line stays valid**: A page with all its closing lines means the same as the page in the short form. The "Create" button of the page editor writes a closing line for every block that it creates. A cell or a node that is added in edit mode is written like those beside it, see [Editing in the Page Editor](#editing-in-the-page-editor).

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

**A closing line with too few colons.** It closes a block further out, and the block that it was meant for ends there as well. The line that was written to close the outer block then finds no open block. Here line 4 should have three colons:

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
Line 6: This line of 2 ':' characters closes no block. No block is open here.
~~~~

**A head line with one colon too few in a layout.** The card has three colons like the `Item` before it. Its head line ends the item, so the card stands directly in the layout, which takes no content outside of its items. The box names both lines and the colons to write:

<!-- sample: error -->
~~~~markdown
:: HomeBlaze.Components.FlowLayout MinWidth=320
::: Item
::: HomeBlaze.Components.Card Title="Water Pump"
The card was meant for the first cell.
~~~~

~~~~text
Line 3: This block has as many ':' characters as the slot block 'Item' at line 2, so it is not inside that block but directly inside FlowLayout, which has no default slot. Write '::::' here to put it inside 'Item'.
~~~~

**A head line with one colon too few in a card.** The motor has two colons like the card. Its head line ends the card, and the motor is a block of its own below the card. The page cannot tell this from two blocks that are meant to follow each other, so it shows no box. Give the motor three colons to put it into the card:

~~~~markdown
:: HomeBlaze.Components.Card Title="Outer Card"
The body of the card.
:: HomeBlaze.Samples.Motor#shortmotor Name="Short Motor" TargetSpeed=800
~~~~

When both blocks have their closing lines, the same mistake does show, because the line that was written to close the card finds no open block. Here the motor needs three colons on both of its lines:

<!-- sample: error -->
~~~~markdown
:: HomeBlaze.Components.Card Title="Outer Card"
:: HomeBlaze.Samples.Motor#closedmotor Name="Short Motor" TargetSpeed=800
::
The text after the motor.
::
~~~~

~~~~text
Line 5: This line of 2 ':' characters closes no block. No block is open here.
~~~~

**A wrong colon line inside a widget that has no place for content.** A closing line that closes nothing and a head line that cannot be read get their box where they stand, for example in the body of the card around them. A `FlowLayout` and a `TabLayout` take no loose content, and neither does a card whose body stands in a `Content` block. Such a widget has no place for the box, so the box replaces the whole widget. It names the line and has the message of the block that is wrong, and with several such blocks there is one box for each. A head line that cannot be read, like `::: Tab Title = "Cooling Fan"` with spaces around the `=`, replaces its `TabLayout` in the same way. Here the head line of the second `Item` has spaces around the `=`:

<!-- sample: error -->
~~~~markdown
:: HomeBlaze.Components.FlowLayout MinWidth=320
::: Item
The first cell.
:::
::: Item Width = 2
The second cell.
:::
::
~~~~

~~~~text
Line 5: Expected key=value but found 'Width = 2'.
~~~~

#### Colon Lines That Are Text

- **Inside markdown code**: A colon line inside a fenced code block or an indented code block is text. It opens and closes nothing, also when the code block is inside a block. One case differs: a code block directly after an item of a definition list, without an empty line between them, is cut short by markdown at the first line that starts with a colon. A colon line there is then read as a colon line of the page: it can open a block, or close the block around the code without any error box. A code block directly after a definition list needs an empty line before it.
- **Text after the colons that cannot be a name**: A colon line is a head line only when the text after the colons starts with a letter, `_` or `{`. A line like `:: 12:30 lunch` or `::)` is ordinary text.
- **Text after the colons that starts with a letter**: A line like `:: note` is read as a head line and is shown as an error, because `note` is no type name. Like every head line it ends the blocks before it that have as many colons or more. Inside a card with two colons such a line ends the card and takes the closing line of the card for itself, see the second sample below.
- **The backslash**: To start a line of text with colons in every case, write a backslash before the first colon: `\:: note`.
- **One colon**: A line that starts with one colon is never a colon line of a block, and colons inside a line have no meaning.

~~~~markdown
:: HomeBlaze.Components.Card Title="Lunch Plan"
:: 12:30 lunch

\:: note that both lines of this card are text
::
~~~~

Without the backslash, line 2 of this card is read as a head line. It ends the card, so the card is shown empty, and its box replaces the text up to the closing line:

<!-- sample: error -->
~~~~markdown
:: HomeBlaze.Components.Card Title="Lunch Plan"
:: note that this line is read as a head line
The body of the card.
::
~~~~

~~~~text
Line 2: Expected key=value but found 'that this line is read as a head line'.
~~~~

### Content and Slots

A widget can take page content: text, expressions and other blocks. Its type declares the places for content as slots. A type has at most one default slot and any number of named slots, and each widget on [Page Widgets](page-widgets.md) lists its slots.

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

**A slot whose blocks create a subject.** Some slots do not hold content directly. Each of their blocks creates a subject of a type that the widget chose, and the block takes the arguments of that type, an optional `#name` and a `json` block, like a widget block. The content of the block goes to the default slot of that subject. The `Tab` of a `TabLayout` is such a slot: each `Tab` block creates a tab with a `Title` and an `Icon`. The `Cell` of a `GridLayout`, the `Node` of a `CanvasLayout`, the `Panel` of `ExpansionPanels` and the `Value` of a `ValueList` work the same way, and so does the `Case` of a `ValueMap`, which takes arguments but no content.

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
- **Content only**: A slot that holds content takes no arguments, no `#name` and no `json` block. `Footer`, `HeaderRight`, `Content` and `Item` are such slots.
- **Empty lines are no content**: A block with nothing but empty lines in it has no content. Everything else is content, also an HTML comment. One case differs: where a widget has no place for loose content, for example between the `Item` blocks of a `FlowLayout`, an HTML comment is left out and is no error.
- **An empty slot block**: A slot block without content still fills its slot. A card with an empty `Footer` block shows the divider of the footer with nothing below it.

#### Slot Errors

A mistake in how the slots of a widget are filled replaces the whole widget with one box. The box names the line that has to be fixed.

**Content in a widget without a default slot.** A motor declares no slot, so its block must stay empty. The layouts have no default slot either: all the content of a `FlowLayout`, a `TabLayout`, a `GridLayout` and a `CanvasLayout` stands in `Item`, `Tab`, `Cell` or `Node` blocks. When the block has no closing line and no slot block follows the content, the box also names the closing line that ends it, see [When a Closing Line Is Needed](#when-a-closing-line-is-needed).

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
Line 4: Card has no slot 'Footr'. Available: Content, Footer, HeaderRight.
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

### The `json` Block

A list, an object, a subject or a text with a line break does not fit on the head line. Write such configuration as a JSON object in a fenced code block with the language `json` that is the first thing inside the block. This page calls it the `json` block.

On a page, no widget that ships with HomeBlaze takes a list, an object or a subject as configuration, so the only configuration of these widgets that needs a `json` block is a text with a line break. The `json` block is there for every other type with such values, for example a device whose configuration has a list.

The `Subtitle` of a `Card` is text. Here it has a line break, which an argument cannot hold:

~~~~markdown
:: HomeBlaze.Components.Card#note Title="Note"
```json
{ "Subtitle": "First line\nSecond line" }
```
::
~~~~

The `json` block itself is not shown, and a card shows its subtitle as running text, so the line break is a space there.

- **Where**: The `json` block is the first thing inside its block. Empty lines before it do not count. It is written with three or more backticks or tildes and the language `json` in lower case. A fenced code block with `json` anywhere else is ordinary content and is shown as code.
- **Which blocks**: A widget block can have a `json` block, and so can a slot block that creates a subject, like a `Tab`.
- **Names**: The names in the object are property names like the keys of the arguments, here `Subtitle` of the card. This holds at every depth of the object, also for the properties of a subject that is nested in a value.
- **`$type`**: A subject inside the object names its type with `$type`, which is spelled the same as in a JSON file. It must be the first name in the braces of that subject. At the top level of the object, a `$type` is ignored, because the type of the block is on its head line.
- **Argument and `json` block**: Both can be used together. When both set the same property, the argument wins.

What the page checks in a `json` block, and what it does not:

- **Checked**: The text must be one valid JSON object without a name that stands twice. A name at the top level of the object that the type does not have is an error like an unknown argument, with the same hint when only the case differs. The property of a slot, like `Cells` of a `GridLayout`, is such a name as well: the box then says how the slot is filled.
- **Values are checked**: A value at the top level of the object that does not fit its property is an error, and the block is not created. The exception is `null`, see the next point. For a single value the message is the one of an argument and names the expected type. For a list or an object the message names the property and the place inside the value where the reader of the JSON stopped. If the `$type` of a nested subject names no type, the whole top level value that contains it is such an error. The same holds for a value deeper in the object that cannot be converted, like text for a number of a nested subject. A nested subject without a `$type` is an error as well, and so is one whose `$type` is not the first name in its braces: the message names the place of the subject and says that it needs its `$type` there. When several values do not fit, the box shows the first of them only: for a `GridLayout` with `{ "Rows": "three", "Columns": "four" }` it names `Rows`, and `Columns` follows once `Rows` is right.
- **`null` is a value**: At the top level, `null` is applied without a message to every property that can be empty. That is every property that holds text, a list or an object, and a number, a yes or no or a duration that its type declares as optional, like `MinHeight` of a `CanvasLayout`. The property is then empty, also where the type gives it a value to start with. For a number, a yes or no or a duration that must have a value, like `Rows` of a `GridLayout`, `null` is an error like any value that does not fit. To leave a property at its default, leave its name out.
- **Names are not checked below the top level**: A name deeper in the object that is no property of its type is skipped without a message, and the property keeps its default. This is the trap when you paste from a `*.json` configuration file, which spells the names in camelCase: `"path"` in a nested subject whose property is `Path` is skipped, and the subject has no path. When a nested value is missing on the page, check the spelling and the case of its name.

A value that does not fit, here text for the number of rows. The grid is not shown:

<!-- sample: error -->
~~~~markdown
:: HomeBlaze.Components.GridLayout#rowgrid Columns=2
```json
{ "Rows": "three" }
```
::
~~~~

~~~~text
Line 1: 'three' is not a valid value for 'Rows'. Expected a value of type Int32.
~~~~

The page editor writes configuration in a fixed way, see [Editing in the Page Editor](#editing-in-the-page-editor): text without a line break, numbers, yes or no and durations go on the head line, and lists, objects and text with a line break go into the `json` block.

### Style Blocks

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

### Names and the Page

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

### Where a Block Is Allowed

A widget block is allowed in three places: directly on the page, directly inside another widget block and directly inside a slot block. A slot block is allowed directly inside the widget block that declares the slot. A style block is allowed everywhere.

- **Lists and quotes**: A widget block inside a list item or a quote is shown as an error, and so is a slot block. A colon line inside a list item or a quote never ends a block outside of it: a head line there ends no block around the list item or quote, and a closing line there is shown as an error when nothing is open inside the list item or quote. A colon line that is indented like the text of a list item belongs to that list item. Without indentation it ends the list.
- **Style blocks**: A widget block inside a style block is shown as an error.
- **Markdown code**: A block inside a fenced code block or an indented code block is shown as text and creates nothing, which is how this page shows its samples.
- **Between HTML tags**: Do not put a block between an opening HTML tag and its closing tag, for example between `<div>` and `</div>`. The page does not report this, but the widget ends up outside the element and the layout breaks. Outside of every block, the lines of a block that follows the tag without an empty line are shown as text. Use a `Card`, a `FlowLayout` or a `TabLayout` to arrange widgets.
- **HTML inside a block**: HTML inside the content of a block does not need an empty line before the next colon line. A head line ends the HTML and is read as a head line, and a closing line with exactly as many colons as an open block around it closes that block. Inside a multi-line HTML comment and inside a `<pre>`, a `<script>` or a `<style>` element a head line is part of the element, so a line like `::selection { color: red }` in a `<style>` element stays what it is. A closing line with the colons of an open block closes that block there as well. Keep such closing lines out of HTML.
- **Other closing lines after HTML**: A closing line of another length directly after HTML, without an empty line, is part of the HTML. It is shown as text and gives no error.

---

## Widgets

These widgets ship with HomeBlaze and are made for pages. [Page Widgets](page-widgets.md) is their reference: the arguments, the slots, the samples and the limits of each. That page shows each widget live, with the source of every sample above its result.

| Widget | What it is for | Reference |
|--------|----------------|-----------|
| `Card` | A titled box around page content, with a subtitle, a link, a header corner and a footer if you want them | [Card](page-widgets.md#card) |
| `FlowLayout` | Cells next to each other that wrap to the width of the screen, for example cards side by side | [FlowLayout](page-widgets.md#flowlayout) |
| `TabLayout` | Several tabs with page content, one of them shown at a time | [TabLayout](page-widgets.md#tablayout) |
| `Widget` | The widget of a subject that exists somewhere else: in a file of its own, or in another block of the page | [Widget](page-widgets.md#widget) |
| `GridLayout` | A grid with a fixed number of rows and columns and page content in each cell | [GridLayout](page-widgets.md#gridlayout) |
| `CanvasLayout` | An area on which page content is placed at any position | [CanvasLayout](page-widgets.md#canvaslayout) |
| `ValueMap` | Text, colour and icon per value, read by the value widgets. It shows nothing itself | [ValueMap](page-widgets.md#valuemap) |
| `ValueList` | Rows that each show a label and a value, for example the values of a device | [ValueList](page-widgets.md#valuelist) |
| `ValueTile` | A small box with a label and one large value | [ValueTile](page-widgets.md#valuetile) |
| `Indicator` | A state as a coloured chip or as an icon | [Indicator](page-widgets.md#indicator) |
| `Progress` | A number as a bar between a minimum and a maximum | [Progress](page-widgets.md#progress) |
| `ExpansionPanels` | Page content in panels that open and close | [ExpansionPanels](page-widgets.md#expansionpanels) |
| `Alert` | A highlighted box for a note or a warning | [Alert](page-widgets.md#alert) |

The names for an `Icon` are described under [Icons](page-widgets.md#icons), and the names for a colour under [Colours](page-widgets.md#colours).

Every other type that can be configured works as a block as well, for example the sample motor or a device: its arguments are its configuration properties, and it shows the widget that its type has. A type without a widget shows the notice "No Widget component for" with its name.

---

## Errors

A block that cannot be shown is replaced by a red box with the line number of the block and a message. A closing line that closes nothing gets a box at its own line. The rest of the page still renders.

- **One box per block**: The box replaces the block with everything in it. The blocks inside it are not created, and they are created again once the error is fixed.
- **A block without a closing line**: For such a block, "everything in it" is everything up to the line that ends it: the next head line with as many colons or fewer, the closing line of a block around it, or the end of the page.
- **Errors in slots**: An error in a slot block, such as a `Tab`, a `Footer` or an `Item`, is shown in a box that names the line of the slot block. The box replaces the whole widget around it. One case differs: when the colon lines or the head line of the slot block are wrong and the widget has a default slot that takes loose content, like a `Card`, the widget is still shown and the box stands in its content where the block was. A widget without a place for loose content has no place for the box, so it is replaced, with one box for each such block in it. That is every widget without a default slot, like the layouts, and also a `Card` whose body stands in a `Content` block.
- **Failures of a subject**: When the subject of a block fails while it is created or configured, the box shows the message of that failure, and the log of HomeBlaze has the details.

[Page Errors](page-errors.md) lists every message with its cause and its fix.

---

## What an Edit of the Page Keeps

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
- **Subjects in the `json` block**: A subject nested in a value of the `json` block is created again on every reload of the page. The block itself is kept, but its configuration is applied again, and that creates the subjects in it anew.
- **Tabs, cells and nodes**: A `Tab`, a `Cell` of a `GridLayout` or a `Node` of a `CanvasLayout` with a name keeps itself and everything inside it when others are added, removed or moved. Those without a name are matched by their position among those without a name. Adding, renaming or removing the name creates it again once.
- **Flow items**: An `Item` cannot have a name, so items are matched by their position. A subject inside an item is created again when an item is added or removed before it, also when the subject has a name.
- **Errors**: Everything inside a block that was shown as an error is created again once the error is fixed.

Give every subject that has state a name, and name the blocks around it too: the card, the tab, the cell and the layout. A subject that must keep its state under all changes belongs in a file of its own, shown on the page through a `Widget` block.

---

## Editing in the Page Editor

The page editor shows the text of the page. It marks the head line of every widget block and of every slot block that creates a subject, together with its `json` block, and it marks every expression.

- **Edit a block**: With the cursor on a marked head line, the toolbar shows a button "Edit" followed by the name of the block, or by its type when it has no name. It opens the edit dialog of the subject. Saving the dialog writes the configuration back into the block and saves the page. Only the head line and the `json` block change, the content of the block stays as it is, and a block without a closing line gets none.
- **Edit an expression**: With the cursor in an expression, the toolbar shows the button "Edit Expression". It opens a dialog to choose another path, writes the path into the expression and saves the page.
- **Create a block**: The "Create" button adds a new block at the line of the cursor and saves the page. The block gets a head line and a closing line, with two colons directly on the page and one colon more than the block that it is then inside of. That is the innermost block around the cursor that has a closing line of its own or takes a widget block as content. A block around the cursor that has neither, like a motor or a `Widget` without a closing line, is ended by the head line of the new block, so the new block stands after it and not inside it. A block that still has slot blocks of its own after the cursor is never ended, so a layout keeps its `Item` and `Tab` blocks: with the cursor between them the new block stands in the item or tab before the cursor when that one is still open, and otherwise directly in the layout, which takes no content there. A `json` block stays the first thing inside its block: with the cursor before it, on it or inside it, the new block goes after it. The editor keeps an empty line before and after the new block.
- **Save first**: "Edit" needs the saved page and the text in the editor to be the same from the start of the page to the end of the block's head line and `json` block. When that part has unsaved changes, the editor asks you to save the page first. Unsaved text after the block is saved together with the edit.

The steps of "Create":

1. Put the cursor on the line where the block should go and click "Create". The dialog "Create subject" opens with the list of the types. The search box and the groups narrow the list.
2. Click a type. The dialog now shows the field "Block name" and the fields of the type, and its title names the type, like "Create Motor". "Back" returns to the list.
3. Check the field "Block name". Its value becomes the `#name`. The dialog suggests the name of the type, with a number after it when the page already uses that name, like `Motor2`. The dialog checks the name while you type: it starts with a letter or `_`, contains only letters, digits, `_` and `-`, and is not used on the page yet. The dialog also rejects a name that differs from a used one only in upper and lower case.
4. Fill in the fields of the type. "Create" stays disabled until the block name is valid and every required field is filled in.
5. Click "Create". The new block stands at the line of the cursor, and the page is saved already.

What the editor writes:

- **On the head line**: Text without a line break, numbers, yes or no and durations, each as one argument. A text is put in double quotes only when it needs them.
- **In the `json` block**: Lists, objects and text with a line break. When nothing of that kind is left, the `json` block is removed.
- **Names**: Both are written with the property names.
- **All values**: Every configuration property that has a value is written, also one that the block did not set before. Values that are not set are left out.
- **Colons**: The head line keeps its number of colons and its `#name` and gets one space after the colons. The arguments are written anew, so arguments that you wrote by hand can change their order.

Editing a subject elsewhere in the UI, for example through the edit button of its widget, writes its block in the same way.

While the page is in edit mode, which is with "Split" or "Inline" in the menu at the top right of the page, a `GridLayout` shows a plus in every free place, and a click on it adds a cell there. A `CanvasLayout` adds a node with a click on a free place of the canvas. Each cell and each node has buttons to edit and remove it, and a node can be moved with the mouse. Each of these writes the `Cell` and `Node` blocks of the page and saves the page:

- **Add**: The click opens the dialog "Create subject". The new cell or node is a `Cell` or `Node` block after the last one of its layout, with the widget that you chose as a block inside it. It is written like the block before it: with as many colons, and with a closing line only when that block has one. In a layout without a cell or a node, the new block has one colon more than the layout. It then gets a closing line when every widget block and slot block of the page has one, and none otherwise.
- **Edit and move**: Editing a cell or a node, or moving a node, writes the head line of its block again, with all its arguments. A node that had only `X` and `Y` has `Width` and `Height` on its head line afterwards. The content of the block stays as it is.
- **Remove**: Removing a cell or a node removes its block with everything in it. Where the text before the block and the text after it would then stand directly below each other, one empty line stays between them.
- **A change that the page cannot keep**: When the page cannot keep a change, nothing is written, and the notification "Configuration applied but not saved. It will be lost on restart." appears. The layout shows the change until the page is read again, which every save of its text and a restart do. Examples of when this happens: adding or removing the block would change another block or other text of the page; the cells or nodes were put into another order outside of the page, for example by a method or another tool; or the page is being read again at that moment. The order of cells and nodes is the order of their blocks, so change it in the text of the page.

Use these buttons with "Inline". In "Split", the editor next to the page does not show what the buttons wrote until the page is opened again, so do not save the text of the editor after using them, because it would write its older text over theirs.

---

## Not Supported

- More formats for expressions than `display` and `raw`, like another unit, another number of decimals or conditions.
- Expressions in arguments. The value of an argument is fixed text.
- Short names for types. A block always has the full type name.
- A name for an `Item` of a `FlowLayout`, and with it a subject inside an item that survives a change of the items before it.
- A check of the names below the top level of a `json` block.
- A widget block inside a list item, a quote, a style block or an HTML element.
- Blocks of one page that are shared with another page. Keep a shared subject in a file of its own and show it with a `Widget` block.

---

## Cells and Nodes From the `json` Block

Pages that were written for an earlier version have the cells of a `GridLayout` as `Cells`, and the nodes of a `CanvasLayout` as `Nodes`, in the `json` block of the layout. Such a layout now shows an error box that names the block form. Convert each one by hand.

Before:

<!-- sample: error -->
~~~~markdown
:: HomeBlaze.Components.GridLayout#machines Rows=1 Columns=2
```json
{
  "Cells": [
    { "Row": 0, "Column": 0, "Child": { "$type": "HomeBlaze.Components.Widget", "Path": "../Conveyor" } },
    { "Row": 0, "Column": 1, "Child": { "$type": "HomeBlaze.Samples.Motor", "Name": "Grid Motor", "TargetSpeed": 900 } }
  ]
}
```
::
~~~~

~~~~text
Line 1: 'Cells' is written as blocks on a page: one 'Cell' block per value inside this block.
~~~~

After:

~~~~markdown
:: HomeBlaze.Components.GridLayout#machines Rows=1 Columns=2
::: Cell Row=0 Column=0
:::: HomeBlaze.Components.Widget Path=../Conveyor
::: Cell Row=0 Column=1
:::: HomeBlaze.Samples.Motor Name="Grid Motor" TargetSpeed=900
::
~~~~

- Every entry of `Cells` becomes one `Cell` block inside the layout, and every entry of `Nodes` one `Node` block, in the order of the list.
- The numbers of the entry become the arguments of that block: `Row`, `Column`, `RowSpan` and `ColumnSpan` for a cell, and `X`, `Y`, `Width` and `Height` for a node.
- The `Child` of the entry becomes a block inside the `Cell` or `Node` block, with one colon more. The value of its `$type` becomes the type name, its single values become arguments, and every other value goes into a `json` block of that block. An entry without a `Child` becomes a block without content.
- The earlier editors wrote `"Cells": []` or `"Nodes": []` into the `json` block of every layout that they saved, and an empty list is an error box as well. Remove it, together with the `json` block when nothing else is in it.
- A layout in a `*.json` configuration file of its own does not change.

---

## The Old `subject(name)` Form

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

## Example: A Dashboard Page

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
Temperature: **{{ /Demo/CoolingFan/Temperature }}**

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

- [Page Widgets](page-widgets.md) - Reference of the widgets for pages
- [Page Errors](page-errors.md) - Every error message with its cause and its fix
- [Subjects, Storage & Files](subjects.md) - Configure subjects and file layout
- [Paths](paths.md) - Path syntax reference
- [Building Custom Subjects](../development/building-subjects.md) - Create subject types and widgets with content slots
