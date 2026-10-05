# HomeBlaze Container Image, Aspire AppHost and Self-Hosting Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Publish HomeBlaze as a multi-arch container image on GHCR with a self-contained `/data` instance folder, add OpenTelemetry/Seq/health checks, an Aspire AppHost for development, and a compose-based setup guide.

**Architecture:** The folder containing `Root.json` becomes the HomeBlaze "data directory". `RootManager` publishes it through a new `IDataDirectoryProvider` context service, and storage, SQLite history and OPC UA resolve their paths against it. A seeder copies the shipped defaults into an empty data folder on first start. The image is built by the .NET SDK (`PublishContainer`), and an Aspire AppHost plus a handwritten compose file orchestrate HomeBlaze with Seq, n8n and an OPC UA simulator.

**Tech Stack:** .NET 10, ASP.NET Core Blazor Server, Aspire 13.5.4, OpenTelemetry 1.19, Seq, xUnit + Moq, GitHub Actions, GHCR.

**Spec:** `docs/superpowers/specs/2026-10-05-homeblaze-container-aspire-design.md`

**Repository rules that apply to every task (from AGENTS.md):**
- Commit messages: no AI attribution, no `Co-Authored-By` trailer.
- Tests: `When<Condition>_Then<Expected>` names, explicit `// Arrange`, `// Act`, `// Assert` comments, no `Task.Delay`/`Thread.Sleep`.
- Markdown: no em dashes, no hard wrapping.
- Library code outside `src/HomeBlaze` must not mention HomeBlaze. This plan only touches `src/HomeBlaze`, `.github`, `.gitignore`, `docs/superpowers` and `src/Directory.Packages.props`/`src/Namotion.Interceptor.slnx`.
- Warnings are errors. Do not add analyzer suppressions without asking the user.

---

## File Structure

| File | Action | Responsibility |
|---|---|---|
| `src/HomeBlaze/HomeBlaze.Abstractions/IDataDirectoryProvider.cs` | Create | Context service contract: the instance data folder |
| `src/HomeBlaze/HomeBlaze.Services/HomeBlazePaths.cs` | Create | Setting keys, defaults, root and plugin configuration path resolution |
| `src/HomeBlaze/HomeBlaze.Services/DataDirectorySeeder.cs` | Create | First-start copy of shipped defaults |
| `src/HomeBlaze/HomeBlaze.Services/RootManager.cs` | Modify | Resolve `ConfigurationPath` once, implement and register `IDataDirectoryProvider` |
| `src/HomeBlaze/HomeBlaze.Storage/FluentStorageContainer.cs` | Modify | Resolve the storage folder against the data directory |
| `src/HomeBlaze/HomeBlaze.Storage/Files/JsonFile.cs`, `GenericFile.cs` | Modify | Use the resolved storage folder for file metadata |
| `src/HomeBlaze/HomeBlaze.History.Sqlite/SqliteDatabaseLocation.cs`, `SqliteHistoryStoreSubject.cs` | Modify | Default store at `<data>/History/Sqlite` |
| `src/HomeBlaze/HomeBlaze.OpcUa/OpcUaCertificateStoreLocation.cs` | Create | `<data>/OpcUa/<Role>/Pki` |
| `src/HomeBlaze/HomeBlaze.OpcUa/OpcUaServer.cs`, `OpcUaClient.cs` | Modify | Use the certificate store location |
| `src/HomeBlaze/HomeBlaze/Data/**` | Move | `Data/*` to `Data/Files/*`, `root.json` to `Data/Root.json` |
| `src/HomeBlaze/HomeBlaze/HomeBlaze.csproj` | Modify | Data items, ServiceDefaults reference, container properties, plugin packages in publish |
| `src/HomeBlaze/HomeBlaze/Program.cs` | Modify | Seeding, plugin path, service defaults |
| `src/HomeBlaze/HomeBlaze.ServiceDefaults/*` | Create | OpenTelemetry, Seq, health endpoints |
| `src/HomeBlaze/HomeBlaze.AppHost/*` | Create | Aspire AppHost |
| `src/HomeBlaze/docker-compose.yml` | Create | Self-hosting compose file |
| `.github/workflows/build.yml` | Modify | `container-build` and `container-publish` jobs |
| `src/HomeBlaze/HomeBlaze/Data/Files/Docs/**` | Modify | Installation, configuration, upgrading, monitoring, Aspire docs |

---

### Task 1: Data directory contract and path helper

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Abstractions/IDataDirectoryProvider.cs`
- Create: `src/HomeBlaze/HomeBlaze.Services/HomeBlazePaths.cs`
- Test: `src/HomeBlaze/HomeBlaze.Services.Tests/HomeBlazePathsTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `src/HomeBlaze/HomeBlaze.Services.Tests/HomeBlazePathsTests.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using Moq;

namespace HomeBlaze.Services.Tests;

public class HomeBlazePathsTests
{
    [Fact]
    public void WhenRootConfigFileIsNotSet_ThenDefaultsToDataRootJsonInWorkingDirectory()
    {
        // Arrange
        var configuration = CreateConfiguration(rootConfigFile: null, pluginConfigurationPath: null);

        // Act
        var path = HomeBlazePaths.GetRootConfigurationPath(configuration);

        // Assert
        Assert.Equal(Path.GetFullPath(Path.Combine("Data", "Root.json")), path);
    }

    [Fact]
    public void WhenConfigurationIsNull_ThenDefaultsToDataRootJsonInWorkingDirectory()
    {
        // Act
        var path = HomeBlazePaths.GetRootConfigurationPath(null);

        // Assert
        Assert.Equal(Path.GetFullPath(Path.Combine("Data", "Root.json")), path);
    }

    [Fact]
    public void WhenRootConfigFileIsAbsolute_ThenItIsUsedAsIs()
    {
        // Arrange
        var rootFile = Path.Combine(Path.GetTempPath(), "homeblaze-instance", "Root.json");
        var configuration = CreateConfiguration(rootFile, pluginConfigurationPath: null);

        // Act
        var path = HomeBlazePaths.GetRootConfigurationPath(configuration);

        // Assert
        Assert.Equal(rootFile, path);
    }

    [Fact]
    public void WhenPluginConfigurationPathIsNotSet_ThenDefaultsToFilesPluginsJsonInDataDirectory()
    {
        // Arrange
        var dataDirectory = Path.Combine(Path.GetTempPath(), "homeblaze-instance");
        var configuration = CreateConfiguration(Path.Combine(dataDirectory, "Root.json"), pluginConfigurationPath: null);

        // Act
        var path = HomeBlazePaths.GetPluginConfigurationPath(configuration);

        // Assert
        Assert.Equal(Path.Combine(dataDirectory, "Files", "Plugins.json"), path);
    }

    [Fact]
    public void WhenPluginConfigurationPathIsRelative_ThenResolvesAgainstDataDirectory()
    {
        // Arrange
        var dataDirectory = Path.Combine(Path.GetTempPath(), "homeblaze-instance");
        var configuration = CreateConfiguration(Path.Combine(dataDirectory, "Root.json"), "Other/Plugins.json");

        // Act
        var path = HomeBlazePaths.GetPluginConfigurationPath(configuration);

        // Assert
        Assert.Equal(Path.Combine(dataDirectory, "Other", "Plugins.json"), path);
    }

    [Fact]
    public void WhenPluginConfigurationPathIsAbsolute_ThenItIsUsedAsIs()
    {
        // Arrange
        var pluginFile = Path.Combine(Path.GetTempPath(), "elsewhere", "Plugins.json");
        var configuration = CreateConfiguration(rootConfigFile: null, pluginFile);

        // Act
        var path = HomeBlazePaths.GetPluginConfigurationPath(configuration);

        // Assert
        Assert.Equal(pluginFile, path);
    }

    private static IConfiguration CreateConfiguration(string? rootConfigFile, string? pluginConfigurationPath)
    {
        var configuration = new Mock<IConfiguration>();
        configuration.Setup(instance => instance[HomeBlazePaths.RootConfigurationFileKey]).Returns(rootConfigFile);
        configuration.Setup(instance => instance[HomeBlazePaths.PluginConfigurationPathKey]).Returns(pluginConfigurationPath);
        return configuration.Object;
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Services.Tests --filter "FullyQualifiedName~HomeBlazePathsTests"`
Expected: build failure, `The name 'HomeBlazePaths' does not exist in the current context`.

- [ ] **Step 3: Create the contract**

Create `src/HomeBlaze/HomeBlaze.Abstractions/IDataDirectoryProvider.cs`:

```csharp
namespace HomeBlaze.Abstractions;

/// <summary>
/// Provides the instance data directory: the folder that contains the root configuration file.
/// Relative paths in configuration resolve against it.
/// </summary>
public interface IDataDirectoryProvider
{
    /// <summary>
    /// Gets the full path of the instance data directory.
    /// </summary>
    string DataDirectory { get; }
}
```

- [ ] **Step 4: Create the path helper**

Create `src/HomeBlaze/HomeBlaze.Services/HomeBlazePaths.cs`:

