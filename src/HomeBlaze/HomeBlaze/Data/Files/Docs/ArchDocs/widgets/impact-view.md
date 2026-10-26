---
title: ImpactView
navTitle: ImpactView
status: Planned
---

# ImpactView [Planned]

Explore what depends on selected code, contracts, or architecture subjects, including documentation that references them. Present linked results as a list, table, or dependency graph. This is a query view over existing identities, not a new Impact domain entity.

## Configuration properties [Proposed schema]

| Property | Type | Default and meaning |
|---|---|---|
| `Path` | Subject/folder path `string` | Required analysis scope. |
| `SubjectSources` | [PathQuery](../shared/path-queries.md) list | Required starting subjects whose known dependents/references should be explored. |
| `RelationshipKinds` | Optional `string` list | Omitted: supported dependency/reference channels, labeled separately. Empty: no relationship traversal. |
| `Depth` | Positive integer | Proposed default: one reverse-reference hop. Further traversal must be explicit and cycle-safe. |

## Markdown example [Proposed]

````markdown
:: Namotion.ArchDocs.ImpactView Path=/
```json
{
  "SubjectSources": [{ "Path": "/Repository/projects/Orders.Contracts" }],
  "Depth": 1
}
```
::
````

## Behavior and limits [Planned]

Show the evidence path from each starting subject to a dependent project, logical Service, Contract, or related document when supported. Distinguish code dependencies, membership, and documentation references; do not traverse every ownership or arbitrary metadata edge as if it represented impact. Details must explain why a result is shown.

Dependency traversal here is explicit view behavior. It does not change Service membership or enable the deferred automatic inclusion of transitive dependencies in selectors. Keep outside-scope endpoints visible according to the query boundary without silently expanding the scope.

Resolve supported Markdown links and structured documentation relationships into backlinks. A documentation link establishes a reference, not a runtime dependency. Documents discovered through backlinks are candidates for review after a change; the view cannot establish whether their prose is correct or automatically rewrite it. Missing references and incomplete analysis stay visible.

Selecting starting subjects is sufficient for an initial useful view. Deriving them from changed source files, a Git diff, or contract comparison is an optional later input adapter; no Git integration is required by this widget. Any changed-file mapping needs repository identity and provider provenance and must not guess from display names.

## Implementation readiness

**Area:** ArchDocs; automatic context placement or link overlays additionally involve HomeBlaze.

**Delivery:** Planned extension using explicit widgets and existing subject navigation.

**Prerequisites:** A complete dependency/reference index, supported relationship traversal, queries, provenance, and summaries. Documentation impact requires the supported link/reference index; it is not supplied by project XML alone.

[DependencyDiagram](dependency-diagram.md) · [Metadata and relationships](../shared/metadata-and-relationships.md) · [Page contributions](../integration/page-contributions.md)
