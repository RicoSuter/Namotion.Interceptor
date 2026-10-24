---
title: Artifact
navTitle: Artifact
status: Planned
---

# Artifact [Planned]

Represents a produced or consumed output such as a package, container image, or executable bundle.

## Implementation readiness

**Delivery:** Can be implemented through existing subject and component extension points. The ArchDocs feature itself is not implemented.

**Prerequisites:** Optional later domain addition; no MVP inclusion or concrete schema selected.

## Direction [Proposed]

Keep the output distinct from its producing CodeProject, a runnable Host definition, and a deployed version. Possible information includes kind, version, location, producer references, and provenance. A package feed is not automatically a SourceRepository.

## Open decisions [Proposed]

Version identity, producer relationships, storage/feed integration, contract links, and whether Artifact participates in the component capability remain to be designed. No mandatory NuGet, container, or cloud-specific hierarchy is selected.

## UI [Planned]

If this optional subject is selected, its compact widget and detail page can show the chosen output identity, kind/version, producer references, location, and provenance. Editing covers only authored values and enrichment; observed feed/build data keeps its provenance. Exact fields and specialized package/image presentation remain proposed with the Artifact model.

## Related specifications

[Overview and status legend](../overview.md) · [Platform readiness](../implementation-readiness.md)
