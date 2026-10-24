---
title: DeploymentMatrix
navTitle: DeploymentMatrix
status: Planned
---

# DeploymentMatrix [Planned]

Display intended installations and environment-specific details from the shared model.

## Implementation readiness

**Delivery:** Can be implemented through existing subject and component extension points. The ArchDocs feature itself is not implemented.

**Prerequisites:** DeploymentUnits, Environments, generated Deployments, and optional Service grouping.

## Behavior [Planned]

Use explicit Environment selections. Show units against environments, optionally grouped or labeled by related Services. Do not manufacture a Cartesian product of deployments. Link each installation to its subject and expose manual versions/endpoints with provenance.

## Invariants [Planned]

Configured presence does not imply observed health. Missing, unknown, failed, and empty data must remain distinct. An explicit Markdown embed is independent of page-context slots; monitoring connectors are optional future work.

## Related specifications

[Overview and status legend](../overview.md) · [Platform readiness](../implementation-readiness.md)
