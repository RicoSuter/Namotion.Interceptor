---
title: DependencyDiagram
navTitle: DependencyDiagram
status: Planned
---

# DependencyDiagram [Planned]

Show discovered code/build dependencies and optional aggregation into logical groups. This complements the semantic [ServiceDiagram](service-diagram.md): a project reference stays a code dependency when grouped and does not become an API call or message flow.

## Configuration properties [Proposed schema]

| Property | Type | Default and meaning |
|---|---|---|
| `Path` | Subject/folder path `string` | Required graph scope for discovery and relative subject references. |
| `ProjectSources` | Optional [PathQuery](../shared/path-queries.md) list selecting [CodeProject](../subjects/code-project.md) | Omitted: discovered projects in scope. Empty: no projects. |
| `GroupSources` | Optional PathQuery list selecting [Service](../subjects/service.md) | Omitted: no Service grouping. Group by those Services' direct selected projects without changing their memberships. |
| `Groups` | Optional named view-group records | Explicit layer/module labels and project queries; these are view configuration, not new domain subjects or inferred Service definitions. Exact record shape remains open. |

## Markdown example [Proposed]

````markdown
:: Namotion.ArchDocs.DependencyDiagram Path=/Repository
::
````

Existing project membership selectors provide reusable grouping rules. A source-folder or namespace rule is usable only when its predicate and analyzer capability are supported; graph-path wildcards do not silently match raw source paths. Grouping aggregates visible facts without duplicating authored project lists.

## Behavior and limits [Planned]

Render resolved `projectReferences`, unresolved dependencies, source links, provenance, and analysis freshness. Allow reverse-dependency exploration and cycle highlighting. Preserve outside-scope dependencies as external references without adding them to selected membership. A view can use a matrix/table presentation when that makes large graphs easier to read.

Aggregated edges retain the underlying project endpoints and evidence. Overlapping Service/group membership must remain visible and must not imply exclusive ownership or create artificial inter-Service runtime edges. Exact multi-group rendering and aggregation controls remain open. [Architecture checks](../rules/architecture-checks.md) can annotate known violations using the same snapshot; incomplete checks remain distinguishable from passes.

Reuse [common diagram infrastructure](architecture-diagram.md). This view supports building-block and layer documentation in arc42 and other styles. Basic project-file analysis supplies literal references only; it does not establish all effective build dependencies, type usage, or runtime interactions.

## Implementation readiness

**Area:** ArchDocs.

**Delivery:** Planned extension using existing component registration and explicit Markdown embeds.

**Prerequisites:** Repository/project discovery, shared queries, source provenance, a dependency reader, and a renderer. Logical grouping additionally needs resolved Service memberships or explicit view groups. No new page-shell capability is required for explicit embedding.

[General widgets](overview.md) · [ImpactView](impact-view.md) · [Architecture checks](../rules/architecture-checks.md)
