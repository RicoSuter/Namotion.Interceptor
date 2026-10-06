# HomeBlaze Runtime Plugin Providers Design

## Goal

Plugin configuration becomes ordinary subjects in the tree instead of a file at a fixed path that is loaded before the host starts. Any subject can contribute plugin assemblies at runtime, several plugin providers can coexist, and files whose type arrives later upgrade in place. Adding a plugin takes effect without a restart.

## Decisions

| Topic | Decision |
|-------|----------|
| Runtime add | Adding a plugin loads it immediately. Removing a plugin or changing its version requires a restart. |
| `PluginConfigurationPath` | Removed (breaking). Plugin providers are found in the tree. |
| Unknown types | JSON with an unresolvable `$type` becomes an `UnknownSubject`; JSON without `$type` stays a `JsonFile`. |
| Provider contract | No new interface, base class, or host service. A provider adds assemblies to the existing `TypeProvider`, which raises a new `TypesChanged` event. Every consumer refreshes itself. |
| Rename | `PluginManager` becomes `NuGetPluginProvider` (breaking: `$type` changes). |
| Paths | Relative cache and feed paths resolve against the instance data directory; absolute paths are used as-is. The cache defaults to `<data>/Plugins/Cache`. |
| Versioning | The container publish reports the real library versions in `deps.json`. Deriving versions from git tags for every build and publishing the HomeBlaze packages stay follow-ups. |

## Components

### TypeProvider (HomeBlaze.Services)

- New `event EventHandler? TypesChanged`, raised after the new type array is published and outside the lock.
- New `AddAssemblies(IEnumerable<Assembly>)` adds a batch and raises `TypesChanged` once. `AddAssembly` stays for fluent startup registration.
- `AddTypes` ignores a type that is already registered (same `Type` instance).
- A different type with the same full name as a registered one is skipped. `AddTypes` and `AddAssemblies` return the skipped types, and the caller (a plugin provider) logs them with both assemblies.
- `TypesChanged` is raised only when at least one type was added.

### Caches that refresh when types are added

`TypeProvider` publishes a new array on every change, so each cache stores the array it was built from and rebuilds when `TypeProvider.Types` returns a different instance. This needs no subscription and cannot miss a change that happens during a rebuild.

- `SubjectTypeRegistry`: the two `Lazy` caches become one snapshot. Entries added through the `Type.GetType` fallback survive only until the next rebuild, which then finds them again through the fallback.
- `SubjectComponentRegistry`: the component map and the resolved cache become one snapshot. This resolves the existing TODO in that class.
- `ConfigurableSubjectSerializer`: `JsonSerializerOptions` caches the polymorphic `$type` list after first use, so the serializer keeps its options in a snapshot as well. Without this, saving a subject whose type was added later fails with an unknown derived type.
- `SubjectTypeRegistryTypeProvider` (MCP) reads `SubjectTypeRegistry.RegisteredTypes` on every call and needs no change.

### UnknownSubject (HomeBlaze.Storage.Files)

- `[InterceptorSubject]` storage file subject next to `JsonFile`, implementing `IStorageFile`, `ITitleProvider` and `IIconProvider`.
- State: `TypeName` (the `$type` value), `Reason` (why it is not the real subject), `FileSize`, `LastModified`.
- Title is the file name without extension, icon is a warning icon in the warning color.
- The raw JSON stays readable, editable and deletable through the existing file operations, like a `JsonFile`.
- It is not `IConfigurable`, so the configuration writer never writes it. The file on disk stays exactly as authored until the real type takes over.
- Its child key in the storage hierarchy is the file name without extension, like a configurable subject's, so its path stays the same when it is upgraded.

### FileSubjectFactory (HomeBlaze.Storage)

For `.json` files:

| Content | Result |
|---------|--------|
| Object with `$type` that resolves | The deserialized subject |
| Object with `$type` that does not resolve | `UnknownSubject` with reason "Type is not loaded." |
| Object with `$type` that resolves but construction or deserialization throws | `UnknownSubject` with the exception message as reason; the exception is logged |
| No `$type`, not an object, or invalid JSON | `JsonFile` |

`ConfigurableSubjectSerializer.Deserialize` keeps its contract (null when there is no `$type` or the type is unknown). The factory reads `$type` itself to tell the cases apart.

### FluentStorageContainer (HomeBlaze.Storage)

