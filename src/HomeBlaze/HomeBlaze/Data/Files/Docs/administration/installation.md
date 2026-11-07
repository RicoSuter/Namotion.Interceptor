---
title: Installation Guide
navTitle: Installation
---

# Installation

HomeBlaze runs as a container (recommended for a permanent installation) or from source (for development).

## Docker

HomeBlaze is published as a container image for amd64 and arm64 (Raspberry Pi 4 and 5) at `ghcr.io/ricosuter/homeblaze`.

| Tag | Content |
|-----|---------|
| `X.Y.Z` | Every release |
| `X.Y`, `latest` | The repository's latest release |
| `sha-<commit>` | Master builds that pass CI |
| `edge` | The newest master commit that passes CI |

Pre-releases are only published with their exact version tag. Until the first release is published, use the `edge` tag by setting `image: ghcr.io/ricosuter/homeblaze:edge` in the compose file. Images are published from the upstream repository only; forks do not publish. GitHub Container Registry makes a new package private on its first push, so the maintainer makes it public once in the package settings.

Copy [docker-compose.yml](https://github.com/RicoSuter/Namotion.Interceptor/blob/master/src/HomeBlaze/docker-compose.yml) into an empty folder, adjust the time zone (`TZ`), and start it:

```bash
docker compose up -d
```

HomeBlaze is then available at http://localhost:8080 and asks you to sign in, see [First start](#first-start). To upgrade, run `docker compose pull` and `docker compose up -d`, and read [Upgrading to sign-in](#upgrading-to-sign-in) when you come from a version without a login.

### First start

HomeBlaze requires signing in. On the first start it creates the user `admin` with an empty password:

1. Open http://localhost:8080 and sign in as `admin` with an empty password.
2. Set a password when HomeBlaze asks for it, at least 10 characters.
3. Add the other people with the operation "Add User" of the `Authorization` subject, see [Authorization](authorization.md#people-and-users).

The empty password is accepted from any address until a password is set, so set it right away and keep a new installation off a network you do not trust until you have. The welcome page opens after you sign in. If you lose the password or the authentication database, see [Recovery](authorization.md#recovery): `docker compose run --rm homeblaze --reset-admin`.

### Data folder

Everything HomeBlaze keeps lives in the folder mounted at `/data` (`./data` in the compose file). See [Configuration](configuration.md#data-folder) for the folder layout and the settings that control it.

On the first start, when `Root.json` does not exist, HomeBlaze copies a starting set into the folder: a welcome page, a help page, this documentation, an empty plugin list, a SQLite history store that records to `History/Sqlite`, a `Host system` subject in `Servers/HostSystem.json` and an `Installation` subject in `Servers/Installation.json` (see [Version and Updates](updates.md)). Existing files are never overwritten, except for the documentation described below. The demo devices and sample plugins are only available when running from source.

Back up HomeBlaze by copying this folder. Stop HomeBlaze first (`docker compose stop`) so the SQLite history and the authentication database are copied in a consistent state. The folder holds `authentication.db`, which stores the users, their password hashes and the roles, and `authentication.db.seeded` next to it. Restoring an old backup brings back the users and roles of that time, including users you removed since and their old passwords, and an empty password of `admin` that was not changed yet. If you set `Authentication:Database` to a file outside the folder, back that file up as well. The container runs as root, so on Linux the files in `./data` belong to root and editing them on the host needs `sudo`. A backup contains the secret keys in `DataProtection-Keys`, so store it as carefully as the installation itself; see [Configuration](configuration.md#data-folder) for how to revoke them.

The documentation in `Files/Docs` belongs to the image. On every start, HomeBlaze deletes the folder and copies the documentation of the running image into it, so keep your own pages outside `Files/Docs`: anything added or changed there is lost on the next start. If that fails, for example because the disk is full, HomeBlaze logs a warning and starts anyway.

To keep `Files/Docs` as it is, set the seed directory to an empty string in the compose file after the first start. The quotes are required, because an entry without a value keeps the image's seed directory:

```yaml
    environment:
      HomeBlaze__SeedDirectory: ""
```

While the setting is empty, a new data folder is not filled with the starting set either.

### Optional services

| Profile | Service | Address |
|---------|---------|---------|
| `seq` | Seq log server, receives logs and traces | http://localhost:5341 |

Seq reads a secret from a `.env` file next to the compose file. Create it once before the first start of the profile:

```bash
echo "SEQ_ADMIN_PASSWORD=$(openssl rand -hex 16)" >> .env
docker compose --profile seq up -d
```

Keep `.env`: Seq creates the user `admin` with `SEQ_ADMIN_PASSWORD` on its first start and asks for a new password at the first login.

HomeBlaze sends its logs and traces to Seq through `ConnectionStrings__seq`. Without the `seq` profile the export fails silently and HomeBlaze keeps working; remove the setting if you never use Seq. See [Monitoring](monitoring.md).

MCP clients connect to `http://<host>:8080/mcp` using the MCP Streamable HTTP transport. The compose file enables MCP. The clients get the roles of the built-in user `mcp`, which is `reader` by default, so they can read but not write; to let them write, change its roles. Without a key, anyone who reaches `/mcp` gets these roles, so create a key for it first, see [Anonymous and MCP](authorization.md#anonymous-and-mcp).

### Security

HomeBlaze requires signing in, see [Authorization](authorization.md). Use HTTPS for anything beyond a trusted network, for example behind a reverse proxy that terminates TLS and supports WebSockets, and tell HomeBlaze which proxy to trust, see [Remote Access](authorization.md#remote-access). The external `/mcp` endpoint is separate from signing in and unauthenticated until a key is created for the user `mcp`. The container runs as root so it can write the data folder and access GPIO devices.

### Upgrading to sign-in

A version with sign-in changes what a running installation does on the first start:

- **Signing in is required.** Every page needs a user. Wall tablets, kiosks and bookmarks that opened HomeBlaze without a login show the sign-in dialog after the upgrade: sign in once on the device with "Keep me signed in", or give the built-in user `anonymous` a role on a network you trust, see [the wall tablet recipe](authorization.md#wall-tablet). A visitor who is not signed in sees nothing until then.
- **`admin` is created** with an empty password and the role `everything` on the first start. Set its password at once, see [First start](#first-start).
- **The `ReadOnly` setting of `McpServer` is gone.** The roles of the built-in user `mcp` decide what `/mcp` clients may do, `reader` by default, which keeps them read-only. If you set it to false to allow writes, in `appsettings` or in your compose file, give `mcp` the role `operator` instead; the setting is ignored, so remove it.
- **Back up the authentication database**, see [Data folder](#data-folder). It is part of the data folder by default.
- **Downgrading** to a version from before sign-in removes sign-in entirely: every page and `/mcp` are open again to anyone who reaches the server, as they were before. Going back between versions with sign-in after a database migration is not supported and may fail. Restore the backup of the data folder taken before the upgrade, or run the newer version again.
- **Configuration is administration.** People change configuration only as full administrators; an `operator` reads, writes state and runs operations. Agents create no Markdown files, and a person chats with an agent only when they hold its roles, see [Authorization](authorization.md#who-may-chat-with-an-agent).
- **Forwarded headers** from a reverse proxy are applied only for the proxies you list in `Authentication:ForwardedHeadersKnownNetworks` or `Authentication:ForwardedHeadersKnownProxies`.

### Hardware and discovery

- Raspberry Pi 3 and 4 GPIO: uncomment the `/dev/gpiomem` device in the compose file. GPIO on Raspberry Pi 5 is not supported in the container yet: Pi 5 exposes `/dev/gpiochip*` instead of `/dev/gpiomem` and needs the `libgpiod` library, which the image does not include. See [GPIO](../devices/Gpio.md#linux-dependencies) for the library requirement.
- The Hue bridge has no IP address setting. HomeBlaze discovers it by trying the Philips cloud discovery endpoint, mDNS, SSDP and a local network scan, in that order. In Docker's default network only the cloud endpoint works, and Philips rate-limits it. For reliable discovery, run HomeBlaze with `network_mode: host` on Linux instead.
- Sonos: in Docker's default network, multicast discovery does not find the speakers and the speakers cannot reach the container's own address to deliver events. Set `SeedHost` to a speaker IP and `EventCallbackHost` to the host IP, and publish the event port (`"6329:6329"`), or use host networking. See [Sonos](../devices/Sonos.md).
- Host networking bypasses the compose service names: remove the `ports` section, and set `ConnectionStrings__seq` to `http://localhost:5341` (Seq is published on the host at port 5341). HomeBlaze then listens directly on host port 8080; if that port is taken, for example by another HomeBlaze instance, set `ASPNETCORE_HTTP_PORTS` to a free port such as `"8090"`.

## From source

### Prerequisites

- .NET 10 SDK
- A modern web browser

### Steps

1. Clone the repository
2. Run `dotnet run --project src/HomeBlaze/HomeBlaze`
3. Open http://localhost:5192

HomeBlaze then uses the development data folder `src/HomeBlaze/HomeBlaze/Data`, which contains demo devices, sample plugins and this documentation. To run HomeBlaze together with Seq, see [Aspire](../development/aspire.md).
