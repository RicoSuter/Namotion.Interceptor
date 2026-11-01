---
title: Component
navTitle: Component
status: Planned
---

# Component [Planned]

## Class definition [Planned]

**Component** is an interface capability for subjects that can participate in architectural composition, not a concrete subject class or a new ownership base. [Service](service.md), [Host](host.md), [Resource](resource.md), and [CodeProject](code-project.md) participate. Exact C# interface names and signatures remain proposed. Membership preserves the concrete subject owner and does not imply execution, deployability, exclusive ownership, or a canonical parent.

Authored enrichment and relationships use the [existing metadata envelope](../shared/metadata-and-relationships.md), through the defining owner. They are not a second configuration store. Names in the tables are proposed serialized camelCase names; concrete CLR signatures remain open. State properties are read-only to authors.

## Configuration properties [Proposed schema]

| Property | Type | Meaning and source |
|---|---|---|
| None declared by this capability | Not applicable | Concrete subjects define their own authored fields; existing display and metadata capabilities are reused. |

## State properties [Proposed schema]

| Property | Type | Meaning and source |
|---|---|---|
| None declared by this capability | Not applicable | Concrete subjects expose applicable resolved collections. Component itself does not require composition, Contracts, Interfaces, or resource provision. |

[Service](service.md) is also a Component, allowing nested logical services. Queries remain usable with subjects outside this capability. [CodeSolution](code-solution.md), [Artifact](artifact.md), and [Actor](actor.md) participation is not selected.

## Implementation readiness

**Delivery:** Can be implemented through existing subject and component extension points. The ArchDocs feature itself is not implemented.

**Prerequisites:** The planned ArchDocs capability model and applicable query, metadata, and lifecycle services. Their delivery order is not an approved implementation plan.

## Capability boundaries [Planned]

Composition, [Contract](contract.md) references, [Interface](interface.md) exposure, and [Resource](resource.md) provision are separate capabilities. Each subject implements only the ones that apply. Existing display, metadata, and path interfaces should be reused rather than copied into a large new base class.

## Invariants [Planned]

- Referencing a component preserves its subject identity and owner.
- Host-to-Resource provision is optional and many-to-many.
- Discovery, logical membership, deployment membership, and diagram scope remain independent.
- Cyclic selector dependencies produce diagnostics; nested services do not imply unrestricted recursive expansion.

## UI [Planned]

Component is a capability, not a requirement to create a standalone component widget. Concrete subjects document their own summary, detail, and edit behavior. Registered components can reuse base/interface fallback and common presentation primitives. Keep UI registrations in UI assemblies, independent of headless domain code.

Compact subject presentation can be reused by aggregating tables, diagram nodes, later link previews, and context contributions. Show only capabilities the subject supplies. Existing IPage provides navigation metadata; a registered Page component still supplies rendering. A document defining a subject remains an ordinary MarkdownFile, and its subject summary must not recursively render the containing document.

## Related specifications

[Overview and status legend](../overview.md) · [Platform readiness](../implementation-readiness.md)
