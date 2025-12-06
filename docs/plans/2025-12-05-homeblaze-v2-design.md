# HomeBlaze v2 Design

A complete rewrite of HomeBlaze built on Namotion.Interceptor, providing a file-system-backed state model with live tracking, automatic persistence, and protocol exposure.

## Overview

HomeBlaze v2 uses the file system as the source of truth. A root configuration file (`root.json`) bootstraps a storage container that materializes files and folders into a tracked object graph (digital twin). Changes to `[Configuration]` properties auto-persist back to JSON files.

## Core Architecture

### Projects (Minimal for v1)

- `HomeBlaze` - Blazor Server host (server-side only, no WASM), wires everything together
- `HomeBlaze.Core` - Shared models, subjects, services, and attributes

### Key Services

| Service | Responsibility |
|---------|----------------|
| `SubjectContextFactory` | Creates InterceptorSubjectContext with all interceptors |
| `RootManager` | Loads `root.json`, deserializes root subject |
| `SubjectTypeRegistry` | Maps `"Type"` strings to .NET types, maps file extensions to subject types |
| `SubjectSerializer` | JSON serialization with `"Type"` discriminator, `[Configuration]` filtering |
| `ConfigurationPersistenceHandler` | Auto-saves `[Configuration]` property changes (debounced) |

### SubjectContextFactory

```csharp
public static class SubjectContextFactory
{
    public static IInterceptorSubjectContext Create(IServiceCollection services)
    {
        return InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithParents()
            .WithLifecycle()
            .WithDataAnnotationValidation()
            .WithHostedServices(services);
    }
}
```

### Startup Flow

```
Startup
   │
   ▼
RootManager.LoadAsync("root.json")
   │
   ▼
Deserialize → FileSystemStorage (BackgroundService)
   │
   ▼
WithHostedServices detects IHostedService, calls StartAsync
   │
   ▼
FileSystemStorage.ExecuteAsync() runs:
  1. ScanAsync() - discover files/folders
  2. WatchAsync() - FileSystemWatcher loop
   │
   ▼
motor.json deserializes → Motor (also BackgroundService)
   │
   ▼
Motor auto-starts, simulates sensor values
```

## Storage System

### Class Hierarchy

```
StorageContainer (abstract, [InterceptorSubject], BackgroundService)
├── Children: Dictionary<string, IInterceptorSubject>
├── ExecuteAsync() → ScanAsync() + WatchAsync()
├── abstract Task ScanAsync(CancellationToken ct)
├── virtual Task WatchAsync(CancellationToken ct)
│
├── FileSystemStorage : StorageContainer
│   └── [Configuration] Path: string
│   └── Uses FileSystemWatcher for real-time updates
│
├── Folder : StorageContainer
│   └── Represents a directory within a storage
│
├── FtpStorage : StorageContainer
│   └── [Configuration] Host, User, Password, Path
│   └── Polling loop for change detection
│
└── AzureBlobStorage : StorageContainer
    └── [Configuration] ConnectionString, Container
    └── Polling (or Event Grid in future)
```

### Storage Provider Comparison

| Storage Type | Library | Change Detection |
|--------------|---------|------------------|
| FileSystem | System.IO | FileSystemWatcher (real-time) |
| FTP/SFTP | FluentStorage | Polling interval |
| Azure Blob | FluentStorage | Polling (Event Grid future) |
| SMB (mounted) | System.IO | FileSystemWatcher |

### Nested Storage Support

Storage containers can be nested. A JSON file inside FileSystemStorage can define an FTP or Azure storage:

```
Data/
├── motor.json         → Motor
├── backup.json        → FtpStorage { Host, User, Path }
│   └── (scans FTP, creates children)
└── cloud.json         → AzureBlobStorage { ConnectionString, Container }
    └── (scans blob container, creates children)
```

### Resilience

Background services implement:
- Retry loop with exponential backoff for scan failures
- Auto-reconnect watchers on disconnect/error
- Health status property (`Connected`, `Error`, `Scanning`)
- Log errors but keep running

## Type Registry & File Mapping

### SubjectTypeRegistry

Two responsibilities:

#### 1. JSON Type Resolution

```csharp
// Resolves "HomeBlaze.Storage.FileSystemStorage" → typeof(FileSystemStorage)
Type? ResolveType(string typeName);

// Registration
void Register<T>() where T : IInterceptorSubject;
void Register(string alias, Type type);

// Auto-discovery
void ScanAssemblies(params Assembly[] assemblies);
// Finds all [InterceptorSubject] classes
```

