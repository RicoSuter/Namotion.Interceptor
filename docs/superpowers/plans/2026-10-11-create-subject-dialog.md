# Create Subject Dialog Redesign Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the HomeBlaze create subject wizard with a type picker (search, group sidebar, list) followed by a configure page that owns the name, and fix the dialog's known defects.

**Architecture:** A plain C# `SubjectTypeCatalog` decides which types are offered and how they are described, a thin `SubjectTypePicker` Razor component renders search, groups and list, and `SubjectSetupDialog` hosts the picker and the configure page. Type metadata comes from attributes on the subject types with fallbacks. Callers pass one options object.

**Tech Stack:** .NET 10, C# 14, Blazor Server, MudBlazor 9.2.0, xUnit, Moq, Playwright (E2E).

**Spec:** `docs/superpowers/specs/2026-10-11-create-subject-dialog-design.md`

## Global Constraints

- All paths are relative to the repository root. HomeBlaze code lives under `src/HomeBlaze/`.
- Warnings are errors. Nullable is enabled.
- No abbreviations in variable and parameter names (`descriptor`, not `d`; `attribute`, not `attr`).
- Inline comments state only a why that a reader cannot derive. XML docs state the contract.
- Tests are named `When<Condition>_Then<ExpectedBehavior>` and use explicit `// Arrange`, `// Act`, `// Assert` comments.
- No `Task.Delay` or `Thread.Sleep` in tests. Use Playwright's retrying assertions or `AsyncTestHelpers.WaitUntilAsync`.
- No em dashes and no hard wrapping in markdown files.
- Commit messages are prefixed with `feat:`, `fix:`, `refactor:`, `test:` or `docs:` and carry no AI attribution of any kind (no agent names, no `Co-Authored-By`, no "Generated with").
- Do not commit `.claude/launch.json`.
- Display names in the UI use sentence case ("Shelly device", "Create subject").
- UI test hooks are `data-testid` attributes. The dialog root keeps `data-testid="create-subject-wizard"`.

## File Structure

Created:

| File | Responsibility |
|------|----------------|
| `src/HomeBlaze/HomeBlaze.Abstractions/Attributes/SubjectIconAttribute.cs` | Icon of a subject type before an instance exists |
| `src/HomeBlaze/HomeBlaze.Abstractions/Attributes/ExcludeFromCreationAttribute.cs` | Marks a type that is never offered for creation |
| `src/HomeBlaze/HomeBlaze.Abstractions/Attributes/SingleInstanceAttribute.cs` | Marks a type that is offered only while no instance exists |
| `src/HomeBlaze/HomeBlaze.Services/SubjectTypeDescriptor.cs` | What the picker shows for one type, plus search matching |
| `src/HomeBlaze/HomeBlaze.Services/SubjectTypeCatalog.cs` | Which types are offered, grouping, filtering, name suggestion |
| `src/HomeBlaze/HomeBlaze.Services.Tests/Models/CatalogSubjects.cs` | Test subject types for the catalog tests |
| `src/HomeBlaze/HomeBlaze.Services.Tests/SubjectTypeCatalogTests.cs` | Catalog unit tests |
| `src/HomeBlaze/HomeBlaze.Components/MudBlazorIconResolver.cs` | Icon name to MudBlazor icon string |
| `src/HomeBlaze/HomeBlaze.Components.Tests/MudBlazorIconResolverTests.cs` | Resolver unit tests |
| `src/HomeBlaze/HomeBlaze.Storage.Abstractions/SubjectSetupRequest.cs` | What a storage caller tells the setup service |
| `src/HomeBlaze/HomeBlaze.Components/Dialogs/SubjectSetupOptions.cs` | Options object of the dialog |
| `src/HomeBlaze/HomeBlaze.Components/Dialogs/SubjectTypePicker.razor` | Search, group sidebar and type list |

Modified:

| File | Change |
|------|--------|
| `src/HomeBlaze/HomeBlaze.Services/ServiceCollectionExtensions.cs` | Register `SubjectTypeCatalog` |
| About 23 subject type files (Task 2) | Add metadata attributes |
| `src/HomeBlaze/HomeBlaze.Host/Display/SubjectIconExtensions.cs` | Delegate icon resolution to the new resolver |
| `src/HomeBlaze/HomeBlaze.Components.Tests/HomeBlaze.Components.Tests.csproj` | Reference `HomeBlaze.Components` |
| `src/HomeBlaze/HomeBlaze.Storage.Abstractions/ISubjectSetupService.cs`, `CreateSubjectResult.cs`, `SubjectCreatorExtensions.cs` | Request parameter, nullable name |
| `src/HomeBlaze/HomeBlaze.Storage/VirtualFolder.cs`, `FluentStorageContainer.cs` | Pass existing child names |
| `src/HomeBlaze/HomeBlaze.Storage.Blazor/BlazorSubjectSetupService.cs` | Build the dialog options |
| `src/HomeBlaze/HomeBlaze.Storage.Tests/SubjectCreatorExtensionsTests.cs` | Follow the new contract |
| `src/HomeBlaze/HomeBlaze.Components/Editors/SubjectEditPanel.razor` | Report validity to the parent |
| `src/HomeBlaze/HomeBlaze.Components/Dialogs/SubjectSetupDialog.razor` | Rewritten |
| `src/HomeBlaze/HomeBlaze.Components/Components/GridLayoutWidget.razor`, `CanvasLayoutWidget.razor` | New `ShowAsync` call |
| `src/HomeBlaze/HomeBlaze.Storage.Blazor/Files/MarkdownFileEditComponent.razor` | New `ShowAsync` call with block names |
| `src/HomeBlaze/HomeBlaze.E2E.Tests/SubjectSetupDialogTests.cs`, `HomeBlaze.E2E.Tests.csproj` | Rewritten tests, test helper reference |
| `src/HomeBlaze/HomeBlaze/Data/Files/Docs/development/building-subjects.md` | Document the attributes and the dialog |

---

### Task 1: Type catalog and creation attributes

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Abstractions/Attributes/SubjectIconAttribute.cs`
- Create: `src/HomeBlaze/HomeBlaze.Abstractions/Attributes/ExcludeFromCreationAttribute.cs`
- Create: `src/HomeBlaze/HomeBlaze.Abstractions/Attributes/SingleInstanceAttribute.cs`
- Create: `src/HomeBlaze/HomeBlaze.Services/SubjectTypeDescriptor.cs`
- Create: `src/HomeBlaze/HomeBlaze.Services/SubjectTypeCatalog.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Services/ServiceCollectionExtensions.cs`
- Test: `src/HomeBlaze/HomeBlaze.Services.Tests/Models/CatalogSubjects.cs`
- Test: `src/HomeBlaze/HomeBlaze.Services.Tests/SubjectTypeCatalogTests.cs`

**Interfaces:**
- Consumes: `SubjectTypeRegistry.RegisteredTypes` (`IReadOnlyCollection<Type>`), `TypeProvider.AddTypes(IEnumerable<Type>)`, `HomeBlaze.Abstractions.IConfigurable`.
- Produces:
  - `HomeBlaze.Abstractions.Attributes.SubjectIconAttribute(string iconName)` with `string IconName { get; }`
  - `HomeBlaze.Abstractions.Attributes.ExcludeFromCreationAttribute`
  - `HomeBlaze.Abstractions.Attributes.SingleInstanceAttribute`
  - `HomeBlaze.Services.SubjectTypeDescriptor(Type Type, string DisplayName, string? Description, string Group, string IconName)` with `bool Matches(string? searchText)`
  - `HomeBlaze.Services.SubjectTypeGroup(string Name, int Count)`
  - `HomeBlaze.Services.SubjectTypeCatalog` with:
    - `const string OtherGroup = "Other"`
    - `IReadOnlyList<SubjectTypeDescriptor> GetCreatableTypes(IEnumerable<IInterceptorSubject> existingSubjects)`
    - `static SubjectTypeDescriptor Describe(Type type)`
    - `static IReadOnlyList<SubjectTypeDescriptor> Filter(IReadOnlyList<SubjectTypeDescriptor> descriptors, string? searchText, string? group)`
    - `static IReadOnlyList<SubjectTypeGroup> GetGroups(IReadOnlyList<SubjectTypeDescriptor> descriptors, string? searchText)`
    - `static bool IsNameTaken(string name, IReadOnlyCollection<string> existingNames, string suffix)`
    - `static string SuggestName(Type type, IReadOnlyCollection<string> existingNames, string suffix)`
  - `SubjectTypeCatalog` registered as a singleton in `AddHomeBlazeServices`.

- [ ] **Step 1: Add the three attributes**

`src/HomeBlaze/HomeBlaze.Abstractions/Attributes/SubjectIconAttribute.cs`:

```csharp
namespace HomeBlaze.Abstractions.Attributes;

/// <summary>
/// Sets the icon shown for a subject type where no instance exists yet, such as the create dialog.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class SubjectIconAttribute(string iconName) : Attribute
{
    /// <summary>
    /// Gets the icon name, using the same names as <see cref="IIconProvider.IconName"/> (e.g., "Hub").
    /// </summary>
    public string IconName { get; } = iconName;
}
```

`src/HomeBlaze/HomeBlaze.Abstractions/Attributes/ExcludeFromCreationAttribute.cs`:

```csharp
namespace HomeBlaze.Abstractions.Attributes;

/// <summary>
/// Marks a subject type that is never offered in the create dialog, because it only exists as part of another subject.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ExcludeFromCreationAttribute : Attribute;
```

`src/HomeBlaze/HomeBlaze.Abstractions/Attributes/SingleInstanceAttribute.cs`:

```csharp
namespace HomeBlaze.Abstractions.Attributes;

/// <summary>
/// Marks a subject type that is offered in the create dialog only while no instance of it exists in the subject graph.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class SingleInstanceAttribute : Attribute;
```

- [ ] **Step 2: Add the test subject types**

`src/HomeBlaze/HomeBlaze.Services.Tests/Models/CatalogSubjects.cs`. The explicit parameterless constructors mirror the existing `TestContainer` model in the same folder.

```csharp
using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Services.Tests.Models;

[InterceptorSubject]
[Category("Devices")]
[DisplayName("Zebra sensor")]
[Description("Counts stripes")]
[SubjectIcon("Pets")]
public partial class CatalogAnnotatedSubject : IConfigurable
{
    public partial string? Label { get; set; }

