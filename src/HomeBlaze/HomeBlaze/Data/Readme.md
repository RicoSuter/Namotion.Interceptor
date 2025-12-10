---
title: Welcome to HomeBlaze
navTitle: Home
order: 0
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
| **Protocol Exposure** | Access via OPC UA and MQTT |
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

> **Try the Demo!** The `demo/` folder includes 5 pre-configured motors. Navigate to the **Browser** to see them live, or read the [Demo Setup Guide](demo/setup.md).

### Step 1: Configure Storage

Edit `root.json` to point to your data folder:

```json
{
    "Type": "HomeBlaze.Storage.FluentStorageContainer",
    "StorageType": "disk",
    "ConnectionString": "./Data"
}
```

### Step 2: Add Subjects

Create JSON files with a `Type` discriminator:

```json
{
    "Type": "HomeBlaze.Subjects.Motor",
    "Name": "Cooling Fan",
    "TargetSpeed": 1500
}
```

### Step 3: Add Documentation

Create markdown files anywhere in the data folder. They'll appear in the navigation automatically!

---

## Supported File Types

| Extension | Subject Type | Description |
|-----------|--------------|-------------|
| `.json` | *Polymorphic* | Deserialized via `Type` property |
| `.md` | `MarkdownFile` | Rendered markdown with frontmatter |
| `.markdown` | `MarkdownFile` | Alternative extension |
| *folder* | `Folder` | Container for nested subjects |

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
| `Root.Children[Motor]` | A subject named "Motor" |
| `Root.Children[docs].Children[0]` | First child of docs folder |

---

## Blockquote Examples

> **Note:** Files are watched in real-time. Edit a file externally and see changes instantly!

> **Tip:** Use frontmatter in markdown files to control navigation order:
> ```yaml
> ---
> title: My Page
> navTitle: Short Name
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

| Equipment | Speed | Purpose |
|-----------|-------|---------|
| Conveyor Belt | 600 RPM | Material handling |
| Exhaust Fan | 1,500 RPM | Ventilation |
| Cooling Fan | 1,800 RPM | Temperature control |
| Water Pump | 2,400 RPM | Water circulation |
| Compressor | 3,000 RPM | Compressed air |

👉 **[View Demo Setup Guide](demo/setup.md)** for detailed exploration steps.

## What's Next?

1. 📂 **Explore the Demo** - Check out the [demo folder](demo/) with live motor simulations
2. 📖 **Read Documentation** - Browse the [docs folder](docs/) for in-depth guides
3. 🔧 **Edit a Motor** - Click any motor in the Browser and change its target speed
4. 📝 **External Edits** - Try editing `demo/cooling-fan.json` externally and watch it update!
5. 🌐 **Protocol Access** - Connect via OPC UA (`opc.tcp://localhost:4840`) or MQTT

---

*Built with Namotion.Interceptor, MudBlazor, and Markdig*
