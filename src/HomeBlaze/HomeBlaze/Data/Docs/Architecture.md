# HomeBlaze v2 Architecture

This document describes the modular architecture for HomeBlaze v2.

## Summary

HomeBlaze is a modular home automation platform built on **Namotion.Interceptor** for property interception and change tracking. The architecture separates concerns into abstraction layers, domain services, storage, and UI projects—allowing headless deployment, custom UI frameworks, or the full Blazor application.

**Key concepts:**
- **Subjects**: Intercepted objects with tracked properties (`[Configuration]`, `[State]`, `[Derived]`)
- **Operations**: Executable methods exposed via `[Operation]` attribute
- **Components**: Blazor UI (Widget, Edit, Page) auto-discovered via `[SubjectComponent]`
- **Context**: `IInterceptorSubjectContext` wires up all interceptors via `SubjectContextFactory`

**Quick reference:**
| Layer | Projects | Use Case |
|-------|----------|----------|
| Abstractions | `*.Abstractions` | Interfaces, attributes, contracts |
| Services | `HomeBlaze.Services` | Headless apps, APIs |
| Storage | `HomeBlaze.Storage` | File-based persistence |
| UI | `HomeBlaze.Host` | Full Blazor application |

## Design Goals

1. **Modularity**: Each project can be used independently
2. **Pure Domain Layer**: Backend services have no UI dependencies
3. **Blazor Separation**: UI components isolated in dedicated projects
4. **Library Reuse**: Build custom apps using HomeBlaze as a library
5. **Clean Layering**: No upward dependencies, abstractions split by concern

## Dependency Graph

Arrows point from user to used (consumer -> dependency):

```
                          HomeBlaze
                              |
              +---------------+---------------+
              |                               |
              v                               v
        HomeBlaze.Host              HomeBlaze.Servers.OpcUa.Blazor
              |                               |
  +-----------+-----------+                   v
  |                       |           HomeBlaze.Servers.OpcUa
  v                       v                   |
HomeBlaze.          HomeBlaze.                |
Storage.Blazor      Host.Services             |
  |                       |                   |
  +-----------+-----------+-------------------+
              |
              v
      HomeBlaze.Storage
              |
      +-------+-------+
      |               |
      v               v
HomeBlaze.      HomeBlaze.Samples
Services              |
      |               |
      +-------+-------+
              |
+-------------+-------------+
|             |             |
v             v             v
HomeBlaze.  HomeBlaze.    HomeBlaze.
Storage.    Components.   Abstractions
Abs.        Abs.                |
|             |                 |
+-------------+-----------------+
              |
              v
     Namotion.Interceptor.*
```

## Project Overview

| Project | SDK | Purpose |
|---------|-----|---------|
| `HomeBlaze.Abstractions` | Microsoft.NET.Sdk | Core metadata interfaces (IIconProvider, ITitleProvider) |
| `HomeBlaze.Storage.Abstractions` | Microsoft.NET.Sdk | Storage contracts and configuration attributes |
| `HomeBlaze.Components.Abstractions` | Microsoft.NET.Sdk | UI component abstractions and attributes |
| `HomeBlaze.Services` | Microsoft.NET.Sdk | Backend domain services (no UI) |
| `HomeBlaze.Host.Services` | Microsoft.NET.Sdk | UI-agnostic services (no MudBlazor) |
| `HomeBlaze.Storage` | Microsoft.NET.Sdk | File storage domain logic (no UI) |
| `HomeBlaze.Components` | Microsoft.NET.Sdk.Razor | Shared UI components (MudBlazor) |
| `HomeBlaze.Storage.Blazor` | Microsoft.NET.Sdk.Razor | Storage UI (Monaco editor, icons) |
| `HomeBlaze.Host` | Microsoft.NET.Sdk.Razor | Blazor application host (MudBlazor) |
| `HomeBlaze.Samples` | Microsoft.NET.Sdk | Sample subjects (Motor, etc.) |
| `HomeBlaze.Servers.OpcUa` | Microsoft.NET.Sdk | OPC UA server integration |
| `HomeBlaze.Servers.OpcUa.Blazor` | Microsoft.NET.Sdk.Razor | OPC UA UI components |
| `HomeBlaze` | Microsoft.NET.Sdk.Web | Minimal host (Program.cs only) |

## Test Projects

| Project | Tests For |
|---------|-----------|
| `HomeBlaze.Services.Tests` | HomeBlaze.Services |
| `HomeBlaze.Host.Services.Tests` | HomeBlaze.Host.Services |
| `HomeBlaze.Storage.Tests` | HomeBlaze.Storage |
| `HomeBlaze.Storage.Blazor.Tests` | HomeBlaze.Storage.Blazor |
| `HomeBlaze.E2E.Tests` | End-to-end Playwright tests |

