---
title: Component
navTitle: Component
status: Planned
---

# Component [Planned]

A shared capability for elements that can participate in architectural composition.

## Implementation readiness

**Delivery:** Can be implemented through existing subject and component extension points. The ArchDocs feature itself is not implemented.

**Prerequisites:** The planned ArchDocs capability model and applicable query, metadata, and lifecycle services. Their delivery order is not an approved implementation plan.

## Shape [Planned]

Use a small component interface shared by Service, Host, Resource, and CodeProject. Exact C# names and signatures remain proposed. Component membership does not imply execution, deployability, exclusive ownership, or a canonical parent. Service is also a component, allowing nested logical services. Queries remain usable with subjects that do not implement this capability.

Composition, contract references, interface exposure, and resource provision are separate capabilities. Each subject implements only the ones that apply. Existing display, metadata, and path interfaces should be reused rather than copied into a large new base class.

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