#### 2. File Extension Mapping

```csharp
[AttributeUsage(AttributeTargets.Class)]
public class FileExtensionAttribute : Attribute
{
    public string Extension { get; }  // e.g. ".md"
}

// Registry methods
Type? ResolveTypeForExtension(string extension);
void RegisterExtension(string extension, Type type);
```

### Built-in Mappings

| Pattern | Subject Type | Notes |
|---------|--------------|-------|
| `*.json` | Deserialize via `"Type"` | Polymorphic subjects |
| `*.md` | `MarkdownFile` | Content via method |
| Folders | `Folder : StorageContainer` | Recursive container |
| `*.*` (unknown) | `GenericFile` | Metadata only |

### Discovery at Startup

```
ScanAssemblies(typeof(Program).Assembly, ...)
   │
   ▼
Find all [InterceptorSubject] types → register for JSON "Type"
   │
   ▼
Find all [FileExtension] attributes → register extension mappings
   │
   ▼
Ready to deserialize root.json and map files
```

## File Subjects

Properties are tracked and may be persisted/broadcast. Binary content uses methods to avoid tracking overhead.

### MarkdownFile

```csharp
[InterceptorSubject]
[FileExtension(".md")]
public partial class MarkdownFile
{
    // Tracked metadata
    public partial string FileName { get; set; }
    public partial long FileSize { get; set; }
    public partial DateTime LastModified { get; set; }

    // Content via methods (not tracked)
    public Task<string> GetContentAsync();
    public Task SetContentAsync(string content);
}
```

### GenericFile

```csharp
[InterceptorSubject]
public partial class GenericFile
{
    // Tracked metadata only
    public partial string FileName { get; set; }
    public partial string Extension { get; set; }
    public partial long FileSize { get; set; }
    public partial DateTime LastModified { get; set; }

    // Binary content via methods (not tracked)
    public Task<byte[]> GetBytesAsync();
    public Task<Stream> OpenReadAsync();
    public Task WriteAsync(byte[] data);
}
```

## Persistence

### [Configuration] Attribute

```csharp
[AttributeUsage(AttributeTargets.Property)]
public class ConfigurationAttribute : Attribute { }
```

Properties marked with `[Configuration]` are:
- Serialized to JSON
- Auto-persisted on change (debounced ~500ms)
- The "recipe" to recreate the subject

Other properties are runtime-only or synced from external sources.

### JSON Format

```json
{
    "Type": "HomeBlaze.Storage.FileSystemStorage",
    "Path": "./Data"
}
```

### ConfigurationPersistenceHandler

Listens for property changes, filters `[Configuration]` properties, debounces, and writes back to the source JSON file.

## Blazor UI

### Libraries

- **MudBlazor** (latest) - UI component library
- **Markdig** - Markdown to HTML rendering

### Navigation Structure

```
┌─────────────────────────────┐
│ 📁 Docs                     │  ← Folder with .md files
│    📄 Getting Started       │
│    📄 Configuration         │
│ 📁 Guides                   │
│    📄 Motor Setup           │
│─────────────────────────────│
│ ⚙️ Browser                  │  ← Always at bottom
└─────────────────────────────┘
```

Navigation is auto-generated from the object graph:

```csharp
void BuildNavigation(StorageContainer container, string basePath = "")
{
    foreach (var md in container.Children.Values.OfType<MarkdownFile>())
    {
        // Add MudNavLink → /docs/{basePath}/{md.FileName}
    }

    foreach (var folder in container.Children.Values.OfType<Folder>())
    {
        if (folder.Children.Values.OfType<MarkdownFile>().Any())
        {
            // Add MudNavGroup with folder name
            BuildNavigation(folder, $"{basePath}/{folder.Name}");
        }
    }
}
```

### Routes

| Path | Page | Description |
|------|------|-------------|
| `/docs/{**path}` | `MarkdownPage` | View rendered markdown |
| `/docs/{**path}/edit` | `MarkdownEditorPage` | Edit with live preview |
| `/browser` | `BrowserPage` | Object graph browser |

### Object Graph Browser

Two modes (toggle):

#### Miller Columns (macOS Finder style)

