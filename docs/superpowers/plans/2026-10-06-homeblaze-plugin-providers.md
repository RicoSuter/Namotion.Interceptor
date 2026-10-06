# HomeBlaze Runtime Plugin Providers Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Plugin providers become ordinary subjects in the HomeBlaze tree that add plugin assemblies at runtime, files whose type arrives later upgrade in place, and the container image reports real library versions.

**Architecture:** `TypeProvider` gains batch adds, duplicate handling and a `TypesChanged` event. Type caches rebuild when `TypeProvider.Types` returns a new array instance. JSON files with an unresolvable `$type` load as `UnknownSubject`, and each `FluentStorageContainer` upgrades them on `TypesChanged`. `PluginManager` becomes `NuGetPluginProvider`, a `BackgroundService` subject owning its own `NuGetPluginLoader`. The fixed `PluginConfigurationPath` and the startup `PluginLoader` go away.

**Tech Stack:** .NET 10, C# 14, Namotion.Interceptor source generator, xUnit, Playwright E2E, GitHub Actions (PowerShell steps).

**Spec:** `docs/superpowers/specs/2026-10-06-homeblaze-plugin-providers-design.md`

---

## Conventions for every task

- Repository rules are in `AGENTS.md`: test names `When<Condition>_Then<Expected>`, explicit `// Arrange`, `// Act`, `// Assert` comments (`// Act & Assert` for exception tests), no `Task.Delay` waits in tests (use `AsyncTestHelpers.WaitUntilAsync`), no em dashes in docs, no hard wrapping in markdown, warnings are errors.
- Never add AI attribution to commits (no `Co-Authored-By`, no "Generated with").
- Comments explain only the why a reader cannot derive. XML docs state the contract.
- Commands run from the repository root unless noted.
- `src/HomeBlaze/Namotion.NuGet.Plugins` is a general-purpose library: its code, comments and tests never mention HomeBlaze.

## File Structure

| File | Change | Responsibility |
|------|--------|----------------|
| `src/HomeBlaze/Namotion.NuGet.Plugins/Loading/PackageExtractor.cs` | Modify | Atomic, concurrency-safe extraction into the cache |
| `src/HomeBlaze/Namotion.NuGet.Plugins.Tests/Loading/PackageExtractorTests.cs` | Create | Extraction tests |
| `src/HomeBlaze/HomeBlaze.Services/TypeProvider.cs` | Modify | Batch add, duplicates, `TypesChanged` |
| `src/HomeBlaze/HomeBlaze.Services/SubjectTypeRegistry.cs` | Modify | Snapshot cache keyed by the types array |
| `src/HomeBlaze/HomeBlaze.Services/Components/SubjectComponentRegistry.cs` | Modify | Snapshot cache keyed by the types array |
| `src/HomeBlaze/HomeBlaze.Services/ConfigurableSubjectSerializer.cs` | Modify | Options snapshot keyed by the types array |
| `src/HomeBlaze/HomeBlaze.Storage/Files/UnknownSubject.cs` | Create | Placeholder for JSON with an unresolvable `$type` |
| `src/HomeBlaze/HomeBlaze.Storage/Internal/FileSubjectFactory.cs` | Modify | JSON classification table |
| `src/HomeBlaze/HomeBlaze.Storage/Internal/StorageHierarchyManager.cs` | Modify | Stable child key for `UnknownSubject` |
| `src/HomeBlaze/HomeBlaze.Storage/Internal/StoragePathRegistry.cs` | Modify | Enumerate subjects of a type |
| `src/HomeBlaze/HomeBlaze.Storage/FluentStorageContainer.cs` | Modify | Upgrade lock, `TypesChanged` handling, recreate on change |
| `src/HomeBlaze/HomeBlaze.Storage.Blazor/Files/JsonFileEditComponent.razor` | Modify | Also edits `UnknownSubject` |
| `src/HomeBlaze/HomeBlaze.Plugins/NuGetPluginPaths.cs` | Create | Cache and feed path resolution |
| `src/HomeBlaze/HomeBlaze.Plugins/NuGetPluginProvider.cs` | Create (replaces `PluginManager.cs`) | The provider subject |
| `src/HomeBlaze/HomeBlaze.Plugins/Plugin.cs` | Modify | Parent type, async remove |
| `src/HomeBlaze/HomeBlaze.Plugins/PluginLoader.cs`, `PluginsServiceCollectionExtensions.cs`, `PluginConfiguration.cs`, `PluginManager.cs` | Delete | Replaced by the provider |
| `src/HomeBlaze/HomeBlaze.Plugins.Tests/*` | Modify/Create | Path, reconcile and integration tests |
| `src/HomeBlaze/HomeBlaze/Program.cs`, `HomeBlaze.Services/HomeBlazePaths.cs`, `HomeBlaze/HomeBlaze.csproj` | Modify | Remove fixed plugin loading |
| `Plugins.json` in Seed, dev data, E2E test data | Modify | `$type` rename, feeds |
| `src/HomeBlaze/HomeBlaze.E2E.Tests/*` | Modify/Create | Factory cleanup, upgrade E2E test |
| `src/Directory.Build.props`, `src/HomeBlaze/Directory.Build.props`, `.github/workflows/build.yml` | Modify | Library package version in the image |
| `src/HomeBlaze/HomeBlaze/Data/Files/Docs/**` | Modify | Documentation |

---

### Task 1: Atomic package extraction in Namotion.NuGet.Plugins

`ExtractToCache` creates the package directory before extracting into it. A concurrent extraction of the same package (two loaders sharing one cache) sees the directory and returns early with missing files, and a crash mid-extraction leaves a partial directory that every later start treats as complete. The persistent cache introduced by this feature makes the second case permanent.

**Files:**
- Modify: `src/HomeBlaze/Namotion.NuGet.Plugins/Loading/PackageExtractor.cs` (method `ExtractToCache`)
- Create: `src/HomeBlaze/Namotion.NuGet.Plugins.Tests/Loading/PackageExtractorTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using System.IO.Compression;
using Namotion.NuGet.Plugins.Loading;
using Xunit;

namespace Namotion.NuGet.Plugins.Tests.Loading;

public class PackageExtractorTests : IDisposable
{
    private readonly DirectoryInfo _cacheDirectory = Directory.CreateTempSubdirectory("package-extractor-");

    [Fact]
    public async Task WhenSamePackageIsExtractedConcurrently_ThenEveryCallSeesAllFiles()
    {
        // Arrange
        var package = CreatePackage(fileCount: 200);
        var extractor = new PackageExtractor(_cacheDirectory.FullName);

        // Act
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            extractor.ExtractAndGetAssemblyPaths("Test.Package", "1.0.0", new MemoryStream(package)))));

        // Assert
        Assert.All(results, paths => Assert.Equal(200, paths.Count));
    }

    [Fact]
    public void WhenExtractionFails_ThenNoPackageDirectoryIsLeftBehind()
    {
        // Arrange
        var extractor = new PackageExtractor(_cacheDirectory.FullName);
        var corruptPackage = new MemoryStream([1, 2, 3, 4]);

        // Act & Assert
        Assert.ThrowsAny<InvalidDataException>(() =>
            extractor.ExtractAndGetAssemblyPaths("Test.Package", "1.0.0", corruptPackage));
        Assert.Null(extractor.GetCachedPackagePath("Test.Package", "1.0.0"));
        Assert.Empty(Directory.GetDirectories(Path.Combine(_cacheDirectory.FullName, "Test.Package")));
    }

    private static byte[] CreatePackage(int fileCount)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (var index = 0; index < fileCount; index++)
            {
                var entry = archive.CreateEntry($"lib/net10.0/Assembly{index}.dll");
                using var writer = new StreamWriter(entry.Open());
                writer.Write(new string('x', 4096));
            }
        }

        return stream.ToArray();
    }

    public void Dispose()
    {
        _cacheDirectory.Delete(recursive: true);
    }
}
```

- [ ] **Step 2: Run the tests and verify they fail**

Run: `dotnet test src/HomeBlaze/Namotion.NuGet.Plugins.Tests --filter "FullyQualifiedName~PackageExtractorTests"`
Expected: `WhenSamePackageIsExtractedConcurrently_ThenEveryCallSeesAllFiles` fails (fewer than 200 paths or an `IOException`), `WhenExtractionFails_ThenNoPackageDirectoryIsLeftBehind` fails (the package directory exists). If the concurrency test passes by luck, rerun it a few times; it must fail at least once before the fix.

- [ ] **Step 3: Implement atomic extraction**

Replace `ExtractToCache` in `PackageExtractor.cs`:

```csharp
    private string ExtractToCache(string packageName, string packageVersion, Stream stream)
    {
        var packagePath = Path.Combine(_cacheDirectory, packageName, packageVersion);
        if (Directory.Exists(packagePath))
        {
            return packagePath;
        }

        // Extracted into a unique sibling and moved into place, so neither a concurrent extraction of the
        // same package nor an interrupted one can leave a partial directory that later loads treat as complete.
        var temporaryPath = $"{packagePath}.extracting-{Guid.NewGuid():N}";
        Directory.CreateDirectory(temporaryPath);
        try
        {
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                archive.ExtractToDirectory(temporaryPath);
            }

            try
            {
                Directory.Move(temporaryPath, packagePath);
            }
            catch (IOException) when (Directory.Exists(packagePath))
            {
                // Another extraction of the same package completed first; its copy is equivalent.
            }
        }
        finally
        {
            if (Directory.Exists(temporaryPath))
            {
                Directory.Delete(temporaryPath, recursive: true);
            }
        }

        return packagePath;
    }
```

- [ ] **Step 4: Run the tests and verify they pass**

Run: `dotnet test src/HomeBlaze/Namotion.NuGet.Plugins.Tests --filter "Category!=Integration"`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/HomeBlaze/Namotion.NuGet.Plugins/Loading/PackageExtractor.cs src/HomeBlaze/Namotion.NuGet.Plugins.Tests/Loading/PackageExtractorTests.cs
git commit -m "fix: extract plugin packages atomically into the cache"
```

---

### Task 2: TypeProvider batch add, duplicates and TypesChanged

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.Services/TypeProvider.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Services.Tests/TypeProviderTests.cs`

- [ ] **Step 1: Write the failing tests**

Append to `TypeProviderTests` (add `using System.Reflection;` and `using System.Reflection.Emit;`):

