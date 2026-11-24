---
title: Installation Guide
navTitle: Installation
---

# Installation

HomeBlaze runs as a container (recommended for a permanent installation) or from source (for development).

## Docker

HomeBlaze is published as a container image for amd64 and arm64 at `ghcr.io/ricosuter/homeblaze`, so it runs on a Raspberry Pi 3, 4, 5 or Zero 2 W with a 64-bit OS. A 32-bit OS cannot run it, and neither can the original Raspberry Pi Zero W, because .NET does not support its ARMv6 CPU. See [Raspberry Pi](#raspberry-pi).

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

HomeBlaze is then available at http://localhost:8080 and opens on its welcome page. The left side of `"8080:8080"` in the `ports` section of the compose file sets the host port, for example `"8090:8080"` serves HomeBlaze on port 8090. To upgrade, run `docker compose pull` and `docker compose up -d`, or install updates from the UI with the [updater](#updater).

### Data folder

Everything HomeBlaze keeps lives in the folder mounted at `/data` (`./data` in the compose file). See [Configuration](configuration.md#data-folder) for the folder layout and the settings that control it.

On the first start, when `Root.json` does not exist, HomeBlaze copies a starting set into the folder: a welcome page, a help page, this documentation, an empty plugin list, a SQLite history store that records to `History/Sqlite`, a `Host system` subject in `Services/HostSystem.json` and an `Installation` subject in `Services/Installation.json` (see [Version and Updates](updates.md)). Existing files are never overwritten, except for the documentation described below. The demo devices and sample plugins are only available when running from source.

Back up HomeBlaze by copying this folder. Stop HomeBlaze first (`docker compose stop`) so the SQLite history is copied in a consistent state. The container runs as root, so on Linux the files in `./data` belong to root and editing them on the host needs `sudo`. A copy of the whole folder contains the secret keys in `DataProtection-Keys` and the passwords and API keys in `Secrets.json`, so store it as carefully as the installation itself; see [Configuration](configuration.md#data-folder) for how to revoke the keys. A backup of only `Files/`, `History/` and `AutomationState/` contains no secrets: after restoring it, enter the secrets again in the edit dialogs.

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
| `updater` | Installs updates from the UI, see [Updater](#updater) | none |

Seq reads a secret from a `.env` file next to the compose file. Create it once before the first start of the profile:

```bash
echo "SEQ_ADMIN_PASSWORD=$(openssl rand -hex 16)" >> .env
docker compose --profile seq up -d
```

With the `updater` profile in `.env`, list both profiles instead, see [Updater](#updater).

Keep `.env`: Seq creates the user `admin` with `SEQ_ADMIN_PASSWORD` on its first start and asks for a new password at the first login.

HomeBlaze sends its logs and traces to Seq through `ConnectionStrings__seq`. Without the `seq` profile the export fails silently and HomeBlaze keeps working; remove the setting if you never use Seq. See [Monitoring](monitoring.md).

### Updater

The updater lets the `Install update` operation of the Installation subject pull and restart HomeBlaze, so an update needs no shell on the host. Turn it on once in the folder of the compose file:

```bash
printf "\nCOMPOSE_DIR='%s'\nCOMPOSE_PROFILES=updater\n" "$PWD" >> .env
chmod 600 .env
docker compose up -d
```

`COMPOSE_DIR` is the absolute path of this folder. The updater mounts it at the same path, read-only except for `data/`, so compose finds `./data` on the host. Update it when the folder moves. Enabling the profile without `COMPOSE_DIR` makes compose refuse to start the updater because of a missing path. `COMPOSE_DIR` must be the folder that contains the compose file: a wrong path that exists makes the updater watch another data folder, and HomeBlaze then shows the updater as not available.

`COMPOSE_PROFILES` keeps the profile on for every later `docker compose up -d`. To use Seq as well, list both profiles, `COMPOSE_PROFILES=updater,seq`, and run plain `docker compose up -d`, since `--profile` replaces the list in `.env`. Without these lines nothing changes.

The updater holds the Docker socket, which gives it root access to the host. It can read the compose folder, including `.env`, and writes only `data/`. It runs only Docker's official `docker:cli` image and the script in the compose file, and listens on no port. Keep the compose folder dedicated to HomeBlaze, never a home directory. Anything that can write the data folder can request an update.

On hosts that enforce SELinux, for example Fedora, RHEL and Podman machines, the updater cannot reach the socket. Add a `docker-compose.override.yml` next to the compose file:

```yaml
services:
  updater:
    security_opt:
      - label=disable
```

To turn the updater off, remove the two lines from `.env` and run `docker compose --profile updater rm -sf updater`.

An install keeps the previous image on the host; `docker image prune` removes unused ones.

The compose of the updater may compute the configuration of the container differently from the compose on the host. The first install after running `docker compose up -d` on the host can therefore recreate HomeBlaze although no newer image exists; later installs only recreate it for a new image or a changed `homeblaze` service.

MCP clients connect to `http://<host>:8080/mcp` using the MCP Streamable HTTP transport. The compose file enables MCP read-only; set `McpServer__ReadOnly` to `"false"` to allow writes.

### Security

HomeBlaze has no login. Keep it on a trusted network or put it behind a reverse proxy that authenticates users and supports WebSockets. The container runs as root so it can write the data folder and access GPIO devices.

### Raspberry Pi

On Raspberry Pi OS based on Debian 13 (trixie), 64-bit, install Docker and Compose from the Debian packages:

```bash
sudo apt install docker.io docker-compose
```

This gave Docker 26.1 and Docker Compose 2.26, which runs as `docker compose`. The commands in this documentation need `sudo` in front of them, or add your user to the `docker` group with `sudo usermod -aG docker $USER` and log out and in again. Group membership is root-equivalent on the host, like the Docker socket of the [updater](#updater).

Measured on a Raspberry Pi Zero 2 W with 416 MB usable RAM, running HomeBlaze, the updater and Docker:

- HomeBlaze used about 160 MB resident memory, and at least 140 MB stayed available, also during an install.
- Pulling the 395 MB image over 2.4 GHz WLAN took about 1.5 to 2 minutes. The UI was slow meanwhile, with requests often taking more than 2 seconds, because the device was busy extracting the image.
- While the container was recreated, HomeBlaze was unreachable for about 40 seconds.

`docker stats` shows `0B` memory on Raspberry Pi OS because its kernel command line contains `cgroup_disable=memory`. To see the numbers, add `cgroup_enable=memory` to the end of the single line in `/boot/firmware/cmdline.txt` and reboot. Nothing in HomeBlaze depends on it.

Optional: WLAN power saving is on by default, which showed as an idle link rate of 1 Mbit/s, latency spikes up to about 65 ms and a few one-minute disconnects in a week. For a Pi used as a server, turn it off with a NetworkManager config file, which survives the regeneration of netplan connections on boot:

```bash
printf '[connection]\nwifi.powersave = 2\n' | sudo tee /etc/NetworkManager/conf.d/wifi-powersave-off.conf
sudo systemctl restart NetworkManager
```

### Hardware and discovery

- Raspberry Pi 3, 4, 400, Zero 2 W and Compute Module 3 and 4 GPIO: uncomment the `/dev/gpiomem` device and the two `/sys` volumes in the compose file, see [GPIO](../devices/Gpio.md#docker). GPIO on Raspberry Pi 5 is not supported in the container yet: Pi 5 exposes `/dev/gpiochip*` instead of `/dev/gpiomem` and needs the `libgpiod` library, which the image does not include. See [GPIO](../devices/Gpio.md#linux-dependencies) for the library requirement.
- The Hue bridge has no IP address setting. HomeBlaze discovers it by trying the Philips cloud discovery endpoint, mDNS, SSDP and a local network scan, in that order. In Docker's default network only the cloud endpoint works, and Philips rate-limits it. For reliable discovery, run HomeBlaze with `network_mode: host` on Linux instead.
- Sonos: in Docker's default network, multicast discovery does not find the speakers and the speakers cannot reach the container's own address to deliver events. Set `SeedHost` to a speaker IP and `EventCallbackHost` to the host IP, and publish the event port (`"6329:6329"`), or use host networking. See [Sonos](../devices/Sonos.md).
- The Discover page of the create dialog finds most devices by multicast and broadcast, which do not reach a container in Docker's default network, so it needs host networking.
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
