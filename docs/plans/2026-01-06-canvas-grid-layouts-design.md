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
    public partial IInterceptorSubject Child { get; set; }  // Required - no empty wrappers
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
    public partial IInterceptorSubject Child { get; set; }  // Required - no empty wrappers
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
- Resize via property editor only (no drag handles)
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

## UI Mockups

### GridLayout - View Mode (2x3 grid)

```
┌─────────────────────────────────────────────────────────────────┐
│  ┌───────────────────┐  ┌───────────────────┐  ┌─────────────┐  │
│  │                   │  │                   │  │             │  │
│  │   Motor Widget    │  │   Motor Widget    │  │   Sensor    │  │
│  │   ┌──────────┐    │  │   ┌──────────┐    │  │   Widget    │  │
│  │   │ ⚙ Motor1 │    │  │   │ ⚙ Motor2 │    │  │             │  │
│  │   │ 1500 RPM │    │  │   │ 2200 RPM │    │  │   25.3°C    │  │
│  │   └──────────┘    │  │   └──────────┘    │  │             │  │
│  │                   │  │                   │  │             │  │
│  └───────────────────┘  └───────────────────┘  └─────────────┘  │
│  ┌───────────────────┐  ┌───────────────────┐  ┌─────────────┐  │
│  │                   │  │                   │  │             │  │
│  │   Status Widget   │  │                   │  │             │  │
│  │   ✓ All systems   │  │      (empty)      │  │   (empty)   │  │
│  │     operational   │  │                   │  │             │  │
│  │                   │  │                   │  │             │  │
│  └───────────────────┘  └───────────────────┘  └─────────────┘  │
└─────────────────────────────────────────────────────────────────┘
```

### GridLayout - Edit Mode

```
┌─────────────────────────────────────────────────────────────[⚙]─┐ ← Edit Layout button
│  ┌───────────────────┐  ┌───────────────────┐  ┌─────────────┐  │   (rows, columns)
│  │ [⚙][🗑][✎]        │  │ [⚙][🗑][✎]        │  │ [⚙][🗑][✎] │  │
│  │   Motor Widget    │  │   Motor Widget    │  │   Sensor    │  │
│  │   ┌──────────┐    │  │   ┌──────────┐    │  │   Widget    │  │
│  │   │ ⚙ Motor1 │    │  │   │ ⚙ Motor2 │    │  │             │  │
│  │   │ 1500 RPM │    │  │   │ 2200 RPM │    │  │   25.3°C    │  │
│  │   └──────────┘    │  │   └──────────┘    │  │             │  │
│  │                   │  │                   │  │             │  │
│  └───────────────────┘  └───────────────────┘  └─────────────┘  │
│  ┌───────────────────┐  ╭┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄╮  ╭┄┄┄┄┄┄┄┄┄┄┄┄┄╮  │
│  │ [⚙][🗑][✎]        │  ┆                   ┆  ┆             ┆  │
│  │   Status Widget   │  ┆                   ┆  ┆             ┆  │
│  │   ✓ All systems   │  ┆        [+]        ┆  ┆     [+]     ┆  │
│  │     operational   │  ┆    click to add   ┆  ┆             ┆  │
│  │                   │  ┆                   ┆  ┆             ┆  │
│  └───────────────────┘  ╰┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄╯  ╰┄┄┄┄┄┄┄┄┄┄┄┄┄╯  │
└─────────────────────────────────────────────────────────────────┘
  ↑ solid border = has content       ↑ dashed border = empty cell

  Layout Button (top-right of container):
  [⚙] = Edit Layout (rows, columns)  → opens GridLayoutEditComponent dialog

  Cell Buttons (top-right of each cell, via SubjectComponent ActionButtons):
  [⚙] = Edit Cell (row, column, spans) → opens GridCellEditComponent dialog
  [🗑] = Delete Cell                    → shows confirmation, removes cell
  [✎] = Edit Widget (from SubjectComponent) → opens child's edit dialog
```

### CanvasLayout - View Mode

```
┌─────────────────────────────────────────────────────────────────┐
│                                                                 │
│     ┌──────────────┐                                            │
│     │ Motor Widget │                                            │
│     │   ⚙ Motor1   │        ┌─────────────────────┐             │
│     │   1500 RPM   │        │   Status Dashboard  │             │
│     └──────────────┘        │   ┌───┐ ┌───┐ ┌───┐ │             │
│                             │   │ ✓ │ │ ✓ │ │ ! │ │             │
│                             │   └───┘ └───┘ └───┘ │             │
│                             └─────────────────────┘             │
│                                                                 │
│          ┌────────────────────┐                                 │
│          │   Sensor Widget    │                                 │
│          │     Temperature    │                                 │
│          │      25.3°C        │                                 │
│          └────────────────────┘                                 │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

### CanvasLayout - Edit Mode

```
┌─────────────────────────────────────────────────────────────[⚙]─┐ ← Edit Layout button
│ · · · · · · · · · · · · · · · · · · · · · · · · · · · · · · · · │   (snap, min height)
│ ·   ┌──────────────┐ ·                                        · │ ← snap grid dots
│ ·   │[⚙][🗑][✎]    │ ·        ┌─────────────────────┐         · │   (when enabled)
│ ·   │ Motor Widget │ ·        │[⚙][🗑][✎]           │         · │
│ ·   │   ⚙ Motor1   │ ·        │   Status Dashboard  │         · │
│ ·   │   1500 RPM   │ ·        │   ┌───┐ ┌───┐ ┌───┐ │         · │
│ ·   └──────────────┘ ·        │   │ ✓ │ │ ✓ │ │ ! │ │         · │
│ · · · · · · · · · · · ·       │   └───┘ └───┘ └───┘ │         · │
│ ·                     ·       └─────────────────────┘         · │
│ ·        ┌────────────────────┐                               · │
│ ·        │[⚙][🗑][✎]          │    ← drag node body to move   · │
│ ·        │   Sensor Widget    │                                · │
│ ·        │     Temperature    │                                · │
│ ·        │      25.3°C        │                                · │
│ ·        └────────────────────┘                                · │
│ · · · · · · · · · · · · · · · · · · · · · · · · · · · · · · · · │
│                                                                 │
│   Click empty area to add new widget                            │
└─────────────────────────────────────────────────────────────────┘

  Layout Button (top-right of container):
  [⚙] = Edit Layout (min height, snap settings) → opens CanvasLayoutEditComponent dialog

  Node Buttons (top-right of each node, via SubjectComponent ActionButtons):
  [⚙] = Edit Node (x, y, width, height) → opens CanvasNodeEditComponent dialog
  [🗑] = Delete Node                     → shows confirmation, removes node
  [✎] = Edit Widget (from SubjectComponent) → opens child's edit dialog
