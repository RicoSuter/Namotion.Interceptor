---
title: Installation Subject
navTitle: Installation
status: Planned
---

# Installation Subject Plan

**Status: Planned**

## Overview

An `Installation` subject describes the running HomeBlaze installation: which build it is, which assemblies it loaded and whether they are consistent, the environment it runs in, its overall health and size, and whether a newer version exists. It complements the `Host system` subject, which describes the machine and the process, and does not repeat its values (runtime version and process uptime stay on `Host system` → `Process`).

## Build Stamping

### What exists today

The .NET SDK's built-in SourceLink appends `+<full commit sha>` to `AssemblyInformationalVersion` of every assembly built from a git checkout, and `src/Directory.Build.props` adds `AssemblyMetadata("RepositoryUrl", ...)`. Every Namotion, HomeBlaze and device assembly carries both already, including local builds.

HomeBlaze pins `Version` to `1.0.0` because plugins bind by assembly version, so the version shown to users comes from `InformationalVersion`, never from `AssemblyVersion`.

### New metadata

A new `src/Directory.Build.targets` adds one `AssemblyMetadata` item per non-empty MSBuild property, in a target that runs before `GetAssemblyAttributes`:

| MSBuild property | Attribute key | Example |
|---|---|---|
| `BuildTag` | `Namotion.Build.Tag` | `v0.5.0` |
| `SourceBranch` | `Namotion.Build.Branch` | `master` |
| `SourceCommitDate` | `Namotion.Build.CommitDate` | ISO 8601 |
| `BuildTime` | `Namotion.Build.Time` | ISO 8601 |
| `BuildRunUrl` | `Namotion.Build.RunUrl` | GitHub Actions run URL |
| `ContainerImageTags` | `Namotion.Build.ImageTags` | `0.5.0;0.5;latest` |

Only CI sets these properties. Local builds set none, so their output stays byte stable and incremental, and no git process runs during a build.

### CI changes in `.github/workflows/build.yml`

- The `Compute tags` step also emits the commit date (`git log -1 --format=%cI`, which works on a depth 1 checkout), the build time, the branch and the run URL, and `container-publish` passes them as properties.
- Edge builds pass `InformationalVersion=edge` instead of `edge+<short sha>`. Today the SDK appends the full sha to the value CI passes, which yields `edge+abc1234.<full sha>`; after the change it is `edge+<full sha>`.
- The `pack` job passes `InformationalVersion=<release version>` and the build properties. Today it only sets `PackageVersion`, so published library assemblies report `0.1.0+<sha>`. `AssemblyVersion` stays unchanged, so binding is unaffected.
- CI builds set `ContinuousIntegrationBuild=true`, as SourceLink recommends.
- `container-build` asserts that the image's `HomeBlaze.dll` carries `Namotion.Build.Time` and an informational version without a doubled sha.

### Reading at runtime

`BuildInformation.FromAssembly(Assembly)` reads only `AssemblyInformationalVersionAttribute` and `AssemblyMetadataAttribute`, which is trimming and AOT safe. Third party packages follow SourceLink unevenly, so parsing is lenient:

- **Version** is the informational version up to the first `+`.
- **Commit** is the text after the first `+`, cut at the first `.`. This handles a doubled sha (`7.3.1+548f69a….548f69a…`) and accepts a short sha (`6.1.0+f4da16f15a`). Without a `+` the commit is empty.
- **Repository URL** is normalized for comparison: a trailing `.git` and `/` are removed and the comparison ignores case.

## Subject

`HomeBlaze.Services/Diagnostics/HomeBlazeInstallation.cs`, next to `HostSystem`: `[Category("System")]`, `[DisplayName("Installation")]`, `[SingleInstance]`, a `BackgroundService` implementing `IConfigurable`, `IMonitoredService`, `ITitleProvider`, `IIconProvider` and `ILastUpdatedProvider`. New installations get one in `Seed/Files/Servers/Installation.json` with the update check disabled.

### Configuration

| Property | Default | Notes |
|---|---|---|
| `Name` | empty | Title falls back to `Installation` |
| `PollingInterval` | 30 s | Refreshes health, data volume and inventory; at least 1 s |
| `UpdateCheckEnabled` | `false` | Opt in to the outbound GitHub call |
| `UpdateCheckInterval` | 24 h | At least 24 h |