- Subscribes to `TypesChanged` when it connects and unsubscribes when it disposes or reconnects.
- On `TypesChanged` it upgrades every `UnknownSubject` in its path registry whose `TypeName` now resolves: it recreates the subject from the file and swaps it into the hierarchy at the same path, using the existing remove and add helpers so the path registry, hashes and `Children` stay consistent.
- A file change on an `UnknownSubject` recreates it the same way, which yields the real subject or a new `UnknownSubject` with the current reason.
- Upgrades, file watcher events and scans run under one per-container `SemaphoreSlim`, so an upgrade cannot interleave with a file event or rescan.

### NuGetPluginProvider (HomeBlaze.Plugins, renamed from PluginManager)

- `[InterceptorSubject]` subject deriving from `BackgroundService`, implementing `IConfigurable`, `ITitleProvider`, `IIconProvider`. Title stays "Plugins".
- `[Configuration]`: `Feeds`, `HostPackages`, `HostIdentifier`, `CacheDirectory` (new), `Plugins`.
- `[State]`: `LoadedPlugins` (as today), `IsRestartRequired`.
- Constructor dependencies: `TypeProvider`, `ILoggerFactory`.
- `ExecuteAsync` creates one `NuGetPluginLoader` from the configuration, loads all configured plugins, adds each loaded assembly to `TypeProvider`, and fills `LoadedPlugins`.
- One reconcile method compares the configured plugins with the loaded ones:
  - a new entry is loaded with the existing loader and its assemblies are added to `TypeProvider`;
  - a removed entry or a changed version marks that plugin "Restart required" and sets `IsRestartRequired`;
  - changed feeds, host packages, host identifier or cache directory set `IsRestartRequired`, because the loader options are fixed at creation.
- `ApplyConfigurationAsync` (called when `Plugins.json` changes on disk) and the `Add Plugin` operation both run reconcile.
- New `Retry` operation: loads all plugins with status Error again through the same path.
- The existing remove operation on `Plugin` edits the configuration and runs reconcile, so the plugin shows "Restart required".
- Loading runs one load call at a time per provider (a `SemaphoreSlim`), so `ExecuteAsync`, reconcile and retry never load concurrently on the same loader.
- The loader lives until the process exits. Disposing it would unload assemblies that live subjects still use, so the subject does not dispose it.

Path resolution (one helper, unit tested):

- `CacheDirectory`: null, empty or whitespace means `Plugins/Cache`. Relative paths resolve against `IDataDirectoryProvider.DataDirectory` from the subject's context, falling back to the working directory when there is none. Rooted paths are used as-is.
- Feed URLs: absolute URIs and rooted paths are used as-is; other values are folder paths that resolve against the data directory.

`Plugin` keeps its parent reference, now typed `NuGetPluginProvider`.

### Removed

