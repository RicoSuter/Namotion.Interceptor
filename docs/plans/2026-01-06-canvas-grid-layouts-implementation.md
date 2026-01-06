# Canvas and Grid Layouts Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add CanvasLayout (free-form drag-and-drop) and GridLayout (rows/columns) components to HomeBlaze for organizing widgets in markdown pages.

**Architecture:** Create four subject classes (CanvasLayout, CanvasNode, GridLayout, GridCell) with widget and edit components. Extend SubjectComponent with ActionButtons parameter for layout-specific buttons. Use Excubo.Blazor.Diagrams for canvas drag-and-drop, CSS Grid for grid layout.

**Tech Stack:** Excubo.Blazor.Diagrams 4.1.*, MudBlazor 8.*, Blazor/.NET 9.0, C# 13 partial properties

**Design Document:** `docs/plans/2026-01-06-canvas-grid-layouts-design.md` (detailed specifications)

---

## Task 1: Add Excubo.Blazor.Diagrams Package Dependency

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.Components/HomeBlaze.Components.csproj:20-23`
- Modify: `src/HomeBlaze/HomeBlaze.Components/_Imports.razor:15`

**Step 1: Add package reference to csproj**

Edit `src/HomeBlaze/HomeBlaze.Components/HomeBlaze.Components.csproj`, add inside `<ItemGroup>`:

```xml
<PackageReference Include="Excubo.Blazor.Diagrams" Version="4.1.*" />
```

**Step 2: Add using directive to _Imports.razor**

Edit `src/HomeBlaze/HomeBlaze.Components/_Imports.razor`, add at end:

```razor
@using Excubo.Blazor.Diagrams
```

**Step 3: Verify build succeeds**

Run: `dotnet build src/HomeBlaze/HomeBlaze.Components/HomeBlaze.Components.csproj`
Expected: Build succeeded with no errors

**Step 4: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Components/HomeBlaze.Components.csproj src/HomeBlaze/HomeBlaze.Components/_Imports.razor
git commit -m "feat: add Excubo.Blazor.Diagrams package for canvas layouts"
```

---

## Task 2: Extend SubjectComponent with ActionButtons Parameter

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.Components/SubjectComponent.razor`

**Step 1: Add ActionButtons parameter and update overlay rendering**

Replace the entire content of `src/HomeBlaze/HomeBlaze.Components/SubjectComponent.razor`:

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

                    @* Additional action buttons from parent *@
                    @ActionButtons
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
    /// Rendered after the built-in edit button.
    /// </summary>
    [Parameter]
    public RenderFragment? ActionButtons { get; set; }

    /// <summary>
    /// Additional parameters to pass to the rendered component (e.g., IsCreating).
    /// </summary>
    [Parameter]
    public Dictionary<string, object?>? AdditionalParameters { get; set; }

    [CascadingParameter(Name = "IsEditing")]
    public bool IsEditing { get; set; }

    /// <summary>
    /// Two-way binding for accessing the rendered component instance.
    /// Useful for edit panels that need to access ISubjectEditComponent methods.
    /// </summary>
    [Parameter]
    public ISubjectComponent? ComponentInstance { get; set; }

    [Parameter]
    public EventCallback<ISubjectComponent?> ComponentInstanceChanged { get; set; }

    private bool HasEditComponent =>
        Subject != null &&
        ComponentRegistry.HasComponent(Subject.GetType(), SubjectComponentType.Edit);

    private Dictionary<string, object?> GetComponentParameters()
    {
        var parameters = new Dictionary<string, object?> { ["Subject"] = Subject };

        if (AdditionalParameters != null)
        {
            foreach (var kvp in AdditionalParameters)
            {
                parameters[kvp.Key] = kvp.Value;
            }
        }

        return parameters;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        var instance = _dynamicComponent?.Instance as ISubjectComponent;
        if (instance != ComponentInstance)
        {
            ComponentInstance = instance;
            await ComponentInstanceChanged.InvokeAsync(instance);
        }
    }

    private async Task OpenEditDialog()
    {
        if (Subject == null)
            return;

        // Use dynamic type lookup for SubjectEditDialog since it's in HomeBlaze.Host
        var dialogType = System.Type.GetType("HomeBlaze.Host.Components.Dialogs.SubjectEditDialog, HomeBlaze.Host");
        if (dialogType == null)
            return;

        var parameters = new DialogParameters<object>
        {
            { "Subject", Subject }
        };

        var options = new DialogOptions
        {
            MaxWidth = MaxWidth.Small,
            FullWidth = true
        };

        await DialogService.ShowAsync(dialogType, "Edit", parameters, options);
    }
}
```