```csharp
using Microsoft.Extensions.Configuration;

namespace HomeBlaze.Services;

/// <summary>
/// Setting keys, defaults and resolution of the HomeBlaze instance paths.
/// </summary>
public static class HomeBlazePaths
{
    /// <summary>Setting that points to the root configuration file.</summary>
    public const string RootConfigurationFileKey = "HomeBlaze:RootConfigFile";

    /// <summary>Setting that points to the folder copied into an empty data directory on first start.</summary>
    public const string SeedDirectoryKey = "HomeBlaze:SeedDirectory";

    /// <summary>Setting that overrides the plugin configuration file.</summary>
    public const string PluginConfigurationPathKey = "PluginConfigurationPath";

    /// <summary>Default root configuration file, relative to the working directory.</summary>
    public static readonly string DefaultRootConfigurationFile = Path.Combine("Data", "Root.json");

    /// <summary>Default plugin configuration file, relative to the data directory.</summary>
    public static readonly string DefaultPluginConfigurationFile = Path.Combine("Files", "Plugins.json");

    /// <summary>
    /// Resolves the full path of the root configuration file. A relative setting resolves against the working directory.
    /// </summary>
    public static string GetRootConfigurationPath(IConfiguration? configuration)
    {
        return Path.GetFullPath(configuration?[RootConfigurationFileKey] ?? DefaultRootConfigurationFile);
    }

    /// <summary>
    /// Resolves the full path of the plugin configuration file. A relative setting resolves against the data directory.
    /// </summary>
    public static string GetPluginConfigurationPath(IConfiguration configuration)
    {
        var dataDirectory = Path.GetDirectoryName(GetRootConfigurationPath(configuration))!;
        return Path.GetFullPath(configuration[PluginConfigurationPathKey] ?? DefaultPluginConfigurationFile, dataDirectory);
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Services.Tests --filter "FullyQualifiedName~HomeBlazePathsTests"`
Expected: 6 passed.

- [ ] **Step 6: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Abstractions/IDataDirectoryProvider.cs src/HomeBlaze/HomeBlaze.Services/HomeBlazePaths.cs src/HomeBlaze/HomeBlaze.Services.Tests/HomeBlazePathsTests.cs
git commit -m "feat: add HomeBlaze instance path resolution"
```

---

### Task 2: RootManager publishes the data directory

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.Services/RootManager.cs`
- Test: `src/HomeBlaze/HomeBlaze.Services.Tests/RootManagerDataDirectoryTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `src/HomeBlaze/HomeBlaze.Services.Tests/RootManagerDataDirectoryTests.cs`:

```csharp
using HomeBlaze.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Namotion.Interceptor;

namespace HomeBlaze.Services.Tests;

public class RootManagerDataDirectoryTests
{
    [Fact]
    public void WhenRootConfigFileIsSet_ThenConfigurationPathAndDataDirectoryFollowIt()
    {
        // Arrange
        var dataDirectory = Path.Combine(Path.GetTempPath(), "homeblaze-instance");
        var rootFile = Path.Combine(dataDirectory, "Root.json");

        // Act
        var (rootManager, _) = CreateRootManager(rootFile);

        // Assert
        Assert.Equal(rootFile, rootManager.ConfigurationPath);
        Assert.Equal(dataDirectory, rootManager.DataDirectory);
    }

    [Fact]
    public void WhenRootManagerIsCreated_ThenItIsRegisteredAsDataDirectoryProvider()
    {
        // Arrange
        var rootFile = Path.Combine(Path.GetTempPath(), "homeblaze-instance", "Root.json");

        // Act
        var (rootManager, context) = CreateRootManager(rootFile);

        // Assert
        Assert.Same(rootManager, context.TryGetService<IDataDirectoryProvider>());
    }