### Build (`[Category("Build")]`, read once from the entry assembly)

| Property | Type | Notes |
|---|---|---|
| `Version` | `string` | `0.5.0`, `edge`, or `1.0.0` for a local build |
| `Channel` | `InstallationChannel` | `Release` when `Tag` is set, `Edge` when `Version` is `edge`, `Development` otherwise |
| `Commit` | `string?` | Full sha |
| `CommitUrl` | `Uri?` | Derived, `<RepositoryUrl>/commit/<Commit>` |
| `VersionUrl` | `Uri?` | Derived, `<RepositoryUrl>/releases/tag/<Tag>` for `Release`, `CommitUrl` otherwise |
| `Tag`, `Branch`, `ImageTags` | `string?` | Empty for local builds |
| `CommitDate`, `BuildTime` | `DateTimeOffset?` | Empty for local builds |
| `BuildRunUrl` | `Uri?` | Empty for local builds |

### Environment (`[Category("Environment")]`)

| Property | Type | Source |
|---|---|---|
| `EnvironmentName` | `string` | `IHostEnvironment` |
| `IsDeveloperMode` | `bool` | The singleton `DeveloperModeService`, kept current through `OnChange` |
| `ListeningUrls` | `string[]` | `IServerAddressesFeature` once the server started; if `HomeBlaze.Services` cannot reference the server abstractions, the host passes them in |
| `DataDirectory` | `string` | Directory of the root configuration file |
| `DataVolumeFreeSpace`, `DataVolumeTotalSize` | `long?`, bytes | `DriveInfo` of `DataDirectory`, polled |
| `DataVolumeUsage` | `decimal?`, percent | Derived |
| `StartTime` | `DateTimeOffset` | Process start time |

### Health (polled)

| Property | Type | Notes |
|---|---|---|
| `SubjectCount` | `int` | Subjects known to the registry |
| `MonitoredServiceCount` | `int` | Subjects implementing `IMonitoredService` |
| `ServicesInErrorCount` | `int` | Of those, with `Status == Error` |

`Status` reflects the installation subject itself: `Running` while polling, `Error` only when the update check fails. Failing services never put the installation into `Error`; their count is a separate value. `IconColor` is `Warning` when an update is available or `HasVersionMismatch` is true, and `Error` when `Status` is `Error`.

## Assembly Inventory

### Shape

```
Installation
├─ HasVersionMismatch, MismatchedRepositories
└─ Repositories                      InstallationRepository[]
   ├─ Namotion.Interceptor  ⚠        Name, RepositoryUrl, HasMismatch
   │  └─ Assemblies                  InstallationAssembly[]
   │     ├─ HomeBlaze                Name, Version, Commit, LoadContext, HasMismatch
   │     ├─ Namotion.Interceptor
   │     └─ Namotion.Interceptor (MyCompany.SamplePlugin1)  ⚠
   ├─ MudBlazor
   │  └─ Assemblies
   │     └─ MudBlazor
   └─ Unknown
      └─ Assemblies
         └─ SomeUnstampedLibrary
```

- `InstallationRepository`: `Name` (last segment of the normalized URL, or `Unknown`), `RepositoryUrl` (`Uri?`), `HasMismatch`, `Assemblies`.
- `InstallationAssembly`: `Name`, `Version`, `Commit`, `LoadContext` (`Default` or the name of the plugin load context), `HasMismatch`.
- Assemblies without a repository URL share the `Unknown` repository.
- Plugin metadata such as package version and load status stays on `PluginManager` and is not repeated.

### Mismatch rules

1. **Duplicate assembly**: the same assembly name is loaded more than once (for example in the default context and in a plugin context) with a different version or commit. Each affected `InstallationAssembly` gets `HasMismatch`. This applies to every repository.
2. **Own repository not uniform**: assemblies from the entry assembly's repository have more than one non-empty commit. That `InstallationRepository` gets `HasMismatch`. Other repositories only list their commits, because a monorepo such as `dotnet/dotnet` ships packages at different patch versions and would always warn.

An empty commit never causes a mismatch. `HasVersionMismatch` is true when any repository or assembly has `HasMismatch`; `MismatchedRepositories` lists the names of the repositories that have `HasMismatch` themselves or contain an assembly that has it.

### Scope and refresh

