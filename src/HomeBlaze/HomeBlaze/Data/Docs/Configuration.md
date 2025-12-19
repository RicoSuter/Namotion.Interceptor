---
title: Configuration Guide
navTitle: Configuration
position: 2
---

# Configuration Guide

This guide explains how to configure and operate HomeBlaze applications.

---

## Overview

HomeBlaze transforms files into a reactive object graph with automatic UI generation. Understanding how to configure subjects, reference them via paths, and organize your data folder is key to building effective dashboards.

---

## The Object Graph

HomeBlaze organizes everything as **subjects** in a tree structure:

```
Root (FluentStorageContainer)
├── Children
│   ├── demo/
│   │   ├── Conveyor.json (Motor)
│   │   └── dashboard.md (MarkdownFile)
│   └── docs/
│       └── Configuration.md (MarkdownFile)
```

### Key Concepts

| Concept | Description |
|---------|-------------|
| **Subject** | Any object in the graph (motors, files, folders, etc.) |
| **Property** | A value on a subject (e.g., `Speed`, `Temperature`) |
| **Root** | The top-level storage container |

---

## Storage & Files

### root.json

The `root.json` file in your application directory defines the storage location:

```json
{
    "type": "HomeBlaze.Storage.FluentStorageContainer",
    "storageType": "disk",
    "connectionString": "./Data"
}
```

| Property | Description |
|----------|-------------|
| `storageType` | `disk` for local files, `inmemory` for testing |
| `connectionString` | Path to your data folder |

### File Types

| Extension | Subject Type | Description |
|-----------|--------------|-------------|
| `.json` | Configured type or `JsonFile` | Subject defined by `type` property |
| `.md` | `MarkdownFile` | Interactive page with expressions |
| Other | `GenericFile` | Basic file representation |

### Folder Structure

Folders in your data directory become the object hierarchy:

```
Data/
├── demo/
│   ├── motor1.json      → Root.demo.motor1
│   └── motor2.json      → Root.demo.motor2
└── docs/
    └── guide.md         → Root.docs[guide.md]
```

Note: Keys with dots (like `guide.md`) use brackets to preserve the extension.

---

## Creating Subjects

### JSON Format

Create subjects by adding JSON files with a `type` discriminator:

```json
{
    "type": "HomeBlaze.Samples.Motor",
    "name": "Conveyor Belt",
    "targetSpeed": 600,
    "simulationInterval": "00:00:02"
}
```

The `type` property specifies the .NET class to instantiate. Properties marked with `[Configuration]` in the class are automatically persisted.

### Background Services

Subjects that inherit from `BackgroundService` start automatically when loaded. They stop when removed from the graph.

### Example: Adding a Motor

1. Create `Data/demo/my-motor.json`:

```json
{
    "type": "HomeBlaze.Samples.Motor",
    "name": "My Custom Motor",
    "targetSpeed": 2000,
    "simulationInterval": "00:00:01"
}
```

2. The motor appears in the browser and starts running immediately.

---

## Path Syntax

Paths let you reference subjects and their properties anywhere in the object graph.

### Path Prefixes

| Prefix | Description | Example |
|--------|-------------|---------|
| `Root.` | Absolute path from root | `Root.Demo.Conveyor` |
| `this.` | Relative to current subject | `this.Child.Name` |
| `../` | Navigate up to parent | `../Sibling.Temperature` |
| *(none)* | Relative to current context | `Demo.Conveyor.Speed` |

### Simplified Path Syntax

For `[InlinePaths]` dictionaries (like the Children dictionary), use simple dot notation:

```
Root.Demo.Conveyor.PropertyName
```

This is equivalent to the verbose form `Root.Children[Demo].Children[Conveyor].PropertyName`.

### Bracket Notation for Keys with Dots

When a key contains a dot (like file extensions), use brackets to preserve it:

```
Root.Demo[Setup.md]
Root.Docs[Pages.md].Title
```

Without brackets, `Root.Demo.Setup.md` would be interpreted as 4 segments instead of 3.

### Examples

| Path | Description |
|------|-------------|
| `Root.Demo.Conveyor` | Absolute path to a motor (simplified) |
| `Root.Demo.Conveyor.CurrentSpeed` | Property on that motor |
| `Root.Demo[Setup.md]` | File with extension (brackets preserve dot) |
| `this.Child.Name` | Property on current subject's child |
| `../Temperature` | Go up one level, access Temperature |
| `motor.Speed` | Inline subject named "motor" (in markdown) |

### Resolution Order

When resolving paths in markdown pages:

1. **Inline subjects first** - Subjects defined in the same page with `` ```subject(name) ``
2. **Relative path** - From current subject context
3. **Global path** - Using `Root.` prefix

### Limitations

- **Parent navigation with multiple parents**: If a subject has multiple parents (rare), `../` returns null (ambiguous path)
- **Detached subjects**: Subjects not attached to the graph have no path

---

## Widgets

Widgets render subjects inline in markdown or reference subjects by path.

### Widget Component

Use the `Widget` subject to embed another subject's widget by path:

```json
{
    "type": "HomeBlaze.Components.Widget",
    "path": "Root.Demo.Conveyor"
}
```

The widget looks up the subject at the specified path and renders its registered component.

### In Markdown

Embed a widget inline:

~~~markdown
```subject(mywidget)
{
    "type": "HomeBlaze.Components.Widget",
    "path": "Root.Demo.Conveyor"
}
```
~~~

---

## Demo Configuration

The demo includes pre-configured motors in the `demo/` folder:

| Motor | Target Speed | Interval |
|-------|--------------|----------|
| Conveyor Belt | 600 RPM | 2s |
| Exhaust Fan | 1,500 RPM | 1s |
| Cooling Fan | 1,800 RPM | 1s |
| Water Pump | 2,400 RPM | 1s |
| Compressor | 3,000 RPM | 1s |

See the [Demo Setup Guide](../Demo/Setup.md) for details.

---

## Tips & Best Practices

1. **Organize by function** - Group related subjects in folders
2. **Use descriptive names** - `conveyor-motor.json` is clearer than `m1.json`
3. **Keep paths short** - Deep nesting makes paths harder to maintain
4. **Use inline subjects for page-specific data** - Don't pollute the global graph
5. **Leverage live updates** - Properties update automatically, no refresh needed

---

## Related Documentation

- [Markdown Pages](Pages.md) - Creating interactive pages
- [Building Subjects](BuildingSubjects.md) - Creating custom subject types
- [Architecture](Architecture.md) - System design overview
