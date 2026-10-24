---
title: DeploymentDiagram
navTitle: DeploymentDiagram
status: Planned
---

# DeploymentDiagram [Planned]

Visualize selected [Environments](../subjects/environment.md), their intended [Deployments](../subjects/deployment.md), [DeploymentUnits](../subjects/deployment-unit.md), and participating [Components](../subjects/component.md). This specializes deployment presentation over the shared graph.

## Configuration properties [Proposed schema]

Keys use PascalCase for Markdown body blocks. These are proposed view properties, not an implemented API.

| Property | Type | Default and meaning |
|---|---|---|
| `Path` | Subject/folder path `string` | Required scope; may select one Environment directly or a tree containing Environments. |
| `EnvironmentSources` | Optional [PathQuery](../shared/path-queries.md) list selecting [Environment](../subjects/environment.md) | Omitted: select Environments in the scope and owned descendants. Empty: none. Explicit entries select a deduplicated set. |
| `DeploymentUnitSources` | Optional PathQuery list selecting [DeploymentUnit](../subjects/deployment-unit.md) | Omitted: display units selected by those Environments. Empty: none. Explicit entries filter displayed units without creating Deployments. |

## Markdown example [Proposed]

Assume `/environments/Production` defines an Environment:

````markdown
:: Namotion.ArchDocs.DeploymentDiagram Path=/environments/Production
::
````

## Behavior and limits [Planned]

Reuse [common diagram behavior](architecture-diagram.md). Display actual generated Deployment subjects, retaining each installation's identity and available version, address, and binding data. Do not manufacture a Cartesian product or imply that every known unit is installed everywhere. Shared component definitions remain shared even when environment bindings differ.

Physical nodes, cloud resources, network topology, and concrete database/queue instances require explicit infrastructure mappings. Missing mappings remain unknown; a Resource's inclusion in a unit does not prove provisioning. Intended configuration does not establish observed health or runtime replicas. Infrastructure mapping schema, node grouping, layout, and renderer remain open.

## Implementation readiness

**Delivery:** Planned extension using existing component registration and explicit Markdown embeds.

**Prerequisites:** Environments, reconciled Deployments, unit participants, available bindings, summaries, and a renderer. Complete infrastructure diagrams require the corresponding mapping model.

[General widgets](overview.md) · [DeploymentMatrix](deployment-matrix.md) · [Environment](../subjects/environment.md)
