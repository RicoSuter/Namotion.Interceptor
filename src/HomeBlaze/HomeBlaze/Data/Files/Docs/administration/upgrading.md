---
title: Upgrading
navTitle: Upgrading
---

# Upgrading

Procedures for upgrading HomeBlaze instances, including configuration migration and backward compatibility.

See [Upgrade and Migration Design](../architecture/design/upgrade-and-migration.md) for the architectural design.

## Instance data folder

HomeBlaze now keeps everything in one [data folder](configuration.md#data-folder): the folder that contains the root configuration file. Instances created before this change move their files as follows.

- **Root configuration**: `root.json` in the working directory becomes `Data/Root.json`. The file name is case-sensitive on Linux, so rename it to `Root.json`. Use `HomeBlaze:RootConfigFile` to keep it somewhere else.
- **Subject files**: the subject files move from `Data/` to `Data/Files/`, and the root configuration uses `"connectionString": "Files"`.
- **Relative paths**: relative paths in the configuration, such as the storage connection string, now resolve against the data folder instead of the working directory.
- **Plugins**: `Plugins.json` moves from `Data/Plugins.json` in the application directory to `Files/Plugins.json` in the data folder. See [Configuration](configuration.md#pluginconfigurationpath) for how relative feed URLs and the cache directory inside it resolve.
- **SQLite history**: the history store now writes to `History/Sqlite` in the data folder. Before, it wrote to the `HomeBlaze/History` folder in the local application data folder: `~/.local/share/HomeBlaze/History` on Linux and `%LOCALAPPDATA%\HomeBlaze\History` on Windows. A relative `databasePath` now resolves against `History` in the data folder. To keep the old history, set `databasePath` of the SQLite history store to the absolute path of the old folder, or move the database files into `History/Sqlite`.
- **OPC UA certificates**: the certificate stores move from `pki` in the working directory to `OpcUa/Server/Pki` and `OpcUa/Client/Pki` in the data folder, and HomeBlaze creates new certificates there. OPC UA servers that HomeBlaze connects to must trust the new HomeBlaze client certificate again, and OPC UA clients that connect to the HomeBlaze OPC UA server must trust its new server certificate. If the HomeBlaze OPC UA server does not accept untrusted certificates automatically, move the client certificates you trusted manually from `pki/trusted` to `OpcUa/Server/Pki/trusted`.
