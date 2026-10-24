---
title: ComponentDiagram
navTitle: ComponentDiagram
status: Planned
---

# ComponentDiagram [Planned]

Explore a [Service](../subjects/service.md) through its [Components](../subjects/component.md), including runnable [Hosts](../subjects/host.md), logical [Resources](../subjects/resource.md), [CodeProjects](../subjects/code-project.md), and nested Services where selected. Different component kinds can use appropriate summaries and grouping.

## Configuration properties [Proposed schema]

Keys use PascalCase for Markdown body blocks. These are proposed view properties, not an implemented API.

| Property | Type | Default and meaning |
|---|---|---|
| `Path` | Subject path referencing [Service](../subjects/service.md) | Required Service whose component view is being described; also supplies relative reference scope. |
| `ComponentSources` | Optional [PathQuery](../shared/path-queries.md) list selecting Component-capable subjects | Omitted: use the Service's resolved `components`. Empty: select none. Explicit entries select the visible set without changing Service membership. |
| `RelationshipKinds` | Optional `string` list | Omitted: display supported semantic relationship kinds. Empty: display no edges. Values filter the [relationship kind](../shared/metadata-and-relationships.md). |

## Markdown example [Proposed]

Assume `/catalog/Orders` is a Service definition:

````markdown
:: Namotion.ArchDocs.ComponentDiagram Path=/catalog/Orders
::
````

## Behavior and limits [Planned]

Reuse the [common diagram behavior](architecture-diagram.md). Membership references the same underlying subjects; it does not clone a shared database or queue. Keep a Service-level interaction at its known boundary until a Host endpoint is authored. Unknown or filtered endpoints must remain visible as incomplete relationships rather than being assigned to a guessed Host. Code dependencies retain their separate meaning and provenance when a presentation supports displaying them.

Do not infer recursive expansion, deployability, or resource ownership from component membership. Exact nested-Service expansion, node styling, edge filtering, and manual layout remain design choices.

## Implementation readiness

**Delivery:** Planned extension using existing component registration and explicit Markdown embeds.

**Prerequisites:** Service components, typed relationships, suitable component summaries, and a renderer. It does not require page-context slots.

[General widgets](overview.md) · [ServiceDiagram](service-diagram.md) · [DeploymentDiagram](deployment-diagram.md)
