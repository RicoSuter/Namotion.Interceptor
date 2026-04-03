# PR #251 Review: "HomeBlaze: Add NuGet plugin system with runtime loading"

**4,906 additions, 67 deletions across 73 files**

Reviewed by 6 specialized agents: Documentation, Architecture, Code Simplification, Correctness/Edge Cases, Resolution/Versioning, Overall Structure.

---

## HIGH severity issues

### 1. `IsAssemblyInDefaultContext` loads assemblies as a side effect of checking

**Files:** `NuGetPluginLoader.cs:359-370`

`LoadFromAssemblyName` permanently loads assemblies into the default ALC as a side effect. This defeats plugin isolation (the core value proposition) and is expensive (throws exceptions for every miss). Additionally, it can trigger the `Resolving` event registered by the loader itself, potentially loading assemblies from `_additionalHostAssemblyPaths` prematurely.

**Recommendation:** Use `AppDomain.CurrentDomain.GetAssemblies()` for a read-only name check instead. Only fall back to `LoadFromAssemblyName` if strictly needed.

> **Assessment: VALID -- should fix.** The code at line 363 does `AssemblyLoadContext.Default.LoadFromAssemblyName(...)` which indeed loads as a side-effect. Using `AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == assemblyName)` is the correct read-only alternative.

---

### 2. Diamond dependency resolution uses "first visited wins"

**Files:** `DependencyGraphResolver.cs:92-94`

When package D is reached first via B (v1.0.0) and then via C (needs >=1.2.0), the second resolution is skipped entirely via `visited.ContainsKey`. `FlattenDependencies` takes the max but can only see versions actually resolved into the tree -- the C->D node is never created. The test `WhenDiamondDependencyExists_ThenResolvesHigherVersion` passes only because the fake provider happens to resolve in the right order. With real NuGet feeds where traversal order may differ, this silently picks a lower version.

**Recommendation:** Track version range constraints in `visited`. When a package is encountered again with a stricter constraint, re-resolve and update. Alternatively, use a two-pass approach: resolve the full graph with all version ranges, then pick the maximum satisfying version.

> **Assessment: VALID -- but low real-world impact.** The `visited.ContainsKey` check at line 93 does skip re-resolution. However, `FlattenDependencies` takes the max version across all nodes, so the issue only manifests if the higher-version node is never created at all (because the lower version was visited first and the second path is skipped). In practice NuGet dependency graphs are shallow and versions align closely, but the fix is correct in principle. Worth addressing eventually.

---

### 3. Host version conflicts abort ALL plugins

**Files:** `NuGetPluginLoader.cs:160-163`

```csharp
if (allHostConflicts.Count > 0)
{
    throw new NuGetPluginVersionConflictException(allHostConflicts);
}
```

If plugin A has a host version conflict but plugin B is fine, this throws and no plugins load at all. This is inconsistent with the partial-failure model used in Phases 1, 4, and 6 (where individual plugin failures are captured in the `failures` list and other plugins proceed).

**Recommendation:** Move conflicting plugins to the `failures` list and exclude them from subsequent phases. If all-or-nothing behavior is intentional for host conflicts (since host packages are shared), document this clearly in the API.

> **Assessment: INTENTIONAL -- should document more prominently in README.** Host conflicts are fail-all by design because host packages are loaded into the shared default ALC. Loading plugin B with a compatible host version while plugin A's conflict poisons the default context would be worse. The README has a "Failure Handling" section but should explicitly call out that host version conflicts abort ALL plugins (not just the offending one) and explain why.

---

### 4. Host assemblies loaded into default ALC are never unloadable

**Files:** `NuGetPluginLoader.cs:212-231`

Phase 5 loads additional host assemblies into the default (non-collectible) context via `_additionalHostAssemblyPaths`. On plugin unload, these paths are never cleaned up, accumulating assemblies permanently across hot-reload cycles. Different versions of the same assembly from different load cycles could also conflict.

**Recommendation:** Track which additional host assemblies were loaded per plugin group. Clean up `_additionalHostAssemblyPaths` entries on unload. Document that assemblies loaded into the default context cannot be reclaimed.

