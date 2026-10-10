# Markdown blocks and slots for HomeBlaze pages

Design for widget blocks with content slots in HomeBlaze markdown pages. This replaces the `subject(name)` code fence. It is a breaking change to the page syntax and all pages in the repository are migrated in the same change.

## Problem

A page can embed a subject today, but a widget cannot contain page content.

- The parser turns a page into a flat list of segments (HTML, expression, subject) stored in `MarkdownFile.Children`. The page component renders them as siblings, so nothing can wrap anything.
- Subjects are embedded with a fence that holds JSON (`` ```subject(name) ``). There is no place for markdown content, and no way to nest one widget in another except through JSON configuration (`GridCell.Child`).
- Layout therefore needs raw HTML and CSS written into the page. The live dashboard page is about 40 KB of generated HTML for this reason.
- Three places know the fence syntax through their own regular expression: `MarkdownContentParser`, `MarkdownFile.RebuildMarkdownContent` and the editor (`MarkdownDecorationParser`, `MarkdownFileEditComponent`).
- `MarkdownContentParser.ExtractTypeName` reads `"type"` although the discriminator is `"$type"`. The reuse check in `ReconcileChildrenAsync` therefore never matches and every file change recreates all embedded subjects.

## Goals

- Inline values stay `{{ path }}`.
- Widgets become `:::` blocks whose content is ordinary markdown, with expressions and further blocks inside.
- A widget type declares where content goes through slot properties. There is one default slot and any number of named slots, single or list.
- One parser for the block syntax, shared by the graph builder, the write-back and the editor.
- Ship three slot widgets: `Card`, `FlowLayout`, `TabLayout`.

## Non-goals

- Formatting in `{{ }}` (units, rounding, conditions).
- `GridLayout` cells written as blocks. Its cell editor writes JSON and would have to generate markdown.
- Lists whose items have different concrete types.
- Short aliases for type names.

## Syntax

### Inline expressions

Unchanged: `{{ path }}` anywhere in the text flow, including inside HTML attributes and inside block content.

### Blocks

A block is a Markdig custom container. It opens with a head line and closes with a line of colons.

```markdown
:::HomeBlaze.Components.Card title="Schlafzimmer" icon=Bed
Ruhig, unter dem Dach. Aktuell **{{ /House/DG/Sensor/Temperature }} °C**.
:::
```

- The fence has at least three colons. `::text::` is Markdig's inline span and is not a block.
- A block that contains other blocks needs a longer fence than every block inside it. With equal lengths the first closing line closes the outer block.
- A block always needs its closing line, also when it is empty.

### Head line grammar

```
head      = fence name [ "#" id ] { whitespace argument }
fence     = ":::" { ":" }
name      = type-name | slot-name
type-name = full .NET type name, contains at least one "."
slot-name = identifier without "."
id        = letter or "_", then letters, digits, "_" or "-"
argument  = key "=" value
key       = JSON name of a [Configuration] property (camelCase)
value     = quoted | bare
quoted    = '"' characters '"'   with \" and \\ as escapes
bare      = characters without whitespace and without '"'
```

All names are case-sensitive.

The head line is parsed from the page source, not from Markdig's `Info` and `Arguments`. Markdig's generic attribute extension removes `{...}` from the arguments, so `path="/a/{b}"` arrives truncated there. The source span of the block gives the exact line.

### Kinds of blocks

| Head | Kind | Meaning |
|---|---|---|
| Name with a dot | Widget block | Creates a subject of that type. Arguments are its configuration. |
| Name without a dot | Slot block | Fills the slot of that name on the enclosing owner type. |
| No name, with `{.class}` | Style block | Markdig's default: a `<div>` with those attributes. |
| No name, no attributes | Error | This is what a miscounted fence produces. |

### Arguments and configuration

- Each argument sets one `[Configuration]` property of the created subject. The key is the JSON property name the serializer already uses.
- The value is typed by the target property. A `string` property always gets a string. A numeric or `bool` property gets the bare literal. Every other type (enum, `TimeSpan`) gets a JSON string and the existing converters apply.
- An unknown key is an error.
- Configuration that is not a scalar goes into a `json` fence that is the first child of the block. It holds a JSON object. Arguments win over the fence when both set the same key.

````markdown
:::HomeBlaze.Components.GridLayout#grid rows=1 columns=4
```json
{ "cells": [] }
```
:::
````

The arguments and the fence are merged into one JSON object with `$type` and handed to the existing `ConfigurableSubjectSerializer`. The serializer does not change.

### Slots

A type declares slots with `[Slot]` on properties.

```csharp
[Slot(IsDefault = true)] public partial IInterceptorSubject? Content { get; set; }
[Slot]                   public partial IInterceptorSubject? Footer { get; set; }
[Slot("Tab")]            public partial Tab[] Tabs { get; set; }
[Slot("Item")]           public partial IInterceptorSubject[] Items { get; set; }
```

- The slot name is what one block is called. It defaults to the property name. For a list the singular is set explicitly.
- A slot name contains no dot. A type used on a page needs a namespace. That is how the two are told apart.
- A slot property is not configuration and is never serialized.

Rules:

1. **One block, one value.** A slot block always produces exactly one value for its property.
2. **The property type decides the value.**
   - Open type (`IInterceptorSubject`): the value is a `MarkdownFragment` holding the block content. Arguments and `#id` are errors.
   - Concrete type (`Tab`): an instance is created, the arguments configure it, and the block content goes into that instance's default slot.
3. **Single or list.** A single property takes one block and a second one is an error. An array property appends one value per block in source order.
4. **Default slot.** Everything inside a widget block that is not a slot block goes to the default slot. The default slot is single and of the open type. A type has at most one. If the content is empty the property is `null`. The default slot can also be filled by a block with its name, but not by both a block and loose content.
5. **Owner type.** Slot names are looked up on the owner of the surrounding content: the widget type for a widget block, the value type for a slot block of a concrete type. Content of an open slot has no owner, so a slot block there is an error.

### Examples

Card with a footer:

```markdown
::::HomeBlaze.Components.Card title="Heizung" icon=Thermostat
Wärmepumpe im UG.

:::Footer
[Notizen](House/UG/Heizung.md)
:::
::::
```

Tabs, where each `Tab` block creates a `Tab` and fills its content:

```markdown
:::::HomeBlaze.Components.TabLayout
::::Tab title="Heizung" icon=Thermostat
Warmwasser {{ /House/UG/Heizung/HotWaterTemperature }} °C.

:::HomeBlaze.Components.Widget path="/House/UG/Heizung"
:::
::::
::::Tab title="Strom" icon=Bolt
Aktuell {{ /House/UG/Zaehler/TotalPower }} W.
::::
:::::
```

Flow layout, where each `Item` block is one cell:

```markdown
:::::HomeBlaze.Components.FlowLayout minWidth=320
::::Item
:::HomeBlaze.Components.Card title="Schlafzimmer" icon=Bed
Ruhig, unter dem Dach.
:::
::::
::::Item
:::HomeBlaze.Components.Card title="Büro" icon=Computer
Arbeitsplatz.
:::
::::
:::::
```

Inline subject with a name, referenced by an expression:

```markdown
:::HomeBlaze.Samples.Motor#mymotor name="MyMotor" targetSpeed=3000 simulationInterval=00:00:01.500
:::

Speed: {{ mymotor/CurrentSpeed }} RPM
```

### Where a block is allowed

A widget block is valid directly on the page, directly inside a widget block, or directly inside a slot block. Anywhere else (list item, table, blockquote, style block) it is an error.

The reason is rendering: the HTML before and after a widget is emitted as separate chunks and each chunk must be balanced. Markdig guarantees that only for these positions.

One existing limit stays: a block placed between a raw `<div>` and its `</div>` splits the raw HTML into unbalanced chunks. The parser cannot see this. It is already true for subject fences today.

### Errors

Every error renders as a visible box in place of the block, with the line number and a message. Nothing falls back to a silent `<div>`.

| Situation | Message contains |
|---|---|
| Unnamed block without attributes | hint that an outer fence must be longer than the inner ones |
| Unknown type name | the name |
| Slot name not declared by the owner type | the available slot names |
| Slot block without an owner (page level, open slot content) | the name |
| Widget block in a position that is not allowed | the position |
| Unknown argument key | the available keys |
| Malformed head line | the offending part |
| Second block for a single slot | the slot name |
| Content in a widget whose type has no default slot | the type name |
| Arguments or `#id` on an open slot | the slot name |
| Duplicate `#id` on the page | the id |
| Old `subject(name)` fence | how to convert it |

## Semantics

### Identity and reuse

Each widget subject has a key inside its fragment: the `#id` when given, otherwise the type's short name plus the position (`Card_3`). When a page is parsed again, a subject with the same key and the same type is kept and updated in place: configuration is applied through `UpdateConfiguration` and `ApplyConfigurationAsync`, and its slots are reconciled the same way, recursively. This is what makes a running `Motor` survive an edit elsewhere on the page.

Subjects without `#id` are positional. Inserting a block above them recreates them. Stateful subjects should be named.

### Page scope

- **Names.** `#id` is unique per page, whatever the nesting depth. The page keeps a table from id to subject. An expression whose first segment is an id resolves from that subject. Top-level named subjects keep their place in `MarkdownFile.Children`, so `/Page.md/mymotor` still works.
- **Relative paths.** Every relative path in page content resolves from the page, however deeply the block is nested. Wrapping a block in a card does not change its paths.

`Widget` resolves its path from itself today while expressions resolve from the page. With page scope `Widget` resolves from its page when it was created by one and from itself otherwise, for example inside a JSON-configured `GridLayout`. The demo page changes from `../../Conveyor` to `../Conveyor`.

## Architecture

### Components

| Unit | Project | Responsibility |
|---|---|---|
| `SlotAttribute` | `HomeBlaze.Abstractions` | Marks a slot property. `Name`, `IsDefault`. |
| `MarkdownPageParser` | `HomeBlaze.Storage` | Page text to block tree with source spans. Pure, no subjects. Owns the head line grammar. |
| `MarkdownBlockWriter` | `HomeBlaze.Storage` | Replaces the head line and the `json` fence of one block in the page text. Inverse of the head line grammar. |
| `MarkdownContentParser` | `HomeBlaze.Storage` | Block tree to subjects: creates, reuses, fills slots, builds the name table. |
| `MarkdownFragment` | `HomeBlaze.Storage` | Subject holding an ordered set of children, same shape as the page. |
| Scoped path resolution | `HomeBlaze.Services` | A separate entry point next to the existing resolver methods. |
| `MarkdownContentView` | `HomeBlaze.Storage.Blazor` | The render loop, shared by the page and the fragment widget. |
| `Card`, `FlowLayout`, `TabLayout`, `Tab` | `HomeBlaze.Components` | The first slot widgets. |

`HomeBlaze.Components` does not reference `HomeBlaze.Storage`. A slot widget therefore renders its slot with `<SubjectComponent Subject="..." Type="Widget" />` and never needs to know what a fragment is. This is the pattern `GridCell.Child` already uses.

### Page parser

Input is the page text after the frontmatter. Output is a tree.

```
PageDocument
  Blocks        top-level blocks in order
  Errors        problems that are visible from syntax alone

BlockNode
  Name, Id, Arguments          parsed head
  FenceLength                  to write the block back unchanged
  HeadSpan                     the head line in the source
  ConfigurationJson, ConfigurationSpan   the optional json fence
  Children                     nested blocks in order
  Position                     page, block, or not allowed
```

Verified in a spike against Markdig 0.45.0 with `UseAdvancedExtensions().UsePreciseSourceLocation()`:

- `CustomContainer.Span` covers the block from the opening fence to the closing fence, and `OpeningFencedCharCount` gives the fence length.
- `Info` contains the `#id` (`HomeBlaze.Components.Tabs#tabs`) and `Arguments` loses `{...}`. Both are ignored and the head line is read from the source.
- A container in a list item has `ListItemBlock` as its parent. A `:::` line inside a raw HTML block is not parsed as a container.
- The old fence is a `FencedCodeBlock` whose `Info` starts with `subject(`.
- A `json` fence as the first child keeps its own span.

### From tree to segments

The whole page is rendered to HTML once. A custom renderer for `CustomContainer` writes `<!--BLOCK:n-->` before and `<!--/BLOCK:n-->` after the rendered children of every widget and slot block, where `n` indexes the block in document order, and skips the `json` fence. Style blocks keep Markdig's default renderer. A splitter with a stack then cuts the HTML into runs per block, and the existing expression pattern splits each run into HTML and expressions. This is the technique the subject marker uses today, one level deeper.

### Subjects

```
Page.md  MarkdownFile
  Children
    Html_0_…          HtmlSegment
    Card_1            Card
      Content         MarkdownFragment
        Children
          Html_0_…        HtmlSegment
          Expression_1_…  RenderExpression
      Footer          MarkdownFragment
    mymotor           Motor
```

- `MarkdownFragment.Children` is an `[InlinePaths]` dictionary like `MarkdownFile.Children`, with the same keys for HTML and expression segments.
- A slot is assigned through the property. Values for an array slot are assigned as one new array.
- `RenderExpression` keeps the page as its parent at every depth, which is what gives expressions page scope.

### Rendering

`MarkdownContentView` takes a children dictionary and renders it the way `MarkdownFilePageComponent.GetRenderSegments` does today: consecutive HTML and expression segments are concatenated into one markup string and every other child is rendered through `SubjectComponent`. The page component and the widget component of `MarkdownFragment` both use it.

Error boxes are HTML in the run and need no subject type.

### Write-back

`MarkdownFile.WriteConfigurationAsync` is called when a subject is edited in the UI. Today it rewrites every fence on the page. With blocks:

1. The parser keeps a map from each created subject to its `BlockNode`.
2. The subject is serialized with the existing serializer.
3. `MarkdownBlockWriter` splits the result: scalar properties go on the head line, objects and arrays into the `json` fence, `null` is omitted. A value is quoted when it contains whitespace, `"` or `=`, or when it is empty.
4. Only the head line span and the fence span are replaced. The fence length, the `#id` and all block content stay byte-identical.

The same writer serves widget blocks and slot blocks of a concrete type.

### Editor

`MarkdownDecorationParser` uses `MarkdownPageParser` for its regions: one per widget block and concrete slot block (the head line), one per expression. The edit button opens the existing dialog and applies the result with `MarkdownBlockWriter`. "Insert subject" inserts a widget block with a three-colon fence.

### MCP

`ReadEvaluatedContent` replaces expressions in the raw text. It switches to the scoped resolution so that names inside blocks resolve, and it trims the path, which the HTML path already does.

## New widgets

| Type | Configuration | Slots |
|---|---|---|
| `Card` | `Title`, `Icon` | `Content` (default), `Footer` |
| `FlowLayout` | `MinWidth` in pixels, default 320 | `Items` as `Item`, open type |
| `TabLayout` | none | `Tabs` as `Tab`, type `Tab` |
| `Tab` | `Title`, `Icon` | `Content` (default) |

- `FlowLayout` lays its items out left to right in columns of equal width that are at least `MinWidth` wide, and wraps. One column on a phone, several on a desktop.
- `TabLayout` is not called `Tabs` because a C# member cannot have the name of its enclosing type.
- `Tab` has no widget component of its own. `TabLayout` renders the tabs.

## Migration

- Pages in the repository that use the old fence: `Data/Files/Demo/Inline.md`, `Data/Files/Devices.md`, the two E2E test pages `TestData/Demo/Grid.md` and `TestData/Demo/Inline.md`, and the documentation pages `pages.md`, `paths.md` and `architecture/design/storage.md`.
- The old fence renders an error box that shows the new form. Without it the fence would silently become a code block.
- The live dashboard page is converted when the server is updated. That is outside the repository.

## Testing

- **Page parser.** Tree shape, spans, head line grammar (quotes, escapes, braces in values), fence lengths, every syntax error.
- **Block writer.** Round trip of head line and `json` fence. After a configuration change only those two spans differ.
- **Content parser.** Each slot rule, every error in the table, reuse of an instance across a second parse, recursive reuse inside slots, the name table, page-scoped expressions and widget paths.
- **Decoration parser.** Regions for widget blocks, concrete slot blocks and expressions.
- **E2E.** A demo page with `Card`, `FlowLayout` and `TabLayout` renders and updates live. Editing a block in the editor changes the head line and nothing else.

## Stages

One branch and one pull request. Each stage leaves the application working.

1. `MarkdownPageParser` and `MarkdownBlockWriter`. Widget blocks replace the fence in the content parser, the write-back and the editor. Pages migrated. No slots.
2. `[Slot]`, `MarkdownFragment`, `MarkdownContentView`, default and named single slots, page scope, `Card`.
3. List slots, concrete slot types, `FlowLayout`, `TabLayout`.
4. Documentation: `pages.md`, `paths.md`, `building-subjects.md`, `storage.md`, the glossary and a demo page for the new widgets.

Before the pull request is opened, the specification and plan under `docs/superpowers` are removed. The user documentation from stage 4 is what remains.

## Risks

- **Scoped resolution.** `SubjectPathResolver` is used everywhere. The scoped lookup is a new entry point and the existing methods stay as they are. Whether a `[Derived]` path on a subject that is created before it is attached re-evaluates correctly needs a test early in stage 2.
- **Array slots.** Subject arrays as tracked properties need to behave like `List<GridCell>` does for `GridLayout.Cells`. If they do not, the slot property type becomes a list.
- **Fence lengths.** Miscounting is easy at three levels. The unnamed-block error is the safety net and its message has to point at the cause.