- Enumerates `AssemblyLoadContext.All`, skipping dynamic assemblies and shared framework assemblies (location under `RuntimeEnvironment.GetRuntimeDirectory()`).
- On each poll the subject sums the assembly counts of all load contexts and rebuilds the inventory only when the sum changed, so a steady state poll allocates nothing for the inventory.
- An assembly that throws while reading attributes is listed with an empty version and commit.
- `AssemblyInventory` works on plain `(name, informationalVersion, repositoryUrl, loadContext)` records, so grouping and rules are tested without real load contexts.

## Update Check

### Units

- `GitHubReleaseClient` (internal): calls the GitHub REST API with an `HttpClient` from `IHttpClientFactory`.
- `UpdateEvaluator` (internal, pure): combines the build information and the responses into the update state, including the SemVer comparison.
- `UpdateSchedule` (internal, pure): computes the next check time from `TimeProvider`, including jitter, backoff and throttling.

### Behavior by channel

The repository is the entry assembly's `RepositoryUrl`, so a fork checks its own releases. A non `github.com` URL skips the check with `UpdateCheckStatus = "Not supported for <host>"`.

- **Release**: `GET /repos/{owner}/{repo}/releases/latest`, which excludes drafts and pre-releases. `UpdateAvailable` is true when the latest version is greater under SemVer ordering, so a running `0.6.0-beta.1` does not treat `0.5.0` as an update.
- **Edge**: the same release call plus `GET /repos/{owner}/{repo}/compare/{Commit}...master`. `CommitsBehind` is `ahead_by` and `ChangesUrl` is the compare page. `UpdateAvailable` is true when `CommitsBehind > 0`.
- **Development**: no check; `UpdateCheckStatus = "Not available for development builds"`.

### Properties (`[Category("Update")]`)

| Property | Type | Notes |
|---|---|---|
| `UpdateAvailable` | `bool?` | Null until the first successful check |
| `LatestVersion` | `string?` | Tag without the `v` prefix |
| `LatestReleaseDate` | `DateTimeOffset?` | |
| `LatestReleaseUrl` | `Uri?` | Release page |
| `ChangesUrl` | `Uri?` | Edge only, compare page |
| `CommitsBehind` | `int?` | Edge only |
| `LastUpdateCheck` | `DateTimeOffset?` | |
| `UpdateCheckStatus` | `string?` | Why no check ran, or the last error |

### Schedule and GitHub rate limits

Unauthenticated requests are limited to 60 per hour per public IP, shared by everything behind it. A `User-Agent` header is required, and a `304 Not Modified` answer to a conditional request does not count against the limit.

- Requests send `User-Agent: HomeBlaze/<Version>` and `If-None-Match` with the stored ETag.
- The first check runs at a random time 5 to 60 minutes after start. `LastUpdateCheck` is not persisted, so the jitter keeps a container in a restart loop from calling GitHub on every start and spreads installations over time.
- Afterwards a check runs every `UpdateCheckInterval` (24 h minimum), which is at most 2 requests per day.
- On failure (network error, `403`, `429`, malformed JSON) `Status` becomes `Error` with the message, the last known values stay, and the next attempt backs off 1 h, 2 h, 4 h and so on, capped at 24 h. A later `Retry-After` or `x-ratelimit-reset` wins. The next success sets `Status` back to `Running`.
- When `UpdateCheckEnabled` is false, no scheduled check runs and `Status` never becomes `Error` because of the update check.

### Operation

`Check for updates` runs a check immediately, also while `UpdateCheckEnabled` is false, since the click is explicit consent to the outbound call. It runs at most once per minute.

## URL Values in the Property Panel

`SubjectPropertyPanel.razor` shows the `OpenInNew` icon button for a `[State]` value of type `Uri` that is absolute with an `http` or `https` scheme, with `target="_blank"` and `rel="noopener noreferrer"`. Any other `Uri`, including `javascript:`, keeps the copy button. Every subject gets this behavior, not only the installation.

## Dashboard Widget

`HomeBlaze.Components/Components/InstallationWidget.razor`, following `HostSystemWidget`: `[SubjectComponent(SubjectComponentType.Widget, typeof(HomeBlazeInstallation))]`, a `MudPaper` with `data-testid="installation-widget"`, formatted with `ValueReader` and `StateUnitExtensions`.