    private static (RootManager RootManager, IInterceptorSubjectContext Context) CreateRootManager(string rootFile)
    {
        var typeProvider = new TypeProvider();
        var typeRegistry = new SubjectTypeRegistry(typeProvider);
        var serializer = new ConfigurableSubjectSerializer(typeProvider, new ServiceCollection().BuildServiceProvider());
        var context = InterceptorSubjectContext.Create();

        var configuration = new Mock<IConfiguration>();
        configuration.Setup(instance => instance[HomeBlazePaths.RootConfigurationFileKey]).Returns(rootFile);

        RootManager? rootManager = null;
        var pathResolver = new SubjectPathResolver(() => rootManager!.Root);
        rootManager = new RootManager(typeRegistry, serializer, context, pathResolver, configuration.Object);
        return (rootManager, context);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Services.Tests --filter "FullyQualifiedName~RootManagerDataDirectoryTests"`
Expected: build failure, `'RootManager' does not contain a definition for 'ConfigurationPath'`.

- [ ] **Step 3: Implement**

In `src/HomeBlaze/HomeBlaze.Services/RootManager.cs`:

1. Change the class declaration and summary:

```csharp
/// <summary>
/// Manages loading and access to the root subject.
/// Bootstraps the system from the root configuration file and provides the instance data directory.
/// </summary>
public class RootManager : BackgroundService, IConfigurationWriter, IDataDirectoryProvider
```

2. Remove the field `private string? _configurationPath;` and add these properties below `IsLoaded`:

```csharp
    /// <summary>
    /// Full path of the root configuration file.
    /// </summary>
    public string ConfigurationPath { get; }

    /// <inheritdoc />
    public string DataDirectory { get; }
```

3. In the constructor, after `_logger = logger;`, add:

```csharp
        ConfigurationPath = HomeBlazePaths.GetRootConfigurationPath(configuration);
        DataDirectory = Path.GetDirectoryName(ConfigurationPath)!;
```

and after `context.AddService(this);` add:

```csharp
        // Storage, history and connectors resolve their relative paths against the data directory.
        context.AddService<IDataDirectoryProvider>(this);
```

4. In `LoadAsync`, replace

```csharp
        var configFileName = _configuration?["HomeBlaze:RootConfigFile"] ?? "root.json";
        _configurationPath = Path.GetFullPath(configFileName);
        _logger?.LogInformation("Loading root configuration from: {Path}", _configurationPath);

        if (!File.Exists(_configurationPath))
        {
            throw new FileNotFoundException($"Root configuration file not found: {_configurationPath}", _configurationPath);
        }

        var json = await File.ReadAllTextAsync(_configurationPath, cancellationToken);
```

with

```csharp
        _logger?.LogInformation("Loading root configuration from: {Path}", ConfigurationPath);

        if (!File.Exists(ConfigurationPath))
        {
            throw new FileNotFoundException($"Root configuration file not found: {ConfigurationPath}", ConfigurationPath);
        }

        var json = await File.ReadAllTextAsync(ConfigurationPath, cancellationToken);
```

5. In `WriteConfigurationAsync`, replace

```csharp
        if (string.IsNullOrEmpty(_configurationPath))
            throw new InvalidOperationException("Cannot save: config path is not set");

        _logger?.LogInformation("Saving root configuration to: {Path}", _configurationPath);

        var json = _serializer.Serialize(Root);
        await File.WriteAllTextAsync(_configurationPath, json, cancellationToken);
```

with

```csharp
        _logger?.LogInformation("Saving root configuration to: {Path}", ConfigurationPath);

        var json = _serializer.Serialize(Root);
        await File.WriteAllTextAsync(ConfigurationPath, json, cancellationToken);
```

6. If `_configuration` is now unused, remove the field and its assignment but keep the constructor parameter. Build with `dotnet build src/HomeBlaze/HomeBlaze.Services` to confirm no warnings.

- [ ] **Step 4: Run the Services tests**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Services.Tests`
Expected: all pass, including `RootManagerRootLoadedTests` and `ConfigurableSubjectStartupTests`. They pass the root file through `HomeBlaze:RootConfigFile`, which is unchanged.

- [ ] **Step 5: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Services/RootManager.cs src/HomeBlaze/HomeBlaze.Services.Tests/RootManagerDataDirectoryTests.cs
git commit -m "feat: let RootManager provide the instance data directory"
```

---

### Task 3: First-start seeding

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Services/DataDirectorySeeder.cs`
- Test: `src/HomeBlaze/HomeBlaze.Services.Tests/DataDirectorySeederTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `src/HomeBlaze/HomeBlaze.Services.Tests/DataDirectorySeederTests.cs`:

```csharp
namespace HomeBlaze.Services.Tests;

public class DataDirectorySeederTests : IDisposable
{
    private readonly DirectoryInfo _seedDirectory = Directory.CreateTempSubdirectory("homeblaze-seed-");
    private readonly DirectoryInfo _dataDirectory = Directory.CreateTempSubdirectory("homeblaze-data-");

    public DataDirectorySeederTests()
    {
        File.WriteAllText(Path.Combine(_seedDirectory.FullName, "Root.json"), "{}");
        Directory.CreateDirectory(Path.Combine(_seedDirectory.FullName, "Files", "Devices"));
        File.WriteAllText(Path.Combine(_seedDirectory.FullName, "Files", "Plugins.json"), "seed-plugins");
        File.WriteAllText(Path.Combine(_seedDirectory.FullName, "Files", "Devices", "Device.json"), "seed-device");
    }

    private string RootFile => Path.Combine(_dataDirectory.FullName, "Root.json");

    [Fact]
    public void WhenRootFileIsMissingAndSeedIsSet_ThenSeedIsCopied()
    {
        // Act
        var copiedFileCount = DataDirectorySeeder.SeedIfMissing(RootFile, _seedDirectory.FullName);

        // Assert
        Assert.Equal(3, copiedFileCount);
        Assert.True(File.Exists(RootFile));
        Assert.Equal("seed-plugins", File.ReadAllText(Path.Combine(_dataDirectory.FullName, "Files", "Plugins.json")));
        Assert.Equal("seed-device", File.ReadAllText(Path.Combine(_dataDirectory.FullName, "Files", "Devices", "Device.json")));
    }

    [Fact]
    public void WhenRootFileExists_ThenNothingIsCopied()
    {
        // Arrange
        File.WriteAllText(RootFile, "existing");

        // Act
        var copiedFileCount = DataDirectorySeeder.SeedIfMissing(RootFile, _seedDirectory.FullName);

        // Assert
        Assert.Equal(0, copiedFileCount);
        Assert.Equal("existing", File.ReadAllText(RootFile));
        Assert.False(Directory.Exists(Path.Combine(_dataDirectory.FullName, "Files")));
    }

    [Fact]
    public void WhenSeedDirectoryIsNotSet_ThenNothingIsCopied()
    {
        // Act
        var copiedFileCount = DataDirectorySeeder.SeedIfMissing(RootFile, null);

        // Assert
        Assert.Equal(0, copiedFileCount);
        Assert.False(File.Exists(RootFile));
    }

    [Fact]
    public void WhenSeedDirectoryDoesNotExist_ThenNothingIsCopied()
    {
        // Act
        var copiedFileCount = DataDirectorySeeder.SeedIfMissing(RootFile, Path.Combine(_seedDirectory.FullName, "missing"));

        // Assert
        Assert.Equal(0, copiedFileCount);
        Assert.False(File.Exists(RootFile));
    }

    [Fact]
    public void WhenDataDirectoryHasFilesButNoRootFile_ThenExistingFilesAreKept()
    {
        // Arrange
        Directory.CreateDirectory(Path.Combine(_dataDirectory.FullName, "Files"));
        File.WriteAllText(Path.Combine(_dataDirectory.FullName, "Files", "Plugins.json"), "user-plugins");

        // Act
        var copiedFileCount = DataDirectorySeeder.SeedIfMissing(RootFile, _seedDirectory.FullName);

        // Assert
        Assert.Equal(2, copiedFileCount);
        Assert.Equal("user-plugins", File.ReadAllText(Path.Combine(_dataDirectory.FullName, "Files", "Plugins.json")));
        Assert.True(File.Exists(RootFile));
    }

    public void Dispose()
    {
        _seedDirectory.Delete(recursive: true);
        _dataDirectory.Delete(recursive: true);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Services.Tests --filter "FullyQualifiedName~DataDirectorySeederTests"`
Expected: build failure, `The name 'DataDirectorySeeder' does not exist in the current context`.

- [ ] **Step 3: Implement**

Create `src/HomeBlaze/HomeBlaze.Services/DataDirectorySeeder.cs`:

```csharp
namespace HomeBlaze.Services;

/// <summary>
/// Fills an empty instance data directory with the shipped defaults on first start.
/// </summary>
public static class DataDirectorySeeder
{
    /// <summary>
    /// Copies the seed directory into the folder of <paramref name="rootConfigurationPath"/> when that file does not exist
    /// and <paramref name="seedDirectory"/> is set and exists. Existing files are never overwritten.
    /// </summary>
    /// <returns>The number of copied files; zero when nothing was seeded.</returns>
    public static int SeedIfMissing(string rootConfigurationPath, string? seedDirectory)
    {
        if (string.IsNullOrWhiteSpace(seedDirectory) ||
            File.Exists(rootConfigurationPath) ||
            !Directory.Exists(seedDirectory))
        {
            return 0;
        }

        var sourceDirectory = Path.GetFullPath(seedDirectory);
        var dataDirectory = Path.GetDirectoryName(Path.GetFullPath(rootConfigurationPath))!;
        var rootFileName = Path.GetFileName(rootConfigurationPath);

        // The root file is copied last: its presence is what disables seeding, so an interrupted
        // copy must not leave a root file behind that blocks the next attempt.
        var sourceFiles = Directory
            .EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories)
            .OrderBy(file => Path.GetRelativePath(sourceDirectory, file) == rootFileName);

        var copiedFileCount = 0;
        foreach (var sourceFile in sourceFiles)
        {
            var targetFile = Path.Combine(dataDirectory, Path.GetRelativePath(sourceDirectory, sourceFile));
            if (File.Exists(targetFile))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
            File.Copy(sourceFile, targetFile);
            copiedFileCount++;
        }

        return copiedFileCount;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Services.Tests --filter "FullyQualifiedName~DataDirectorySeederTests"`
Expected: 5 passed.

- [ ] **Step 5: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Services/DataDirectorySeeder.cs src/HomeBlaze/HomeBlaze.Services.Tests/DataDirectorySeederTests.cs
git commit -m "feat: seed an empty HomeBlaze data directory on first start"
```

---

### Task 4: Storage resolves its folder against the data directory

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.Storage/FluentStorageContainer.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Storage/Files/JsonFile.cs:56`
- Modify: `src/HomeBlaze/HomeBlaze.Storage/Files/GenericFile.cs:64`
- Test: `src/HomeBlaze/HomeBlaze.Storage.Tests/FluentStorageContainerPathTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `src/HomeBlaze/HomeBlaze.Storage.Tests/FluentStorageContainerPathTests.cs`:

```csharp
using HomeBlaze.Abstractions;
using HomeBlaze.Services;
using HomeBlaze.Storage.Internal;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Tests;

public class FluentStorageContainerPathTests : IDisposable
{
    private readonly DirectoryInfo _dataDirectory = Directory.CreateTempSubdirectory("homeblaze-data-");

    [Fact]
    public async Task WhenConnectionStringIsRelative_ThenStorageResolvesAgainstDataDirectory()
    {
        // Arrange
        var filesDirectory = Directory.CreateDirectory(Path.Combine(_dataDirectory.FullName, "Files"));
        File.WriteAllText(Path.Combine(filesDirectory.FullName, "notes.txt"), "hello");

        var storage = CreateStorage(withDataDirectory: true);
        storage.ConnectionString = "Files";
        storage.EnableFileWatching = false;

        // Act
        await storage.ConnectAsync(CancellationToken.None);

        // Assert
        Assert.Single(storage.Children);
        Assert.Equal(Path.Combine(filesDirectory.FullName, "notes.txt"), storage.GetFileSystemPath("notes.txt"));
        storage.Dispose();
    }

    [Fact]
    public void WhenConnectionStringIsAbsolute_ThenDataDirectoryIsIgnored()
    {
        // Arrange
        var absoluteDirectory = Path.Combine(Path.GetTempPath(), "homeblaze-absolute");
        var storage = CreateStorage(withDataDirectory: true);
        storage.ConnectionString = absoluteDirectory;

        // Act
        var path = storage.GetFileSystemPath("/Devices/a.json");

        // Assert
        Assert.Equal(Path.Combine(absoluteDirectory, "Devices", "a.json"), path);
    }

    [Fact]
    public void WhenNoDataDirectoryIsProvided_ThenRelativeConnectionStringResolvesAgainstWorkingDirectory()
    {
        // Arrange
        var storage = CreateStorage(withDataDirectory: false);
        storage.ConnectionString = "Files";

        // Act
        var path = storage.GetFileSystemPath("a.json");

        // Assert
        Assert.Equal(Path.GetFullPath(Path.Combine("Files", "a.json")), path);
    }

    private FluentStorageContainer CreateStorage(bool withDataDirectory)
    {
        var typeProvider = new TypeProvider();
        var typeRegistry = new SubjectTypeRegistry(typeProvider);

        var services = new ServiceCollection();
        services.AddSingleton(typeProvider);
        services.AddSingleton(typeRegistry);
        services.AddSingleton<IInterceptorSubjectContext>(InterceptorSubjectContext.Create());
        services.AddSingleton<SubjectFactory>();
        services.AddSingleton<ConfigurableSubjectSerializer>();
        services.AddSingleton<RootManager>();
        services.AddSingleton(sp => new SubjectPathResolver(() => sp.GetRequiredService<RootManager>().Root));
        services.AddSingleton<MarkdownContentParser>();
        var serviceProvider = services.BuildServiceProvider();

        var storage = new FluentStorageContainer(
            typeRegistry,
            serviceProvider.GetRequiredService<ConfigurableSubjectSerializer>(),
            serviceProvider);

        if (withDataDirectory)
        {
            var context = InterceptorSubjectContext.Create();
            context.AddService<IDataDirectoryProvider>(new TestDataDirectoryProvider(_dataDirectory.FullName));
            ((IInterceptorSubject)storage).Context.AddFallbackContext(context);
        }

        return storage;
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

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests --filter "FullyQualifiedName~FluentStorageContainerPathTests"`
Expected: build failure, `'FluentStorageContainer' does not contain a definition for 'GetFileSystemPath'`.

- [ ] **Step 3: Implement the resolution in FluentStorageContainer**

In `src/HomeBlaze/HomeBlaze.Storage/FluentStorageContainer.cs`:

1. Add a field next to `_fileWatcher`:

```csharp
    private string? _storageDirectory;
```

2. Add these members after `ApplyConfigurationAsync`:

```csharp
    /// <summary>
    /// Returns the file system path of a storage-relative path, resolving a relative
    /// <see cref="ConnectionString"/> against the instance data directory.
    /// </summary>
    internal string GetFileSystemPath(string relativePath)
    {
        return Path.GetFullPath(Path.Combine(_storageDirectory ?? ResolveStorageDirectory(), relativePath.TrimStart('/', '\\')));
    }

    private string ResolveStorageDirectory()
    {
        var baseDirectory = ((IInterceptorSubject)this).Context.TryGetService<IDataDirectoryProvider>()?.DataDirectory
            ?? Directory.GetCurrentDirectory();

        return string.IsNullOrEmpty(ConnectionString)
            ? baseDirectory
            : Path.GetFullPath(ConnectionString, baseDirectory);
    }
```

3. In `ConnectAsync`, directly after `Status = StorageStatus.Initializing;` add:

```csharp
        _storageDirectory = isInMemory ? null : ResolveStorageDirectory();
```

and replace

```csharp
                "disk" or "filesystem" => StorageFactory.Blobs.DirectoryFiles(
                    Path.GetFullPath(ConnectionString)),
```

with

```csharp
                "disk" or "filesystem" => StorageFactory.Blobs.DirectoryFiles(_storageDirectory!),
```

and replace `isInMemory ? "(in-memory)" : ConnectionString);` with `isInMemory ? "(in-memory)" : _storageDirectory);`.

4. In `StartFileWatching`, replace `Path.GetFullPath(ConnectionString),` with `_storageDirectory!,`.

5. Replace each of the four occurrences of

```csharp
        var fullPath = Path.GetFullPath(Path.Combine(ConnectionString, path));
```

(in `WriteConfigurationAsync`, `AddSubjectAsync`, `WriteBlobAsync`, `DeleteBlobAsync`) with

```csharp
        var fullPath = GetFileSystemPath(path);
```

6. Add `using HomeBlaze.Abstractions;` if it is missing (it is already imported at the top of the file).

- [ ] **Step 4: Use it for file metadata**

In `src/HomeBlaze/HomeBlaze.Storage/Files/JsonFile.cs` and `src/HomeBlaze/HomeBlaze.Storage/Files/GenericFile.cs`, replace

```csharp
                var fileInfo = new FileInfo(Path.Combine(container.ConnectionString, FullPath));
```

with

```csharp
                var fileInfo = new FileInfo(container.GetFileSystemPath(FullPath));
```

- [ ] **Step 5: Run the storage tests**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests`
Expected: all pass, including the existing `FluentStorageContainerTests`.

- [ ] **Step 6: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Storage src/HomeBlaze/HomeBlaze.Storage.Tests/FluentStorageContainerPathTests.cs
git commit -m "feat: resolve HomeBlaze storage paths against the data directory"
```

---

### Task 5: SQLite history defaults to the data directory

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.History.Sqlite/SqliteDatabaseLocation.cs`
- Modify: `src/HomeBlaze/HomeBlaze.History.Sqlite/SqliteHistoryStoreSubject.cs:86-92,235`
- Test: `src/HomeBlaze/HomeBlaze.History.Sqlite.Tests/SqliteDatabaseLocationTests.cs`

- [ ] **Step 1: Write the failing tests**

Append these tests inside the `SqliteDatabaseLocationTests` class:

```csharp
    [Fact]
    public void WhenDataDirectoryIsSet_ThenBaseDirectoryIsHistoryUnderDataDirectory()
    {
        // Arrange
        var dataDirectory = Path.Combine(Path.GetTempPath(), "homeblaze-instance");

        // Act
        var baseDirectory = SqliteDatabaseLocation.DefaultBaseDirectory(dataDirectory);

        // Assert
        Assert.Equal(Path.Combine(dataDirectory, "History"), baseDirectory);
    }

    [Fact]
    public void WhenDataDirectoryIsNull_ThenBaseDirectoryIsLocalApplicationData()
    {
        // Act
        var baseDirectory = SqliteDatabaseLocation.DefaultBaseDirectory(null);

        // Assert
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HomeBlaze"),
            baseDirectory);
    }

    [Fact]
    public void WhenDataDirectoryIsSetAndPathIsEmpty_ThenStoreIsHistorySqliteUnderDataDirectory()
    {
        // Arrange
        var dataDirectory = Path.Combine(Path.GetTempPath(), "homeblaze-instance");

        // Act
        var resolved = SqliteDatabaseLocation.Resolve(string.Empty, SqliteDatabaseLocation.DefaultBaseDirectory(dataDirectory));

        // Assert
        Assert.Equal(Path.Combine(dataDirectory, "History", "Sqlite"), resolved);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.History.Sqlite.Tests --filter "FullyQualifiedName~SqliteDatabaseLocationTests"`
Expected: build failure, `No overload for method 'DefaultBaseDirectory' takes 1 arguments`.

- [ ] **Step 3: Implement**

Replace the body of `src/HomeBlaze/HomeBlaze.History.Sqlite/SqliteDatabaseLocation.cs` with:

```csharp
namespace HomeBlaze.History.Sqlite;

/// <summary>
/// Resolves the directory that holds the SQLite partition database files. Relative paths resolve under the
/// history folder of the instance data directory, which is outside the scanned subject files. Without a data
/// directory they resolve under the per-user local application data folder. Absolute paths are used as-is.
/// </summary>
internal static class SqliteDatabaseLocation
{
    /// <summary>
    /// The folder name used when no database path is configured.
    /// </summary>
    public const string DefaultFolderName = "Sqlite";

    /// <summary>
    /// The base directory under which relative database paths resolve: <c>History</c> in the instance data
    /// directory, or the local application data folder with a "HomeBlaze" subfolder when there is none.
    /// </summary>
    public static string DefaultBaseDirectory(string? dataDirectory) =>
        dataDirectory is null
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HomeBlaze")
            : Path.Combine(dataDirectory, "History");

    /// <summary>
    /// Resolves the configured database path against the given base directory. Null, empty, or whitespace
    /// resolves to the default folder under the base directory. A rooted (absolute) path is returned
    /// unchanged; a relative path is combined with the base directory.
    /// </summary>
    public static string Resolve(string? configuredPath, string baseDirectory)
    {
        var value = string.IsNullOrWhiteSpace(configuredPath) ? DefaultFolderName : configuredPath.Trim();

        return Path.IsPathRooted(value) ? value : Path.Combine(baseDirectory, value);
    }
}
```

In `src/HomeBlaze/HomeBlaze.History.Sqlite/SqliteHistoryStoreSubject.cs`:

1. Replace line 235:

```csharp
        var directory = SqliteDatabaseLocation.Resolve(DatabasePath, SqliteDatabaseLocation.DefaultBaseDirectory());
```

with

```csharp
        var dataDirectory = context.TryGetService<IDataDirectoryProvider>()?.DataDirectory;
        var directory = SqliteDatabaseLocation.Resolve(DatabasePath, SqliteDatabaseLocation.DefaultBaseDirectory(dataDirectory));
```

2. Replace the `DatabasePath` XML summary with:

```csharp
    /// <summary>
    /// Directory that holds the partition database files. Relative paths resolve under the <c>History</c> folder of
    /// the instance data directory; absolute paths are used as-is; empty uses the default "Sqlite" folder.
    /// </summary>
```

3. Add `using HomeBlaze.Abstractions;` at the top if it is not there.

- [ ] **Step 4: Run the SQLite tests**

Run: `dotnet test src/HomeBlaze/HomeBlaze.History.Sqlite.Tests --filter "Category!=Integration"`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.History.Sqlite src/HomeBlaze/HomeBlaze.History.Sqlite.Tests/SqliteDatabaseLocationTests.cs
git commit -m "feat: store SQLite history in the HomeBlaze data directory"
```

---

### Task 6: OPC UA certificate stores in the data directory

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.OpcUa/OpcUaCertificateStoreLocation.cs`
- Modify: `src/HomeBlaze/HomeBlaze.OpcUa/OpcUaServer.cs` (configuration block around line 254)
- Modify: `src/HomeBlaze/HomeBlaze.OpcUa/OpcUaClient.cs` (configuration block around line 250)

There is no `HomeBlaze.OpcUa` test project. The helper is a two-line pure function, verified by the manual AppHost run in Task 13.

- [ ] **Step 1: Create the helper**

```csharp
using HomeBlaze.Abstractions;
using Namotion.Interceptor;

namespace HomeBlaze.OpcUa;

/// <summary>
/// Resolves the OPC UA certificate store folder for a role inside the instance data directory.
/// </summary>
internal static class OpcUaCertificateStoreLocation
{
    /// <summary>
    /// Returns <c>&lt;data&gt;/OpcUa/&lt;role&gt;/Pki</c>, or null when no data directory is available so the
    /// library default applies.
    /// </summary>
    public static string? Resolve(IInterceptorSubject subject, string role)
    {
        var dataDirectory = subject.Context.TryGetService<IDataDirectoryProvider>()?.DataDirectory;
        return dataDirectory is null ? null : Path.Combine(dataDirectory, "OpcUa", role, "Pki");
    }
}
```

- [ ] **Step 2: Use it in the server**

In `OpcUaServer.ExecuteAsync`, directly after the `var configuration = new OpcUaServerConfiguration { ... };` statement, add:

```csharp
            // Separate from the client store: the server cleans its own store on start.
            if (OpcUaCertificateStoreLocation.Resolve(this, "Server") is { } certificateStorePath)
            {
                configuration.CertificateStoreBasePath = certificateStorePath;
            }
```

- [ ] **Step 3: Use it in the client**

In `OpcUaClient.ExecuteAsync`, directly after the `var configuration = new OpcUaClientConfiguration { ... };` statement, add:

```csharp
            if (OpcUaCertificateStoreLocation.Resolve(this, "Client") is { } certificateStorePath)
            {
                configuration.CertificateStoreBasePath = certificateStorePath;
            }
```

If the compiler reports that `CertificateStoreBasePath` is init-only, move the assignment into the object initializer as `CertificateStoreBasePath = OpcUaCertificateStoreLocation.Resolve(this, "Client") ?? "pki",` (and `"Server"` for the server), with a comment that `"pki"` is the library default.

- [ ] **Step 4: Build**

Run: `dotnet build src/HomeBlaze/HomeBlaze.OpcUa`
Expected: Build succeeded, 0 warnings.

- [ ] **Step 5: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.OpcUa
git commit -m "feat: keep HomeBlaze OPC UA certificates in the data directory"
```

---

### Task 7: Move the development data into the instance layout and wire Program.cs

**Files:**
- Move: `src/HomeBlaze/HomeBlaze/Data/*` to `src/HomeBlaze/HomeBlaze/Data/Files/*`
- Move: `src/HomeBlaze/HomeBlaze/root.json` to `src/HomeBlaze/HomeBlaze/Data/Root.json`
- Modify: `src/HomeBlaze/HomeBlaze/HomeBlaze.csproj`
- Modify: `src/HomeBlaze/HomeBlaze/Program.cs:37-40`
- Modify: `.gitignore`

- [ ] **Step 1: Move the files**

```bash
cd src/HomeBlaze/HomeBlaze
mkdir -p Data/Files
for entry in Data/*; do
  if [ "$entry" != "Data/Files" ]; then git mv "$entry" Data/Files/; fi
done
git mv root.json Data/Root.json
cd -
```

- [ ] **Step 2: Point the root at Files**

Replace the content of `src/HomeBlaze/HomeBlaze/Data/Root.json` with:

```json
{
  "$type": "HomeBlaze.Storage.FluentStorageContainer",
  "storageType": "disk",
  "connectionString": "Files",
  "enableFileWatching": true
}
```

- [ ] **Step 3: Update the project items**

In `src/HomeBlaze/HomeBlaze/HomeBlaze.csproj`, replace

```xml
        <Content Update="Data\**\*" CopyToOutputDirectory="PreserveNewest" />
        <Content Update="root.json" CopyToOutputDirectory="PreserveNewest" />
        <Content Remove="Data\Plans.json" />
```

with

```xml
        <!-- The whole shipped tree is the seed for an empty data directory, Markdown included.
             Runtime folders (Data\History, Data\OpcUa) stay out of the output. -->
        <Content Remove="Data\**\*" />
        <None Remove="Data\**\*" />
        <None Include="Data\Root.json;Data\Files\**\*" Exclude="Data\Files\Plans.json" CopyToOutputDirectory="PreserveNewest" CopyToPublishDirectory="PreserveNewest" />
```

Keep `<Watch Remove="Data/**/*" />` unchanged.

- [ ] **Step 4: Ignore runtime folders**

Append to `.gitignore` under the existing `# HomeBlaze plugin output` block:

```
src/HomeBlaze/HomeBlaze/Data/History/
src/HomeBlaze/HomeBlaze/Data/OpcUa/
```

- [ ] **Step 5: Wire seeding and the plugin path in Program.cs**

In `src/HomeBlaze/HomeBlaze/Program.cs`, replace

```csharp
var pluginConfigPath = builder.Configuration.GetValue<string>("PluginConfigurationPath")
    ?? Path.Combine(AppContext.BaseDirectory, "Data", "Plugins.json");
```

with

```csharp
// Seeding must run before AddHomeBlazePlugins, which reads the plugin configuration during registration.
var seededFileCount = DataDirectorySeeder.SeedIfMissing(
    HomeBlazePaths.GetRootConfigurationPath(builder.Configuration),
    builder.Configuration[HomeBlazePaths.SeedDirectoryKey]);

var pluginConfigPath = HomeBlazePaths.GetPluginConfigurationPath(builder.Configuration);
```

and directly after `var app = builder.Build();` add:

```csharp
if (seededFileCount > 0)
{
    app.Logger.LogInformation("Seeded the data directory with {Count} default files.", seededFileCount);
}
```

`HomeBlaze.Services` is already imported at the top of the file.

- [ ] **Step 6: Build and check the output tree**

Run:

```bash
dotnet build src/HomeBlaze/HomeBlaze
ls src/HomeBlaze/HomeBlaze/bin/Debug/net10.0/Data src/HomeBlaze/HomeBlaze/bin/Debug/net10.0/Data/Files | head -20
ls src/HomeBlaze/HomeBlaze/bin/Debug/net10.0/Data/Files/Docs | head -3
```

Expected: `Data` contains `Root.json` and `Files`; `Files` contains `Dashboard.md`, `Devices`, `Docs`, `Plugins.json`; `Docs` contains Markdown files.

- [ ] **Step 7: Run HomeBlaze from source and check it loads**

Run (background), then probe:

```bash
dotnet run --project src/HomeBlaze/HomeBlaze --launch-profile http > /tmp/homeblaze-run.log 2>&1 &
echo $! > /tmp/homeblaze-run.pid
for attempt in $(seq 1 60); do curl -sf http://localhost:5192/ > /dev/null && break; sleep 2; done
grep -E "Loading root configuration|Connected to storage|Plugin loading complete" /tmp/homeblaze-run.log
kill "$(cat /tmp/homeblaze-run.pid)"
```

Expected log lines: `Loading root configuration from: .../HomeBlaze/Data/Root.json`, `Connected to storage: disk at .../HomeBlaze/Data/Files`, `Plugin loading complete: 2 loaded, 0 failed.`

- [ ] **Step 8: Run the E2E tests**

Run: `dotnet test src/HomeBlaze/HomeBlaze.E2E.Tests`
Expected: all pass. `WebTestingHostFactory` sets `HomeBlaze:RootConfigFile=testRoot.json` (data directory becomes the test output folder, so `./TestData` resolves as before) and an absolute `PluginConfigurationPath`.

- [ ] **Step 9: Run all HomeBlaze unit tests**

Run: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`
Expected: all pass.

- [ ] **Step 10: Commit**

```bash
git add -A src/HomeBlaze/HomeBlaze .gitignore
git commit -m "refactor: move HomeBlaze development data into the instance folder layout"
```

---

### Task 8: HomeBlaze.ServiceDefaults

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.ServiceDefaults/HomeBlaze.ServiceDefaults.csproj`
- Create: `src/HomeBlaze/HomeBlaze.ServiceDefaults/Extensions.cs`
- Modify: `src/Directory.Packages.props`
- Modify: `src/Namotion.Interceptor.slnx`
- Modify: `src/HomeBlaze/HomeBlaze/HomeBlaze.csproj`, `src/HomeBlaze/HomeBlaze/Program.cs`
- Test: `src/HomeBlaze/HomeBlaze.E2E.Tests/HealthEndpointTests.cs`

- [ ] **Step 1: Write the failing test**

Create `src/HomeBlaze/HomeBlaze.E2E.Tests/HealthEndpointTests.cs`:

```csharp
using System.Net;
using HomeBlaze.E2E.Tests.Infrastructure;

namespace HomeBlaze.E2E.Tests;

[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class HealthEndpointTests
{
    private readonly PlaywrightFixture _fixture;

    public HealthEndpointTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/alive")]
    public async Task WhenHealthEndpointIsRequested_ThenItReturnsOk(string path)
    {
        // Arrange
        using var client = new HttpClient { BaseAddress = new Uri(_fixture.ServerAddress) };

        // Act
        using var response = await client.GetAsync(path);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test src/HomeBlaze/HomeBlaze.E2E.Tests --filter "FullyQualifiedName~HealthEndpointTests"`
Expected: FAIL, either a non-OK status or a body other than `Healthy` (the Blazor router answers).

- [ ] **Step 3: Add package versions**

First confirm the Aspire packages exist in 13.5.4:

```bash
dotnet package search Aspire.Seq --exact-match --format json | grep -c '"13.5.4"'
dotnet package search Aspire.Hosting.Seq --exact-match --format json | grep -c '"13.5.4"'
```

Expected: `1` for each. If not, use the newest 13.5.x that both packages have and use it everywhere this plan says 13.5.4.

In `src/Directory.Packages.props`, add inside the existing `<ItemGroup>` (keep alphabetical order):

```xml
    <PackageVersion Include="Aspire.Hosting.Seq" Version="13.5.4" />
    <PackageVersion Include="Aspire.Seq" Version="13.5.4" />
    <PackageVersion Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" Version="1.19.1" />
    <PackageVersion Include="OpenTelemetry.Extensions.Hosting" Version="1.19.1" />
    <PackageVersion Include="OpenTelemetry.Instrumentation.AspNetCore" Version="1.19.0" />
    <PackageVersion Include="OpenTelemetry.Instrumentation.Http" Version="1.19.0" />
    <PackageVersion Include="OpenTelemetry.Instrumentation.Runtime" Version="1.19.0" />
```

- [ ] **Step 4: Create the project**

`src/HomeBlaze/HomeBlaze.ServiceDefaults/HomeBlaze.ServiceDefaults.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <IsAspireSharedProject>true</IsAspireSharedProject>
  </PropertyGroup>

  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />

    <PackageReference Include="Aspire.Seq" />
    <PackageReference Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" />
    <PackageReference Include="OpenTelemetry.Extensions.Hosting" />
    <PackageReference Include="OpenTelemetry.Instrumentation.AspNetCore" />
    <PackageReference Include="OpenTelemetry.Instrumentation.Http" />
    <PackageReference Include="OpenTelemetry.Instrumentation.Runtime" />
  </ItemGroup>

</Project>
```

`src/HomeBlaze/HomeBlaze.ServiceDefaults/Extensions.cs`:

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Adds OpenTelemetry, optional Seq export and health checks to HomeBlaze.
/// Based on the Aspire service defaults template, without the HTTP client resilience handler and service discovery:
/// device clients keep their own timeout and retry behavior.
/// </summary>
public static class Extensions
{
    private const string HealthEndpointPath = "/health";
    private const string AlivenessEndpointPath = "/alive";
    private const string SeqConnectionName = "seq";

    /// <summary>
    /// Adds OpenTelemetry, optional Seq export and the default health checks.
    /// </summary>
    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureOpenTelemetry();
        builder.AddDefaultHealthChecks();

        if (!string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString(SeqConnectionName)))
        {
            builder.AddSeqEndpoint(SeqConnectionName);
        }

        return builder;
    }

    /// <summary>
    /// Collects logs, metrics and traces and exports them over OTLP when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set.
    /// </summary>
    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();
            })
            .WithTracing(tracing =>
            {
                tracing.AddSource(builder.Environment.ApplicationName)
                    .AddAspNetCoreInstrumentation(options =>
                        options.Filter = context =>
                            !context.Request.Path.StartsWithSegments(HealthEndpointPath)
                            && !context.Request.Path.StartsWithSegments(AlivenessEndpointPath))
                    .AddHttpClientInstrumentation();
            });

        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        return builder;
    }

