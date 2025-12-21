# Create Subject Wizard - Design Document

## Overview

Add the ability to create new subjects (JSON files) directly from the HomeBlaze UI. Users navigate to a folder or storage, click a "Create" operation, select a subject type from a wizard, configure it, and the JSON file is saved automatically.

## User Flow

1. Navigate to folder/storage in browser pane
2. Click "Create" operation button in the operations list
3. Wizard dialog opens:
   - **Step 1:** Type picker - expansion panels grouped by `[Category]`, cards in grid showing type name, icon, and `[Description]`
   - **Step 2:** Setup/Edit component with `IsCreating=true`
4. Click "Create" (bottom right) or "Cancel" (bottom left)
5. On create: subject added to folder, JSON file saved to storage

## Components

### 1. SubjectComponentType Extension

Add `Setup` to the enum:

```csharp
public enum SubjectComponentType
{
    Page,
    Edit,
    Setup,  // New
    Widget
}
```

### 2. IsCreating Parameter

Add `IsCreating` to the `ISubjectEditComponent` interface:

```csharp
public interface ISubjectEditComponent : ISubjectComponent
{
    // ... existing members ...

    /// <summary>
    /// Gets or sets whether the component is in creation mode (new subject) vs edit mode.
    /// </summary>
    bool IsCreating { get; set; }
}
```

Edit components receive this as a parameter:

```csharp
[Parameter]
public bool IsCreating { get; set; }
```

The dialog passes `true` during creation, `false` during editing.

### 3. Component Fallback Chain

For both edit and create:
1. Look for dedicated component (`Setup` for create, `Edit` for edit)
2. If none found, use `GenericEditComponent`

**Enhance `SubjectComponentRegistry.GetComponent()` for inheritance:**

```csharp
public SubjectComponentRegistration? GetComponent(Type subjectType, SubjectComponentType type, string? name = null)
{
    // Try exact match first
    var exact = _components.Value.GetValueOrDefault((subjectType, type, name));
    if (exact != null)
        return exact;

    // Try base classes
    var baseType = subjectType.BaseType;
    while (baseType != null && baseType != typeof(object))
    {
        var baseMatch = _components.Value.GetValueOrDefault((baseType, type, name));
        if (baseMatch != null)
            return baseMatch;
        baseType = baseType.BaseType;
    }

    // Try interfaces (for IConfigurableSubject fallback)
    foreach (var iface in subjectType.GetInterfaces())
    {
        var ifaceMatch = _components.Value.GetValueOrDefault((iface, type, name));
        if (ifaceMatch != null)
            return ifaceMatch;
    }

    return null;
}
```

### 4. GenericEditComponent

New component that auto-generates a form from `[Configuration]` properties:

```csharp
[SubjectComponent(SubjectComponentType.Edit, typeof(IConfigurableSubject))]
public partial class GenericEditComponent
{
    [Parameter] public IInterceptorSubject Subject { get; set; }
    [Parameter] public bool IsCreating { get; set; }

    // Renders form based on GetConfigurationProperties()
}
```

### 5. CreateSubjectWizard

New dialog component using MudStepper:

```csharp
public partial class CreateSubjectWizard
{
    public static Task<IInterceptorSubject?> ShowAsync(IDialogService dialogService)
    {
        // Opens wizard, returns created subject or null if cancelled
    }
}
```

**Dialog Header:**
- Dynamic title showing current step: "Step 1: Select Type" or "Step 2: Configure {TypeName}"

**Dialog Actions (always visible at bottom, both steps):**
- Cancel (bottom left) - closes dialog, returns null
- Create (bottom right) - disabled on step 1, enabled on step 2

**Step 1 - Select Type:**
```
┌─────────────────────────────────────────────────────────────────┐
│ Step 1: Select Type                                         [X] │
├─────────────────────────────────────────────────────────────────┤
│  ● Step 1: Select Type    ○ Step 2: Configure                   │
│  ───────────────────────────────────────────────────────────    │
│                                                                 │
│  ▼ Samples                                                      │
│  ┌─────────────────────┐  ┌─────────────────────┐              │
│  │ Motor               │  │ Sensor              │              │
│  │ Simulated motor     │  │ Temperature sensor  │              │
│  │ with speed control  │  │ with intervals      │              │
│  └─────────────────────┘  └─────────────────────┘              │
│                                                                 │
│  ▼ Servers                                                      │
│  ┌─────────────────────┐                                       │
│  │ OpcUaServer         │                                       │
│  │ Exposes subjects    │                                       │
│  │ via OPC UA protocol │                                       │
│  └─────────────────────┘                                       │
├─────────────────────────────────────────────────────────────────┤
│ [Cancel]                                     [Create] (disabled) │
└─────────────────────────────────────────────────────────────────┘
```
- `MudExpansionPanels` with `MultiExpansion="true"` (all expanded by default)
- Grouped by `[Category]` attribute
- Inside each panel: `MudGrid` with `MudCard` for each type
- Cards display: Type name, `[Description]` (no icon for MVP)
- Vertical scrollbar within dialog content area
- Click card advances to step 2

