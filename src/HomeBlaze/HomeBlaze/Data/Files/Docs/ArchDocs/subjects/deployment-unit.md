---
title: DeploymentUnit
navTitle: DeploymentUnit
status: Planned
---

# DeploymentUnit [Planned]

## Class definition [Planned]

**DeploymentUnit** is an authored subject class selecting the explicit [Components](component.md) participating in a coordinated release. Its defining file owns its configuration; selected participants retain their own owners. It is independent of a [Service](service.md) boundary and does not itself establish provisioning, execution, or observed deployment.

Authored enrichment and relationships use the [existing metadata envelope](../shared/metadata-and-relationships.md), through the defining owner. They are not a second configuration store. Names in the tables are proposed serialized camelCase names; concrete CLR signatures remain open. State properties are read-only to authors.

## Configuration properties [Proposed schema]

| Property | Type | Meaning and source |
|---|---|---|
| `name` | `string` | Readable release-unit name; identity is not derived from the label alone. |
| `componentSources` | Path-query collection selecting [Component](component.md) | Explicit release participants. Proposed default: absent or empty selects none; invalid or ineligible results produce diagnostics. |

Selection syntax, union/deduplication behavior, and invalidation follow [path queries](../shared/path-queries.md). The participant eligibility and managed-resource schemas remain open.

## State properties [Proposed schema]

| Property | Type | Meaning and source |
|---|---|---|
| `components` | Reference collection of [Component](component.md) | Resolved, deduplicated release participants. Component capability alone does not establish release eligibility. |
| `hosts` | Reference collection of [Host](host.md) | Direct Host subset of selected participants, where useful; no transitive expansion through Services or Resources. |
| `deployments` | Reference collection of [Deployment](deployment.md) | Inverse intended installations that reference this unit. |
| `diagnostics` | Diagnostic collection | Selector failures, cyclic dependencies, wrong kinds, and eligibility issues. |

## Implementation readiness

**Delivery:** Can be implemented through existing subject and component extension points. The ArchDocs feature itself is not implemented.

**Prerequisites:** Component path queries and release-participant eligibility rules. No HomeBlaze page-shell extension is required.

## UI [Planned]

**Summary widget:** release identity, selected participants, related Services, and environment installation links.

**Detail page:** explicit release membership, component provenance/diagnostics, and related Deployments. Distinguish an existing dependency from Resource configuration managed in the release.

**Editing:** authored participant queries and metadata. Reuse the general [DeploymentMatrix](../widgets/deployment-matrix.md) when an aggregate environment view is useful; its independent specification remains in widgets/.

## Detailed behavior [Planned]

A configurable selection of components participating in one coordinated release. It can select all hosts of one service, a subset, hosts from several services, and Resources whose definitions or configuration participate in that release. Ordinary dependencies remain separate from release membership. A reference to an existing managed resource does not by itself declare that the unit provisions it. There is no ownership or cardinality restriction that forces a deployment unit to correspond to one service.

The user accepted component-based deployment composition. The working schema therefore uses componentSources and resolved components in place of the earlier hostSources-only shape. Selecting service/hosts remains a valid concise way to select its direct Hosts. Component capability alone does not establish deployability: a library CodeProject is not a runnable deployment participant merely because it can appear in a Service's components. Exact eligibility and dependency-versus-managed-resource representation remain schema decisions.

The common arrangement is one service associated with one deployment unit. Service boundaries remain logical boundaries when the deployment arrangement changes.

## Usage examples

[JSON catalog](../examples/json-catalog.md) · [Shared components](../examples/repositories-and-shared-components.md)

## Related specifications

[Overview and status legend](../overview.md) · [Platform readiness](../implementation-readiness.md)