```

### SubjectSetupDialog (when clicking empty cell/area)

```
╔═══════════════════════════════════════════════════════════════╗
║  Step 1: Select Type                                      [X] ║
╠═══════════════════════════════════════════════════════════════╣
║                                                               ║
║  Name: [my-widget          ]                                  ║
║        Name for the subject (will be saved as {name}.json)    ║
║                                                               ║
║  ─────────────────────────────────────────────────────────    ║
║                                                               ║
║  Layouts                                                      ║
║  ┌─────────────────┐  ┌─────────────────┐                     ║
║  │ ═══ Canvas      │  │ ▦ Grid          │                     ║
║  │ Free-form       │  │ Rows/columns    │                     ║
║  │ positioning     │  │ layout          │                     ║
║  └─────────────────┘  └─────────────────┘                     ║
║                                                               ║
║  Samples                                                      ║
║  ┌─────────────────┐  ┌─────────────────┐                     ║
║  │ ⚙ Motor         │  │ 🌡 Sensor       │                     ║
║  │ Simulated       │  │ Temperature     │                     ║
║  │ motor control   │  │ sensor          │                     ║
║  └─────────────────┘  └─────────────────┘                     ║
║                                                               ║
║  Widgets                                                      ║
║  ┌─────────────────┐                                          ║
║  │ 🔗 Widget       │                                          ║
║  │ Reference to    │                                          ║
║  │ another subject │                                          ║
║  └─────────────────┘                                          ║
║                                                               ║
╠═══════════════════════════════════════════════════════════════╣
║                                    [Cancel]  [Next →]         ║
╚═══════════════════════════════════════════════════════════════╝
```

### GridLayoutEditComponent (in Edit Dialog)

```
╔═══════════════════════════════════════════════════════════════╗
║  Edit GridLayout                                          [X] ║
╠═══════════════════════════════════════════════════════════════╣
║                                                               ║
║  Rows                                                         ║
║  ┌────────────────────────────────────────┐                   ║
║  │ [2                                 ] ▼ │                   ║
║  └────────────────────────────────────────┘                   ║
║                                                               ║
║  Columns                                                      ║
║  ┌────────────────────────────────────────┐                   ║
║  │ [3                                 ] ▼ │                   ║
║  └────────────────────────────────────────┘                   ║
║                                                               ║
║  Cells: 4                                                     ║
║                                                               ║
╠═══════════════════════════════════════════════════════════════╣
║                                    [Cancel]  [Save]           ║
╚═══════════════════════════════════════════════════════════════╝
```

### CanvasLayoutEditComponent (in Edit Dialog)

```
╔═══════════════════════════════════════════════════════════════╗
║  Edit CanvasLayout                                        [X] ║
╠═══════════════════════════════════════════════════════════════╣
║                                                               ║
║  Minimum Height (pixels)                                      ║
║  ┌────────────────────────────────────────┐                   ║
║  │ [400                               ]   │                   ║
║  └────────────────────────────────────────┘                   ║
║  Optional. Leave empty for auto height.                       ║
║                                                               ║
║  ┌────┐                                                       ║
║  │ ✓  │  Enable Grid Snap                                     ║
║  └────┘                                                       ║
║                                                               ║
║  Snap Size (pixels)                                           ║
║  ┌────────────────────────────────────────┐                   ║
║  │ [100                               ]   │                   ║
║  └────────────────────────────────────────┘                   ║
║                                                               ║
║  Nodes: 3                                                     ║
║                                                               ║
╠═══════════════════════════════════════════════════════════════╣
║                                    [Cancel]  [Save]           ║
╚═══════════════════════════════════════════════════════════════╝
```

### GridCellEditComponent (in Edit Dialog)

```
╔═══════════════════════════════════════════════════════════════╗
║  Edit GridCell                                            [X] ║
╠═══════════════════════════════════════════════════════════════╣
║                                                               ║
║  Position (leave empty for auto-flow)                         ║
║                                                               ║
║  Row                          Column                          ║
║  ┌──────────────────┐        ┌──────────────────┐             ║
║  │ [0           ] ✕ │        │ [1           ] ✕ │             ║
║  └──────────────────┘        └──────────────────┘             ║
║                                                               ║
║  Span                                                         ║
║                                                               ║
║  Row Span                     Column Span                     ║
║  ┌──────────────────┐        ┌──────────────────┐             ║
║  │ [1             ] │        │ [2             ] │             ║
║  └──────────────────┘        └──────────────────┘             ║
║                                                               ║
║  ───────────────────────────────────────────────────────────  ║
║                                                               ║
║  Child Widget                                                 ║
║  ┌─────────────────────────────────────────────────────────┐  ║
║  │ ℹ Motor                                                 │  ║
║  └─────────────────────────────────────────────────────────┘  ║
║                                                               ║
╠═══════════════════════════════════════════════════════════════╣
║                                    [Cancel]  [Save]           ║
╚═══════════════════════════════════════════════════════════════╝
```

### Markdown Page with Embedded Grid

```
╔═══════════════════════════════════════════════════════════════╗
║  📄 Dashboard.md                            2026-01-06 14:32  ║
╠═══════════════════════════════════════════════════════════════╣
║                                                               ║
║  # My Dashboard                                               ║
║                                                               ║
║  Welcome to the monitoring dashboard. Here's the current      ║
║  status of all systems:                                       ║
║                                                               ║
║  ┌─────────────────────────────────────────────────────────┐  ║
║  │  ┌─────────────────┐  ┌─────────────────┐  ┌─────────┐  │  ║
║  │  │   Motor 1       │  │   Motor 2       │  │ Temp    │  │  ║
║  │  │   ⚙ Running     │  │   ⚙ Running     │  │ 25.3°C  │  │  ║
║  │  │   1500 RPM      │  │   2200 RPM      │  │         │  │  ║
║  │  └─────────────────┘  └─────────────────┘  └─────────┘  │  ║
║  │  ┌─────────────────────────────────────┐  ┌─────────┐  │  ║
║  │  │         System Status               │  │ Alerts  │  │  ║
║  │  │   ✓ All systems operational         │  │  0 new  │  │  ║
║  │  └─────────────────────────────────────┘  └─────────┘  │  ║
║  └─────────────────────────────────────────────────────────┘  ║
║        ↑ GridLayout embedded via ```subject(dashboard)```     ║
║                                                               ║
║  ## Notes                                                     ║
║                                                               ║
║  - All motors are running within normal parameters            ║
║  - Temperature is stable                                      ║
║                                                               ║
╚═══════════════════════════════════════════════════════════════╝
```

### Nested Layout (Grid containing Canvas)

```
┌─────────────────────────────────────────────────────────────────┐
│  GridLayout (2 rows, 1 column)                                  │
│  ┌───────────────────────────────────────────────────────────┐  │
│  │   Header Widget - Navigation Bar                          │  │
│  │   [Home] [Dashboard] [Settings]                           │  │
│  └───────────────────────────────────────────────────────────┘  │
│  ┌───────────────────────────────────────────────────────────┐  │
│  │   CanvasLayout (nested)                                   │  │
│  │   ┌─────────────┐                                         │  │
│  │   │ Motor 1     │     ┌──────────────────┐                │  │
│  │   │ 1500 RPM    │     │ Status Panel     │                │  │
│  │   └─────────────┘     │ ✓ All OK         │                │  │
│  │                       └──────────────────┘                │  │
│  │         ┌─────────────────┐                               │  │
│  │         │ Sensor Array    │                               │  │
│  │         │ T: 25°C H: 60%  │                               │  │
│  │         └─────────────────┘                               │  │
│  └───────────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────────┘
```

---

## Edit Mode UX

### Visual Indicators

| Element | Appearance |
|---------|------------|
| Layout container | [⚙] Edit Layout button in top-right corner |
| Empty areas | Dashed border, cursor changes to pointer, [+] icon |
| Filled cells/nodes | Solid border, action buttons in top-right |
| Action buttons | Three buttons: [⚙] Edit Node/Cell, [✎] Edit Widget, [🗑] Delete |

### Interactions

| Action | Canvas | Grid |
|--------|--------|------|
| Edit layout config | [⚙] on container | [⚙] on container |
| Move | Drag node body | Drag to another cell (DropZone) |
| Resize | [⚙] Edit Node dialog | N/A (fills cell) |
| Add | Click empty canvas | Click empty grid cell |
| Remove | [🗑] Delete button | [🗑] Delete button |
| Edit position/spans | [⚙] Edit Node/Cell button | [⚙] Edit Cell button |
| Edit child widget | [✎] Edit Widget button | [✎] Edit Widget button |

### Action Buttons

**Layout Container (top-right corner):**
```
┌─────────────────────────────────────────[⚙]─┐
│                                             │
│   ... layout content ...                    │
│                                             │
└─────────────────────────────────────────────┘
```
- [⚙] Edit Layout → Opens CanvasLayoutEditComponent or GridLayoutEditComponent dialog

**Each Node/Cell (top-right corner, via SubjectComponent ActionButtons):**
```
┌─────────────────────────────┐
│ [⚙][🗑][✎]                  │
│                             │
│   Widget Content            │
│                             │
└─────────────────────────────┘
```

| Button | Icon | Source | Action |
|--------|------|--------|--------|
| Edit Node/Cell | ⚙ (Settings) | ActionButtons | Opens dialog to edit position/size (canvas) or row/column/spans (grid) |
| Delete | 🗑 (Delete) | ActionButtons | Shows confirmation dialog, then removes the node/cell |
| Edit Widget | ✎ (Edit) | SubjectComponent | Opens dialog to edit the child widget's properties |

**No selection state needed** - all actions are directly on the element via buttons.
**Reuses SubjectComponent's edit button** - no duplicate "Edit Widget" button needed.

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

## SubjectComponent Extension

Extend `SubjectComponent` to accept additional action buttons:

### Updated SubjectComponent.razor

```razor
@inject SubjectComponentRegistry ComponentRegistry
@inject IDialogService DialogService

