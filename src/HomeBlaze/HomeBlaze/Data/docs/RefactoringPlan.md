# HomeBlaze v2 Refactoring Plan

This plan describes the steps to refactor HomeBlaze from the current structure to the target modular architecture.

## Current State

```
HomeBlaze/
+-- HomeBlaze.Abstractions/     (pure - keep as-is)
+-- HomeBlaze.Core/             (mixed - to be split)
+-- HomeBlaze.Storage/          (has MudBlazor - to be split)
+-- HomeBlaze/                  (monolithic - to be split)
+-- HomeBlaze.Samples/          (keep as-is)
+-- HomeBlaze.Tests/            (update references)
```

## Target State

```
HomeBlaze/
+-- HomeBlaze.Abstractions/     (unchanged)
+-- HomeBlaze.Services/         (new - from Core backend files)
+-- HomeBlaze.Services.UI/      (new - from Core UI files)
+-- HomeBlaze.Storage/          (cleaned - no MudBlazor)
+-- HomeBlaze.Storage.Blazor/   (new - storage UI)
+-- HomeBlaze.Host/           (new - from HomeBlaze components)
+-- HomeBlaze/                  (minimal host)
+-- HomeBlaze.Samples/          (unchanged)
+-- HomeBlaze.Services.Tests/   (new - tests for Services)
+-- HomeBlaze.Services.UI.Tests/ (new - tests for Services.UI)
+-- HomeBlaze.Storage.Tests/    (updated references)
```

---

## Phase 1: Create New Projects

### Step 1.1: Create HomeBlaze.Services

Create `HomeBlaze.Services/HomeBlaze.Services.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\HomeBlaze.Abstractions\HomeBlaze.Abstractions.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" Version="9.0.0" />
    <PackageReference Include="Namotion.Interceptor" Version="*" />
    <PackageReference Include="Namotion.Interceptor.Hosting" Version="*" />
    <PackageReference Include="Namotion.Interceptor.Registry" Version="*" />
    <PackageReference Include="Namotion.Interceptor.Tracking" Version="*" />
    <PackageReference Include="Namotion.Interceptor.Validation" Version="*" />
  </ItemGroup>
</Project>
```

### Step 1.2: Create HomeBlaze.Services.UI

Create `HomeBlaze.Services.UI/HomeBlaze.Services.UI.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\HomeBlaze.Services\HomeBlaze.Services.csproj" />
  </ItemGroup>
</Project>
```

### Step 1.3: Create HomeBlaze.Storage.Blazor

Create `HomeBlaze.Storage.Blazor/HomeBlaze.Storage.Blazor.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk.Razor">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\HomeBlaze.Storage\HomeBlaze.Storage.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="MudBlazor" Version="8.*" />
    <PackageReference Include="BlazorMonaco" Version="3.4.0" />
  </ItemGroup>
</Project>
```

### Step 1.4: Create HomeBlaze.Host

Create `HomeBlaze.Host/HomeBlaze.Host.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk.Razor">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\HomeBlaze.Services.UI\HomeBlaze.Services.UI.csproj" />
    <ProjectReference Include="..\HomeBlaze.Storage.Blazor\HomeBlaze.Storage.Blazor.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="MudBlazor" Version="8.*" />
    <PackageReference Include="Markdig" Version="0.*" />
  </ItemGroup>
</Project>
```

---

## Phase 2: Move Files from HomeBlaze.Core

### Step 2.1: Move to HomeBlaze.Services

Move these files and update namespace to `HomeBlaze.Services`:

| File | New Location |
|------|--------------|
| `RootManager.cs` | `HomeBlaze.Services/` |
| `ConfigurableSubjectSerializer.cs` | `HomeBlaze.Services/Serialization/` |
| `SubjectTypeRegistry.cs` | `HomeBlaze.Services/` |
| `SubjectContextFactory.cs` | `HomeBlaze.Services/` |
| `SubjectPathResolver.cs` | `HomeBlaze.Services/Navigation/` |
| `SubjectPathResolverExtensions.cs` | `HomeBlaze.Services/Navigation/` |
| `TypeProvider.cs` | `HomeBlaze.Services/Infrastructure/` |
| `ConfigurationWriterExtensions.cs` | `HomeBlaze.Services/` |
| `SubjectRegistryExtensions.cs` | `HomeBlaze.Services/` |

### Step 2.2: Move to HomeBlaze.Services.UI

Move these files and update namespace to `HomeBlaze.Services.UI`:

| File | New Location |
|------|--------------|
| `SubjectComponentRegistry.cs` | `HomeBlaze.Services.UI/Components/` |
| `SubjectComponentRegistration.cs` | `HomeBlaze.Services.UI/Components/` |
| `NavigationItemResolver.cs` | `HomeBlaze.Services.UI/Navigation/` |
| `NavigationItem.cs` | `HomeBlaze.Services.UI/Navigation/` |
| `RoutePathResolver.cs` | `HomeBlaze.Services.UI/Navigation/` |
| `DeveloperModeService.cs` | `HomeBlaze.Services.UI/` |
| `SubjectDisplayExtensions.cs` | `HomeBlaze.Services.UI/Display/` |
| `StateUnitExtensions.cs` | `HomeBlaze.Services.UI/Display/` |

### Step 2.3: Create DI Extension Methods

Create `HomeBlaze.Services/ServiceCollectionExtensions.cs`:

```csharp
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddHomeBlazeServices(this IServiceCollection services)
    {
        services.AddSingleton<RootManager>();
        services.AddSingleton<SubjectContextFactory>();
        services.AddSingleton<SubjectTypeRegistry>();
        services.AddSingleton<ConfigurableSubjectSerializer>();
        // ... other services
        return services;
    }
}
```

Create `HomeBlaze.Services.UI/ServiceCollectionExtensions.cs`:

```csharp
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddHomeBlazeUIServices(this IServiceCollection services)
    {
        services.AddHomeBlazeServices(); // include base services
        services.AddSingleton<SubjectComponentRegistry>();
        services.AddSingleton<NavigationItemResolver>();
        services.AddSingleton<RoutePathResolver>();
        services.AddSingleton<DeveloperModeService>();
        // ... other services
        return services;
    }
}
```

### Step 2.4: Delete HomeBlaze.Core

After all files are moved and building, delete:
- `HomeBlaze.Core/` folder
- Remove from solution file

---

## Phase 3: Split HomeBlaze.Storage

### Step 3.1: Move UI code to HomeBlaze.Storage.Blazor

Move these files:
- `Files/JsonFileEditComponent.razor`
- `Files/MarkdownFileEditComponent.razor`
- `Files/MarkdownFilePageComponent.razor`
- Any icon-related code using MudBlazor

### Step 3.2: Remove MudBlazor from HomeBlaze.Storage

Update `HomeBlaze.Storage.csproj`:
- Remove MudBlazor package reference
- Remove BlazorMonaco package reference
- Change SDK from `Microsoft.NET.Sdk.Razor` to `Microsoft.NET.Sdk`

### Step 3.3: Make Icons Configurable

In storage types that use icons:
- Remove hardcoded MudBlazor icon strings
- Use `IIconProvider` interface pattern
- Let `HomeBlaze.Storage.Blazor` provide icon mappings

### Step 3.4: Create DI Extension Method

Create `HomeBlaze.Storage/ServiceCollectionExtensions.cs`:

```csharp
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddHomeBlazeStorage(this IServiceCollection services)
    {
        services.AddSingleton<StorageHierarchyManager>();
        services.AddSingleton<FileSubjectFactory>();
        services.AddSingleton<StoragePathRegistry>();
        return services;
    }
}
```

Create `HomeBlaze.Storage.Blazor/ServiceCollectionExtensions.cs`:

```csharp
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddHomeBlazeStorageBlazor(this IServiceCollection services)
    {
        services.AddHomeBlazeStorage(); // include base storage
        // Register storage components
        return services;
    }
}
```

---

## Phase 4: Create HomeBlaze.Host

### Step 4.1: Move Blazor Components

Move from `HomeBlaze/` to `HomeBlaze.Host/`:

| File | New Location |
|------|--------------|
| `Components/HomeBlazorComponentBase.cs` | `HomeBlaze.Host/` |
| `Components/SubjectBrowser.razor` | `HomeBlaze.Host/Components/` |
| `Components/SubjectPropertyPanel.razor` | `HomeBlaze.Host/Components/` |
| `Components/SubjectEditDialog.razor` | `HomeBlaze.Host/Components/` |
| `Components/PropertyEditor.razor` | `HomeBlaze.Host/Components/` |
| `Components/ConfigurationPropertiesEditor.razor` | `HomeBlaze.Host/Components/` |
| `Components/NavMenu.razor` | `HomeBlaze.Host/Components/Navigation/` |
| `Components/NavFolder.razor` | `HomeBlaze.Host/Components/Navigation/` |
| `Components/Pages/Home.razor` | `HomeBlaze.Host/Pages/` |
| `Components/Pages/Browser.razor` | `HomeBlaze.Host/Pages/` |
| `Components/Pages/Error.razor` | `HomeBlaze.Host/Pages/` |
| `Components/Layout/` | `HomeBlaze.Host/Layout/` |

### Step 4.2: Create _Imports.razor

Create `HomeBlaze.Host/_Imports.razor`:

```razor
@using Microsoft.AspNetCore.Components.Web
@using Microsoft.AspNetCore.Components.Routing
@using MudBlazor
@using HomeBlaze.Host
@using HomeBlaze.Host.Components
@using HomeBlaze.Services
@using HomeBlaze.Services.UI
```

### Step 4.3: Create DI Extension Method

Create `HomeBlaze.Host/ServiceCollectionExtensions.cs`:

```csharp
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddHomeBlazeBlazor(this IServiceCollection services)
    {
        services.AddHomeBlazeUIServices();      // include UI services
        services.AddHomeBlazeStorageBlazor();   // include storage blazor
        // Register Blazor components
        return services;
    }

    public static IServiceCollection AddHomeBlaze(this IServiceCollection services)
    {
        return services.AddHomeBlazeBlazor();   // convenience method
    }
}
```

---

## Phase 5: Minimize HomeBlaze Host

### Step 5.1: Update HomeBlaze.csproj

Remove component-related packages, keep only:

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\HomeBlaze.Host\HomeBlaze.Host.csproj" />
    <ProjectReference Include="..\HomeBlaze.Samples\HomeBlaze.Samples.csproj" />
  </ItemGroup>
</Project>
```

### Step 5.2: Simplify Program.cs

```csharp
var builder = WebApplication.CreateBuilder(args);

// Single line registers everything
builder.Services.AddHomeBlaze();

// Or selective registration for custom scenarios
// builder.Services.AddHomeBlazeServices();
// builder.Services.AddHomeBlazeStorage();

var app = builder.Build();
// ... middleware configuration
app.Run();
```

### Step 5.3: Update App.razor

Reference components from `HomeBlaze.Host`:

```razor
@using HomeBlaze.Host
@using HomeBlaze.Host.Layout
```

---

## Phase 6: Update Solution and Tests

### Step 6.1: Update Solution File

Add new projects to `Namotion.Interceptor.sln`:

```
HomeBlaze.Services
HomeBlaze.Services.UI
HomeBlaze.Storage.Blazor
HomeBlaze.Host
HomeBlaze.Services.Tests
HomeBlaze.Services.UI.Tests
```

Remove:
```
HomeBlaze.Core
HomeBlaze.Tests (replaced by specific test projects)
```

### Step 6.2: Create Test Projects

Create `HomeBlaze.Services.Tests/HomeBlaze.Services.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\HomeBlaze.Services\HomeBlaze.Services.csproj" />
    <PackageReference Include="xunit" Version="2.*" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.*" />
  </ItemGroup>