**Step 2 - Configure:**
```
┌─────────────────────────────────────────────────────────────────┐
│ Step 2: Create Motor                                        [X] │
├─────────────────────────────────────────────────────────────────┤
│  ✓ Step 1: Select Type    ● Step 2: Configure                   │
│  ───────────────────────────────────────────────────────────    │
│                                                                 │
│  Name *                                                         │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │ workshop-motor                                          │   │
│  └─────────────────────────────────────────────────────────┘   │
│  📄 workshop-motor.json                                         │
│                                                                 │
│  ─────────────────────────────────────────────────────────────  │
│                                                                 │
│  [Edit/Setup Component with IsCreating=true]                    │
│                                                                 │
├─────────────────────────────────────────────────────────────────┤
│ [Cancel]                              [← Back]        [Create]  │
└─────────────────────────────────────────────────────────────────┘
```
- Name input at top with filename preview helper text
- Divider separates name from subject configuration
- Loads Setup component, or falls back to Edit, or GenericEditComponent
- Passes `IsCreating=true` to component
- Back button returns to step 1
- Create enabled when name valid AND component reports `IsValid`

### 6. SubjectCreationHelper

Shared helper for folder and storage operations:

```csharp
public static class SubjectCreationHelper
{
    public static async Task<IInterceptorSubject?> CreateSubjectAsync(
        IDialogService dialogService,
        IStorageContainer container,
        string relativePath)
    {
        var result = await CreateSubjectWizard.ShowAsync(dialogService);
        if (result == null)
            return null;

        var fileName = $"{result.Name}.json";
        var fullPath = Path.Combine(relativePath, fileName);

        await container.AddSubjectAsync(fullPath, result.Subject, CancellationToken.None);
        return result.Subject;
    }
}
```

### 7. SubjectFactory (New Class)

Extract creation logic from serializer:

```csharp
public class SubjectFactory
{
    private readonly IServiceProvider _serviceProvider;

    public SubjectFactory(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public IInterceptorSubject CreateSubject(Type type)
    {
        var instance = ActivatorUtilities.CreateInstance(_serviceProvider, type);
        if (instance is IInterceptorSubject subject)
        {
            return subject;
        }

        throw new InvalidOperationException(
            $"Type {type.FullName} must implement IInterceptorSubject.");
    }
}
```

### 8. ConfigurableSubjectSerializer Update

Use `SubjectFactory` internally:

```csharp
public class ConfigurableSubjectSerializer
{
    private readonly SubjectTypeRegistry _typeRegistry;
    private readonly SubjectFactory _subjectFactory;

    public ConfigurableSubjectSerializer(
        SubjectTypeRegistry typeRegistry,
        SubjectFactory subjectFactory)
    {
        _typeRegistry = typeRegistry;
        _subjectFactory = subjectFactory;
    }

    private IInterceptorSubject CreateInstance(Type type)
        => _subjectFactory.CreateSubject(type);

    // ... rest unchanged
}
```

### 9. SubjectMethodInvoker Enhancement

Change registration from singleton to scoped:

```csharp
services.AddScoped<ISubjectMethodInvoker, SubjectMethodInvoker>();
```

Resolve DI parameters using ActivatorUtilities semantics:

