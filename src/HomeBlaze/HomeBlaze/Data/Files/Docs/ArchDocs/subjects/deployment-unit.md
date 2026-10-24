---
title: DeploymentUnit
navTitle: DeploymentUnit
status: Planned
---

# DeploymentUnit [Planned]

Selects the explicit components participating in a coordinated release.

## Implementation readiness

**Delivery:** Can be implemented through existing subject and component extension points. The ArchDocs feature itself is not implemented.

**Prerequisites:** Component path queries and release-participant eligibility rules. No HomeBlaze page-shell extension is required.

## Data [Planned]

| Configuration | Resolved state |
|---|---|
| name, componentSources | components, typed Host subset where useful, diagnostics |

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
