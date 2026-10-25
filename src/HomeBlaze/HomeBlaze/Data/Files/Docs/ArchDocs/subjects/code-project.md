---
title: CodeProject
navTitle: CodeProject
status: Planned
---

# CodeProject [Planned]

## Class definition [Planned]

**CodeProject** is a source/build subject class participating in the [Component](component.md) interface capability. A [SourceRepository](source-repository.md) owns its discovered identity and facts. It represents a project independently of a runnable process; a library is useful without any [Host](host.md), network [Interface](interface.md), or [Endpoint](endpoint.md). Architectural selections reference the same project instance.

Authored enrichment and relationships use the [existing metadata envelope](../shared/metadata-and-relationships.md), through the defining owner. They are not a second configuration store. Names in the tables are proposed serialized camelCase names; concrete CLR signatures remain open. State properties are read-only to authors.

## Configuration properties [Proposed schema]

| Property | Type | Meaning and source |
|---|---|---|
| None on a discovered CodeProject | Not applicable | Project identity, source facts, and inverse references are provider or resolved state. Authored description and relationships use repository-owned descendant metadata. |

## State properties [Proposed schema]

| Property | Type | Meaning and source |
|---|---|---|
| `key` | `string` | Stable repository-relative identity in the provider namespace, independent of display name and discovery order. |
| `name` | `string` | Discovered readable project name. |
| `sourcePath` | `string` | Normalized repository-relative project-file location; distinct from a subject path. |
| `language` | `string?` | Discovered language when established; null means unknown. |
| `classification` | `string?` | Supported library, executable, test, or other classification; unresolved inputs remain unknown. |
| `provenance` | Provenance records | Provider identity and supporting source locations for discovered facts. |
| `diagnostics` | Diagnostic collection | Unresolved references, conditions, and incomplete source facts. |
| `repository` | Reference to [SourceRepository](source-repository.md) | Canonical discovery owner. |
| `solutions` | Reference collection of [CodeSolution](code-solution.md) | Inverse memberships from discovered solutions. Zero or many; membership does not change project identity or ownership. |
| `projectReferences` | Reference collection of [CodeProject](code-project.md) | Resolved literal code dependencies; unresolved literal inputs remain available with diagnostics. |
| `hosts` | Reference collection of [Host](host.md) | Zero or many process definitions that use this entry or supporting project; never a required one-to-one wrapper. |
| `contracts` | Reference collection of [Contract](contract.md) | Inverse references to Contracts whose explicit or inferred primary project association resolves to this project; read-only, without a competing editable project-side association. |

The [Contract](contract.md) owns its optional `codeProjectPath` association and resolved `codeProject` reference. A project can therefore have Contracts while `hosts` is empty. Project references are code dependencies and do not imply runtime calls.

## Implementation readiness

**Delivery:** Can be implemented through existing subject and component extension points. The ArchDocs feature itself is not implemented.

**Prerequisites:** SourceRepository and a project provider; optional Contract associations use the Contract's explicit reference or supported discovery analyzer.

A CodeProject can contribute to multiple Hosts and Services. Libraries remain CodeProjects. A public programming API can have one or more Contracts without a Host, network Interface, or Endpoint. Basic project XML discovery does not extract public types or members; that is a separate provider capability.

## Invariants [Planned]

Project references establish code dependencies and do not assert runtime calls. Repository ownership and subject identity survive architectural selection and refresh. Tests and executable projects need explicit classification where supported; runnable output alone does not establish a production role.

## UI [Planned]

**Summary widget:** project name, source path, language/classification, and available Contract references.

**Detail page:** project dependencies, related Hosts/Services, public API Contracts, provenance, and unresolved-input diagnostics. Reuse [dependency](../widgets/dependency-diagram.md), [impact](../widgets/impact-view.md), and architecture-check results when those capabilities are available, including evidence-backed backlinks to documentation.

**Editing:** authored enrichment and references through the owning metadata/configuration route. Do not edit provider-owned project facts as if they were manually configured values. A library summary needs no Host or Endpoint.

## Usage examples

[Library contracts](../scenarios/library-contracts.md)

## Related specifications

[Overview and status legend](../overview.md) · [Platform readiness](../implementation-readiness.md)