@if (Subject != null)
{
    var registration = ComponentRegistry.GetComponent(Subject.GetType(), Type);
    if (registration?.ComponentType != null)
    {
        <div class="subject-component-wrapper" style="position: relative;">
            @if (IsEditing && Type == SubjectComponentType.Widget && (ActionButtons != null || HasEditComponent))
            {
                <div class="subject-edit-overlay">
                    @* Additional action buttons from parent *@
                    @ActionButtons

                    @* Built-in edit button (if edit component exists) *@
                    @if (HasEditComponent)
                    {
                        <MudIconButton Icon="@Icons.Material.Filled.Edit"
                                       Size="Size.Small"
                                       Color="Color.Primary"
                                       Title="Edit"
                                       OnClick="OpenEditDialog"
                                       data-testid="edit-subject-button" />
                    }
                </div>
            }
            <DynamicComponent Type="registration.ComponentType"
                              @ref="_dynamicComponent"
                              Parameters="@GetComponentParameters()" />
        </div>
    }
    else
    {
        <MudAlert Severity="Severity.Warning" Dense="true">
            No @Type component for @Subject.GetType().Name
        </MudAlert>
    }
}

<style>
    .subject-edit-overlay {
        position: absolute;
        top: 4px;
        right: 4px;
        z-index: 10;
        display: flex;
        gap: 2px;
        background: rgba(255,255,255,0.95);
        border-radius: 4px;
        padding: 2px;
        box-shadow: 0 1px 3px rgba(0,0,0,0.2);
        opacity: 0.9;
    }
    .subject-edit-overlay:hover {
        opacity: 1;
    }
    .subject-edit-overlay .mud-icon-button {
        padding: 4px;
    }
</style>

@code {
    private DynamicComponent? _dynamicComponent;

    [Parameter]
    public IInterceptorSubject? Subject { get; set; }

    [Parameter]
    public SubjectComponentType Type { get; set; }

    /// <summary>
    /// Additional action buttons to display in the edit overlay.
    /// Rendered before the built-in edit button.
    /// </summary>
    [Parameter]
    public RenderFragment? ActionButtons { get; set; }

    /// <summary>
    /// Additional parameters to pass to the rendered component.
    /// </summary>
    [Parameter]
    public Dictionary<string, object?>? AdditionalParameters { get; set; }

    [CascadingParameter(Name = "IsEditing")]
    public bool IsEditing { get; set; }

    [Parameter]
    public ISubjectComponent? ComponentInstance { get; set; }

    [Parameter]
    public EventCallback<ISubjectComponent?> ComponentInstanceChanged { get; set; }

    private bool HasEditComponent =>
        Subject != null &&
        ComponentRegistry.HasComponent(Subject.GetType(), SubjectComponentType.Edit);

    // ... rest of existing code unchanged ...
}
```

**Key changes:**
- Added `ActionButtons` RenderFragment parameter
- Updated `.subject-edit-overlay` to use flexbox for multiple buttons
- Overlay now shows even without edit component (if ActionButtons provided)
- ActionButtons render before the built-in edit button

---

## Dependencies

Add to HomeBlaze.Components.csproj:

```xml
<PackageReference Include="Excubo.Blazor.Diagrams" Version="4.1.*" />
```

### Full HomeBlaze.Components.csproj Changes

```xml
<ItemGroup>
    <!-- Existing references... -->

    <!-- ADD THIS LINE -->
    <PackageReference Include="Excubo.Blazor.Diagrams" Version="4.1.*" />