    /// <summary>
    /// Adds a liveness check tagged <c>live</c>.
    /// </summary>
    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        return builder;
    }

    /// <summary>
    /// Maps <c>/health</c> (all checks) and <c>/alive</c> (checks tagged <c>live</c>) in every environment.
    /// The endpoints report status only.
    /// </summary>
    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        app.MapHealthChecks(HealthEndpointPath);
        app.MapHealthChecks(AlivenessEndpointPath, new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains("live")
        });

        return app;
    }
}
```

- [ ] **Step 5: Register the project**

In `src/Namotion.Interceptor.slnx`, inside `<Folder Name="/HomeBlaze/">`, add:

```xml
    <Project Path="HomeBlaze/HomeBlaze.ServiceDefaults/HomeBlaze.ServiceDefaults.csproj" />
```

In `src/HomeBlaze/HomeBlaze/HomeBlaze.csproj`, add to the `ProjectReference` group:

```xml
        <ProjectReference Include="..\HomeBlaze.ServiceDefaults\HomeBlaze.ServiceDefaults.csproj" />
```

- [ ] **Step 6: Call it from Program.cs**

In `src/HomeBlaze/HomeBlaze/Program.cs`, directly after `var builder = WebApplication.CreateBuilder(args);` add:

```csharp
builder.AddServiceDefaults();
```

and directly before `app.MapStaticAssets();` add:

```csharp
app.MapDefaultEndpoints();
```

- [ ] **Step 7: Run the test to verify it passes**

Run: `dotnet test src/HomeBlaze/HomeBlaze.E2E.Tests --filter "FullyQualifiedName~HealthEndpointTests"`
Expected: 2 passed.

- [ ] **Step 8: Run the full E2E suite**

Run: `dotnet test src/HomeBlaze/HomeBlaze.E2E.Tests`
Expected: all pass.

- [ ] **Step 9: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.ServiceDefaults src/Directory.Packages.props src/Namotion.Interceptor.slnx src/HomeBlaze/HomeBlaze/HomeBlaze.csproj src/HomeBlaze/HomeBlaze/Program.cs src/HomeBlaze/HomeBlaze.E2E.Tests/HealthEndpointTests.cs
git commit -m "feat: add OpenTelemetry, Seq and health endpoints to HomeBlaze"
```