```csharp
    [Fact]
    public void WhenTypesAreAdded_ThenTypesChangedIsRaisedOnce()
    {
        // Arrange
        var provider = new TypeProvider();
        var raisedCount = 0;
        provider.TypesChanged += (_, _) => raisedCount++;

        // Act
        provider.AddTypes([typeof(string), typeof(int)]);

        // Assert
        Assert.Equal(1, raisedCount);
    }

    [Fact]
    public void WhenAllTypesAreAlreadyRegistered_ThenTypesChangedIsNotRaisedAndTypesInstanceIsKept()
    {
        // Arrange
        var provider = new TypeProvider();
        provider.AddTypes([typeof(string)]);
        var typesBefore = provider.Types;
        var raisedCount = 0;
        provider.TypesChanged += (_, _) => raisedCount++;

        // Act
        provider.AddTypes([typeof(string)]);

        // Assert
        Assert.Equal(0, raisedCount);
        Assert.Same(typesBefore, provider.Types);
        Assert.Single(provider.Types);
    }

    [Fact]
    public void WhenTypesAreAdded_ThenTypesReturnsNewInstance()
    {
        // Arrange
        var provider = new TypeProvider();
        var typesBefore = provider.Types;

        // Act
        provider.AddTypes([typeof(string)]);

        // Assert
        Assert.NotSame(typesBefore, provider.Types);
    }

    [Fact]
    public void WhenDifferentTypeHasSameFullName_ThenItIsSkippedAndReturned()
    {
        // Arrange
        var provider = new TypeProvider();
        var firstType = CreateDynamicType("FirstAssembly", "Duplicate.Name.Device");
        var secondType = CreateDynamicType("SecondAssembly", "Duplicate.Name.Device");
        provider.AddTypes([firstType]);

        // Act
        var skippedTypes = provider.AddTypes([secondType]);

        // Assert
        Assert.Equal([secondType], skippedTypes);
        Assert.Contains(firstType, provider.Types);
        Assert.DoesNotContain(secondType, provider.Types);
    }

    [Fact]
    public void WhenAssembliesAreAdded_ThenTypesChangedIsRaisedOnce()
    {
        // Arrange
        var provider = new TypeProvider();
        var raisedCount = 0;
        provider.TypesChanged += (_, _) => raisedCount++;

        // Act
        provider.AddAssemblies([typeof(TypeProvider).Assembly, typeof(TypeProviderTests).Assembly]);

        // Assert
        Assert.Equal(1, raisedCount);
        Assert.Contains(typeof(TypeProvider), provider.Types);
        Assert.Contains(typeof(TypeProviderTests), provider.Types);
    }

    private static Type CreateDynamicType(string assemblyName, string typeName)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(assemblyName), AssemblyBuilderAccess.Run);
        return assembly.DefineDynamicModule(assemblyName).DefineType(typeName, TypeAttributes.Public).CreateType();
    }
```

- [ ] **Step 2: Run the tests and verify they fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Services.Tests --filter "FullyQualifiedName~TypeProviderTests"`
Expected: compile errors (`TypesChanged`, `AddAssemblies` missing, `AddTypes` returns void).

- [ ] **Step 3: Implement**

Replace the body of `TypeProvider.cs`:

```csharp
using System.Reflection;

namespace HomeBlaze.Services;

/// <summary>
/// Central provider for types from assemblies. Used by registries for lazy scanning.
/// </summary>
public class TypeProvider
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Type> _typesByFullName = new(StringComparer.Ordinal);

    // Copy-on-write: readers take the current array without a lock, writers publish a replacement.
    // Registration is a short burst at startup while the lookups below run for the life of the process
    // and sit on request paths, so the cost belongs on the write side. Publishing a new array also means
    // a reader that is midway through an enumeration keeps walking the snapshot it started on.
    private Type[] _types = [];

    /// <summary>
    /// Gets all collected types. Returns the same instance until types are added, so callers can cache
    /// data derived from it per instance.
    /// </summary>
    public IReadOnlyCollection<Type> Types => Volatile.Read(ref _types);

    /// <summary>
    /// Raised after types were added, outside the registration lock.
    /// </summary>
    public event EventHandler? TypesChanged;

    /// <summary>
    /// Adds exported types from an assembly.
    /// </summary>
    public TypeProvider AddAssembly(Assembly assembly)
    {
        AddAssemblies([assembly]);
        return this;
    }

    /// <summary>
    /// Adds exported types from the assemblies and raises <see cref="TypesChanged"/> once when any type was added.
    /// </summary>
    /// <returns>Types skipped because a different type with the same full name is already registered.</returns>
    public IReadOnlyList<Type> AddAssemblies(IEnumerable<Assembly> assemblies)
    {
        return AddTypes(assemblies.SelectMany(GetExportedTypes));
    }

    /// <summary>
    /// Adds types directly and raises <see cref="TypesChanged"/> once when any type was added.
    /// Already registered types are ignored.
    /// </summary>
    /// <returns>Types skipped because a different type with the same full name is already registered.</returns>
    public IReadOnlyList<Type> AddTypes(IEnumerable<Type> types)
    {
        var candidateTypes = types as Type[] ?? types.ToArray();
        List<Type>? skippedTypes = null;
        var addedCount = 0;

        lock (_lock)
        {
            var addedTypes = new List<Type>(candidateTypes.Length);
            foreach (var type in candidateTypes)
            {
                var fullName = type.FullName ?? type.Name;
                if (_typesByFullName.TryGetValue(fullName, out var registeredType))
                {
                    if (registeredType != type)
                    {
                        (skippedTypes ??= []).Add(type);
                    }

                    continue;
                }

                _typesByFullName.Add(fullName, type);
                addedTypes.Add(type);
            }

            if (addedTypes.Count > 0)
            {
                var existingTypes = _types;
                var combinedTypes = new Type[existingTypes.Length + addedTypes.Count];

                existingTypes.CopyTo(combinedTypes, 0);
                addedTypes.CopyTo(combinedTypes, existingTypes.Length);

                Volatile.Write(ref _types, combinedTypes);
            }

            addedCount = addedTypes.Count;
        }

        if (addedCount > 0)
        {
            TypesChanged?.Invoke(this, EventArgs.Empty);
        }

        return (IReadOnlyList<Type>?)skippedTypes ?? [];
    }

    private static Type[] GetExportedTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetExportedTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.Where(type => type is not null).ToArray()!;
        }
    }
}
```

- [ ] **Step 4: Build the solution and run the HomeBlaze unit tests**

Run: `dotnet build src/Namotion.Interceptor.slnx` then `dotnet test src/HomeBlaze/HomeBlaze.Services.Tests`
Expected: build succeeds (callers that ignored the old `void` result still compile), all tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Services/TypeProvider.cs src/HomeBlaze/HomeBlaze.Services.Tests/TypeProviderTests.cs
git commit -m "feat: add batch registration, duplicate handling and a change event to the HomeBlaze type provider"
```

---

### Task 3: Type caches rebuild when types are added

Each cache keeps the `TypeProvider.Types` instance it was built from and rebuilds when a different instance comes back. Concurrent rebuilds may duplicate work but never keep a stale result, because a stale snapshot fails the reference check on the next access.

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.Services/SubjectTypeRegistry.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Services/Components/SubjectComponentRegistry.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Services/ConfigurableSubjectSerializer.cs`
- Create: `src/HomeBlaze/HomeBlaze.Services.Tests/SubjectTypeRegistryTests.cs` (if a file of that name exists, append to it)
- Modify: `src/HomeBlaze/HomeBlaze.Services.Tests/SubjectComponentRegistryTests.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Services.Tests/Serialization/ConfigurableSubjectSerializerTests.cs`

- [ ] **Step 1: Write the failing tests**

`SubjectTypeRegistryTests.cs`:

```csharp
using HomeBlaze.Services.Tests.Models;

namespace HomeBlaze.Services.Tests;

public class SubjectTypeRegistryTests
{
    [Fact]
    public void WhenTypeIsAddedAfterFirstLookup_ThenItResolves()
    {
        // Arrange
        var typeProvider = new TypeProvider();
        var registry = new SubjectTypeRegistry(typeProvider);
        Assert.Null(registry.ResolveType(typeof(TestContainer).FullName!));

        // Act
        typeProvider.AddTypes([typeof(TestContainer)]);

        // Assert
        Assert.Equal(typeof(TestContainer), registry.ResolveType(typeof(TestContainer).FullName!));
        Assert.Contains(typeof(TestContainer), registry.RegisteredTypes);
    }
}
```

Append to `SubjectComponentRegistryTests`:

```csharp
    [Fact]
    public void WhenComponentTypeIsAddedAfterFirstLookup_ThenItResolves()
    {
        // Arrange
        var typeProvider = new TypeProvider();
        var registry = new SubjectComponentRegistry(typeProvider);
        Assert.Null(registry.GetComponent(typeof(TestSubject), SubjectComponentType.Edit));

        // Act
        typeProvider.AddTypes([typeof(TestEditComponent)]);

        // Assert
        var component = registry.GetComponent(typeof(TestSubject), SubjectComponentType.Edit);
        Assert.NotNull(component);
        Assert.Equal(typeof(TestEditComponent), component.ComponentType);
    }
```

Append to `ConfigurableSubjectSerializerTests` (it uses its own `TypeProvider` with registered types, so this test creates a separate one):

```csharp
    [Fact]
    public void WhenSubjectTypeIsAddedAfterFirstUse_ThenItSerializesAndDeserializes()
    {
        // Arrange
        var typeProvider = new TypeProvider();
        typeProvider.AddTypes([typeof(TestSubject)]);
        var serializer = new ConfigurableSubjectSerializer(typeProvider, new ServiceCollection().BuildServiceProvider());
        var context = InterceptorSubjectContext.Create();
        serializer.Serialize(new TestSubject(context));

        // Act
        typeProvider.AddTypes([typeof(ParentSubject)]);
        var json = serializer.Serialize(new ParentSubject(context));
        var deserialized = serializer.Deserialize(json);

        // Assert
        Assert.Contains(typeof(ParentSubject).FullName!, json);
        Assert.IsType<ParentSubject>(deserialized);
    }
```

- [ ] **Step 2: Run the tests and verify they fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Services.Tests --filter "FullyQualifiedName~WhenTypeIsAddedAfterFirstLookup|FullyQualifiedName~WhenComponentTypeIsAddedAfterFirstLookup|FullyQualifiedName~WhenSubjectTypeIsAddedAfterFirstUse"`
Expected: all three fail (null resolution, null component, `NotSupportedException` for the unknown derived type).

- [ ] **Step 3: Implement SubjectTypeRegistry snapshot**

Replace the two `Lazy` fields and their uses in `SubjectTypeRegistry.cs`:

```csharp
public class SubjectTypeRegistry
{
    private readonly TypeProvider _typeProvider;
    private Snapshot? _snapshot;

    public SubjectTypeRegistry(TypeProvider typeProvider)
    {
        _typeProvider = typeProvider;
    }

    /// <summary>
    /// Gets all registered subject types.
    /// </summary>
    public IReadOnlyCollection<Type> RegisteredTypes => GetSnapshot().TypesByName.Values.Distinct().ToList();

    /// <summary>
    /// Gets all registered file extension mappings.
    /// </summary>
    public IReadOnlyDictionary<string, Type> ExtensionMappings => GetSnapshot().TypesByExtension;

    // ResolveType, ResolveTypeForExtension, IsRegistered and HasExtensionMapping keep their bodies but read
    // `var snapshot = GetSnapshot();` once and use snapshot.TypesByName / snapshot.TypesByExtension
    // instead of _typesByName.Value / _typesByExtension.Value.

    private Snapshot GetSnapshot()
    {
        var types = _typeProvider.Types;
        var snapshot = Volatile.Read(ref _snapshot);
        if (snapshot is null || !ReferenceEquals(snapshot.Source, types))
        {
            snapshot = new Snapshot(types, ScanTypes(types), ScanExtensions(types));
            Volatile.Write(ref _snapshot, snapshot);
        }

        return snapshot;
    }

    // ScanTypes and ScanExtensions take the types collection as a parameter instead of reading _typeProvider.Types.

    private sealed record Snapshot(
        IReadOnlyCollection<Type> Source,
        ConcurrentDictionary<string, Type> TypesByName,
        ConcurrentDictionary<string, Type> TypesByExtension);
}
```

Write out every method body in full in the file; the comments above only describe the mechanical change.

- [ ] **Step 4: Implement SubjectComponentRegistry snapshot**

In `SubjectComponentRegistry.cs`, replace `_components` and `_resolvedCache` (and remove the TODO about clearing the cache):

