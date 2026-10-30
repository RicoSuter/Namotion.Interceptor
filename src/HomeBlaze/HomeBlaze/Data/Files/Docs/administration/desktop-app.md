---
title: Desktop App
navTitle: Desktop App
---

# Desktop App

HomeBlaze also ships as a native desktop app for Windows, Linux and macOS. It opens HomeBlaze in its own window instead of a browser tab, and it can either connect to a HomeBlaze instance that already runs elsewhere or run HomeBlaze itself on this computer.

---

## Connecting or running locally

Point the app at a HomeBlaze instance that runs elsewhere, by its address, and the window behaves like a browser tab open to that address. Or let the app run HomeBlaze itself: it starts HomeBlaze in the background and opens the window on it. A locally run instance listens only on this computer, so nothing else on the network can reach it, which matters because HomeBlaze itself has no login.

## Links to other sites

The window always stays on HomeBlaze. A link to another site, including one that would open a new tab, opens in your default browser instead, and an email link opens your mail program. Like the settings shortcut below, this needs the HomeBlaze instance shown in the window to support it, so with an older remote instance such a link still opens inside the window, and restarting the app is the way back.

## Choosing a folder

Running HomeBlaze locally needs a folder to keep its data in. The key difference between the two kinds you can choose: a data folder is a complete, self-contained instance you can move or back up as a unit, while a folder of files stays exactly as it is, because HomeBlaze keeps its own database and settings elsewhere.

- **A HomeBlaze data folder.** The folder itself holds `Root.json` and the `Files` folder, exactly as a server installation does, so everything the instance owns lives inside it. Pick an **empty** folder to set up a new instance: it is filled with a welcome page, a help page, this documentation, an empty plugin list, a SQLite history store and a host system subject. No OPC UA server is set up, so a fresh data folder does not open any extra network port. A folder that already holds other files is refused, because setting an instance up in it would write HomeBlaze's own files alongside yours and would replace a `Files/Docs` folder of your own on every start. Open such a folder as a folder of files instead.
- **A folder of files.** Any existing folder, such as a folder of notes or photos, mounted as is. HomeBlaze keeps its database and settings in a private workspace elsewhere, so the folder itself only ever receives the changes you make through HomeBlaze and nothing else. Reopening the same folder later reuses the same private workspace, so its history is kept.

---

## Settings

The app's settings, including which kind of folder to use, live on one screen. Open it with `Ctrl+Comma` (`Cmd+Comma` on macOS) while the window has focus. The shortcut needs the HomeBlaze instance shown in the window to support it, so it is not available when connecting to an older remote instance; start the app with the `--settings` command-line option instead in that case.

---

## Where things are stored

| What | Windows | Linux | macOS |
|---|---|---|---|
| App settings | `%APPDATA%\HomeBlaze` | `~/.config/HomeBlaze` | `~/Library/Application Support/HomeBlaze` |
| Private workspaces | `%LOCALAPPDATA%\HomeBlaze\Workspaces` | `~/.local/share/HomeBlaze/Workspaces` | `~/Library/Application Support/HomeBlaze/Workspaces` |
| Host logs | `%LOCALAPPDATA%\HomeBlaze\Logs` | `~/.local/share/HomeBlaze/Logs` | `~/Library/Application Support/HomeBlaze/Logs` |

---

## Limits

Only one window can have a given data folder open at a time; opening the same folder again in a second window is refused. This check does not see a container instance using the same folder, so do not point the desktop app and a container at the same data folder at the same time.

---

## Related Documentation

- [Installation](installation.md): installing HomeBlaze as a container or from source
- [Configuration](configuration.md#data-folder): the data folder layout a HomeBlaze data folder also uses