## Abstraction Projects

### HomeBlaze.Abstractions

**Purpose**: Core metadata interfaces used across all layers.

**Contents**:
- `IIconProvider` - Subject icon metadata
- `ITitleProvider` - Subject title metadata
- `KnownAttributes` - Well-known attribute names (e.g., `IsEnabled`)
- `[Operation]` - Mark methods as executable operations
- `[Query]` - Mark methods as read-only queries

**Dependencies**: `Namotion.Interceptor`

**Use when**: Any subject needing display metadata.

### HomeBlaze.Storage.Abstractions

**Purpose**: Storage system contracts and configuration attributes.

**Contents**:
- `IStorageContainer` - Blob storage abstraction
- `IStorageFile` - File subject interface
- `IConfigurationWriter` - Configuration persistence
- `IConfigurableSubject` - Subjects with persistent config
- `StorageStatus` - Storage connection status
- `[Configuration]` - Property persistence marker
- `[State]` - UI display property marker
- `[FileExtension]` - File type mapping

**Dependencies**: `Namotion.Interceptor`

**Use when**: Building storage backends or configurable subjects.

### HomeBlaze.Components.Abstractions

**Purpose**: UI component system contracts.

**Contents**:
- `ISubjectComponent` - Base component interface
- `ISubjectEditComponent` - Editor component interface
- `IPage` - Page component interface
- `[SubjectComponent]` - Component registration attribute
- `SubjectComponentType` - Component type enum (Page, Edit, Widget)
- Navigation types (`NavigationLocation`, `AppBarAlignment`)

**Dependencies**: `Namotion.Interceptor`

**Use when**: Building UI components for subjects.

## Service Projects

### HomeBlaze.Services

**Purpose**: Core backend services including component discovery.

**Features**:
- **Root Management**: Load/save subject tree from `root.json`
- **JSON Serialization**: Polymorphic with `"type"` discriminator
- **Type Discovery**: Find types by name or file extension
- **Context Factory**: Create `IInterceptorSubjectContext`
- **Path Resolution**: Subject references to object graph paths
- **Component Registry**: Discover and resolve UI components
- **Property Utilities**: Filter by `[Configuration]`/`[State]`
- **Method Discovery**: Find `[Operation]` and `[Query]` methods on subjects
- **Method Invocation**: Execute operations with parameter conversion
- **Method Properties**: `MethodPropertyInitializer` creates virtual properties for methods, enabling `[PropertyAttribute]` on operations (e.g., `IsEnabled`)

**Dependencies**: `HomeBlaze.Abstractions`, `HomeBlaze.Storage.Abstractions`, `HomeBlaze.Components.Abstractions`

**Use when**: Building headless applications or APIs.

### Subject Context Factory

`SubjectContextFactory.Create()` configures the `IInterceptorSubjectContext` with all services required for HomeBlaze subjects:

| Method | Purpose                                                                       |
|--------|-------------------------------------------------------------------------------|
| `WithFullPropertyTracking()` | Equality checks, change detection, derived property updates                   |
| `WithReadPropertyRecorder()` | Tracks which properties are read (for derived property detection)             |
| `WithRegistry()` | Object graph navigation and querying                                          |
| `WithParents()` | Track parent-child relationships                                              |
| `WithLifecycle()` | Attach/detach callbacks with `IsFirstAttach`/`IsFinalDetach`                  |
| `WithDataAnnotationValidation()` | DataAnnotation validation on property changes                                 |
| `WithHostedServices()` | Auto-start/stop `BackgroundService` subjects                                  |
| `MethodPropertyInitializer` | Virtual properties for `[Operation]` and `[Query]` methods |

This ensures consistent behavior across all subjects. For details on each service, see the [Namotion.Interceptor documentation](../../../docs/).

### HomeBlaze.Host.Services

**Purpose**: UI rendering support without MudBlazor dependency.

**Features**:
- **Navigation Tree**: Build navigation from subject graph
- **Route Resolution**: Subjects to URL-friendly paths
- **Display Helpers**: Title, icon, formatted state values
- **Developer Mode**: Toggle developer features

**Dependencies**: `HomeBlaze.Services`

**Use when**: Building any UI framework integration.

## Storage Projects

### HomeBlaze.Storage

**Purpose**: File-based storage system without UI.

**Features**:
- **Blob Storage**: Local or cloud via FluentStorage
- **Virtual File System**: Folders and files as subjects
- **File Types**: Generic, JSON, Markdown with frontmatter
- **Change Detection**: FileSystemWatcher integration

