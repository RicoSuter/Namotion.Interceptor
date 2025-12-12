# HomeBlaze v2 Architecture

This document describes the target modular architecture for HomeBlaze v2.

## Design Goals

1. **Modularity**: Each project can be used independently
2. **Pure Domain Layer**: Backend services have no UI dependencies
3. **Blazor Separation**: UI components isolated in dedicated projects
4. **Library Reuse**: Build custom apps using HomeBlaze as a library

## Dependency Graph

Arrows point from implementation to what it depends on (impl -> base):

```
                         Namotion.Interceptor
                                 ^
                    HomeBlaze.Abstractions
                           ^    ^
          +----------------+    +----------------+
          |                                      |
   HomeBlaze.Services                    HomeBlaze.Storage
          ^                                      ^
          |                                      |
   HomeBlaze.Host.Services             HomeBlaze.Storage.Blazor
          ^                                      ^
          +----------+    +----------------------+
                     |    |
               HomeBlaze.Host (MudBlazor)
                     ^
                     |
                 HomeBlaze (Host)
```

## Project Overview

| Project | SDK | Purpose |
|---------|-----|---------|
| `HomeBlaze.Abstractions` | Microsoft.NET.Sdk | Pure interfaces, attributes, enums |
| `HomeBlaze.Services` | Microsoft.NET.Sdk | Backend domain services (no UI) |
| `HomeBlaze.Host.Services` | Microsoft.NET.Sdk | UI-agnostic services (no MudBlazor) |
| `HomeBlaze.Storage` | Microsoft.NET.Sdk | File storage domain logic (no UI) |
| `HomeBlaze.Storage.Blazor` | Microsoft.NET.Sdk.Razor | Storage UI (Monaco editor, icons) |
| `HomeBlaze.Host` | Microsoft.NET.Sdk.Razor | Shared Blazor components (MudBlazor) |
| `HomeBlaze` | Microsoft.NET.Sdk.Web | Minimal host (Program.cs only) |

## Test Projects

| Project | Tests For |
|---------|-----------|
| `HomeBlaze.Services.Tests` | HomeBlaze.Services |
| `HomeBlaze.Host.Services.Tests` | HomeBlaze.Host.Services |
| `HomeBlaze.Storage.Tests` | HomeBlaze.Storage |

---

## Project Details

### HomeBlaze.Abstractions

**Purpose**: Pure contracts and shared types for the entire ecosystem.

**Features**:
- Subject lifecycle contracts (`IConfigurableSubject`)
- Display metadata interfaces (`ITitleProvider`, `IIconProvider`, `IPage`)
- Configuration persistence (`IConfigurationWriter`)
- Storage abstraction (`IStorageContainer`, `IStorageFile`)
- Component discovery (`[SubjectComponent]` attribute)
- Property semantics (`[Configuration]`, `[State]` attributes with `StateUnit` enum)
- File type mapping (`[FileExtension]` attribute)

**Dependencies**: Only `Namotion.Interceptor`

**Use when**: Building any HomeBlaze-compatible subject or module.

---

### HomeBlaze.Services

**Purpose**: Core backend services that can run without any UI (console, API, background worker).

**Features**:
- **Root Management**: Load/save the subject tree from `root.json`, automatic persistence on changes
- **JSON Serialization**: Polymorphic serialization with `"type"` discriminator, only `[Configuration]` properties
- **Type Discovery**: Find subject types by name or file extension for dynamic instantiation
- **Context Factory**: Create `IInterceptorSubjectContext` with full tracking, validation, registry, lifecycle
- **Path Resolution**: Convert between subject references and object graph paths (e.g., `Children/0/Notes`)
- **Assembly Scanning**: Lazy type provider for discovering subjects and components across assemblies
- **Property Utilities**: Filter properties by `[Configuration]`/`[State]`, find configuration writers in hierarchy

**Dependencies**: `HomeBlaze.Abstractions`, `Namotion.Interceptor.*`, `Microsoft.Extensions.Hosting.Abstractions`

**Use when**: Building headless applications, APIs, or background services that work with subjects.

---

### HomeBlaze.Host.Services

**Purpose**: Services that support UI rendering but don't depend on specific UI frameworks like MudBlazor.

**Features**:
- **Component Discovery**: Find registered Blazor components (Page, Edit, Widget) for any subject type
- **Navigation Tree**: Build hierarchical navigation from subject graph, respecting `IPage`
- **Route Resolution**: Convert subjects to URL-friendly paths (e.g., `docs/readme.md`) for browser routing
- **Display Helpers**: Get title, icon, formatted state values with units (°C, %, kWh, etc.)
- **Developer Mode**: Toggle developer features across the application
- **Component Metadata**: Registration records linking subjects to their Blazor components

**Dependencies**: `HomeBlaze.Services`

**Use when**: Building any UI (Blazor, MAUI, WPF) that needs navigation, display formatting, or component discovery.

---

### HomeBlaze.Storage

**Purpose**: File-based storage system for subjects, without any UI dependencies.

**Features**:
- **Blob Storage Integration**: Connect to local filesystem or cloud storage via FluentStorage
- **Virtual File System**: Hierarchical folders and files as subjects in the object graph
- **File Type Support**: Generic files, JSON with schema awareness, Markdown with frontmatter
- **Change Detection**: FileSystemWatcher integration for live updates
- **Configuration Persistence**: Implement `IConfigurationWriter` for file-based subjects
- **Path Registry**: Track file locations and detect changes via content hashing