```csharp
    private readonly TypeProvider _typeProvider;
    private Snapshot? _snapshot;

    public SubjectComponentRegistry(TypeProvider typeProvider)
    {
        _typeProvider = typeProvider;
    }

    public SubjectComponentRegistration? GetComponent(Type subjectType, SubjectComponentType type, string? name = null)
    {
        var snapshot = GetSnapshot();
        return snapshot.ResolvedCache.GetOrAdd((subjectType, type, name),
            key => ResolveComponent(snapshot.Components, key.Item1, key.Item2, key.Item3));
    }

    private Snapshot GetSnapshot()
    {
        var types = _typeProvider.Types;
        var snapshot = Volatile.Read(ref _snapshot);
        if (snapshot is null || !ReferenceEquals(snapshot.Source, types))
        {
            snapshot = new Snapshot(types, LoadComponents(types), new());
            Volatile.Write(ref _snapshot, snapshot);
        }

        return snapshot;
    }

    private sealed record Snapshot(
        IReadOnlyCollection<Type> Source,
        Dictionary<(Type SubjectType, SubjectComponentType Type, string? Name), SubjectComponentRegistration> Components,
        ConcurrentDictionary<(Type, SubjectComponentType, string?), SubjectComponentRegistration?> ResolvedCache);
```

`ResolveComponent` takes the components dictionary as its first parameter instead of reading `_components.Value`. `GetComponents`, `HasComponent` and `GetAllComponents` read `GetSnapshot().Components`. `LoadComponents` takes the types collection as a parameter.

- [ ] **Step 5: Implement the serializer options snapshot**

In `ConfigurableSubjectSerializer.cs`, replace the `_options` field and its constructor initialization:

```csharp
    private readonly TypeProvider _typeProvider;
    private readonly IServiceProvider _serviceProvider;
    private OptionsSnapshot? _optionsSnapshot;

    public ConfigurableSubjectSerializer(TypeProvider typeProvider, IServiceProvider serviceProvider)
    {
        _typeProvider = typeProvider;
        _serviceProvider = serviceProvider;
    }

    // System.Text.Json freezes the polymorphic $type list of an options instance on first use, so types
    // added later need a new instance.
    private JsonSerializerOptions Options
    {
        get
        {
            var types = _typeProvider.Types;
            var snapshot = Volatile.Read(ref _optionsSnapshot);
            if (snapshot is null || !ReferenceEquals(snapshot.Source, types))
            {
                snapshot = new OptionsSnapshot(types, new JsonSerializerOptions
                {
                    TypeInfoResolver = new ConfigurationJsonTypeInfoResolver(_typeProvider),
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = true,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                    Converters = { new JsonStringEnumConverter() }
                });
                Volatile.Write(ref _optionsSnapshot, snapshot);
            }

            return snapshot.Options;
        }
    }

    private sealed record OptionsSnapshot(IReadOnlyCollection<Type> Source, JsonSerializerOptions Options);
```

Replace every `_options` use with `Options`. In `PopulateConfigurationProperties`, read `var options = Options;` once before the loops and use `options` inside them, so one deserialization uses one options instance.

- [ ] **Step 6: Run the HomeBlaze unit tests**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Services.Tests` and `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests` and `dotnet test src/HomeBlaze/HomeBlaze.AI.Tests`
Expected: all pass.

- [ ] **Step 7: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Services src/HomeBlaze/HomeBlaze.Services.Tests
git commit -m "feat: refresh HomeBlaze type caches when types are added"
```

---

### Task 4: UnknownSubject and JSON classification

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Storage/Files/UnknownSubject.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Storage/Internal/FileSubjectFactory.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Storage/Internal/StorageHierarchyManager.cs` (`GetChildKey`)
- Modify: `src/HomeBlaze/HomeBlaze.Storage.Blazor/Files/JsonFileEditComponent.razor`
- Modify: `src/HomeBlaze/HomeBlaze.Storage.Tests/HomeBlaze.Storage.Tests.csproj` (generator analyzer and `Namotion.Interceptor.Testing` reference)
- Create: `src/HomeBlaze/HomeBlaze.Storage.Tests/ThrowingSubject.cs`
- Create: `src/HomeBlaze/HomeBlaze.Storage.Tests/FileSubjectFactoryJsonTests.cs`

- [ ] **Step 1: Add the test project references**

In `HomeBlaze.Storage.Tests.csproj`, add to the project reference item group:

```xml
    <ProjectReference Include="..\..\Namotion.Interceptor.Generator\Namotion.Interceptor.Generator.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
    <ProjectReference Include="..\..\Namotion.Interceptor.Testing\Namotion.Interceptor.Testing.csproj" />
```

- [ ] **Step 2: Add a subject whose construction throws**

`ThrowingSubject.cs`:

```csharp
using HomeBlaze.Abstractions;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Storage.Tests;