**Step 2: Verify build succeeds**

Run: `dotnet build src/HomeBlaze/HomeBlaze.Components/HomeBlaze.Components.csproj`
Expected: Build succeeded with no errors

**Step 3: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Components/SubjectComponent.razor
git commit -m "feat: add ActionButtons parameter to SubjectComponent for layout edit buttons"
```

---

## Task 3: Create CanvasLayout and CanvasNode Subject Classes

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Components/CanvasLayout.cs`
- Create: `src/HomeBlaze/HomeBlaze.Components/CanvasNode.cs`

**Step 1: Create CanvasLayout.cs**

Create `src/HomeBlaze/HomeBlaze.Components/CanvasLayout.cs`:

```csharp
using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Components;

/// <summary>
/// Free-form canvas layout with draggable nodes at arbitrary positions.
/// </summary>
[Category("Layouts")]
[Description("Free-form canvas layout with draggable widgets")]
[InterceptorSubject]
public partial class CanvasLayout : IConfigurableSubject, ITitleProvider
{
    /// <summary>
    /// Optional minimum height in pixels. Defaults to 400.
    /// </summary>
    [Configuration]
    public partial int? MinHeight { get; set; }

    /// <summary>
    /// Enable snapping node positions to grid.
    /// </summary>
    [Configuration]
    public partial bool IsSnapToGridEnabled { get; set; }

    /// <summary>
    /// Grid size in pixels for snap-to-grid. Default: 100.
    /// </summary>
    [Configuration]
    public partial int GridSize { get; set; }

    /// <summary>
    /// Collection of nodes positioned on the canvas.
    /// </summary>
    [Configuration]
    public partial List<CanvasNode> Nodes { get; set; }

    public string? Title => "Canvas";

    public CanvasLayout()
    {
        GridSize = 100;
        Nodes = new List<CanvasNode>();
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
```

**Step 2: Create CanvasNode.cs**

Create `src/HomeBlaze/HomeBlaze.Components/CanvasNode.cs`:

```csharp
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Components;

/// <summary>
/// A positioned node on a canvas containing a child subject.
/// </summary>
[InterceptorSubject]
public partial class CanvasNode : IConfigurableSubject
{
    /// <summary>
    /// X position in pixels from left edge.
    /// </summary>
    [Configuration]
    public partial int X { get; set; }

    /// <summary>
    /// Y position in pixels from top edge.
    /// </summary>
    [Configuration]
    public partial int Y { get; set; }

    /// <summary>
    /// Width in pixels.
    /// </summary>
    [Configuration]
    public partial int Width { get; set; }

    /// <summary>
    /// Height in pixels.
    /// </summary>
    [Configuration]
    public partial int Height { get; set; }

    /// <summary>
    /// The child subject to render in this node.
    /// </summary>
    [Configuration]
    public partial IInterceptorSubject? Child { get; set; }

    public CanvasNode()
    {
        Width = 200;
        Height = 150;
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
```

**Step 3: Verify build succeeds**

Run: `dotnet build src/HomeBlaze/HomeBlaze.Components/HomeBlaze.Components.csproj`
Expected: Build succeeded with no errors, source generator creates backing fields

**Step 4: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Components/CanvasLayout.cs src/HomeBlaze/HomeBlaze.Components/CanvasNode.cs
git commit -m "feat: add CanvasLayout and CanvasNode subject classes"
```

---

## Task 4: Create GridLayout and GridCell Subject Classes

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Components/GridLayout.cs`
- Create: `src/HomeBlaze/HomeBlaze.Components/GridCell.cs`

**Step 1: Create GridLayout.cs**

Create `src/HomeBlaze/HomeBlaze.Components/GridLayout.cs`:

