---
title: Architecture Diagrams
navTitle: Architecture Diagrams
status: Planned
---

# Architecture Diagrams [Planned]

Render service, component, context, dependency, scenario, or deployment views without fixing a documentation format.

## Widget entry points [Proposed]

Use separate author-facing widgets with configurations appropriate to each view. Share graph queries, relationship readers, node summaries, navigation, and renderer infrastructure underneath them. Distinct entry points allow different boxes, labels, grouping, and layout behavior without a growing `View` switch that exposes irrelevant options to each author.

| Widget | Primary data | Configuration reference |
|---|---|---|
| [ServiceDiagram](service-diagram.md) | [Services](../subjects/service.md) and interactions between them | Service scope and optional source queries |
| [ComponentDiagram](component-diagram.md) | A Service's [Components](../subjects/component.md), such as [Hosts](../subjects/host.md) and [Resources](../subjects/resource.md) | Service reference and optional component queries |
| [ContextDiagram](context-diagram.md) | Internal/external Services and optional [Actors](../subjects/actor.md) | Explicit boundary selections per view |
| [DeploymentDiagram](deployment-diagram.md) | [Environments](../subjects/environment.md), [Deployments](../subjects/deployment.md), and [DeploymentUnits](../subjects/deployment-unit.md) | Environment selections and available bindings |
| [DependencyDiagram](dependency-diagram.md) | Discovered [CodeProject](../subjects/code-project.md) references, optionally grouped by Service or explicit layer/module queries | Project scope and optional group definitions |
| [ScenarioDiagram](scenario-diagram.md) | Authored ordered interactions referring to shared graph participants | Explicit participant references and steps |

These are proposed names and schemas, all unimplemented. This page owns common graph presentation behavior, not a second public configuration schema or a domain subject. Renderer selection and whether specialized views share one renderer or several remain open; sequence rendering can have different needs from static graph layout. [ImpactView](impact-view.md) can use a graph, table, or list, and [architecture-check findings](architecture-check-results.md) can annotate compatible diagrams without becoming another independent architecture model.

## Implementation readiness

**Delivery:** Can be implemented through existing subject and component extension points. The ArchDocs feature itself is not implemented.

**Prerequisites:** Shared scope queries, component/exposure/provision relationships, relationship readers, and a renderer to be selected.

## Behavior [Planned]

Select graph data independently of display settings. Context views choose their internal/external sets per diagram. Component views can show Hosts, Resources, and nested Services. Preserve relationship kinds, unknown endpoints, overlapping membership, and provenance; aggregation must not fabricate runtime order.

Manual layout belongs to the document/view; semantic connections belong to shared metadata. CanvasLayout placement and referenced subject widgets already exist [Implemented], but connector lines and generated architecture diagrams are [Planned]. Renderer choice, grouping, layout, and connector editing remain open.

## Runtime and deployment limits [Planned]

Runtime sequences need authored scenarios; the proposed ScenarioDiagram supplies linked participants and explicit steps rather than inferring order from dependencies. Complete deployment views need environment bindings and infrastructure mappings. Ordinary arc42 headings do not add missing graph facts. Actors are optional until their model is selected.

## Context boundaries [Planned]

The user confirmed that internal/external is relative to the diagram's documented system boundary, including neighboring systems operated by the same organization. The author chooses what is internal and external for each context diagram. Payments can be internal in the Commerce context and external in an Orders context without changing the Service subject. Organizational ownership is a separate concern and is not requested as a substitute for this boundary classification.

The proposed [ContextDiagram configuration](context-diagram.md#configuration-properties-proposed-schema) has InternalSources and ExternalSources lists using the shared PathQuery record, with resolved internal and external sets as view state. Exact field spellings and rendering details remain open. The sample demonstrates both contexts using the same Orders and Payments subjects. Its block configuration uses PascalCase keys, as existing Markdown configuration fences do; persisted subject/frontmatter configuration follows its own naming convention.

Proposed defaults: explicit lists control the visible participants and relationships between them; diagnose an identity selected into both sets instead of cloning it or silently choosing a side. Optional automatic discovery of neighboring participants can be considered separately. Boundary membership and layout are view configuration, while semantic relationships stay in the shared model. No global internal/external flag or separate external-Service subtype is needed. Actors and their representation remain open; no mandatory System root has been selected.

## Related specifications

[Overview and status legend](../overview.md) · [Platform readiness](../implementation-readiness.md)
