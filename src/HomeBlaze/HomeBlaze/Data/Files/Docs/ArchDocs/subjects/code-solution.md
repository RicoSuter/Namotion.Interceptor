---
title: CodeSolution
navTitle: CodeSolution
status: Planned
---

# CodeSolution [Planned]

## Class definition [Planned]

**CodeSolution** is a discovered source/build grouping subject class owned by a [SourceRepository](source-repository.md). A solution-file analyzer creates it from supported solution files and resolves its members to the repository's existing [CodeProjects](code-project.md). It describes source/build organization independently of a [Service](service.md), runnable [Host](host.md), or [DeploymentUnit](deployment-unit.md). Participation in the [Component](component.md) capability remains open.

Authored enrichment and relationships use the [existing metadata envelope](../shared/metadata-and-relationships.md), through the defining owner. They are not a second configuration store. Names in the tables are proposed serialized camelCase names; concrete CLR signatures remain open. State properties are read-only to authors.

## Configuration properties [Proposed schema]

| Property | Type | Meaning and source |
|---|---|---|
| None on a discovered CodeSolution | Not applicable | SourceRepository selects analyzers/scan scope. Descriptive enrichment uses owner metadata, not an independently editable project-membership property. |

## State properties [Proposed schema]

| Property | Type | Meaning and source |
|---|---|---|
| `key` | `string` | Stable repository-relative identity in the analyzer namespace, independent of display name and discovery order. |
| `name` | `string` | Discovered readable solution name. |
| `sourcePath` | `string` | Normalized repository-relative solution-file location. |
| `repository` | Reference to [SourceRepository](source-repository.md) | Canonical discovery owner. |
| `projects` | Reference collection of [CodeProject](code-project.md) | Resolved existing repository projects in this solution; zero or many, without ownership transfer. |
| `projectLocations` | Read-only source-location records | Literal solution project entries, including unresolved/unsupported entries with provenance; exact record shape remains open. |
| `provenance` | Provenance records | Analyzer identity, solution file, and supporting locations for membership facts. |
| `diagnostics` | Diagnostic collection | Unresolved project locations, unsupported entries, malformed source, and scope findings. |

## Implementation readiness

**Delivery:** Can be implemented through existing subject/analyzer extension points. The ArchDocs feature itself is not implemented.

**Prerequisites:** SourceRepository, solution-file discovery, project-reference resolution, and stable keyed reconciliation. Supported solution formats and analyzer options remain proposed; adding this subject does not implement a parser or authorize builds.

## Membership and lifecycle [Planned]

A repository owns its discovered solutions and projects as separate keyed collections. A solution references existing CodeProjects; it never recreates them underneath itself or transfers their canonical ownership. A project can belong to zero, one, or many solutions, and the same solution can contain many projects. CodeProject.solutions is the read-only inverse of discovered membership.

Identity follows the repository/analyzer/element-kind/repository-relative key rules in [reconciliation](../shared/reconciliation.md). Repeated discovery reuses compatible solution subjects and unchanged membership references. A solution-file move is a removal and addition unless a later explicit identity mapping preserves it. Removing a solution or its membership does not remove a project still present in repository discovery. Confirmed source/project removal and failed-pass retention follow the repository publication contract.

Solution membership records source/build grouping only. It does not select logical Service components, recursively expand their membership, imply a runtime communication edge, create a Host, or select release participants. No solution-level deployment capability is selected.

## Scope and open choices [Proposed]

Supported solution formats and revisions, solution folders, nested grouping, configuration/platform mappings, project kinds, and incomplete or out-of-scope project references require analyzer-specific decisions. Preserve unsupported entries and unresolved locations as source-located diagnostics instead of inventing a CodeProject or silently treating a partial read as an empty solution. Reading a solution does not restore, evaluate MSBuild, build, or execute it.

A solution's project references use source-file locations interpreted within the repository analysis scope. They are distinct from authored subject paths. Exact serialized key/reference representation and support for projects outside a configured source root remain open.

## UI [Planned]

**Summary widget:** solution name, repository-relative source path, available [CodeProject](code-project.md) count, and discovery diagnostics.

**Detail page:** browse referenced [CodeProjects](code-project.md), their other [CodeSolutions](code-solution.md), project dependencies, source provenance, and unresolved/unsupported solution entries. Show grouping without presenting it as a Service or deployment boundary.

**Editing:** authored description, links, tags, and relationships through the [SourceRepository](source-repository.md) owner's metadata. Discovered identity and solution membership remain analyzer-owned; edit the source solution file through a suitable source-authoring route rather than creating a competing architecture membership list.

## Related specifications

[SourceRepository](source-repository.md) · [CodeProject](code-project.md) · [Reconciliation](../shared/reconciliation.md) · [Overview and status legend](../overview.md) · [Platform readiness](../implementation-readiness.md)
