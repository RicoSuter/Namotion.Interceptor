---
title: Security
navTitle: Security
status: In Progress
---

# Security Design

## Overview

Security in HomeBlaze covers graph-level authorization (who can read/write which properties and invoke which operations), the inter-node trust boundary, secrets handling, trust-zone segmentation, and MCP access control. Graph authorization is in progress ([PR #137](https://github.com/RicoSuter/Namotion.Interceptor/pull/137)); the other aspects are planned.

## Graph Authorization [In Progress]

Authorization operates at three levels, from broad to specific:

| Level | Attribute | Scope |
|-------|-----------|-------|
| Subject | `[SubjectAuthorize]` | Default access rules for all properties and operations on a subject |
| Property | `[SubjectPropertyAuthorize]` | Override for a specific property (read/write separately) |
| Method | `[SubjectMethodAuthorize]` | Override for a specific operation or query |

### Entity Categories

Properties and operations are categorized by their nature, each with different default access levels:

| Entity | Examples | Default Read | Default Write/Invoke |
|--------|----------|-------------|---------------------|
| State | Sensor readings, device status | Guest | Operator |
| Configuration | Settings, parameters | User | Supervisor |
| Query | Read-only methods | User | — |
| Operation | State-changing methods | — | Operator |

### Actions

| Action | Applies to |
|--------|-----------|
| Read | Properties |
| Write | Properties |
| Invoke | Methods (operations and queries) |

### Role-Based

Authorization is role-based with OR logic — any matching role grants access. Roles are managed through the Blazor UI (user management, role assignment).

## Inter-Node Trust Boundary [Planned]

WebSocket connections between instances (satellite-to-central, primary-to-standby) need authentication to prevent unauthorized nodes from joining the topology. Likely approach: mutual TLS or token-based authentication on WebSocket connections.

Node authentication proves *node* identity only. It does not prove that a per-request *user* authorization was honored upstream. Because every instance runs the same binary and its role is only configuration, a receiving node must not trust that a peer already authorized a write or an operation.

**Principle:** the node that owns a subject re-validates user-level authorization on every inbound write and operation, using the caller identity propagated with the request. A peer's assertion of a role or identity is never accepted on trust. Control-plane requests (operations, setpoints) are authorized and executed on the owning node (see [Methods](methods.md)). This is the security half of the reflection-plane vs control-plane split in the [Architecture Overview](../overview.md).

## MCP Access Control [Planned]

Planned. When an external agent connects via MCP, the authorization context determines which subjects, properties, and operations are accessible. The same graph authorization model applies: the MCP session carries a role/identity.

## Secrets [Planned]

Device credentials, broker passwords, TLS keys, and LLM provider API keys must not be treated as ordinary `[Configuration]` properties. Configuration is serialized to storage and replicated across instances (a database-backed or proxied subject can carry configuration on the wire), so a secret stored as a plain configuration string would propagate to every peer and to disk in clear form.

Required handling:
- A distinct secret property kind (or marker) that is excluded from wire serialization and cross-instance replication, and redacted in logs, the UI, and MCP output.
- Secret *values* held in an external secret store (environment, key vault, or platform secret manager); the subject holds only a reference, not the value.

Until this exists, secrets should not be placed in synced or proxied subjects.

## Trust Zones [Planned]

The "same binary, config-driven role" model is coherent *within* a single trust zone. Across zones (for example the field/control, supervisory/UNS, and MES/ERP levels of an ISA-95 or IEC 62443 layout), the boundary needs real segmentation: authenticated nodes, re-validated authorization at the boundary, and no implicit trust that a lower zone enforced policy. The same-binary simplicity is kept inside a zone; the hardened, re-validating boundary sits between zones.

## Key Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Authorization model | Role-based, attribute-driven, three levels | Matches the subject model; overrides flow from broad to specific |
| Entity categories | State, Configuration, Query, Operation | Different sensitivity levels need different defaults |
| Inter-node auth | Deferred | Focus on graph authorization first |
| Inter-node trust | Owning node re-validates user authz; node auth proves node identity only | Same binary and config-driven role means a peer cannot be trusted to have authorized a request |
| Secrets | Not ordinary `[Configuration]`; external store plus a non-replicated reference | Configuration is serialized and replicated, so a plain secret would leak to peers and disk |
| Trust zones | Same binary within a zone; hardened re-validating boundary between zones | Cross-zone segmentation for ISA-95 / IEC 62443 layouts |

## Open Questions

- Inter-node authentication mechanism (TLS, tokens, certificates)
- Caller-identity propagation format in the WebSocket/RPC protocol
- MCP session identity model, and whether in-process built-in agents bypass attribute authorization
- Secret property mechanism and choice of external secret store
- Audit integration: logging authorization decisions (see [Audit](audit.md))