> **Assessment: VALID -- but inherently unsolvable.** Once an assembly is loaded into the default (non-collectible) ALC, the runtime cannot unload it. Cleaning up `_additionalHostAssemblyPaths` would only prevent *future* resolution via the Resolving hook but wouldn't unload what's already there. This is a fundamental .NET limitation. We should document this clearly (both in code and README's Limitations section) rather than try to "fix" it.

---

### 5. `DownloadResourceResult` is never disposed (resource leak)

**Files:** `NuGetPackageRepository.cs:91-108`

The `DownloadResourceResult` from `GetDownloadResourceResultAsync` is `IDisposable` but only its `.PackageStream` is returned to the caller. The wrapper object (which may hold HTTP connections and temp files managed by the NuGet SDK) is leaked.

**Recommendation:** Either dispose `DownloadResourceResult` after copying the stream to a `MemoryStream`, or return the result itself and let the caller manage its lifetime.

> **Assessment: VALID -- should fix.** The `DownloadResourceResult` wrapping the `PackageStream` is not disposed. Copying to `MemoryStream` before returning is the cleanest fix since the caller already consumes the stream immediately.

---

### 6. Race condition in `ExtractToCache` (TOCTOU)

**Files:** `PackageExtractor.cs:99-104`

```csharp
if (!Directory.Exists(packagePath))
{
    Directory.CreateDirectory(packagePath);
    using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
    archive.ExtractToDirectory(packagePath);
}
```

`Directory.Exists` check and `ExtractToDirectory` are not atomic. Concurrent callers sharing a cache directory can pass the check before either creates the directory, causing `IOException` when the second call tries to extract into the already-populated path.

**Recommendation:** Use a file-based lock or a named `Mutex` scoped to the package path. Alternatively, extract to a temp directory first and `Directory.Move` atomically.

> **Assessment: VALID -- but low risk in practice.** `LoadPluginsAsync` is called once at startup sequentially. Concurrent extraction of the same package into the same cache directory would require concurrent loader instances sharing a cache path, which doesn't happen in normal usage. Worth fixing with a simple `lock` or atomic move if we ever support concurrent loading, but not urgent.

---

### 7. Assembly references in `NuGetPluginGroup` pin ALC after Dispose

**Files:** `NuGetPluginGroup.cs:37-38, 62-69`

After `Dispose()` calls `_loadContext.Unload()`, the `Assemblies` list still holds strong references to `Assembly` objects. The .NET runtime's collectible ALC will not be collected until all external `Assembly`/`Type` references are released. Any consumer that calls `GetTypes<T>()` and holds the result prevents GC of the context.

**Recommendation:** Clear or null out `Assemblies` in `Dispose()`. Add an `ObjectDisposedException` guard on `GetTypes` post-disposal. Document that callers must release all `Type`/`Assembly` references for the context to actually be collected.

> **Assessment: VALID -- should fix.** `Assemblies` is a readonly `IReadOnlyList<Assembly>` property, so we can't clear it directly, but we could null out the backing field or switch to a mutable list. Adding `ObjectDisposedException` guard on `GetTypes` and documenting the GC requirement are both good ideas. The README already mentions this under "Unloading Plugins" but a code-level guard would be safer.

---

### 8. Host version conflicts NOT detected for transitive dependencies

**Files:** `DependencyGraphResolver.cs:96-106`

Host-classified dependencies are skipped with `continue` during resolution, so they never appear in `flatDependencies`. Phase 3 validation looks in `flatDependencies` to check host version compatibility, but since these packages were skipped, no validation occurs. Only the root plugin's direct dependencies (if they are in the host) will be checked. Transitive host version incompatibilities go undetected, potentially causing runtime `MissingMethodException` or `TypeLoadException`.

**Recommendation:** Only skip transitive resolution for packages confirmed in `HostDependencyResolver.Dependencies`. For pattern-matched packages, continue resolving their transitive tree but classify them as Host during the classification phase.

> **Assessment: PARTIALLY VALID.** The code skips resolution for both deps.json hosts AND pattern-matched hosts at lines 97-108 of `DependencyGraphResolver`. For deps.json hosts this is correct (they're already loaded, version is validated in Phase 3). For pattern-matched hosts, skipping resolution means their transitive deps are never discovered -- but pattern-matched hosts ARE downloaded and loaded into the default context, so their own transitive deps would need separate resolution. However, since `FlattenDependencies` does include host-classified packages for Phase 3 validation (line 123-125 of the loader), the version *is* validated for deps.json hosts. The gap is specifically for pattern-matched host packages' own transitive deps. Worth noting but complex to fix.

---

### 9. SamplePlugin package version mismatch (1.0.0 vs 0.1.0)

**Files:** `project-structure.md`, `plugins.md`, `Data/Plugins.json`, `FolderFeedPluginLoadingTests.cs`

Docs, `Plugins.json`, and integration tests consistently reference version `1.0.0`, but neither `HomeBlaze.SamplePlugin.csproj` nor `HomeBlaze.SamplePlugin.HomeBlaze.csproj` sets a `<Version>` property. The root `Directory.Build.props` sets `<Version>0.1.0</Version>`, so generated packages will be `0.1.0`. Integration tests expecting `HomeBlaze.SamplePlugin.1.0.0.nupkg` will fail.

**Recommendation:** Either add `<Version>1.0.0</Version>` to the sample plugin csprojs, or update all references to `0.1.0`.

> **Assessment: VALID -- must fix before merge.** `Directory.Build.props` sets `<Version>0.1.0</Version>` and neither SamplePlugin csproj overrides it. The nupkgs will be `0.1.0` but `Plugins.json` references `1.0.0`. Either add `<Version>1.0.0</Version>` to the sample plugin csprojs or change all references to `0.1.0`.

---

## MEDIUM severity issues

### 10. `LoadPluginsAsync` is a 250-line monolith

**Files:** `NuGetPluginLoader.cs:80-337`

This single method contains 6 distinct phases, each iterating over `pluginGraphs` separately. The intermediate tuple `(NuGetPluginRequest Request, Dictionary<string, NuGetVersion> FlatDependencies, Dictionary<string, DependencyClassification> Classifications)` is used across all phases.

**Recommendation:** Extract each phase into a named private method. Replace the 3-element value tuple with a named type (private record or class).

> **Assessment: VALID -- good refactor suggestion.** The method is long and the value tuple is hard to read. Extracting phases and using a named record would improve readability. Not blocking but worthwhile.

---

### 11. Duplicated `CreateSourceRepository` logic

**Files:**
- `NuGetPackageRepository.cs:122-134`
- `NuGetDependencyInfoProvider.cs:87-98`

These two methods are functionally identical: create a `SourceRepository` from a `NuGetFeed`, set credentials if present, add CoreV3 providers.

**Recommendation:** Extract a shared static helper, e.g. `NuGetFeedExtensions.CreateSourceRepository(this NuGetFeed feed)`.

> **Assessment: VALID -- should fix.** The two `CreateSourceRepository` methods in `NuGetPackageRepository.cs:122` and `NuGetDependencyInfoProvider.cs:87` are identical. A shared extension method is the right approach.

---

### 12. Repeated "is additional host package" predicate

**Files:** `NuGetPluginLoader.cs` lines 137-138, 173-174, 197-199, 217-218

The condition `classifications[packageName] == DependencyClassification.Host && !hostResolver.Contains(packageName)` appears 4 times with slight variations.

**Recommendation:** Extract a local function: `bool IsAdditionalHostPackage(string packageName)`.

> **Assessment: VALID -- good cleanup.** The pattern `classifications[packageName] == DependencyClassification.Host && !hostResolver.Contains(packageName)` appears 4 times. A local function or method would improve readability.

---

### 13. Swallowed exceptions hide missing dependencies

**Files:** `NuGetDependencyInfoProvider.cs:51`

When fetching dependencies from a feed, all exceptions are caught and logged at Debug level, then empty `[]` is returned. If ALL feeds fail (network down, auth failure), the resolver thinks the package has zero dependencies and proceeds to load an incomplete plugin. Also affects `NuGetPluginGroup.GetTypes` (line 57) and `IsAssemblyInDefaultContext` (line 367).

**Recommendation:** At minimum, if all feeds fail for a given package, propagate the error rather than returning empty. Consider catching only expected exception types (`HttpRequestException`, `FatalProtocolException`).

> **Assessment: VALID -- should improve.** `NuGetDependencyInfoProvider.GetDependenciesAsync` catches all exceptions and returns `[]` if all feeds fail. This silently produces an incomplete dependency tree. At minimum, log at Warning level (not Debug) when all feeds fail, or propagate the last exception. The `GetTypes` swallow in `NuGetPluginGroup` is intentional (some assemblies may have unresolvable type references).

---

### 14. `PluginLoaderService` does I/O in constructor

**Files:** `PluginLoaderService.cs:17-28`

Reads two files from disk (`PluginConfiguration.LoadFrom`, `HostDependencyResolver.FromDepsJson`) and hooks a static event on `AssemblyLoadContext.Default` during DI container resolution. If the config file is malformed, the DI container build throws with a hard-to-diagnose error. `FromDepsJson()` can throw `InvalidOperationException` in AOT/single-file scenarios.

**Recommendation:** Move initialization to `LoadPluginsAsync` or introduce an `InitializeAsync` method. Register an `IHostedService` that performs initialization during the startup pipeline.

> **Assessment: VALID -- should improve.** Move `PluginConfiguration.LoadFrom()` and `HostDependencyResolver.FromDepsJson()` out of the constructor into `LoadPluginsAsync()`. The constructor should just store the config path string. Additionally, the current design where the config path is configured separately is not ideal -- mark as `[Planned]` in the HomeBlaze architecture doc for redesign. `HostDependencyResolver.FromDepsJson()` should accept an optional path string parameter and be called inside `LoadPluginsAsync`.

---

### 15. TFM priority list can select `net10.0` assemblies on `net9.0` runtime

**Files:** `PackageExtractor.cs:10-19`

Hardcoded `TargetFrameworkPriority` puts `net10.0` first. If the host runs on .NET 9.0 but a package has both `net9.0` and `net10.0` folders, it selects `net10.0`, causing potential `MissingMethodException` at runtime. Also won't include future `net11.0+` without code changes.

**Recommendation:** Use only `FrameworkReducer.GetNearest` with the actual runtime TFM (already computed via `Environment.Version`). The hardcoded priority list is redundant and actively harmful for the net10.0-on-net9.0 case.

> **Assessment: VALID -- should fix.** The hardcoded priority list will select `net10.0` even on a `net9.0` runtime. Since the `FrameworkReducer` fallback already uses `Environment.Version` correctly, the priority list is both redundant and harmful. Removing it and using only `FrameworkReducer.GetNearest` is the right fix.

---

### 16. `CompositeNuGetPackageRepository` doesn't fail over on network errors

**Files:** `CompositeNuGetPackageRepository.cs:18-30`

`DownloadPackageAsync` only catches `PackageNotFoundException` when iterating repos. If the first repo throws `HttpRequestException` (transient network failure), it propagates immediately without trying the next feed.

**Recommendation:** Catch `HttpRequestException` (and potentially `FatalProtocolException`) in addition to `PackageNotFoundException` and try the next feed.

> **Assessment: VALID -- should fix.** `CompositeNuGetPackageRepository.DownloadPackageAsync` only catches `PackageNotFoundException`. A transient network error on the first feed would prevent trying subsequent feeds. Adding `HttpRequestException` and `FatalProtocolException` to the catch is straightforward.

---

### 17. No package signature verification

**Files:** `CompositeNuGetPackageRepository.cs`, `NuGetPluginLoader.cs`

No verification of package signatures, no package hash validation, and no mechanism to mark feeds as trusted or untrusted. If a user configures multiple feeds and a malicious feed is listed before nuget.org, it could serve a trojanized version of any package. The `CompositeNuGetPackageRepository` will use whichever feed responds first.

**Recommendation:** At minimum, document the trust model. Consider adding optional package signature verification for feeds marked as untrusted.

> **Assessment: VALID but out of scope.** Package signature verification is a significant feature. For now, documenting the trust model in README's Limitations section (feed ordering = trust ordering, first feed wins) is sufficient. This is consistent with how NuGet itself works -- `nuget.config` feed order implies trust priority.

---

### 18. `VersionCompatibility.IsCompatible` diverges from NuGet semver

**Files:** `VersionCompatibility.cs:14-27`

Custom rule: "major must match, plugin minor <= host minor, patch ignored." This doesn't match NuGet's range-based semantics. If a plugin transitively depends on `Microsoft.Extensions.Logging 9.1.0` and the host has `9.0.0`, this flags a conflict even though `9.0.0` might satisfy the original version range. Pre-release labels are also completely ignored.

**Recommendation:** Compare concrete versions against the original version range constraint, not against each other with custom rules.

> **Assessment: PARTIALLY VALID.** The custom rules (major match, minor <=) are stricter than NuGet's range semantics but intentionally so. Host version compatibility is different from NuGet dependency resolution -- a plugin built against `Microsoft.Extensions.Logging 9.1.0` may use APIs not present in `9.0.0`. The "minor plugin <= host" rule is conservative and safe. However, comparing against the original version range rather than resolved version would be more accurate. Pre-release labels being ignored is fine for the typical use case (stable host packages). Worth documenting the rationale.

---

### 19. No cancellation token propagation in `PackageExtractor`

**Files:** `PackageExtractor.cs:31-36, 95-107`

`ExtractAndGetAssemblyPaths` and `ExtractToCache` do not accept or forward a `CancellationToken`. Extraction of large zip archives can't be cancelled.

**Recommendation:** Add `CancellationToken` parameter and thread it through the extraction path.

> **Assessment: VALID but low priority.** Package extraction is typically fast (zip extract to local disk). Adding `CancellationToken` is good practice but `ZipArchive.ExtractToDirectory` doesn't accept one anyway, so the cancellation would only apply between entries if we extract manually. Nice-to-have.

---

### 20. `downloadResult` leaks on retry path

**Files:** `NuGetPackageRepository.cs:91-118`

If `GetDownloadResourceResultAsync` succeeds (returning a `DownloadResourceResult`) but the null check on line 96 triggers `HttpRequestException`, the `downloadResult` is not disposed before the retry loop creates a new one.

**Recommendation:** Wrap `downloadResult` in a `using` block or dispose it in the `catch` clause before retrying.

> **Assessment: VALID -- overlaps with #5.** The `DownloadResourceResult` disposal issue is the same root cause as #5. Fixing #5 (copy to MemoryStream and dispose) would fix this too. The null-check throw path specifically wouldn't leak since `downloadResult.PackageStream` being null means there's nothing to dispose on the stream side, but the wrapper object itself may still hold resources.

---

### 21. `_additionalHostAssemblyPaths` written/read without concurrent load guard

**Files:** `NuGetPluginLoader.cs:213-231, 372-380`

While `_additionalHostAssemblyPaths` is a `ConcurrentDictionary` (individual ops are safe), nothing prevents concurrent `LoadPluginsAsync` calls. `OnDefaultContextResolving` can fire from any thread during Phase 6, potentially before Phase 5 has finished populating all paths.

**Recommendation:** Add a `SemaphoreSlim(1,1)` guard on `LoadPluginsAsync`, or document that it must not be called concurrently.

> **Assessment: VALID but low risk.** `LoadPluginsAsync` is called once at startup in `PluginLoaderService`. Concurrent calls would be a programming error, not a race condition in normal use. Adding a simple XML doc note that "this method is not thread-safe and must not be called concurrently" is sufficient. A `SemaphoreSlim` would be defensive overkill for a startup-only method.

---

### 22. `DependencyClassifier.Classify` accepts unused `NuGetVersion` parameter

**Files:** `DependencyClassifier.cs:52`

The `version` parameter is never read in the method body. Classification is purely name-based.

**Recommendation:** Remove the `version` parameter.

> **Assessment: VALID -- should fix.** The `version` parameter in `DependencyClassifier.Classify` is indeed never used. Classification is purely name-based. Remove it and update `ClassifyAll` accordingly.

---

### 23. CI job runs unit tests redundantly

**Files:** `.github/workflows/build.yml` (test-nugetplugins-integration job)

Every other integration test job uses `--filter "Category=Integration"`. The NuGet plugins job runs all tests without filtering, and no tests have `[Trait("Category", "Integration")]`. Unit tests run in both the main `test` job and this dedicated job.

**Recommendation:** Add `[Trait("Category", "Integration")]` to tests that need NuGet network access and add `--filter "Category=Integration"` to the CI step. Or remove the dedicated job until real integration tests exist.

> **Assessment: VALID -- should fix.** The CI job was intended to run the new folder-feed integration test plus the existing nuget.org tests. Since the folder-feed test (Task 8 in our plan) hasn't been implemented yet, and the job currently runs all tests without filtering, it's redundant with the main test job. Once the folder-feed test is added, the job should filter to `Category=Integration`.

---

### 24. `IDependencyInfoProvider` is `internal` -- prevents custom dependency resolvers

**Files:** `IDependencyInfoProvider.cs:9`, `DependencyGraphResolver.cs:18`

This is the key abstraction for dependency metadata sources. It's correctly used in tests via `FakeDependencyInfoProvider`, but consumers can't inject custom implementations (local cache, custom feed protocol, pre-computed graph).

**Recommendation:** Make `IDependencyInfoProvider` and the corresponding `DependencyGraphResolver` constructor `public`.

> **Assessment: VALID but intentionally internal for now.** The `internal` visibility + `InternalsVisibleTo` for tests is the current pattern. Making it `public` would expand the public API surface before the library is stable (version 0.1.0). Better to keep it internal until there's a concrete use case for custom dependency resolvers. Can be promoted to public in a future version.

---

### 25. Phase 4 `isHostPackageFailure` check is overly broad

**Files:** `NuGetPluginLoader.cs:196-209`

The check looks at whether *any* dependency in the plugin's tree is an additional host package. But the exception might be from downloading a *private* dependency. If the plugin graph also happens to contain an additional host package, the entire operation is incorrectly aborted.

**Recommendation:** Check which specific package failed to download, not whether the plugin has any host packages.

> **Assessment: VALID -- should fix.** The `isHostPackageFailure` check at line 197 tests whether the plugin *has* any additional host packages, not whether the *failed* package is a host package. If a private dependency download fails but the plugin also has a host package, the error incorrectly escalates to fail-all. The fix should track which specific package caused the exception.

---

### 26. `HostDependencyResolver` misses compile-only deps and framework reference assemblies

**Files:** `HostDependencyResolver.cs:107-118`

`FromDependencyContext` uses `RuntimeLibraries` only. Compile-only dependencies (analyzers, source generators) and framework reference assemblies (e.g., `Microsoft.AspNetCore.Http.Abstractions` via framework reference) are not detected.

**Recommendation:** Document this limitation. Consider also checking `CompileLibraries` for completeness.

> **Assessment: PARTIALLY VALID.** Using `RuntimeLibraries` is correct for the primary use case -- these are the packages actually loaded at runtime. `CompileLibraries` include analyzers and source generators that aren't loaded as runtime assemblies. Framework reference assemblies (e.g., `Microsoft.AspNetCore.Http.Abstractions`) are implicitly available in the default ALC and don't need to be in the dependency map -- the `IsAssemblyInDefaultContext` check handles them. Documenting the limitation is worthwhile but `CompileLibraries` would add noise.

---

### 27. Cache directory uses `Guid.NewGuid()` -- no reuse across restarts

**Files:** `NuGetPluginLoader.cs:34-36`

When no explicit cache directory is specified, a new random temp directory is created every time. All packages are re-downloaded on every application restart.

**Recommendation:** Use a deterministic default path (e.g., based on entry assembly name or a well-known app data location).

> **Assessment: VALID -- should fix.** Using `Guid.NewGuid()` means every restart re-downloads everything. A deterministic path like `Path.Combine(Path.GetTempPath(), "Namotion.NuGet.Plugins", Assembly.GetEntryAssembly()?.GetName().Name ?? "default")` would enable cross-restart caching.

---

### 28. `NuGetPackageRepository` stream lifetime is fragile

**Files:** `NuGetPackageRepository.cs:75-93`

The `downloadCacheContext` is `using`-scoped but the returned `PackageStream` may be backed by temp files that the cache context manages. Disposing the context could invalidate the stream before the caller reads it. Works in practice because the caller reads immediately, but fragile.

**Recommendation:** Copy the stream to a `MemoryStream` before returning, or return the `DownloadResourceResult` wrapper for proper lifetime management.

> **Assessment: VALID -- overlaps with #5 and #20.** This is the same `DownloadResourceResult` disposal issue viewed from another angle. The stream *works* in practice because the caller (`NuGetPluginLoader`) reads immediately in `ExtractAndGetAssemblyPaths`, but it's fragile. Fixing #5 (copy to MemoryStream) resolves all three findings (#5, #20, #28) at once.

---

### 29. `HostPackageVersionResolver` conflict message is misleading for 3+ plugins

**Files:** `HostPackageVersionResolver.cs:42-46`

`requiredVersion` shows the last conflicting pair rather than the full picture. `requestedBy` includes all plugins (conflicting and non-conflicting). Confusing when more than 2 plugins are involved.

**Recommendation:** Restructure the conflict message to show all contributing plugins with their individual ranges.

> **Assessment: VALID but minor.** The `requestedBy` string at line 39-40 of `HostPackageVersionResolver` does include all plugins with their ranges (`string.Join(", ", pluginRanges.Select(...))`). The `requiredVersion` showing only the last conflicting pair is the real issue. Improving the message structure is nice-to-have.

---

### 30. `NuGetDependencyInfoProvider` creates a new `SourceRepository` per call

**Files:** `NuGetDependencyInfoProvider.cs:33, 63`

`SourceRepository` internally caches resource objects. Recreating it per call discards that cache, causing redundant HTTP service index requests for deep dependency trees.

**Recommendation:** Cache `SourceRepository` instances per feed URL.

> **Assessment: VALID -- should fix.** `NuGetDependencyInfoProvider` creates a new `SourceRepository` on every `GetDependenciesAsync` and `ResolveVersionAsync` call. For a deep dependency tree this means many redundant HTTP service index requests. Caching per feed URL (e.g., a `Dictionary<string, SourceRepository>` field) is a simple optimization.

---

### 31. Unresolvable dependencies silently ignored

**Files:** `DependencyGraphResolver.cs:112-116`

When `ResolveVersionAsync` returns null for a dependency, it logs a warning and continues. A plugin may load with missing transitive dependencies, leading to runtime `FileNotFoundException`.

**Recommendation:** Either fail the resolution or add unresolvable dependencies to a warnings list returned to the caller.

> **Assessment: VALID -- should improve.** At line 114 of `DependencyGraphResolver`, unresolvable deps are logged at Warning level but silently dropped. This can lead to runtime `FileNotFoundException` when the plugin tries to use that dependency. Failing the resolution for the affected plugin (adding to the failures list) would be safer than silently continuing.

---

## LOW severity issues

### 32. `Namotion.NuGet.Plugins` is misplaced under `src/HomeBlaze/`

It's a standalone library with zero HomeBlaze dependencies, uses the `Namotion.*` namespace prefix, and is `IsPackable=true`. Should be at `src/Namotion.NuGet.Plugins/` alongside other top-level `Namotion.*` projects.

> **Assessment: INTENTIONAL.** The library was deliberately moved to `src/HomeBlaze/` in this PR because `src/` root is reserved for `Namotion.Interceptor.*` projects. The plugin library will eventually live in its own repository. Keeping it under HomeBlaze for now avoids polluting the Interceptor namespace in the solution root.

---

### 33. Duplicate config model hierarchy

**Files:** `PluginConfiguration.cs` (inner `FeedEntry`/`PluginEntry`), `Models/PluginFeedEntry.cs`, `Models/PluginConfigEntry.cs`

Three parallel model hierarchies for the same data. Consolidate into one set of DTOs.

> **Assessment: VALID -- should fix.** `PluginConfiguration.FeedEntry`/`PluginEntry` and `Models/PluginFeedEntry`/`PluginConfigEntry` serve overlapping purposes. Additionally, `PluginConfigEntry` still has a `Path` property that was removed from `PluginEntry`. The Models classes appear unused now -- they should either replace the inner classes or be deleted.

---

### 34. Data carrier classes should be records

**Files:** `NuGetPluginConflict.cs`, `NuGetPluginFailure.cs`, `NuGetPluginLoadResult.cs`, `NuGetPackageInfo.cs`

~120 lines of constructor + get-only property boilerplate reducible to 4 one-line records with zero behavioral change.

> **Assessment: VALID -- good simplification.** These are pure data carriers with no behavior. Records would be more concise and provide structural equality for free. However, `NuGetPluginLoadResult.Success` is a computed property (not just a field), which records handle fine via a body member.

---

### 35. Test boilerplate duplication

**Files:** `NuGetPluginLoaderIntegrationTests.cs`, `NuGetPluginLoaderEdgeCaseTests.cs`, `FolderFeedPluginLoadingTests.cs`

Identical cache-dir setup, `IDisposable` cleanup, and loader construction duplicated across 3+ test classes.

**Recommendation:** Extract a shared test fixture or helper class.

> **Assessment: VALID but minor.** The boilerplate is ~5 lines per class (temp dir creation + cleanup). A shared fixture would save a few lines but adds indirection. Low priority.

---

### 36. Temp cache directory never cleaned up on Dispose

**Files:** `NuGetPluginLoader.cs:34-35, 383-398`

When no cache directory is specified, a temp directory with a GUID is created. `Dispose()` never cleans it up, accumulating extracted packages in the temp folder over time.

> **Assessment: VALID -- related to #27.** If we fix #27 (deterministic cache path), the accumulation problem goes away since the same path is reused across restarts. For the Guid case, adding cleanup to `Dispose()` would be correct, but risky if assemblies from the cache are still loaded. Best fixed by addressing #27.

---

### 37. Pre-release versions hardcoded to excluded

**Files:** `NuGetDependencyInfoProvider.cs:69`

`includePrerelease: false` is hardcoded. If a plugin explicitly requests a pre-release version, transitive dependencies with pre-release ranges will not be found.

> **Assessment: VALID -- should make configurable.** Only 3 locations have `includePrerelease: false` hardcoded. Easy fix: add `IncludePrerelease` (default `false`) to `NuGetPluginLoaderOptions` and thread it through `NuGetDependencyInfoProvider` and `NuGetPackageRepository`. This way pre-release plugins work correctly when explicitly opted in.

---

### 38. Broken relative link in plugins.md

**Files:** `plugins.md:35`

`[README](../../../../Namotion.NuGet.Plugins/README.md)` resolves incorrectly. Needs one additional `../` level.

> **Assessment: VALID -- should fix.** The relative path from `src/HomeBlaze/HomeBlaze/Data/Docs/architecture/design/plugins.md` to `src/HomeBlaze/Namotion.NuGet.Plugins/README.md` needs 6 levels up, not 4. Should be `../../../../../../Namotion.NuGet.Plugins/README.md`.

---

### 39. `PluginInfo` property name wrong in plugins.md

**Files:** `plugins.md:84`

Documentation states `AssemblyCount` but the actual property is `Assemblies` (a `string[]`).

> **Assessment: VALID -- should fix.** The `PluginInfo` class has `Assemblies` (a `string[]`), not `AssemblyCount`. The documentation should match the code.

---

### 40. Missing test projects in `project-structure.md`

**Files:** `project-structure.md:75-79`

`HomeBlaze.Plugins.Tests` and `Namotion.NuGet.Plugins.Tests` are not listed in the Test Projects table.

> **Assessment: VALID -- should fix.** Both test projects should be listed in `project-structure.md`.

---

### 41. Missing `HomeBlaze.SamplePlugin.HomeBlaze` in project-structure.md

**Files:** `project-structure.md:57-71`

The Razor SDK project providing Blazor UI components for the sample plugin is missing from the Project Overview table.

> **Assessment: VALID -- should fix.** `HomeBlaze.SamplePlugin.HomeBlaze` needs to be added to `project-structure.md`.

---

### 42. `PluginInfo.RemovePlugin` parameter should use `this.Name`

**Files:** `PluginInfo.cs:37-40`

Takes a `packageName` parameter from the UI `[Operation]` attribute but should use its own `Name` property instead.

> **Assessment: VALID -- should fix.** The method should be parameterless and use `_manager.RemovePlugin(Name)` since `PluginInfo` already knows its own name.

---

### 43. `PackageNameMatcher` regex cache is unbounded

**Files:** `PackageNameMatcher.cs:11`

Static `ConcurrentDictionary<string, Regex>` with no eviction. Low risk since patterns come from configuration and are finite.

> **Assessment: NOT AN ISSUE.** As the finding itself notes, patterns come from configuration and are finite (typically 2-5 entries). The cache is bounded by the number of distinct patterns ever used. No eviction needed.

---

### 44. `NuGetFeed.Url` XML doc omits local folder path support

**Files:** `NuGetFeed.cs:16-17, 32-33`

Says "NuGet V3 service index URL" but also accepts local directory paths per README and plugins.md.

> **Assessment: VALID -- should fix.** The XML doc on `Url` and the constructor parameter should mention that local folder paths are also accepted. E.g., "The NuGet V3 service index URL, or a local directory path for folder-based feeds."

---

### 45. README namespace table lists internal types as public

**Files:** `README.md:41-47`

Lists `PluginAssemblyLoadContext` and `PackageExtractor` (both `internal`) in the public API table. Misses public types: `NuGetPluginVersionConflictException`, `PackageNotFoundException`, `DependencyClassification`.

> **Assessment: VALID -- should fix.** The namespace organization table should only list public types. Remove `PluginAssemblyLoadContext` and `PackageExtractor`, add the missing public types.

---

### 46. Mermaid diagram arrows reversed in plugins.md

**Files:** `plugins.md:92-117`

`SAMPLE --> SAMPLEUI` and `BOGUS --> SAMPLE` show reversed dependency direction. Should be `SAMPLEUI --> SAMPLE` and `SAMPLE --> BOGUS`.

> **Assessment: NEED TO VERIFY.** Depends on whether arrows mean "depends on" or "is depended upon by." If the diagram uses "depends on" convention (A --> B means A depends on B), then `SAMPLEUI --> SAMPLE` and `SAMPLE --> BOGUS` is correct. Need to check the actual Mermaid diagram in plugins.md.

---

### 47. `Namotion.NuGet.Plugins.Tests` has spurious HomeBlaze references

**Files:** `Namotion.NuGet.Plugins.Tests.csproj:25-29`

`HomeBlaze.Abstractions` and `HomeBlaze.SamplePlugin.HomeBlaze` project references are not used by any tests. Undermines standalone independence.

> **Assessment: PARTIALLY VALID.** The `HomeBlaze.SamplePlugin.HomeBlaze` reference is a build-order-only dependency (`ReferenceOutputAssembly=false`) -- it ensures nupkgs are produced before tests run. This is intentional. The `HomeBlaze.Abstractions` reference may be needed for the folder-feed integration test (to use `IConfigurable` in type discovery assertions). If not used by any existing test, it could be removed until the integration test is added.

---

### 48. Version pinning inconsistency

**Files:** `Namotion.NuGet.Plugins.csproj`

`Microsoft.Extensions.Logging.Abstractions` pinned to exact `9.0.14` while adjacent `Microsoft.Extensions.DependencyModel` uses `9.*` floating. Standardize to `9.*`.

> **Assessment: VALID -- should fix.** Version pinning style should be consistent within the same csproj. Change `9.0.14` to `9.*` to match `DependencyModel`.

---

### 49. Missing unit tests for `DependencyGraphResolver` and `HostPackageVersionResolver`

The `IDependencyInfoProvider` abstraction and `FakeDependencyInfoProvider` exist specifically for testability but have limited test coverage. Diamond dependency edge cases, conflict detection, and resolution failures are undertested.

> **Assessment: VALID -- should improve.** The fake provider infrastructure is good but more edge case tests would catch issues like #2 (diamond dependency). Worth adding as part of ongoing test improvements.

---

### 50. `PluginAssemblyLoadContext.Load` falls back to default ALC silently

**Files:** `PluginAssemblyLoadContext.cs:42`

When an assembly is neither in `_hostAssemblyNames` nor `_privateAssemblyPaths`, returning `null` falls back to the default ALC silently. This can cause type identity mismatches.

**Recommendation:** Add a `LogDebug` so operators can diagnose `InvalidCastException` or `MissingMethodException` at runtime.

> **Assessment: VALID -- but `PluginAssemblyLoadContext` is `internal` and has no logger.** Returning `null` to fall back to the default ALC is the correct behavior per .NET ALC design. If an assembly resolves unexpectedly from the default context, the `IsAssemblyInDefaultContext` check in the loader already logs at Debug level (line 280). Adding logging directly to the ALC would require injecting a logger, which adds complexity for marginal benefit.

---

### 51. `FromAssemblies` uses assembly version, not NuGet package version

**Files:** `HostDependencyResolver.cs:74-90`

Uses `assembly.GetName().Version` (assembly version, often `X.0.0.0`) instead of NuGet package version. Some packages have assembly versions that diverge significantly from package versions.

> **Assessment: VALID -- known limitation.** `FromAssemblies` is explicitly documented as a fallback for when `deps.json` is unavailable (AOT, single-file). Assembly versions *do* diverge from NuGet versions for some packages (e.g., `System.Text.Json` assembly version is `9.0.0.0` while NuGet version is `9.0.5`). The XML doc already notes this is less accurate than `FromDepsJson()`. Worth documenting more prominently.

---

### 52. `SearchPackagesAsync` returns duplicates across feeds

**Files:** `CompositeNuGetPackageRepository.cs:33-43`

Results from all feeds are concatenated without deduplication. Feeds sharing packages will produce duplicate entries.

> **Assessment: VALID but minor.** `SearchPackagesAsync` is used for UI search functionality, not for loading. Duplicate results are cosmetic. A simple `DistinctBy(p => p.PackageName)` would fix it if needed.

---

### 53. `PluginAssemblyLoadContext` has duplicated null-check on `assemblyName.Name`

**Files:** `PluginAssemblyLoadContext.cs:28, 34`

Same `assemblyName.Name != null` check repeated twice.

**Recommendation:** Extract early: `if (assemblyName.Name is null) return null;` at the top.

> **Assessment: VALID -- trivial cleanup.** A single early return at the top would eliminate the duplicate null check.

---

### 54. `NuGetPackageRepository.DownloadPackageAsync` creates `SourceCacheContext` three times

**Files:** `NuGetPackageRepository.cs:61, 80, 90`

Three separate `SourceCacheContext` instances per retry iteration. A single context can be reused.

> **Assessment: PARTIALLY VALID.** Looking at the code, there are actually only two `SourceCacheContext` instances per iteration: one for metadata (`metadataCacheContext` at line 80) and one for download (`downloadCacheContext` at line 90). The `listCacheContext` at line 61 is only used in the version-resolve branch. The download context has `DirectDownload = true` which requires a separate context. Consolidating the two metadata contexts into one would be a minor improvement.

---

### 55. Feed credential model only supports API key auth

**Files:** `NuGetDependencyInfoProvider.cs:87-98`

`ApiKey` used as password with username "apikey" and `isPasswordClearText: true`. Some feeds use bearer tokens or other auth schemes.

> **Assessment: VALID but acceptable for v0.1.0.** The "apikey as password" pattern is the standard NuGet convention for API key authentication (used by nuget.org, Azure DevOps, GitHub Packages). Bearer token and other auth schemes are less common for NuGet feeds. Can be extended in the future if needed.

---

### 56. `Dispose()` is not thread-safe with respect to concurrent `LoadPluginsAsync`

**Files:** `NuGetPluginLoader.cs:383-398`

`_disposed` is checked and set without synchronization. `LoadPluginsAsync` does not check `_disposed`. A concurrent call could proceed past Phase 5 after `Dispose` has already unhooked the resolver.

**Recommendation:** Add `ObjectDisposedException` check at the start of `LoadPluginsAsync`.

> **Assessment: VALID -- easy fix.** Adding `ObjectDisposedException.ThrowIf(_disposed, this)` at the start of `LoadPluginsAsync` is a one-line defensive check. Worth adding.

---

### 57. `VersionCompatibilityTests` and edge case tests should be merged

**Files:** `VersionCompatibilityTests.cs`, `VersionCompatibilityEdgeCaseTests.cs`, `DependencyClassifierTests.cs`, `DependencyClassifierEdgeCaseTests.cs`

Each "edge case" file adds 3-4 tests for the same class. No organizational benefit from the split.

> **Assessment: VALID but minor.** Merging would reduce file count but doesn't change behavior. The split was likely an artifact of incremental development. Can be consolidated in a future cleanup pass.

---

### 58. `PackageExtractorTests` nupkg creation helpers are near-duplicates

**Files:** `PackageExtractorTests.cs:104-172`

`CreateTestNupkg`, `CreateTestNupkgMultiTfm`, `CreateEmptyNupkg` share the same structure. Collapse into one method with optional TFM list parameter.

> **Assessment: VALID -- minor cleanup.** Consolidating into one method with an optional TFM parameter would reduce duplication. Low priority.

---

### 59. `HostPackageVersionResolver.VersionResolutionResult` uses mutable Dictionary in a record

**Files:** `HostPackageVersionResolver.cs:8`

`ResolvedRanges` is a `Dictionary<string, VersionRange>` in a record (records imply value semantics). `Success` is derivable from `Conflicts.Count == 0`.

**Recommendation:** Use `IReadOnlyDictionary` for the property type. Remove `Success` or make it a computed property.

> **Assessment: VALID -- minor improvement.** `Success` is already derivable from `Conflicts.Count == 0` (and the record already computes it that way). Using `IReadOnlyDictionary` for `ResolvedRanges` would be better for a record. Low priority since this is an `internal` type.

---

### 60. `PackageNameMatcher` glob semantics differ from typical conventions

**Files:** `PackageNameMatcher.cs:21`

`*` matches "any characters within a single dot-separated segment". Users familiar with MSBuild/NuGet globbing may expect `*` to match across dots.

**Recommendation:** Consider supporting `**` for multi-segment matching, or document the divergence prominently.

> **Assessment: VALID -- should fix.** `*` should match anything including dots, consistent with NuGet's own `packageSourceMapping` conventions. Change the regex from `[^.]+` to `.+`. Update the README's pattern matching table and examples accordingly. This also fixes the real-world issue where `Namotion.Devices.*.Abstractions` wouldn't match `Namotion.Devices.Philips.Hue.Abstractions`.

---

### 61. README installation section -- package not published

**Files:** `README.md:20-22`

Shows `dotnet add package Namotion.NuGet.Plugins` but package is version 0.1.0 and not yet on NuGet.org.

> **Assessment: VALID -- expected for pre-release.** The installation section is forward-looking for when the package is published. Could add a note like "Not yet published to NuGet.org" or remove the section until it is. Minor.

---

## Summary

| Severity | Count |
|----------|-------|
| High     | 9     |
| Medium   | 22    |
| Low      | 30    |

## Top priorities before merge

1. **Fix `IsAssemblyInDefaultContext` side effects** (#1) -- defeats plugin isolation, the core value proposition
2. **Fix diamond dependency resolution** (#2) -- silent version downgrades under real NuGet conditions
3. **Fix SamplePlugin version mismatch** (#9) -- integration tests will fail as-is
4. **Fix `DownloadResourceResult` disposal leak** (#5) -- resource leak at scale
5. **Fix ALC unload blocked by strong Assembly references** (#7) -- prevents plugin hot-reload from working
