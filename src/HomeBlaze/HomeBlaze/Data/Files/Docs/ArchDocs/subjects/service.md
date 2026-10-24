---
title: Service
navTitle: Service
status: Planned
---

# Service [Planned]

## Class definition [Planned]

**Service** is an authored subject class participating in the [Component](component.md) interface capability and representing a logical software capability, application, or neighboring system. Its defining file owns logical configuration. Selected [Hosts](host.md), [CodeProjects](code-project.md), [Resources](resource.md), and nested Services remain references to their original owners, with overlapping membership allowed.

Authored enrichment and relationships use the [existing metadata envelope](../shared/metadata-and-relationships.md), through the defining owner. They are not a second configuration store. Names in the tables are proposed serialized camelCase names; concrete CLR signatures remain open. State properties are read-only to authors.

## Configuration properties [Proposed schema]

| Property | Type | Meaning and source |
|---|---|---|
| `name` | `string` | Readable logical capability/application/system name; no extra Application entity is introduced. |
| `kind` | [Identifier](../shared/identifiers.md) (optional) | Optional descriptive classification; vocabulary remains proposed. |
| `technology` | [Identifier](../shared/identifiers.md) (optional) | Optional descriptive technology; omitted means unspecified. |
| `componentSources` | Path-query collection selecting [Component](component.md) | Explicit logical membership. Proposed default: absent or empty selects none; no transitive dependency expansion. |

`kind` and `technology` follow the shared [Identifier](../shared/identifiers.md) model: plain text, a link to any subject or ordinary Markdown page, or a typed target enabling richer presentation. Classification remains master data in each case. Expected classification types and exact reference syntax remain open.

Selectors follow [path queries](../shared/path-queries.md). Description and architectural relationships use existing metadata; interface-exposure persistence remains proposed. There is no required repository, Host, or deployment-unit selector.

## State properties [Proposed schema]

| Property | Type | Meaning and source |
|---|---|---|
| `diagnostics` | Diagnostic collection | Malformed/unresolved selectors, wrong result kinds, and cyclic selection dependencies. |
| `components` | Reference collection of [Component](component.md) | Resolved, deduplicated direct selections without cloning or ownership transfer. |
| `hosts` | Reference collection of [Host](host.md) | Direct Host subset only. Selecting a Resource or another Service does not add its underlying Hosts. |
| `interfaces` | Reference collection of [Interface](interface.md) | Logical exposed capabilities from canonical exposure relationships. |
| `deployments` | Reference collection of [Deployment](deployment.md) | Related intended installations through release participants; not an ownership or one-to-one unit relationship. |

## Implementation readiness

**Delivery:** Can be implemented through existing subject and component extension points. The ArchDocs feature itself is not implemented.

**Prerequisites:** The planned ArchDocs capability model and applicable query, metadata, and lifecycle services. Their delivery order is not an approved implementation plan.

## UI [Planned]

**Summary widget:** title/description, relevant component and Contract/Interface information, and deployment navigation. Reuse the same compact summary when explicitly embedded in Markdown, referenced by a canvas node, or shown in an aggregate view.

**Detail page:** explore selected Hosts/projects, Resources, Interfaces, relationships, diagnostics, provenance, and related Deployments without copying their facts into the page. Reuse available [architecture-check findings](../shared/architecture-checks.md) and supported documentation backlinks; the same memberships feed dependency grouping and validation.

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

[JSON catalog](../scenarios/json-catalog.md) · [Markdown authoring variants](../scenarios/markdown-authoring.md)

## Related specifications

[Overview and status legend](../overview.md) · [Platform readiness](../implementation-readiness.md)
