---
title: Version and Updates
navTitle: Updates
---

# Version and Updates

The `Installation` subject shows which HomeBlaze version runs, how it was built, its environment and whether a newer release or, for an edge build, newer commits exist. A new installation has one in `Services/Installation.json`; otherwise create it from the create dialog in the `System` group. Only one can be created.

| Property | Description |
|----------|-------------|
| `Version`, `Channel` | The running version and whether it is a `Release`, an `Edge` build of the latest commit, or a `Development` build |
| `Commit`, `Tag`, `Branch`, `CommitDate`, `BuildTime` | Where and when the build was made; only CI builds have a tag, branch and times |
| `RepositoryUrl`, `VersionUrl`, `CommitUrl`, `BuildRunUrl` | Links to the repository, to the release or commit, and to the CI run |
| `EnvironmentName`, `IsDeveloperMode` | The hosting environment and whether developer mode is on |
| `DataDirectory`, `DataVolumeFreeSpace`, `DataVolumeTotalSize`, `DataVolumeUsage` | The data folder and the free space, total size and usage of its volume, refreshed every `PollingInterval` (30 seconds by default) |
| `ConnectedCircuitCount` | The open browser tabs, updated at once |
| `SubjectCount`, `PropertyCount`, `StatePropertyCount`, `ConfigurationPropertyCount` | The subjects of the graph and their properties: all registered ones, including attributes and methods, and those with state or configuration; refreshed every `PollingInterval` |
| `OperationCount`, `QueryCount` | The operations and queries of all subjects; refreshed every `PollingInterval` |
| `Clients` | The browsers connected to this installation and the named ones, see [Clients](#clients) |
| `UpdateAvailable` | Whether a newer release published at least 30 minutes ago exists or, for an edge build, whether its branch has newer commits; empty until the first successful check, and stays empty when the running version of a release is not a semantic version or GitHub cannot compare the commit of an edge build with its branch |
| `LatestVersion`, `LatestReleaseDate`, `LatestReleaseUrl` | The latest release published at least 30 minutes ago, only checked for release builds |
| `CommitsBehind`, `CompareUrl` | How many commits of its branch an edge build lacks, and the GitHub page that lists them; only checked for edge builds |
| `LastUpdateCheck`, `UpdateCheckStatus` | When the last check finished, and why it failed, cannot run, holds back a release published less than 30 minutes ago, or could not place the commit of an edge build on its branch |
| `IsUpdaterAvailable` | Whether the updater runs next to this installation, see [Install updates](#install-updates) |
| `UpdateInstallState`, `UpdateInstallStatus`, `LastUpdateInstall` | The progress of the current or last install, what it did or why it failed, and when it finished |

The installation widget shows the version, channel and build, an available release or the number of new commits, the update check status, the data volume and the environment on a page.

## Clients

Every open browser tab is a client in `Clients` while it is open, shown as `Anonymous client`. To keep a browser, open the menu at the right end of the app bar and choose `Name this browser`, or call `Rename` on its client. A named client is saved in the file of the Installation subject (`Services/Installation.json` in a new installation), the browser remembers it in a cookie for a year, and all its tabs belong to it. The cookie holds a secret token of the client rather than its id, so knowing the path of a client is not enough to receive its notifications. It stays in the list while the browser is closed, with `IsConnected` false. `Forget` removes the name again.

A client receives notifications like any other notification target, for example from an automation or an AI agent: it shows them as a dialog in its open tabs. A notification with the `Emergency` priority stays until its button is clicked. When none of its tabs is open, sending fails and nothing is kept for later, so name the browsers that should receive notifications, such as a wall tablet. A tab whose connection dropped still counts as open until the server gives up on it, by default after about 3 minutes, so sending to it in that time succeeds and the notification shows only if the tab reconnects before then.

## Update check

The update check is on by default for release and edge builds. For a development build, an edge build without a commit or branch, or a repository URL that is missing, not on GitHub or not in the owner/name form, `UpdateCheckStatus` says why no check is possible. To install an update, use `Install update` (see [Install updates](#install-updates)) or pull the new image as described in [Installation](installation.md#docker).

A check of a release build asks the GitHub API for the latest release. A newer release is offered once it is 30 minutes old on GitHub's clock, so that its image is published; until then the check keeps the release it found before, `UpdateCheckStatus` says that the new one is pending, and the next check runs when the 30 minutes have passed. A check of an edge build asks the GitHub API to compare its commit with the branch it was built from, normally `master`, and reads how many commits the branch has that the build lacks; the request leaves out the changed files, so its answer stays small however far behind the build is. When the commit is not on the branch, for example after the branch was rewritten, `UpdateCheckStatus` says so while `CommitsBehind` still counts the commits the build lacks. When GitHub cannot compare them, for example because the commit or the branch no longer exists, `UpdateCheckStatus` says so and `UpdateAvailable`, `CommitsBehind` and `CompareUrl` are cleared. Neither changes `Status` nor delays the next check.

Each check sends one request without a token and nothing about the installation except the version in the user agent; GitHub sees the IP address the request comes from. Set `UpdateCheckEnabled` to false for an installation that must not connect to the internet on its own.

The first check runs 5 to 60 minutes after each start, then every `UpdateCheckInterval` (24 hours by default, from 24 hours up to 365 days). A change of `UpdateCheckEnabled` or `UpdateCheckInterval` in the editor or the configuration file applies at once; one written directly to the property, for example through MCP, applies within an hour.

A failed check is retried after 1 hour, doubling each time up to 24 hours. A longer wait that GitHub asks for wins, also capped at 24 hours. The error stays in `UpdateCheckStatus` until a check succeeds and does not change `Status`. GitHub allows 60 unauthenticated requests per hour per IP address, shared by everything behind that address, so other clients can use them up and the check fails with "GitHub rate limit reached." A request that takes longer than 30 seconds fails the check with "GitHub did not answer in time."

`Check for updates` checks immediately, also while the scheduled check is off, at most once per minute. It is refused while the installation is stopped or while GitHub asked to wait.

## Install updates

A Docker installation can install an update from the UI when the `updater` service of the compose file runs; set it up as described in [Installation](installation.md#updater). `IsUpdaterAvailable` is true while the updater touched `data/.updater-heartbeat` within the last 60 seconds (it does so every 30 seconds), and is refreshed every `PollingInterval`. It can read false while an install runs, because the updater does not touch the heartbeat while compose runs.

`Install update` asks for confirmation, then asks the updater to pull the image of the tag in the compose file and recreate the `homeblaze` container with it. It is available while the updater runs, the installation runs, an update is available and no install is in progress, so after a new release with the scheduled check off, run `Check for updates` first. MCP clients and agents cannot call it.

| `UpdateInstallState` | Meaning |
|-----|-----|
| `None` | No install since the start |
| `Requested` | Waiting for the updater to take the request; fails after 30 seconds |
| `Installing` | The updater pulls the image and recreates the container; fails 30 minutes after the request |
| `Succeeded` | HomeBlaze runs the new version; `UpdateInstallStatus` names the old and new version, or the commits for an edge build |
| `UpToDate` | No newer image exists for the tag, for example with a pinned version, while the image of a new edge commit is not published yet, or when CI took longer than 30 minutes to publish the image of a new release |
| `Failed` | Compose failed or the updater did not answer; `UpdateInstallStatus` shows the last line of its output |

On a small device such as a Raspberry Pi Zero 2 W, the pull can take a few minutes and the UI is slow meanwhile, see [Raspberry Pi](installation.md#raspberry-pi). While the container is recreated, open tabs reconnect on their own. The new process reports the outcome, so the result stays visible until the next restart. HomeBlaze and the updater talk through four files in the data folder: HomeBlaze writes `.update-request`, and the updater renames it to `.update-running`, writes the compose output to `.update-log` and the exit code to `.update-result`. HomeBlaze deletes `.update-running` and `.update-result` after it reported the outcome, while `.update-log` stays until the next install overwrites it. A failure is also written to the HomeBlaze log.

If the updater restarts during an install, the install fails with "The updater restarted while an install ran." A pull that takes longer than 25 minutes fails as well.

The updater only runs `docker compose pull homeblaze` and `docker compose up -d homeblaze` in the folder of the compose file. It applies changes of the `homeblaze` service in the compose file, but never updates other services or itself, and never chooses a version. It expects the service to be named `homeblaze` and the data folder to be `./data`, as in the shipped compose file, and does not support a project started with `docker compose -p`.

If `Install update` stays disabled, check that:

- `docker compose ps` lists `updater`.
- `data/.updater-heartbeat` is recent.
- `Check for updates` found an update.
- No install is in progress.
