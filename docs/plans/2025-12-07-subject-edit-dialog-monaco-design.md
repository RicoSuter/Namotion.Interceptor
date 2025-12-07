# Subject Edit Dialog with Monaco Editor

## Overview

Enhance the subject editing experience by:
1. Unifying the edit dialog to use registered Edit components via `SubjectComponentRegistry`
2. Adding Monaco editor support for file-based subjects (Markdown, JSON)
3. Allowing Edit components to specify their preferred dialog size

## Changes

### 1. Extend ISubjectEditComponent Interface

**File:** `HomeBlaze.Abstractions/Components/ISubjectEditComponent.cs`

Add `PreferredDialogSize` property with default implementation:

```csharp
using MudBlazor;

public interface ISubjectEditComponent : ISubjectComponent
{
    bool IsValid { get; }
    bool IsDirty { get; }
    event Action<bool>? IsValidChanged;
    event Action<bool>? IsDirtyChanged;
    Task SaveAsync();

    /// <summary>
    /// Preferred dialog size for this edit component.
    /// </summary>
    MaxWidth PreferredDialogSize => MaxWidth.Small;
}
```

### 2. Rename SubjectConfigurationDialog → SubjectEditDialog

**File:** `HomeBlaze/Components/SubjectConfigurationDialog.razor` → `HomeBlaze/Components/SubjectEditDialog.razor`

Update the dialog to:
1. Inject `SubjectComponentRegistry`
2. Look up `SubjectComponentType.Edit` for the subject type
3. If found: render via `DynamicComponent`, wire up `ISubjectEditComponent` events, use component's `PreferredDialogSize`
4. If not found: render auto-generated property editors (current behavior), use `MaxWidth.Small`

The dialog options are passed by the caller, but the dialog can communicate preferred size back.

### 3. Rename MotorEditView → MotorEditComponent

**File:** `HomeBlaze/Components/Views/MotorEditView.razor` → `HomeBlaze/Components/SubjectComponents/MotorEditComponent.razor`

- Move to SubjectComponents folder for consistency
- Rename class/file
- Update attribute to match new location

### 4. Create MarkdownFileEditComponent

**File:** `HomeBlaze/Components/SubjectComponents/MarkdownFileEditComponent.razor`

```razor
@using HomeBlaze.Abstractions
@using HomeBlaze.Abstractions.Attributes
@using HomeBlaze.Abstractions.Components
@using HomeBlaze.Storage.Files
@using BlazorMonaco
@using BlazorMonaco.Editor
@using Namotion.Interceptor

@attribute [SubjectComponent(SubjectComponentType.Edit, typeof(MarkdownFile))]
@implements ISubjectEditComponent

<div style="height: 500px;">
    <StandaloneCodeEditor @ref="_editor"
        Id="markdown-editor"
        ConstructionOptions="GetEditorOptions" />
</div>

@code {
    [Parameter]
    public IInterceptorSubject? Subject { get; set; }

    private MarkdownFile? File => Subject as MarkdownFile;
    private StandaloneCodeEditor? _editor;
    private string _originalContent = string.Empty;
    private bool _isLoading = true;

    public bool IsValid => true;
    public bool IsDirty { get; private set; }
    public MaxWidth PreferredDialogSize => MaxWidth.Large;

    public event Action<bool>? IsValidChanged;
    public event Action<bool>? IsDirtyChanged;

    private StandaloneEditorConstructionOptions GetEditorOptions(StandaloneCodeEditor editor)
    {
        return new StandaloneEditorConstructionOptions
        {
            AutomaticLayout = true,
            Language = "markdown",
            Value = _originalContent,
            WordWrap = "on"
        };
    }

    protected override async Task OnParametersSetAsync()
    {
        if (File != null && _isLoading)
        {
            _originalContent = await File.GetContentAsync();
            _isLoading = false;
        }
    }

    public async Task SaveAsync()
    {
        if (File == null || _editor == null) return;

        var content = await _editor.GetValue();
        await File.SetContentAsync(content);
        _originalContent = content;
        IsDirty = false;
        IsDirtyChanged?.Invoke(false);
    }
}
```

### 5. Create JsonFileEditComponent

**File:** `HomeBlaze/Components/SubjectComponents/JsonFileEditComponent.razor`

Same structure as MarkdownFileEditComponent but with:
- `Language = "json"`
- Works with `JsonFile.Content` property directly

