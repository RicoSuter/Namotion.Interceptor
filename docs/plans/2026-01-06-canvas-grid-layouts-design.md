# Canvas and Grid Layout Components Design

## Overview

Add two composable layout subjects to HomeBlaze for organizing widgets:

- **CanvasLayout**: Free-form positioning with drag-and-drop
- **GridLayout**: Structured rows/columns with optional cell spanning

Both can be embedded in markdown pages and nested within each other.

---

## Subject Model

### CanvasLayout

```csharp
[InterceptorSubject]
public partial class CanvasLayout : IConfigurableSubject, ITitleProvider
{
    [Configuration]
    public partial int? MinHeight { get; set; }  // Optional minimum height in pixels

    [Configuration]
    public partial bool SnapEnabled { get; set; }  // Enable 100px grid snap

    [Configuration]
    public partial int SnapSize { get; set; }  // Default: 100

    [Configuration]
    public partial IList<CanvasNode> Nodes { get; set; }

    public string? Title => "Canvas";
}
```

### CanvasNode

```csharp
[InterceptorSubject]
public partial class CanvasNode : IConfigurableSubject
{
    [Configuration]
    public partial int X { get; set; }  // Pixels from left

    [Configuration]
    public partial int Y { get; set; }  // Pixels from top

    [Configuration]
    public partial int Width { get; set; }  // Pixels

    [Configuration]
    public partial int Height { get; set; }  // Pixels

    [Configuration]
    public partial IInterceptorSubject? Child { get; set; }  // The widget content
}
```

### GridLayout

```csharp
[InterceptorSubject]
public partial class GridLayout : IConfigurableSubject, ITitleProvider
{
    [Configuration]
    public partial int Rows { get; set; }

    [Configuration]
    public partial int Columns { get; set; }

    [Configuration]
    public partial IList<GridCell> Cells { get; set; }

    public string? Title => "Grid";
}
```

### GridCell

```csharp
[InterceptorSubject]
public partial class GridCell : IConfigurableSubject
{
    [Configuration]
    public partial int? Row { get; set; }  // Null = auto-flow

    [Configuration]
    public partial int? Column { get; set; }  // Null = auto-flow

    [Configuration]
    public partial int RowSpan { get; set; }  // Default: 1

    [Configuration]
    public partial int ColumnSpan { get; set; }  // Default: 1

    [Configuration]
    public partial IInterceptorSubject? Child { get; set; }  // The widget content
}
```

---

## Child Content Pattern

Nodes and cells use `Child` property only. To reference another subject by path, embed a `Widget`:

```json
{
  "Child": { "$type": "HomeBlaze.Components.Widget", "Path": "Root.Demo.Motor1" }
}
```

To embed an inline subject:

```json
{
  "Child": { "$type": "HomeBlaze.Samples.Motor", "Name": "Inline Motor", "TargetSpeed": 1500 }
}
```

This reuses the existing `Widget` subject for path resolution - no duplication of logic.

---

## Widget Components

### CanvasLayoutWidget

- Renders using **Excubo.Blazor.Diagrams** library
- Each `CanvasNode` becomes a `<Node X="..." Y="...">`
- Renders `SubjectComponent` for each node's Child
- Canvas fills parent container (with optional MinHeight)

**Edit mode features:**
- Drag node body to move (updates X/Y)
- 8 resize handles (corners + edges) to resize
- Visible borders on nodes for drag affordance
- Click empty area to add (opens SubjectPickerDialog)
- Snap to grid when SnapEnabled (100px default)

### GridLayoutWidget

- Renders using CSS Grid
- Uses **MudBlazor DropZone** for drag between cells in edit mode
- Cells map to `grid-row` / `grid-column` CSS properties
- Spans via `grid-row-end: span N` / `grid-column-end: span N`
- Grid fills parent container, rows/columns divide equally

**Edit mode features:**
- Drag cells between positions (MudDropZone)
- Click empty cell to add (opens SubjectPickerDialog)
- Spans configured via property editor only

---

## Edit Mode UX

### Visual Indicators

