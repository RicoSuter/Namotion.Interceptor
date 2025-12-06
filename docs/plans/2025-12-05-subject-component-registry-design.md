# Subject Component Registry Design

## Overview

Migrate the existing `SubjectViewAttribute` system to use enum-based component types, add object-path-based page routing, and remove the `IPage` interface. This enables any subject to have page/edit/widget views via component registration.

## Goals

- Subjects stay UI-agnostic (no Blazor dependencies)
- Components point to subjects (not vice versa)
- Support multiple component types per subject (Page, Edit, Widget)
- Enable non-file subjects to have page views
- Fast O(1) registry lookup (no inheritance fallback)
- Use existing registry APIs for path resolution
- TDD with snapshot testing

## Design Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Extend vs Replace | **Migrate** | Breaking changes OK, only 1 usage |
| ViewType | **Enum** | Type-safe, discoverable |
| Inheritance fallback | **No** | Simpler, faster; use generic form for edit fallback |
| Subject property | **IInterceptorSubject?** | Enables dynamic UI generation |
| IPage interface | **Remove** | Replace with SubjectComponent system |
| URL path | **/pages/** | Plural for consistency |

## Core Types

### SubjectComponentType Enum

```csharp
namespace HomeBlaze.Abstractions;

public enum SubjectComponentType
{
    Page,
    Edit,
    Widget
}
```

### SubjectComponentAttribute (migrated from SubjectViewAttribute)

```csharp
namespace HomeBlaze.Abstractions.Attributes;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public class SubjectComponentAttribute : Attribute
{
    public Type SubjectType { get; }
    public SubjectComponentType ComponentType { get; }
    public string? Name { get; set; }

    public SubjectComponentAttribute(SubjectComponentType componentType, Type subjectType)
    {
        ComponentType = componentType;
        SubjectType = subjectType ?? throw new ArgumentNullException(nameof(subjectType));
    }
}
```

### ISubjectComponent Interface

```csharp
namespace HomeBlaze.Abstractions;

public interface ISubjectComponent
{
    IInterceptorSubject? Subject { get; set; }
}
```

### SubjectComponentRegistration Record

```csharp
namespace HomeBlaze.Core.Services;

public record SubjectComponentRegistration(
    Type ComponentType,
    Type SubjectType,
    SubjectComponentType Type,
    string? Name
);
```

### SubjectComponentRegistry (migrated from SubjectViewRegistry)

```csharp
namespace HomeBlaze.Core.Services;

public class SubjectComponentRegistry
{
    private readonly Dictionary<(Type SubjectType, SubjectComponentType Type, string? Name),
        SubjectComponentRegistration> _components = new();

    public SubjectComponentRegistry ScanAssemblies(params Assembly[] assemblies)
    {
        foreach (var assembly in assemblies)
        {
            foreach (var type in assembly.GetTypes())
            {
                foreach (var attr in type.GetCustomAttributes<SubjectComponentAttribute>())
                {
                    var key = (attr.SubjectType, attr.ComponentType, attr.Name);
                    _components[key] = new SubjectComponentRegistration(
                        type, attr.SubjectType, attr.ComponentType, attr.Name);
                }
            }
        }
        return this;
    }

    public SubjectComponentRegistration? GetComponent(
        Type subjectType,
        SubjectComponentType type,
        string? name = null)
        => _components.GetValueOrDefault((subjectType, type, name));

    public IEnumerable<SubjectComponentRegistration> GetComponents(
        Type subjectType,
        SubjectComponentType type)
        => _components.Values.Where(r => r.SubjectType == subjectType && r.Type == type);

    public bool HasComponent(
        Type subjectType,
        SubjectComponentType type,
        string? name = null)
        => _components.ContainsKey((subjectType, type, name));
}
```

### SubjectPathResolver

Uses existing `RegisteredSubjectProperty` APIs instead of runtime type checking.

```csharp
namespace HomeBlaze.Core.Services;

public static class SubjectPathResolver
{
    public static IInterceptorSubject? ResolveSubject(
        IInterceptorSubject root,
        ISubjectRegistry registry,
        string path)
    {
        if (string.IsNullOrEmpty(path))
            return root;

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = root;

        for (int i = 0; i < segments.Length; i++)
        {
            var segment = Uri.UnescapeDataString(segments[i]);
            var registered = registry.TryGetRegisteredSubject(current);
            if (registered == null)
                return null;

            var property = registered.TryGetProperty(segment);
            if (property == null || !property.HasChildSubjects)
                return null;

            // Direct subject reference
            if (property.IsSubjectReference)
            {
                var child = property.Children.FirstOrDefault();
                if (child.Subject == null)
                    return null;
                current = child.Subject;
                continue;
            }

            // Collection or dictionary - next segment is key/index
            if (i + 1 >= segments.Length)
                return null;

            var indexStr = Uri.UnescapeDataString(segments[++i]);
            var matchedChild = property.Children
                .FirstOrDefault(c => c.Index?.ToString() == indexStr);

            if (matchedChild.Subject == null)
                return null;

            current = matchedChild.Subject;
        }

        return current;
    }
}
```

## URL Routing

Route: `/pages/{**path}` where path uses object paths like `Children/Notes/Children/file.md`

### Pages.razor (renamed from DocsPage.razor)

```razor
@page "/pages/{*Path}"
@inherits HomeBlazorComponentBase
@inject SubjectComponentRegistry ComponentRegistry
@inject ISubjectRegistry SubjectRegistry

@code {
    [Parameter]
    public string? Path { get; set; }

    private IInterceptorSubject? _subject;
    private Type? _componentType;

    protected override void OnParametersSet()
    {
        _subject = SubjectPathResolver.ResolveSubject(Root!, SubjectRegistry, Path ?? "");

        if (_subject != null)
        {
            var registration = ComponentRegistry.GetComponent(
                _subject.GetType(),
                SubjectComponentType.Page);
            _componentType = registration?.ComponentType;
        }
    }
}

@if (_componentType != null && _subject != null)
{
    <DynamicComponent Type="_componentType"
        Parameters="@(new Dictionary<string, object?> { ["Subject"] = _subject })" />
}
else if (_subject != null)
{
    <MudText>No page component registered for @_subject.GetType().Name</MudText>
}
else
{
    <MudText Color="Color.Error">Subject not found at path: @Path</MudText>
}
```

## Edit Fallback

When no specific Edit component is registered, fall back to the generic `SubjectConfigurationDialog` which uses `[Configuration]` properties.

## Migration

### Files to Delete
- `HomeBlaze.Abstractions/Attributes/SubjectViewAttribute.cs`
- `HomeBlaze.Abstractions/IPage.cs` (if exists)
- `HomeBlaze.Core/Services/SubjectViewRegistry.cs`

### Files to Create
- `HomeBlaze.Abstractions/SubjectComponentType.cs`
- `HomeBlaze.Abstractions/Attributes/SubjectComponentAttribute.cs`
- `HomeBlaze.Abstractions/ISubjectComponent.cs`
- `HomeBlaze.Core/Services/SubjectComponentRegistration.cs`
- `HomeBlaze.Core/Services/SubjectComponentRegistry.cs`
- `HomeBlaze.Core/Services/SubjectPathResolver.cs`
- `HomeBlaze/Components/Pages/Pages.razor`

### Files to Modify
- `HomeBlaze/Components/Views/MotorEditView.razor` - Update attribute
- `HomeBlaze/Program.cs` - Replace SubjectViewRegistry with SubjectComponentRegistry
- Remove DocsPage.razor

## Testing

### New Project: HomeBlaze.Tests

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="Moq" />
    <PackageReference Include="Verify.Xunit" />
  </ItemGroup>
</Project>
```

### Test Cases

**SubjectPathResolverTests**
- `ResolveSubject_EmptyPath_ReturnsRoot`
- `ResolveSubject_SinglePropertyPath_ReturnsChild`
- `ResolveSubject_DictionaryPath_ReturnsChildByKey`
- `ResolveSubject_CollectionPath_ReturnsChildByIndex`
- `ResolveSubject_InvalidPath_ReturnsNull`
- `ResolveSubject_NestedPath_ReturnsDeepChild`
- Snapshot tests for complex path resolution scenarios

**SubjectComponentRegistryTests**
- `ScanAssemblies_FindsAttributedComponents`
- `GetComponent_ExactMatch_ReturnsRegistration`
- `GetComponent_NoMatch_ReturnsNull`
- `GetComponent_WithName_ReturnsNamedRegistration`
- `GetComponents_ReturnsAllOfType`
- Snapshot tests for registry state after scanning

## Usage Examples

### Page Component

```csharp
[SubjectComponent(SubjectComponentType.Page, typeof(MarkdownFile))]
public partial class MarkdownFilePageComponent : ComponentBase, ISubjectComponent
{
    [Parameter]
    public IInterceptorSubject? Subject { get; set; }

    private MarkdownFile? File => Subject as MarkdownFile;
}
```

### Edit Component (migrated)

```csharp
[SubjectComponent(SubjectComponentType.Edit, typeof(Motor))]
public partial class MotorEditView : ComponentBase, ISubjectComponent
{
    [Parameter]
    public IInterceptorSubject? Subject { get; set; }

    private Motor? Motor => Subject as Motor;
}
```

### Multiple Widgets

```csharp
[SubjectComponent(SubjectComponentType.Widget, typeof(Motor), Name = "status")]
public partial class MotorStatusWidget : ComponentBase, ISubjectComponent
{
    [Parameter]
    public IInterceptorSubject? Subject { get; set; }
}

[SubjectComponent(SubjectComponentType.Widget, typeof(Motor), Name = "temperature")]
public partial class MotorTemperatureWidget : ComponentBase, ISubjectComponent
{
    [Parameter]
    public IInterceptorSubject? Subject { get; set; }
}
```
