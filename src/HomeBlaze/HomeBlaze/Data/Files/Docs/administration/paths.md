---
title: Paths
navTitle: Paths
position: 5
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

---

## Canonical Syntax and `[InlinePaths]` {#canonical}

For `[InlinePaths]` dictionaries (like the `Children` dictionary on folders), child keys are inlined as path segments:

```
/Demo/Conveyor/PropertyName
```

This is the canonical (route) form of `/Children[Demo]/Children[Conveyor]/PropertyName`.

---

## Brackets for Collection Indices {#brackets}

Use brackets when accessing non-inlined collection entries explicitly:

```
/Devices[0]/Temperature
```

With `[InlinePaths]` dictionaries (like folder children), keys become direct segments — no brackets needed:

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
| `motor/Speed` | Inline subject named "motor" (in markdown) |

---

## Resolution Order

When resolving paths in markdown pages:

1. **Inline subjects first** — Subjects defined in the same page with `` ```subject(name) ``
2. **Relative path** — From current subject context
3. **Global path** — Using `/` prefix

---

## Limitations

- **Parent navigation with multiple parents**: If a subject has multiple parents (rare), `../` returns null (ambiguous path).
- **Detached subjects**: Subjects not attached to the graph have no path.

---

## Related

- [Subjects, Storage & Files](subjects.md) — how folders and files become paths
- [Markdown Pages](pages.md) — using paths in live expressions and widgets
