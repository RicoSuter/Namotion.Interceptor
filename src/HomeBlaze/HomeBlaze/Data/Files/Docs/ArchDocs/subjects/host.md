---
title: Host
navTitle: Host
status: Planned
---

# Host [Planned]

## Class definition [Planned]

**Host** is a subject class participating in the [Component](component.md) interface capability and representing a runnable process definition, independently of a physical machine or running replica. An authored definition belongs to its defining file; a discovered Host belongs to its [SourceRepository](source-repository.md). [CodeProjects](code-project.md), provided [Resources](resource.md), and exposed [Interfaces](interface.md) are references, not owned copies.

Authored enrichment and relationships use the [existing metadata envelope](../shared/metadata-and-relationships.md), through the defining owner. They are not a second configuration store. Names in the tables are proposed serialized camelCase names; concrete CLR signatures remain open. State properties are read-only to authors.

## Configuration properties [Proposed schema]

| Property | Type | Meaning and source |
|---|---|---|
| `name` | `string` | Readable process-definition name for an authored Host. |
| `runtime` | [Identifier](../shared/identifiers.md) (optional) | Optional extensible runtime description; omitted means unknown. |
| `packaging` | [Identifier](../shared/identifiers.md) (optional) | Optional packaging description; omitted means unknown. |
| `executionBehavior` | [Identifier](../shared/identifiers.md) (optional) | Optional extensible behavior such as continuous or run-to-completion; no always-running default is implied. |
| `roles` | `string` collection | Optional descriptive process roles. Absent or empty assigns no known roles. |
| `entryProjectPath` | Optional [Identifier](../shared/identifiers.md), expected [CodeProject](code-project.md) | Entry project identifier when known. Plain text or a generic subject link remains useful before a typed CodeProject is available. |
| `supportingProjectPaths` | Subject-path collection targeting [CodeProject](code-project.md) | Optional supporting code references. Absent or empty selects no known supporting project. |

For discovered Hosts, name/runtime/packaging/execution/role and project facts are provider-owned state where supported, rather than independently editable configuration. Authored overrides require an explicit future contract. The canonical metadata relationship source for resource provision and interface exposure remains open.

## State properties [Proposed schema]

| Property | Type | Meaning and source |
|---|---|---|
| `provenance` | Provenance records | Provider and supporting source for discovered facts. |
| `diagnostics` | Diagnostic collection | Unresolved project references and invalid relationship/configuration inputs. |
| `repository` | Optional reference to [SourceRepository](source-repository.md) | Canonical owner for a discovered Host; absent for an independently authored definition. |
| `entryProject` | Optional reference to [CodeProject](code-project.md) | Resolved or discovered entry project. |
| `supportingProjects` | Reference collection of [CodeProject](code-project.md) | Resolved or discovered supporting code references. |
| `resources` | Reference collection of [Resource](resource.md) | Zero or many provided capabilities resolved from the canonical provision relationship. |
| `interfaces` | Reference collection of [Interface](interface.md) | Logical exposed capabilities resolved from canonical exposure relationships. |
| `services` | Reference collection of [Service](service.md) | Inverse direct logical memberships; no ownership or automatic nested expansion. |

## Implementation readiness

**Delivery:** Can be implemented through existing subject and component extension points. The ArchDocs feature itself is not implemented.

**Prerequisites:** Manual configuration can start independently; discovered Hosts require SourceRepository and an applicable provider.

Execution can be continuous or run-to-completion. A worker, migration executable, local orchestration tool, or database process needs no dedicated platform-specific subtype. A managed resource can be modeled without any known Host.

## Relationships [Planned]

Hosts can provide multiple Resources; a Resource can have multiple providing Hosts. These are references. Services can select Hosts directly without an intermediate Resource. Discovery of an executable does not imply Service membership, deployment membership, an always-running role, or observed execution.

## UI [Planned]

**Summary widget:** process definition, known source/runtime/packaging, and execution behavior when supplied.

**Detail page:** entry/supporting CodeProjects, provided Resources, exposed Interfaces, architectural relationships, and release participation with provenance.

**Editing:** supported authored Host configuration and enrichment. Discovered facts remain provider-owned. The widget does not equate a process definition with an observed running replica or infer health from deployment membership.

## Related specifications

[Overview and status legend](../overview.md) · [Platform readiness](../implementation-readiness.md)
