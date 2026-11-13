---
title: OPC UA
navTitle: OPC UA
---

# OPC UA

The **OPC UA server** subject exposes a part of the subject tree to OPC UA clients, and the **OPC UA client** subject mirrors the nodes of an external OPC UA server under its `Root`. Both build on the Namotion.Interceptor OPC UA [client](https://github.com/RicoSuter/Namotion.Interceptor/blob/master/docs/connectors-opcua-client.md) and [server](https://github.com/RicoSuter/Namotion.Interceptor/blob/master/docs/connectors-opcua-server.md) connectors, whose documentation covers the details.

## OPC UA Client

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Name` | string | "" | Display name |
| `ServerUrl` | string | "" | Server endpoint URL, for example `opc.tcp://plc:4840` |
| `RootPath` | string | null | Folder under Objects to start browsing from, with `/` as the delimiter, for example `Machines/MyMachine` |
| `Username`, `Password` | string, string (secret) | null | Credentials; anonymous when either is empty |
| `AcceptUntrustedServerCertificates` | bool | false | Accept any server certificate, see [Certificates](#certificates) |
| `SamplingInterval` | int | null | Default sampling interval in milliseconds; null uses the server default, 0 reports every change immediately |
| `IsEnabled` | bool | true | Start with HomeBlaze; **Start** and **Stop** set it |

The client prefers an unsecured endpoint and uses a secured one only when the server offers nothing else.

| State | Description |
|-------|-------------|
| `Status`, `StatusMessage` | Service status. While the client runs but is not connected, `StatusMessage` shows the last connection error, such as an untrusted server certificate |
| `IsConnected` | Whether the client is connected; empty while it is not running. The green icon requires it |
| `IncomingChangesPerSecond`, `OutgoingChangesPerSecond`, `MonitoredItemCount`, `PollingItemCount`, `PendingWriteCount`, `TotalReconnections` | Statistics |
| `Root` | The discovered nodes, recreated on each connection |

## OPC UA Server

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Name` | string | "" | Display name |
| `Path` | string | "" | [Path](paths.md) of the subject to expose, for example `/` or `/Children[demo]` |
| `ApplicationName` | string | "Namotion.Interceptor.Server" | OPC UA application name |
| `NamespaceUri` | string | "http://namotion.com/Interceptor/" | Namespace URI of the exposed nodes |
| `RootName` | string | null | Folder under Objects for the exposed nodes |
| `BaseAddress` | string | "opc.tcp://localhost:4840/" | Endpoint the server listens on |
| `CleanCertificateStore` | bool | false | Create a new server certificate on every start, which clients then have to trust again |
| `BufferTimeMs` | int | 8 | Time window in milliseconds to batch changes before they are published |
| `IsEnabled` | bool | true | Start with HomeBlaze; **Start** and **Stop** set it |

| State | Description |
|-------|-------------|
| `Status`, `StatusMessage` | Service status, and the error when the server could not be set up, such as a path that does not resolve. A failed start, such as a server certificate that cannot be loaded, is retried, and `StatusMessage` shows its error until the server accepts connections |
| `IsAcceptingConnections` | Whether the server accepts client connections; empty while it is not running or has not reported yet. `IsServerRunning` and the green icon require it |
| `IncomingChangesPerSecond`, `OutgoingChangesPerSecond`, `ActiveSessionCount` | Statistics |

## Certificates

The client and the server keep separate certificate stores in the data folder, `OpcUa/Client/Pki` and `OpcUa/Server/Pki`, so trusting a certificate for one does not trust it for the other. Their layout and expiry rules are described in the connector's [Transport Security](https://github.com/RicoSuter/Namotion.Interceptor/blob/master/docs/connectors-opcua-client.md#transport-security).

- **Trust a server**: the client checks the server certificate on a secured endpoint, on a username login, and on an anonymous login whose token policy requires it. An untrusted certificate is written to `OpcUa/Client/Pki/rejected/certs` and named in `StatusMessage`; move it to `OpcUa/Client/Pki/trusted/certs`. Alternatively switch on **Accept Untrusted Server Certificates**, only on a trusted network.
- **Trust a client**: for a secured connection, move the client's certificate from `OpcUa/Server/Pki/rejected/certs` to `OpcUa/Server/Pki/trusted/certs`.
- **Server certificate**: created on the first start and kept across restarts. An expired one is replaced on start and has to be trusted again.
- **Host name**: both certificates contain the host name. The [docker-compose.yml](https://github.com/RicoSuter/Namotion.Interceptor/blob/master/src/HomeBlaze/docker-compose.yml) sets `hostname: homeblaze` so they stay valid when the container is recreated; keep a fixed host name when running the image another way. After a host name change, the server fails to start and secured client connections fail until the old certificate is deleted from `own/certs` and `own/private`; the new one has to be trusted again.
