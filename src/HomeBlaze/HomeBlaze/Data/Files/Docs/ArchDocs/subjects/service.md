---
title: Service
navTitle: Service
status: Planned
---

# Service [Planned]

Represents a logical software capability, application, or neighboring system.

## Implementation readiness

**Delivery:** Can be implemented through existing subject and component extension points. The ArchDocs feature itself is not implemented.

**Prerequisites:** The planned ArchDocs capability model and applicable query, metadata, and lifecycle services. Their delivery order is not an approved implementation plan.

## Data [Planned]

| Authored configuration | Resolved state |
|---|---|
| name, componentSources; descriptive kind/technology remain proposed | components, direct Host subset hosts, exposed Interfaces, query diagnostics |

## UI [Planned]

**Summary widget:** title/description, relevant component and Contract/Interface information, and deployment navigation. Reuse the same compact summary when explicitly embedded in Markdown, referenced by a canvas node, or shown in an aggregate view.

**Detail page:** explore selected Hosts/projects, Resources, Interfaces, relationships, diagnostics, provenance, and related Deployments without copying their facts into the page.

**Editing:** logical configuration and component selection, plus authored metadata relationships/enrichment. Source discovery and deployment selections retain their own owners.

All three can use existing registered components. Automatic context-slot insertion and ordinary-link previews are separate HomeBlaze extensions. The containing Markdown document retains its normal body and navigation identity.

## Boundary [Planned]

No required repository, Host, deployment unit, organization, or project container. Logical membership can overlap. Internal/external belongs to each diagram, not to the Service type.

## Detailed behavior [Planned]

A configurable subject representing a logical software capability or bounded context. "Application" is not an additional entity. A service can be implemented by several hosts and can select libraries or other code elements as well.

A service contains path-based component-source configuration and architectural descriptions and relationships. Its resolved components reference repository-owned code/hosts and authored resources; they do not clone them. It exposes its selected Host components through a resolved hosts collection, so a deployment unit can reference `service/hosts` directly. Additional typed subsets can be queried with ordinary paths and filters rather than a named-selection dictionary. Membership can overlap when explicitly configured. Shared code and shared resources retain one subject identity.

The user confirmed that neighboring enterprise systems are also modeled as Services. A service can be documented without known repositories or hosts. Resources sit alongside hosts at the component level: a database or consumed cloud offering can be a Resource, while an enterprise software system is a Service. The term "service" in a cloud provider's product name does not by itself make that offering an architecture Service. A provider system can still be documented as a Service when that logical system is relevant, without requiring a second subject for every resource. Exact provider/resource links remain open.

The sample uses Resource for the two database definitions. Their hosting and interface details are not yet supplied. Adding a resource or another service to an application's components does not automatically add underlying hosts to the application's resolved hosts. The direct Host subset remains explicit. Services, Hosts, and Resources can expose Interfaces describing database protocols, HTTP, or other supported interaction mechanisms.

## Usage examples

[JSON catalog](../examples/json-catalog.md) · [Markdown authoring variants](../examples/markdown-authoring.md)

## Related specifications

[Overview and status legend](../overview.md) · [Platform readiness](../implementation-readiness.md)