- `PluginLoader`, `PluginsServiceCollectionExtensions.AddHomeBlazePlugins`.
- `HomeBlazePaths.PluginConfigurationPathKey`, `DefaultPluginConfigurationFile`, `GetPluginConfigurationPath`.
- The plugin loading block in `Program.cs`. `HomeBlaze.Plugins` stays registered with `TypeProvider` so `NuGetPluginProvider` resolves.
- `PluginConfiguration.LoadFrom(path, baseDirectory)` and its JSON reading if nothing else uses them; the loader options are built from the subject's configuration properties.
- The `CopyPluginNupkgs` target in `HomeBlaze.csproj` (dev now reads the project's `Plugins` folder directly). The E2E project keeps its copy.

## Data Flow

### Startup

1. `RootManager` loads `Root.json`, the storage scan creates subjects. Files whose `$type` is not resolvable yet become `UnknownSubject`s.
2. Hosted subjects start, including every `NuGetPluginProvider` in the tree. Each loads its packages and adds the assemblies to `TypeProvider`.
3. `TypesChanged` fires: registries and the serializer refresh, every storage upgrades its now resolvable `UnknownSubject`s, and the new subjects attach and start like any other subject.
4. Whatever is still unknown after all providers have finished stays an `UnknownSubject` with its reason.

### Adding a plugin at runtime

The `Add Plugin` operation appends the entry to `Plugins` (persisted by the normal configuration save) and runs reconcile, which loads only the new package with the existing loader. Shared contracts that are already loaded are reused.

### Multiple providers

Each provider has its own loader and its own `LoadedPlugins`. Shared contract assemblies are loaded once per process into the default load context, and the first loaded version wins. The docs recommend keeping plugins that share contracts in one provider when their contract versions differ. Duplicate type names across providers are skipped with a warning (see TypeProvider).

## Error Handling

- A package that fails to load (feed unreachable, incompatible versions, download error) shows `Status = Error` with the reason, the provider icon turns to warning, and the failure is logged. Subjects of its types stay `UnknownSubject`s. `Retry` loads it again.
- An upgrade whose construction throws leaves an `UnknownSubject` with the error as reason.
- A provider removed at runtime (its file deleted) leaves its types loaded until restart.
- Subjects referencing paths (dashboards, history, OPC UA server) first see an `UnknownSubject` and then the real subject. The implementation checks each of them for direct object references that would keep pointing to the replaced placeholder and fixes those that do.
- Two providers sharing the cache directory may extract the same package concurrently. The implementation verifies the library's `PackageExtractor` is safe for that and fixes it in `Namotion.NuGet.Plugins` if not.

## Versioning in the Container Image

- The root `src/Directory.Build.props` keeps `0.1.0` as the default library version but takes it from a dedicated property (for example `LibraryVersion`) when set. HomeBlaze's own pinned `1.0.0` is unaffected, because a global `-p:Version` would also override it and break plugins built against HomeBlaze 1.0.0 on the major version.
- The container publish in `.github/workflows/build.yml` passes that property: `X.Y.Z` for releases, the newest release tag for `edge` builds.
- The implementation verifies whether `deps.json` takes the project's `Version` or `PackageVersion` and sets the one it reads, and checks that assembly versions in the image do not drop below what published plugins reference.
- The container smoke test asserts that `HomeBlaze.deps.json` in the built image lists `Namotion.Interceptor` with the passed version.

## Data and Docs

- `$type` becomes `HomeBlaze.Plugins.NuGetPluginProvider` in the Seed, dev data and E2E test data `Plugins.json`.
- Dev `Plugins.json`: local feed `../Plugins`, no `cacheDirectory` (default `Data/Plugins/Cache`). `.gitignore` adds `src/HomeBlaze/HomeBlaze/Data/Plugins/` and drops the obsolete `PluginsCache` entry.
- E2E test data keeps the local feed `Plugins`, which resolves against the test output folder where the sample packages are copied. It sets `cacheDirectory` to `PluginsCache`, because the default `Plugins/Cache` would sit inside that feed folder. `WebTestingHostFactory` no longer sets `PluginConfigurationPath`.
- `architecture/design/plugins.md`: new bootstrap sequence, multiple providers, `UnknownSubject`, path rules, writing your own provider by adding assemblies to `TypeProvider`.
- `administration/configuration.md`: remove `PluginConfigurationPath`, describe cache and feed path rules, add `Plugins/Cache` to the data folder layout, mention excluding it from backups.
- `architecture/project-structure.md`: replace `PluginLoader` and `AddHomeBlazePlugins`.

## Testing

Unit tests:

- `TypeProvider`: `TypesChanged` fires once per add with new types, not for an empty or already registered add; a duplicate full name from another type is skipped.
- `SubjectTypeRegistry`, `SubjectComponentRegistry`: a type added after first use resolves.
- `ConfigurableSubjectSerializer`: a subject whose type was added after the first serialization serializes and deserializes.
- `FileSubjectFactory`: the four rows of the table above.
- `FluentStorageContainer`: an `UnknownSubject` upgrades at the same path on `TypesChanged`; a file edit on an `UnknownSubject` recreates it; plain `JsonFile`s are untouched.
- `NuGetPluginProvider`: reconcile for add, remove, version change and settings change; cache and feed path resolution (default, relative, absolute, no data directory).

E2E tests:

- `PluginLoadingTests` keep working from the test data `Plugins.json` without `PluginConfigurationPath`.
- New: a test data file of a sample plugin type shows up as the real subject in the browser, which exercises the upgrade path.

## Breaking Changes

- `PluginConfigurationPath` setting removed.
- `$type` `HomeBlaze.Plugins.PluginManager` renamed to `HomeBlaze.Plugins.NuGetPluginProvider`.
- Relative feed and cache paths in `Plugins.json` resolve against the data directory instead of the application directory.
- `PluginLoader` and `AddHomeBlazePlugins` removed.

## Out of Scope

- Unloading or updating plugins at runtime.
- Deriving library versions from git tags for every build.
- Publishing the HomeBlaze packages to NuGet.
- A marker interface for listing plugin providers.