**Dependencies**: `HomeBlaze.Services`, `FluentStorage`, `YamlDotNet`

**Use when**: Adding file storage to any application.

### HomeBlaze.Storage.Blazor

**Purpose**: UI components for file storage.

**Features**:
- **Monaco Editor**: Full code editor for files
- **File Icons**: MudBlazor icon mappings
- **File Editors**: Ready-to-use Razor components

**Dependencies**: `HomeBlaze.Storage`, `HomeBlaze.Services`, `MudBlazor`, `BlazorMonaco`

**Use when**: Building Blazor apps with file editing.

## UI Projects

### HomeBlaze.Components

**Purpose**: Shared UI components with MudBlazor.

**Dependencies**: `HomeBlaze.Services`, `HomeBlaze.Abstractions`, `HomeBlaze.Components.Abstractions`, `MudBlazor`

**Use when**: Building reusable Blazor UI components.

### HomeBlaze.Host

**Purpose**: Complete Blazor component library.

**Features**:
- **Subject Browser**: Tree view navigation
- **Property Panel**: Edit subject properties and execute operations
- **Operations UI**: Execute `[Operation]` methods with parameter dialogs
- **Navigation Menu**: Sidebar from subject tree
- **Layout**: Standard application layout

**Dependencies**: `HomeBlaze.Host.Services`, `HomeBlaze.Storage.Blazor`, `MudBlazor`

**Use when**: Building Blazor applications.

### HomeBlaze (Host)

**Purpose**: Minimal web host composing all modules.

**Features**:
- **DI Composition**: Register all services
- **Blazor Server**: Full application with SignalR
- **App Startup**: Root subject loading

**Dependencies**: `HomeBlaze.Host`, `HomeBlaze.Samples`

**Use when**: Running the full application.

### HomeBlaze.Samples

**Purpose**: Sample subject implementations for demonstration and testing.

**Contents**:
- `Motor` - Simulated motor with speed, temperature, status
- Sample subjects showing all HomeBlaze patterns

**Dependencies**: `HomeBlaze.Abstractions`, `HomeBlaze.Components.Abstractions`, `HomeBlaze.Storage.Abstractions`, `Namotion.Interceptor.*`

**Use when**: Learning HomeBlaze patterns or testing.

### HomeBlaze.Servers.OpcUa

**Purpose**: OPC UA server integration for industrial automation.

**Dependencies**: `HomeBlaze.Abstractions`, `HomeBlaze.Services`, `Namotion.Interceptor.OpcUa`

**Use when**: Exposing subjects via OPC UA protocol.

### HomeBlaze.Servers.OpcUa.Blazor

**Purpose**: UI components for OPC UA server configuration.

**Dependencies**: `HomeBlaze.Servers.OpcUa`, `HomeBlaze.Components`, `MudBlazor`

**Use when**: Adding OPC UA server UI to Blazor applications.

## Service Registration

```csharp
// HomeBlaze.Services - Core backend services
services.AddHomeBlazeServices();

// HomeBlaze.Host.Services - UI services (also calls AddHomeBlazeServices)
services.AddHomeBlazeHostServices();

// HomeBlaze.Storage.Blazor - Storage services
services.AddHomeBlazeStorage();

// HomeBlaze.Host - Full Blazor host (also calls AddHomeBlazeHostServices)
services.AddHomeBlazeHost();
```

| Method | Services |
|--------|----------|
| `AddHomeBlazeServices()` | `TypeProvider`, `SubjectTypeRegistry`, `IInterceptorSubjectContext`, `SubjectFactory`, `ConfigurableSubjectSerializer`, `RootManager`, `SubjectPathResolver`, `DeveloperModeService`, `ISubjectMethodInvoker` |
| `AddHomeBlazeHostServices()` | `SubjectComponentRegistry`, `NavigationItemResolver` |
| `AddHomeBlazeStorage()` | `MarkdownContentParser`, `ISubjectSetupService` |
| `AddHomeBlazeHost()` | MudBlazor services + all above |

## Usage Scenarios

### Headless/API Application
```
Reference: HomeBlaze.Services + HomeBlaze.Storage
```
- Full domain logic and file storage without UI

### Custom UI Framework (MAUI, WPF)
```
Reference: HomeBlaze.Host.Services + HomeBlaze.Storage
```
- Domain logic + display helpers, implement own UI

### Custom Blazor Application
```
Reference: HomeBlaze.Host
```
- Full Blazor components, extend as needed

### Full HomeBlaze Application
```
Reference: HomeBlaze (host project)
```
- Complete application with all features

## Extension Points

### Adding Custom Subjects
1. Create class with `[InterceptorSubject]`
2. Add `[Configuration]` for persistence
3. Add `[State]` for display
4. Add `[Operation]` for executable actions
5. Implement `ITitleProvider`/`IIconProvider`

