---
title: Version and Updates
navTitle: Updates
position: 9
---

# Version and Updates

The `Installation` subject shows which HomeBlaze version runs, how it was built, its environment and whether a newer release or, for an edge build, newer commits exist. A new installation has one in `Servers/Installation.json`; otherwise create it from the create dialog in the `System` group. Only one can be created.

| Property | Description |
|----------|-------------|
| `Version`, `Channel` | The running version and whether it is a `Release`, an `Edge` build of the latest commit, or a `Development` build |
| `Commit`, `Tag`, `Branch`, `CommitDate`, `BuildTime` | Where and when the build was made; only CI builds have a tag, branch and times |
| `RepositoryUrl`, `VersionUrl`, `CommitUrl`, `BuildRunUrl` | Links to the repository, to the release or commit, and to the CI run |
| `EnvironmentName`, `IsDeveloperMode` | The hosting environment and whether developer mode is on |
| `DataDirectory`, `DataVolumeFreeSpace`, `DataVolumeTotalSize`, `DataVolumeUsage` | The data folder and the free space, total size and usage of its volume, refreshed every `PollingInterval` (30 seconds by default) |
| `UpdateAvailable` | Whether a newer release exists or, for an edge build, whether its branch has newer commits; empty until the first successful check, and stays empty when the running version of a release is not a semantic version or GitHub cannot compare the commit of an edge build with its branch |
| `LatestVersion`, `LatestReleaseDate`, `LatestReleaseUrl` | The latest release, only checked for release builds |
| `CommitsBehind`, `CompareUrl` | How many commits of its branch an edge build lacks, and the GitHub page that lists them; only checked for edge builds |
| `LastUpdateCheck`, `UpdateCheckStatus` | When the last check finished, and why it failed, cannot run, or could not place the commit of an edge build on its branch |

The installation widget shows the version, channel and build, an available release or the number of new commits, the update check status, the data volume and the environment on a page.

## Update check

The update check is on by default for release and edge builds. For a development build, an edge build without a commit or branch, or a repository URL that is missing, not on GitHub or not in the owner/name form, `UpdateCheckStatus` says why no check is possible. To install an update, pull the new image as described in [Installation](installation.md#docker).

A check of a release build asks the GitHub API for the latest release. A check of an edge build asks the GitHub API to compare its commit with the branch it was built from, normally `master`, and reads how many commits the branch has that the build lacks; the request leaves out the changed files, so its answer stays small however far behind the build is. When the commit is not on the branch, for example after the branch was rewritten, `UpdateCheckStatus` says so while `CommitsBehind` still counts the commits the build lacks. When GitHub cannot compare them, for example because the commit or the branch no longer exists, `UpdateCheckStatus` says so and the other values stay empty. Neither changes `Status` nor delays the next check.

Each check sends one request without a token and nothing about the installation except the version in the user agent; GitHub sees the IP address the request comes from. Set `UpdateCheckEnabled` to false for an installation that must not connect to the internet on its own.

The first check runs 5 to 60 minutes after each start, then every `UpdateCheckInterval` (24 hours by default, from 24 hours up to 365 days). A change of `UpdateCheckEnabled` or `UpdateCheckInterval` in the editor or the configuration file applies at once; one written directly to the property, for example through MCP, applies within an hour.

A failed check is retried after 1 hour, doubling each time up to 24 hours. A longer wait that GitHub asks for wins, also capped at 24 hours. The error stays in `UpdateCheckStatus` until a check succeeds and does not change `Status`. GitHub allows 60 unauthenticated requests per hour per IP address, shared by everything behind that address, so other clients can use them up and the check fails with "GitHub rate limit reached." A request that takes longer than 30 seconds fails the check with "GitHub did not answer in time."

`Check for updates` checks immediately, also while the scheduled check is off, at most once per minute. It is refused while the installation is stopped or while GitHub asked to wait.
