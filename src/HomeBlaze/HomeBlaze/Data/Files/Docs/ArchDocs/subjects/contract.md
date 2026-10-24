---
title: Contract
navTitle: Contract
status: Planned
---

# Contract [Planned]

## Class definition [Planned]

Contract describes a public software surface independently of networking, language, deployment, or a consumer. It is the common subject capability for [OpenApiContract](openapi-contract.md), [ProtobufContract](protobuf-contract.md), and [CSharpApiContract](csharp-api-contract.md). [CodeProjects](code-project.md) and [Interfaces](interface.md) can reference Contracts. Whether the base class itself is concrete remains a schema decision; a document with no supported parser must still have useful raw source presentation.

## Configuration properties [Proposed schema]

These fields describe the intended authored schema, not implemented configuration. Exact CLR declarations, type discriminators, and serialization details remain proposed.

| Property | Type | Required | Contract |
|---|---|---|---|
| `name` | `string` | No | Optional readable name; absence does not prevent raw document access or establish a different identity. |
| `version` | `string` | No | Optional authored version label. Absence means no authored version; it does not imply a default or compatibility guarantee. |
| `codeProjectPath` | [Identifier](../shared/identifiers.md), expected [CodeProject](code-project.md) | No | Explicit project identifier. Plain text remains descriptive; any resolved subject provides a link; a compatible CodeProject enables the typed association. This does not select source bytes. |

Format-specific file inputs belong to the subtype. A raw contract file and an authored descriptor are distinct: the descriptor configures a subject and selects its raw source, while discovery already knows the raw file location. The descriptor need not contain a copy of the source document.

## State properties [Proposed schema]

The following state is read-only. The exact member names beyond the resolved `codeProject` reference, record types, collection shapes, revision identity, and snapshot ABI remain proposed.

| State | Type | Meaning |
|---|---|---|
| `provenance` | Source/provenance records | Raw document location, defining file or analyzer owner, and available source/revision information. Explicit association and inferred association remain distinguishable. |
| `diagnostics` | Diagnostic records | Unsupported input, unresolved references, parse failures, ambiguous project association, and conflicting definitions, with readable source locations. |
| `codeProject` | Optional [CodeProject](code-project.md) reference | The resolved typed association, explicit or inferred from a unique same-directory project. A plain identifier or generic link remains valid without it; unresolved paths and ambiguous inference produce diagnostics. |
| `referringSubjects` | Subject references, including [CodeProject](code-project.md) and [Interface](interface.md) | Subjects that reference this Contract. These references do not clone it or transfer ownership. |

The associated CodeProject's Contracts collection is a read-only inverse of this association. Do not maintain a second editable `hasContract` relationship for the same fact. Other descriptive relationships still use the shared relationship model.

## Implementation readiness

**Delivery:** Can be implemented through existing subject and component extension points. The ArchDocs feature, Contract schemas, raw contract discovery, and parsers described here are not implemented.

**Prerequisites:** Subject registration, documented file origins and subject references, project discovery for inferred associations, and reconciliation. Parsing and public API extraction are optional analyzer capabilities.

## Association and discovery [Planned]

An explicit `codeProjectPath` takes precedence over directory inference. Preserve a plain project identifier or a generic subject link as valid descriptive data; the typed `codeProject` association remains absent until a compatible CodeProject resolves. An unresolved path produces a resolution diagnostic. Do not silently substitute an inferred project for an explicit identifier. For a selected or discovered raw contract document without an explicit identifier, consider only CodeProjects whose source project file is in the raw document's own directory. Exactly one candidate permits association. No candidates or multiple candidates require an explicit `codeProjectPath` resolving to a CodeProject to establish that typed association; keep the raw document accessible and report a visible diagnostic.

Do not search ancestor directories, choose a nearest project, or infer a project through a Host. A CodeProject can have Contracts with zero Hosts, Interfaces, or Endpoints and can contribute to zero or many Hosts. File path origins, precedence, owner boundaries, and unresolved collision choices are specified once in [Contract file discovery](../analyzers/contract-discovery.md).

## Invariants [Planned]

A library API needs no Host, network Interface, or Endpoint. Parsed state must remain tied to its source/revision. Unknown or unsupported input is reported explicitly and retains raw source access. A version label does not implement comparison or compatibility assessment.

Authored definitions take precedence over same-source discovered duplicates. Discovery never writes descriptor JSON. A referencing subject does not take ownership of a Contract. Exact source identity, competing explicit definitions, and authored/generated ownership transitions remain design work, with conflicts reported rather than silently reassigned.

## UI [Planned]

**Summary widget:** source, optional version, association and its provenance, diagnostics, and referencing subjects.

**Detail page:** common source/document access and available public-surface information. Reuse the base presentation when no specialized parser or viewer is available. Format-specific viewers belong to the subtype specifications.

**Editing:** supported authored name, version, subtype source selection, and explicit project association through the defining file's writer. Parsed state and inferred references remain read-only. A diagnostic for a missing or ambiguous project must be understandable without opening a diagram.

Explicit embeds and standalone detail pages use existing registration and can precede automatic context/link-preview integration. Registering a subtype does not implement discovery, parsing, comparison, or compatibility assessment.

## Usage examples

[Contract files and discovery](../scenarios/contract-files.md) · [Library contracts](../scenarios/library-contracts.md)

## Related specifications

[Overview and status legend](../overview.md) · [Platform readiness](../implementation-readiness.md) · [File authoring](../file-authoring.md)
