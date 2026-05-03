---
title: Security
navTitle: Security
status: In Progress
---

# Security Design

## Overview

Security in HomeBlaze covers graph-level authorization (who can read/write which properties and invoke which operations), inter-node authentication, and MCP access control. Graph authorization is in progress ([PR #137](https://github.com/RicoSuter/Namotion.Interceptor/pull/137)); other aspects are planned.

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

## Inter-Node Authentication [Planned]

Planned. WebSocket connections between instances (satellite-to-central, primary-to-standby) need authentication to prevent unauthorized nodes from joining the topology.

Likely approach: mutual TLS or token-based authentication on WebSocket connections.

## MCP Access Control [Planned]

Planned. When an external agent connects via MCP, the authorization context determines which subjects, properties, and operations are accessible. The same graph authorization model applies — the MCP session carries a role/identity.

## Key Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Authorization model | Role-based, attribute-driven, three levels | Matches the subject model; overrides flow from broad to specific |
| Entity categories | State, Configuration, Query, Operation | Different sensitivity levels need different defaults |
| Inter-node auth | Deferred | Focus on graph authorization first |

## Open Questions

- Inter-node authentication mechanism (TLS, tokens, certificates)
- MCP session identity model
- Authorization for cross-instance operation proxying
- Audit integration — logging authorization decisions (see [Audit](audit.md))