[InterceptorSubject]
public partial class ThrowingSubject : IConfigurable
{
    public ThrowingSubject()
    {
        throw new InvalidOperationException("Device driver is broken.");
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
```

- [ ] **Step 3: Write the failing tests**

`FileSubjectFactoryJsonTests.cs` (scans a temp directory through `FluentStorageContainer`, following `FluentStorageContainerPathTests`):

```csharp
using HomeBlaze.Samples;
using HomeBlaze.Services;
using HomeBlaze.Storage.Files;
using HomeBlaze.Storage.Internal;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Tests;

public class FileSubjectFactoryJsonTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("homeblaze-json-");

    [Fact]
    public async Task WhenTypeResolves_ThenSubjectIsCreated()
    {
        // Arrange
        WriteFile("Motor1.json", """{ "$type": "HomeBlaze.Samples.Motor" }""");
        using var storage = CreateStorage(typeof(Motor).Assembly.GetExportedTypes());

        // Act
        await storage.ConnectAsync(CancellationToken.None);

        // Assert
        Assert.IsType<Motor>(storage.Children["Motor1"]);
    }

    [Fact]
    public async Task WhenTypeDoesNotResolve_ThenUnknownSubjectIsCreatedUnderKeyWithoutExtension()
    {
        // Arrange
        WriteFile("Sensor1.json", """{ "$type": "MyCompany.Sensor", "name": "Kitchen" }""");
        using var storage = CreateStorage([]);

        // Act
        await storage.ConnectAsync(CancellationToken.None);

        // Assert
        var unknown = Assert.IsType<UnknownSubject>(storage.Children["Sensor1"]);
        Assert.Equal("MyCompany.Sensor", unknown.TypeName);
        Assert.Equal(UnknownSubject.TypeNotLoadedReason, unknown.Reason);
    }

    [Fact]
    public async Task WhenConstructionThrows_ThenUnknownSubjectHasErrorAsReason()
    {
        // Arrange
        WriteFile("Broken.json", $$"""{ "$type": "{{typeof(ThrowingSubject).FullName}}" }""");
        using var storage = CreateStorage([typeof(ThrowingSubject)]);

        // Act
        await storage.ConnectAsync(CancellationToken.None);

        // Assert
        var unknown = Assert.IsType<UnknownSubject>(storage.Children["Broken"]);
        Assert.Contains("Device driver is broken.", unknown.Reason);
    }

    [Theory]
    [InlineData("""{ "name": "plain data" }""")]
    [InlineData("""[1, 2, 3]""")]
    [InlineData("""{ not json""")]
    public async Task WhenJsonHasNoType_ThenJsonFileIsCreated(string content)
    {
        // Arrange
        WriteFile("Data.json", content);
        using var storage = CreateStorage([]);

        // Act
        await storage.ConnectAsync(CancellationToken.None);

        // Assert
        Assert.IsType<JsonFile>(storage.Children["Data.json"]);
    }

    private void WriteFile(string relativePath, string content)
    {
        var path = Path.Combine(_directory.FullName, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private FluentStorageContainer CreateStorage(IEnumerable<Type> types)
    {
        var typeProvider = new TypeProvider();
        typeProvider.AddTypes(types);
        var typeRegistry = new SubjectTypeRegistry(typeProvider);

        var services = new ServiceCollection();
        services.AddSingleton(typeProvider);
        services.AddSingleton(typeRegistry);
        services.AddSingleton<IInterceptorSubjectContext>(InterceptorSubjectContext.Create());
        services.AddSingleton<ConfigurableSubjectSerializer>();
        services.AddSingleton<MarkdownContentParser>();
        var serviceProvider = services.BuildServiceProvider();

        return new FluentStorageContainer(typeRegistry, serviceProvider.GetRequiredService<ConfigurableSubjectSerializer>(), serviceProvider)
        {
            ConnectionString = _directory.FullName,
            EnableFileWatching = false
        };
    }

    public void Dispose()
    {
        _directory.Delete(recursive: true);
    }
}
```

If `Motor` needs services that `CreateStorage` does not register, register them the way `FluentStorageContainerTests.CreateDependencies` does. If the object initializer does not compile for the partial properties, set the two properties after construction.

- [ ] **Step 4: Run the tests and verify they fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests --filter "FullyQualifiedName~FileSubjectFactoryJsonTests"`
Expected: compile error, `UnknownSubject` does not exist.

- [ ] **Step 5: Create UnknownSubject**

`UnknownSubject.cs`:

```csharp
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Storage.Abstractions;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Storage.Files;

/// <summary>
/// Stands in for a JSON file whose <c>$type</c> cannot be created, either because the type is not loaded
/// or because creating it failed. The file is never written by the configuration writer, and the storage
/// replaces this subject with the real one once its type can be created.
/// </summary>
[InterceptorSubject]
public partial class UnknownSubject : IStorageFile, ITitleProvider, IIconProvider
{
    /// <summary>
    /// The reason used when no loaded type has the file's <c>$type</c> name.
    /// </summary>
    public const string TypeNotLoadedReason = "Type is not loaded.";

    public string? Title => Path.GetFileNameWithoutExtension(FullPath);

    public string IconName => "Warning";

    public string IconColor => "Warning";

    public IStorageContainer Storage { get; }

    public string FullPath { get; }

    public string Name { get; }

    /// <summary>
    /// The <c>$type</c> value of the file.
    /// </summary>
    [State("Type", Position = 1)]
    public partial string TypeName { get; internal set; }

    /// <summary>
    /// Why the file is not the real subject.
    /// </summary>
    [State(Position = 2)]
    public partial string Reason { get; internal set; }

    [State("Size", Position = 3)]
    public partial long FileSize { get; set; }

    [State("Modified", Position = 4)]
    public partial DateTime LastModified { get; set; }

    public UnknownSubject(IStorageContainer storage, string fullPath, string typeName, string reason)
    {
        Storage = storage;
        FullPath = fullPath;
        Name = Path.GetFileName(fullPath);
        TypeName = typeName;
        Reason = reason;
    }

    public Task<Stream> ReadAsync(CancellationToken cancellationToken)
        => Storage.ReadBlobAsync(FullPath, cancellationToken);

    public Task WriteAsync(Stream content, CancellationToken cancellationToken)
        => Storage.WriteBlobAsync(FullPath, content, cancellationToken);

    // The storage recreates the subject on a file change, so there is no in-memory content to refresh.
    public Task OnFileChangedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
```

Check `IStorageFile` and `JsonFile` for any member this does not implement (for example a property the interface declares) and match `JsonFile`'s implementation of it.

- [ ] **Step 6: Classify JSON in FileSubjectFactory**

Replace `CreateFromJsonBlobAsync` (add `using System.Text.Json;`):

```csharp
    private async Task<IInterceptorSubject?> CreateFromJsonBlobAsync(
        IBlobStorage client,
        IStorageContainer storage,
        Blob blob,
        CancellationToken cancellationToken)
    {
        var json = await client.ReadTextAsync(blob.FullPath, cancellationToken: cancellationToken);
        var typeName = TryReadTypeName(json);
        if (typeName is null)
        {
            return new JsonFile(storage, blob.FullPath);
        }

        string reason;
        try
        {
            var subject = _serializer.Deserialize(json);
            if (subject != null)
            {
                // All IConfigurable implementations are also IInterceptorSubject (via [InterceptorSubject] attribute)
                return (IInterceptorSubject)subject;
            }

            reason = UnknownSubject.TypeNotLoadedReason;
        }
        catch (Exception exception)
        {
            _logger?.LogError(exception, "Failed to create subject of type {Type} from: {Path}", typeName, blob.FullPath);
            reason = (exception.InnerException ?? exception).Message;
        }

        var unknownSubject = new UnknownSubject(storage, blob.FullPath, typeName, reason);
        UpdateFileMetadata(unknownSubject, blob);
        return unknownSubject;
    }

    private static string? TryReadTypeName(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("$type", out var typeElement) &&
                   typeElement.ValueKind == JsonValueKind.String &&
                   !string.IsNullOrWhiteSpace(typeElement.GetString())
                ? typeElement.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
```

`ActivatorUtilities` wraps constructor exceptions in `TargetInvocationException`, which is why the reason prefers the inner exception's message.

- [ ] **Step 7: Keep the placeholder's child key stable**

In `StorageHierarchyManager.GetChildKey`, change the condition so an `UnknownSubject` gets the key without extension (add `using HomeBlaze.Storage.Files;`):

```csharp
        // A placeholder takes the key of the subject it stands in for, so its path survives the upgrade.
        if (subject is IConfigurable or UnknownSubject &&
            Path.GetExtension(fullPath).Equals(FileExtensions.Json, StringComparison.OrdinalIgnoreCase))
```

- [ ] **Step 8: Let the JSON editor edit placeholders**

In `JsonFileEditComponent.razor`, add a second attribute and widen the file accessor:

```razor
@attribute [SubjectComponent(SubjectComponentType.Edit, typeof(JsonFile))]
@attribute [SubjectComponent(SubjectComponentType.Edit, typeof(UnknownSubject))]
```

```csharp
    private IStorageFile? File => Subject as IStorageFile;
```

Add `@using HomeBlaze.Storage.Abstractions` if `IStorageFile` is not already in scope.

- [ ] **Step 9: Run the storage tests**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests` and `dotnet build src/HomeBlaze/HomeBlaze.Storage.Blazor`
Expected: all pass, build succeeds.

- [ ] **Step 10: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Storage src/HomeBlaze/HomeBlaze.Storage.Blazor src/HomeBlaze/HomeBlaze.Storage.Tests
git commit -m "feat: load JSON files with an unknown HomeBlaze type as UnknownSubject"
```

---

### Task 5: Storage upgrades placeholders when types arrive

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.Storage/Internal/StoragePathRegistry.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Storage/FluentStorageContainer.cs`
- Create: `src/HomeBlaze/HomeBlaze.Storage.Tests/FluentStorageContainerUpgradeTests.cs`

- [ ] **Step 1: Write the failing tests**

`FluentStorageContainerUpgradeTests.cs` (same `CreateStorage`, `WriteFile` and `Dispose` helpers as `FileSubjectFactoryJsonTests`, except `CreateStorage` also returns the `TypeProvider`; copy them rather than sharing, the helpers are small):

```csharp
    [Fact]
    public async Task WhenTypeIsAddedLater_ThenUnknownSubjectIsUpgradedAtSamePath()
    {
        // Arrange
        WriteFile("Devices/Motor1.json", """{ "$type": "HomeBlaze.Samples.Motor" }""");
        var (storage, typeProvider) = CreateStorage();
        using var _ = storage;
        await storage.ConnectAsync(CancellationToken.None);
        var folder = Assert.IsType<VirtualFolder>(storage.Children["Devices"]);
        Assert.IsType<UnknownSubject>(folder.Children["Motor1"]);

        // Act
        typeProvider.AddAssembly(typeof(Motor).Assembly);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() =>
            ((VirtualFolder)storage.Children["Devices"]).Children.TryGetValue("Motor1", out var subject) && subject is Motor);
    }

    [Fact]
    public async Task WhenUnknownSubjectIsRewrittenWithKnownType_ThenItIsRecreated()
    {
        // Arrange
        WriteFile("Motor1.json", """{ "$type": "HomeBlaze.Samples.Motorr" }""");
        var (storage, typeProvider) = CreateStorage();
        using var _ = storage;
        typeProvider.AddAssembly(typeof(Motor).Assembly);
        await storage.ConnectAsync(CancellationToken.None);
        var unknown = Assert.IsType<UnknownSubject>(storage.Children["Motor1"]);

        // Act
        await unknown.WriteAsync(new MemoryStream("""{ "$type": "HomeBlaze.Samples.Motor" }"""u8.ToArray()), CancellationToken.None);

        // Assert
        Assert.IsType<Motor>(storage.Children["Motor1"]);
    }

    [Fact]
    public async Task WhenTypesChange_ThenPlainJsonFilesAndUnresolvedPlaceholdersAreKept()
    {
        // Arrange
        WriteFile("Data.json", """{ "name": "plain data" }""");
        WriteFile("Sensor1.json", """{ "$type": "MyCompany.Sensor" }""");
        var (storage, typeProvider) = CreateStorage();
        using var _ = storage;
        await storage.ConnectAsync(CancellationToken.None);
        var jsonFile = storage.Children["Data.json"];
        var unknown = storage.Children["Sensor1"];

        // Act
        typeProvider.AddAssembly(typeof(Motor).Assembly);
        await storage.UpgradeUnknownSubjectsAsync();

        // Assert
        Assert.Same(jsonFile, storage.Children["Data.json"]);
        Assert.Same(unknown, storage.Children["Sensor1"]);
    }
```

Add `using Namotion.Interceptor.Testing;`.

- [ ] **Step 2: Run the tests and verify they fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests --filter "FullyQualifiedName~FluentStorageContainerUpgradeTests"`
Expected: compile error (`UpgradeUnknownSubjectsAsync` missing), then failures once it exists as a stub.

- [ ] **Step 3: Enumerate subjects in the path registry**

Add to `StoragePathRegistry`:

```csharp
    /// <summary>
    /// Returns a snapshot of the registered subjects of type <typeparamref name="T"/> with their original paths.
    /// </summary>
    public List<(T Subject, string Path)> GetSubjects<T>() where T : IInterceptorSubject
        => _subjectPaths
            .Where(entry => entry.Key is T)
            .Select(entry => ((T)entry.Key, entry.Value))
            .ToList();
```

- [ ] **Step 4: Implement the upgrade in FluentStorageContainer**

Add fields and constructor code (add `using HomeBlaze.Storage.Files;` and `using Microsoft.Extensions.DependencyInjection;`):

```csharp
    private readonly TypeProvider? _typeProvider;

    // Serializes everything that rebuilds or swaps children: scans, file watcher events and placeholder upgrades.
    private readonly SemaphoreSlim _hierarchyLock = new(1, 1);
```

In the constructor: `_typeProvider = serviceProvider.GetService<TypeProvider>();`

In `ConnectAsync`, subscribe before the scan and run the scan under the lock. Subscribing first means a type added during the scan is not missed: its upgrade waits for the lock and then sees the scanned placeholders.

```csharp
            if (_typeProvider is not null)
            {
                // Removing first keeps a reconnect from subscribing twice.
                _typeProvider.TypesChanged -= OnTypesChanged;
                _typeProvider.TypesChanged += OnTypesChanged;
            }

            await _hierarchyLock.WaitAsync(cancellationToken);
            try
            {
                await ScanAsync(cancellationToken);
            }
            finally
            {
                _hierarchyLock.Release();
            }
```

In `StartFileWatching`, pass a locked rescan: `() => RunLockedAsync(() => ScanAsync(CancellationToken.None))`.

Wrap `ProcessFileEventAsync`:

```csharp
    private Task ProcessFileEventAsync(FileSystemEventArgs e)
        => RunLockedAsync(() =>
        {
            var relativePath = _fileWatcher!.GetRelativePath(e.FullPath);

            return e.ChangeType switch
            {
                WatcherChangeTypes.Created => HandleFileCreatedAsync(relativePath),
                WatcherChangeTypes.Changed => HandleFileChangedAsync(relativePath, e.FullPath),
                WatcherChangeTypes.Deleted => HandleFileDeletedAsync(relativePath),
                WatcherChangeTypes.Renamed when e is RenamedEventArgs re =>
                    HandleFileRenamedAsync(relativePath, _fileWatcher.GetRelativePath(re.OldFullPath)),
                _ => Task.CompletedTask
            };
        });

    private async Task RunLockedAsync(Func<Task> action)
    {
        await _hierarchyLock.WaitAsync();
        try
        {
            await action();
        }
        finally
        {
            _hierarchyLock.Release();
        }
    }
```

In `HandleFileChangedAsync`, handle placeholders before the `IStorageFile` branch (an `UnknownSubject` is an `IStorageFile`):

```csharp
        if (existingSubject is UnknownSubject unknownSubject)
        {
            await RecreateAsync(relativePath, unknownSubject, CancellationToken.None);
            return;
        }
```

In `WriteBlobAsync`, replace the notification at the end:

```csharp
        if (_pathRegistry.TryGetSubject(path, out var subject))
        {
            if (subject is UnknownSubject unknownSubject)
            {
                await RunLockedAsync(() => RecreateAsync(path, unknownSubject, cancellationToken));
            }
            else if (subject is IStorageFile file)
            {
                await file.OnFileChangedAsync(cancellationToken);
            }
        }
```

Add the upgrade methods:

```csharp
    private void OnTypesChanged(object? sender, EventArgs e)
    {
        _ = UpgradeUnknownSubjectsAsync();
    }

    /// <summary>
    /// Recreates every <see cref="UnknownSubject"/> from its file and replaces it when the result differs.
    /// </summary>
    internal async Task UpgradeUnknownSubjectsAsync()
    {
        try
        {
            await RunLockedAsync(async () =>
            {
                if (_client is null)
                {
                    return;
                }

                foreach (var (unknownSubject, path) in _pathRegistry.GetSubjects<UnknownSubject>())
                {
                    await RecreateAsync(path, unknownSubject, CancellationToken.None);
                }
            });
        }
        catch (Exception exception)
        {
            _logger?.LogError(exception, "Failed to upgrade unknown subjects in storage.");
        }
    }

    // Callers hold _hierarchyLock.
    private async Task RecreateAsync(string path, UnknownSubject unknownSubject, CancellationToken cancellationToken)
    {
        var replacement = await _subjectFactory.CreateFromBlobAsync(Client, this, new Blob(path), cancellationToken);
        if (replacement is null ||
            replacement is UnknownSubject { } candidate &&
            candidate.TypeName == unknownSubject.TypeName &&
            candidate.Reason == unknownSubject.Reason)
        {
            return;
        }

        try
        {
            var content = await Client.ReadTextAsync(path, cancellationToken: cancellationToken);
            _pathRegistry.UpdateHash(path, StoragePathRegistry.ComputeHash(content));
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(exception, "Failed to compute hash for: {Path}", path);
        }

        var children = new Dictionary<string, IInterceptorSubject>(Children);
        _hierarchyManager.RemoveFromHierarchy(path, unknownSubject, children);
        _pathRegistry.Unregister(path);
        _pathRegistry.Register(replacement, path);
        _hierarchyManager.PlaceInHierarchy(path, replacement, children, this);
        Children = children;

        _logger?.LogInformation("Recreated {Path} as {Type}.", path, replacement.GetType().FullName);
    }
```

Check that `_pathRegistry.Unregister` does not drop the hash you just stored; if it does, call `UpdateHash` after `Register` instead.

In `Dispose`, unsubscribe before disposing the client: `if (_typeProvider is not null) _typeProvider.TypesChanged -= OnTypesChanged;` and dispose `_hierarchyLock` after the watcher.

- [ ] **Step 5: Run the storage tests**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests`
Expected: all pass. Run the upgrade tests 10 times in a row (`for i in $(seq 10); do dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests --filter "FullyQualifiedName~FluentStorageContainerUpgradeTests" --no-build || break; done`) and expect no failure.

- [ ] **Step 6: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Storage src/HomeBlaze/HomeBlaze.Storage.Tests
git commit -m "feat: upgrade HomeBlaze placeholder subjects when their type is loaded"
```

---

### Task 6: Plugin cache and feed path resolution

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Plugins/NuGetPluginPaths.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Plugins/HomeBlaze.Plugins.csproj` (reference `HomeBlaze.Services`, `InternalsVisibleTo`)
- Create: `src/HomeBlaze/HomeBlaze.Plugins.Tests/NuGetPluginPathsTests.cs`

- [ ] **Step 1: Update the project file**

In `HomeBlaze.Plugins.csproj` add:

```xml
  <ItemGroup>
    <InternalsVisibleTo Include="HomeBlaze.Plugins.Tests" />
  </ItemGroup>
```

and to the project references:

```xml
    <ProjectReference Include="..\HomeBlaze.Services\HomeBlaze.Services.csproj" />
```

Also add `<PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" />` (for `BackgroundService` in Task 7) if it is not already available transitively; check `src/Directory.Packages.props` for the version entry.

- [ ] **Step 2: Write the failing tests**

```csharp
using Xunit;

namespace HomeBlaze.Plugins.Tests;

public class NuGetPluginPathsTests
{
    private static readonly string DataDirectory = Path.Combine(Path.GetTempPath(), "homeblaze-data");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WhenCacheDirectoryIsNotSet_ThenDefaultUnderDataDirectoryIsUsed(string? configured)
    {
        // Act
        var path = NuGetPluginPaths.ResolveCacheDirectory(configured, DataDirectory);

        // Assert
        Assert.Equal(Path.Combine(DataDirectory, "Plugins", "Cache"), path);
    }

    [Fact]
    public void WhenCacheDirectoryIsRelative_ThenItResolvesAgainstDataDirectory()
    {
        // Act
        var path = NuGetPluginPaths.ResolveCacheDirectory("MyCache", DataDirectory);

        // Assert
        Assert.Equal(Path.Combine(DataDirectory, "MyCache"), path);
    }

    [Fact]
    public void WhenCacheDirectoryIsAbsolute_ThenItIsUsedAsIs()
    {
        // Arrange
        var absolute = Path.Combine(Path.GetTempPath(), "shared-cache");

        // Act
        var path = NuGetPluginPaths.ResolveCacheDirectory(absolute, DataDirectory);

        // Assert
        Assert.Equal(absolute, path);
    }

    [Fact]
    public void WhenDataDirectoryIsMissing_ThenWorkingDirectoryIsUsed()
    {
        // Act
        var path = NuGetPluginPaths.ResolveCacheDirectory(null, dataDirectory: null);

        // Assert
        Assert.Equal(Path.GetFullPath(Path.Combine("Plugins", "Cache")), path);
    }

    [Theory]
    [InlineData("https://api.nuget.org/v3/index.json")]
    [InlineData("http://localhost:5555/v3/index.json")]
    public void WhenFeedUrlIsAbsoluteUri_ThenItIsUsedAsIs(string url)
    {
        // Act
        var resolved = NuGetPluginPaths.ResolveFeedUrl(url, DataDirectory);

        // Assert
        Assert.Equal(url, resolved);
    }

    [Fact]
    public void WhenFeedUrlIsRelativeFolder_ThenItResolvesAgainstDataDirectory()
    {
        // Act
        var resolved = NuGetPluginPaths.ResolveFeedUrl("../Plugins", DataDirectory);

        // Assert
        Assert.Equal(Path.GetFullPath(Path.Combine(DataDirectory, "../Plugins")), resolved);
    }

    [Fact]
    public void WhenFeedUrlIsRootedFolder_ThenItIsUsedAsIs()
    {
        // Arrange
        var folder = Path.Combine(Path.GetTempPath(), "packages");

        // Act
        var resolved = NuGetPluginPaths.ResolveFeedUrl(folder, DataDirectory);

        // Assert
        Assert.Equal(folder, resolved);
    }
}
```

- [ ] **Step 3: Run the tests and verify they fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Plugins.Tests --filter "FullyQualifiedName~NuGetPluginPathsTests"`
Expected: compile error, `NuGetPluginPaths` does not exist.

- [ ] **Step 4: Implement**

```csharp
namespace HomeBlaze.Plugins;

/// <summary>
/// Resolves the folder paths of a plugin provider. Relative paths resolve against the instance data directory,
/// or the working directory when there is none; rooted paths and absolute URIs are used as-is.
/// </summary>
internal static class NuGetPluginPaths
{
    /// <summary>
    /// The cache folder used when none is configured, relative to the data directory.
    /// </summary>
    public static readonly string DefaultCacheDirectory = Path.Combine("Plugins", "Cache");

    public static string ResolveCacheDirectory(string? configured, string? dataDirectory)
    {
        var value = string.IsNullOrWhiteSpace(configured) ? DefaultCacheDirectory : configured.Trim();
        return Path.GetFullPath(value, dataDirectory ?? Directory.GetCurrentDirectory());
    }

    public static string ResolveFeedUrl(string url, string? dataDirectory)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !uri.IsFile)
        {
            return url;
        }

        return Path.IsPathRooted(url)
            ? url
            : Path.GetFullPath(url, dataDirectory ?? Directory.GetCurrentDirectory());
    }
}
```

On Windows `Uri.TryCreate` treats `C:\packages` as a file URI, which the `IsFile` check sends to the path branch.

- [ ] **Step 5: Run the tests and verify they pass**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Plugins.Tests --filter "FullyQualifiedName~NuGetPluginPathsTests"`
Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Plugins src/HomeBlaze/HomeBlaze.Plugins.Tests
git commit -m "feat: resolve HomeBlaze plugin cache and feed paths against the data directory"
```

---

### Task 7: NuGetPluginProvider replaces PluginManager and PluginLoader

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Plugins/NuGetPluginProvider.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Plugins/Plugin.cs`
- Delete: `src/HomeBlaze/HomeBlaze.Plugins/PluginManager.cs`, `PluginLoader.cs`, `PluginsServiceCollectionExtensions.cs`, `PluginConfiguration.cs`
- Delete: `src/HomeBlaze/HomeBlaze.Plugins.Tests/PluginConfigurationTests.cs`
- Create: `src/HomeBlaze/HomeBlaze.Plugins.Tests/NuGetPluginProviderTests.cs`
- Modify: `src/HomeBlaze/HomeBlaze/Program.cs` (only what is needed to compile: remove `AddHomeBlazePlugins` and the plugin loading block; Task 9 does the rest)