---

### Task 9: Container image settings

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze/HomeBlaze.csproj`

- [ ] **Step 1: Add the container properties**

Add a new `PropertyGroup` to `src/HomeBlaze/HomeBlaze/HomeBlaze.csproj`:

```xml
    <PropertyGroup>
        <ContainerRepository>homeblaze</ContainerRepository>
        <ContainerRuntimeIdentifiers>linux-x64;linux-arm64</ContainerRuntimeIdentifiers>
        <!-- SDK-built images own every file as root, so the app user could not write /data.
             Root also gives access to GPIO devices. -->
        <ContainerUser>root</ContainerUser>
    </PropertyGroup>

    <ItemGroup>
        <ContainerPort Include="8080" Type="tcp" />
        <ContainerPort Include="4840" Type="tcp" />
        <ContainerEnvironmentVariable Include="HomeBlaze__RootConfigFile" Value="/data/Root.json" />
        <ContainerEnvironmentVariable Include="HomeBlaze__SeedDirectory" Value="/app/Data" />
    </ItemGroup>
```

- [ ] **Step 2: Include the bundled plugin packages in the publish output**

Below the existing `CopyPluginNupkgs` target, add:

```xml
    <!-- Plugins\*.nupkg are produced by the sample plugin builds, so they are added after the build. -->
    <Target Name="PublishPluginNupkgs" AfterTargets="ComputeResolvedFilesToPublishList">
        <ItemGroup>
            <ResolvedFileToPublish Include="Plugins\*.nupkg" RelativePath="Plugins\%(Filename)%(Extension)" CopyToPublishDirectory="PreserveNewest" />
        </ItemGroup>
    </Target>