```
┌─────────────────┬─────────────────┬─────────────────┐
│ Root            │ Motor           │                 │
│ (StorageContainer)│ (Motor)       │                 │
│─────────────────│─────────────────│                 │
│ Path: ./Data    │ Name: "Fan"     │                 │
│                 │ Speed: 1200     │                 │
│ Children:       │ Temp: 45.2°C    │                 │
│ > Motor         │ Status: Running │                 │
│ > notes.md      │                 │                 │
└─────────────────┴─────────────────┴─────────────────┘
```

#### Tree View

```
┌──────────────────┬──────────────────────┐
│ 📁 Root          │ Motor                │
│  ├─ 📄 motor     │──────────────────────│
│  ├─ 📄 notes.md  │ Name: Fan Motor      │
│  └─ 📁 sensors/  │ Speed: 1200 RPM      │
│      └─ temp     │ Temperature: 45°C    │
└──────────────────┴──────────────────────┘
```

### Property Display Order

1. **Primitives** (`string`, `int`, `bool`, `DateTime`, etc.) - inline value, editable if `[Configuration]`
2. **Single subject references** - clickable → opens new pane
3. **Subject collections** (`IEnumerable<IInterceptorSubject>`) - list all, each clickable
4. **Subject dictionaries** (`Dictionary<string, IInterceptorSubject>`) - list with keys, each clickable

`[Derived]` properties shown as read-only computed values.

### Component Architecture

```
SubjectBrowser : TrackingComponentBase<IInterceptorSubject>
├── SubjectPane       - Shows one subject's properties
├── SubjectTreeView   - Full tree for quick navigation
└── PropertyRenderer  - Renders based on property type
```

Uses `TrackingComponentBase<T>` from `Namotion.Interceptor.Blazor` for automatic UI refresh on property changes.

## Protocol Exposure (Optional)

The entire object graph can be exposed via industrial protocols:

```csharp
// OPC UA Server
services.AddOpcUaSubjectServer<StorageContainer>("opc", rootName: "HomeBlaze");

// MQTT Server
services.AddMqttSubjectServer(
    _ => root,
    _ => new MqttServerConfiguration
    {
        BrokerPort = 1883,
        PathProvider = new AttributeBasedSourcePathProvider("mqtt", "/")
    });
```

## Sample: Motor Subject

```csharp
[InterceptorSubject]
public partial class Motor : BackgroundService
{
    // Configuration (persisted to JSON)
    [Configuration]
    public partial string Name { get; set; }

    [Configuration]
    public partial int TargetSpeed { get; set; }

    [Configuration]
    public partial TimeSpan SimulationInterval { get; set; }

    // Live state (simulated, not persisted)
    public partial int CurrentSpeed { get; set; }
    public partial double Temperature { get; set; }
    public partial MotorStatus Status { get; set; }

    // Derived
    [Derived]
    public double SpeedDelta => TargetSpeed - CurrentSpeed;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        Status = MotorStatus.Running;
        while (!ct.IsCancellationRequested)
        {
            CurrentSpeed += Math.Sign(TargetSpeed - CurrentSpeed) * 10;
            Temperature = 25 + (CurrentSpeed / 100.0) + Random.Shared.NextDouble() * 2;
            await Task.Delay(SimulationInterval, ct);
        }
        Status = MotorStatus.Stopped;
    }
}
```

**Data/motor.json:**
```json
{
    "Type": "HomeBlaze.Subjects.Motor",
    "Name": "Cooling Fan",
    "TargetSpeed": 1500,
    "SimulationInterval": "00:00:01"
}
```

## Sample Data Structure

**root.json:**
```json
{
    "Type": "HomeBlaze.Storage.FileSystemStorage",
    "Path": "./Data"
}
```

**Data/ folder:**
```
Data/
├── motor.json        → Motor (simulated sensors)
├── notes/
│   ├── readme.md     → MarkdownFile
│   └── setup.md      → MarkdownFile
└── backup.json       → FtpStorage (nested storage)
```

## Dependencies

| Package | Purpose |
|---------|---------|
| MudBlazor | UI components |
| Markdig | Markdown to HTML |
| FluentStorage | Azure, FTP, SFTP storage abstraction |
| Namotion.Interceptor.* | Core interceptor libraries |

## Future Considerations

- Dynamic NuGet package loading for custom subject types
- Azure Event Grid for real-time blob change notifications
- SMB storage via EzSmb/SMBLibrary
- Historical state storage (time series database)
- Dashboard/widget system
