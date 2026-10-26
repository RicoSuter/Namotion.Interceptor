---
title: ContextDiagram
navTitle: ContextDiagram
status: Planned
---

# ContextDiagram [Planned]

Describe a chosen system boundary using existing [Services](../subjects/service.md) and optional [Actors](../subjects/actor.md). Internal and external are properties of this view, including for neighboring systems operated by the same organization.

## Configuration properties [Proposed schema]

Keys use PascalCase for Markdown body blocks. Each selection uses the shared [PathQuery](../shared/path-queries.md) record. These are proposed view properties, not a domain boundary flag.

| Property | Type | Default and meaning |
|---|---|---|
| `Path` | Subject/folder path `string` | Required scope for relative references. It does not itself classify participants. |
| `InternalSources` | PathQuery list selecting [Service](../subjects/service.md), with optional Actor support later | Explicit internal participants. Omitted or empty: none; never infer a boundary from ownership. |
| `ExternalSources` | PathQuery list selecting [Service](../subjects/service.md), with optional Actor support later | Explicit external participants. Omitted or empty: none; do not automatically add transitive neighbors. |
| `RelationshipKinds` | Optional `string` list | Omitted: supported semantic relationships between selected participants. Empty: no edges. Values filter the [relationship kind](../shared/metadata-and-relationships.md). |

## Markdown example [Proposed]

Assume the two paths below identify Service subjects. In this view, Payments is outside the Orders boundary:

````markdown
:: Namotion.ArchDocs.ContextDiagram Path=/catalog
```json
{
  "InternalSources": [ { "Path": "/catalog/Orders" } ],
  "ExternalSources": [ { "Path": "/catalog/Payments" } ]
}
```
::
````

A Commerce context can select both as internal without changing either Service. A subject selected into both sets produces a diagnostic rather than a duplicate node or a silent choice. Empty selections describe an empty view; the widget should explain that no participants were selected.

## Behavior and limits [Planned]

Reuse [common diagram and boundary behavior](architecture-diagram.md#context-boundaries-planned). Keep shared identity, relationship provenance, ordinary navigation, and view-local layout. No mandatory System root, external-Service subtype, or global internal/external field is needed. Follow the shared live Canvas direction for rendering and saved layout while retaining the explicit boundary-selection contract. Actor modeling, exact boundary visuals, and optional neighbor discovery remain open.

## Implementation readiness

**Delivery:** Planned extension using existing component registration and explicit Markdown embeds.

**Prerequisites:** Service queries, typed relationships, shared summaries, and the planned shared Canvas diagram capabilities. Actor support is optional later work; no page-context extension is required for this explicit view.

[General widgets](overview.md) · [ServiceDiagram](service-diagram.md) · [Scenarios](../scenarios/arc42.md)
