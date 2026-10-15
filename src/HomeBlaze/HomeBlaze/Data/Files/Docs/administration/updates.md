---
title: Version and Updates
navTitle: Updates
position: 9
---

# Version and Updates

The `Installation` subject shows which HomeBlaze version runs, how it was built, its environment and whether a newer release exists. A new installation has one in `Servers/Installation.json`; otherwise create it from the create dialog in the `System` group. Only one can be created.

| Property | Description |
|----------|-------------|
| `Version`, `Channel` | The running version and whether it is a `Release`, an `Edge` build of the latest commit, or a `Development` build |
| `Commit`, `Tag`, `Branch`, `CommitDate`, `BuildTime` | Where and when the build was made; only CI builds have a tag, branch and times |
| `RepositoryUrl`, `VersionUrl`, `CommitUrl`, `BuildRunUrl` | Links to the repository, to the release or commit, and to the CI run |
| `EnvironmentName`, `IsDeveloperMode` | The hosting environment and whether developer mode is on |
| `DataDirectory`, `DataVolumeFreeSpace`, `DataVolumeTotalSize`, `DataVolumeUsage` | The data folder and the free space, total size and usage of its volume, refreshed every `PollingInterval` (30 seconds by default) |
| `UpdateAvailable`, `LatestVersion`, `LatestReleaseDate`, `LatestReleaseUrl` | The latest release and whether it is newer than the running version; empty until the first successful check, and `UpdateAvailable` stays empty when the running version is not a semantic version |
| `LastUpdateCheck`, `UpdateCheckStatus` | When the last check finished, and why it failed or cannot run |

The installation widget shows the version, channel and build, an available update, the update check status, the data volume and the environment on a page.

## Update check

The update check is on by default, but only release builds can check. For an edge or development build, or a missing or non-GitHub repository URL, `UpdateCheckStatus` says why no check is possible. To install an update, pull the new image as described in [Installation](installation.md#docker).

A check asks the GitHub API for the latest release. It sends nothing about the installation except the version in the user agent; GitHub sees the IP address the request comes from. Set `UpdateCheckEnabled` to false for an installation that must not connect to the internet on its own.

The first check runs 5 to 60 minutes after each start, then every `UpdateCheckInterval` (24 hours by default, from 24 hours up to 365 days). A change in the editor or the configuration file applies at once; one written directly to the property, for example through MCP, applies within an hour.

A failed check is retried after 1 hour, doubling each time up to 24 hours. A longer wait that GitHub asks for wins, also capped at 24 hours. The error stays in `UpdateCheckStatus` until a check succeeds and does not change `Status`.

`Check for updates` checks immediately, also while the scheduled check is off, at most once per minute. It is refused while the installation is stopped or while GitHub asked to wait.