### Adding Custom Components
1. Create Razor component implementing `ISubjectComponent`
2. Add `[SubjectComponent(SubjectComponentType.Page, typeof(MySubject))]`
3. Auto-discovered and used for that subject

### Adding Custom Modules
1. Reference appropriate layer
2. Add subjects and components
3. Reference from host project

## Subject Component System

Subjects can have associated UI components for different purposes. The system automatically discovers and renders the appropriate component based on subject type.

### Component Types

| Type     | Purpose                                  | Interface |
|----------|------------------------------------------|-----------|
| `Widget` | Inline visualization (e.g., in markdown) | `ISubjectComponent` |
| `Edit`   | Configuration editor dialog              | `ISubjectEditComponent` |
| `Page`   | Full-page view                           | `ISubjectComponent` |

**Note:** For creation wizards, the framework uses `Edit` components with `IsCreating=true`. Use `ISubjectEditComponent.IsCreating` to differentiate creation vs editing behavior.

### Registering Components

Use `[SubjectComponent]` attribute on Razor components:

```razor
@attribute [SubjectComponent(SubjectComponentType.Widget, typeof(Motor))]
@implements ISubjectComponent

<MudPaper>@MotorSubject?.CurrentSpeed RPM</MudPaper>

@code {
    [Parameter] public IInterceptorSubject? Subject { get; set; }
    private Motor? MotorSubject => Subject as Motor;
}
```

### Rendering Components with `<SubjectComponent>`

Use the `<SubjectComponent>` component to dynamically render any subject's registered component:

```razor
@* Render a subject's widget *@
<SubjectComponent Subject="@motor" Type="SubjectComponentType.Widget" />

@* Render a subject's page component *@
<SubjectComponent Subject="@page" Type="SubjectComponentType.Page" />

@* With component instance binding (for edit dialogs needing validation access) *@
<SubjectComponent Subject="@subject" Type="SubjectComponentType.Edit"
    @bind-ComponentInstance="_editComponent" />
```

The component automatically:
- Looks up the registered component from `SubjectComponentRegistry`
- Renders via `DynamicComponent`
- Shows a warning alert if no component is registered

---

## Design Notes

### Why Namotion.Interceptor?

HomeBlaze uses **Namotion.Interceptor** for property interception instead of traditional `INotifyPropertyChanged` or proxy-based approaches:

| Approach | Pros | Cons |
|----------|------|------|
| Manual INPC | Full control | Boilerplate, error-prone |
| Fody/PostSharp | Zero boilerplate | Build-time weaving complexity |
| Castle DynamicProxy | Runtime flexibility | Reflection overhead, proxy types |
| **Source Generation** | Zero reflection, AOT-friendly | Requires C# 13 partial properties |

The source generator approach provides:
- **Compile-time safety**: Errors caught during build
- **Zero runtime reflection**: Optimal performance, AOT-compatible
- **Derived property tracking**: Automatic dependency detection via `WithReadPropertyRecorder()`
- **Extensible middleware**: Chain of `IReadInterceptor`/`IWriteInterceptor`

### Abstraction Layering

The three abstraction projects split concerns by domain:

```
HomeBlaze.Abstractions          → Display metadata (ITitleProvider, IIconProvider)
HomeBlaze.Storage.Abstractions  → Persistence ([Configuration], IConfigurableSubject)
HomeBlaze.Components.Abstractions → UI contracts ([SubjectComponent], ISubjectEditComponent)
```

This allows:
- Storage backends without UI dependencies
- Custom UI frameworks without storage coupling
- Headless services with only core abstractions

### Service Registration Cascade

The `AddHomeBlaze*` methods cascade to avoid duplicate registrations:

```
AddHomeBlazeHost()
    └─→ AddHomeBlazeHostServices()
            └─→ AddHomeBlazeServices()
```

Call only the highest-level method needed for your scenario.

### Component Discovery

UI components use assembly scanning with `[SubjectComponent]` attributes rather than manual registration:

- **Pros**: Zero configuration, automatic discovery, type-safe
- **Cons**: Slightly slower startup (mitigated by caching in `SubjectComponentRegistry`)

The registry caches component lookups by subject type and component type, so runtime resolution is O(1) after initial scan.

### BackgroundService Integration

Subjects extending `BackgroundService` are automatically started/stopped via `WithHostedServices()`. This integrates with .NET's hosting model:

- Services start when attached to the context
- Services stop gracefully via `CancellationToken`
- Lifecycle callbacks (`IsFirstAttach`/`IsFinalDetach`) handle setup/teardown
