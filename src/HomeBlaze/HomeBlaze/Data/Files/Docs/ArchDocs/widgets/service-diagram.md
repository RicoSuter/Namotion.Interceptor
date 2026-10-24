---
title: ServiceDiagram
navTitle: ServiceDiagram
status: Planned
---

# ServiceDiagram [Planned]

Render logical [Services](../subjects/service.md) and their supported semantic interactions, using Service-specific summaries and layout rather than process-level detail. The widget is independent of documentation format.

## Configuration properties [Proposed schema]

Keys use PascalCase for Markdown body blocks. These are proposed view properties, not a new domain subject or implemented API.

| Property | Type | Default and meaning |
|---|---|---|
| `Path` | Subject/folder path `string` | Required scope for discovering Services and resolving relative paths. |
| `ServiceSources` | Optional [PathQuery](../shared/path-queries.md) list selecting [Service](../subjects/service.md) | Omitted: select Services in the scope and owned descendants. Empty: select none. Explicit entries select a deduplicated set without including transitive dependencies. |
| `RelationshipKinds` | Optional `string` list | Omitted: display supported semantic relationship kinds between selected participants. Empty: display no edges. Values filter the [relationship kind](../shared/metadata-and-relationships.md). |

## Markdown example [Proposed]

````markdown
:: Namotion.ArchDocs.ServiceDiagram Path=/catalog
::
````

## Behavior and limits [Planned]

Reuse the [common diagram behavior](architecture-diagram.md). Show each Service identity once. When aggregating component interactions into Service edges, retain original endpoints and provenance; overlapping membership must not imply exclusive ownership or fabricated calls. A selected Service's source-code references do not establish runtime interactions. Ordinary selection never silently adds neighboring Services.

Exact node presentation, renderer, aggregation controls, layout persistence, and interaction remain design choices. Manual view state belongs to the view and typed semantic connections remain in the model. Shared node summaries can also be used in a manually arranged canvas.

## Implementation readiness

**Delivery:** Planned extension using existing component registration and explicit Markdown embeds.

**Prerequisites:** Service selection, typed relationship readers, shared summaries, and a renderer. Automatic context panes and link overlays require the separate page extension.

[General widgets](overview.md) · [ContextDiagram](context-diagram.md) · [ComponentDiagram](component-diagram.md)
