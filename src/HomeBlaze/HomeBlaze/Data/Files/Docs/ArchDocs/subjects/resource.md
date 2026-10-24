---
title: Resource
navTitle: Resource
status: Planned
---

# Resource [Planned]

## Class definition [Planned]

**Resource** is a subject class participating in the [Component](component.md) interface capability and representing a logical capability consumed by components, such as a database, queue, bucket, or cloud offering. Its authored definition or discovering analyzer owns it. Providing [Hosts](host.md), consuming subjects, and [Service](service.md) memberships reference one shared logical identity; Resource-to-Service inheritance is not selected.

Authored enrichment and relationships use the [existing metadata envelope](../shared/metadata-and-relationships.md), through the defining owner. They are not a second configuration store. Names in the tables are proposed serialized camelCase names; concrete CLR signatures remain open. State properties are read-only to authors.

## Configuration properties [Proposed schema]

| Property | Type | Meaning and source |
|---|---|---|
| `name` | `string` | Readable logical resource name. |
| `kind` | `string` | Extensible capability kind rather than a mandatory platform-specific subtype. |
| `technology` | `string?` | Optional descriptive technology; omitted means unknown. |

Descriptions and typed provide/consume/exposure relationships use existing metadata. Their canonical authored source, inverse-navigation rules, and environment mapping records remain proposed; there are no competing editable relationship collections on both endpoints.

## State properties [Proposed schema]

| Property | Type | Meaning and source |
|---|---|---|
| `hosts` | Reference collection of [Host](host.md) | Zero or many providing processes from canonical provision relationships. No Host is required. |
| `interfaces` | Reference collection of [Interface](interface.md) | Logical exposed capabilities from canonical exposure relationships. |
| `consumers` | Reference collection of [Component](component.md) | Consumers resolved from typed dependency relationships, distinct from membership and release participation. |
| `services` | Reference collection of [Service](service.md) | Inverse direct logical memberships; shared membership preserves resource identity. |
| `endpoints` | Reference collection of [Endpoint](endpoint.md) | Access bindings through exposed Interfaces where known. |
| `deployments` | Reference collection of [Deployment](deployment.md) | Related intended installations and resource mappings where supplied; no provisioning or health implication. |
| `diagnostics` | Diagnostic collection | Unresolved relationship targets and invalid binding or configuration inputs. |

## Implementation readiness

**Delivery:** Can be implemented through existing subject and component extension points. The ArchDocs feature itself is not implemented.

**Prerequisites:** The planned ArchDocs capability model and applicable query, metadata, and lifecycle services. Their delivery order is not an approved implementation plan.

## UI [Planned]

**Summary widget:** logical capability, kind/technology where supplied, provider/consumer relationships, and relevant bindings.

**Detail page:** providing Hosts, exposing Interfaces, consumers, shared membership, related Endpoints, and environment mappings with explicit unknown states.

**Editing:** the Resource's authored configuration and relationships. Dependency membership does not imply provisioning or observed health. Resource can have useful page/edit/widget presentation without inheriting Service.

## Invariants [Planned]

No visible Host is required. A shared Resource retains one logical identity across users. Inclusion in a Service is separate from release participation and provisioning. If a direct database Host reference describes enough, no separate Resource is required.

## Detailed behavior [Planned]

A reusable component-level dependency such as a database, queue, topic, storage bucket, or consumed cloud offering. A Service's component view can show Resources alongside Hosts. Services can include resources in components, and typed relationships can originate at a Service or a particular Host. A resource can be referenced by several services, and membership does not imply exclusive ownership. Including it in a service does not automatically provision it or select it into a DeploymentUnit.

Host describes a runnable process; Resource describes a capability consumed by components. The user accepted a many-to-many provides relationship: a Host can provide zero or more Resources, and a Resource can have zero or more providing Hosts. This can describe a resource provided by several database-cluster processes without requiring a known Host for a managed cloud resource. Providing a resource is a reference relationship and does not transfer subject ownership. Exact serialized fields, the authored source of the relationship, and whether inverse navigation is derived remain open. Shared capabilities and presentation can be expressed through interfaces; Resource-to-Service inheritance has not been chosen.

The user confirmed that a Service can reference a database Host directly through componentSources. Resource is optional when that Host reference provides enough detail. For example, a PostgreSQL process can be a Host, and individual databases it exposes can be Resources when those distinctions matter. Neither an intermediate Resource nor a complete provider model is required. Component membership alone does not require deploying that Host together with the application's other Hosts; DeploymentUnit selections determine that grouping.

The sample resources are logical definitions independent of environments. Deployments can bind these logical definitions to concrete environment-specific resources while retaining their logical identities. Exact instance/mapping records remain open. Do not imply resource provisioning or a particular deployment lifecycle from the presence of a Resource subject.

## Usage examples

[Shared components](../examples/repositories-and-shared-components.md)

## Related specifications

[Overview and status legend](../overview.md) · [Platform readiness](../implementation-readiness.md)
