---
title: Resource
navTitle: Resource
status: Planned
---

# Resource [Planned]

Represents a logical capability consumed by components, such as a database, queue, bucket, or cloud offering.

## Implementation readiness

**Delivery:** Can be implemented through existing subject and component extension points. The ArchDocs feature itself is not implemented.

**Prerequisites:** The planned ArchDocs capability model and applicable query, metadata, and lifecycle services. Their delivery order is not an approved implementation plan.

## Data [Planned]

| Authored configuration | Relationships and bindings |
|---|---|
| name, extensible kind, optional technology | Optional providing Hosts, exposed Interfaces, consumers, environment-specific mappings |

## UI [Planned]

A standalone Resource can have useful page/edit/widget components like other subjects. Reuse generic subject presentation; Resource does not inherit Service merely to gain UI.

## Invariants [Planned]

No visible Host is required. A shared Resource retains one logical identity across users. Inclusion in a Service is separate from release participation and provisioning. If a direct database Host reference describes enough, no separate Resource is required.

## Detailed behavior [Planned]

A reusable component-level dependency such as a database, queue, topic, storage bucket, or consumed cloud offering. A Service's component view can show Resources alongside Hosts. Services can include resources in components, and typed relationships can originate at a Service or a particular Host. A resource can be referenced by several services, and membership does not imply exclusive ownership. Including it in a service does not automatically provision it or select it into a DeploymentUnit.

Host describes a runnable process; Resource describes a capability consumed by components. The user accepted a many-to-many provides relationship: a Host can provide zero or more Resources, and a Resource can have zero or more providing Hosts. This can describe a resource provided by several database-cluster processes without requiring a known Host for a managed cloud resource. Providing a resource is a reference relationship and does not transfer subject ownership. Exact serialized fields, the authored source of the relationship, and whether inverse navigation is derived remain open. Shared capabilities and presentation can be expressed through interfaces; Resource-to-Service inheritance has not been chosen.

The user confirmed that a Service can reference a database Host directly through componentSources. Resource is optional when that Host reference provides enough detail. For example, a PostgreSQL process can be a Host, and individual databases it exposes can be Resources when those distinctions matter. Neither an intermediate Resource nor a complete provider model is required. Component membership alone does not require deploying that Host together with the application's other Hosts; DeploymentUnit selections determine that grouping.

The sample resources are logical definitions independent of environments. Deployments can bind these logical definitions to concrete environment-specific resources while retaining their logical identities. Exact instance/mapping records remain open. Do not imply resource provisioning or a particular deployment lifecycle from the presence of a Resource subject.

## Related specifications

[Overview and status legend](../overview.md) · [Platform readiness](../implementation-readiness.md)