```csharp
public class SubjectMethodInvoker : ISubjectMethodInvoker
{
    private readonly IServiceProvider _serviceProvider;

    public SubjectMethodInvoker(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public async Task<MethodInvocationResult> InvokeAsync(
        IInterceptorSubject subject,
        SubjectMethodInfo method,
        object?[] userParameters,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var parameters = ResolveParameters(method, userParameters);

            var result = method.MethodInfo.Invoke(subject, parameters);
            if (method.IsAsync && result is Task task)
            {
                await task.WaitAsync(cancellationToken);
                result = GetTaskResult(task, method.ResultType);
            }

            return MethodInvocationResult.Succeeded(result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var actualException = ex is TargetInvocationException tie
                ? tie.InnerException ?? ex
                : ex;

            return MethodInvocationResult.Failed(actualException);
        }
    }

    private object?[] ResolveParameters(SubjectMethodInfo method, object?[] userParameters)
    {
        var parameterInfos = method.MethodInfo.GetParameters();
        var resolvedParameters = new object?[parameterInfos.Length];
        var userParameterIndex = 0;

        for (var i = 0; i < parameterInfos.Length; i++)
        {
            var parameterType = parameterInfos[i].ParameterType;

            // Try to resolve from DI first (ActivatorUtilities semantics)
            var service = _serviceProvider.GetService(parameterType);
            if (service != null)
            {
                resolvedParameters[i] = service;
            }
            else
            {
                // Use user-provided parameter
                resolvedParameters[i] = userParameterIndex < userParameters.Length
                    ? userParameters[userParameterIndex++]
                    : null;
            }
        }

        return resolvedParameters;
    }

    private static object? GetTaskResult(Task task, Type? resultType)
    {
        if (resultType == null)
            return null;

        var resultProperty = task.GetType().GetProperty("Result");
        return resultProperty?.GetValue(task);
    }
}
```

### 10. Operation on VirtualFolder / Storage

```csharp
[Operation(Title = "Create", Icon = "Add")]
public async Task CreateAsync(IDialogService dialogService)
{
    await SubjectCreationHelper.CreateSubjectAsync(
        dialogService,
        this.Storage,
        this.RelativePath);
}
```

### 11. Markdown Editor - Create Button

Add a "Create" button to the markdown editor that:
1. Opens the same `CreateSubjectWizard`
2. Inserts the JSON as a subject block at the current cursor position
3. Saves the file automatically

**Location:**
- In split mode: Right pane toolbar (next to existing Edit button)
- In fullscreen edit mode: Header toolbar

**Implementation in `MarkdownFileEditComponent.razor`:**

```razor
<SectionContent SectionName="EditActions">
    @* Existing edit button *@
    @if (_activeRegion != null)
    {
        <MudButton ...>Edit</MudButton>
    }

    @* New create button - always visible *@
    <MudButton Variant="Variant.Filled"
               Color="Color.Primary"
               Size="Size.Small"
               StartIcon="@Icons.Material.Filled.Add"
               OnClick="OnCreateSubjectClick">
        Create
    </MudButton>
</SectionContent>
```

**Create handler:**

```csharp
private async Task OnCreateSubjectClick()
{
    if (File == null || _editor == null)
        return;

    // Open create wizard (returns subject + name)
    var result = await CreateSubjectWizard.ShowAsync(DialogService);
    if (result == null)
        return;

    // Serialize to JSON
    var json = Serializer.Serialize(result.Subject);

    // Build subject block markdown using user-chosen name
    var subjectBlock = $"\n```subject({result.Name})\n{json}\n```\n";

    // Get current cursor position
    var position = await _editor.GetPosition();

    // Insert at cursor
    var range = new BlazorMonaco.Range(
        position.LineNumber, position.Column,
        position.LineNumber, position.Column);

    await _editor.ExecuteEdits("insert-subject", new[]
    {
        new IdentifiedSingleEditOperation
        {
            Range = range,
            Text = subjectBlock
        }
    });

    // Update content and save
    _currentContent = await _editor.GetValue();
    await RefreshDecorationsAsync();

    using var stream = new MemoryStream(Encoding.UTF8.GetBytes(_currentContent));
    await File.WriteAsync(stream, CancellationToken.None);

    _originalContent = _currentContent;
    IsDirtyChanged?.Invoke(false);
}
```

## Subject Type Attributes

Subject types should use standard .NET attributes for the type picker:

```csharp
using System.ComponentModel;

[Category("Sensors")]
[Description("Monitors temperature using a connected probe")]
[InterceptorSubject]
public partial class TemperatureSensor : IConfigurableSubject, ITitleProvider, IIconProvider
{
    // ...
}
```

**Type Picker Filtering:**

The type picker should only show types that:
1. Implement `IConfigurableSubject`
2. Have a `[Category]` attribute (types without category are infrastructure/internal)

This naturally excludes `FluentStorageContainer`, `VirtualFolder`, and other infrastructure types that shouldn't be user-created.

