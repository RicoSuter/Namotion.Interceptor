---
title: Artifact
navTitle: Artifact
status: Planned
---

# Artifact [Planned]

## Class definition [Planned]

**Artifact** is an optional proposed subject class representing a produced or consumed output such as a package, container image, or executable bundle. Its defining file or a future build/feed analyzer owns it. It is distinct from a producing [CodeProject](code-project.md), a runnable [Host](host.md), and a deployed version. Participation in [Component](component.md) remains open. The following fields are proposals for this optional concept, not an approved concrete schema.

Authored enrichment and relationships use the [existing metadata envelope](../shared/metadata-and-relationships.md), through the defining owner. They are not a second configuration store. Names in the tables are proposed serialized camelCase names; concrete CLR signatures remain open. State properties are read-only to authors.

## Configuration properties [Proposed schema]

| Property | Type | Meaning and source |
|---|---|---|
| `name` | `string` | Readable output name; proposed required field if Artifact is selected. |
| `kind` | [Identifier](../shared/identifiers.md) (optional) | Optional extensible output kind; omitted means unspecified. |
| `version` | `string?` | Authored version label when known; version identity and comparison semantics remain open. |
| `location` | `string?` | Optional readable artifact location or source descriptor; storage/feed resolution remains open. |
| `producerPaths` | Subject-path collection targeting [CodeProject](code-project.md) | Optional explicit producer references; absent or empty selects no known producer. The canonical producer relationship remains proposed. |

For a discovered Artifact, analyzer-owned identity/version/location facts would be read-only state instead of competing editable fields. Supported overrides and the authored-versus-discovered contract remain open.

## State properties [Proposed schema]

| Property | Type | Meaning and source |
|---|---|---|
| `provenance` | Provenance records | Supporting source, build, or feed facts when supplied by an analyzer; none is fabricated from the artifact name. |
| `diagnostics` | Diagnostic collection | Unresolved producer/location inputs and validation issues. |
| `producers` | Reference collection of [CodeProject](code-project.md) | Resolved producer references, without transferring project ownership. |

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