```

- [ ] **Step 3: Build a local image archive**

Run:

```bash
dotnet publish src/HomeBlaze/HomeBlaze/HomeBlaze.csproj -c Release -r linux-x64 /t:PublishContainer \
  -p:ContainerRuntimeIdentifiers= -p:ContainerImageTag=local \
  -p:ContainerArchiveOutputPath=/tmp/homeblaze-local.tar.gz
```

Expected: `Pushed image 'homeblaze:local' to local archive at '/tmp/homeblaze-local.tar.gz'`.

- [ ] **Step 4: Smoke test the image locally**

```bash
docker load -i /tmp/homeblaze-local.tar.gz
mkdir -p /tmp/homeblaze-data && rm -rf /tmp/homeblaze-data/*
docker run -d --name homeblaze-local -p 8080:8080 -v /tmp/homeblaze-data:/data homeblaze:local
for attempt in $(seq 1 60); do curl -sf http://localhost:8080/health && break; sleep 2; done
ls /tmp/homeblaze-data /tmp/homeblaze-data/Files /tmp/homeblaze-data/Files/Docs | head -20
docker exec homeblaze-local ls /app/Plugins
docker logs homeblaze-local 2>&1 | grep -E "Seeded the data directory|Plugin loading complete"
docker rm -f homeblaze-local
```

Expected: `Healthy`; `/tmp/homeblaze-data` contains `Root.json`, `Files`; `Files/Docs` contains Markdown; `/app/Plugins` lists the two sample `.nupkg`; log shows seeding and `2 loaded, 0 failed`. If `/app/Plugins` is empty, fix the `PublishPluginNupkgs` target before continuing. Remove `/tmp/homeblaze-data` with `sudo rm -rf` if root-owned files block the cleanup.

- [ ] **Step 5: Commit**

```bash
git add src/HomeBlaze/HomeBlaze/HomeBlaze.csproj
git commit -m "feat: build HomeBlaze as a container image"
```

---

### Task 10: CI jobs for the image

**Files:**
- Modify: `.github/workflows/build.yml`

- [ ] **Step 1: Add the build job**

Insert this job after `test-modbus-integration` and before `pack`:

```yaml
  container-build:
    timeout-minutes: 20
    needs: [ changes ]
    if: needs.changes.outputs.homeblaze == 'true'
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4

      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: ${{ env.DOTNET_VERSION }}

      - name: Build image archive
        run: |
          dotnet publish src/HomeBlaze/HomeBlaze/HomeBlaze.csproj -c Release -r linux-x64 /t:PublishContainer `
            -p:ContainerRuntimeIdentifiers= -p:ContainerImageTag=smoke `
            -p:ContainerArchiveOutputPath=${{ runner.temp }}/homeblaze.tar.gz

      - name: Smoke test image
        run: |
          docker load -i "${{ runner.temp }}/homeblaze.tar.gz"
          $data = New-Item -ItemType Directory -Path (Join-Path "${{ runner.temp }}" "homeblaze-data")
          docker run -d --name homeblaze -p 8080:8080 -v "$($data.FullName):/data" homeblaze:smoke
          $healthy = $false
          for ($attempt = 0; $attempt -lt 60 -and -not $healthy; $attempt++) {
            try { $healthy = (Invoke-WebRequest -Uri http://localhost:8080/health -UseBasicParsing).StatusCode -eq 200 } catch { Start-Sleep -Seconds 2 }
          }
          docker logs homeblaze
          if (-not $healthy) { throw "HomeBlaze did not become healthy" }
          foreach ($file in @("Root.json", "Files/Plugins.json")) {
            if (-not (Test-Path (Join-Path $data.FullName $file))) { throw "Missing seeded file $file" }
          }
```

- [ ] **Step 2: Add the publish job**

Insert directly after `container-build`:

```yaml
  container-publish:
    timeout-minutes: 30
    if: github.event_name == 'release' || (github.event_name == 'push' && github.ref == 'refs/heads/master')
    needs: [ container-build, test, test-homeblaze-integration, test-nugetplugins-integration, test-opcua-integration, test-websocket-integration, test-mqtt-integration, test-modbus-integration ]
    runs-on: ubuntu-latest
    permissions:
      contents: read
      packages: write
    steps:
      - uses: actions/checkout@v4

      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: ${{ env.DOTNET_VERSION }}

      - name: Log in to GitHub Container Registry
        uses: docker/login-action@v3
        with:
          registry: ghcr.io
          username: ${{ github.actor }}
          password: ${{ secrets.GITHUB_TOKEN }}

      - name: Compute tags
        id: tags
        run: |
          if ("${{ github.event_name }}" -eq "release") {
            $version = "${{ github.event.release.tag_name }}" -replace '^v', ''
            $parts = $version.Split('.')
            $tags = "$version;$($parts[0]).$($parts[1]);latest"
            $informationalVersion = $version
          } else {
            $short = "${{ github.sha }}".Substring(0, 7)
            $tags = "edge;sha-$short"
            $informationalVersion = "edge+$short"
          }
          echo "TAGS=$tags" >> $env:GITHUB_OUTPUT
          echo "INFORMATIONAL_VERSION=$informationalVersion" >> $env:GITHUB_OUTPUT

      # Tags go through an environment variable because MSBuild splits semicolons on the command line.
      # Version is not overridden: HomeBlaze pins it to 1.0.0 because plugins bind by assembly version.
      - name: Publish multi-arch image
        env:
          ContainerImageTags: ${{ steps.tags.outputs.TAGS }}
        run: |
          dotnet publish src/HomeBlaze/HomeBlaze/HomeBlaze.csproj -c Release /t:PublishContainer `
            -p:ContainerRegistry=ghcr.io -p:ContainerRepository=ricosuter/homeblaze `
            -p:InformationalVersion=${{ steps.tags.outputs.INFORMATIONAL_VERSION }}
```

- [ ] **Step 3: Validate the workflow syntax**

Run: `python3 -c "import yaml,sys; yaml.safe_load(open('.github/workflows/build.yml')); print('ok')"`
Expected: `ok`. If `actionlint` is installed, also run `actionlint .github/workflows/build.yml` and expect no errors.

- [ ] **Step 4: Commit**

```bash
git add .github/workflows/build.yml
git commit -m "ci: build, smoke test and publish the HomeBlaze container image"
```

---

### Task 11: HomeBlaze.AppHost

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.AppHost/HomeBlaze.AppHost.csproj`
- Create: `src/HomeBlaze/HomeBlaze.AppHost/AppHost.cs`
- Create: `src/HomeBlaze/HomeBlaze.AppHost/aspire.config.json`
- Create: `src/HomeBlaze/HomeBlaze.AppHost/appsettings.json`, `appsettings.Development.json`
- Create: `src/HomeBlaze/HomeBlaze.AppHost/Properties/launchSettings.json`
- Modify: `src/Namotion.Interceptor.slnx`

- [ ] **Step 1: Create the project file**

`src/HomeBlaze/HomeBlaze.AppHost/HomeBlaze.AppHost.csproj`:

```xml
<Project Sdk="Aspire.AppHost.Sdk/13.5.4">

    <PropertyGroup>
        <OutputType>Exe</OutputType>
        <AspireUseCliBundle>true</AspireUseCliBundle>
        <UserSecretsId>3dc9421e-7d2a-4338-898b-99cb8152f843</UserSecretsId>
    </PropertyGroup>

    <ItemGroup>
        <ProjectReference Include="..\HomeBlaze\HomeBlaze.csproj" />
    </ItemGroup>

    <ItemGroup>
        <PackageReference Include="Aspire.Hosting.Seq" />
    </ItemGroup>

</Project>
```

- [ ] **Step 2: Create the AppHost**

`src/HomeBlaze/HomeBlaze.AppHost/AppHost.cs`:

```csharp
using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);

// Persistent containers outlive the AppHost and are reused on the next start, so F5 does not wait for them every
// time. Switched on by both launch profiles; off by default.
var containerLifetime = builder.Configuration.GetValue("AppHost:PersistentContainers", false)
    ? ContainerLifetime.Persistent
    : ContainerLifetime.Session;

var seq = builder.AddSeq("seq")
    .WithDataVolume()
    .WithLifetime(containerLifetime);

var homeblaze = builder.AddProject<Projects.HomeBlaze>("homeblaze")
    .WithReference(seq)
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health");

// Generated once and kept in the AppHost's user secrets, so the persistent n8n volume stays decryptable.
var n8nEncryptionKey = builder.AddParameter(
    "n8n-encryption-key",
    new GenerateParameterDefault { MinLength = 32, Special = false },
    secret: true,
    persist: true);

builder.AddContainer("n8n", "n8nio/n8n")
    .WithHttpEndpoint(targetPort: 5678, name: "http")
    .WithVolume("homeblaze-n8n-data", "/home/node/.n8n")
    .WithEnvironment("N8N_ENCRYPTION_KEY", n8nEncryptionKey)
    .WithEnvironment("N8N_SECURE_COOKIE", "false")
    .WithReference(homeblaze.GetEndpoint("http"))
    .WithLifetime(containerLifetime);

// OPC UA simulator for trying the OPC UA client without hardware: opc.tcp://localhost:50000
builder.AddContainer("opcplc", "iotedge/opc-plc")
    .WithImageRegistry("mcr.microsoft.com")
    .WithArgs("--pn=50000", "--autoaccept", "--ut", "--ph=localhost")
    .WithEndpoint(port: 50000, targetPort: 50000, scheme: "tcp", name: "opcua")
    .WithLifetime(containerLifetime);

builder.Build().Run();
```

- [ ] **Step 3: Create the configuration files**

`src/HomeBlaze/HomeBlaze.AppHost/aspire.config.json`:

```json
{
  "appHost": {
    "path": "HomeBlaze.AppHost.csproj"
  }
}
```

`src/HomeBlaze/HomeBlaze.AppHost/appsettings.json`:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  }
}
```

`src/HomeBlaze/HomeBlaze.AppHost/appsettings.Development.json`:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning",
      "Aspire.Hosting.Dcp": "Warning"
    }
  }
}
```

`src/HomeBlaze/HomeBlaze.AppHost/Properties/launchSettings.json`:

```json
{
  "$schema": "https://json.schemastore.org/launchsettings.json",
  "profiles": {
    "https": {
      "commandName": "Project",
      "dotnetRunMessages": true,
      "launchBrowser": true,
      "applicationUrl": "https://localhost:17245;http://localhost:15045",
      "environmentVariables": {
        "ASPNETCORE_ENVIRONMENT": "Development",
        "DOTNET_ENVIRONMENT": "Development",
        "AppHost__PersistentContainers": "true",
        "ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL": "https://localhost:21185",
        "ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL": "https://localhost:22045"
      }
    },
    "http": {
      "commandName": "Project",
      "dotnetRunMessages": true,
      "launchBrowser": true,
      "applicationUrl": "http://localhost:15045",
      "environmentVariables": {
        "ASPNETCORE_ENVIRONMENT": "Development",
        "DOTNET_ENVIRONMENT": "Development",
        "AppHost__PersistentContainers": "true",
        "ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL": "http://localhost:19165",
        "ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL": "http://localhost:20025"
      }
    }
  }
}
```

- [ ] **Step 4: Register the project**

In `src/Namotion.Interceptor.slnx`, inside `<Folder Name="/HomeBlaze/">`, add:

```xml
    <Project Path="HomeBlaze/HomeBlaze.AppHost/HomeBlaze.AppHost.csproj" />
```

- [ ] **Step 5: Build**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: Build succeeded, 0 warnings. If NuGet reports NU1008 or NU1010 for packages the Aspire SDK adds implicitly (central package management), add matching `<PackageVersion Include="..." Version="13.5.4" />` entries to `src/Directory.Packages.props`. If an Aspire API differs in 13.5.4 (for example the `AddParameter` overload with `GenerateParameterDefault`), check the Aspire docs at https://aspire.dev and use the current equivalent with the same behavior: a generated, persisted secret.

- [ ] **Step 6: Run the AppHost and check the resources**

```bash
dotnet run --project src/HomeBlaze/HomeBlaze.AppHost --launch-profile http > /tmp/apphost.log 2>&1 &
echo $! > /tmp/apphost.pid
for attempt in $(seq 1 90); do curl -sf http://localhost:5192/health > /dev/null && break; sleep 2; done
curl -sf http://localhost:5192/health
docker ps --format '{{.Names}} {{.Status}}' | grep -E "seq|n8n|opcplc"
docker exec "$(docker ps -q --filter name=n8n | head -1)" env | grep services__homeblaze
```

Expected: `Healthy`; seq, n8n and opcplc running; the n8n container has a `services__homeblaze__http__0` variable with a URL reachable from the container. Then check:

```bash
docker exec "$(docker ps -q --filter name=n8n | head -1)" sh -c 'wget -qO- "$services__homeblaze__http__0/health"'
```

Expected: `Healthy`. Record the exact URL form (for example `http://host.docker.internal:5192`) for the documentation in Task 12. Stop with `kill "$(cat /tmp/apphost.pid)"`.

- [ ] **Step 7: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.AppHost src/Namotion.Interceptor.slnx
git commit -m "feat: add an Aspire AppHost for HomeBlaze development"
```

---

### Task 12: Compose file and documentation

**Files:**
- Create: `src/HomeBlaze/docker-compose.yml`
- Modify: `src/HomeBlaze/HomeBlaze/Data/Files/Docs/administration/installation.md`
- Modify: `src/HomeBlaze/HomeBlaze/Data/Files/Docs/administration/configuration.md`
- Modify: `src/HomeBlaze/HomeBlaze/Data/Files/Docs/administration/upgrading.md`
- Modify: `src/HomeBlaze/HomeBlaze/Data/Files/Docs/administration/monitoring.md`
- Create: `src/HomeBlaze/HomeBlaze/Data/Files/Docs/development/aspire.md`
- Modify: docs that mention `root.json` or the old `Data/` layout

- [ ] **Step 1: Create the compose file**

`src/HomeBlaze/docker-compose.yml`:

```yaml
# HomeBlaze self-hosting. Start: docker compose up -d
# Optional services: docker compose --profile seq --profile n8n up -d
# HomeBlaze has no login: keep it on a trusted network or behind an authenticating reverse proxy.
services:
  homeblaze:
    image: ghcr.io/ricosuter/homeblaze:latest
    restart: unless-stopped
    ports:
      - "8080:8080"   # UI and MCP
      - "4840:4840"   # OPC UA server subject
    volumes:
      - ./data:/data  # Root.json, Files/, History/, OpcUa/; filled with defaults on first start
    environment:
      TZ: Europe/Zurich
      McpServer__Enabled: "true"
      McpServer__ReadOnly: "true"  # set to "false" to let MCP clients such as n8n write values
      ConnectionStrings__seq: http://seq:5341
    # devices:
    #   - /dev/gpiomem  # Raspberry Pi GPIO

  seq:
    image: datalust/seq
    profiles: [seq]
    restart: unless-stopped
    ports:
      - "5341:80"
    environment:
      ACCEPT_EULA: "Y"
    volumes:
      - seq-data:/data

  n8n:
    image: docker.n8n.io/n8nio/n8n
    profiles: [n8n]
    restart: unless-stopped
    ports:
      - "5678:5678"
    environment:
      N8N_ENCRYPTION_KEY: ${N8N_ENCRYPTION_KEY}
      N8N_SECURE_COOKIE: "false"
      GENERIC_TIMEZONE: Europe/Zurich
    volumes:
      - n8n-data:/home/node/.n8n

volumes:
  seq-data:
  n8n-data:
```

- [ ] **Step 2: Verify compose against the local image**

```bash
docker tag homeblaze:local ghcr.io/ricosuter/homeblaze:latest
cd /tmp && rm -rf homeblaze-compose && mkdir homeblaze-compose && cd homeblaze-compose
cp "$OLDPWD/src/HomeBlaze/docker-compose.yml" .
echo "N8N_ENCRYPTION_KEY=$(openssl rand -hex 32)" > .env
docker compose --profile seq --profile n8n up -d
for attempt in $(seq 1 60); do curl -sf http://localhost:8080/health && break; sleep 2; done
docker compose exec n8n wget -qO- http://homeblaze:8080/health
docker compose logs homeblaze | grep -iE "otlp|seq|exception" | head
docker compose down
docker compose up -d homeblaze
for attempt in $(seq 1 60); do curl -sf http://localhost:8080/health && break; sleep 2; done
docker compose logs homeblaze | grep -iE "exception|error" | head
docker compose down
cd "$OLDPWD"
```

Expected: `Healthy` from the host and from inside n8n; HomeBlaze logs show no exporter exceptions with and without the `seq` service. If logs flood with Seq export errors when Seq is not running, add a comment in the compose file telling users to remove `ConnectionStrings__seq` when they do not use the `seq` profile, and note it in the guide.

- [ ] **Step 3: Connect n8n to HomeBlaze over MCP (manual)**

Open http://localhost:5678, create the owner account, add an AI Agent workflow with an MCP Client Tool node: transport HTTP Streamable, endpoint `http://homeblaze:8080/mcp`. List tools and run `browse`. Expected: the tool list and a browse result of the HomeBlaze tree. Report the outcome to the user; this is a manual check.

- [ ] **Step 4: Write the Docker section in installation.md**

Append to `src/HomeBlaze/HomeBlaze/Data/Files/Docs/administration/installation.md`:

````markdown
## Docker

HomeBlaze is published as a container image for amd64 and arm64 (Raspberry Pi 4 and 5) at `ghcr.io/ricosuter/homeblaze`.

| Tag | Content |
|-----|---------|
| `latest`, `X.Y`, `X.Y.Z` | Releases |
| `edge`, `sha-<commit>` | Every change on master |

Copy [docker-compose.yml](https://github.com/RicoSuter/Namotion.Interceptor/blob/master/src/HomeBlaze/docker-compose.yml) into an empty folder and start it:

```bash
docker compose up -d
```

HomeBlaze is then available at http://localhost:8080.

### Data folder

Everything HomeBlaze keeps lives in the folder mounted at `/data` (`./data` in the compose file):

```
data/
├── Root.json       root configuration
├── Files/          devices, dashboards, pages, Plugins.json, docs
├── History/Sqlite/ SQLite history
└── OpcUa/          OPC UA certificates
```

On the first start, when `Root.json` does not exist, HomeBlaze copies the shipped defaults into the folder. Existing files are never overwritten. Back up HomeBlaze by copying this folder.

Shipped docs and demo files in `Files/` are not updated by later image versions. To refresh them, delete `Files/Docs` (or other shipped files) and restart; missing files are copied again only when `Root.json` is missing too, so move `Root.json` away for that restart and put it back afterwards.

### Optional services

| Profile | Service | Address |
|---------|---------|---------|
| `seq` | Seq log server, receives logs and traces | http://localhost:5341 |
| `n8n` | n8n workflow automation | http://localhost:5678 |

```bash
echo "N8N_ENCRYPTION_KEY=$(openssl rand -hex 32)" > .env
docker compose --profile seq --profile n8n up -d
```

Keep `.env`: n8n encrypts its stored credentials with this key.

To use HomeBlaze from an n8n AI agent, add an **MCP Client Tool** node with transport **HTTP Streamable** and endpoint `http://homeblaze:8080/mcp`. The compose file enables MCP read-only; set `McpServer__ReadOnly` to `"false"` to allow writes.

### Security

HomeBlaze has no login. Keep it on a trusted network or put it behind a reverse proxy that authenticates users and supports WebSockets. The container runs as root so it can write the data folder and access GPIO devices.

### Hardware and discovery

- Raspberry Pi GPIO: uncomment the `/dev/gpiomem` device in the compose file.
- The Hue bridge is discovered over mDNS and SSDP, which do not pass Docker's default network. Enter the bridge IP address, or run HomeBlaze with `network_mode: host` on Linux. With host networking, remove the `ports` section and reach HomeBlaze from n8n at `http://host.docker.internal:8080/mcp` after adding `extra_hosts: ["host.docker.internal:host-gateway"]` to the n8n service.

### Forks

GitHub Container Registry makes a new package private on its first push. Make it public once in the package settings on GitHub.
````

- [ ] **Step 5: Update configuration.md**

In `src/HomeBlaze/HomeBlaze/Data/Files/Docs/administration/configuration.md`, replace the `PluginConfigurationPath` section with sections for each setting below, keeping the page's existing format (heading, table with Setting/Default, override example):

| Setting | Default | Description |
|---|---|---|
| `HomeBlaze:RootConfigFile` | `Data/Root.json` | Root configuration file, relative to the working directory. Its folder is the data folder; relative paths in configuration resolve against it. |
| `HomeBlaze:SeedDirectory` | not set | Folder copied into the data folder on first start, when the root configuration file does not exist. The container image sets it to `/app/Data`. |
| `PluginConfigurationPath` | `Files/Plugins.json` | Plugin configuration file, relative to the data folder. |
| `McpServer:Enabled`, `McpServer:ReadOnly` | `false`, `true` | MCP endpoint at `/mcp`. |
| `ConnectionStrings:seq` | not set | Seq server URL; enables log and trace export to Seq. |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | not set | OpenTelemetry endpoint for logs, metrics and traces. |

Add a short "Data folder" section with the same layout tree as in installation.md and link to it.

- [ ] **Step 6: Update upgrading.md and monitoring.md**

Append to `upgrading.md` a section "Instance data folder" stating: `root.json` moved to `Data/Root.json` (the name is case-sensitive on Linux); subject files moved to `Data/Files/`; relative paths now resolve against the data folder; SQLite history defaults to `Data/History/Sqlite` instead of the local application data folder, so set `databasePath` to the old folder to keep old history; OPC UA certificates moved to `Data/OpcUa/Server/Pki` and `Data/OpcUa/Client/Pki`, so OPC UA servers must trust the HomeBlaze client certificate again.

Replace the body of `monitoring.md` (remove `status: Planned` from the front matter) with sections on: health endpoints `/health` and `/alive`; OpenTelemetry export via `OTEL_EXPORTER_OTLP_ENDPOINT`; Seq via `ConnectionStrings:seq`; the Aspire dashboard during development (link to `../development/aspire.md`). Keep the existing link to the observability design.

- [ ] **Step 7: Write development/aspire.md**

Create `src/HomeBlaze/HomeBlaze/Data/Files/Docs/development/aspire.md` with front matter matching the other files in `development/` and these sections:
- Running: `dotnet run --project src/HomeBlaze/HomeBlaze.AppHost` or F5 on `HomeBlaze.AppHost`; prerequisites (.NET 10 SDK, Docker or Podman).
- Resources table: `homeblaze` (source-tree `Data/`), `seq`, `n8n`, `opcplc` (`opc.tcp://localhost:50000`).
- Persistent containers: `AppHost:PersistentContainers`, how to turn it off for a run, how to remove the containers.
- n8n: the MCP URL recorded in Task 11 Step 6.
- OPC UA simulator: add an OPC UA client subject in the UI with server URL `opc.tcp://localhost:50000`.

- [ ] **Step 8: Update existing mentions**

Run:

```bash
grep -rn "root.json\|Data/\|Data\\\\" src/HomeBlaze/HomeBlaze/Data/Files --include=*.md
```

For each hit that describes the HomeBlaze data layout, change `root.json` to `Data/Root.json` and `Data/...` subject paths to `Data/Files/...`. Leave unrelated uses (for example code samples about other folders) unchanged. Check the result renders sensibly in the files listed by the grep.

- [ ] **Step 9: Check the docs rules**

Run:

```bash
grep -rn "—" src/HomeBlaze/HomeBlaze/Data/Files/Docs/administration src/HomeBlaze/HomeBlaze/Data/Files/Docs/development
```

Expected: no output (no em dashes).

- [ ] **Step 10: Commit**

```bash
git add src/HomeBlaze/docker-compose.yml src/HomeBlaze/HomeBlaze/Data/Files/Docs
git commit -m "docs: document HomeBlaze Docker hosting, configuration and Aspire development"
```

---

### Task 13: Final verification

- [ ] **Step 1: Full unit test run**

Run: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`
Expected: all pass.

- [ ] **Step 2: HomeBlaze integration tests**

Run: `dotnet test src/HomeBlaze/HomeBlaze.E2E.Tests` and `dotnet test src/HomeBlaze/HomeBlaze.History.Sqlite.Tests`
Expected: all pass.

- [ ] **Step 3: AppHost check with OPC UA**

Start the AppHost as in Task 11 Step 6. In the HomeBlaze UI, add an OPC UA client with `opc.tcp://localhost:50000` and confirm simulated nodes appear. Confirm `src/HomeBlaze/HomeBlaze/Data/OpcUa/Client/Pki` was created and `git status` does not list it.

- [ ] **Step 4: Ask the user for the Windows check**

Ask the user to press F5 on `HomeBlaze` and on `HomeBlaze.AppHost` on Windows and confirm HomeBlaze loads `Data\Root.json`. Do not open the pull request before they confirm.

- [ ] **Step 5: Follow-ups**

Add to the pull request description (not to the code): two-phase startup with plugins found in the tree; separating shipped content from user data so upgrades refresh docs; `docs/mcp.md` still says SSE; `deployment.md` open question on image publishing can be closed.