</ItemGroup>
```

Excubo.Blazor.Diagrams provides:
- `<Diagram>` container component
- `<Node X="..." Y="...">` for positioned content
- Built-in drag-to-move
- No custom JavaScript required

### _Imports.razor Addition

Add to `src/HomeBlaze/HomeBlaze.Components/_Imports.razor`:

```razor
@using Excubo.Blazor.Diagrams
```

---

## File Structure

```
src/HomeBlaze/
├── HomeBlaze.Components/
│   ├── CanvasLayout.cs              # Subject class
│   ├── CanvasNode.cs                # Subject class
│   ├── GridLayout.cs                # Subject class
│   ├── GridCell.cs                  # Subject class
│   ├── Widget.cs                    # (existing)
│   └── Components/
│       ├── CanvasLayoutWidget.razor
│       ├── CanvasLayoutEditComponent.razor
│       ├── CanvasNodeEditComponent.razor
│       ├── GridLayoutWidget.razor
│       ├── GridLayoutEditComponent.razor
│       └── GridCellEditComponent.razor
```

### Exact File Paths

| File | Full Path |
|------|-----------|
| CanvasLayout.cs | `src/HomeBlaze/HomeBlaze.Components/CanvasLayout.cs` |
| CanvasNode.cs | `src/HomeBlaze/HomeBlaze.Components/CanvasNode.cs` |
| GridLayout.cs | `src/HomeBlaze/HomeBlaze.Components/GridLayout.cs` |
| GridCell.cs | `src/HomeBlaze/HomeBlaze.Components/GridCell.cs` |
| CanvasLayoutWidget.razor | `src/HomeBlaze/HomeBlaze.Components/Components/CanvasLayoutWidget.razor` |
| CanvasLayoutEditComponent.razor | `src/HomeBlaze/HomeBlaze.Components/Components/CanvasLayoutEditComponent.razor` |
| CanvasNodeEditComponent.razor | `src/HomeBlaze/HomeBlaze.Components/Components/CanvasNodeEditComponent.razor` |
| GridLayoutWidget.razor | `src/HomeBlaze/HomeBlaze.Components/Components/GridLayoutWidget.razor` |
| GridLayoutEditComponent.razor | `src/HomeBlaze/HomeBlaze.Components/Components/GridLayoutEditComponent.razor` |
| GridCellEditComponent.razor | `src/HomeBlaze/HomeBlaze.Components/Components/GridCellEditComponent.razor` |

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

---

## Detailed Implementation

### Subject Implementation

#### CanvasLayout.cs

```csharp
using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Components;

[Category("Layouts")]
[Description("Free-form canvas layout with draggable widgets")]
[InterceptorSubject]
public partial class CanvasLayout : IConfigurableSubject, ITitleProvider
{
    [Configuration]
    public partial int? MinHeight { get; set; }

    [Configuration]
    public partial bool SnapEnabled { get; set; }

    [Configuration]
    public partial int SnapSize { get; set; }

    [Configuration]
    public partial List<CanvasNode> Nodes { get; set; }

    public string? Title => "Canvas";

