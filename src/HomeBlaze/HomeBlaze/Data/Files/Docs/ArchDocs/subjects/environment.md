---
title: Environment
navTitle: Environment
status: Planned
---

# Environment [Planned]

Declares which deployment units have intended installations in a named context.

## Implementation readiness

**Delivery:** Can be implemented through existing subject and component extension points. The ArchDocs feature itself is not implemented.

**Prerequisites:** Deployment-unit selection, stable generated identity, and keyed reconciliation.

## Data [Planned]

| Configuration | Owned state |
|---|---|
| name, deploymentUnitSources | Resolved units and one generated Deployment per selected unit |

Environment is generic: it does not prescribe cloud, cluster, namespace, or geographic hierarchy. Metadata enriches generated descendants through the owning file. Standalone JSON can use existing metadata sidecars; Markdown frontmatter persistence needs a HomeBlaze extension.

## Detailed behavior [Planned]

A configurable subject describing a deployment context, such as staging or production. It declares its deployment units and owns the generated Deployment subjects for those selections. Environments can be authored as JSON files or child definitions in Markdown frontmatter.

It does not generate deployments for every service or every deployment unit in the graph. Only declared selections produce deployments.

## Usage examples

[JSON catalog](../examples/json-catalog.md)

## Related specifications

[Overview and status legend](../overview.md) · [Platform readiness](../implementation-readiness.md)
