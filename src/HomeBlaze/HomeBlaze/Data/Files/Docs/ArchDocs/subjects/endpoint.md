---
title: Endpoint
navTitle: Endpoint
status: Planned
---

# Endpoint [Planned]

Supplies environment-specific access details for a logical Interface.

## Implementation readiness

**Delivery:** Can be implemented through existing subject and component extension points. The ArchDocs feature itself is not implemented.

**Prerequisites:** Interface and Environment references; an optional Deployment reference and its bindings.

## Data [Planned]

| References | Access details |
|---|---|
| Interface, Environment, optional Deployment | Address or protocol-appropriate connection binding; exact fields remain open |

The same Interface can have different Endpoints in different environments. Endpoint describes access; Contract describes the public surface. An address is not evidence of successful access or health. Keep intended and observed data distinguishable.

## Open schema choices [Proposed]

Settle defining owner, persistence, generated-versus-authored lifecycle, protocol-specific bindings, and multiple endpoints in one environment. This page does not assume an HTTP URL is sufficient for every protocol.

## Related specifications

[Overview and status legend](../overview.md) · [Platform readiness](../implementation-readiness.md)
