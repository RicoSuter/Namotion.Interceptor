---
title: Welcome to HomeBlaze
navTitle: Home
position: 0
---

# HomeBlaze v2

> A modern home automation platform built on **Namotion.Interceptor** for real-time object tracking and automatic persistence.

HomeBlaze v2 is a complete rewrite that treats your file system as the source of truth. JSON files become live objects, markdown files become documentation, and everything is browsable in a modern UI.

---

## Key Features

| Feature | Description |
|---------|-------------|
| **Live Tracking** | Property changes propagate instantly to the UI |
| **Auto-Persistence** | `[Configuration]` properties save automatically |
| **File Watching** | External file edits sync in real-time |
| **Protocol Exposure** | *Planned:* OPC UA and MQTT integration |
| **Markdown Docs** | Write docs in markdown, view them beautifully |

---

## Architecture Overview

HomeBlaze uses a **digital twin** approach where files become tracked objects:

```
File System                    Object Graph
------------                   ------------
root.json          -->         FluentStorageContainer
  Data/
    motor.json     -->           Motor (BackgroundService)
    Readme.md      -->           MarkdownFile
    docs/          -->           Folder
      setup.md     -->             MarkdownFile
```

### Core Concepts

1. **Subjects** - Tracked objects with intercepted properties
2. **Storage Containers** - Mount points that materialize files into subjects
3. **Configuration** - Properties marked `[Configuration]` persist to JSON
4. **State** - Runtime properties that don't persist

---

## Quick Start

> **Try the Demo!** The `demo/` folder includes 5 pre-configured motors. Navigate to the **Browser** to see them live, or read the [Demo Setup Guide](demo/Setup.md).

### Step 1: Configure Storage

Edit `root.json` to point to your data folder:

```json
{
    "type": "HomeBlaze.Storage.FluentStorageContainer",
    "storageType": "disk",
    "connectionString": "./Data"
}
```

### Step 2: Add Subjects

Create JSON files with a `type` discriminator:

```json
{
    "type": "HomeBlaze.Samples.Motor",
    "name": "Cooling Fan",
    "targetSpeed": 1500
}
```

### Step 3: Add Documentation

Create markdown files anywhere in the data folder. They'll appear in the navigation automatically!

---

## Supported File Types

| Extension | Subject Type | Description |
|-----------|--------------|-------------|
| `.json` | *Polymorphic* | Deserialized via `type` property |
| `.md` | `MarkdownFile` | Rendered markdown with frontmatter |
| `.markdown` | `MarkdownFile` | Alternative extension |
| *folder* | `VirtualFolder` | Container for nested subjects |

---

## Property Attributes

Use these attributes to control property behavior:

- **`[Configuration]`** - Persisted to JSON, editable in UI
- **`[State]`** - Displayed in browser, runtime-only
- **`[Derived]`** - Computed property, auto-updates on dependencies

### Example Subject

```csharp
[InterceptorSubject]
public partial class Motor : BackgroundService
{
    [Configuration]
    public partial string Name { get; set; }

    [Configuration]
    public partial int TargetSpeed { get; set; }

    [State]
    public partial int CurrentSpeed { get; set; }

    [Derived]
    public int SpeedDelta => TargetSpeed - CurrentSpeed;
}
```

---

## Browser Navigation

The **Object Browser** shows your entire object graph:

- **Miller Columns** - Click to drill down, like macOS Finder
- **Live Updates** - Properties update in real-time
- **Edit Mode** - Click the edit button to modify configuration

### Object Path Notation

Paths use familiar C# syntax:

| Path | Description |
|------|-------------|
| `Root` | The root storage container |
| `Root.Children[motor.json]` | A subject named "motor.json" |
| `Root.Children[demo].Children[conveyor.json]` | Conveyor motor in demo folder |

---

## Blockquote Examples

> **Note:** Files are watched in real-time. Edit a file externally and see changes instantly!

> **Tip:** Use frontmatter in markdown files to control navigation order:
> ```yaml
> ---
> title: My Page
> navTitle: Short Name
> icon: Article
> order: 1
> ---
> ```

---

## Storage Backends

HomeBlaze supports multiple storage backends via FluentStorage:

| Backend | Status | Change Detection |
|---------|--------|------------------|
| Local Disk | Supported | FileSystemWatcher |
| Azure Blob | Planned | Polling |
| FTP/SFTP | Planned | Polling |
| SMB Share | Planned | FileSystemWatcher |

---

## Demo System

The `demo/` folder contains a working example with 5 motors simulating a small factory:

| Equipment | Current Speed | Target Speed | Temperature |
|-----------|---------------|--------------|-------------|
| Conveyor Belt | {{ Root.Children[demo].Children[conveyor.json].CurrentSpeed }} | {{ Root.Children[demo].Children[conveyor.json].TargetSpeed }} | {{ Root.Children[demo].Children[conveyor.json].Temperature }} |
| Exhaust Fan | {{ Root.Children[demo].Children[exhaust-fan.json].CurrentSpeed }} | {{ Root.Children[demo].Children[exhaust-fan.json].TargetSpeed }} | {{ Root.Children[demo].Children[exhaust-fan.json].Temperature }} |
| Cooling Fan | {{ Root.Children[demo].Children[cooling-fan.json].CurrentSpeed }} | {{ Root.Children[demo].Children[cooling-fan.json].TargetSpeed }} | {{ Root.Children[demo].Children[cooling-fan.json].Temperature }} |
| Water Pump | {{ Root.Children[demo].Children[water-pump.json].CurrentSpeed }} | {{ Root.Children[demo].Children[water-pump.json].TargetSpeed }} | {{ Root.Children[demo].Children[water-pump.json].Temperature }} |
| Compressor | {{ Root.Children[demo].Children[compressor.json].CurrentSpeed }} | {{ Root.Children[demo].Children[compressor.json].TargetSpeed }} | {{ Root.Children[demo].Children[compressor.json].Temperature }} |

👉 **[View Demo Setup Guide](demo/Setup.md)** for detailed exploration steps.

## What's Next?

1. 📂 **Explore the Demo** - Check out the [demo folder](demo/) with live motor simulations
2. 📖 **Read Documentation** - Browse the [docs folder](docs/) for in-depth guides
3. 🔧 **Edit a Motor** - Click any motor in the Browser and change its target speed
4. 📝 **External Edits** - Try editing `demo/cooling-fan.json` externally and watch it update!

---

*Built with Namotion.Interceptor, MudBlazor, and Markdig*
