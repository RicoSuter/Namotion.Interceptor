---
title: ServiceTable
navTitle: ServiceTable
status: Planned
---

# ServiceTable [Planned]

Find and summarize Services within a supplied subject/folder scope.

## Configuration properties [Proposed schema]

| Property | Type | Default and meaning |
|---|---|---|
| `Path` | Subject/folder path `string` | Required scope. Find [Services](../subjects/service.md) in this scope and its owned descendants, deduplicated by identity. Relationships do not extend the search scope. |

Keys use PascalCase for the existing Markdown body-block convention. Column configuration and master-detail controls remain open. For example, with Services defined under `/catalog`:

````markdown
:: Namotion.ArchDocs.ServiceTable Path=/catalog
::
````

## Implementation readiness

**Delivery:** Can be implemented through existing subject and component extension points. The ArchDocs feature itself is not implemented.

**Prerequisites:** Service capability, scoped subject traversal, shared queries, and compact subject presentation.

## Behavior [Planned]

Find nested definitions from JSON, Markdown children when available, and body blocks. Show each identity once with relevant description, component counts, and deployment navigation. Proposed traversal follows owned descendants rather than following every relationship or membership reference beyond the scope. Rows update with graph changes, without rerunning source analysis.

## Existing primitive [Implemented]

SubjectTable already filters the immediate subjects of a folder/collection by type. Recursive service discovery and architecture-specific summaries remain planned. Reuse appropriate table and query primitives.

## Optional interaction [Proposed]

Master-detail can be implemented inside the widget first. A document-level context pane needs the page-contribution extension. Exact columns and presentation controls remain open.

## Related specifications

[Overview and status legend](../overview.md) · [Platform readiness](../implementation-readiness.md)