**Dependencies**: `HomeBlaze.Abstractions`, `FluentStorage`, `YamlDotNet`

**Use when**: Adding file storage capabilities to any HomeBlaze application.

---

### HomeBlaze.Storage.Blazor

**Purpose**: UI components for file storage, including code editing and file icons.

**Features**:
- **Monaco Editor**: Full code editor for JSON, Markdown, and other text files
- **Syntax Highlighting**: Language-aware editing with IntelliSense
- **File Icons**: MudBlazor icon mappings for storage, folders, and file types
- **File Editor Components**: Ready-to-use Razor components for editing storage files

**Dependencies**: `HomeBlaze.Storage`, `MudBlazor`, `BlazorMonaco`

**Use when**: Building Blazor applications that need file editing capabilities.

---

### HomeBlaze.Host

**Purpose**: Complete Blazor component library for building HomeBlaze UIs.

**Features**:
- **Subject Browser**: Tree view navigation of the subject hierarchy
- **Property Panel**: Display and edit subject properties with automatic editors
- **Configuration Editor**: Edit `[Configuration]` properties with validation
- **Edit Dialogs**: Modal dialogs for creating/editing subjects
- **Navigation Menu**: Sidebar navigation built from subject tree
- **Folder Navigation**: Expandable folder components for hierarchical navigation
- **Pages**: Home, Browser, Error pages ready to use
- **Base Component**: `HomeBlazorComponentBase` with injected root context
- **Layout**: Standard application layout with navigation drawer

**Dependencies**: `HomeBlaze.Services.UI`, `HomeBlaze.Storage.Blazor`, `MudBlazor`, `Markdig`

**Use when**: Building Blazor Server or WebAssembly applications with HomeBlaze.

---

### HomeBlaze (Host)

**Purpose**: Minimal web host that composes all modules together.

**Features**:
- **DI Composition**: Register all services from dependent projects
- **Blazor Server**: Full Blazor Server application with SignalR
- **App Startup**: Configure root subject loading and background services
- **Module Selection**: Reference only the modules needed for deployment

**Dependencies**: `HomeBlaze.Host`, `HomeBlaze.Samples` (optional)

**Use when**: Running the full HomeBlaze application.

---

### HomeBlaze.Samples

**Purpose**: Sample subjects demonstrating the framework capabilities.

**Features**:
- **Motor Subject**: Example configurable subject with state properties
- **Sample Data**: JSON configuration files for testing

**Dependencies**: `HomeBlaze.Abstractions`

**Use when**: Learning the framework or testing functionality.

---

## Service Registration

Each project provides an extension method to register its services in DI:

### Registration Methods

```csharp
// HomeBlaze.Services - Core backend services
services.AddHomeBlazeServices();

// HomeBlaze.Host.Services - UI services (also calls AddHomeBlazeServices)
services.AddHomeBlazeHostServices();

// HomeBlaze.Host - Full Blazor host (also calls AddHomeBlazeHostServices)
services.AddHomeBlazeHost();
```

### What Each Method Registers

| Method | Services |
|--------|----------|
| `AddHomeBlazeServices()` | `TypeProvider`, `SubjectTypeRegistry`, `ConfigurableSubjectSerializer`, `SubjectPathResolver`, `RootManager`, `IInterceptorSubjectContext` |
| `AddHomeBlazeHostServices()` | `SubjectComponentRegistry`, `RoutePathResolver`, `NavigationItemResolver`, `DeveloperModeService` |
| `AddHomeBlazeHost()` | MudBlazor services + all above |

---

## Usage Scenarios

### Scenario 1: Headless/API Application
```
Reference: HomeBlaze.Services + HomeBlaze.Storage
```
- Full domain logic and file storage without any UI
- Build REST APIs, console apps, or background workers
- Expose subjects via GraphQL, SignalR, or custom protocols

### Scenario 2: Custom UI Framework (MAUI, WPF, Avalonia)
```
Reference: HomeBlaze.Host.Services + HomeBlaze.Storage
```
- Domain logic + navigation/display helpers
- Use navigation tree and display extensions
- Implement own UI components using the service layer

### Scenario 3: Custom Blazor Application
```
Reference: HomeBlaze.Host
```
- Full Blazor component library
- Build custom host with selected modules
- Override or extend standard components

### Scenario 4: Full HomeBlaze Application
```
Reference: HomeBlaze (host project)
```
- Complete application with all features
- Add custom modules as project references

---

## Extension Points

### Adding Custom Subjects
1. Create a class with `[InterceptorSubject]` attribute
2. Add `[Configuration]` properties for persistence
3. Add `[State]` properties for display
4. Implement `ITitleProvider`/`IIconProvider` for UI

### Adding Custom Components
1. Create Razor component implementing `ISubjectComponent`
2. Add `[SubjectComponent]` attribute specifying target subject type
3. Component will be auto-discovered and used for that subject

### Adding Custom Modules
1. Create new project referencing appropriate layer
2. Add subjects and components
3. Reference from host project
4. Register services in DI if needed
