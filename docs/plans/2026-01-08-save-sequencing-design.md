# Save Sequencing Fix for Subject Creation

## Problem

When creating a new subject via `SubjectSetupDialog`, `SaveAsync` is called BEFORE the subject is assigned to its parent property. This means `TryFindFirstParent<IConfigurationWriter>()` returns null because the parent chain doesn't exist yet.

## Solution

Add a `beforeSave` callback parameter to `SubjectSetupDialog.ShowAsync`. Callers that need to establish a parent chain before saving can pass a callback that:
1. Creates the container (GridCell/CanvasNode)
2. Assigns the subject as child
3. Adds the container to the parent collection

The callback is invoked just before `SaveAsync`, ensuring the parent chain exists when persistence runs.

## API

```csharp
public static Task<CreateSubjectResult?> ShowAsync(
    IDialogService dialogService,
    Action<IInterceptorSubject>? beforeSave = null)
```

## Usage

```csharp
// GridLayoutWidget
await SubjectSetupDialog.ShowAsync(DialogService, subject =>
{
    var cell = SubjectFactory.CreateSubject<GridCell>();
    cell.Row = row;
    cell.Column = column;
    cell.Child = subject;
    Grid?.Cells.Add(cell);
});
```

## Files Modified

- `SubjectSetupDialog.razor` - Add BeforeSave parameter
- `GridLayoutWidget.razor` - Pass callback
- `CanvasLayoutWidget.razor` - Pass callback