| Element | Appearance |
|---------|------------|
| Empty areas | No indicator, cursor changes to pointer |
| Selected node/cell | Colored border (accent color) |
| Resize handles | 8 small squares at corners and edge midpoints |
| Node borders | Visible in edit mode for drag affordance |
| Delete button | Inside top-right corner, overlays content |

### Interactions

| Action | Canvas | Grid |
|--------|--------|------|
| Move | Drag node body | Drag to another cell (DropZone) |
| Resize | Drag corner/edge handles | N/A (fills cell) |
| Add | Click empty canvas | Click empty grid cell |
| Remove | Hover delete button | Hover delete button |
| Remove (touch) | Select + page delete button | Select + page delete button |
| Configure | Property editor | Property editor |

### Selection

- Single selection tracked at layout level
- Click node/cell to select
- Click empty area to deselect
- Selected item shows accent-colored border
- Page-level delete button operates on selection (touch-friendly)

---

## Markdown Integration

Embed layouts in markdown using subject blocks:

~~~markdown
# My Dashboard

```subject(dashboard)
{
  "$type": "HomeBlaze.Components.GridLayout",
  "Rows": 2,
  "Columns": 3,
  "Cells": [
    {
      "Row": 0, "Column": 0, "ColumnSpan": 2,
      "Child": { "$type": "HomeBlaze.Components.Widget", "Path": "Root.Demo.Motor1" }
    },
    {
      "Row": 0, "Column": 2,
      "Child": { "$type": "HomeBlaze.Samples.Motor", "Name": "Sensor Display" }
    }
  ]
}
```
~~~

### Nesting Example

Grid containing a Canvas:

```json
{
  "$type": "HomeBlaze.Components.GridLayout",
  "Rows": 2,
  "Columns": 1,
  "Cells": [
    {
      "Row": 0, "Column": 0,
      "Child": { "$type": "HomeBlaze.Components.Widget", "Path": "Root.Header" }
    },
    {
      "Row": 1, "Column": 0,
      "Child": {
        "$type": "HomeBlaze.Components.CanvasLayout",
        "SnapEnabled": true,
        "Nodes": [
          { "X": 50, "Y": 50, "Width": 200, "Height": 150, "Child": { "$type": "..." } }
        ]
      }
    }
  ]
}
```

---

## Dependencies

Add to HomeBlaze.Host:

```xml
<PackageReference Include="Excubo.Blazor.Diagrams" Version="4.1.*" />
```

Excubo.Blazor.Diagrams provides:
- `<Diagram>` container component
- `<Node X="..." Y="...">` for positioned content
- Built-in drag-to-move and resize
- No custom JavaScript required

---

## File Structure

```
src/HomeBlaze/
├── HomeBlaze.Components/
│   ├── CanvasLayout.cs
│   ├── CanvasNode.cs
│   ├── GridLayout.cs
│   ├── GridCell.cs
│   └── Widget.cs (existing)

├── HomeBlaze.Host/
│   └── Components/
│       ├── CanvasLayoutWidget.razor
│       ├── CanvasLayoutEditComponent.razor
│       ├── CanvasNodeEditComponent.razor
│       ├── GridLayoutWidget.razor
│       ├── GridLayoutEditComponent.razor
│       └── GridCellEditComponent.razor
```

---

## Out of Scope (YAGNI)

- Connections/lines between canvas nodes
- Per-row/column custom sizing in grid
- Undo/redo for drag operations
- Keyboard shortcuts for moving/resizing
- Copy/paste of nodes/cells
- Z-index ordering for overlapping nodes

---

## Implementation Order

1. Add Excubo.Blazor.Diagrams dependency
2. Create CanvasLayout and CanvasNode subjects
3. Create CanvasLayoutWidget with basic rendering
4. Add canvas edit mode (move, resize, add, delete)
5. Create GridLayout and GridCell subjects
6. Create GridLayoutWidget with basic rendering
7. Add grid edit mode (drag between cells, add, delete)
8. Create edit components for all subjects
9. Test markdown embedding
10. Test nesting (grid containing canvas)