</Project>
```

Create `HomeBlaze.Services.UI.Tests/HomeBlaze.Services.UI.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\HomeBlaze.Services.UI\HomeBlaze.Services.UI.csproj" />
    <PackageReference Include="xunit" Version="2.*" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.*" />
  </ItemGroup>
</Project>
```

### Step 6.3: Update Storage Tests

Update `HomeBlaze.Storage.Tests.csproj`:
- Ensure no UI dependencies in storage tests
- Add separate tests for `HomeBlaze.Storage.Blazor` if needed

---

## Phase 7: Verification

### Step 7.1: Build All Projects

```bash
dotnet build src/HomeBlaze/HomeBlaze.sln
```

### Step 7.2: Run Tests

```bash
dotnet test src/HomeBlaze/HomeBlaze.sln
```

### Step 7.3: Run Application

```bash
dotnet run --project src/HomeBlaze/HomeBlaze
```

### Step 7.4: Verify Modularity

Test each scenario:

1. **Headless**: Create test console app referencing only `HomeBlaze.Services`
2. **Custom UI**: Create test project referencing `HomeBlaze.Services.UI`
3. **Full App**: Verify existing `HomeBlaze` host works

---

## File Migration Summary

### From HomeBlaze.Core to HomeBlaze.Services (9 files)

- `RootManager.cs`
- `ConfigurableSubjectSerializer.cs`
- `SubjectTypeRegistry.cs`
- `SubjectContextFactory.cs`
- `SubjectPathResolver.cs`
- `SubjectPathResolverExtensions.cs`
- `TypeProvider.cs`
- `ConfigurationWriterExtensions.cs`
- `SubjectRegistryExtensions.cs`

### From HomeBlaze.Core to HomeBlaze.Services.UI (8 files)

- `SubjectComponentRegistry.cs`
- `SubjectComponentRegistration.cs`
- `NavigationItemResolver.cs`
- `NavigationItem.cs`
- `RoutePathResolver.cs`
- `DeveloperModeService.cs`
- `SubjectDisplayExtensions.cs`
- `StateUnitExtensions.cs`

### From HomeBlaze to HomeBlaze.Host (~15 files)

- All `.razor` files in Components/
- All `.razor` files in Components/Pages/
- All files in Components/Layout/
- `HomeBlazorComponentBase.cs`

### From HomeBlaze.Storage to HomeBlaze.Storage.Blazor (3 files)

- `Files/JsonFileEditComponent.razor`
- `Files/MarkdownFileEditComponent.razor`
- `Files/MarkdownFilePageComponent.razor`

---

## Namespace Changes

| Old Namespace | New Namespace |
|---------------|---------------|
| `HomeBlaze.Core` | `HomeBlaze.Services` |
| `HomeBlaze.Core.Components` | `HomeBlaze.Services.UI.Components` |
| `HomeBlaze.Core.UI` | `HomeBlaze.Services.UI.Display` |
| `HomeBlaze.Components` | `HomeBlaze.Host.Components` |
| `HomeBlaze.Pages` | `HomeBlaze.Host.Pages` |

---

## Complexity Summary

| Phase | Tasks | Complexity |
|-------|-------|------------|
| Phase 1 | Create 4 new projects | Low |
| Phase 2 | Move 17 files from Core + DI extensions | Medium |
| Phase 3 | Split Storage (3 razor files) | Low |
| Phase 4 | Move ~15 Blazor files + DI extensions | Medium |
| Phase 5 | Minimize host | Low |
| Phase 6 | Update solution/tests | Low |
| Phase 7 | Verification | Low |

**Total**: Medium complexity refactoring
