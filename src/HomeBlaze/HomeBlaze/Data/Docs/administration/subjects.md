---
title: Subjects, Storage & Files
navTitle: Subjects
position: 3
---

# Subjects, Storage & Files

This guide covers managing subjects — the objects in the HomeBlaze graph — and how they map to files on disk. For the conceptual overview, see [Concepts](../concepts.md). For app-level settings like logging and the MCP server, see [Configuration](configuration.md).

---

## Managing Subjects

Subjects can be managed in two ways — both produce the same result:

**Via the Blazor UI:**
- **Create subjects** — The subject browser lets you create new subjects by selecting a type from the registry and filling in configuration properties via an auto-generated editor
- **Edit subjects** — Select any subject to view and edit its `[Configuration]` properties in the property panel. Changes are saved back to the JSON file automatically
- **Create and edit files** — New JSON and Markdown files can be created from the UI. Markdown files open in an integrated Monaco editor with live preview
- **Manage folders** — Create, rename, and delete folders in the storage tree

**Via files directly:**
- Edit JSON and Markdown files in the `Data/` folder with any editor
- Changes are picked up automatically via file system watching
- This is useful for bulk setup, version control, or scripting

Both approaches work on the same underlying files — the UI is a management layer on top of the file-based storage, not a separate system.

---

## Storage & `root.json` {#rootjson}

The `root.json` file in your application directory defines the storage location:

```json
{
    "$type": "HomeBlaze.Storage.FluentStorageContainer",
    "storageType": "disk",
    "connectionString": "./Data"
}
```

| Property | Description |
|----------|-------------|
| `storageType` | `disk` for local files, `inmemory` for testing |
| `connectionString` | Path to your data folder |

For the design rationale (pluggable backends, recovery behavior), see [Storage Design](../architecture/design/storage.md).

---

## File Types {#file-types}

| Extension | Subject Type | Description |
|-----------|--------------|-------------|
| `.json` | Configured type or `JsonFile` | Subject defined by `$type` property |
| `.md` | `MarkdownFile` | Interactive page with expressions |
| Other | `GenericFile` | Basic file representation |

---

## Folder Structure

Folders in your data directory become the object hierarchy:

```
Data/
├── demo/
│   ├── motor1.json      → /demo/motor1
│   └── motor2.json      → /demo/motor2
└── docs/
    └── guide.md         → /docs/guide.md
```

Folders use `[InlinePaths]`, so children are path segments directly (no `Children[...]` brackets). See [Paths](paths.md) for full syntax.

---

## Creating Subjects

### JSON Format

Create subjects by adding JSON files with a `$type` discriminator:

```json
{
    "$type": "HomeBlaze.Samples.Motor",
    "name": "Conveyor Belt",
    "targetSpeed": 600,
    "simulationInterval": "00:00:02"
}
```

The `$type` property specifies the .NET class to instantiate. Properties marked with `[Configuration]` in the class are automatically persisted. See [Building Subjects](../development/building-subjects.md) for authoring the C# side.

### Background Services

Subjects that inherit from `BackgroundService` start automatically when loaded. They stop when removed from the graph.

### Example: Adding a Motor

1. Create `Data/demo/my-motor.json`:

```json
{
    "$type": "HomeBlaze.Samples.Motor",
    "name": "My Custom Motor",
    "targetSpeed": 2000,
    "simulationInterval": "00:00:01"
}
```

2. The motor appears in the browser and starts running immediately.

---

## Widgets

Widgets render subjects inline in markdown pages or reference subjects by path.

### Widget Subject

Use the `Widget` subject to embed another subject's widget by path:

```json
{
    "$type": "HomeBlaze.Components.Widget",
    "path": "/Demo/Conveyor"
}
```

The widget looks up the subject at the specified path and renders its registered component.

For embedding widgets inline within markdown pages, see [Markdown Pages — Embedded Subjects](pages.md#embedded-subjects).

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

---

## Tips & Best Practices

1. **Organize by function** — Group related subjects in folders
2. **Use descriptive names** — `conveyor-motor.json` is clearer than `m1.json`
3. **Keep paths short** — Deep nesting makes paths harder to maintain
4. **Use inline subjects for page-specific data** — Don't pollute the global graph
5. **Leverage live updates** — Properties update automatically, no refresh needed

---

## Related

- [Paths](paths.md) — referencing subjects and properties
- [Markdown Pages](pages.md) — building interactive pages
- [Building Subjects](../development/building-subjects.md) — authoring subject types in C#
- [Storage Design](../architecture/design/storage.md) — storage architecture
