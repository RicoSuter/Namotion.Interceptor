---
title: CSharpApiContract
navTitle: CSharpApiContract
status: Planned
---

# CSharpApiContract [Planned]

## Class definition [Planned]

**CSharpApiContract** is a subject class derived from [Contract](contract.md) for a public C# programming surface. It retains the Contract defining-file or analyzer owner and its optional primary [CodeProject](code-project.md) association. It does not require a [Host](host.md), network [Interface](interface.md), or [Endpoint](endpoint.md). Concrete extraction and snapshot representations remain proposed.

Authored enrichment and relationships use the [existing metadata envelope](../shared/metadata-and-relationships.md), through the defining owner. They are not a second configuration store. Names in the tables are proposed serialized camelCase names; concrete CLR signatures remain open. State properties are read-only to authors.

## Configuration properties [Proposed schema]

| Property | Type | Meaning and source |
|---|---|---|
| Inherited Contract fields | [Contract configuration](contract.md#configuration-properties-proposed-schema) | Optional `name`, `version`, and `codeProjectPath`; do not duplicate them in this subtype. |
| `snapshotPath` | `string?` | Optional explicitly supplied API snapshot input; its format and resolution contract remain open. |
| `revision` | `string?` | Optional source/snapshot revision selector; omitted means unspecified, not the latest remote revision. |
| `targetFramework` | `string?` | Optional framework surface selector. Unspecified framework behavior must be settled before extraction is implemented. |

Snapshot input and analyzer extraction are alternative proposed input capabilities. Source-symbol versus compiled-metadata extraction, precedence, defaults, and validation remain open; no restore, build, or execution is authorized by these field proposals.

## State properties [Proposed schema]

| Property | Type | Meaning and source |
|---|---|---|
| Inherited Contract state | [Contract state](contract.md#state-properties-proposed-schema) | Common resolved source/project references, provenance, and diagnostics. |
| `publicTypes` | Read-only public-type records | Available extracted or snapshot types with member/signature records. Exact parsed shape remains proposed; unavailable extraction is distinct from a known empty surface. |
| `surfaceRevision` | `string?` | Revision actually represented by the available surface. |
| `surfaceTargetFramework` | `string?` | Framework actually represented by the available surface. |

## Implementation readiness

**Delivery:** Can be implemented through existing subject and component extension points. The ArchDocs feature itself is not implemented.

**Prerequisites:** Contract; an optional C# public API analyzer or an explicitly supplied snapshot.

## Inheritance [Planned]

CSharpApiContract derives from Contract. It can describe public types, members, and signatures of a CodeProject without network Interfaces or Endpoints. A Contract may be authored as a supplied snapshot before extraction exists.

## Scope and open choices [Proposed]

Decide whether extraction uses source symbols or compiled metadata, which surface/revision and target framework it describes, and the snapshot format. The minimal csproj XML analyzer does not implement this. Generating a complete surface must report unresolved inputs rather than invent members. Extraction, comparison, and compatibility assessment are separate capabilities.

## UI [Planned]

The specialized widget/detail view extends the [Contract presentation](contract.md#ui-planned) with public types, members, and signatures for the selected project/revision/target where available. Link back to the defining CodeProject and make incomplete extraction visible.

Public API browsing does not require network Interfaces, Hosts, or Endpoints. A supplied snapshot can use common source presentation before extraction exists. API comparison and compatibility assessment are separate capabilities.

## Usage examples

[Library contracts](../examples/library-contracts.md)

## Related specifications

[Overview and status legend](../overview.md) · [Platform readiness](../implementation-readiness.md)
