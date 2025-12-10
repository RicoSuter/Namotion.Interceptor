# HomeBlaze v2

HomeBlaze v2 is a complete rewrite built on Namotion.Interceptor, providing a file-system-backed state model with live tracking, automatic persistence, and protocol exposure.

## Project Structure

```
src/HomeBlaze/
├── HomeBlaze/                    # Blazor Server host, wires everything together
├── HomeBlaze.Abstractions/       # Shared interfaces, attributes, models
│   └── Attributes/               # [Configuration], [SubjectView], etc.
├── HomeBlaze.Core/               # Core services, registries, serialization
└── HomeBlaze.Storage/            # Storage containers (FileSystem, FTP, Azure, etc.)
```

## Browser UI

The browser displays the object graph using Miller columns (macOS Finder style).

### Pane Structure

1. **Header**: Icon + Title + Close button
2. **Action buttons**: Edit (if edit view registered), Delete
3. **Primitive properties**: Text blocks (`Name: value`)
4. **Child collections**: Expansion panels with list items
5. **Details accordion**: Object path and type info

### Property Display Order

1. Primitive properties (string, int, bool, DateTime, etc.)
2. Single subject references
3. Subject collections/dictionaries

### Object Path Notation

Paths use C# property access syntax:
- `Root` - root subject
- `Root.Children[Motor]` - dictionary indexer
- `Root.Children[Motor].Sensors[0]` - collection indexer

### Pane Layout

- Fixed 480px width per pane
- Horizontal scroll with auto-scroll to last pane on navigation

## SubjectView System

Custom views for subjects are registered via `[SubjectView]` attribute.

### View Types

- `edit` - Editing form, replaces pane content when Edit clicked
- `widget` - Dashboard widget representation (future)
- `setup` - Initial setup wizard (future)

### Edit View Interface

Edit views implement `ISubjectEditView`:
- `IsValid` / `IsValidChanged` - Form validation state
- `IsDirty` / `IsDirtyChanged` - Unsaved changes state
- `SaveAsync()` - Called by pane to persist changes

Events follow MudBlazor's pattern (separate events with value).

### Edit Mode Flow

1. User clicks Edit (only shown if edit view registered)
2. Pane replaces property display with edit view
3. Pane shows Save/Cancel buttons (Save disabled until valid)
4. On Save, pane calls `SaveAsync()` and exits edit mode

## Architecture Principles

### Storage is an Implementation Detail

The browser shows the **object graph**, not the file system. Storage containers materialize their children from external sources, but the browser treats all subjects uniformly.

### Live Tracking

Panes automatically refresh when tracked properties change via Namotion.Interceptor's tracking system.

### Configuration Persistence

Properties marked with `[Configuration]` are automatically persisted back to JSON when changed (debounced).

## File Naming Convention

Data files use UpperCamelCase:
- `Motor.json` not `motor.json`
- `Notes/` not `notes/`

This ensures object paths look like idiomatic C# property access.