```csharp
using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Components;

/// <summary>
/// Grid layout with configurable rows and columns.
/// </summary>
[Category("Layouts")]
[Description("Grid layout with configurable rows and columns")]
[InterceptorSubject]
public partial class GridLayout : IConfigurableSubject, ITitleProvider
{
    /// <summary>
    /// Number of rows in the grid.
    /// </summary>
    [Configuration]
    public partial int Rows { get; set; }

    /// <summary>
    /// Number of columns in the grid.
    /// </summary>
    [Configuration]
    public partial int Columns { get; set; }

    /// <summary>
    /// Collection of cells in the grid.
    /// </summary>
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

**Step 2: Create GridCell.cs**

Create `src/HomeBlaze/HomeBlaze.Components/GridCell.cs`:

```csharp
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Components;

/// <summary>
/// A cell in a grid layout containing a child subject.
/// </summary>
[InterceptorSubject]
public partial class GridCell : IConfigurableSubject
{
    /// <summary>
    /// Row position (0-indexed). Null for auto-flow.
    /// </summary>
    [Configuration]
    public partial int? Row { get; set; }

    /// <summary>
    /// Column position (0-indexed). Null for auto-flow.
    /// </summary>
    [Configuration]
    public partial int? Column { get; set; }

    /// <summary>
    /// Number of rows this cell spans. Default: 1.
    /// </summary>
    [Configuration]
    public partial int RowSpan { get; set; }

    /// <summary>
    /// Number of columns this cell spans. Default: 1.
    /// </summary>
    [Configuration]
    public partial int ColumnSpan { get; set; }

    /// <summary>
    /// The child subject to render in this cell.
    /// </summary>
    [Configuration]
    public partial IInterceptorSubject? Child { get; set; }

