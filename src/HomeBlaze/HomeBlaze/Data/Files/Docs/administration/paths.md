---
title: Paths
navTitle: Paths
position: 7
---

# Paths

Paths reference subjects and properties anywhere in the object graph.

---

## Path Prefixes

| Prefix | Description | Example |
|--------|-------------|---------|
| `/` | Absolute path from root | `/Demo/Conveyor` |
| `./` | Relative to current subject | `./Child/Name` |
| `../` | Navigate up to parent | `../Sibling/Temperature` |
| *(none)* | Relative to current context | `Demo/Conveyor/Speed` |

On a markdown page the current subject is the page, and a path without a prefix can start with the name of a block. See "Paths on Markdown Pages" below.

---

## Canonical Syntax and `[InlinePaths]`

For `[InlinePaths]` dictionaries (like the `Children` dictionary on folders), child keys are inlined as path segments:

```
/Demo/Conveyor/PropertyName
```

This is the canonical (route) form of `/Children[Demo]/Children[Conveyor]/PropertyName`.

---

## Brackets for Collection Indices

Use brackets when accessing non-inlined collection entries explicitly:

```
/Devices[0]/Temperature
```

With `[InlinePaths]` dictionaries (like folder children), keys become direct segments, so no brackets are needed:

```
/Demo/Setup.md
/Docs/Welcome.md/Title
```

---

## Examples

| Path | Description |
|------|-------------|
| `/Demo/Conveyor` | Absolute path to a motor |
| `/Demo/Conveyor/CurrentSpeed` | Property on that motor |
| `/Demo/Setup.md` | File in an `[InlinePaths]` folder |
| `./Child/Name` | Property on current subject's child |
| `../Temperature` | Go up one level, access Temperature |
| `motor/CurrentSpeed` | Property of the block named `motor` on a markdown page |

---

## Paths on Markdown Pages

Every relative path on a markdown page starts at the page. This holds for the path of an expression and for the `Path` of a `Widget` block, however deeply the block is nested. Moving a block into a card or into a tab does not change where its paths lead.

A block gets a name with `#name` on its head line. The name is known anywhere on the page, also inside other blocks and above the block that declares it. See [Markdown Pages](pages.md) for the block syntax.

~~~~markdown
:: HomeBlaze.Components.Card Title="Motor Card" Icon=Settings
::: HomeBlaze.Samples.Motor#cardmotor Name=CardMotor TargetSpeed=1200 SimulationInterval=00:00:01
:::
::

:: HomeBlaze.Components.Card Title="Reference Card" Icon=Link
::: HomeBlaze.Components.Widget Path=cardmotor
:::

::: HomeBlaze.Components.Widget Path=../Conveyor
:::

The named motor has a temperature of {{ cardmotor/Temperature }} °C.
::
~~~~

### Resolution Order

1. **Absolute path**: A path that starts with `/` starts at the root.
2. **Explicit relative path**: A path that starts with `./` or `../` starts at the page. `./` is the page itself and `../` is the folder that contains the page. Such a path never uses names.
3. **Name first**: Any other path is split at its first `/`. When the first segment is the name of a block on the page, the path starts at the subject of that block. Otherwise it starts at the page, like a path with `./`.

For the page `/Demo/Blocks.md` this gives:

| Path | Resolves to |
|------|-------------|
| `cardmotor/Temperature` | `Temperature` of the block named `cardmotor` on the page |
| `../Conveyor/CurrentSpeed` | `/Demo/Conveyor/CurrentSpeed` |
| `/Demo/Conveyor/CurrentSpeed` | `/Demo/Conveyor/CurrentSpeed` |
| `Title` | The title of the page |

A `Widget` that is not on a page, for example one in a JSON file of its own, resolves a relative path from itself.

---

## Limitations

- **Parent navigation with multiple parents**: If a subject has multiple parents (rare), `../` returns null (ambiguous path).
- **Detached subjects**: Subjects not attached to the graph have no path.
- **A property of the page**: On a markdown page, a property of the page itself is written without a prefix, like `Title`. With `./` before it, as in `./Title`, an expression shows nothing.
- **Names are local to their page**: A name declared with `#name` is known on its own page only. Another page reaches a block that is directly on a page through that page, for example `/Demo/Inline.md/mymotor`.
- **Braces and `<` in an expression**: The path of an expression on a markdown page cannot contain `{`, `}` or `<` literally. A file name or a key that contains one of them is written percent-encoded, as `%7B`, `%7D` or `%3C`, because every segment of a path is decoded before it is looked up.

---

## Related

- [Subjects, Storage & Files](subjects.md): how folders and files become paths
- [Markdown Pages](pages.md): using paths in live expressions and widgets