```razor
@using HomeBlaze.Abstractions
@using HomeBlaze.Abstractions.Attributes
@using HomeBlaze.Abstractions.Components
@using HomeBlaze.Storage.Files
@using BlazorMonaco
@using BlazorMonaco.Editor
@using Namotion.Interceptor

@attribute [SubjectComponent(SubjectComponentType.Edit, typeof(JsonFile))]
@implements ISubjectEditComponent

<div style="height: 500px;">
    <StandaloneCodeEditor @ref="_editor"
        Id="json-editor"
        ConstructionOptions="GetEditorOptions" />
</div>

@code {
    [Parameter]
    public IInterceptorSubject? Subject { get; set; }

    private JsonFile? File => Subject as JsonFile;
    private StandaloneCodeEditor? _editor;
    private string _originalContent = string.Empty;

    public bool IsValid => true;
    public bool IsDirty { get; private set; }
    public MaxWidth PreferredDialogSize => MaxWidth.Large;

    public event Action<bool>? IsValidChanged;
    public event Action<bool>? IsDirtyChanged;

    private StandaloneEditorConstructionOptions GetEditorOptions(StandaloneCodeEditor editor)
    {
        return new StandaloneEditorConstructionOptions
        {
            AutomaticLayout = true,
            Language = "json",
            Value = _originalContent
        };
    }

    protected override void OnParametersSet()
    {
        if (File != null)
        {
            _originalContent = File.Content;
        }
    }

    public async Task SaveAsync()
    {
        if (File == null || _editor == null) return;

        var content = await _editor.GetValue();
        File.Content = content;
        await File.SaveAsync();
        _originalContent = content;
        IsDirty = false;
        IsDirtyChanged?.Invoke(false);
    }
}
```

### 6. Update SubjectPropertyPanel

**File:** `HomeBlaze/Components/SubjectPropertyPanel.razor`

- Change `SubjectConfigurationDialog` → `SubjectEditDialog`
- Update `OpenConfigurationDialog` → `OpenEditDialog`

### 7. Update Pages.razor

**File:** `HomeBlaze/Components/Pages/Pages.razor`

Add Edit button to toolbar:

```razor
@inject SubjectComponentRegistry ComponentRegistry
@inject IDialogService DialogService

<MudStack Row="true" Class="mb-2">
    <MudText Typo="Typo.h4">@_title</MudText>
    <MudSpacer />
    <MudIconButton OnClick="@OpenEditDialog"
                   Icon="@Icons.Material.Filled.Edit"
                   Variant="Variant.Filled"
                   Color="Color.Primary"
                   Size="Size.Small" />
</MudStack>

@code {
    private async Task OpenEditDialog()
    {
        if (_subject == null) return;

        var parameters = new DialogParameters<SubjectEditDialog>
        {
            { x => x.Subject, _subject }
        };

        // Check if custom edit component exists and get its preferred size
        var editRegistration = ComponentRegistry.GetComponent(
            _subject.GetType(),
            SubjectComponentType.Edit);

        var maxWidth = MaxWidth.Small;
        if (editRegistration != null)
        {
            // Will be determined by dialog after component renders
            maxWidth = MaxWidth.Large; // Default larger for custom components
        }

        var options = new DialogOptions
        {
            MaxWidth = maxWidth,
            FullWidth = true,
            CloseButton = true
        };

        await DialogService.ShowAsync<SubjectEditDialog>($"Edit {_title}", parameters, options);
    }
}
```

### 8. Add BlazorMonaco Package

**File:** `HomeBlaze/HomeBlaze.csproj`

```xml
<PackageReference Include="BlazorMonaco" Version="3.4.0" />
```

**File:** `HomeBlaze/Components/App.razor`

Add before the Blazor script tags:

```html
<script src="_content/BlazorMonaco/jsInterop.js"></script>
<script src="_content/BlazorMonaco/lib/monaco-editor/min/vs/loader.js"></script>
<script src="_content/BlazorMonaco/lib/monaco-editor/min/vs/editor/editor.main.js"></script>
```

**File:** `HomeBlaze/Components/_Imports.razor`

Add:

```razor
@using BlazorMonaco
@using BlazorMonaco.Editor
```

## Files to Create

- `HomeBlaze/Components/SubjectComponents/MarkdownFileEditComponent.razor`
- `HomeBlaze/Components/SubjectComponents/JsonFileEditComponent.razor`

## Files to Modify

- `HomeBlaze.Abstractions/Components/ISubjectEditComponent.cs` - Add PreferredDialogSize
- `HomeBlaze/Components/SubjectConfigurationDialog.razor` → Rename to `SubjectEditDialog.razor`, add registry lookup
- `HomeBlaze/Components/Views/MotorEditView.razor` → Move/rename to `SubjectComponents/MotorEditComponent.razor`
- `HomeBlaze/Components/SubjectPropertyPanel.razor` - Use SubjectEditDialog
- `HomeBlaze/Components/Pages/Pages.razor` - Add Edit button
- `HomeBlaze/HomeBlaze.csproj` - Add BlazorMonaco package
- `HomeBlaze/Components/App.razor` - Add Monaco script references
- `HomeBlaze/Components/_Imports.razor` - Add BlazorMonaco usings

## Dependencies

- BlazorMonaco 3.4.0 (NuGet)

## Notes

- BlazorMonaco requires interactive render mode (not static SSR)
- Monaco editor height must be set explicitly via CSS
- The dialog determines size after the edit component is instantiated and queried for PreferredDialogSize
