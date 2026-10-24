---
title: Interface
navTitle: Interface
status: Planned
---

# Interface [Planned]

Describes a logical exposed capability and its access mechanism or protocol.

## Implementation readiness

**Delivery:** Can be implemented through existing subject and component extension points. The ArchDocs feature itself is not implemented.

**Prerequisites:** Contract references, subject-path resolution, and exposure relationships; contract parsing is optional.

## Data [Planned]

| Authored or discovered | Related subjects |
|---|---|
| Name, description, protocol/technology, provenance where discovered | Exposing Service/Host/Resource, zero or more Contracts, environment Endpoints |

An Interface is independent of a concrete address and can be documented with implementation details unknown. A database protocol or message interface is valid; HTTP is not assumed. A CodeProject can reference a programming API Contract directly without creating an Interface.

## Open schema choices [Proposed]

Choose the exposure relationship and its canonical authored location, shared identity, and cardinalities. Do not duplicate the same Interface to show it from different subjects. Exact field names and ownership remain open.

## Related specifications

[Overview and status legend](../overview.md) · [Platform readiness](../implementation-readiness.md)