```
┌────────────────────────────────────────────────────────┐
│ ⓘ  Installation                     [Release] [⚠ 1]   │
│    0.5.0 · b1b7c2f · built 2026-10-14            ↗     │
├────────────────────────────────────────────────────────┤
│ ⬆ Update available: 0.6.0 (2026-10-20)  Release notes ↗│
│ Services  14 running · 1 error                         │
│ Subjects  1,284                                        │
│ Data      12.4 GB free of 64 GB (81 %)                 │
│ ▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓░░░░                                   │
│ Production · developer mode off                        │
└────────────────────────────────────────────────────────┘
```

- Header: title, `Version · short commit · built <BuildTime>` (`development build` without a build time), and a link icon to `VersionUrl`. Chips show `Channel` and, on a mismatch, a warning chip with the number of `MismatchedRepositories`.
- Update line: shown when `UpdateAvailable` is true, linking `LatestReleaseUrl` (edge: `<CommitsBehind> commits behind master` linking `ChangesUrl`). Shows `Update check failed` when `Status` is `Error`; hidden while the check is disabled.
- Then services, subjects, the data volume with a progress bar of `DataVolumeUsage`, and the environment.
- External links use `target="_blank" rel="noopener noreferrer"`.

## Files

| Area | File | Change |
|---|---|---|
| Build | `src/Directory.Build.targets` (new) | `Namotion.Build.*` properties to `AssemblyMetadata` |
| CI | `.github/workflows/build.yml` | Build properties, edge informational version, pack informational version, `ContinuousIntegrationBuild`, image assertion |
| Subject | `HomeBlaze.Services/Diagnostics/HomeBlazeInstallation.cs` | Subject, polling, scheduling |
| | `…/Diagnostics/InstallationRepository.cs`, `InstallationAssembly.cs`, `InstallationChannel.cs` | Inventory subjects and channel enum |
| | `…/Diagnostics/BuildInformation.cs`, `AssemblyInventory.cs` | Parsing, grouping, mismatch rules |
| | `…/Diagnostics/GitHubReleaseClient.cs`, `UpdateEvaluator.cs`, `UpdateSchedule.cs` | Update check |
| | `HomeBlaze.Services.csproj` | `Microsoft.Extensions.Http`, server addresses abstraction if needed |
| UI | `HomeBlaze.Host/Components/Browser/SubjectPropertyPanel.razor` | `Uri` link icon |
| | `HomeBlaze.Components/Components/InstallationWidget.razor` | Dashboard widget |
| Seed | `HomeBlaze/Seed/Files/Servers/Installation.json`, `HomeBlaze/Data/Files/Servers/Installation.json` | Default subject |
| Docs | `Docs/administration/monitoring.md` | `Installation` section: properties, update check and its outbound call, reading a mismatch |

## Testing

Unit tests in `HomeBlaze.Services.Tests`:

- `BuildInformation`: `x+sha`, `edge+sha`, no commit, short commit, doubled commit; URL normalization of `.git`, trailing `/` and case.
- `AssemblyInventory`: grouping, the `Unknown` repository, rule 1 across two load contexts, rule 2 for the own repository, and no mismatch for a third party monorepo with mixed patch versions.
- `UpdateEvaluator`: SemVer ordering of pre-release and release, all three channels, a non GitHub repository.
- `GitHubReleaseClient` with a fake handler: `200`, `304` with ETag, `403` and `429` with reset headers, malformed JSON.
- `UpdateSchedule` with a fake `TimeProvider`: first check jitter window, backoff doubling and cap, `Retry-After`, manual check throttle.
- `HomeBlazeInstallation`: health counts with a service in `Error`, `Status` stays `Running` with the check disabled, `IconColor` on mismatch and on an available update.

E2E tests in `HomeBlaze.E2E.Tests`:

- `SubjectBrowserLayoutTests`: a `Uri` state shows the link icon with `target="_blank"`; a `javascript:` URI shows the copy icon.
- Installation widget on a dashboard shows the version, the channel chip and the `VersionUrl` link.

The change touches the HomeBlaze UI, so `dotnet test src/HomeBlaze/HomeBlaze.E2E.Tests` runs in addition to the unit tests. No connector changes, so the Connector Tester and benchmarks are not needed.

## Out of Scope

- An automatic update or image pull action.
- Running git at local build time to stamp tag, date or a dirty flag.
