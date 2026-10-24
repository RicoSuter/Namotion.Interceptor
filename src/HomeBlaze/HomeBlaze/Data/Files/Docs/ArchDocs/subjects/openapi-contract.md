---
title: OpenApiContract
navTitle: OpenApiContract
status: Planned
---

# OpenApiContract [Planned]

A Contract specialization for an OpenAPI public API description.

## Implementation readiness

**Delivery:** Can be implemented through existing subject and component extension points. The ArchDocs feature itself is not implemented.

**Prerequisites:** Contract; an optional OpenAPI analyzer and specialized presentation.

## Inheritance [Planned]

OpenApiContract derives from Contract and retains its source, identity, provenance, and optional version semantics. Specialized data can describe operations, parameters, requests/responses, and schemas. It can be referenced by an Interface or an appropriate CodeProject.

## Scope and open choices [Proposed]

Supported OpenAPI revisions, external-reference policy, parsed model shape, parser dependency, and version comparison are not selected. A source document can be recorded before parsing is implemented. No network polling or endpoint probing is implied.

## UI [Planned]

The specialized widget/detail view extends the [Contract presentation](contract.md#ui-planned) with supported operations, parameters, request/response details, and schemas. Offer source-document access and parser diagnostics, retaining a useful common summary when parsing is unavailable.

Operation/schema navigation is specific to this subject and lives with this specification. Supported revisions, parsing, comparison, and compatibility analysis remain separate decisions; do not imply them from the subtype alone.

## Related specifications

[Overview and status legend](../overview.md) · [Platform readiness](../implementation-readiness.md)
