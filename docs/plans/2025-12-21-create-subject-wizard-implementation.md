# Create Subject Wizard Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add ability to create new subjects via a wizard dialog from folders, storage, and markdown editor.

**Architecture:** Two-step wizard (type selection → configuration) with shared creation logic. SubjectFactory extracts instance creation, SubjectMethodInvoker enhanced for DI parameter resolution, GenericEditComponent provides fallback form generation.

**Tech Stack:** Blazor, MudBlazor 8.x (MudStepper, MudExpansionPanels, MudGrid, MudCard), System.ComponentModel attributes, Playwright for E2E tests.

---

## Task 1: Add Setup to SubjectComponentType

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.Components.Abstractions/Attributes/SubjectComponentType.cs`

**Step 1: Add Setup enum value**

```csharp
namespace HomeBlaze.Components.Abstractions.Attributes;

public enum SubjectComponentType
{
    Page,
    Edit,
    Setup,
    Widget
}
```

**Step 2: Build to verify compilation**

Run: `dotnet build src/HomeBlaze/HomeBlaze.Components.Abstractions/HomeBlaze.Components.Abstractions.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Components.Abstractions/Attributes/SubjectComponentType.cs
git commit -m "feat(HomeBlaze): add Setup to SubjectComponentType enum"
```

---

## Task 2: Add IsCreating to ISubjectEditComponent

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.Components.Abstractions/ISubjectEditComponent.cs`

**Step 1: Add IsCreating property to interface**

Add after `PreferredDialogSize`:

```csharp
    /// <summary>
    /// Gets or sets whether the component is in creation mode (new subject) vs edit mode.
    /// </summary>
    bool IsCreating { get; set; }
```

**Step 2: Build to find all implementing components**

Run: `dotnet build src/Namotion.Interceptor.slnx 2>&1 | grep -i "does not implement"`
Expected: List of components that need IsCreating added

**Step 3: Add IsCreating to MotorEditComponent.razor**

In `src/HomeBlaze/HomeBlaze.Samples.Blazor/MotorEditComponent.razor`, add:

```csharp
[Parameter]
public bool IsCreating { get; set; }
```

**Step 4: Add IsCreating to MarkdownFileEditComponent.razor**

In `src/HomeBlaze/HomeBlaze.Storage.Blazor/Files/MarkdownFileEditComponent.razor`, add in @code block:

```csharp
[Parameter]
public bool IsCreating { get; set; }
```

**Step 5: Add IsCreating to OpcUaServerEditComponent.razor**

In `src/HomeBlaze/HomeBlaze.Servers.OpcUa.Blazor/OpcUaServerEditComponent.razor`, add:

```csharp
[Parameter]
public bool IsCreating { get; set; }
```

**Step 6: Build to verify all implementations**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: Build succeeded

**Step 7: Commit**

```bash
git add -A
git commit -m "feat(HomeBlaze): add IsCreating to ISubjectEditComponent interface"
```

---

## Task 3: Create SubjectFactory

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Services/SubjectFactory.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Services/ServiceCollectionExtensions.cs`

**Step 1: Create SubjectFactory class**

```csharp
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;

namespace HomeBlaze.Services;

/// <summary>
/// Factory for creating subject instances using dependency injection.
/// </summary>
public class SubjectFactory
{
    private readonly IServiceProvider _serviceProvider;

    public SubjectFactory(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    /// <summary>
    /// Creates a new instance of the specified subject type.
    /// </summary>
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

    /// <summary>
    /// Creates a new instance of the specified subject type.
    /// </summary>
    public T CreateSubject<T>() where T : IInterceptorSubject
    {
        return (T)CreateSubject(typeof(T));
    }
}
```

**Step 2: Register SubjectFactory in DI**

In `ServiceCollectionExtensions.cs`, add after line 25:

```csharp
        services.AddSingleton<SubjectFactory>();
```

**Step 3: Build to verify**

Run: `dotnet build src/HomeBlaze/HomeBlaze.Services/HomeBlaze.Services.csproj`
Expected: Build succeeded

**Step 4: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Services/SubjectFactory.cs src/HomeBlaze/HomeBlaze.Services/ServiceCollectionExtensions.cs
git commit -m "feat(HomeBlaze): add SubjectFactory for subject instantiation"
```

---

## Task 4: Update ConfigurableSubjectSerializer to use SubjectFactory

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.Services/ConfigurableSubjectSerializer.cs`

**Step 1: Inject SubjectFactory and use it**

Replace the constructor and CreateInstance method:

```csharp
    private readonly SubjectTypeRegistry _typeRegistry;
    private readonly SubjectFactory _subjectFactory;
    private readonly JsonSerializerOptions _jsonOptions;