    public CatalogAnnotatedSubject()
    {
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

[InterceptorSubject]
[Category("Devices")]
[DisplayName("Alpha relay")]
public partial class CatalogSecondDeviceSubject : IConfigurable
{
    public partial string? Label { get; set; }

    public CatalogSecondDeviceSubject()
    {
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

[InterceptorSubject]
[Category("Servers")]
public partial class CatalogServerSubject : IConfigurable
{
    public partial string? Label { get; set; }

    public CatalogServerSubject()
    {
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

[InterceptorSubject]
public partial class CatalogPlainSubject : IConfigurable
{
    public partial string? Label { get; set; }

    public CatalogPlainSubject()
    {
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

[InterceptorSubject]
[ExcludeFromCreation]
public partial class CatalogExcludedSubject : IConfigurable
{
    public partial string? Label { get; set; }

    public CatalogExcludedSubject()
    {
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

[InterceptorSubject]
[SingleInstance]
public partial class CatalogSingleInstanceSubject : IConfigurable
{
    public partial string? Label { get; set; }

    public CatalogSingleInstanceSubject()
    {
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

[InterceptorSubject]
public partial class CatalogNotConfigurableSubject
{
    public partial string? Label { get; set; }

    public CatalogNotConfigurableSubject()
    {
    }
}
```

- [ ] **Step 3: Write the failing catalog tests**

`src/HomeBlaze/HomeBlaze.Services.Tests/SubjectTypeCatalogTests.cs`:

```csharp
using HomeBlaze.Services.Tests.Models;
using Namotion.Interceptor;

namespace HomeBlaze.Services.Tests;

public class SubjectTypeCatalogTests
{
    private static SubjectTypeCatalog CreateCatalog(params Type[] types)
    {
        var typeProvider = new TypeProvider();
        typeProvider.AddTypes(types);
        return new SubjectTypeCatalog(new SubjectTypeRegistry(typeProvider));
    }

    private static IReadOnlyList<SubjectTypeDescriptor> DescribeAll()
    {
        return CreateCatalog(
                typeof(CatalogAnnotatedSubject),
                typeof(CatalogSecondDeviceSubject),
                typeof(CatalogServerSubject),
                typeof(CatalogPlainSubject))
            .GetCreatableTypes([]);
    }

    [Fact]
    public void WhenTypeIsNotConfigurable_ThenItIsNotListed()
    {
        // Arrange
        var catalog = CreateCatalog(typeof(CatalogPlainSubject), typeof(CatalogNotConfigurableSubject));

        // Act
        var descriptors = catalog.GetCreatableTypes([]);

        // Assert
        Assert.Equal([typeof(CatalogPlainSubject)], descriptors.Select(descriptor => descriptor.Type));
    }

    [Fact]
    public void WhenTypeIsExcludedFromCreation_ThenItIsNotListed()
    {
        // Arrange
        var catalog = CreateCatalog(typeof(CatalogPlainSubject), typeof(CatalogExcludedSubject));

        // Act
        var descriptors = catalog.GetCreatableTypes([]);

        // Assert
        Assert.Equal([typeof(CatalogPlainSubject)], descriptors.Select(descriptor => descriptor.Type));
    }

    [Fact]
    public void WhenSingleInstanceTypeHasNoInstance_ThenItIsListed()
    {
        // Arrange
        var catalog = CreateCatalog(typeof(CatalogSingleInstanceSubject));

        // Act
        var descriptors = catalog.GetCreatableTypes([new CatalogPlainSubject()]);

        // Assert
        Assert.Equal([typeof(CatalogSingleInstanceSubject)], descriptors.Select(descriptor => descriptor.Type));
    }

    [Fact]
    public void WhenSingleInstanceTypeHasAnInstance_ThenItIsNotListed()
    {
        // Arrange
        var catalog = CreateCatalog(typeof(CatalogSingleInstanceSubject), typeof(CatalogPlainSubject));
        IInterceptorSubject[] existingSubjects = [new CatalogSingleInstanceSubject(), new CatalogPlainSubject()];

        // Act
        var descriptors = catalog.GetCreatableTypes(existingSubjects);

        // Assert
        Assert.Equal([typeof(CatalogPlainSubject)], descriptors.Select(descriptor => descriptor.Type));
    }

    [Fact]
    public void WhenTypeHasAllAttributes_ThenDescriptorUsesThem()
    {
        // Act
        var descriptor = SubjectTypeCatalog.Describe(typeof(CatalogAnnotatedSubject));

        // Assert
        Assert.Equal("Zebra sensor", descriptor.DisplayName);
        Assert.Equal("Counts stripes", descriptor.Description);
        Assert.Equal("Devices", descriptor.Group);
        Assert.Equal("Pets", descriptor.IconName);
    }

    [Fact]
    public void WhenTypeHasNoAttributes_ThenDescriptorUsesFallbacks()
    {
        // Act
        var descriptor = SubjectTypeCatalog.Describe(typeof(CatalogPlainSubject));

        // Assert
        Assert.Equal("Catalog Plain Subject", descriptor.DisplayName);
        Assert.Null(descriptor.Description);
        Assert.Equal(SubjectTypeCatalog.OtherGroup, descriptor.Group);
        Assert.Equal("Category", descriptor.IconName);
    }

    [Fact]
    public void WhenTypeHasCategoryButNoIcon_ThenGroupIconIsUsed()
    {
        // Act
        var descriptor = SubjectTypeCatalog.Describe(typeof(CatalogServerSubject));

        // Assert
        Assert.Equal("Dns", descriptor.IconName);
    }

    [Fact]
    public void WhenTypesAreListed_ThenGroupsAreAlphabeticalWithOtherLastAndTypesAreSortedByDisplayName()
    {
        // Act
        var descriptors = DescribeAll();

        // Assert
        Assert.Equal(
            ["Alpha relay", "Zebra sensor", "Catalog Server Subject", "Catalog Plain Subject"],
            descriptors.Select(descriptor => descriptor.DisplayName));
        Assert.Equal(
            ["Devices", "Devices", "Servers", "Other"],
            descriptors.Select(descriptor => descriptor.Group));
    }

    [Theory]
    [InlineData("zebra")]
    [InlineData("CatalogAnnotated")]
    [InlineData("stripes")]
    [InlineData("  ZEBRA   devices ")]
    public void WhenSearchMatchesNameClassDescriptionOrGroup_ThenTypeIsFound(string searchText)
    {
        // Arrange
        var descriptors = DescribeAll();

        // Act
        var matches = SubjectTypeCatalog.Filter(descriptors, searchText, group: null);

        // Assert
        Assert.Contains(matches, descriptor => descriptor.Type == typeof(CatalogAnnotatedSubject));
        Assert.DoesNotContain(matches, descriptor => descriptor.Type == typeof(CatalogServerSubject));
    }

    [Fact]
    public void WhenSearchIsEmpty_ThenAllTypesAreReturned()
    {
        // Arrange
        var descriptors = DescribeAll();

        // Act
        var matches = SubjectTypeCatalog.Filter(descriptors, searchText: " ", group: null);

        // Assert
        Assert.Equal(descriptors, matches);
    }

    [Fact]
    public void WhenGroupIsSelected_ThenOnlyThatGroupIsReturned()
    {
        // Arrange
        var descriptors = DescribeAll();

        // Act
        var matches = SubjectTypeCatalog.Filter(descriptors, searchText: null, group: "Devices");

        // Assert
        Assert.Equal(
            [typeof(CatalogSecondDeviceSubject), typeof(CatalogAnnotatedSubject)],
            matches.Select(descriptor => descriptor.Type));
    }

    [Fact]
    public void WhenSearching_ThenGroupCountsFollowTheSearch()
    {
        // Arrange
        var descriptors = DescribeAll();

        // Act
        var groups = SubjectTypeCatalog.GetGroups(descriptors, "zebra");

        // Assert
        Assert.Equal(
            [new SubjectTypeGroup("Devices", 1), new SubjectTypeGroup("Servers", 0), new SubjectTypeGroup("Other", 0)],
            groups);
    }

    [Fact]
    public void WhenNameIsFree_ThenTypeNameIsSuggested()
    {
        // Act
        var name = SubjectTypeCatalog.SuggestName(typeof(CatalogPlainSubject), ["Other", "Dashboard.md"], ".json");

        // Assert
        Assert.Equal("CatalogPlainSubject", name);
    }

    [Fact]
    public void WhenNameIsTaken_ThenTheNextFreeNumberIsAppended()
    {
        // Arrange
        string[] existingNames = ["CatalogPlainSubject", "catalogplainsubject2.json"];

        // Act
        var name = SubjectTypeCatalog.SuggestName(typeof(CatalogPlainSubject), existingNames, ".json");

        // Assert
        Assert.Equal("CatalogPlainSubject3", name);
    }

    [Theory]
    [InlineData("history", true)]
    [InlineData("HISTORY", true)]
    [InlineData("Dashboard", false)]
    [InlineData("Other", false)]
    public void WhenExistingNameMatchesWithOrWithoutSuffixIgnoringCase_ThenNameIsTaken(string name, bool expected)
    {
        // Arrange
        string[] existingNames = ["History", "Dashboard.md"];

        // Act
        var isTaken = SubjectTypeCatalog.IsNameTaken(name, existingNames, ".json");

        // Assert
        Assert.Equal(expected, isTaken);
    }
}
```

- [ ] **Step 4: Run the tests to verify they fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Services.Tests --filter "FullyQualifiedName~SubjectTypeCatalogTests"`
Expected: build FAILS with `CS0246: The type or namespace name 'SubjectTypeCatalog' could not be found`.

- [ ] **Step 5: Implement the descriptor**

`src/HomeBlaze/HomeBlaze.Services/SubjectTypeDescriptor.cs`:

```csharp
namespace HomeBlaze.Services;

/// <summary>
/// Describes a subject type that can be created, as shown in the create dialog.
/// </summary>
public sealed record SubjectTypeDescriptor(Type Type, string DisplayName, string? Description, string Group, string IconName)
{
    /// <summary>
    /// Returns whether every whitespace separated term of the search text occurs in the display name,
    /// the class name, the description or the group, ignoring case. An empty search matches.
    /// </summary>
    public bool Matches(string? searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return true;
        }

        foreach (var term in searchText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Contains(DisplayName, term) &&
                !Contains(Type.Name, term) &&
                !Contains(Description, term) &&
                !Contains(Group, term))
            {
                return false;
            }
        }

        return true;
    }

    private static bool Contains(string? text, string term)
    {
        return text is not null && text.Contains(term, StringComparison.OrdinalIgnoreCase);
    }
}
```

- [ ] **Step 6: Implement the catalog**

`src/HomeBlaze/HomeBlaze.Services/SubjectTypeCatalog.cs`:

```csharp
using System.ComponentModel;
using System.Reflection;
using System.Text.RegularExpressions;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor;

namespace HomeBlaze.Services;

/// <summary>
/// A group of creatable subject types and the number of its types that match the current search.
/// </summary>
public sealed record SubjectTypeGroup(string Name, int Count);

/// <summary>
/// Lists the subject types that can be created, with the information the create dialog shows for them.
/// </summary>
public partial class SubjectTypeCatalog
{
    /// <summary>
    /// The group of types that have no <see cref="CategoryAttribute"/>.
    /// </summary>
    public const string OtherGroup = "Other";

    private const string DefaultIconName = "Category";

    private static readonly Dictionary<string, string> GroupIconNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Devices"] = "DevicesOther",
        ["Servers"] = "Dns",
        ["Clients"] = "Cable",
        ["History"] = "History",
        ["Layouts"] = "Dashboard",
        ["Components"] = "Widgets",
        ["Samples"] = "Science",
        ["Storage"] = "Storage"
    };

    private readonly SubjectTypeRegistry _typeRegistry;

    public SubjectTypeCatalog(SubjectTypeRegistry typeRegistry)
    {
        _typeRegistry = typeRegistry;
    }

    /// <summary>
    /// Gets the creatable types, ordered by group (alphabetically, <see cref="OtherGroup"/> last) and then by display name.
    /// </summary>
    /// <param name="existingSubjects">The subjects of the current graph, used to hide single instance types that already exist.</param>
    public IReadOnlyList<SubjectTypeDescriptor> GetCreatableTypes(IEnumerable<IInterceptorSubject> existingSubjects)
    {
        var existingTypes = existingSubjects.Select(subject => subject.GetType()).ToHashSet();

        return _typeRegistry.RegisteredTypes
            .Where(type => typeof(IConfigurable).IsAssignableFrom(type) && !type.IsAbstract)
            .Where(type => !type.IsDefined(typeof(ExcludeFromCreationAttribute), false))
            .Where(type => !type.IsDefined(typeof(SingleInstanceAttribute), false) || !existingTypes.Contains(type))
            .Select(Describe)
            .OrderBy(descriptor => descriptor.Group == OtherGroup)
            .ThenBy(descriptor => descriptor.Group, StringComparer.OrdinalIgnoreCase)
            .ThenBy(descriptor => descriptor.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Describes a type from its attributes, falling back to the class name split into words,
    /// the <see cref="OtherGroup"/> group and the icon of the group.
    /// </summary>
    public static SubjectTypeDescriptor Describe(Type type)
    {
        var group = type.GetCustomAttribute<CategoryAttribute>(false)?.Category;
        if (string.IsNullOrWhiteSpace(group))
        {
            group = OtherGroup;
        }

        var displayName = type.GetCustomAttribute<DisplayNameAttribute>(false)?.DisplayName;
        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = WordBoundaryRegex().Replace(type.Name, " ");
        }

        var iconName = type.GetCustomAttribute<SubjectIconAttribute>(false)?.IconName;
        if (string.IsNullOrWhiteSpace(iconName))
        {
            iconName = GroupIconNames.GetValueOrDefault(group, DefaultIconName);
        }

        var description = type.GetCustomAttribute<DescriptionAttribute>(false)?.Description;
        if (string.IsNullOrWhiteSpace(description))
        {
            description = null;
        }

        return new SubjectTypeDescriptor(type, displayName, description, group, iconName);
    }

    /// <summary>
    /// Returns the descriptors that match the search text and, when a group is given, belong to it. The order is preserved.
    /// </summary>
    public static IReadOnlyList<SubjectTypeDescriptor> Filter(
        IReadOnlyList<SubjectTypeDescriptor> descriptors, string? searchText, string? group)
    {
        return descriptors
            .Where(descriptor => group is null || string.Equals(descriptor.Group, group, StringComparison.OrdinalIgnoreCase))
            .Where(descriptor => descriptor.Matches(searchText))
            .ToList();
    }

    /// <summary>
    /// Returns every group in the order of the descriptors, with the number of its types that match the search text.
    /// </summary>
    public static IReadOnlyList<SubjectTypeGroup> GetGroups(IReadOnlyList<SubjectTypeDescriptor> descriptors, string? searchText)
    {
        return descriptors
            .GroupBy(descriptor => descriptor.Group)
            .Select(group => new SubjectTypeGroup(group.Key, group.Count(descriptor => descriptor.Matches(searchText))))
            .ToList();
    }

    /// <summary>
    /// Returns whether an existing name equals the name, with or without the suffix, ignoring case.
    /// </summary>
    public static bool IsNameTaken(string name, IReadOnlyCollection<string> existingNames, string suffix)
    {
        var nameWithSuffix = name + suffix;
        return existingNames.Any(existingName =>
            string.Equals(existingName, name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(existingName, nameWithSuffix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Suggests a name for a new subject of the type: the class name, with a number from 2 appended while it is taken.
    /// </summary>
    public static string SuggestName(Type type, IReadOnlyCollection<string> existingNames, string suffix)
    {
        var name = type.Name;
        for (var number = 2; IsNameTaken(name, existingNames, suffix); number++)
        {
            name = type.Name + number;
        }

        return name;
    }

    [GeneratedRegex("(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])")]
    private static partial Regex WordBoundaryRegex();
}
```

- [ ] **Step 7: Register the catalog**

In `src/HomeBlaze/HomeBlaze.Services/ServiceCollectionExtensions.cs`, add one line directly after `services.AddSingleton<SubjectFactory>();`:

```csharp
        services.AddSingleton<SubjectTypeCatalog>();
```

- [ ] **Step 8: Run the tests to verify they pass**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Services.Tests --filter "FullyQualifiedName~SubjectTypeCatalogTests"`
Expected: PASS, 21 tests (15 methods, two of them theories with 4 cases each).

If the build reports that a test subject already defines a parameterless constructor, remove the explicit constructors from `CatalogSubjects.cs`. The generator then supplies them.

- [ ] **Step 9: Run the whole services test project**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Services.Tests`
Expected: PASS. `ServiceCollectionExtensionsTests` must still pass with the new registration.

- [ ] **Step 10: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Abstractions/Attributes src/HomeBlaze/HomeBlaze.Services src/HomeBlaze/HomeBlaze.Services.Tests
git commit -m "feat: add the HomeBlaze subject type catalog and creation attributes"
```

---

### Task 2: Metadata on the subject types

**Files:**
- Modify: the 23 files in the table below.

**Interfaces:**
- Consumes: `SubjectIconAttribute`, `ExcludeFromCreationAttribute`, `SingleInstanceAttribute` from Task 1, and `System.ComponentModel.DisplayNameAttribute`, `CategoryAttribute`, `DescriptionAttribute`.
- Produces: the display names below. Task 5's E2E tests rely on `Motor` being "Motor", `InMemoryHistoryStoreSubject` being "In-memory history" and `SqliteHistoryStoreSubject` being "SQLite history".

- [ ] **Step 1: Add the attributes**

Add the listed attributes directly above the existing `[InterceptorSubject]` attribute of each class, keeping every attribute that is already there. Each file needs `using System.ComponentModel;` for `[DisplayName]`, `[Category]` and `[Description]`, and `using HomeBlaze.Abstractions.Attributes;` for the other three. Add a using only where it is missing.

| File (under `src/HomeBlaze/`) | Class | Attributes to add |
|---|---|---|
| `Namotion.Devices.Shelly/ShellyDevice.cs` | `ShellyDevice` | `[DisplayName("Shelly device")]` `[SubjectIcon("Hub")]` |
| `Namotion.Devices.Sonos/SonosSystem.cs` | `SonosSystem` | `[DisplayName("Sonos system")]` `[SubjectIcon("LibraryMusic")]` |
| `Namotion.Devices.MyStrom/MyStromSwitch.cs` | `MyStromSwitch` | `[DisplayName("myStrom switch")]` `[SubjectIcon("Power")]` |
| `Namotion.Devices.Wallbox/WallboxCharger.cs` | `WallboxCharger` | `[DisplayName("Wallbox charger")]` `[SubjectIcon("EvStation")]` |
| `Namotion.Devices.Luxtronik/LuxtronikHeatPump.cs` | `LuxtronikHeatPump` | `[DisplayName("Luxtronik heat pump")]` `[SubjectIcon("HeatPump")]` |
| `Namotion.Devices.Gpio/GpioSubject.cs` | `GpioSubject` | `[DisplayName("GPIO")]` `[SubjectIcon("Memory")]` |
| `Namotion.Devices.Ecowitt/EcowittGateway.cs` | `EcowittGateway` | `[DisplayName("Ecowitt gateway")]` `[SubjectIcon("Cloud")]` |
| `Namotion.Devices.SunSpec/SunSpecDevice.cs` | `SunSpecDevice` | `[DisplayName("SunSpec device")]` `[SubjectIcon("SolarPower")]` |
| `Namotion.Devices.Philips.Hue/HueBridge.cs` | `HueBridge` | `[DisplayName("Philips Hue bridge")]` `[SubjectIcon("Hub")]` |
| `MyCompany.SamplePlugin1/SampleDevice1.cs` | `SampleDevice1` | `[DisplayName("Sample device 1")]` `[SubjectIcon("Thermostat")]` |
| `MyCompany.SamplePlugin2/SampleDevice2.cs` | `SampleDevice2` | `[DisplayName("Sample device 2")]` `[SubjectIcon("LightMode")]` |
| `HomeBlaze.Samples/Motor.cs` | `Motor` | `[DisplayName("Motor")]` `[SubjectIcon("Settings")]` |
| `HomeBlaze.OpcUa/OpcUaServer.cs` | `OpcUaServer` | `[DisplayName("OPC UA server")]` `[SubjectIcon("Dns")]` |
| `HomeBlaze.OpcUa/OpcUaClient.cs` | `OpcUaClient` | `[DisplayName("OPC UA client")]` `[SubjectIcon("Cable")]` |
| `HomeBlaze.History.InMemory/InMemoryHistoryStoreSubject.cs` | `InMemoryHistoryStoreSubject` | `[DisplayName("In-memory history")]` `[SubjectIcon("History")]` |
| `HomeBlaze.History.Sqlite/SqliteHistoryStoreSubject.cs` | `SqliteHistoryStoreSubject` | `[DisplayName("SQLite history")]` `[SubjectIcon("History")]` |
| `HomeBlaze.Components/GridLayout.cs` | `GridLayout` | `[DisplayName("Grid layout")]` `[SubjectIcon("GridView")]` |
| `HomeBlaze.Components/CanvasLayout.cs` | `CanvasLayout` | `[DisplayName("Canvas layout")]` `[SubjectIcon("Dashboard")]` |
| `HomeBlaze.Components/Widget.cs` | `Widget` | `[DisplayName("Widget")]` `[SubjectIcon("Widgets")]` |
| `HomeBlaze.Storage/FluentStorageContainer.cs` | `FluentStorageContainer` | `[Category("Storage")]` `[DisplayName("Storage")]` `[Description("Mounts a storage location and loads the files in it as subjects")]` `[SubjectIcon("Storage")]` |
| `HomeBlaze.Plugins/PluginManager.cs` | `PluginManager` | `[DisplayName("Plugin manager")]` `[Description("Loads plugins from NuGet feeds")]` `[SubjectIcon("Extension")]` `[SingleInstance]` |
| `HomeBlaze.Components/GridCell.cs` | `GridCell` | `[ExcludeFromCreation]` |
| `HomeBlaze.Components/CanvasNode.cs` | `CanvasNode` | `[ExcludeFromCreation]` |

Example, `src/HomeBlaze/HomeBlaze.Plugins/PluginManager.cs` after the change:

```csharp
using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Plugins.Models;
using Namotion.Interceptor.Attributes;
using Namotion.NuGet.Plugins;

namespace HomeBlaze.Plugins;

[DisplayName("Plugin manager")]
[Description("Loads plugins from NuGet feeds")]
[SubjectIcon("Extension")]
[SingleInstance]
[InterceptorSubject]
public partial class PluginManager : IConfigurable, ITitleProvider, IIconProvider
```

- [ ] **Step 2: Verify that every listed class carries its attributes**

Run:

```bash
cd src/HomeBlaze && grep -rl "\[SubjectIcon(" --include="*.cs" . | grep -v "/obj/\|/bin/\|Tests\|Attributes/" | wc -l && grep -rl "\[ExcludeFromCreation\]" --include="*.cs" . | grep -v "/obj/\|/bin/\|Tests" | wc -l && grep -rl "\[SingleInstance\]" --include="*.cs" . | grep -v "/obj/\|/bin/\|Tests" | wc -l
```

Expected output: `21`, `2`, `1`.

- [ ] **Step 3: Build**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: build succeeds with 0 warnings and 0 errors.

- [ ] **Step 4: Accept the two public API snapshots that list class attributes**

The public API snapshots of the two history stores include the attributes of each class, so they change.

Run: `dotnet test src/HomeBlaze/HomeBlaze.History.InMemory.Tests --filter "FullyQualifiedName~VerifyChecksTests"` and `dotnet test src/HomeBlaze/HomeBlaze.History.Sqlite.Tests --filter "FullyQualifiedName~VerifyChecksTests"`
Expected: both FAIL with a snapshot mismatch and write a `VerifyChecksTests.PublicApi.received.txt` next to the verified file.

Check that the only difference in each received file is the two added lines above the store class (`[HomeBlaze.Abstractions.Attributes.SubjectIcon("History")]` and `[System.ComponentModel.DisplayName("In-memory history")]` or `[System.ComponentModel.DisplayName("SQLite history")]`), then accept them:

```bash
mv src/HomeBlaze/HomeBlaze.History.InMemory.Tests/VerifyChecksTests.PublicApi.received.txt src/HomeBlaze/HomeBlaze.History.InMemory.Tests/VerifyChecksTests.PublicApi.verified.txt
mv src/HomeBlaze/HomeBlaze.History.Sqlite.Tests/VerifyChecksTests.PublicApi.received.txt src/HomeBlaze/HomeBlaze.History.Sqlite.Tests/VerifyChecksTests.PublicApi.verified.txt
```

Run both commands again.
Expected: PASS.

- [ ] **Step 5: Run the unit tests**

Run: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`
Expected: PASS. If another `VerifyChecksTests.PublicApi` test fails for the same reason, accept its snapshot the same way after checking that only attribute lines were added.

- [ ] **Step 6: Commit**

```bash
git add src/HomeBlaze
git commit -m "feat: describe the HomeBlaze subject types for the create dialog"
```

---

### Task 3: Icon resolver in HomeBlaze.Components

The picker lives in `HomeBlaze.Components` and cannot reference `HomeBlaze.Host`, where `SubjectIconExtensions.ResolveMudBlazorIcon` is. The implementation moves down and the host method delegates to it, so there is one implementation and no call site changes.

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Components/MudBlazorIconResolver.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Host/Display/SubjectIconExtensions.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Components.Tests/HomeBlaze.Components.Tests.csproj`
- Test: `src/HomeBlaze/HomeBlaze.Components.Tests/MudBlazorIconResolverTests.cs`

**Interfaces:**
- Produces: `HomeBlaze.Components.MudBlazorIconResolver.Resolve(string? iconName)` returning a MudBlazor icon string, `Icons.Material.Filled.Article` for a null, empty or unknown name.

- [ ] **Step 1: Reference the components project from its test project**

In `src/HomeBlaze/HomeBlaze.Components.Tests/HomeBlaze.Components.Tests.csproj`, add next to the existing `ProjectReference`:

```xml
        <ProjectReference Include="..\HomeBlaze.Components\HomeBlaze.Components.csproj" />
```

- [ ] **Step 2: Write the failing tests**

`src/HomeBlaze/HomeBlaze.Components.Tests/MudBlazorIconResolverTests.cs`:

```csharp
using MudBlazor;

namespace HomeBlaze.Components.Tests;

public class MudBlazorIconResolverTests
{
    [Fact]
    public void WhenIconNameIsKnown_ThenTheMaterialFilledIconIsReturned()
    {
        // Act
        var icon = MudBlazorIconResolver.Resolve("Hub");

        // Assert
        Assert.Equal(Icons.Material.Filled.Hub, icon);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("NoSuchIconName")]
    public void WhenIconNameIsMissingOrUnknown_ThenTheDefaultIconIsReturned(string? iconName)
    {
        // Act
        var icon = MudBlazorIconResolver.Resolve(iconName);

        // Assert
        Assert.Equal(Icons.Material.Filled.Article, icon);
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Components.Tests --filter "FullyQualifiedName~MudBlazorIconResolverTests"`
Expected: build FAILS with `CS0103: The name 'MudBlazorIconResolver' does not exist in the current context`.

- [ ] **Step 4: Implement the resolver**

`src/HomeBlaze/HomeBlaze.Components/MudBlazorIconResolver.cs`:

```csharp
using System.Collections.Concurrent;
using System.Reflection;
using MudBlazor;

namespace HomeBlaze.Components;

/// <summary>
/// Resolves subject icon names (e.g., "Folder", "Hub") to MudBlazor icon strings.
/// </summary>
public static class MudBlazorIconResolver
{
    private static readonly ConcurrentDictionary<string, string> IconCache = new();

    /// <summary>
    /// Resolves an icon name to the Material filled icon of that name, or to the article icon
    /// when the name is null, empty or unknown.
    /// </summary>
    public static string Resolve(string? iconName)
    {
        if (string.IsNullOrEmpty(iconName))
        {
            return Icons.Material.Filled.Article;
        }

        return IconCache.GetOrAdd(iconName, name =>
        {
            var field = typeof(Icons.Material.Filled).GetField(name, BindingFlags.Public | BindingFlags.Static);
            return field?.GetValue(null) as string ?? Icons.Material.Filled.Article;
        });
    }
}
```

- [ ] **Step 5: Delegate from the host**

In `src/HomeBlaze/HomeBlaze.Host/Display/SubjectIconExtensions.cs`:

Replace the body of `ResolveMudBlazorIcon` so the method reads:

```csharp
    /// <summary>
    /// Resolves an icon name to a MudBlazor icon string.
    /// </summary>
    public static string ResolveMudBlazorIcon(string iconName)
    {
        return MudBlazorIconResolver.Resolve(iconName);
    }
```

Delete the now unused field `private static readonly ConcurrentDictionary<string, string> IconCache = new();` and the usings `System.Collections.Concurrent` and `System.Reflection`. Add `using HomeBlaze.Components;`.

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Components.Tests`
Expected: PASS, including the existing `TimeZones` tests.

- [ ] **Step 7: Build the host**

Run: `dotnet build src/HomeBlaze/HomeBlaze.Host`
Expected: build succeeds with 0 warnings and 0 errors.

- [ ] **Step 8: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Components src/HomeBlaze/HomeBlaze.Components.Tests src/HomeBlaze/HomeBlaze.Host
git commit -m "refactor: move the HomeBlaze icon name resolver to the components project"
```

---

### Task 4: Storage setup contract

The storage callers must tell the dialog which names already exist, and a caller without a name gets a null name back. Today a name that already exists is accepted and `AddSubjectAsync` overwrites the file.

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Storage.Abstractions/SubjectSetupRequest.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Storage.Abstractions/ISubjectSetupService.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Storage.Abstractions/CreateSubjectResult.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Storage.Abstractions/SubjectCreatorExtensions.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Storage/VirtualFolder.cs` (method `CreateAsync`, near line 93)
- Modify: `src/HomeBlaze/HomeBlaze.Storage/FluentStorageContainer.cs` (method `CreateAsync`, near line 604)
- Modify: `src/HomeBlaze/HomeBlaze.Storage.Blazor/BlazorSubjectSetupService.cs`
- Test: `src/HomeBlaze/HomeBlaze.Storage.Tests/SubjectCreatorExtensionsTests.cs`

**Interfaces:**
- Produces:
  - `HomeBlaze.Storage.Abstractions.SubjectSetupRequest(IReadOnlyCollection<string> ExistingNames)`
  - `ISubjectSetupService.CreateSubjectAsync(SubjectSetupRequest request, CancellationToken cancellationToken)` returning `Task<CreateSubjectResult?>`
  - `CreateSubjectResult(IInterceptorSubject Subject, string? Name)`
  - `SubjectCreatorExtensions.CreateSubjectAndAddToStorageAsync(this ISubjectSetupService subjectSetupService, IStorageContainer storage, IReadOnlyCollection<string> existingNames, CancellationToken cancellationToken)`

- [ ] **Step 1: Rewrite the extension tests for the new contract**

Replace the content of `src/HomeBlaze/HomeBlaze.Storage.Tests/SubjectCreatorExtensionsTests.cs`:

```csharp
using HomeBlaze.Storage.Abstractions;
using Moq;
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Tests;

public class SubjectCreatorExtensionsTests
{
    [Fact]
    public async Task WhenTheDialogIsCancelled_ThenNoSubjectIsAdded()
    {
        // Arrange
        var subjectSetupService = new Mock<ISubjectSetupService>();
        var storage = new Mock<IStorageContainer>();

        subjectSetupService
            .Setup(service => service.CreateSubjectAsync(It.IsAny<SubjectSetupRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CreateSubjectResult?)null);

        // Act
        await subjectSetupService.Object.CreateSubjectAndAddToStorageAsync(storage.Object, [], CancellationToken.None);

        // Assert
        storage.Verify(
            container => container.AddSubjectAsync(It.IsAny<string>(), It.IsAny<IInterceptorSubject>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task WhenASubjectIsCreated_ThenItIsAddedWithTheJsonExtension()
    {
        // Arrange
        var subjectSetupService = new Mock<ISubjectSetupService>();
        var storage = new Mock<IStorageContainer>();
        var subject = new Mock<IInterceptorSubject>();

        subjectSetupService
            .Setup(service => service.CreateSubjectAsync(It.IsAny<SubjectSetupRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreateSubjectResult(subject.Object, "MySubject"));

        // Act
        await subjectSetupService.Object.CreateSubjectAndAddToStorageAsync(storage.Object, [], CancellationToken.None);

        // Assert
        storage.Verify(
            container => container.AddSubjectAsync("MySubject.json", subject.Object, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task WhenTheResultHasNoName_ThenNoSubjectIsAdded()
    {
        // Arrange
        var subjectSetupService = new Mock<ISubjectSetupService>();
        var storage = new Mock<IStorageContainer>();
        var subject = new Mock<IInterceptorSubject>();

        subjectSetupService
            .Setup(service => service.CreateSubjectAsync(It.IsAny<SubjectSetupRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreateSubjectResult(subject.Object, null));

        // Act
        await subjectSetupService.Object.CreateSubjectAndAddToStorageAsync(storage.Object, [], CancellationToken.None);

        // Assert
        storage.Verify(
            container => container.AddSubjectAsync(It.IsAny<string>(), It.IsAny<IInterceptorSubject>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task WhenCreating_ThenExistingNamesAndCancellationTokenArePassedOn()
    {
        // Arrange
        var subjectSetupService = new Mock<ISubjectSetupService>();
        var storage = new Mock<IStorageContainer>();
        var subject = new Mock<IInterceptorSubject>();
        string[] existingNames = ["History", "Dashboard.md"];
        using var cancellationTokenSource = new CancellationTokenSource();

        subjectSetupService
            .Setup(service => service.CreateSubjectAsync(It.IsAny<SubjectSetupRequest>(), cancellationTokenSource.Token))
            .ReturnsAsync(new CreateSubjectResult(subject.Object, "Test"));

        // Act
        await subjectSetupService.Object.CreateSubjectAndAddToStorageAsync(
            storage.Object, existingNames, cancellationTokenSource.Token);

        // Assert
        subjectSetupService.Verify(
            service => service.CreateSubjectAsync(
                It.Is<SubjectSetupRequest>(request => request.ExistingNames.SequenceEqual(existingNames)),
                cancellationTokenSource.Token),
            Times.Once);
        storage.Verify(
            container => container.AddSubjectAsync("Test.json", subject.Object, cancellationTokenSource.Token),
            Times.Once);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests --filter "FullyQualifiedName~SubjectCreatorExtensionsTests"`
Expected: build FAILS with `CS0246: The type or namespace name 'SubjectSetupRequest' could not be found`.

- [ ] **Step 3: Add the request and change the contract**

`src/HomeBlaze/HomeBlaze.Storage.Abstractions/SubjectSetupRequest.cs`:

```csharp
namespace HomeBlaze.Storage.Abstractions;

/// <summary>
/// What a storage tells the setup service about the place the new subject is created in.
/// </summary>
/// <param name="ExistingNames">The names that are already used there, with or without file extension.</param>
public record SubjectSetupRequest(IReadOnlyCollection<string> ExistingNames);
```

`src/HomeBlaze/HomeBlaze.Storage.Abstractions/ISubjectSetupService.cs`, replace the method and its documentation:

```csharp
    /// <summary>
    /// Prompts the user to create a new subject.
    /// </summary>
    /// <returns>The created subject and its name, or null if cancelled.</returns>
    Task<CreateSubjectResult?> CreateSubjectAsync(SubjectSetupRequest request, CancellationToken cancellationToken);
```

`src/HomeBlaze/HomeBlaze.Storage.Abstractions/CreateSubjectResult.cs`, replace the record and its documentation:

```csharp
/// <summary>
/// Result of the create subject dialog.
/// </summary>
/// <param name="Subject">The created subject.</param>
/// <param name="Name">The chosen name without file extension, or null when the caller asked for no name.</param>
public record CreateSubjectResult(IInterceptorSubject Subject, string? Name);
```

`src/HomeBlaze/HomeBlaze.Storage.Abstractions/SubjectCreatorExtensions.cs`, replace the method:

```csharp
    /// <summary>
    /// Opens the create dialog and adds the created subject to storage.
    /// </summary>
    /// <param name="subjectSetupService">The setup service.</param>
    /// <param name="storage">The storage the subject is added to.</param>
    /// <param name="existingNames">The names that are already used in the storage, so the dialog can reject a duplicate.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    public static async Task CreateSubjectAndAddToStorageAsync(
        this ISubjectSetupService subjectSetupService,
        IStorageContainer storage,
        IReadOnlyCollection<string> existingNames,
        CancellationToken cancellationToken)
    {
        var result = await subjectSetupService.CreateSubjectAsync(new SubjectSetupRequest(existingNames), cancellationToken);
        if (result?.Name is not { } name)
            return;

        var fileName = $"{name}{FileExtensions.Json}";
        await storage.AddSubjectAsync(fileName, result.Subject, cancellationToken);
    }
```

- [ ] **Step 4: Update the two storage callers**

In `src/HomeBlaze/HomeBlaze.Storage/VirtualFolder.cs` and in `src/HomeBlaze/HomeBlaze.Storage/FluentStorageContainer.cs`, change the body of `CreateAsync` in both files to:

```csharp
        => subjectSetupService.CreateSubjectAndAddToStorageAsync(this, [.. Children.Keys], cancellationToken);
```

`Children` keys are file names. For a JSON subject the key has no extension (`History`), for other files it has one (`Dashboard.md`). `SubjectTypeCatalog.IsNameTaken` handles both.

- [ ] **Step 5: Update the Blazor service signature**

In `src/HomeBlaze/HomeBlaze.Storage.Blazor/BlazorSubjectSetupService.cs`, change the method to take the request. The request is used in Task 5, when the dialog accepts options:

```csharp
    public Task<CreateSubjectResult?> CreateSubjectAsync(SubjectSetupRequest request, CancellationToken cancellationToken)
    {
        return SubjectSetupDialog.ShowAsync(_dialogService);
    }
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests`
Expected: PASS.

- [ ] **Step 7: Build everything that references the contract**

Run: `dotnet build src/HomeBlaze/HomeBlaze`
Expected: build succeeds with 0 warnings and 0 errors. `MarkdownFileEditComponent.razor` still compiles with the nullable name, because it only interpolates it.

- [ ] **Step 8: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Storage.Abstractions src/HomeBlaze/HomeBlaze.Storage src/HomeBlaze/HomeBlaze.Storage.Blazor src/HomeBlaze/HomeBlaze.Storage.Tests
git commit -m "refactor: pass the existing names of a storage to the subject setup service"
```

---

### Task 5: Picker, dialog and callers

This is one task because the dialog, the picker and the callers only work together, and the E2E tests are their shared test cycle.

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Components/Dialogs/SubjectSetupOptions.cs`
- Create: `src/HomeBlaze/HomeBlaze.Components/Dialogs/SubjectTypePicker.razor`
- Modify: `src/HomeBlaze/HomeBlaze.Components/Dialogs/SubjectSetupDialog.razor` (full rewrite)
- Modify: `src/HomeBlaze/HomeBlaze.Components/Editors/SubjectEditPanel.razor`
- Modify: `src/HomeBlaze/HomeBlaze.Components/Components/GridLayoutWidget.razor` (method `AddCellAt`)
- Modify: `src/HomeBlaze/HomeBlaze.Components/Components/CanvasLayoutWidget.razor` (method `AddNodeAtPosition`)
- Modify: `src/HomeBlaze/HomeBlaze.Storage.Blazor/BlazorSubjectSetupService.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Storage.Blazor/Files/MarkdownFileEditComponent.razor` (method `OnCreateClick`)
- Modify: `src/HomeBlaze/HomeBlaze.E2E.Tests/HomeBlaze.E2E.Tests.csproj`
- Test: `src/HomeBlaze/HomeBlaze.E2E.Tests/SubjectSetupDialogTests.cs` (full rewrite)

**Interfaces:**
- Consumes: `SubjectTypeCatalog`, `SubjectTypeDescriptor`, `SubjectTypeGroup` (Task 1), the display names of Task 2, `MudBlazorIconResolver.Resolve` (Task 3), `SubjectSetupRequest` and the nullable `CreateSubjectResult.Name` (Task 4).
- Produces:
  - `HomeBlaze.Components.Dialogs.SubjectSetupOptions` with `Action<IInterceptorSubject>? BeforeSave { get; init; }` and `SubjectNameOptions? Name { get; init; }`
  - `HomeBlaze.Components.Dialogs.SubjectNameOptions(string Label, string Suffix, IReadOnlyCollection<string> ExistingNames)`
  - `SubjectSetupDialog.ShowAsync(IDialogService dialogService, SubjectSetupOptions? options = null)` returning `Task<CreateSubjectResult?>`
  - `SubjectEditPanel` parameter `EventCallback<bool> IsValidChanged`
  - Test hooks: `create-subject-wizard`, `create-dialog-title`, `back-button`, `cancel-button`, `create-button`, `create-name-input`, `subject-type-picker`, `type-search-input`, `type-group-all`, `type-group-{group in lower case}`, `type-row-{class name in lower case}`, `type-empty-state`, `type-clear-search`.

- [ ] **Step 1: Reference the test helpers from the E2E project**

In `src/HomeBlaze/HomeBlaze.E2E.Tests/HomeBlaze.E2E.Tests.csproj`, add to the `ItemGroup` that holds the project references:

```xml
    <ProjectReference Include="..\..\Namotion.Interceptor.Testing\Namotion.Interceptor.Testing.csproj" />
```

- [ ] **Step 2: Rewrite the E2E tests for the new flow**

Replace the content of `src/HomeBlaze/HomeBlaze.E2E.Tests/SubjectSetupDialogTests.cs`:

```csharp
using System.Text.RegularExpressions;
using HomeBlaze.E2E.Tests.Infrastructure;
using Microsoft.Playwright;
using Namotion.Interceptor.Testing;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for SubjectSetupDialog: the type picker, the configure page and the create action.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class SubjectSetupDialogTests
{
    private const int PageLoadTimeout = 30000;
    private const int ElementVisibilityTimeout = 5000;

    private readonly PlaywrightFixture _fixture;

    public SubjectSetupDialogTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// The Create operation of the subject pane the dialog is opened from. Scoped to the pane
    /// container so it cannot resolve to the dialog's own Create button.
    /// </summary>
    private static ILocator PaneCreateButton(IPage page)
    {
        return page.Locator("#scrollContainer button:has-text('Create')").First;
    }

    private static ILocator Dialog(IPage page) => page.Locator("[data-testid='create-subject-wizard']");

    private static ILocator DialogTitle(IPage page) => page.Locator("[data-testid='create-dialog-title']");

    private static ILocator SearchInput(IPage page) => page.GetByPlaceholder("Search types");

    private static ILocator TypeRows(IPage page) => page.Locator("[data-testid^='type-row-']");

    private static ILocator TypeRow(IPage page, string typeName) => page.Locator($"[data-testid='type-row-{typeName}']");

    private static ILocator NameInput(IPage page) => page.GetByLabel("File name");

    private static ILocator CreateButton(IPage page) => page.Locator("[data-testid='create-button']");

    private async Task OpenSubjectBrowserAsync(IPage page)
    {
        await page.GotoAsync($"{_fixture.ServerAddress}");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        // The automatic timezone resolves through JS after interactive rendering. Prerendered
        // links are already clickable, but navigating then can race the old page's hydration.
        await Assertions.Expect(page.GetByTitle(new Regex(@"^Automatic \(")))
            .ToBeVisibleAsync(new() { Timeout = PageLoadTimeout });

        var browserLink = page.GetByRole(AriaRole.Link, new() { Name = "Browser" });
        await Assertions.Expect(browserLink).ToBeVisibleAsync(new() { Timeout = PageLoadTimeout });
        await browserLink.ClickAsync();
        await page.WaitForURLAsync(url => url.Contains("/browser"), new() { Timeout = PageLoadTimeout });

        // The URL changes before the browser has built its panes, and the pane's Create operation is
        // what the dialog is opened from, so that button is the condition the next step depends on.
        await Assertions.Expect(PaneCreateButton(page)).ToBeVisibleAsync(new() { Timeout = PageLoadTimeout });
    }

    private async Task OpenDialogAsync(IPage page)
    {
        await OpenSubjectBrowserAsync(page);
        await PaneCreateButton(page).ClickAsync();

        // The dialog frame appears before its content, so the first type row is the condition to hold for.
        await Assertions.Expect(TypeRows(page).First).ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });
    }

    private static async Task PickTypeAsync(IPage page, string typeName)
    {
        await TypeRow(page, typeName).ClickAsync();
        await Assertions.Expect(CreateButton(page)).ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });
    }

    [Fact]
    public async Task WhenTheDialogOpens_ThenThePickerIsShownWithoutANameField()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();

        // Act
        await OpenDialogAsync(page);

        // Assert
        await Assertions.Expect(DialogTitle(page)).ToHaveTextAsync("Create subject");
        await Assertions.Expect(SearchInput(page)).ToBeFocusedAsync(new() { Timeout = ElementVisibilityTimeout });
        await Assertions.Expect(page.Locator("[data-testid='type-group-all']")).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator("[data-testid='create-name-input']")).ToHaveCountAsync(0);
        await Assertions.Expect(CreateButton(page)).ToHaveCountAsync(0);
        await Assertions.Expect(page.Locator("[data-testid='cancel-button']")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task WhenTheDialogOpens_ThenTypesThatMustNotBeCreatedAreNotListed()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();

        // Act
        await OpenDialogAsync(page);

        // Assert - the cell and node types only exist inside a layout, and the test root already has a plugin manager.
        await Assertions.Expect(TypeRow(page, "gridlayout")).ToBeVisibleAsync();
        await Assertions.Expect(TypeRow(page, "gridcell")).ToHaveCountAsync(0);
        await Assertions.Expect(TypeRow(page, "canvasnode")).ToHaveCountAsync(0);
        await Assertions.Expect(TypeRow(page, "pluginmanager")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task WhenSearching_ThenTheListAndTheGroupCountsAreNarrowed()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        await OpenDialogAsync(page);

        // Act
        await SearchInput(page).FillAsync("simulated motor");

        // Assert
        await Assertions.Expect(TypeRows(page)).ToHaveCountAsync(1, new() { Timeout = ElementVisibilityTimeout });
        await Assertions.Expect(TypeRow(page, "motor")).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator("[data-testid='type-group-all']")).ToHaveTextAsync(new Regex(@"^\s*All\s*1\s*$"));
        await Assertions.Expect(page.Locator("[data-testid='type-group-devices']")).ToHaveTextAsync(new Regex(@"^\s*Devices\s*0\s*$"));
    }

    [Fact]
    public async Task WhenNothingMatchesTheSearch_ThenTheEmptyStateOffersToClearIt()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        await OpenDialogAsync(page);

        // Act
        await SearchInput(page).FillAsync("no such type anywhere");

        // Assert
        await Assertions.Expect(page.Locator("[data-testid='type-empty-state']"))
            .ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });
        await Assertions.Expect(TypeRows(page)).ToHaveCountAsync(0);

        await page.Locator("[data-testid='type-clear-search']").ClickAsync();
        await Assertions.Expect(TypeRow(page, "motor")).ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });
        await Assertions.Expect(SearchInput(page)).ToHaveValueAsync("");
    }

    [Fact]
    public async Task WhenAGroupIsSelected_ThenOnlyThatGroupIsListed()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        await OpenDialogAsync(page);

        // Act
        await page.Locator("[data-testid='type-group-layouts']").ClickAsync();

        // Assert
        await Assertions.Expect(TypeRows(page)).ToHaveCountAsync(2, new() { Timeout = ElementVisibilityTimeout });
        await Assertions.Expect(TypeRow(page, "gridlayout")).ToBeVisibleAsync();
        await Assertions.Expect(TypeRow(page, "canvaslayout")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task WhenATypeIsPicked_ThenTheConfigurePageShowsTheSuggestedName()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        await OpenDialogAsync(page);

        // Act
        await PickTypeAsync(page, "motor");

        // Assert
        await Assertions.Expect(DialogTitle(page)).ToHaveTextAsync("Create Motor");
        await Assertions.Expect(NameInput(page)).ToHaveValueAsync("Motor");
        await Assertions.Expect(Dialog(page).GetByText(".json", new() { Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(Dialog(page).GetByText("Part of the subject's path, which is its ID.")).ToBeVisibleAsync();
        await Assertions.Expect(CreateButton(page)).ToBeEnabledAsync(new() { Timeout = ElementVisibilityTimeout });
    }

    [Fact]
    public async Task WhenGoingBack_ThenTheSearchIsPreservedAndAnotherTypeGetsItsOwnForm()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        await OpenDialogAsync(page);
        await SearchInput(page).FillAsync("history");
        await PickTypeAsync(page, "inmemoryhistorystoresubject");
        await Assertions.Expect(DialogTitle(page)).ToHaveTextAsync("Create In-memory history");

        // Act
        await page.Locator("[data-testid='back-button']").ClickAsync();

        // Assert
        await Assertions.Expect(DialogTitle(page)).ToHaveTextAsync("Create subject");
        await Assertions.Expect(SearchInput(page)).ToHaveValueAsync("history");
        await Assertions.Expect(CreateButton(page)).ToHaveCountAsync(0);

        await PickTypeAsync(page, "sqlitehistorystoresubject");
        await Assertions.Expect(DialogTitle(page)).ToHaveTextAsync("Create SQLite history");
        await Assertions.Expect(NameInput(page)).ToHaveValueAsync("SqliteHistoryStoreSubject");
    }

    [Fact]
    public async Task WhenARequiredFieldIsEmpty_ThenCreateIsDisabled()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        await OpenDialogAsync(page);

        // Act
        await PickTypeAsync(page, "shellydevice");

        // Assert
        await Assertions.Expect(CreateButton(page)).ToBeDisabledAsync(new() { Timeout = ElementVisibilityTimeout });

        // The Shelly form reports its validity on key up, which a fill does not raise.
        await page.GetByLabel("Host Address (IP or hostname)").PressSequentiallyAsync("192.0.2.1");
        await Assertions.Expect(CreateButton(page)).ToBeEnabledAsync(new() { Timeout = ElementVisibilityTimeout });
    }

    [Fact]
    public async Task WhenTheNameIsInvalid_ThenCreateIsDisabledAndTheReasonIsShown()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        await OpenDialogAsync(page);
        await PickTypeAsync(page, "motor");
        var helperText = Dialog(page).Locator(".mud-input-helper-text");

        // Act & Assert - the test root already contains History.json
        await NameInput(page).FillAsync("history");
        await Assertions.Expect(helperText.Filter(new() { HasText = "A subject with this name already exists" }))
            .ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });
        await Assertions.Expect(CreateButton(page)).ToBeDisabledAsync();

        await NameInput(page).FillAsync("invalid/name");
        await Assertions.Expect(helperText.Filter(new() { HasText = "Name contains invalid characters" }))
            .ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });
        await Assertions.Expect(CreateButton(page)).ToBeDisabledAsync();

        await NameInput(page).FillAsync("");
        await Assertions.Expect(helperText.Filter(new() { HasText = "Name is required" }))
            .ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });
        await Assertions.Expect(CreateButton(page)).ToBeDisabledAsync();

        await NameInput(page).FillAsync("ValidName");
        await Assertions.Expect(CreateButton(page)).ToBeEnabledAsync(new() { Timeout = ElementVisibilityTimeout });
    }

    [Fact]
    public async Task WhenASubjectIsCreated_ThenItIsStoredUnderTheChosenName()
    {
        // The file is written to the test output directory, which later runs reuse, so it is removed again.
        // File watching is off in testRoot.json, so the created motor stays in the live graph for the rest
        // of the host's life, which is why the name is unique per run.
        var name = $"CreatedMotor{Guid.NewGuid():N}";
        var filePath = Path.Combine(AppContext.BaseDirectory, "TestData", $"{name}.json");
        try
        {
            // Arrange
            var page = await _fixture.CreatePageAsync();
            await OpenDialogAsync(page);
            await PickTypeAsync(page, "motor");
            await NameInput(page).FillAsync(name);
            await Assertions.Expect(CreateButton(page)).ToBeEnabledAsync(new() { Timeout = ElementVisibilityTimeout });

            // Act
            await CreateButton(page).ClickAsync();

            // Assert
            await Assertions.Expect(Dialog(page)).Not.ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });
            await AsyncTestHelpers.WaitUntilAsync(() => File.Exists(filePath), TimeSpan.FromSeconds(10));
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task WhenCancelIsClicked_ThenTheDialogCloses()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        await OpenDialogAsync(page);

        // Act
        await page.Locator("[data-testid='cancel-button']").ClickAsync();

        // Assert
        await Assertions.Expect(Dialog(page)).Not.ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });
    }

    [Fact]
    public async Task WhenHostedSubjectIsAddedToGridWithIsEnabledCleared_ThenItStartsDisabled()
    {
        // Creating the cell rewrites the page file in the test output directory, which later runs reuse.
        // Restoring it only restores the file: file watching is off in testRoot.json, so the created
        // (disabled) store stays in the live graph for the rest of the host's life.
        var gridPagePath = Path.Combine(AppContext.BaseDirectory, "TestData", "Demo", "Grid.md");
        var originalGridPage = await File.ReadAllBytesAsync(gridPagePath);
        try
        {
            // Arrange
            var page = await _fixture.CreatePageAsync();
            await page.GotoAsync($"{_fixture.ServerAddress}pages/Demo/Grid.md");
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

            var editMenu = page.Locator("[data-testid='edit-mode-menu']");
            await Assertions.Expect(editMenu).ToBeVisibleAsync(new() { Timeout = PageLoadTimeout });
            await editMenu.ClickAsync();
            await page.GetByText("Inline").ClickAsync();

            var emptyCell = page.Locator(".grid-cell.empty").First;
            await Assertions.Expect(emptyCell).ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });
            var cellIndex = await page.Locator(".grid-cell:not(.empty)").CountAsync();
            await emptyCell.ClickAsync();

            await Assertions.Expect(TypeRows(page).First).ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });
            await PickTypeAsync(page, "inmemoryhistorystoresubject");

            // A grid cell has no name, so the dialog must not ask for one.
            await Assertions.Expect(page.Locator("[data-testid='create-name-input']")).ToHaveCountAsync(0);

            var isEnabledCheckBox = page.GetByLabel("Enabled (auto-start on application startup)");
            await Assertions.Expect(isEnabledCheckBox).ToBeCheckedAsync(new() { Timeout = ElementVisibilityTimeout });
            await isEnabledCheckBox.UncheckAsync();
            await Assertions.Expect(isEnabledCheckBox).Not.ToBeCheckedAsync();
            await Assertions.Expect(CreateButton(page)).ToBeEnabledAsync(new() { Timeout = ElementVisibilityTimeout });

            // Act
            await CreateButton(page).ClickAsync();
            await Assertions.Expect(Dialog(page)).Not.ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });

            // Assert - the store reads IsEnabled once when it starts, so a start that saw the constructor
            // default reports Running for good instead of Disabled.
            await page.GotoAsync($"{_fixture.ServerAddress}browser/Demo/Grid.md/grid/Cells/{cellIndex}/Child");
            await Assertions.Expect(page.GetByText("In-Memory History").First)
                .ToBeVisibleAsync(new() { Timeout = PageLoadTimeout });
            await Assertions.Expect(page.GetByText("Status: Disabled"))
                .ToBeVisibleAsync(new() { Timeout = ElementVisibilityTimeout });
        }
        finally
        {
            await File.WriteAllBytesAsync(gridPagePath, originalGridPage);
        }
    }
}
```

- [ ] **Step 3: Run the E2E tests to verify they fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.E2E.Tests --filter "FullyQualifiedName~SubjectSetupDialogTests"`
Expected: FAIL. `WhenCancelIsClicked_ThenTheDialogCloses` may pass; every other test fails, most of them on the missing `type-row-` elements.

If Playwright reports missing browsers, install them once with `pwsh src/HomeBlaze/HomeBlaze.E2E.Tests/bin/Debug/net10.0/playwright.ps1 install chromium` and run again.

- [ ] **Step 4: Add the dialog options**

`src/HomeBlaze/HomeBlaze.Components/Dialogs/SubjectSetupOptions.cs`:

```csharp
using Namotion.Interceptor;

namespace HomeBlaze.Components.Dialogs;

/// <summary>
/// Options of the create subject dialog.
/// </summary>
public sealed record SubjectSetupOptions
{
    /// <summary>
    /// Optional callback that establishes the parent chain, invoked after the edits are applied and before they are persisted.
    /// </summary>
    public Action<IInterceptorSubject>? BeforeSave { get; init; }

    /// <summary>
    /// How the dialog asks for the name, or null when the created subject has no name of its own.
    /// </summary>
    public SubjectNameOptions? Name { get; init; }
}

/// <summary>
/// How the create subject dialog asks for the name of the new subject.
/// </summary>
/// <param name="Label">The label of the name field.</param>
/// <param name="Suffix">A fixed suffix shown after the name, such as ".json", or an empty string.</param>
/// <param name="ExistingNames">The names that are already used, with or without the suffix. The dialog rejects them.</param>
public sealed record SubjectNameOptions(string Label, string Suffix, IReadOnlyCollection<string> ExistingNames);
```

- [ ] **Step 5: Add the picker component**

`src/HomeBlaze/HomeBlaze.Components/Dialogs/SubjectTypePicker.razor`:

```razor
<div class="subject-type-picker" data-testid="subject-type-picker">
    <MudTextField T="string"
                  Value="_searchText"
                  ValueChanged="OnSearchChanged"
                  Placeholder="Search types"
                  Immediate="true"
                  AutoFocus="true"
                  Variant="Variant.Outlined"
                  Margin="Margin.Dense"
                  Adornment="Adornment.Start"
                  AdornmentIcon="@Icons.Material.Filled.Search"
                  data-testid="type-search-input" />

    <div class="subject-type-picker-body">
        <div class="subject-type-groups">
            <button type="button"
                    class="@GetGroupClass(null)"
                    @onclick="() => SelectGroup(null)"
                    data-testid="type-group-all">
                <span>All</span>
                <span>@_matchCount</span>
            </button>
            @foreach (var group in _groups)
            {
                var groupName = group.Name;
                <button type="button"
                        class="@GetGroupClass(groupName)"
                        @onclick="() => SelectGroup(groupName)"
                        data-testid="@($"type-group-{groupName.ToLowerInvariant()}")">
                    <span>@groupName</span>
                    <span>@group.Count</span>
                </button>
            }
        </div>

        <div class="subject-type-list">
            @if (_sections.Count == 0)
            {
                <div class="subject-type-empty" data-testid="type-empty-state">
                    <MudText Typo="Typo.body2">No types match</MudText>
                    @if (!string.IsNullOrWhiteSpace(_searchText))
                    {
                        <MudButton Variant="Variant.Text"
                                   Color="Color.Primary"
                                   OnClick="ClearSearch"
                                   data-testid="type-clear-search">Clear search</MudButton>
                    }
                </div>
            }
            @foreach (var section in _sections)
            {
                @if (_selectedGroup is null)
                {
                    <MudText Typo="Typo.caption" Class="subject-type-section-header">@section.Key</MudText>
                }
                @foreach (var descriptor in section)
                {
                    <button type="button"
                            class="subject-type-row"
                            @onclick="() => OnTypeSelected.InvokeAsync(descriptor)"
                            data-testid="@($"type-row-{descriptor.Type.Name.ToLowerInvariant()}")">
                        <MudIcon Icon="@MudBlazorIconResolver.Resolve(descriptor.IconName)" />
                        <span class="subject-type-row-text">
                            <span>@descriptor.DisplayName</span>
                            @if (descriptor.Description is not null)
                            {
                                <span class="subject-type-row-description">@descriptor.Description</span>
                            }
                        </span>
                    </button>
                }
            }
        </div>
    </div>
</div>

<style>
    .subject-type-picker {
        display: flex;
        flex-direction: column;
        gap: 12px;
    }

    .subject-type-picker-body {
        display: flex;
        gap: 16px;
        height: calc(100vh - 400px);
        min-height: 220px;
    }

    .subject-type-groups {
        display: flex;
        flex-direction: column;
        flex-shrink: 0;
        gap: 2px;
        width: 170px;
        overflow-y: auto;
    }

    .subject-type-group {
        display: flex;
        justify-content: space-between;
        gap: 8px;
        padding: 6px 10px;
        border: 0;
        border-radius: 4px;
        background: transparent;
        color: var(--mud-palette-text-secondary);
        font: inherit;
        text-align: left;
        cursor: pointer;
    }

    .subject-type-group:hover {
        background: var(--mud-palette-action-default-hover);
    }

    .subject-type-group.selected {
        background: var(--mud-palette-action-default-hover);
        color: var(--mud-palette-primary);
    }

    .subject-type-list {
        flex: 1;
        min-width: 0;
        overflow-y: auto;
    }

    .subject-type-section-header {
        display: block;
        margin: 12px 0 2px;
        color: var(--mud-palette-text-secondary);
    }

    .subject-type-row {
        display: flex;
        align-items: center;
        gap: 12px;
        width: 100%;
        padding: 8px;
        border: 0;
        border-top: 1px solid var(--mud-palette-lines-default);
        background: transparent;
        color: var(--mud-palette-text-primary);
        font: inherit;
        text-align: left;
        cursor: pointer;
    }

    .subject-type-row:hover,
    .subject-type-row:focus-visible {
        background: var(--mud-palette-action-default-hover);
    }

    .subject-type-row-text {
        display: flex;
        flex-direction: column;
        min-width: 0;
    }

    .subject-type-row-description {
        color: var(--mud-palette-text-secondary);
        font-size: 0.875rem;
    }

    .subject-type-empty {
        display: flex;
        flex-direction: column;
        align-items: flex-start;
        gap: 8px;
        padding: 24px 8px;
        color: var(--mud-palette-text-secondary);
    }

    @@media (max-width: 600px) {
        .subject-type-picker-body {
            flex-direction: column;
        }

        .subject-type-groups {
            flex-direction: row;
            width: auto;
            overflow-x: auto;
            overflow-y: hidden;
        }

        .subject-type-group {
            border: 1px solid var(--mud-palette-lines-default);
            border-radius: 16px;
            white-space: nowrap;
        }
    }
</style>

@code {
    /// <summary>
    /// The creatable types, in the order they are listed.
    /// </summary>
    [Parameter, EditorRequired]
    public IReadOnlyList<SubjectTypeDescriptor> Types { get; set; } = [];

    /// <summary>
    /// Raised when the user picks a type.
    /// </summary>
    [Parameter]
    public EventCallback<SubjectTypeDescriptor> OnTypeSelected { get; set; }

    private string _searchText = "";
    private string? _selectedGroup;
    private int _matchCount;
    private IReadOnlyList<SubjectTypeGroup> _groups = [];
    private List<IGrouping<string, SubjectTypeDescriptor>> _sections = [];

    protected override void OnParametersSet()
    {
        Refresh();
    }

    private void OnSearchChanged(string? searchText)
    {
        _searchText = searchText ?? "";
        Refresh();
    }

    private void SelectGroup(string? group)
    {
        _selectedGroup = group;
        Refresh();
    }

    private void ClearSearch()
    {
        _searchText = "";
        Refresh();
    }

    private void Refresh()
    {
        _groups = SubjectTypeCatalog.GetGroups(Types, _searchText);
        _matchCount = _groups.Sum(group => group.Count);
        _sections = SubjectTypeCatalog
            .Filter(Types, _searchText, _selectedGroup)
            .GroupBy(descriptor => descriptor.Group)
            .ToList();
    }

    private string GetGroupClass(string? group)
    {
        return string.Equals(group, _selectedGroup, StringComparison.Ordinal)
            ? "subject-type-group selected"
            : "subject-type-group";
    }
}
```

`@@media` is the Razor escape for a literal `@media`.

- [ ] **Step 6: Make the edit panel report its validity**

In `src/HomeBlaze/HomeBlaze.Components/Editors/SubjectEditPanel.razor`:

1. Wire the properties editor. Replace

```razor
            <ConfigurationPropertiesEditor @ref="_propertiesEditor" Subject="@Subject" />
```

with

```razor
            <ConfigurationPropertiesEditor @ref="_propertiesEditor"
                                           Subject="@Subject"
                                           IsValidChanged="OnPropertiesEditorIsValidChanged" />
```

2. Add the parameter below `IsCreating`, and the two fields below `_propertiesEditor`:

```csharp
    /// <summary>
    /// Raised with the current validity once the hosted editor is ready, and again whenever the validity changes.
    /// </summary>
    [Parameter] public EventCallback<bool> IsValidChanged { get; set; }
```

```csharp
    private bool? _reportedIsValid;

    private bool UsesSubjectComponent => Subject is not null &&
        ((IsCreating && ComponentRegistry.HasComponent(Subject.GetType(), SubjectComponentType.Setup)) ||
         ComponentRegistry.HasComponent(Subject.GetType(), SubjectComponentType.Edit));
```

3. Replace the `OnAfterRender` override with an async one that also reports. The subscription block is unchanged:

```csharp
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        await base.OnAfterRenderAsync(firstRender);

        // Subscribe to new edit component if available
        if (_subjectComponent is ISubjectEditComponent editComponent && _editComponent != editComponent)
        {
            if (_editComponent != null)
            {
                _editComponent.IsValidChanged -= OnIsValidChanged;
                _editComponent.IsDirtyChanged -= OnIsDirtyChanged;
            }

            _editComponent = editComponent;
            _editComponent.IsValidChanged += OnIsValidChanged;
            _editComponent.IsDirtyChanged += OnIsDirtyChanged;
            StateHasChanged();
        }

        await ReportIsValidAsync();
    }
```

4. Replace `OnIsValidChanged` and add the two new methods next to it:

```csharp
    private void OnIsValidChanged(bool isValid) => InvokeAsync(async () =>
    {
        StateHasChanged();
        await ReportIsValidAsync();
    });

    private Task OnPropertiesEditorIsValidChanged(bool isValid) => ReportIsValidAsync();

    private async Task ReportIsValidAsync()
    {
        // The hosted component is bound one render after this panel, and until then IsValid falls back
        // to true. Reporting that would briefly enable the parent's confirm button for an invalid form.
        if (UsesSubjectComponent && _subjectComponent is null)
        {
            return;
        }

        var isValid = IsValid;
        if (_reportedIsValid != isValid)
        {
            _reportedIsValid = isValid;
            await IsValidChanged.InvokeAsync(isValid);
        }
    }
```

`SubjectEditDialog` does not set `IsValidChanged` and is unaffected.

- [ ] **Step 7: Rewrite the dialog**

Replace the content of `src/HomeBlaze/HomeBlaze.Components/Dialogs/SubjectSetupDialog.razor`:

```razor
@using System.Buffers
@using HomeBlaze.Components.Editors

@inject SubjectTypeCatalog TypeCatalog
@inject SubjectFactory SubjectFactory
@inject RootManager RootManager
@inject ISnackbar Snackbar

<MudDialog data-testid="create-subject-wizard"
           Style="height: calc(100vh - 200px);"
           DefaultFocus="DefaultFocus.FirstChild">
    <TitleContent>
        <div style="display: flex; align-items: center; gap: 8px;">
            @if (_selectedType is not null)
            {
                <MudIconButton Icon="@Icons.Material.Filled.ArrowBack"
                               Size="Size.Small"
                               OnClick="GoBack"
                               aria-label="Back to type selection"
                               data-testid="back-button" />
            }
            <MudText Typo="Typo.h6" data-testid="create-dialog-title">@GetTitle()</MudText>
        </div>
    </TitleContent>
    <DialogContent>
        @* The picker stays in the tree while the configure page is shown, so its search and group survive "Back". *@
        <div hidden="@(_selectedType is not null)">
            <SubjectTypePicker Types="_types" OnTypeSelected="SelectType" />
        </div>
        @if (_selectedType is not null && _createdSubject is not null)
        {
            <MudStack Spacing="3">
                @if (Options.Name is { } nameOptions)
                {
                    <MudTextField T="string"
                                  Value="_subjectName"
                                  ValueChanged="OnNameChanged"
                                  Label="@nameOptions.Label"
                                  Required="true"
                                  Immediate="true"
                                  Adornment="@(nameOptions.Suffix.Length > 0 ? Adornment.End : Adornment.None)"
                                  AdornmentText="@nameOptions.Suffix"
                                  Error="@(_nameError is not null)"
                                  ErrorText="@_nameError"
                                  HelperText="Part of the subject's path, which is its ID. Avoid renaming it later."
                                  data-testid="create-name-input" />
                }
                <SubjectEditPanel @key="_createdSubject"
                                  @ref="_editPanel"
                                  Subject="_createdSubject"
                                  IsCreating="true"
                                  IsValidChanged="OnEditPanelIsValidChanged" />
            </MudStack>
        }
    </DialogContent>
    <DialogActions>
        <MudButton OnClick="Cancel"
                   data-testid="cancel-button">Cancel</MudButton>
        @if (_selectedType is not null)
        {
            <MudButton Color="Color.Primary"
                       Variant="Variant.Filled"
                       Disabled="@(!CanCreate)"
                       OnClick="Create"
                       data-testid="create-button">Create</MudButton>
        }
    </DialogActions>
</MudDialog>

@code {
    private static readonly SearchValues<char> InvalidFileNameCharacters =
        SearchValues.Create(Path.GetInvalidFileNameChars());

    [CascadingParameter]
    private IMudDialogInstance? MudDialog { get; set; }

    /// <summary>
    /// The options of the dialog.
    /// </summary>
    [Parameter]
    public SubjectSetupOptions Options { get; set; } = new();

    private IReadOnlyList<SubjectTypeDescriptor> _types = [];
    private SubjectTypeDescriptor? _selectedType;
    private IInterceptorSubject? _createdSubject;
    private SubjectEditPanel? _editPanel;
    private bool _isEditPanelValid;
    private string _subjectName = "";
    private string? _nameError;
    private bool _isNameEdited;

    private bool CanCreate => _createdSubject is not null && _nameError is null && _isEditPanelValid;

    protected override void OnInitialized()
    {
        IEnumerable<IInterceptorSubject> existingSubjects =
            RootManager.Root?.Context.TryGetService<ISubjectRegistry>()?.KnownSubjects.Keys
            ?? Enumerable.Empty<IInterceptorSubject>();

        _types = TypeCatalog.GetCreatableTypes(existingSubjects);
    }

    private string GetTitle()
    {
        return _selectedType is null ? "Create subject" : $"Create {_selectedType.DisplayName}";
    }

    private void SelectType(SubjectTypeDescriptor descriptor)
    {
        // The subject only backs the form. It is attached to nothing until Create, so a subject that is
        // replaced here or left behind by Cancel is simply dropped.
        _createdSubject = SubjectFactory.CreateSubject(descriptor.Type);
        _selectedType = descriptor;
        _isEditPanelValid = false;

        if (Options.Name is { } nameOptions && !_isNameEdited)
        {
            _subjectName = SubjectTypeCatalog.SuggestName(descriptor.Type, nameOptions.ExistingNames, nameOptions.Suffix);
        }

        _nameError = ValidateName(_subjectName);
        RefreshDialog();
    }

    private void GoBack()
    {
        _selectedType = null;
        _createdSubject = null;
        _editPanel = null;
        _isEditPanelValid = false;
        RefreshDialog();
    }

    private void OnNameChanged(string? name)
    {
        _subjectName = name ?? "";
        _isNameEdited = true;
        _nameError = ValidateName(_subjectName);
        RefreshDialog();
    }

    private void OnEditPanelIsValidChanged(bool isValid)
    {
        _isEditPanelValid = isValid;
        RefreshDialog();
    }

    private string? ValidateName(string name)
    {
        if (Options.Name is not { } nameOptions)
        {
            return null;
        }

        var trimmedName = name.Trim();
        if (trimmedName.Length == 0)
        {
            return "Name is required";
        }

        if (trimmedName.AsSpan().ContainsAny(InvalidFileNameCharacters))
        {
            return "Name contains invalid characters";
        }

        if (SubjectTypeCatalog.IsNameTaken(trimmedName, nameOptions.ExistingNames, nameOptions.Suffix))
        {
            return "A subject with this name already exists";
        }

        return null;
    }

    private void RefreshDialog()
    {
        // The title is rendered by the dialog container, which only follows an explicit refresh.
        StateHasChanged();
        MudDialog?.StateHasChanged();
    }

    private void Cancel()
    {
        MudDialog?.Cancel();
    }

    private async Task Create()
    {
        if (_createdSubject is null || _editPanel is null || !CanCreate)
        {
            return;
        }

        try
        {
            // Attaching can start a hosted subject right away, and it reads its configuration once it
            // starts, so the edits must be applied before BeforeSave attaches it.
            if (!await _editPanel.ApplyChangesAsync(CancellationToken.None))
            {
                return;
            }

            // Persisting finds its writer through the parents BeforeSave establishes.
            Options.BeforeSave?.Invoke(_createdSubject);

            await _editPanel.PersistAsync(CancellationToken.None);

            var name = Options.Name is null ? null : _subjectName.Trim();
            MudDialog?.Close(DialogResult.Ok(new CreateSubjectResult(_createdSubject, name)));
        }
        catch (Exception exception)
        {
            Snackbar.Add($"Failed to create subject: {exception.Message}", Severity.Error);
        }
    }

    /// <summary>
    /// Shows the create subject dialog and returns the result.
    /// </summary>
    /// <param name="dialogService">The dialog service.</param>
    /// <param name="options">The options, or null for a dialog that asks for no name.</param>
    /// <returns>The created subject and its name, or null if cancelled.</returns>
    public static async Task<CreateSubjectResult?> ShowAsync(
        IDialogService dialogService,
        SubjectSetupOptions? options = null)
    {
        var dialogOptions = new DialogOptions
        {
            MaxWidth = MaxWidth.Medium,
            FullWidth = true,
            CloseOnEscapeKey = true
        };

        var parameters = new DialogParameters<SubjectSetupDialog>
        {
            { x => x.Options, options ?? new SubjectSetupOptions() }
        };

        var dialog = await dialogService.ShowAsync<SubjectSetupDialog>("Create subject", parameters, dialogOptions);
        var result = await dialog.Result;

        if (result?.Canceled != false)
            return null;

        return result.Data as CreateSubjectResult;
    }
}
```

- [ ] **Step 8: Update the four callers**

`src/HomeBlaze/HomeBlaze.Components/Components/GridLayoutWidget.razor`, method `AddCellAt`:

```csharp
    private async Task AddCellAt(int row, int column)
    {
        await SubjectSetupDialog.ShowAsync(DialogService, new SubjectSetupOptions
        {
            BeforeSave = subject =>
            {
                if (Grid == null) return;

                var cell = SubjectFactory.CreateSubject<GridCell>();
                cell.Row = row;
                cell.Column = column;
                cell.Child = subject;

                // Replace list to trigger property setter and parent tracking
                Grid.Cells = [.. Grid.Cells, cell];
            }
        });
    }
```

`src/HomeBlaze/HomeBlaze.Components/Components/CanvasLayoutWidget.razor`, method `AddNodeAtPosition`:

```csharp
    private async Task AddNodeAtPosition(int x, int y)
    {
        x = SnapToGrid(x);
        y = SnapToGrid(y);

        await SubjectSetupDialog.ShowAsync(DialogService, new SubjectSetupOptions
        {
            BeforeSave = subject =>
            {
                if (Canvas == null) return;

                var node = SubjectFactory.CreateSubject<CanvasNode>();
                node.X = x;
                node.Y = y;
                node.Child = subject;

                // Replace list to trigger property setter and parent tracking
                Canvas.Nodes = [.. Canvas.Nodes, node];
            }
        });
    }
```

Both files need `@using HomeBlaze.Components.Dialogs` only if it is not already there. They call `SubjectSetupDialog` today, so it is.

`src/HomeBlaze/HomeBlaze.Storage.Blazor/BlazorSubjectSetupService.cs`, method `CreateSubjectAsync`:

```csharp
    public Task<CreateSubjectResult?> CreateSubjectAsync(SubjectSetupRequest request, CancellationToken cancellationToken)
    {
        return SubjectSetupDialog.ShowAsync(_dialogService, new SubjectSetupOptions
        {
            Name = new SubjectNameOptions("File name", FileExtensions.Json, request.ExistingNames)
        });
    }
```

`src/HomeBlaze/HomeBlaze.Storage.Blazor/Files/MarkdownFileEditComponent.razor`, the start of method `OnCreateClick`. Replace

```csharp
        // Open the SubjectSetupDialog
        var result = await SubjectSetupDialog.ShowAsync(DialogService);
        if (result == null)
            return;
```

with

```csharp
        // The block name is the subject's key in the page, so the names of the other blocks are taken.
        var existingNames = Regex.Matches(_currentContent, @"```subject\(([^)]+)\)")
            .Select(match => match.Groups[1].Value)
            .ToArray();

        var result = await SubjectSetupDialog.ShowAsync(DialogService, new SubjectSetupOptions
        {
            Name = new SubjectNameOptions("Name", "", existingNames)
        });

        if (result?.Name is null)
            return;
```

The file already uses `Regex`, so its using is present.

- [ ] **Step 9: Build**

Run: `dotnet build src/HomeBlaze/HomeBlaze`
Expected: build succeeds with 0 warnings and 0 errors.

- [ ] **Step 10: Run the E2E tests to verify they pass**

Run: `dotnet test src/HomeBlaze/HomeBlaze.E2E.Tests --filter "FullyQualifiedName~SubjectSetupDialogTests"`
Expected: PASS, 12 tests.

Known points to check if a test fails:
- `WhenTheDialogOpens_ThenThePickerIsShownWithoutANameField` fails on the focus assertion: the dialog focuses its container instead of the search field. Confirm `DefaultFocus="DefaultFocus.FirstChild"` is on `MudDialog` and `AutoFocus="true"` on the search field.
- A title assertion fails with the old title: a state change did not call `RefreshDialog()`.
- `WhenARequiredFieldIsEmpty_ThenCreateIsDisabled` fails because "Create" is enabled: `ReportIsValidAsync` ran before the Shelly edit component was bound. Confirm the `UsesSubjectComponent` guard.

- [ ] **Step 11: Run the remaining E2E tests**

Run: `dotnet test src/HomeBlaze/HomeBlaze.E2E.Tests`
Expected: PASS. `MarkdownEditorTests` and `PageEditTests` exercise the editor that hosts the changed embed button.

- [ ] **Step 12: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Components src/HomeBlaze/HomeBlaze.Storage.Blazor src/HomeBlaze/HomeBlaze.E2E.Tests
git commit -m "feat: pick the type first in the HomeBlaze create subject dialog"
```

---

### Task 6: Documentation and final verification

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze/Data/Files/Docs/development/building-subjects.md`
- Check: `src/HomeBlaze/HomeBlaze/Data/Files/Docs/architecture/design/testing.md`

- [ ] **Step 1: Document the create dialog metadata**

In `src/HomeBlaze/HomeBlaze/Data/Files/Docs/development/building-subjects.md`, insert this section directly before the heading `## Configuration Lifecycle`:

````markdown
## Create Dialog

Every subject type that implements `IConfigurable` is offered in the create dialog, which is opened from a storage folder, from the markdown editor and from the grid and canvas layouts. The dialog shows a searchable list of types with a group sidebar, and then the setup or edit component of the chosen type.

The dialog describes a type with attributes on the class. All of them are optional:

| Attribute | Purpose | Without it |
|-----------|---------|------------|
| `[DisplayName("Motor")]` | Title in the list and in the dialog title, in sentence case | Class name split into words |
| `[Description("...")]` | One sentence below the title, also searched | No description |
| `[Category("Samples")]` | Group in the sidebar | Group "Other", listed last |
| `[SubjectIcon("Settings")]` | Icon in the list, using the same names as `IIconProvider.IconName` | Default icon of the group |

`[DisplayName]`, `[Description]` and `[Category]` come from `System.ComponentModel`, `[SubjectIcon]` from `HomeBlaze.Abstractions.Attributes`.

```csharp
using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;

[Category("Samples")]
[DisplayName("Motor")]
[Description("Simulated motor with speed control and temperature monitoring")]
[SubjectIcon("Settings")]
[InterceptorSubject]
public partial class Motor : BackgroundService, IConfigurable
{
}
```

`[SubjectIcon]` is needed in addition to `IIconProvider` because the dialog has no instance to ask, and an instance's icon often depends on its state.

### Name of a New Subject

When a subject is created in a storage folder, the dialog asks for the file name on the configure page and suggests the class name, with a number appended when it is already used (`Motor`, `Motor2`). The file name is part of the subject's path, and the path is its ID, so it should not be changed later. A name that already exists in the folder is rejected.

### Hiding a Type

Two attributes from `HomeBlaze.Abstractions.Attributes` control whether a type is offered:

- `[ExcludeFromCreation]`: the type is never offered. Use it for a type that only exists as part of another subject, such as the cell of a grid.
- `[SingleInstance]`: the type is offered only while no instance of it exists in the subject graph.

```csharp
[InterceptorSubject]
[ExcludeFromCreation]
public partial class GridCell : IConfigurable
{
}
```
````

- [ ] **Step 2: Bring the existing mention of the wizard in line**

In the same file, in the list under "This pattern applies to:", replace

```markdown
- **Setup/creation wizards** (SubjectSetupDialog uses the same edit components with `IsCreating = true`)
```

with

```markdown
- **The create dialog** (SubjectSetupDialog hosts the same edit components with `IsCreating = true`)
```

- [ ] **Step 3: Add the attributes to the summary table**

In the same file, in the "## Summary" section, add these rows at the end of the first table (the one whose header is `| Attribute | Purpose | Persisted | Displayed |`):

```markdown
| `[DisplayName]`, `[Description]`, `[Category]`, `[SubjectIcon]` | Describe the type in the create dialog | No | In the create dialog |
| `[ExcludeFromCreation]`, `[SingleInstance]` | Hide the type from the create dialog | No | No |
```

- [ ] **Step 4: Check the testing design document**

Read the section "## UI End-to-End Tests" in `src/HomeBlaze/HomeBlaze/Data/Files/Docs/architecture/design/testing.md`. It names `SubjectSetupDialogTests` as the example of waiting for the automatic timezone label before a click that needs a Blazor event handler. The rewritten tests keep that class name and that wait in `OpenSubjectBrowserAsync`, so the sentence stays correct and the file needs no change. Edit it only if it says something that is no longer true.

- [ ] **Step 5: Verify the markdown rules**

Run: `grep -c $'\u2014' src/HomeBlaze/HomeBlaze/Data/Files/Docs/development/building-subjects.md`
Expected: the same count as before this task (the new text adds no em dash). Compare with `git show HEAD:src/HomeBlaze/HomeBlaze/Data/Files/Docs/development/building-subjects.md | grep -c $'\u2014'`.

- [ ] **Step 6: Full build and unit tests**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: build succeeds with 0 warnings and 0 errors.

Run: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`
Expected: PASS.

- [ ] **Step 7: Full HomeBlaze E2E run**

Run: `dotnet test src/HomeBlaze/HomeBlaze.E2E.Tests`
Expected: PASS.

- [ ] **Step 8: Look at the dialog in the running app**

Start the app from the repository root with `dotnet run --project src/HomeBlaze/HomeBlaze --no-launch-profile --urls http://localhost:5193` (use the browser preview configuration `homeblaze-create-dialog` if it is available), open `http://localhost:5193/browser` and click "Create". Check at 1300 pixels and at 375 pixels width:

- Every row has an icon that is not the generic article icon, a sentence case title and a readable (not pink) description.
- The groups are Clients, Components, Devices, History, Layouts, Samples, Servers, Storage. "Other" is absent, because every listed type has a category except the plugin manager, which the sample data already contains.
- At 375 pixels the groups form one horizontal row above the list.
- Picking "Shelly device" shows "File name" with `ShellyDevice` and `.json`, and "Create" is disabled until a host address is entered.

Fix what does not match, rerun Step 7 and include the fix in the commit of this task. Cancel the dialog without creating a subject.

- [ ] **Step 9: Commit**

```bash
git add src/HomeBlaze/HomeBlaze/Data/Files/Docs
git commit -m "docs: document the HomeBlaze create dialog metadata"
```
