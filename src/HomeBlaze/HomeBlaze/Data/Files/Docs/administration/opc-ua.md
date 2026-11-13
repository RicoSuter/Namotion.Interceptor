---
title: OPC UA
navTitle: OPC UA
---

# OPC UA

The **OPC UA server** subject exposes a part of the subject tree to OPC UA clients, and the **OPC UA client** subject mirrors the nodes of an external OPC UA server under its `Root`. Both build on the OPC UA connector of Namotion.Interceptor, whose [client](https://github.com/RicoSuter/Namotion.Interceptor/blob/master/docs/connectors-opcua-client.md) and [server](https://github.com/RicoSuter/Namotion.Interceptor/blob/master/docs/connectors-opcua-server.md) documentation covers the details.

## OPC UA Client

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Name` | string | "" | Display name |
| `ServerUrl` | string | "" | Server endpoint URL, for example `opc.tcp://plc:4840` |
| `RootPath` | string | null | Folder under Objects to start browsing from, with `/` as the delimiter, for example `Machines/MyMachine` |
| `Username`, `Password` | string, string (secret) | null | Credentials; anonymous when either is empty |
| `AcceptUntrustedServerCertificates` | bool | false | Accept any server certificate, see [Trusting a server](#trusting-a-server) |
| `SamplingInterval` | int | null | Default sampling interval in milliseconds; null uses the server default, 0 reports every change immediately |
| `IsEnabled` | bool | true | Start with HomeBlaze; **Start** and **Stop** set it |

The client prefers an unsecured endpoint and uses a secured one only when the server offers nothing else.

| State | Description |
|-------|-------------|
| `Status`, `StatusMessage` | Service status. While the client runs but is not connected, `StatusMessage` shows the last connection error, such as a rejected server certificate with the folders to fix it; it is cleared once the client connects |
| `IsConnected` | Whether the client is connected; empty while it is not running |
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
| `CleanCertificateStore` | bool | false | Create a new server certificate on every start, see [Server certificate](#server-certificate) |
| `BufferTimeMs` | int | 8 | Time window in milliseconds to batch changes before they are published |
| `IsEnabled` | bool | true | Start with HomeBlaze; **Start** and **Stop** set it |

| State | Description |
|-------|-------------|
| `Status`, `StatusMessage` | Service status and the error when the server could not be set up, such as a path that does not resolve. A failure while the server starts, such as a rejected server certificate, is retried and written to the log |
| `IncomingChangesPerSecond`, `OutgoingChangesPerSecond`, `ActiveSessionCount` | Statistics |

## Certificates

Each role has its own certificate stores in the data folder, `OpcUa/Client/Pki` and `OpcUa/Server/Pki`, so a certificate trusted by the client is not trusted by the server. Each holds `own` (the subject's certificate and private key), `trusted`, `issuer` and `rejected`, laid out as described in [Client Certificate](https://github.com/RicoSuter/Namotion.Interceptor/blob/master/docs/connectors-opcua-client.md#client-certificate).

### Trusting a server

The client checks the server certificate on secured connections, before it sends a username and password over an unsecured one, and for an anonymous login when the server requires a security policy for it. A certificate it does not trust is written to `OpcUa/Client/Pki/rejected/certs`, and `StatusMessage` shows the reason. Move the file to `OpcUa/Client/Pki/trusted/certs`; the next connection attempt succeeds. Alternatively, switch on **Accept Untrusted Server Certificates**, which accepts any server certificate and is only appropriate on a trusted network.

### Trusting a client

The server accepts a secured connection only from a client whose certificate it trusts. A rejected client certificate is written to `OpcUa/Server/Pki/rejected/certs`; move it to `OpcUa/Server/Pki/trusted/certs`. Clients that connect without security need no trust.

### Server certificate

The server creates a self-signed certificate on its first start and keeps it across restarts, so clients trust it once. On start it logs a warning within 30 days of expiry and replaces an expired self-signed certificate, after which clients have to trust the new one. With **Clean Certificate Store** it creates a new certificate on every start instead.

The certificates of both roles contain the host name. The [docker-compose.yml](https://github.com/RicoSuter/Namotion.Interceptor/blob/master/src/HomeBlaze/docker-compose.yml) sets `hostname: homeblaze`, so they stay valid when the container is recreated; keep a fixed host name when running the image another way. Otherwise, after a change of the host name, the server fails to start and secured client connections fail until the certificate is deleted from `own/certs` and `own/private`, after which a new one is created and has to be trusted again.