    public CanvasLayout()
    {
        SnapSize = 100;
        Nodes = new List<CanvasNode>();
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
```

#### CanvasNode.cs

```csharp
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Components;

[InterceptorSubject]
public partial class CanvasNode : IConfigurableSubject
{
    [Configuration]
    public partial int X { get; set; }

    [Configuration]
    public partial int Y { get; set; }

    [Configuration]
    public partial int Width { get; set; }

    [Configuration]
    public partial int Height { get; set; }

    [Configuration]
    public partial IInterceptorSubject Child { get; set; }

    public CanvasNode()
    {
        Width = 100;
        Height = 100;
        Child = null!; // Set during creation
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
```

#### GridLayout.cs

```csharp
using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Components;

[Category("Layouts")]
[Description("Grid layout with configurable rows and columns")]
[InterceptorSubject]
public partial class GridLayout : IConfigurableSubject, ITitleProvider
{
    [Configuration]
    public partial int Rows { get; set; }

    [Configuration]
    public partial int Columns { get; set; }

    [Configuration]
    public partial List<GridCell> Cells { get; set; }

    public string? Title => "Grid";

    public GridLayout()
    {
        Rows = 2;
        Columns = 2;
        Cells = new List<GridCell>();
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
```

#### GridCell.cs

```csharp
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Components;

[InterceptorSubject]
public partial class GridCell : IConfigurableSubject
{
    [Configuration]
    public partial int? Row { get; set; }  // Null = auto-flow

    [Configuration]
    public partial int? Column { get; set; }  // Null = auto-flow

    [Configuration]
    public partial int RowSpan { get; set; }

    [Configuration]
    public partial int ColumnSpan { get; set; }

    [Configuration]
    public partial IInterceptorSubject Child { get; set; }

    public GridCell()
    {
        RowSpan = 1;
        ColumnSpan = 1;
        Child = null!; // Set during creation
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
```

---

### Widget Component Implementation

#### CanvasLayoutWidget.razor

**Key patterns from codebase analysis:**

```razor
@using Excubo.Blazor.Diagrams
@using HomeBlaze.Components.Abstractions
@using HomeBlaze.Components.Abstractions.Attributes
@using HomeBlaze.Components.Dialogs

@attribute [SubjectComponent(SubjectComponentType.Widget, typeof(CanvasLayout))]
@implements ISubjectComponent

@inject IDialogService DialogService
@inject SubjectFactory SubjectFactory
@inject SubjectComponentRegistry ComponentRegistry

<div class="canvas-layout-container"
     style="position: relative; width: 100%; min-height: @(Canvas?.MinHeight ?? 300)px;"
     @onclick="OnCanvasClick"
     @onclick:stopPropagation="true">

    @* Layout edit button - top right of container *@
    @if (IsEditing)
    {
        <MudIconButton Icon="@Icons.Material.Filled.Settings"
                       Size="Size.Small"
                       Color="Color.Primary"
                       Class="layout-edit-button"
                       Title="Edit Layout"
                       OnClick="EditLayout" />
    }

    <Diagram @ref="_diagram">
        <Nodes>
            @if (Canvas?.Nodes != null)
            {
                @foreach (var node in Canvas.Nodes)
                {
                    <Node @key="node"
                          Id="@GetNodeId(node)"
                          X="@node.X"
                          Y="@node.Y"
                          XChanged="@(x => OnNodePositionChanged(node, (int)x, node.Y))"
                          YChanged="@(y => OnNodePositionChanged(node, node.X, (int)y))">
                        <div class="canvas-node"
                             style="width: @(node.Width)px; height: @(node.Height)px;">
                            <SubjectComponent Subject="@node.Child"
                                              Type="SubjectComponentType.Widget">
                                <ActionButtons>
                                    <MudIconButton Icon="@Icons.Material.Filled.Settings"
                                                   Size="Size.Small"
                                                   Title="Edit Node"
                                                   OnClick="() => EditNode(node)" />
                                    <MudIconButton Icon="@Icons.Material.Filled.Delete"
                                                   Size="Size.Small"
                                                   Color="Color.Error"
                                                   Title="Delete"
                                                   OnClick="() => DeleteNode(node)" />
                                </ActionButtons>
                            </SubjectComponent>
                        </div>
                    </Node>
                }
            }
        </Nodes>
    </Diagram>
</div>

<style>
    .canvas-layout-container {
        background: rgba(0,0,0,0.02);
        border: 1px dashed rgba(128,128,128,0.3);
    }

    .layout-edit-button {
        position: absolute;
        top: 4px;
        right: 4px;
        z-index: 20;
        background: rgba(255,255,255,0.9);
        border-radius: 4px;
    }

    .canvas-node {
        background: var(--mud-palette-surface);
        border: 1px solid rgba(128,128,128,0.2);
        border-radius: 4px;
        overflow: hidden;
    }
</style>

@code {
    private Diagram? _diagram;

    [Parameter]
    public IInterceptorSubject? Subject { get; set; }

    [CascadingParameter(Name = "IsEditing")]
    public bool IsEditing { get; set; }

    private CanvasLayout? Canvas => Subject as CanvasLayout;

    private string GetNodeId(CanvasNode node) => node.GetHashCode().ToString();

    private void OnNodePositionChanged(CanvasNode node, int x, int y)
    {
        if (Canvas?.SnapEnabled == true)
        {
            var snapSize = Canvas.SnapSize > 0 ? Canvas.SnapSize : 100;
            x = (int)Math.Round((double)x / snapSize) * snapSize;
            y = (int)Math.Round((double)y / snapSize) * snapSize;
        }

        node.X = x;
        node.Y = y;
    }

    private async Task OnCanvasClick(MouseEventArgs e)
    {
        if (!IsEditing) return;

        // Add node at click position
        await AddNodeAtPosition((int)e.OffsetX, (int)e.OffsetY);
    }

    private async Task AddNodeAtPosition(int x, int y)
    {
        if (Canvas?.SnapEnabled == true)
        {
            var snapSize = Canvas.SnapSize > 0 ? Canvas.SnapSize : 100;
            x = (int)Math.Round((double)x / snapSize) * snapSize;
            y = (int)Math.Round((double)y / snapSize) * snapSize;
        }

        var result = await SubjectSetupDialog.ShowAsync(DialogService);
        if (result?.Subject == null) return;

        var node = SubjectFactory.CreateSubject<CanvasNode>();
        node.X = x;
        node.Y = y;
        node.Child = result.Subject;

        Canvas?.Nodes.Add(node);
    }

    private async Task EditLayout()
    {
        if (Canvas == null) return;
        await SubjectEditDialog.ShowAsync(DialogService, ComponentRegistry, Canvas, "Edit Canvas Layout");
    }

    private async Task EditNode(CanvasNode node)
    {
        await SubjectEditDialog.ShowAsync(DialogService, ComponentRegistry, node, "Edit Node");
    }

    private async Task DeleteNode(CanvasNode node)
    {
        var confirmed = await DialogService.ShowMessageBox(
            "Delete Node",
            "Are you sure you want to delete this node?",
            yesText: "Delete",
            cancelText: "Cancel");

        if (confirmed == true)
        {
            Canvas?.Nodes.Remove(node);
        }
    }
}
```

**Note on resizing:**
Node resizing is handled via the property editor (CanvasNodeEditComponent) rather than drag handles. This avoids JavaScript interop complexity while still providing full control over width/height values.

---

#### GridLayoutWidget.razor

```razor
@using HomeBlaze.Components.Abstractions
@using HomeBlaze.Components.Abstractions.Attributes
@using HomeBlaze.Components.Dialogs

@attribute [SubjectComponent(SubjectComponentType.Widget, typeof(GridLayout))]
@implements ISubjectComponent

@inject IDialogService DialogService
@inject SubjectFactory SubjectFactory
@inject SubjectComponentRegistry ComponentRegistry

<div class="grid-layout-container"
     style="display: grid;
            grid-template-rows: repeat(@(Grid?.Rows ?? 1), 1fr);
            grid-template-columns: repeat(@(Grid?.Columns ?? 1), 1fr);
            gap: 8px;
            width: 100%;
            min-height: 200px;
            position: relative;">

    @* Layout edit button - top right of container *@
    @if (IsEditing)
    {
        <MudIconButton Icon="@Icons.Material.Filled.Settings"
                       Size="Size.Small"
                       Color="Color.Primary"
                       Class="layout-edit-button"
                       Title="Edit Layout"
                       OnClick="EditLayout" />
    }

    @* Render cells *@
    @if (Grid?.Cells != null)
    {
        foreach (var cell in Grid.Cells)
        {
            <div class="grid-cell" style="@GetCellStyle(cell)">
                <SubjectComponent Subject="@cell.Child"
                                  Type="SubjectComponentType.Widget">
                    <ActionButtons>
                        <MudIconButton Icon="@Icons.Material.Filled.Settings"
                                       Size="Size.Small"
                                       Title="Edit Cell"
                                       OnClick="() => EditCell(cell)" />
                        <MudIconButton Icon="@Icons.Material.Filled.Delete"
                                       Size="Size.Small"
                                       Color="Color.Error"
                                       Title="Delete"
                                       OnClick="() => DeleteCell(cell)" />
                    </ActionButtons>
                </SubjectComponent>
            </div>
        }
    }

    @* Render empty cells for adding in edit mode *@
    @if (IsEditing)
    {
        @foreach (var (row, col) in GetEmptyCellPositions())
        {
            <div class="grid-cell empty"
                 style="grid-row: @(row + 1); grid-column: @(col + 1);"
                 @onclick="() => AddCellAt(row, col)">
                <MudIcon Icon="@Icons.Material.Filled.Add"
                         Color="Color.Default"
                         Class="add-icon" />
            </div>
        }
    }
</div>

<style>
    .grid-layout-container {
        background: rgba(0,0,0,0.02);
        border: 1px dashed rgba(128,128,128,0.3);
        padding: 8px;
    }

    .layout-edit-button {
        position: absolute;
        top: -36px;
        right: 4px;
        z-index: 20;
        background: rgba(255,255,255,0.9);
        border-radius: 4px;
    }

    .grid-cell {
        background: var(--mud-palette-surface);
        border: 1px solid rgba(128,128,128,0.2);
        border-radius: 4px;
        min-height: 100px;
        overflow: hidden;
    }

    .grid-cell.empty {
        display: flex;
        align-items: center;
        justify-content: center;
        cursor: pointer;
        border-style: dashed;
    }

    .grid-cell.empty:hover {
        background: rgba(var(--mud-palette-primary-rgb), 0.1);
    }

    .add-icon {
        opacity: 0.5;
    }
</style>

@code {
    [Parameter]
    public IInterceptorSubject? Subject { get; set; }

    [CascadingParameter(Name = "IsEditing")]
    public bool IsEditing { get; set; }

    private GridLayout? Grid => Subject as GridLayout;

    private string GetCellStyle(GridCell cell)
    {
        var styles = new List<string>();

        if (cell.Row.HasValue)
            styles.Add($"grid-row: {cell.Row.Value + 1} / span {cell.RowSpan}");

        if (cell.Column.HasValue)
            styles.Add($"grid-column: {cell.Column.Value + 1} / span {cell.ColumnSpan}");

        return string.Join("; ", styles);
    }

    private IEnumerable<(int row, int col)> GetEmptyCellPositions()
    {
        if (Grid == null) yield break;

        var occupied = new HashSet<(int, int)>();

        // Mark all occupied cells (accounting for spans)
        foreach (var cell in Grid.Cells)
        {
            if (!cell.Row.HasValue || !cell.Column.HasValue) continue;

            for (int r = 0; r < cell.RowSpan; r++)
            {
                for (int c = 0; c < cell.ColumnSpan; c++)
                {
                    occupied.Add((cell.Row.Value + r, cell.Column.Value + c));
                }
            }
        }

        // Return unoccupied positions
        for (int r = 0; r < Grid.Rows; r++)
        {
            for (int c = 0; c < Grid.Columns; c++)
            {
                if (!occupied.Contains((r, c)))
                    yield return (r, c);
            }
        }
    }

    private async Task AddCellAt(int row, int column)
    {
        var result = await SubjectSetupDialog.ShowAsync(DialogService);
        if (result?.Subject == null) return;

        var cell = SubjectFactory.CreateSubject<GridCell>();
        cell.Row = row;
        cell.Column = column;
        cell.Child = result.Subject;

        Grid?.Cells.Add(cell);
    }

    private async Task EditLayout()
    {
        if (Grid == null) return;
        await SubjectEditDialog.ShowAsync(DialogService, ComponentRegistry, Grid, "Edit Grid Layout");
    }

    private async Task EditCell(GridCell cell)
    {
        await SubjectEditDialog.ShowAsync(DialogService, ComponentRegistry, cell, "Edit Cell");
    }

    private async Task DeleteCell(GridCell cell)
    {
        var confirmed = await DialogService.ShowMessageBox(
            "Delete Cell",
            "Are you sure you want to delete this cell?",
            yesText: "Delete",
            cancelText: "Cancel");

        if (confirmed == true)
        {
            Grid?.Cells.Remove(cell);
        }
    }
}
```

---

### Edit Components

#### CanvasLayoutEditComponent.razor

```razor
@attribute [SubjectComponent(SubjectComponentType.Edit, typeof(CanvasLayout))]
@implements ISubjectEditComponent
@implements IDisposable

<MudForm>
    <MudNumericField @bind-Value="_minHeight"
                     Label="Minimum Height (pixels)"
                     HelperText="Optional. Leave empty for auto height."
                     Class="mb-4" />

    <MudSwitch @bind-Value="_snapEnabled"
               Label="Enable Grid Snap"
               Color="Color.Primary"
               Class="mb-4" />

    @if (_snapEnabled)
    {
        <MudNumericField @bind-Value="_snapSize"
                         Label="Snap Size (pixels)"
                         Min="10"
                         Max="500"
                         Class="mb-4" />
    }

    <MudText Typo="Typo.body2" Class="mud-text-secondary">
        Nodes: @(Canvas?.Nodes?.Count ?? 0)
    </MudText>
</MudForm>

@code {
    [Parameter]
    public IInterceptorSubject? Subject { get; set; }

    [Parameter]
    public bool IsCreating { get; set; }

    private CanvasLayout? Canvas => Subject as CanvasLayout;

    private int? _minHeight;
    private bool _snapEnabled;
    private int _snapSize;

    private int? _originalMinHeight;
    private bool _originalSnapEnabled;
    private int _originalSnapSize;

    public bool IsValid => true;
    public bool IsDirty => _minHeight != _originalMinHeight
                        || _snapEnabled != _originalSnapEnabled
                        || _snapSize != _originalSnapSize;

    public event Action<bool>? IsValidChanged;
    public event Action<bool>? IsDirtyChanged;

    protected override void OnInitialized()
    {
        if (Canvas != null)
        {
            _minHeight = Canvas.MinHeight;
            _snapEnabled = Canvas.SnapEnabled;
            _snapSize = Canvas.SnapSize;

            _originalMinHeight = _minHeight;
            _originalSnapEnabled = _snapEnabled;
            _originalSnapSize = _snapSize;
        }
    }

    public Task SaveAsync(CancellationToken cancellationToken)
    {
        if (Canvas != null)
        {
            Canvas.MinHeight = _minHeight;
            Canvas.SnapEnabled = _snapEnabled;
            Canvas.SnapSize = _snapSize;

            _originalMinHeight = _minHeight;
            _originalSnapEnabled = _snapEnabled;
            _originalSnapSize = _snapSize;

            IsDirtyChanged?.Invoke(false);
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        IsValidChanged = null;
        IsDirtyChanged = null;
    }
}
```

#### CanvasNodeEditComponent.razor

```razor
@using HomeBlaze.Components.Inputs

@attribute [SubjectComponent(SubjectComponentType.Edit, typeof(CanvasNode))]
@implements ISubjectEditComponent
@implements IDisposable

<MudForm>
    <MudGrid>
        <MudItem xs="6">
            <MudNumericField @bind-Value="_x"
                             Label="X Position"
                             Adornment="Adornment.End"
                             AdornmentText="px" />
        </MudItem>
        <MudItem xs="6">
            <MudNumericField @bind-Value="_y"
                             Label="Y Position"
                             Adornment="Adornment.End"
                             AdornmentText="px" />
        </MudItem>
        <MudItem xs="6">
            <MudNumericField @bind-Value="_width"
                             Label="Width"
                             Min="50"
                             Adornment="Adornment.End"
                             AdornmentText="px" />
        </MudItem>
        <MudItem xs="6">
            <MudNumericField @bind-Value="_height"
                             Label="Height"
                             Min="50"
                             Adornment="Adornment.End"
                             AdornmentText="px" />
        </MudItem>
    </MudGrid>

    <MudDivider Class="my-4" />

    <MudText Typo="Typo.subtitle2" Class="mb-2">Child Widget</MudText>
    @if (Node?.Child != null)
    {
        <MudAlert Severity="Severity.Info" Dense="true">
            @Node.Child.GetType().Name
        </MudAlert>
    }
    else
    {
        <MudAlert Severity="Severity.Warning" Dense="true">
            No child widget assigned
        </MudAlert>
    }
</MudForm>

@code {
    [Parameter]
    public IInterceptorSubject? Subject { get; set; }

    [Parameter]
    public bool IsCreating { get; set; }

    private CanvasNode? Node => Subject as CanvasNode;

    private int _x, _y, _width, _height;
    private int _originalX, _originalY, _originalWidth, _originalHeight;

    public bool IsValid => _width >= 50 && _height >= 50;
    public bool IsDirty => _x != _originalX || _y != _originalY
                        || _width != _originalWidth || _height != _originalHeight;

    public event Action<bool>? IsValidChanged;
    public event Action<bool>? IsDirtyChanged;

    protected override void OnInitialized()
    {
        if (Node != null)
        {
            _x = Node.X;
            _y = Node.Y;
            _width = Node.Width;
            _height = Node.Height;

            _originalX = _x;
            _originalY = _y;
            _originalWidth = _width;
            _originalHeight = _height;
        }
    }

    public Task SaveAsync(CancellationToken cancellationToken)
    {
        if (Node != null)
        {
            Node.X = _x;
            Node.Y = _y;
            Node.Width = _width;
            Node.Height = _height;

            _originalX = _x;
            _originalY = _y;
            _originalWidth = _width;
            _originalHeight = _height;

            IsDirtyChanged?.Invoke(false);
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        IsValidChanged = null;
        IsDirtyChanged = null;
    }
}
```

#### GridLayoutEditComponent.razor

```razor
@attribute [SubjectComponent(SubjectComponentType.Edit, typeof(GridLayout))]
@implements ISubjectEditComponent
@implements IDisposable

<MudForm>
    <MudNumericField @bind-Value="_rows"
                     Label="Rows"
                     Min="1"
                     Max="20"
                     Class="mb-4" />

    <MudNumericField @bind-Value="_columns"
                     Label="Columns"
                     Min="1"
                     Max="20"
                     Class="mb-4" />

    <MudText Typo="Typo.body2" Class="mud-text-secondary">
        Cells: @(Grid?.Cells?.Count ?? 0)
    </MudText>
</MudForm>

@code {
    [Parameter]
    public IInterceptorSubject? Subject { get; set; }

    [Parameter]
    public bool IsCreating { get; set; }

    private GridLayout? Grid => Subject as GridLayout;

    private int _rows;
    private int _columns;

    private int _originalRows;
    private int _originalColumns;

    public bool IsValid => _rows >= 1 && _columns >= 1;
    public bool IsDirty => _rows != _originalRows || _columns != _originalColumns;

    public event Action<bool>? IsValidChanged;
    public event Action<bool>? IsDirtyChanged;

    protected override void OnInitialized()
    {
        if (Grid != null)
        {
            _rows = Grid.Rows;
            _columns = Grid.Columns;

            _originalRows = _rows;
            _originalColumns = _columns;
        }
    }

    public Task SaveAsync(CancellationToken cancellationToken)
    {
        if (Grid != null)
        {
            Grid.Rows = _rows;
            Grid.Columns = _columns;

            _originalRows = _rows;
            _originalColumns = _columns;

            IsDirtyChanged?.Invoke(false);
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        IsValidChanged = null;
        IsDirtyChanged = null;
    }
}
```

#### GridCellEditComponent.razor

```razor
@attribute [SubjectComponent(SubjectComponentType.Edit, typeof(GridCell))]
@implements ISubjectEditComponent
@implements IDisposable

<MudForm>
    <MudText Typo="Typo.subtitle2" Class="mb-2">Position (leave empty for auto-flow)</MudText>

    <MudGrid>
        <MudItem xs="6">
            <MudNumericField @bind-Value="_row"
                             Label="Row"
                             Min="0"
                             Clearable="true" />
        </MudItem>
        <MudItem xs="6">
            <MudNumericField @bind-Value="_column"
                             Label="Column"
                             Min="0"
                             Clearable="true" />
        </MudItem>
    </MudGrid>

    <MudText Typo="Typo.subtitle2" Class="mt-4 mb-2">Span</MudText>

    <MudGrid>
        <MudItem xs="6">
            <MudNumericField @bind-Value="_rowSpan"
                             Label="Row Span"
                             Min="1"
                             Max="10" />
        </MudItem>
        <MudItem xs="6">
            <MudNumericField @bind-Value="_columnSpan"
                             Label="Column Span"
                             Min="1"
                             Max="10" />
        </MudItem>
    </MudGrid>

    <MudDivider Class="my-4" />

    <MudText Typo="Typo.subtitle2" Class="mb-2">Child Widget</MudText>
    @if (Cell?.Child != null)
    {
        <MudAlert Severity="Severity.Info" Dense="true">
            @Cell.Child.GetType().Name
        </MudAlert>
    }
    else
    {
        <MudAlert Severity="Severity.Warning" Dense="true">
            No child widget assigned
        </MudAlert>
    }
</MudForm>

@code {
    [Parameter]
    public IInterceptorSubject? Subject { get; set; }

    [Parameter]
    public bool IsCreating { get; set; }

    private GridCell? Cell => Subject as GridCell;

    private int? _row;
    private int? _column;
    private int _rowSpan;
    private int _columnSpan;

    private int? _originalRow;
    private int? _originalColumn;
    private int _originalRowSpan;
    private int _originalColumnSpan;

    public bool IsValid => _rowSpan >= 1 && _columnSpan >= 1;
    public bool IsDirty => _row != _originalRow
                        || _column != _originalColumn
                        || _rowSpan != _originalRowSpan
                        || _columnSpan != _originalColumnSpan;

    public event Action<bool>? IsValidChanged;
    public event Action<bool>? IsDirtyChanged;

    protected override void OnInitialized()
    {
        if (Cell != null)
        {
            _row = Cell.Row;
            _column = Cell.Column;
            _rowSpan = Cell.RowSpan;
            _columnSpan = Cell.ColumnSpan;

            _originalRow = _row;
            _originalColumn = _column;
            _originalRowSpan = _rowSpan;
            _originalColumnSpan = _columnSpan;
        }
    }

    public Task SaveAsync(CancellationToken cancellationToken)
    {
        if (Cell != null)
        {
            Cell.Row = _row;
            Cell.Column = _column;
            Cell.RowSpan = _rowSpan;
            Cell.ColumnSpan = _columnSpan;

            _originalRow = _row;
            _originalColumn = _column;
            _originalRowSpan = _rowSpan;
            _originalColumnSpan = _columnSpan;

            IsDirtyChanged?.Invoke(false);
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        IsValidChanged = null;
        IsDirtyChanged = null;
    }
}
```

---

### Important Implementation Notes

#### Excubo.Blazor.Diagrams Considerations

1. **Container sizing**: The diagram requires a non-zero height parent container
   ```html
   <div style="min-height: 300px;">
       <Diagram>...</Diagram>
   </div>
   ```

2. **Node positioning**: Use `X`, `Y`, `XChanged`, `YChanged` for two-way binding
   - Avoid `@bind-X` with many nodes (performance issues reported)
   - Use explicit changed callbacks instead

3. **No built-in resize**: Resize is handled via property editor (simpler, no JS interop)

4. **Node content**: Nodes wrap arbitrary Blazor content via `RenderFragment`

#### State Management

1. **No selection state needed**: All actions are performed via inline buttons on each node/cell

2. **Edit mode**: Received via `[CascadingParameter(Name = "IsEditing")]`

3. **Subject changes**: Direct property assignment triggers change tracking
   ```csharp
   node.X = newX;  // Automatically tracked
   ```

#### SubjectSetupDialog Integration

```csharp
// Show dialog and get created subject
var result = await SubjectSetupDialog.ShowAsync(DialogService);
if (result?.Subject == null) return;

// Create wrapper (node or cell) and assign child
var node = SubjectFactory.CreateSubject<CanvasNode>();
node.Child = result.Subject;
Canvas.Nodes.Add(node);
```

#### Serialization

The existing `ConfigurableSubjectSerializer` handles:
- `List<CanvasNode>` and `List<GridCell>` serialization
- Polymorphic `IInterceptorSubject` child with `$type` discriminator
- `[Configuration]` properties only (state is excluded)

#### Edit Button Handling

Buttons are rendered via the extended `SubjectComponent.ActionButtons` RenderFragment:

| Button | Icon | Source | Action |
|--------|------|--------|--------|
| Edit Node/Cell | Settings (⚙) | Layout widget passes via ActionButtons | Opens dialog with `CanvasNode` or `GridCell` |
| Delete | Delete (🗑) | Layout widget passes via ActionButtons | Shows confirmation, removes node/cell |
| Edit Widget | Edit (✎) | Built-in to SubjectComponent | Opens dialog with `node.Child` or `cell.Child` |

Each layout container also displays an Edit Layout button (⚙) in the top-right corner to edit layout-level settings (rows/columns for grid, snap settings for canvas).

**Benefits of this approach:**
- Reuses SubjectComponent's existing edit overlay styling
- No duplicate "Edit Widget" button (SubjectComponent provides it)
- All buttons in one consistent location (top-right corner)
- Eliminates selection state - all actions are directly on the element

#### Type Registration

Types are **automatically registered** via the `[InterceptorSubject]` attribute. The `SubjectTypeRegistry` scans all assemblies via `TypeProvider` and registers:
- Types with `[InterceptorSubject]` attribute
- Types implementing `IInterceptorSubject`

No manual registration required for CanvasLayout, CanvasNode, GridLayout, or GridCell.

---

## Testing Verification

### Step 1: Build Verification

```bash
cd src/HomeBlaze
dotnet build HomeBlaze.Components/HomeBlaze.Components.csproj
```

Expected: No build errors, source generator creates backing code for subjects.

### Step 2: Type Registration Check

Add a temporary test in the application startup or a test file:

```csharp
// In Program.cs or a test
var typeRegistry = services.GetRequiredService<SubjectTypeRegistry>();
Debug.Assert(typeRegistry.RegisteredTypes.Any(t => t.Name == "CanvasLayout"));
Debug.Assert(typeRegistry.RegisteredTypes.Any(t => t.Name == "GridLayout"));
```

### Step 3: Component Registration Check

```csharp
var componentRegistry = services.GetRequiredService<SubjectComponentRegistry>();
Debug.Assert(componentRegistry.HasComponent(typeof(CanvasLayout), SubjectComponentType.Widget));
Debug.Assert(componentRegistry.HasComponent(typeof(CanvasLayout), SubjectComponentType.Edit));
Debug.Assert(componentRegistry.HasComponent(typeof(GridLayout), SubjectComponentType.Widget));
Debug.Assert(componentRegistry.HasComponent(typeof(GridLayout), SubjectComponentType.Edit));
```

### Step 4: Manual UI Testing

1. **Create subject via wizard:**
   - Open HomeBlaze
   - Navigate to a folder, click "Create"
   - Verify "Canvas" and "Grid" appear in Layouts category
   - Create a GridLayout with 2x2

2. **Add cells to grid:**
   - Enable edit mode
   - Click empty cells to add widgets
   - Verify SubjectSetupDialog opens
   - Create a Motor or other subject

3. **Edit cell properties:**
   - Select a cell
   - Verify property panel shows row/column/span fields
   - Change values and save

4. **Test canvas (once grid works):**
   - Create CanvasLayout
   - Click empty area to add nodes
   - Drag nodes to move
   - Verify snap works when enabled

5. **Test nesting:**
   - In a grid cell, add a CanvasLayout as child
   - Add nodes to the nested canvas

### Step 5: Serialization Test

1. Create a GridLayout with cells
2. Save to storage
3. Reload application
4. Verify layout persists correctly

### Step 6: Markdown Embedding Test

Create a test markdown file:

~~~markdown
# Test Dashboard

```subject(testgrid)
{
  "$type": "HomeBlaze.Components.GridLayout",
  "Rows": 2,
  "Columns": 2,
  "Cells": []
}
```
~~~

Verify the grid renders in the markdown page.
