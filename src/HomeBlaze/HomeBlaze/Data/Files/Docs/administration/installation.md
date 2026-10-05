---
title: Installation Guide
navTitle: Installation
position: 1
---

# Installation

HomeBlaze runs as a container (recommended for a permanent installation) or from source (for development).

## Docker

HomeBlaze is published as a container image for amd64 and arm64 (Raspberry Pi 4 and 5) at `ghcr.io/ricosuter/homeblaze`.

| Tag | Content |
|-----|---------|
| `latest`, `X.Y`, `X.Y.Z` | Releases |
| `edge`, `sha-<commit>` | Every change on master |

Pre-releases are only published with their exact version tag. Until the first release is published, use the `edge` tag by setting `image: ghcr.io/ricosuter/homeblaze:edge` in the compose file. Images are published from the upstream repository only; forks do not publish. GitHub Container Registry makes a new package private on its first push, so the maintainer makes it public once in the package settings.

Copy [docker-compose.yml](https://github.com/RicoSuter/Namotion.Interceptor/blob/master/src/HomeBlaze/docker-compose.yml) into an empty folder, adjust the time zone (`TZ`, and `GENERIC_TIMEZONE` for n8n), and start it:

```bash
docker compose up -d
```

HomeBlaze is then available at http://localhost:8080 and opens on its welcome page. To upgrade, run `docker compose pull` and `docker compose up -d`, then read [Upgrading](upgrading.md).

### Data folder

Everything HomeBlaze keeps lives in the folder mounted at `/data` (`./data` in the compose file). See [Configuration](configuration.md#data-folder) for the folder layout and the settings that control it.

On the first start, when `Root.json` does not exist, HomeBlaze copies a starting set into the folder: a welcome page, a help page, this documentation, an empty plugin list and a SQLite history store that records to `History/Sqlite`. Existing files are never overwritten. The demo devices and sample plugins are only available when running from source.

Back up HomeBlaze by copying this folder. Stop HomeBlaze first (`docker compose stop`) so the SQLite history is copied in a consistent state. The container runs as root, so on Linux the files in `./data` belong to root and editing them on the host needs `sudo`.

The documentation in `Files/Docs` is not updated by later image versions. To replace it with the documentation of the running image:

```bash
docker compose exec homeblaze sh -c 'rm -rf /data/Files/Docs && cp -r /app/Seed/Files/Docs /data/Files/Docs'
```

### Optional services

| Profile | Service | Address |
|---------|---------|---------|
| `seq` | Seq log server, receives logs and traces | http://localhost:5341 |
| `n8n` | n8n workflow automation | http://localhost:5678 |

Both services read a secret from a `.env` file next to the compose file. Create it once before the first start of a profile:

```bash
echo "SEQ_ADMIN_PASSWORD=$(openssl rand -hex 16)" >> .env
echo "N8N_ENCRYPTION_KEY=$(openssl rand -hex 32)" >> .env
docker compose --profile seq --profile n8n up -d
```

Keep `.env`: n8n encrypts its stored credentials with this key. Seq creates the user `admin` with `SEQ_ADMIN_PASSWORD` on its first start and asks for a new password at the first login.

HomeBlaze sends its logs and traces to Seq through `ConnectionStrings__seq`. Without the `seq` profile the export fails silently and HomeBlaze keeps working; remove the setting if you never use Seq. See [Monitoring](monitoring.md).

To use HomeBlaze from an n8n AI agent, add an **MCP Client Tool** node with transport **HTTP Streamable** and endpoint `http://homeblaze:8080/mcp`. The compose file enables MCP read-only; set `McpServer__ReadOnly` to `"false"` to allow writes.

The n8n image is pinned to a version because n8n migrates its database when it upgrades. Change the version deliberately and back up the `n8n-data` volume first.

### Security

HomeBlaze has no login. Keep it on a trusted network or put it behind a reverse proxy that authenticates users and supports WebSockets. The container runs as root so it can write the data folder and access GPIO devices.

### Hardware and discovery

- Raspberry Pi 3 and 4 GPIO: uncomment the `/dev/gpiomem` device in the compose file. GPIO on Raspberry Pi 5 is not supported in the container yet: Pi 5 exposes `/dev/gpiochip*` instead of `/dev/gpiomem` and needs the `libgpiod` library, which the image does not include. See [GPIO](../devices/Gpio.md#linux-dependencies) for the library requirement.
- The Hue bridge has no IP address setting. HomeBlaze discovers it by trying the Philips cloud discovery endpoint, mDNS, SSDP and a local network scan, in that order. In Docker's default network only the cloud endpoint works, and Philips rate-limits it. For reliable discovery, run HomeBlaze with `network_mode: host` on Linux instead.
- Host networking bypasses the compose service names: remove the `ports` section, set `ConnectionStrings__seq` to `http://localhost:5341` (Seq is published on the host at port 5341), and reach HomeBlaze from n8n at `http://host.docker.internal:8080/mcp` after adding `extra_hosts: ["host.docker.internal:host-gateway"]` to the n8n service.

## From source

### Prerequisites

- .NET 10 SDK
- A modern web browser

### Steps

1. Clone the repository
2. Run `dotnet run --project src/HomeBlaze/HomeBlaze`
3. Open http://localhost:5192

HomeBlaze then uses the development data folder `src/HomeBlaze/HomeBlaze/Data`, which contains demo devices, sample plugins and this documentation. To run HomeBlaze together with Seq, n8n and an OPC UA simulator, see [Aspire](../development/aspire.md).
