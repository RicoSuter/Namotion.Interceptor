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

Show participants, references to Services, and related Deployments using registered components. References to existing dependencies must be visibly distinct from configuration managed as part of a release.

## Detailed behavior [Planned]

A configurable selection of components participating in one coordinated release. It can select all hosts of one service, a subset, hosts from several services, and Resources whose definitions or configuration participate in that release. Ordinary dependencies remain separate from release membership. A reference to an existing managed resource does not by itself declare that the unit provisions it. There is no ownership or cardinality restriction that forces a deployment unit to correspond to one service.

The user accepted component-based deployment composition. The working schema therefore uses componentSources and resolved components in place of the earlier hostSources-only shape. Selecting service/hosts remains a valid concise way to select its direct Hosts. Component capability alone does not establish deployability: a library CodeProject is not a runnable deployment participant merely because it can appear in a Service's components. Exact eligibility and dependency-versus-managed-resource representation remain schema decisions.

The common arrangement is one service associated with one deployment unit. Service boundaries remain logical boundaries when the deployment arrangement changes.

## Related specifications

[Overview and status legend](../overview.md) · [Platform readiness](../implementation-readiness.md)