- [ ] **Step 1: Write the failing tests**

These tests use an empty local folder feed, so nothing is downloaded. `ReconcileAsync` is internal and called directly instead of starting the hosted service.

```csharp
using HomeBlaze.Abstractions;
using HomeBlaze.Plugins.Models;
using HomeBlaze.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor;
using Xunit;

namespace HomeBlaze.Plugins.Tests;

public class NuGetPluginProviderTests : IDisposable
{
    private readonly DirectoryInfo _dataDirectory = Directory.CreateTempSubdirectory("homeblaze-plugins-");

    [Fact]
    public async Task WhenPackageIsMissingFromFeed_ThenPluginShowsError()
    {
        // Arrange
        var provider = CreateProvider();
        provider.Plugins = [new PluginEntry { PackageName = "Missing.Package", Version = "1.0.0" }];

        // Act
        await provider.ReconcileAsync(CancellationToken.None);

        // Assert
        var plugin = provider.LoadedPlugins["Missing.Package"];
        Assert.Equal(ServiceStatus.Error, plugin.Status);
        Assert.False(string.IsNullOrEmpty(plugin.StatusMessage));
        Assert.Equal("Warning", provider.IconColor);
    }

    [Fact]
    public async Task WhenFailedPluginIsRemoved_ThenItDisappearsWithoutRestart()
    {
        // Arrange
        var provider = CreateProvider();
        provider.Plugins = [new PluginEntry { PackageName = "Missing.Package", Version = "1.0.0" }];
        await provider.ReconcileAsync(CancellationToken.None);

        // Act
        await provider.RemovePluginAsync("Missing.Package");

        // Assert
        Assert.Empty(provider.LoadedPlugins);
        Assert.False(provider.IsRestartRequired);
    }

    [Fact]
    public async Task WhenFeedsChangeAfterLoading_ThenRestartIsRequired()
    {
        // Arrange
        var provider = CreateProvider();
        await provider.ReconcileAsync(CancellationToken.None);

        // Act
        provider.Feeds = [.. provider.Feeds, new PluginFeedEntry { Name = "other", Url = "Other" }];
        await provider.ApplyConfigurationAsync(CancellationToken.None);

        // Assert
        Assert.True(provider.IsRestartRequired);
    }

    [Fact]
    public async Task WhenPluginIsAddedTwice_ThenSecondAddThrows()
    {
        // Arrange
        var provider = CreateProvider();
        await provider.AddPluginAsync("Missing.Package", "1.0.0", CancellationToken.None);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.AddPluginAsync("missing.package", "1.0.0", CancellationToken.None));
    }

    private NuGetPluginProvider CreateProvider()
    {
        Directory.CreateDirectory(Path.Combine(_dataDirectory.FullName, "Feed"));
        var provider = new NuGetPluginProvider(new TypeProvider(), NullLoggerFactory.Instance)
        {
            Feeds = [new PluginFeedEntry { Name = "local", Url = "Feed" }]
        };

        var context = InterceptorSubjectContext.Create();
        context.AddService<IDataDirectoryProvider>(new TestDataDirectoryProvider(_dataDirectory.FullName));
        ((IInterceptorSubject)provider).Context.AddFallbackContext(context);
        return provider;
    }

    public void Dispose()
    {
        _dataDirectory.Delete(recursive: true);
    }

    private sealed class TestDataDirectoryProvider(string dataDirectory) : IDataDirectoryProvider
    {
        public string DataDirectory => dataDirectory;
    }
}
```

Add `<PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />` to the test project if `NullLoggerFactory` is not available, and a reference to `Namotion.Interceptor` if `InterceptorSubjectContext` is not.

- [ ] **Step 2: Run the tests and verify they fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Plugins.Tests --filter "FullyQualifiedName~NuGetPluginProviderTests"`
Expected: compile error, `NuGetPluginProvider` does not exist.

- [ ] **Step 3: Implement NuGetPluginProvider**

`NuGetPluginProvider.cs`:

```csharp
using System.Text.Json;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Plugins.Models;
using HomeBlaze.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.NuGet.Plugins;
using Namotion.NuGet.Plugins.Configuration;
using Namotion.NuGet.Plugins.Loading;

namespace HomeBlaze.Plugins;

/// <summary>
/// Loads NuGet packages as plugins and adds their assemblies to the <see cref="TypeProvider"/>.
/// Adding a plugin takes effect immediately. Removing a plugin, changing its version or changing the
/// feeds, host packages, host identifier or cache directory takes effect after a restart.
/// </summary>
[InterceptorSubject]
public partial class NuGetPluginProvider : BackgroundService, IConfigurable, ITitleProvider, IIconProvider
{
    private readonly TypeProvider _typeProvider;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<NuGetPluginProvider> _logger;
    private readonly SemaphoreSlim _loadLock = new(1, 1);

