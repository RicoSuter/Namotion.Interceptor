---
title: Contract Views
navTitle: Contract Views
status: Planned
---

# Contract Views [Planned]

Explore public software surfaces with generic fallback and format-specific detail.

## Implementation readiness

**Delivery:** Can be implemented through existing subject and component extension points. The ArchDocs feature itself is not implemented.

**Prerequisites:** Contract types, source references, optional parsed results, and registered presentation components.

## Behavior [Planned]

Common presentation shows source, version where supplied, provenance, diagnostics, and the referencing subjects. Specialized views can show OpenAPI operations/schemas, protobuf services/messages, or C# public types/members. Unsupported formats retain useful source/document access.

## Scope [Proposed]

Parsing support, comparison, and compatibility assessment are independent capabilities. No generic viewer should claim these exist merely because the subtype is registered. Explicit embeds and standalone pages can precede ordinary-link previews and automatic page contributions.

## Related specifications

[Overview and status legend](../overview.md) · [Platform readiness](../implementation-readiness.md)