    public GridCell()
    {
        RowSpan = 1;
        ColumnSpan = 1;
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
```

**Step 3: Verify build succeeds**

Run: `dotnet build src/HomeBlaze/HomeBlaze.Components/HomeBlaze.Components.csproj`
Expected: Build succeeded with no errors

**Step 4: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Components/GridLayout.cs src/HomeBlaze/HomeBlaze.Components/GridCell.cs
git commit -m "feat: add GridLayout and GridCell subject classes"
```

---

## Task 5: Create CanvasLayoutWidget Component

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Components/Components/CanvasLayoutWidget.razor`

**Step 1: Create CanvasLayoutWidget.razor**

Create `src/HomeBlaze/HomeBlaze.Components/Components/CanvasLayoutWidget.razor`:

```razor
@using Excubo.Blazor.Diagrams
@using HomeBlaze.Components.Dialogs

@attribute [SubjectComponent(SubjectComponentType.Widget, typeof(CanvasLayout))]
@implements ISubjectComponent

@inject IDialogService DialogService
@inject SubjectFactory SubjectFactory
@inject SubjectComponentRegistry ComponentRegistry

<div class="canvas-layout-container"
     style="position: relative; width: 100%; min-height: @(Canvas?.MinHeight ?? 400)px;"
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
                          Draggable="@IsEditing"
                          XChanged="@(x => OnNodePositionChanged(node, (int)x, node.Y))"
                          YChanged="@(y => OnNodePositionChanged(node, node.X, (int)y))">
                        <div class="canvas-node @(IsEditing ? "editing" : "")"
                             style="width: @(node.Width)px; height: @(node.Height)px;">
                            <div class="canvas-node-content">
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

    /* In edit mode: disable widget interaction, show drag cursor */
    .canvas-node.editing {
        cursor: move;
        border: 2px dashed var(--mud-palette-primary);
    }

    .canvas-node.editing .canvas-node-content {
        pointer-events: none;  /* Disable all widget interaction */
    }

    .canvas-node-content {
        width: 100%;
        height: 100%;
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
        if (Canvas?.IsSnapToGridEnabled == true)
        {
            var snapSize = Canvas.GridSize > 0 ? Canvas.GridSize : 100;
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
        if (Canvas?.IsSnapToGridEnabled == true)
        {
            var snapSize = Canvas.GridSize > 0 ? Canvas.GridSize : 100;
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

**Step 2: Verify build succeeds**

Run: `dotnet build src/HomeBlaze/HomeBlaze.Components/HomeBlaze.Components.csproj`
Expected: Build succeeded with no errors

**Step 3: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Components/Components/CanvasLayoutWidget.razor
git commit -m "feat: add CanvasLayoutWidget component with drag-and-drop support"
```

---

## Task 6: Create GridLayoutWidget Component

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Components/Components/GridLayoutWidget.razor`

**Step 1: Create GridLayoutWidget.razor**

Create `src/HomeBlaze/HomeBlaze.Components/Components/GridLayoutWidget.razor`:

```razor
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
            min-height: 400px;
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
            <div class="grid-cell @(IsEditing ? "editing" : "")" style="@GetCellStyle(cell)">
                <div class="grid-cell-content">
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

    /* In edit mode: disable widget interaction */
    .grid-cell.editing {
        border: 2px dashed var(--mud-palette-primary);
    }

    .grid-cell.editing .grid-cell-content {
        pointer-events: none;  /* Disable all widget interaction */
    }

    .grid-cell-content {
        width: 100%;
        height: 100%;
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

**Step 2: Verify build succeeds**

Run: `dotnet build src/HomeBlaze/HomeBlaze.Components/HomeBlaze.Components.csproj`
Expected: Build succeeded with no errors

**Step 3: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Components/Components/GridLayoutWidget.razor
git commit -m "feat: add GridLayoutWidget component with CSS Grid layout"
```

---

## Task 7: Create CanvasLayout Edit Component

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Components/Components/CanvasLayoutEditComponent.razor`

**Step 1: Create CanvasLayoutEditComponent.razor**

Create `src/HomeBlaze/HomeBlaze.Components/Components/CanvasLayoutEditComponent.razor`:

```razor
@attribute [SubjectComponent(SubjectComponentType.Edit, typeof(CanvasLayout))]
@implements ISubjectEditComponent
@implements IDisposable

<MudForm>
    <MudNumericField @bind-Value="_minHeight"
                     Label="Minimum Height (pixels)"
                     HelperText="Optional. Leave empty for default (400px)."
                     Class="mb-4" />

    <MudSwitch @bind-Value="_snapEnabled"
               Label="Snap to Grid"
               Color="Color.Primary"
               Class="mb-4" />

    @if (_snapEnabled)
    {
        <MudNumericField @bind-Value="_gridSize"
                         Label="Grid Size (pixels)"
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
    private int _gridSize;

    private int? _originalMinHeight;
    private bool _originalSnapEnabled;
    private int _originalGridSize;

    public bool IsValid => true;
    public bool IsDirty => _minHeight != _originalMinHeight
                        || _snapEnabled != _originalSnapEnabled
                        || _gridSize != _originalGridSize;

    public event Action<bool>? IsValidChanged;
    public event Action<bool>? IsDirtyChanged;

    protected override void OnInitialized()
    {
        if (Canvas != null)
        {
            _minHeight = Canvas.MinHeight;
            _snapEnabled = Canvas.IsSnapToGridEnabled;
            _gridSize = Canvas.GridSize;

            _originalMinHeight = _minHeight;
            _originalSnapEnabled = _snapEnabled;
            _originalGridSize = _gridSize;
        }
    }

    public Task SaveAsync(CancellationToken cancellationToken)
    {
        if (Canvas != null)
        {
            Canvas.MinHeight = _minHeight;
            Canvas.IsSnapToGridEnabled = _snapEnabled;
            Canvas.GridSize = _gridSize;

            _originalMinHeight = _minHeight;
            _originalSnapEnabled = _snapEnabled;
            _originalGridSize = _gridSize;

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

**Step 2: Verify build succeeds**

Run: `dotnet build src/HomeBlaze/HomeBlaze.Components/HomeBlaze.Components.csproj`
Expected: Build succeeded with no errors

**Step 3: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Components/Components/CanvasLayoutEditComponent.razor
git commit -m "feat: add CanvasLayoutEditComponent for editing canvas properties"
```

---

## Task 8: Create CanvasNode Edit Component

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Components/Components/CanvasNodeEditComponent.razor`

**Step 1: Create CanvasNodeEditComponent.razor**

Create `src/HomeBlaze/HomeBlaze.Components/Components/CanvasNodeEditComponent.razor`:

```razor
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

**Step 2: Verify build succeeds**

Run: `dotnet build src/HomeBlaze/HomeBlaze.Components/HomeBlaze.Components.csproj`
Expected: Build succeeded with no errors

**Step 3: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Components/Components/CanvasNodeEditComponent.razor
git commit -m "feat: add CanvasNodeEditComponent for editing node position and size"
```

---

## Task 9: Create GridLayout Edit Component

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Components/Components/GridLayoutEditComponent.razor`

**Step 1: Create GridLayoutEditComponent.razor**

Create `src/HomeBlaze/HomeBlaze.Components/Components/GridLayoutEditComponent.razor`:

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

**Step 2: Verify build succeeds**

Run: `dotnet build src/HomeBlaze/HomeBlaze.Components/HomeBlaze.Components.csproj`
Expected: Build succeeded with no errors

**Step 3: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Components/Components/GridLayoutEditComponent.razor
git commit -m "feat: add GridLayoutEditComponent for editing grid rows and columns"
```

---

## Task 10: Create GridCell Edit Component

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Components/Components/GridCellEditComponent.razor`

**Step 1: Create GridCellEditComponent.razor**

Create `src/HomeBlaze/HomeBlaze.Components/Components/GridCellEditComponent.razor`:

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

**Step 2: Verify build succeeds**

Run: `dotnet build src/HomeBlaze/HomeBlaze.Components/HomeBlaze.Components.csproj`
Expected: Build succeeded with no errors

**Step 3: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Components/Components/GridCellEditComponent.razor
git commit -m "feat: add GridCellEditComponent for editing cell position and spans"
```

---

## Task 11: Verify Full Build and Run All Tests

**Step 1: Build entire solution**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: Build succeeded with no errors

**Step 2: Run all HomeBlaze tests**

Run: `dotnet test src/Namotion.Interceptor.slnx --filter "FullyQualifiedName~HomeBlaze"`
Expected: All tests pass

**Step 3: Final commit (if any fixes needed)**

If any fixes were made:
```bash
git add -A
git commit -m "fix: address build/test issues in layout components"
```

---

## Task 12: Manual Verification

**Step 1: Start HomeBlaze application**

Run: `dotnet run --project src/HomeBlaze/HomeBlaze.Host`

**Step 2: Verify type registration**

1. Navigate to a folder in the storage hierarchy
2. Click "Create" button
3. Verify "Layouts" category appears with:
   - "Canvas" - Free-form canvas layout with draggable widgets
   - "Grid" - Grid layout with configurable rows and columns

**Step 3: Test Grid Layout**

1. Create a new GridLayout (name: "test-grid")
2. Set rows=2, columns=2, click Create
3. Enable edit mode
4. Verify empty cells show [+] icon
5. Click empty cell, add a subject (Motor, etc.)
6. Verify action buttons appear: [Edit Widget] [Edit Cell] [Delete]
7. Click Edit Cell, modify row span, save
8. Verify cell spans correctly

**Step 4: Test Canvas Layout**

1. Create a new CanvasLayout (name: "test-canvas")
2. Set snap to grid = true, grid size = 100
3. Enable edit mode
4. Click on canvas area to add node
5. Verify node appears at click position (snapped)
6. Drag node to new position
7. Verify snap-to-grid works
8. Verify action buttons work

**Step 5: Test Serialization**

1. Create layouts with content
2. Restart application
3. Verify layouts persist correctly

---

## Summary of Files Created/Modified

| Action | File Path |
|--------|-----------|
| Modify | `src/HomeBlaze/HomeBlaze.Components/HomeBlaze.Components.csproj` |
| Modify | `src/HomeBlaze/HomeBlaze.Components/_Imports.razor` |
| Modify | `src/HomeBlaze/HomeBlaze.Components/SubjectComponent.razor` |
| Create | `src/HomeBlaze/HomeBlaze.Components/CanvasLayout.cs` |
| Create | `src/HomeBlaze/HomeBlaze.Components/CanvasNode.cs` |
| Create | `src/HomeBlaze/HomeBlaze.Components/GridLayout.cs` |
| Create | `src/HomeBlaze/HomeBlaze.Components/GridCell.cs` |
| Create | `src/HomeBlaze/HomeBlaze.Components/Components/CanvasLayoutWidget.razor` |
| Create | `src/HomeBlaze/HomeBlaze.Components/Components/GridLayoutWidget.razor` |
| Create | `src/HomeBlaze/HomeBlaze.Components/Components/CanvasLayoutEditComponent.razor` |
| Create | `src/HomeBlaze/HomeBlaze.Components/Components/CanvasNodeEditComponent.razor` |
| Create | `src/HomeBlaze/HomeBlaze.Components/Components/GridLayoutEditComponent.razor` |
| Create | `src/HomeBlaze/HomeBlaze.Components/Components/GridCellEditComponent.razor` |