    // Requested version per package of every load attempt, successful or failed.
    private readonly Dictionary<string, string?> _requestedVersions = new(StringComparer.OrdinalIgnoreCase);

    // Never disposed: disposing it unloads plugin assemblies that live subjects still use.
    private NuGetPluginLoader? _loader;
    private string? _loaderSettings;

    public NuGetPluginProvider(TypeProvider typeProvider, ILoggerFactory loggerFactory)
    {
        _typeProvider = typeProvider;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<NuGetPluginProvider>();

        Plugins = [];
        Feeds = [];
        HostPackages = [];
        HostIdentifier = null;
        CacheDirectory = null;
        LoadedPlugins = new Dictionary<string, Plugin>(StringComparer.OrdinalIgnoreCase);
        IsRestartRequired = false;
    }

    public string? Title => "Plugins";

    public string? IconName => "Extension";

    [Derived]
    public string? IconColor =>
        IsRestartRequired || LoadedPlugins.Values.Any(plugin => plugin.Status == ServiceStatus.Error) ? "Warning" : "Success";

    [Configuration]
    public partial PluginEntry[] Plugins { get; set; }

    [Configuration]
    public partial PluginFeedEntry[] Feeds { get; set; }

    [Configuration]
    public partial string[] HostPackages { get; set; }

    [Configuration]
    public partial string? HostIdentifier { get; set; }

    /// <summary>
    /// Folder for downloaded packages. Relative paths resolve against the data directory; empty means <c>Plugins/Cache</c>.
    /// </summary>
    [Configuration]
    public partial string? CacheDirectory { get; set; }

    [State]
    public partial Dictionary<string, Plugin> LoadedPlugins { get; internal set; }

    /// <summary>
    /// Whether a configuration change only takes effect after a restart.
    /// </summary>
    [State]
    public partial bool IsRestartRequired { get; internal set; }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => ReconcileAsync(stoppingToken);

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => ReconcileAsync(cancellationToken);

