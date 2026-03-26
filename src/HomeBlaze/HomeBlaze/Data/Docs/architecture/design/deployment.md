---
title: Deployment
navTitle: Deployment
---

# Deployment Design [Planned]

## Overview

HomeBlaze is a single .NET binary whose role is determined by configuration, not separate builds. This document describes network requirements, deployment patterns, and operational considerations. For deployment stages (single instance through multi-instance HA), see the [Scalability and Deployment](../overview.md) section of the architecture overview.

## Network Communication

HomeBlaze instances communicate over standard protocols. The specific ports are configurable.

| Communication | Protocol | Default | Direction |
|---------------|----------|---------|-----------|
| Satellite → Central | WebSocket | Configurable | Outbound from satellite |
| Primary → Standby | WebSocket | Configurable | Inbound to primary |
| Operator UI | HTTP/HTTPS (Blazor/SignalR) | Configurable | Inbound |
| MCP server | HTTP/HTTPS or stdio | Configurable | Inbound |
| OPC UA server | OPC UA TCP | 4840 (OPC UA default) | Inbound |
| OPC UA client | OPC UA TCP | — | Outbound to device |
| MQTT client | MQTT TCP | 1883 / 8883 (TLS) | Outbound to broker |
| MQTT server | MQTT TCP | Configurable | Inbound |

### Inter-Node Communication

All inter-node communication (satellite↔central, primary↔standby) uses the WebSocket connector. This is a single TCP connection per link, carrying SubjectUpdate messages for state sync and (planned) operation proxying.

- WebSocket connections are initiated by the downstream node (satellite connects to central, standby connects to primary)
- Reconnection is automatic with backoff
- TLS should be used for production deployments (see [Security](security.md) — inter-node authentication is planned)

### Service Discovery

How a satellite finds central (or standby finds primary) is a configuration concern — the connection URL is specified in the satellite/standby configuration. HomeBlaze does not include built-in service discovery.

For environments that need dynamic discovery, external mechanisms can be used (DNS, Consul, Kubernetes services, etc.) and the connection URL can reference these.

## Deployment Patterns

HomeBlaze does not prescribe a deployment model. The same binary runs on bare metal, VMs, containers, or edge devices.

### Bare Metal / VM

Simplest deployment — run the .NET process directly. Suitable for single-instance, small HA pairs, or edge/satellite nodes on dedicated hardware (e.g., Raspberry Pi, industrial PCs).

### Containers

Each instance runs as a container. For HA pairs, a StatefulSet with 2 replicas maps naturally — pod ordinal can determine role (0 = primary, 1 = standby). Services route traffic to the active pod.

### Edge Devices

Satellites can run on low-power edge hardware close to field devices. The single binary and .NET's cross-platform support make this possible without special builds. Storage backend should be local filesystem for edge nodes.

## Scaling the UI / API Layer

When a single instance cannot handle the HTTP load from operators and AI agents, multiple UNS instances can sync bidirectionally — the same WebSocket mechanism used for satellite↔central sync. Each instance holds full state, serves UI and MCP traffic, and propagates writes to its peers. Source tagging prevents feedback loops.

```
                    ┌──────────────────┐
                    │  HTTP Load       │
                    │  Balancer        │
                    └──┬───────────┬───┘
                       │           │
              ┌────────▼───┐  ┌────▼─────────┐
              │ UNS        │  │ UNS          │
              │ Instance 1 │  │ Instance 2   │
              │ (UI/MCP)   │  │ (UI/MCP)     │
              └─────┬──────┘  └──────┬───────┘
                    │    WebSocket   │
                    │◄──────────────►│
                    │   (bidir sync) │
                    └────────────────┘
```

- Uses the existing WebSocket connector — each instance connects to the other(s) as both client and server
- Writes on any instance propagate to all peers via the normal change pipeline without feedback loops
- Conflict resolution is last-writer-wins (existing SubjectUpdate semantics)
- Operations use different semantics — see [Methods and Operations](methods.md)
- Each instance can independently receive satellite connections
- Each instance can optionally have its own history sink

This is the same pattern as satellite↔central, applied peer-to-peer. No new infrastructure or protocol changes required.

See [Scalability](scalability.md) for other scaling approaches (selective sync, hierarchical UNS, federated query).

## Reverse Proxy Considerations

When running behind a reverse proxy (nginx, Caddy, cloud load balancer), ensure:
- WebSocket upgrade is supported (required for Blazor SignalR and inter-node sync)
- Request timeouts are long enough for WebSocket connections (idle connections should not be terminated)
- If TLS is terminated at the proxy, internal communication can use plain HTTP/WS

## Open Questions

- Should HomeBlaze provide a health check endpoint for load balancer probes?
- Container image publishing and versioning strategy
- Edge device provisioning and remote configuration updates
- Monitoring integration guidance (Prometheus scrape endpoint, OTLP push, etc.)
