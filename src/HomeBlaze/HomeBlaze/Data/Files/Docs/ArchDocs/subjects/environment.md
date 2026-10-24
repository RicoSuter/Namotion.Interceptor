---
title: Environment
navTitle: Environment
status: Planned
---

# Environment [Planned]

## Class definition [Planned]

**Environment** is an authored subject class describing a named deployment context and owning generated [Deployment](deployment.md) children for explicitly selected [DeploymentUnits](deployment-unit.md). Its defining file owns unit selection and descendant metadata. It prescribes no cloud, cluster, namespace, or geographic hierarchy.

Authored enrichment and relationships use the [existing metadata envelope](../shared/metadata-and-relationships.md), through the defining owner. They are not a second configuration store. Names in the tables are proposed serialized camelCase names; concrete CLR signatures remain open. State properties are read-only to authors.

## Configuration properties [Proposed schema]

| Property | Type | Meaning and source |
|---|---|---|
| `name` | `string` | Readable context name such as staging or production; label alone is not persistent identity. |
| `deploymentUnitSources` | Path-query collection selecting [DeploymentUnit](deployment-unit.md) | Explicit intended unit selections. Proposed default: absent or empty selects none; other subject kinds are diagnostics. |

Selectors follow [path queries](../shared/path-queries.md). Existing JSON sidecars hold metadata; Markdown-frontmatter persistence remains a planned host extension.

## State properties [Proposed schema]

| Property | Type | Meaning and source |
|---|---|---|
| `diagnostics` | Diagnostic collection | Invalid selectors, unresolved references, and reconciliation issues. |
| `deploymentUnits` | Reference collection of [DeploymentUnit](deployment-unit.md) | Resolved, deduplicated selections, without transferring unit ownership. |
| `deployments` | Keyed owned collection of [Deployment](deployment.md) | One generated enduring installation per selected unit. Reuse stable identities, add selections, and remove confirmed deselections. |

## Implementation readiness

**Delivery:** Can be implemented through existing subject and component extension points. The ArchDocs feature itself is not implemented.

**Prerequisites:** Deployment-unit selection, stable generated identity, and keyed reconciliation.

Environment is generic: it does not prescribe cloud, cluster, namespace, or geographic hierarchy. Metadata enriches generated descendants through the owning file. Standalone JSON can use existing metadata sidecars; Markdown frontmatter persistence needs a HomeBlaze extension.

## Detailed behavior [Planned]

A configurable subject describing a deployment context, such as staging or production. It declares its deployment units and owns the generated Deployment subjects for those selections. Environments can be authored as JSON files or child definitions in Markdown frontmatter.

It does not generate deployments for every service or every deployment unit in the graph. Only declared selections produce deployments.

## UI [Planned]

**Summary widget:** environment identity, selected deployment units, and intended installation counts.

**Detail page:** selected units, generated Deployments, bindings, authored enrichment, and diagnostics. An environment-scoped [DeploymentMatrix](../widgets/deployment-matrix.md) can aggregate those subjects without duplicating its specification here.

**Editing:** unit selection and metadata on the Environment and owned generated descendants. Intended installation counts do not establish observed health. JSON-sidecar editing can precede planned frontmatter persistence.

## Usage examples

[JSON catalog](../examples/json-catalog.md)

## Related specifications

[Overview and status legend](../overview.md) · [Platform readiness](../implementation-readiness.md)