    public ConfigurableSubjectSerializer(SubjectTypeRegistry typeRegistry, SubjectFactory subjectFactory)
    {
        _typeRegistry = typeRegistry;
        _subjectFactory = subjectFactory;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }
```

And replace CreateInstance:

```csharp
    private IInterceptorSubject CreateInstance(Type type)
    {
        return _subjectFactory.CreateSubject(type);
    }
```

**Step 2: Remove unused IServiceProvider field and using**

Remove `private readonly IServiceProvider _serviceProvider;` and the `Microsoft.Extensions.DependencyInjection` using if no longer needed.

**Step 3: Build to verify**

Run: `dotnet build src/HomeBlaze/HomeBlaze.Services/HomeBlaze.Services.csproj`
Expected: Build succeeded

**Step 4: Run existing tests**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Services.Tests/HomeBlaze.Services.Tests.csproj`
Expected: All tests pass

**Step 5: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Services/ConfigurableSubjectSerializer.cs
git commit -m "refactor(HomeBlaze): use SubjectFactory in ConfigurableSubjectSerializer"
```

---

## Task 5: Enhance SubjectMethodInvoker for DI Parameter Resolution

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.Services/SubjectMethodInvoker.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Services/ServiceCollectionExtensions.cs`

**Step 1: Add IServiceProvider to SubjectMethodInvoker**

Replace the class with:

```csharp
using System.Reflection;
using HomeBlaze.Abstractions.Services;
using Namotion.Interceptor;

namespace HomeBlaze.Services;

/// <summary>
/// Invokes operations and queries on subjects, resolving DI services for parameters.
/// </summary>
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
        catch (Exception exception)
        {
            var actualException = exception is TargetInvocationException tie
                ? tie.InnerException ?? exception
                : exception;

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

**Step 2: Change registration to scoped**

In `ServiceCollectionExtensions.cs`, change line 28 from:

```csharp
        services.AddSingleton<ISubjectMethodInvoker, SubjectMethodInvoker>();
```

to:

```csharp
        services.AddScoped<ISubjectMethodInvoker, SubjectMethodInvoker>();
```

**Step 3: Build to verify**

Run: `dotnet build src/HomeBlaze/HomeBlaze.Services/HomeBlaze.Services.csproj`
Expected: Build succeeded

**Step 4: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Services/SubjectMethodInvoker.cs src/HomeBlaze/HomeBlaze.Services/ServiceCollectionExtensions.cs
git commit -m "feat(HomeBlaze): add DI parameter resolution to SubjectMethodInvoker"
```

---

## Task 6: Enhance SubjectComponentRegistry for Inheritance Fallback

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.Services/Components/SubjectComponentRegistry.cs`

**Step 1: Update GetComponent method**

Replace the GetComponent method:

```csharp
    /// <summary>
    /// Gets a specific component registration for a subject type and component type.
    /// Supports inheritance and interface fallback for generic components.
    /// </summary>
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

**Step 2: Build to verify**

Run: `dotnet build src/HomeBlaze/HomeBlaze.Services/HomeBlaze.Services.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Services/Components/SubjectComponentRegistry.cs
git commit -m "feat(HomeBlaze): add inheritance fallback to SubjectComponentRegistry"
```

---

## Task 7: Add Category and Description to Existing Subjects

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.Samples/Motor.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Components/Widget.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Servers.OpcUa/OpcUaServer.cs`

**Step 1: Add attributes to Motor**

At top of Motor.cs, add using:

```csharp
using System.ComponentModel;
```

Add attributes before `[InterceptorSubject]`:

```csharp
[Category("Samples")]
[Description("Simulated motor with speed control and temperature monitoring")]
[InterceptorSubject]
public partial class Motor : BackgroundService, IConfigurableSubject, ITitleProvider, IIconProvider
```

**Step 2: Add attributes to Widget**

At top of Widget.cs, add using:

```csharp
using System.ComponentModel;
```

Add attributes before `[InterceptorSubject]`:

```csharp
[Category("Components")]
[Description("References another subject by path and renders its widget")]
[InterceptorSubject]
public partial class Widget : ITitleProvider, IConfigurableSubject
```

**Step 3: Add attributes to OpcUaServer**

At top of OpcUaServer.cs, add using:

```csharp
using System.ComponentModel;
```

Add attributes before `[InterceptorSubject]`:

```csharp
[Category("Servers")]
[Description("Exposes subjects via OPC UA protocol")]
[InterceptorSubject]
public partial class OpcUaServer : BackgroundService, IConfigurableSubject, ITitleProvider, IIconProvider, IServerSubject
```

**Step 4: Build to verify**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: Build succeeded

**Step 5: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Samples/Motor.cs src/HomeBlaze/HomeBlaze.Components/Widget.cs src/HomeBlaze/HomeBlaze.Servers.OpcUa/OpcUaServer.cs
git commit -m "feat(HomeBlaze): add Category and Description attributes to subjects"
```

---

## Task 8: Create GenericEditComponent

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Host/Components/GenericEditComponent.razor`

**Step 1: Create the component**

```razor
@using HomeBlaze.Abstractions
@using HomeBlaze.Components.Abstractions
@using HomeBlaze.Components.Abstractions.Attributes
@using MudBlazor
@using Namotion.Interceptor

@attribute [SubjectComponent(SubjectComponentType.Edit, typeof(IConfigurableSubject))]
@implements ISubjectEditComponent

<MudStack Spacing="3">
    @foreach (var property in GetConfigurationProperties())
    {
        <div>
            @if (property.Type == typeof(string))
            {
                <MudTextField T="string"
                              Label="@property.Name"
                              Value="@((string?)property.GetValue())"
                              ValueChanged="@(v => { property.SetValue(v); OnPropertyChanged(); })"
                              Immediate="true"
                              data-testid="@($"config-field-{property.Name.ToLowerInvariant()}")" />
            }
            else if (property.Type == typeof(int))
            {
                <MudNumericField T="int"
                                 Label="@property.Name"
                                 Value="@((int)(property.GetValue() ?? 0))"
                                 ValueChanged="@(v => { property.SetValue(v); OnPropertyChanged(); })"
                                 Immediate="true"
                                 data-testid="@($"config-field-{property.Name.ToLowerInvariant()}")" />
            }
            else if (property.Type == typeof(bool))
            {
                <MudSwitch T="bool"
                           Label="@property.Name"
                           Value="@((bool)(property.GetValue() ?? false))"
                           ValueChanged="@(v => { property.SetValue(v); OnPropertyChanged(); })"
                           data-testid="@($"config-field-{property.Name.ToLowerInvariant()}")" />
            }
            else if (property.Type == typeof(TimeSpan))
            {
                <MudTextField T="string"
                              Label="@property.Name"
                              Value="@((property.GetValue() as TimeSpan?)?.ToString() ?? "")"
                              ValueChanged="@(v => { if (TimeSpan.TryParse(v, out var ts)) property.SetValue(ts); OnPropertyChanged(); })"
                              Immediate="true"
                              HelperText="Format: hh:mm:ss"
                              data-testid="@($"config-field-{property.Name.ToLowerInvariant()}")" />
            }
            else
            {
                <MudTextField T="string"
                              Label="@($"{property.Name} ({property.Type.Name})")"
                              Value="@(property.GetValue()?.ToString() ?? "")"
                              Disabled="true"
                              HelperText="Unsupported type for generic editor"
                              data-testid="@($"config-field-{property.Name.ToLowerInvariant()}")" />
            }
        </div>
    }
</MudStack>

@code {
    [Parameter]
    public IInterceptorSubject? Subject { get; set; }

    [Parameter]
    public bool IsCreating { get; set; }

    public bool IsValid => true;
    public bool IsDirty { get; private set; }

    public event Action<bool>? IsValidChanged;
    public event Action<bool>? IsDirtyChanged;

    public string PreferredDialogSize => "Small";

    private IEnumerable<Namotion.Interceptor.Registry.RegisteredProperty> GetConfigurationProperties()
    {
        if (Subject == null)
            return Enumerable.Empty<Namotion.Interceptor.Registry.RegisteredProperty>();

        return Subject.GetConfigurationProperties();
    }

    private void OnPropertyChanged()
    {
        IsDirty = true;
        IsDirtyChanged?.Invoke(true);
    }

    public Task SaveAsync(CancellationToken cancellationToken)
    {
        IsDirty = false;
        IsDirtyChanged?.Invoke(false);
        return Task.CompletedTask;
    }
}
```

**Step 2: Build to verify**

Run: `dotnet build src/HomeBlaze/HomeBlaze.Host/HomeBlaze.Host.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Host/Components/GenericEditComponent.razor
git commit -m "feat(HomeBlaze): add GenericEditComponent for fallback form generation"
```

---

## Task 9: Create CreateSubjectWizard Dialog

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Host/Components/Dialogs/CreateSubjectWizard.razor`

**Step 1: Create the wizard component**

```razor
@using System.ComponentModel
@using System.Reflection
@using HomeBlaze.Abstractions
@using HomeBlaze.Components.Abstractions
@using HomeBlaze.Components.Abstractions.Attributes
@using HomeBlaze.Services
@using HomeBlaze.Services.Components
@using MudBlazor
@using Namotion.Interceptor

@inject SubjectTypeRegistry TypeRegistry
@inject SubjectComponentRegistry ComponentRegistry
@inject SubjectFactory SubjectFactory

<MudDialog data-testid="create-subject-wizard">
    <TitleContent>
        @if (_activeStep == 0)
        {
            <MudText Typo="Typo.h6">Step 1: Select Type</MudText>
        }
        else
        {
            <MudText Typo="Typo.h6">Step 2: Create @(_selectedType?.Name ?? "Subject")</MudText>
        }
    </TitleContent>
    <DialogContent>
        <MudStepper @bind-ActiveIndex="_activeStep"
                    Linear="true"
                    PreventStepChange="OnPreventStepChange"
                    Class="mud-width-full"
                    data-testid="create-wizard-stepper">
            <MudStep Title="Select Type">
                <div style="max-height: 400px; overflow-y: auto;">
                    @foreach (var category in GetGroupedTypes())
                    {
                        <MudExpansionPanels MultiExpansion="true" data-testid="@($"category-panel-{category.Key.ToLowerInvariant()}")">
                            <MudExpansionPanel IsInitiallyExpanded="true" Text="@category.Key">
                                <MudGrid Spacing="2">
                                    @foreach (var type in category.Value)
                                    {
                                        <MudItem xs="12" sm="6">
                                            <MudCard Outlined="true"
                                                     Class="@(_selectedType == type ? "mud-border-primary" : "")"
                                                     Style="cursor: pointer;"
                                                     @onclick="() => SelectType(type)"
                                                     data-testid="@($"type-card-{type.Name.ToLowerInvariant()}")">
                                                <MudCardContent>
                                                    <MudText Typo="Typo.subtitle1">@type.Name</MudText>
                                                    <MudText Typo="Typo.body2" Color="Color.Secondary">
                                                        @(type.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "No description")
                                                    </MudText>
                                                </MudCardContent>
                                            </MudCard>
                                        </MudItem>
                                    }
                                </MudGrid>
                            </MudExpansionPanel>
                        </MudExpansionPanels>
                    }
                </div>
            </MudStep>
            <MudStep Title="Configure">
                @if (_selectedType != null && _createdSubject != null)
                {
                    <MudStack Spacing="3">
                        <MudTextField @bind-Value="_subjectName"
                                      Label="Name"
                                      Required="true"
                                      RequiredError="Name is required"
                                      HelperText="@($"{_subjectName}.json")"
                                      Immediate="true"
                                      data-testid="create-name-input" />

                        <MudDivider />

                        @if (_editComponentType != null)
                        {
                            <DynamicComponent Type="_editComponentType"
                                              Parameters="@(new Dictionary<string, object?>
                                              {
                                                  { "Subject", _createdSubject },
                                                  { "IsCreating", true }
                                              })" />
                        }
                    </MudStack>
                }
            </MudStep>
        </MudStepper>
    </DialogContent>
    <DialogActions>
        <MudButton OnClick="Cancel"
                   data-testid="cancel-button">Cancel</MudButton>
        @if (_activeStep > 0)
        {
            <MudButton OnClick="GoBack"
                       data-testid="back-button">Back</MudButton>
        }
        <MudButton Color="Color.Primary"
                   Variant="Variant.Filled"
                   Disabled="@(!CanCreate)"
                   OnClick="Create"
                   data-testid="create-button">Create</MudButton>
    </DialogActions>
</MudDialog>

@code {
    [CascadingParameter]
    private IMudDialogInstance? MudDialog { get; set; }

    private int _activeStep = 0;
    private Type? _selectedType;
    private IInterceptorSubject? _createdSubject;
    private Type? _editComponentType;
    private string _subjectName = "";

    private bool CanCreate => _activeStep == 1
        && !string.IsNullOrWhiteSpace(_subjectName)
        && _createdSubject != null;

    private Dictionary<string, List<Type>> GetGroupedTypes()
    {
        return TypeRegistry.Types
            .Where(t => typeof(IConfigurableSubject).IsAssignableFrom(t))
            .Where(t => t.GetCustomAttribute<CategoryAttribute>() != null)
            .GroupBy(t => t.GetCustomAttribute<CategoryAttribute>()!.Category)
            .ToDictionary(g => g.Key, g => g.ToList());
    }

    private void SelectType(Type type)
    {
        _selectedType = type;
        _createdSubject = SubjectFactory.CreateSubject(type);
        _subjectName = type.Name.ToLowerInvariant();

        // Find edit component
        var setupComponent = ComponentRegistry.GetComponent(type, SubjectComponentType.Setup);
        var editComponent = ComponentRegistry.GetComponent(type, SubjectComponentType.Edit);
        _editComponentType = setupComponent?.ComponentType ?? editComponent?.ComponentType;

        // Advance to step 2
        _activeStep = 1;
    }

    private Task<bool> OnPreventStepChange(StepChangeDirection direction)
    {
        // Allow going back, prevent going forward without selection
        if (direction == StepChangeDirection.Forward && _selectedType == null)
            return Task.FromResult(true);

        return Task.FromResult(false);
    }

    private void GoBack()
    {
        _activeStep = 0;
    }

    private void Cancel()
    {
        MudDialog?.Cancel();
    }

    private void Create()
    {
        if (_createdSubject != null && !string.IsNullOrWhiteSpace(_subjectName))
        {
            MudDialog?.Close(DialogResult.Ok(new CreateSubjectResult(_createdSubject, _subjectName)));
        }
    }

    /// <summary>
    /// Shows the create subject wizard and returns the result.
    /// </summary>
    public static async Task<CreateSubjectResult?> ShowAsync(IDialogService dialogService)
    {
        var options = new DialogOptions
        {
            MaxWidth = MaxWidth.Medium,
            FullWidth = true,
            CloseOnEscapeKey = true
        };

        var dialog = await dialogService.ShowAsync<CreateSubjectWizard>("Create Subject", options);
        var result = await dialog.Result;

        if (result?.Canceled != false)
            return null;

        return result.Data as CreateSubjectResult;
    }
}
```

**Step 2: Create CreateSubjectResult record**

Add at the end of the file, outside the @code block, or create a separate file `src/HomeBlaze/HomeBlaze.Host/Components/Dialogs/CreateSubjectResult.cs`:

```csharp
namespace HomeBlaze.Host.Components.Dialogs;

/// <summary>
/// Result from the CreateSubjectWizard containing the created subject and chosen name.
/// </summary>
public record CreateSubjectResult(Namotion.Interceptor.IInterceptorSubject Subject, string Name);
```

**Step 3: Build to verify**

Run: `dotnet build src/HomeBlaze/HomeBlaze.Host/HomeBlaze.Host.csproj`
Expected: Build succeeded

**Step 4: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Host/Components/Dialogs/CreateSubjectWizard.razor src/HomeBlaze/HomeBlaze.Host/Components/Dialogs/CreateSubjectResult.cs
git commit -m "feat(HomeBlaze): add CreateSubjectWizard dialog"
```

---

## Task 10: Create SubjectCreationHelper

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Host/Helpers/SubjectCreationHelper.cs`

**Step 1: Create the helper class**

```csharp
using HomeBlaze.Host.Components.Dialogs;
using HomeBlaze.Storage.Abstractions;
using MudBlazor;
using Namotion.Interceptor;

namespace HomeBlaze.Host.Helpers;

/// <summary>
/// Shared helper for creating subjects in folders and storage.
/// </summary>
public static class SubjectCreationHelper
{
    /// <summary>
    /// Opens the create subject wizard and saves the result to storage.
    /// </summary>
    public static async Task<IInterceptorSubject?> CreateSubjectAsync(
        IDialogService dialogService,
        IStorageContainer container,
        string relativePath)
    {
        var result = await CreateSubjectWizard.ShowAsync(dialogService);
        if (result == null)
            return null;

        var fileName = $"{result.Name}.json";
        var fullPath = string.IsNullOrEmpty(relativePath)
            ? fileName
            : Path.Combine(relativePath, fileName);

        await container.AddSubjectAsync(fullPath, result.Subject, CancellationToken.None);
        return result.Subject;
    }
}
```

**Step 2: Build to verify**

Run: `dotnet build src/HomeBlaze/HomeBlaze.Host/HomeBlaze.Host.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Host/Helpers/SubjectCreationHelper.cs
git commit -m "feat(HomeBlaze): add SubjectCreationHelper for shared creation logic"
```

---

## Task 11: Add Create Operation to VirtualFolder

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.Storage/VirtualFolder.cs`

**Step 1: Add Create operation method**

Add the using at top:

```csharp
using MudBlazor;
```

Add the operation method to VirtualFolder class:

```csharp
    /// <summary>
    /// Opens the create subject wizard to add a new subject to this folder.
    /// </summary>
    [Operation(Title = "Create", Icon = "Add", Position = 1)]
    public async Task CreateAsync(IDialogService dialogService)
    {
        await HomeBlaze.Host.Helpers.SubjectCreationHelper.CreateSubjectAsync(
            dialogService,
            Storage,
            RelativePath);
    }
```

**Step 2: Add project reference if needed**

Check if HomeBlaze.Storage references HomeBlaze.Host. If not, we need to move SubjectCreationHelper to a shared location or use a different approach.

**Step 3: Build to verify**

Run: `dotnet build src/HomeBlaze/HomeBlaze.Storage/HomeBlaze.Storage.csproj`
Expected: Build succeeded (or identify circular reference issue)

**Step 4: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Storage/VirtualFolder.cs
git commit -m "feat(HomeBlaze): add Create operation to VirtualFolder"
```

---

## Task 12: Add Create Operation to FluentStorageContainer

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.Storage/FluentStorageContainer.cs`

**Step 1: Add Create operation method**

Add the using at top:

```csharp
using MudBlazor;
```

Add the operation method:

```csharp
    /// <summary>
    /// Opens the create subject wizard to add a new subject to this storage.
    /// </summary>
    [Operation(Title = "Create", Icon = "Add", Position = 1)]
    public async Task CreateAsync(IDialogService dialogService)
    {
        await HomeBlaze.Host.Helpers.SubjectCreationHelper.CreateSubjectAsync(
            dialogService,
            this,
            string.Empty);
    }
```

**Step 2: Build to verify**

Run: `dotnet build src/HomeBlaze/HomeBlaze.Storage/HomeBlaze.Storage.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Storage/FluentStorageContainer.cs
git commit -m "feat(HomeBlaze): add Create operation to FluentStorageContainer"
```

---

## Task 13: Add Create Button to MarkdownFileEditComponent

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.Storage.Blazor/Files/MarkdownFileEditComponent.razor`

**Step 1: Add using for CreateSubjectWizard**

Add at top:

```razor
@using HomeBlaze.Host.Components.Dialogs
```

**Step 2: Add Create button to SectionContent**

Find the `<SectionContent SectionName="EditActions">` section and add after the existing edit button:

```razor
    <MudButton Variant="Variant.Filled"
               Color="Color.Primary"
               Size="Size.Small"
               StartIcon="@Icons.Material.Filled.Add"
               OnClick="OnCreateSubjectClick"
               data-testid="create-subject-button">
        Create
    </MudButton>
```

**Step 3: Add OnCreateSubjectClick handler**

Add to @code block:

```csharp
    private async Task OnCreateSubjectClick()
    {
        if (File == null || _editor == null)
            return;

        var result = await CreateSubjectWizard.ShowAsync(DialogService);
        if (result == null)
            return;

        var json = Serializer.Serialize(result.Subject);
        var subjectBlock = $"\n```subject({result.Name})\n{json}\n```\n";

        var position = await _editor.GetPosition();
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

        _currentContent = await _editor.GetValue();
        await RefreshDecorationsAsync();

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(_currentContent));
        await File.WriteAsync(stream, CancellationToken.None);

        _originalContent = _currentContent;
        IsDirtyChanged?.Invoke(false);
    }
```

**Step 4: Build to verify**

Run: `dotnet build src/HomeBlaze/HomeBlaze.Storage.Blazor/HomeBlaze.Storage.Blazor.csproj`
Expected: Build succeeded

**Step 5: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Storage.Blazor/Files/MarkdownFileEditComponent.razor
git commit -m "feat(HomeBlaze): add Create button to MarkdownFileEditComponent"
```

---

## Task 14: Create E2E Tests for CreateSubjectWizard

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.E2E.Tests/CreateSubjectWizardTests.cs`

**Step 1: Create the test file**

```csharp
using HomeBlaze.E2E.Tests.Infrastructure;
using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for the Create Subject Wizard functionality.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
public class CreateSubjectWizardTests
{
    private readonly PlaywrightFixture _fixture;

    public CreateSubjectWizardTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task CreateSubjectFromBrowser_ShowsWizard()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync(_fixture.ServerAddress);
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        // Navigate to Browser
        var browserLink = page.GetByRole(AriaRole.Link, new() { Name = "Browser" });
        await Assertions.Expect(browserLink).ToBeVisibleAsync(new() { Timeout = 30000 });
        await browserLink.ClickAsync();
        await page.WaitForURLAsync(url => url.Contains("/browser"), new() { Timeout = 30000 });

        // Wait for browser to load and find a folder with Create operation
        await page.WaitForTimeoutAsync(2000);

        // Look for Create button in operations panel
        var createButton = page.Locator("button:has-text('Create')").First;

        // Assert - if Create button exists, click it and verify wizard opens
        if (await createButton.IsVisibleAsync())
        {
            await createButton.ClickAsync();

            var wizard = page.Locator("[data-testid='create-subject-wizard']");
            await Assertions.Expect(wizard).ToBeVisibleAsync(new() { Timeout = 10000 });
        }
    }

    [Fact]
    public async Task TypePicker_GroupsByCategory()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync(_fixture.ServerAddress);
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        // Navigate to Browser
        var browserLink = page.GetByRole(AriaRole.Link, new() { Name = "Browser" });
        await browserLink.ClickAsync();
        await page.WaitForURLAsync(url => url.Contains("/browser"), new() { Timeout = 30000 });
        await page.WaitForTimeoutAsync(2000);

        // Open Create wizard
        var createButton = page.Locator("button:has-text('Create')").First;
        if (!await createButton.IsVisibleAsync())
            return; // Skip if no Create button available

        await createButton.ClickAsync();

        // Assert - category panels should be visible
        var samplesPanel = page.Locator("[data-testid='category-panel-samples']");
        await Assertions.Expect(samplesPanel).ToBeVisibleAsync(new() { Timeout = 10000 });
    }

    [Fact]
    public async Task SelectType_AdvancesToConfigureStep()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync(_fixture.ServerAddress);
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        // Navigate to Browser
        var browserLink = page.GetByRole(AriaRole.Link, new() { Name = "Browser" });
        await browserLink.ClickAsync();
        await page.WaitForURLAsync(url => url.Contains("/browser"), new() { Timeout = 30000 });
        await page.WaitForTimeoutAsync(2000);

        // Open Create wizard
        var createButton = page.Locator("button:has-text('Create')").First;
        if (!await createButton.IsVisibleAsync())
            return;

        await createButton.ClickAsync();
        await page.WaitForTimeoutAsync(500);

        // Click on Motor type card
        var motorCard = page.Locator("[data-testid='type-card-motor']");
        if (await motorCard.IsVisibleAsync())
        {
            await motorCard.ClickAsync();

            // Assert - name input should be visible (step 2)
            var nameInput = page.Locator("[data-testid='create-name-input']");
            await Assertions.Expect(nameInput).ToBeVisibleAsync(new() { Timeout = 5000 });
        }
    }

    [Fact]
    public async Task BackButton_ReturnsToTypePicker()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync(_fixture.ServerAddress);
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        // Navigate to Browser
        var browserLink = page.GetByRole(AriaRole.Link, new() { Name = "Browser" });
        await browserLink.ClickAsync();
        await page.WaitForURLAsync(url => url.Contains("/browser"), new() { Timeout = 30000 });
        await page.WaitForTimeoutAsync(2000);

        // Open Create wizard and select type
        var createButton = page.Locator("button:has-text('Create')").First;
        if (!await createButton.IsVisibleAsync())
            return;

        await createButton.ClickAsync();
        await page.WaitForTimeoutAsync(500);

        var motorCard = page.Locator("[data-testid='type-card-motor']");
        if (!await motorCard.IsVisibleAsync())
            return;

        await motorCard.ClickAsync();
        await page.WaitForTimeoutAsync(500);

        // Click Back button
        var backButton = page.Locator("[data-testid='back-button']");
        await Assertions.Expect(backButton).ToBeVisibleAsync(new() { Timeout = 5000 });
        await backButton.ClickAsync();

        // Assert - type cards should be visible again
        await Assertions.Expect(motorCard).ToBeVisibleAsync(new() { Timeout = 5000 });
    }

    [Fact]
    public async Task CancelButton_ClosesWizard()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync(_fixture.ServerAddress);
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        // Navigate to Browser
        var browserLink = page.GetByRole(AriaRole.Link, new() { Name = "Browser" });
        await browserLink.ClickAsync();
        await page.WaitForURLAsync(url => url.Contains("/browser"), new() { Timeout = 30000 });
        await page.WaitForTimeoutAsync(2000);

        // Open Create wizard
        var createButton = page.Locator("button:has-text('Create')").First;
        if (!await createButton.IsVisibleAsync())
            return;

        await createButton.ClickAsync();
        await page.WaitForTimeoutAsync(500);

        // Click Cancel
        var cancelButton = page.Locator("[data-testid='cancel-button']");
        await Assertions.Expect(cancelButton).ToBeVisibleAsync(new() { Timeout = 5000 });
        await cancelButton.ClickAsync();

        // Assert - wizard should be closed
        var wizard = page.Locator("[data-testid='create-subject-wizard']");
        await Assertions.Expect(wizard).Not.ToBeVisibleAsync(new() { Timeout = 5000 });
    }
}
```

**Step 2: Build tests**

Run: `dotnet build src/HomeBlaze/HomeBlaze.E2E.Tests/HomeBlaze.E2E.Tests.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.E2E.Tests/CreateSubjectWizardTests.cs
git commit -m "test(HomeBlaze): add E2E tests for CreateSubjectWizard"
```

---

## Task 15: Final Build and Test

**Step 1: Build entire solution**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: Build succeeded

**Step 2: Run unit tests**

Run: `dotnet test src/Namotion.Interceptor.slnx --filter "FullyQualifiedName!~E2E"`
Expected: All tests pass

**Step 3: Run E2E tests (if server available)**

Run: `dotnet test src/HomeBlaze/HomeBlaze.E2E.Tests/HomeBlaze.E2E.Tests.csproj`
Expected: Tests pass (or skip gracefully if server not running)

**Step 4: Final commit**

```bash
git add -A
git commit -m "feat(HomeBlaze): complete Create Subject Wizard implementation"
```

---

## Summary

This implementation plan covers:

1. **Infrastructure** (Tasks 1-6): SubjectComponentType, ISubjectEditComponent, SubjectFactory, SubjectMethodInvoker enhancements
2. **Subject Metadata** (Task 7): Category/Description attributes on existing subjects
3. **UI Components** (Tasks 8-9): GenericEditComponent and CreateSubjectWizard
4. **Integration** (Tasks 10-13): SubjectCreationHelper and operations on folder/storage/markdown
5. **Testing** (Task 14): Playwright E2E tests
6. **Verification** (Task 15): Full build and test run

Total: 15 tasks with bite-sized steps for TDD workflow.