    [Operation(Title = "Add Plugin")]
    public async Task AddPluginAsync(string packageName, string version, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageName);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        if (Plugins.Any(plugin => plugin.PackageName.Equals(packageName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Plugin '{packageName}' is already configured.");
        }

        Plugins = [.. Plugins, new PluginEntry { PackageName = packageName, Version = version }];
        await ReconcileAsync(cancellationToken);
    }

    [Operation(Title = "Retry Failed Plugins")]
    public Task RetryAsync(CancellationToken cancellationToken) => ReconcileAsync(cancellationToken, retryFailed: true);

    internal Task RemovePluginAsync(string packageName)
    {
        Plugins = Plugins
            .Where(plugin => !plugin.PackageName.Equals(packageName, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return ReconcileAsync(CancellationToken.None);
    }

    /// <summary>
    /// Brings the loaded plugins in line with the configuration: loads new entries (and failed ones when
    /// <paramref name="retryFailed"/> is set) and marks changes that need a restart.
    /// </summary>
    internal async Task ReconcileAsync(CancellationToken cancellationToken, bool retryFailed = false)
    {
        await _loadLock.WaitAsync(cancellationToken);
        try
        {
            var dataDirectory = ((IInterceptorSubject)this).Context.TryGetService<IDataDirectoryProvider>()?.DataDirectory;
            var options = CreateLoaderOptions(dataDirectory);
            var settings = JsonSerializer.Serialize(new
            {
                Feeds = options.Feeds.Select(feed => new { feed.Name, feed.Url, feed.ApiKey }),
                HostPackages,
                HostIdentifier,
                options.CacheDirectory
            });

            if (_loader is null)
            {
                _loader = new NuGetPluginLoader(options, _loggerFactory.CreateLogger<NuGetPluginLoader>());
                _loaderSettings = settings;
            }
            else if (settings != _loaderSettings)
            {
                IsRestartRequired = true;
            }

            var plugins = new Dictionary<string, Plugin>(LoadedPlugins, StringComparer.OrdinalIgnoreCase);
            var configured = Plugins
                .GroupBy(entry => entry.PackageName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            foreach (var (packageName, plugin) in plugins.ToList())
            {
                var isRemoved = !configured.TryGetValue(packageName, out var entry);
                if (plugin.Status == ServiceStatus.Error)
                {
                    // Nothing was loaded for a failed plugin, so dropping or changing it needs no restart.
                    if (isRemoved)
                    {
                        plugins.Remove(packageName);
                    }

                    continue;
                }

                if (isRemoved)
                {
                    MarkRestartRequired(plugin, "Removed. Takes effect after a restart.");
                }
                else if (!string.Equals(_requestedVersions.GetValueOrDefault(packageName), entry!.Version, StringComparison.OrdinalIgnoreCase))
                {
                    MarkRestartRequired(plugin, $"Version {entry.Version} takes effect after a restart.");
                }
            }

            var entriesToLoad = configured.Values
                .Where(entry =>
                    !plugins.TryGetValue(entry.PackageName, out var plugin) ||
                    plugin.Status == ServiceStatus.Error &&
                    (retryFailed || !string.Equals(_requestedVersions.GetValueOrDefault(entry.PackageName), entry.Version, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (entriesToLoad.Count > 0)
            {
                await LoadAsync(entriesToLoad, plugins, cancellationToken);
            }

            LoadedPlugins = plugins;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private async Task LoadAsync(List<PluginEntry> entries, Dictionary<string, Plugin> plugins, CancellationToken cancellationToken)
    {
        foreach (var entry in entries)
        {
            _requestedVersions[entry.PackageName] = entry.Version;
        }

        _logger.LogInformation("Loading {Count} plugins...", entries.Count);

        NuGetPluginLoadResult result;
        try
        {
            result = await _loader!.LoadPluginsAsync(
                entries.Select(entry => new NuGetPluginReference(entry.PackageName, entry.Version)),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Plugin loading failed.");
            foreach (var entry in entries)
            {
                plugins[entry.PackageName] = CreateFailedPlugin(entry.PackageName, exception.Message);
            }

            return;
        }

        foreach (var loadedPlugin in result.LoadedPlugins)
        {
            var skippedTypes = _typeProvider.AddAssemblies(loadedPlugin.Assemblies);
            foreach (var skippedType in skippedTypes)
            {
                var registeredAssembly = _typeProvider.Types
                    .FirstOrDefault(type => type.FullName == skippedType.FullName)?.Assembly.GetName().Name;
                _logger.LogWarning(
                    "Type {Type} from {Assembly} was skipped because {RegisteredAssembly} already provides a type with that name.",
                    skippedType.FullName, skippedType.Assembly.GetName().Name, registeredAssembly);
            }

            plugins[loadedPlugin.PackageName] = CreateLoadedPlugin(loadedPlugin, skippedTypes);
        }

        foreach (var failure in result.Failures)
        {
            _logger.LogError("Plugin '{Plugin}' failed to load: {Reason}", failure.PackageName, failure.Reason);
            plugins[failure.PackageName] = CreateFailedPlugin(failure.PackageName, failure.Reason);
        }

        _logger.LogInformation("Plugin loading complete: {Loaded} loaded, {Failed} failed.",
            result.LoadedPlugins.Count, result.Failures.Count);
    }

    private NuGetPluginLoaderOptions CreateLoaderOptions(string? dataDirectory)
    {
        return new NuGetPluginLoaderOptions
        {
            Feeds = Feeds.Length > 0
                ? Feeds.Select(feed => new NuGetFeed(feed.Name, NuGetPluginPaths.ResolveFeedUrl(feed.Url, dataDirectory), feed.ApiKey)).ToList()
                : [NuGetFeed.NuGetOrg],
            IsHostPackage = HostPackages.Length > 0
                ? name => NuGetPackageNameMatcher.IsMatchAny(name, HostPackages)
                : null,
            HostDependencies = HostDependencyResolver.FromDepsJson(),
            CacheDirectory = NuGetPluginPaths.ResolveCacheDirectory(CacheDirectory, dataDirectory),
            HostIdentifier = HostIdentifier,
        };
    }

    private void MarkRestartRequired(Plugin plugin, string message)
    {
        plugin.StatusMessage = message;
        IsRestartRequired = true;
    }

    private Plugin CreateLoadedPlugin(NuGetPlugin plugin, IReadOnlyList<Type> skippedTypes)
    {
        return new Plugin(this)
        {
            Name = plugin.PackageName,
            Version = plugin.PackageVersion,
            Description = plugin.Metadata.Description,
            Authors = plugin.Metadata.Authors,
            IconUrl = plugin.Metadata.IconUrl,
            Tags = plugin.Metadata.Tags.ToArray(),
            HostDependencies = plugin.Dependencies
                .Where(dependency => dependency.Classification == NuGetDependencyClassification.Host)
                .Select(dependency => $"{dependency.PackageName} v{dependency.Version}")
                .ToArray(),
            PrivateDependencies = plugin.Dependencies
                .Where(dependency => dependency.Classification == NuGetDependencyClassification.Isolated)
                .Select(dependency => $"{dependency.PackageName} v{dependency.Version}")
                .ToArray(),
            Assemblies = plugin.Assemblies
                .Select(assembly =>
                {
                    var name = assembly.GetName();
                    return $"{name.Name} v{name.Version?.ToString(3) ?? "?"}";
                })
                .ToArray(),
            Status = ServiceStatus.Running,
            StatusMessage = skippedTypes.Count > 0
                ? $"Skipped {skippedTypes.Count} types already provided by other assemblies: {string.Join(", ", skippedTypes.Select(type => type.FullName))}"
                : null
        };
    }

    private Plugin CreateFailedPlugin(string packageName, string reason)
    {
        return new Plugin(this)
        {
            Name = packageName,
            Version = "",
            Assemblies = [],
            Status = ServiceStatus.Error,
            StatusMessage = reason
        };
    }
}
```

Copy the `using` list and the `NuGetPluginLoadResult`, `NuGetPlugin`, `NuGetDependencyClassification` namespaces from the deleted `PluginLoader.cs` and `PluginManager.cs` if the ones above do not compile. `Plugin`'s setters are `internal`, which this assembly can use.

- [ ] **Step 4: Update Plugin**

In `Plugin.cs`, change the parent field and constructor parameter to `NuGetPluginProvider provider` (field `_provider`), and replace the remove operation:

```csharp
    [Operation(Title = "Remove Plugin", RequiresConfirmation = true)]
    public Task RemovePluginAsync() => _provider.RemovePluginAsync(Name);
```

Change `StatusMessage`'s setter to `internal set` if it is not already (it is set by the provider after construction).

- [ ] **Step 5: Delete the replaced files**

```bash
git rm src/HomeBlaze/HomeBlaze.Plugins/PluginManager.cs src/HomeBlaze/HomeBlaze.Plugins/PluginLoader.cs src/HomeBlaze/HomeBlaze.Plugins/PluginsServiceCollectionExtensions.cs src/HomeBlaze/HomeBlaze.Plugins/PluginConfiguration.cs src/HomeBlaze/HomeBlaze.Plugins.Tests/PluginConfigurationTests.cs
```

Keep `Models/PluginEntry.cs` and `Models/PluginFeedEntry.cs`.

- [ ] **Step 6: Make Program.cs compile**

In `src/HomeBlaze/HomeBlaze/Program.cs`, delete `var pluginConfigPath = ...`, `builder.Services.AddHomeBlazePlugins(pluginConfigPath);`, and the `// Load runtime plugins` block (from `var pluginLoader = ...` to the closing brace of `if (pluginResult != null)`). Change the comment above the seeding call to `// Seeding runs before anything reads the data directory.` Change `typeProvider.AddAssembly(typeof(PluginManager).Assembly);` to `typeof(NuGetPluginProvider)`.

- [ ] **Step 7: Run the tests and build**

Run: `dotnet build src/Namotion.Interceptor.slnx` then `dotnet test src/HomeBlaze/HomeBlaze.Plugins.Tests --filter "Category!=Integration"`
Expected: build succeeds (fix any remaining reference to the deleted types; `git grep -n "PluginManager\|PluginLoader\|AddHomeBlazePlugins\|PluginConfiguration\b" -- "*.cs"` must be empty), tests pass.

- [ ] **Step 8: Commit**

```bash
git add -A src/HomeBlaze/HomeBlaze.Plugins src/HomeBlaze/HomeBlaze.Plugins.Tests src/HomeBlaze/HomeBlaze/Program.cs
git commit -m "feat!: replace the HomeBlaze plugin manager and startup loader with NuGetPluginProvider subjects"
```

---

### Task 8: Provider integration tests with the sample packages

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Plugins.Tests/NuGetPluginProviderIntegrationTests.cs`
- Modify: `.github/workflows/build.yml` (job `test-nugetplugins-integration`)

- [ ] **Step 1: Write the tests**

The sample packages are produced by building the solution into `src/HomeBlaze/HomeBlaze/Plugins`. The tests use that folder as an absolute feed and a temp data directory.

```csharp
using HomeBlaze.Abstractions;
using HomeBlaze.Plugins.Models;
using HomeBlaze.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor;
using Xunit;

namespace HomeBlaze.Plugins.Tests;

[Trait("Category", "Integration")]
public class NuGetPluginProviderIntegrationTests : IDisposable
{
    private readonly DirectoryInfo _dataDirectory = Directory.CreateTempSubdirectory("homeblaze-plugins-integration-");

    [Fact]
    public async Task WhenProviderLoadsSamplePlugin_ThenItsTypesAreAddedAndTypesChangedIsRaised()
    {
        // Arrange
        var typeProvider = new TypeProvider();
        var raisedCount = 0;
        typeProvider.TypesChanged += (_, _) => raisedCount++;
        var provider = CreateProvider(typeProvider);
        provider.Plugins = [new PluginEntry { PackageName = "MyCompany.SamplePlugin1.HomeBlaze", Version = "1.0.0" }];

        // Act
        await provider.ReconcileAsync(CancellationToken.None);

        // Assert
        Assert.Equal(ServiceStatus.Running, provider.LoadedPlugins["MyCompany.SamplePlugin1.HomeBlaze"].Status);
        Assert.Contains(typeProvider.Types, type => type.FullName == "MyCompany.SamplePlugin1.SampleDevice1");
        Assert.True(raisedCount >= 1);
        Assert.True(Directory.Exists(Path.Combine(_dataDirectory.FullName, "Plugins", "Cache")));
    }

    [Fact]
    public async Task WhenPluginIsAddedAfterLoading_ThenItLoadsWithoutRestart()
    {
        // Arrange
        var typeProvider = new TypeProvider();
        var provider = CreateProvider(typeProvider);
        provider.Plugins = [new PluginEntry { PackageName = "MyCompany.SamplePlugin1.HomeBlaze", Version = "1.0.0" }];
        await provider.ReconcileAsync(CancellationToken.None);

        // Act
        await provider.AddPluginAsync("MyCompany.SamplePlugin2.HomeBlaze", "1.0.0", CancellationToken.None);

        // Assert
        Assert.Equal(ServiceStatus.Running, provider.LoadedPlugins["MyCompany.SamplePlugin2.HomeBlaze"].Status);
        Assert.Contains(typeProvider.Types, type => type.FullName == "MyCompany.SamplePlugin2.SampleDevice2");
        Assert.False(provider.IsRestartRequired);
    }

    [Fact]
    public async Task WhenLoadedPluginIsRemoved_ThenRestartIsRequired()
    {
        // Arrange
        var provider = CreateProvider(new TypeProvider());
        provider.Plugins = [new PluginEntry { PackageName = "MyCompany.SamplePlugin1.HomeBlaze", Version = "1.0.0" }];
        await provider.ReconcileAsync(CancellationToken.None);

        // Act
        await provider.RemovePluginAsync("MyCompany.SamplePlugin1.HomeBlaze");

        // Assert
        Assert.True(provider.IsRestartRequired);
        Assert.Contains("restart", provider.LoadedPlugins["MyCompany.SamplePlugin1.HomeBlaze"].StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    private NuGetPluginProvider CreateProvider(TypeProvider typeProvider)
    {
        var provider = new NuGetPluginProvider(typeProvider, NullLoggerFactory.Instance)
        {
            Feeds = [new PluginFeedEntry { Name = "samples", Url = FindPluginsFolder() }],
            HostIdentifier = "HomeBlaze"
        };

        var context = InterceptorSubjectContext.Create();
        context.AddService<IDataDirectoryProvider>(new TestDataDirectoryProvider(_dataDirectory.FullName));
        ((IInterceptorSubject)provider).Context.AddFallbackContext(context);
        return provider;
    }

    private static string FindPluginsFolder()
    {
        var directory = Path.GetDirectoryName(typeof(NuGetPluginProviderIntegrationTests).Assembly.Location);
        while (directory != null)
        {
            var pluginsPath = Path.Combine(directory, "HomeBlaze", "Plugins");
            if (File.Exists(Path.Combine(pluginsPath, "MyCompany.SamplePlugin1.HomeBlaze.1.0.0.nupkg")))
            {
                return pluginsPath;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new InvalidOperationException("Could not find the sample plugin packages. Build the solution first.");
    }

    public void Dispose()
    {
        _dataDirectory.Delete(recursive: true);
    }

    private sealed class TestDataDirectoryProvider(string dataDirectory) : IDataDirectoryProvider
    {
        public string DataDirectory => dataDirectory;
    }
}
```

The sample plugins load into the test process for the rest of the run. That is fine because each test uses a new `TypeProvider`; the shared `MyCompany.Abstractions` resolves once per process by design.

- [ ] **Step 2: Run the tests**

Run: `dotnet build src/Namotion.Interceptor.slnx` then `dotnet test src/HomeBlaze/HomeBlaze.Plugins.Tests --filter "Category=Integration"`
Expected: all pass. If loading plugin 2 after plugin 1 fails, the cause is in `NuGetPluginLoader` handling a second `LoadPluginsAsync` call; fix it there with a library test in `Namotion.NuGet.Plugins.Tests` that loads the two sample plugins in two calls on one loader.

- [ ] **Step 3: Run them in CI**

In `.github/workflows/build.yml`, job `test-nugetplugins-integration`, add a step after `Run NuGet plugins integration tests`:

```yaml
      - name: Run HomeBlaze plugin provider integration tests
        run: |
          dotnet test src/HomeBlaze/HomeBlaze.Plugins.Tests `
            --configuration Release `
            --no-build `
            --filter "Category=Integration" `
            --results-directory ./TestResults `
            --collect:"XPlat Code Coverage" `
            -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=cobertura
```

Check that the job's `if:` path filter covers `src/HomeBlaze/HomeBlaze.Plugins/**` and `src/HomeBlaze/HomeBlaze.Plugins.Tests/**`; add them to the filter that triggers it if not.

- [ ] **Step 4: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Plugins.Tests .github/workflows/build.yml
git commit -m "test: load the sample plugins through NuGetPluginProvider"
```

---

### Task 9: Host wiring, data files and E2E

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.Services/HomeBlazePaths.cs`, `src/HomeBlaze/HomeBlaze.Services.Tests/HomeBlazePathsTests.cs`
- Modify: `src/HomeBlaze/HomeBlaze/HomeBlaze.csproj`
- Modify: `src/HomeBlaze/HomeBlaze/Seed/Files/Plugins.json`, `src/HomeBlaze/HomeBlaze/Data/Files/Plugins.json`, `src/HomeBlaze/HomeBlaze.E2E.Tests/TestData/Plugins.json`
- Modify: `.gitignore`
- Modify: `src/HomeBlaze/HomeBlaze.E2E.Tests/Infrastructure/WebTestingHostFactory.cs`
- Create: `src/HomeBlaze/HomeBlaze.E2E.Tests/TestData/SampleSensor.json`
- Modify: `src/HomeBlaze/HomeBlaze.E2E.Tests/PluginLoadingTests.cs`

- [ ] **Step 1: Remove the plugin path helpers**

In `HomeBlazePaths.cs`, delete `PluginConfigurationPathKey`, `DefaultPluginConfigurationFile` and `GetPluginConfigurationPath`, and the tests for them in `HomeBlazePathsTests.cs`. `git grep -n "PluginConfigurationPath\|GetPluginConfigurationPath\|DefaultPluginConfigurationFile" -- src` must only find docs afterwards (fixed in Task 12).

- [ ] **Step 2: Remove the nupkg copy from the host project**

In `src/HomeBlaze/HomeBlaze/HomeBlaze.csproj`, delete the `CopyPluginNupkgs` target and the comment above it about `Plugins\*.nupkg` in the item group (the comment that starts with `<!-- Plugins\*.nupkg are generated by sample plugin builds`). The two `ProjectReference`s to the sample plugins stay so building the host still produces the packages.

- [ ] **Step 3: Update the plugin files**

Seed `Plugins.json`: change `$type` to `HomeBlaze.Plugins.NuGetPluginProvider`, keep the rest.

Dev `Data/Files/Plugins.json`:

```json
{
  "$type": "HomeBlaze.Plugins.NuGetPluginProvider",
  "feeds": [
    { "name": "local", "url": "../Plugins" },
    { "name": "nuget.org", "url": "https://api.nuget.org/v3/index.json" }
  ],
  "hostPackages": [],
  "hostIdentifier": "HomeBlaze",
  "plugins": [
    { "packageName": "MyCompany.SamplePlugin1.HomeBlaze", "version": "1.0.0" },
    { "packageName": "MyCompany.SamplePlugin2.HomeBlaze", "version": "1.0.0" }
  ]
}
```

E2E `TestData/Plugins.json`: change `$type` to `HomeBlaze.Plugins.NuGetPluginProvider` and `"cacheDirectory": null` to `"cacheDirectory": "PluginsCache"`. The default `Plugins/Cache` would sit inside the local `Plugins` feed folder of the test output.

- [ ] **Step 4: Update .gitignore**

Replace the `src/HomeBlaze/HomeBlaze/PluginsCache/` line with `src/HomeBlaze/HomeBlaze/Data/Plugins/`. Keep `src/HomeBlaze/HomeBlaze/Plugins/` (the generated sample packages).

- [ ] **Step 5: Update the E2E host factory**

In `WebTestingHostFactory.ConfigureWebHost`, delete the `PluginConfigurationPath` setting and its comment. The test data `Plugins.json` is found in the tree under `TestData`.

- [ ] **Step 6: Add the upgrade E2E test**

`TestData/SampleSensor.json`:

```json
{
  "$type": "MyCompany.SamplePlugin1.SampleDevice1",
  "name": "E2E Sample Sensor",
  "pollingIntervalMs": 2000
}
```

Append to `PluginLoadingTests`:

```csharp
    [Fact]
    public async Task WhenPluginLoadsAfterStartup_ThenItsDeviceFileBecomesTheRealSubject()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();

        // Act
        await page.GotoAsync(_fixture.ServerAddress);
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        var browserLink = page.GetByRole(AriaRole.Link, new() { Name = "Browser" });
        await Assertions.Expect(browserLink).ToBeVisibleAsync(new() { Timeout = PageLoadTimeout });
        await browserLink.ClickAsync();

        // Assert - the title comes from SampleDevice1, the placeholder would show the file name
        await Assertions.Expect(page.GetByText("E2E Sample Sensor").First)
            .ToBeVisibleAsync(new() { Timeout = PageLoadTimeout });
    }
```

- [ ] **Step 7: Run the E2E tests**

Run: `dotnet build src/Namotion.Interceptor.slnx` then `dotnet test src/HomeBlaze/HomeBlaze.E2E.Tests`
Expected: all pass, including both existing `PluginLoadingTests` and the new one. Check the other E2E tests for assertions that count root children and would change with the added `SampleSensor.json`; adjust them if so.

- [ ] **Step 8: Start the dev host once**

Run: `dotnet run --project src/HomeBlaze/HomeBlaze` (stop with Ctrl+C after the log shows `Plugin loading complete: 2 loaded, 0 failed.`)
Expected: the log shows the sample plugins loading from `src/HomeBlaze/HomeBlaze/Plugins` and `src/HomeBlaze/HomeBlaze/Data/Plugins/Cache` is created. If `git status` shows files under `Data/Plugins`, the `.gitignore` entry is wrong.

- [ ] **Step 9: Commit**

```bash
git add -A .gitignore src/HomeBlaze/HomeBlaze src/HomeBlaze/HomeBlaze.Services src/HomeBlaze/HomeBlaze.Services.Tests src/HomeBlaze/HomeBlaze.E2E.Tests
git commit -m "feat!: find HomeBlaze plugin providers in the subject tree instead of a fixed configuration path"
```

---

### Task 10: Path references across placeholder upgrades

Investigate, then fix only what is broken. The upgrade replaces the subject object at a path, so anything that captured the old object instead of resolving the path again keeps the placeholder.

**Files:** to be determined by the investigation; record the findings in the commit message.

- [ ] **Step 1: Audit the consumers**

For each of these, find how it refers to other subjects and whether it reacts to a subject being replaced at the same path (detach of the old object, attach of the new one):

- Dashboard and page widgets (`git grep -n "ResolveSubject\|SubjectPathResolver\|ISubjectPathResolver" -- src/HomeBlaze`), including markdown expressions in `MarkdownContentParser`.
- History stores (`HomeBlaze.History*`): recording is driven by property changes of attached subjects; check that the new subject's properties are recorded under the same path.
- OPC UA server (`HomeBlaze.OpcUa`): check that a replaced child is removed from and added to the address space.
- The subject browser tree and detail pane (`HomeBlaze.Host`): check that a selected placeholder switches to the real subject or at least does not throw.

- [ ] **Step 2: Add a regression test for each broken consumer and fix it**

For each consumer that keeps the old object, write a test in its test project in the style of `FluentStorageContainerUpgradeTests` (placeholder first, add the type, wait for the upgrade, assert the consumer shows the real subject), see it fail, then fix the consumer to resolve by path or react to the replacement.

- [ ] **Step 3: Run the affected test projects and the E2E tests**

Run: the test projects you touched, then `dotnet test src/HomeBlaze/HomeBlaze.E2E.Tests`.
Expected: all pass.

- [ ] **Step 4: Commit**

```bash
git add -A src/HomeBlaze
git commit -m "fix: follow HomeBlaze subjects that are replaced at the same path"
```

If nothing needed fixing, skip the commit and report the audit result instead.

---

### Task 11: Real library versions in the container image

`deps.json` lists each project with its `PackageVersion` (verified: building with `-p:PackageVersion=0.9.3` changes the `deps.json` entries). Assembly versions come from `Version` and stay `0.1.0.0`, which matches the published packages because `pack` also only sets `PackageVersion`.

**Files:**
- Modify: `src/Directory.Build.props`
- Modify: `src/HomeBlaze/Directory.Build.props`
- Modify: `.github/workflows/build.yml` (jobs `container-build` and `container-publish`)

- [ ] **Step 1: Add the library package version property**

In `src/Directory.Build.props`, after `<Version>0.1.0</Version>`:

```xml
    <!-- Lets an application build report the released library version in deps.json, which plugin loaders compare
         against the versions plugins were built with. pack sets PackageVersion directly instead. -->
    <PackageVersion Condition="'$(LibraryPackageVersion)' != ''">$(LibraryPackageVersion)</PackageVersion>
```

In `src/HomeBlaze/Directory.Build.props`, after `<Version>1.0.0</Version>`:

```xml
    <PackageVersion>$(Version)</PackageVersion>
```

and extend the comment above it with one sentence: `PackageVersion is pinned too, because deps.json reports it and plugins are checked against it.`

- [ ] **Step 2: Verify locally**

Run: `dotnet build src/HomeBlaze/HomeBlaze.Plugins.Tests -p:LibraryPackageVersion=0.9.3 -o /tmp/deps-check` then `grep -o '"Namotion.Interceptor/[^"]*"\|"HomeBlaze.Abstractions/[^"]*"' /tmp/deps-check/HomeBlaze.Plugins.Tests.deps.json | sort -u`, then `rm -rf /tmp/deps-check`.
Expected: `"HomeBlaze.Abstractions/1.0.0"` and `"Namotion.Interceptor/0.9.3"`. Build once more without the property afterwards so the normal `obj` state is restored.

- [ ] **Step 3: Pass the version in container-publish**

In the `Compute tags` step, set `$libraryVersion = $version` in the release branch, and in the else branch:

```powershell
            $libraryVersion = (gh release view --repo $env:GITHUB_REPOSITORY --json tagName -q .tagName) -replace '^v', ''
            if ($LASTEXITCODE -ne 0) { throw "Could not read the latest release" }
```

and output it: `echo "LIBRARY_VERSION=$libraryVersion" >> $env:GITHUB_OUTPUT`.

In `Publish multi-arch image`, add `-p:LibraryPackageVersion=${{ steps.tags.outputs.LIBRARY_VERSION }}` and replace the comment `# Version is not overridden: ...` with `# Version is not overridden: HomeBlaze pins it to 1.0.0 because plugins bind by assembly version. LibraryPackageVersion only changes the library versions reported in deps.json.`

- [ ] **Step 4: Check it in the smoke test**

In `container-build`, `Build image archive`, add `-p:LibraryPackageVersion=9.9.9`. In `Smoke test image`, after the health loop:

```powershell
          $deps = docker exec homeblaze cat /app/HomeBlaze.deps.json
          if (-not ($deps -match '"Namotion.Interceptor/9\.9\.9"')) { throw "deps.json does not report the library package version" }
          if (-not ($deps -match '"HomeBlaze.Abstractions/1\.0\.0"')) { throw "deps.json does not report the pinned HomeBlaze version" }
```

Check the image's working directory is `/app` (it is the .NET SDK container default); adjust the path if the container sets another one.

- [ ] **Step 5: Commit**

```bash
git add src/Directory.Build.props src/HomeBlaze/Directory.Build.props .github/workflows/build.yml
git commit -m "ci: report the released library versions in the HomeBlaze image deps.json"
```

---

### Task 12: Documentation

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze/Data/Files/Docs/architecture/design/plugins.md`
- Modify: `src/HomeBlaze/HomeBlaze/Data/Files/Docs/administration/configuration.md`
- Modify: `src/HomeBlaze/HomeBlaze/Data/Files/Docs/architecture/project-structure.md`
- Check: `git grep -n "PluginManager\|PluginLoader\|PluginConfigurationPath\|PluginsCache\|AddHomeBlazePlugins" -- src/HomeBlaze docs ':!docs/superpowers'` must be empty when done.

- [ ] **Step 1: plugins.md**

- Replace the `Configuration (Files/Plugins.json)` section: plugin providers are `NuGetPluginProvider` subjects anywhere in the tree; the Seed ships `Files/Plugins.json`; update the JSON example (`$type` `HomeBlaze.Plugins.NuGetPluginProvider`, no `cacheDirectory`) and the field table (`cacheDirectory`: relative to the data folder, default `Plugins/Cache`; `feeds`: relative folder paths resolve against the data folder).
- Subject model: rename to `NuGetPluginProvider`, list `CacheDirectory`, `IsRestartRequired`, the `Add Plugin` and `Retry Failed Plugins` operations, and the restart rules (adding is immediate; removing, version changes and loader setting changes need a restart).
- Replace the bootstrap sequence diagram: root load, storage scan with `UnknownSubject` placeholders, providers start and add assemblies to `TypeProvider`, `TypesChanged`, storages upgrade placeholders.
- New section `Unknown Types`: what `UnknownSubject` shows, that its file is never rewritten, that editing its JSON recreates it.
- New section `Multiple Providers`: one loader per provider, shared contracts load once per process and the first loaded version wins, keep plugins sharing contracts in one provider when their contract versions differ, duplicate type names are skipped with a warning.
- New section `Writing a Provider`: any subject can be a provider by getting `TypeProvider` injected and calling `AddAssemblies`; storages upgrade placeholders automatically; removing assemblies is not supported.
- Set front matter `status` to `Implemented`.

- [ ] **Step 2: configuration.md**

- Remove the `PluginConfigurationPath` section and its row in the environment variable table.
- Data folder layout: add `Plugins/Cache/  downloaded plugin packages` and a sentence that it can be excluded from backups.
- Replace the sentence about relative feed URLs and the cache resolving against the application directory with the data folder rule.

- [ ] **Step 3: project-structure.md**

Replace the `PluginLoader` bullet with `NuGetPluginProvider`: a subject that loads NuGet plugins at runtime and adds their assemblies to `TypeProvider`. Remove the `AddHomeBlazePlugins(path)` table row.

- [ ] **Step 4: Check links and style**

Run: `grep -rn "—" src/HomeBlaze/HomeBlaze/Data/Files/Docs | head` (expect nothing new) and follow each changed relative link to make sure the target exists.

- [ ] **Step 5: Commit**

```bash
git add src/HomeBlaze/HomeBlaze/Data/Files/Docs
git commit -m "docs: describe HomeBlaze plugin providers in the subject tree"
```

---

### Task 13: Final verification

- [ ] **Step 1: Full build and unit tests**

Run: `dotnet build src/Namotion.Interceptor.slnx` and `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`
Expected: build succeeds with no warnings, all tests pass.

- [ ] **Step 2: Integration tests for the touched areas**

Run: `dotnet test src/HomeBlaze/Namotion.NuGet.Plugins.Tests --filter "Category=Integration"`, `dotnet test src/HomeBlaze/HomeBlaze.Plugins.Tests --filter "Category=Integration"`, `dotnet test src/HomeBlaze/HomeBlaze.E2E.Tests`
Expected: all pass.

- [ ] **Step 3: Container smoke test locally**

Run the `container-build` steps from `.github/workflows/build.yml` locally (publish the linux-x64 archive with `-p:LibraryPackageVersion=9.9.9`, load it, run it with an empty data folder, check `/health`, the seeded files and the `deps.json` checks).
Expected: healthy, seeded, `deps.json` reports `Namotion.Interceptor/9.9.9` and `HomeBlaze.Abstractions/1.0.0`, and the log shows the seed `Plugins.json` loading as `NuGetPluginProvider` with no plugins and no errors.

- [ ] **Step 4: Remove the working spec and plan**

As in the previous HomeBlaze feature, the spec and plan are working documents. Delete them in a final commit before the pull request:

```bash
git rm docs/superpowers/specs/2026-10-06-homeblaze-plugin-providers-design.md docs/superpowers/plans/2026-10-06-homeblaze-plugin-providers.md
git commit -m "chore: remove the working spec and plan"
```