**Icon for Type Cards (MVP):**

No icon for now - cards display type name and description only. Can add icons in a future iteration.

**Name Input in Step 1:**

The wizard returns both the subject AND the chosen name. User enters name in step 1 alongside type selection.

```csharp
public record CreateSubjectResult(IInterceptorSubject Subject, string Name);

public static Task<CreateSubjectResult?> ShowAsync(IDialogService dialogService)
{
    // Returns subject + name, or null if cancelled
}
```

**Filename/Block Name:**

The user-chosen name is used for:
- JSON filename in folder/storage: `{name}.json`
- Subject block name in markdown: `subject({name})`

## E2E Tests

Add Playwright tests in `HomeBlaze.E2E.Tests`:

### Test Cases

1. **CreateSubjectFromFolder_ShowsWizard**
   - Navigate to a folder
   - Click "Create" operation
   - Verify wizard dialog opens with type picker

2. **TypePicker_GroupsByCategory**
   - Open create wizard
   - Verify types are grouped in expansion panels by category
   - Verify all panels are expanded by default

3. **TypePicker_ShowsTypeCards**
   - Open create wizard
   - Verify each type shows as a card with icon, name, description

4. **SelectType_AdvancesToSetupStep**
   - Open create wizard
   - Click on a type card
   - Verify wizard advances to step 2 (setup/edit component)

5. **BackButton_ReturnsToTypePicker**
   - Advance to step 2
   - Click back button
   - Verify returns to type picker

6. **CreateSubject_SavesJsonFile**
   - Complete wizard and click Create
   - Verify new JSON file exists in storage
   - Verify file contains correct type discriminator

7. **CancelWizard_NoFileCreated**
   - Open wizard, select type
   - Click Cancel
   - Verify no new file created

8. **MarkdownEditor_CreateButton_InsertsSubject**
   - Open a markdown file for editing
   - Click Create button in toolbar
   - Select a type, configure, and create
   - Verify subject block inserted at cursor position
   - Verify file is saved

9. **MarkdownEditor_CreateButton_GeneratesUniqueName**
   - Create a subject in markdown
   - Create another of the same type
   - Verify second subject has unique name (e.g., "motor1")

## Dependencies

- MudBlazor 8.x (MudStepper, MudExpansionPanels, MudGrid, MudCard)
- System.ComponentModel (CategoryAttribute, DescriptionAttribute)

## Existing Subjects - Add Attributes

Update existing `IConfigurableSubject` types with `[Category]` and `[Description]` attributes:

| File | Type | Category | Description |
|------|------|----------|-------------|
| `HomeBlaze.Samples/Motor.cs` | `Motor` | "Samples" | "Simulated motor with speed control and temperature monitoring" |
| `HomeBlaze.Components/Widget.cs` | `Widget` | "Components" | "References another subject by path and renders its widget" |
| `HomeBlaze.Servers.OpcUa/OpcUaServer.cs` | `OpcUaServer` | "Servers" | "Exposes subjects via OPC UA protocol" |

**Note:** `FluentStorageContainer` and `VirtualFolder` are infrastructure types and should NOT appear in the type picker. They are not user-creatable subjects.

## File Changes Summary

| File | Change |
|------|--------|
| `SubjectComponentType.cs` | Add `Setup` value |
| `ISubjectEditComponent.cs` | Add `IsCreating` property |
| `GenericEditComponent.razor` | New - auto-generated form |
| `CreateSubjectWizard.razor` | New - wizard dialog (returns subject + name) |
| `SubjectCreationHelper.cs` | New - shared creation logic |
| `SubjectFactory.cs` | New - extracted from serializer |
| `SubjectComponentRegistry.cs` | Add inheritance/interface fallback lookup |
| `ConfigurableSubjectSerializer.cs` | Use SubjectFactory |
| `SubjectMethodInvoker.cs` | Add DI parameter resolution |
| `ServiceCollectionExtensions.cs` | Register scoped invoker, SubjectFactory |
| `VirtualFolder.cs` | Add Create operation |
| `FluentStorageContainer.cs` | Add Create operation |
| `Motor.cs` | Add `[Category]`, `[Description]` |
| `Widget.cs` | Add `[Category]`, `[Description]` |
| `OpcUaServer.cs` | Add `[Category]`, `[Description]` |
| `MarkdownFileEditComponent.razor` | Add Create button and handler |
| `HomeBlaze.E2E.Tests/` | Add Playwright tests |
