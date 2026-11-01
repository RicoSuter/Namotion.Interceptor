---
title: Actor
navTitle: Actor
status: Planned
---

# Actor [Planned]

## Class definition [Planned]

**Actor** is an optional proposed subject class representing a person or role interacting with a system in architectural views. It is authored by its defining file; it is not a user account. No [Component](component.md), composition, or deployment capability is selected. The following fields are proposals for this optional concept, not an approved concrete schema.

Authored enrichment and relationships use the [existing metadata envelope](../shared/metadata-and-relationships.md), through the defining owner. They are not a second configuration store. Names in the tables are proposed serialized camelCase names; concrete CLR signatures remain open. State properties are read-only to authors.

## Configuration properties [Proposed schema]

| Property | Type | Meaning and source |
|---|---|---|
| `name` | `string` | Readable person or role name; proposed required field if Actor is selected. |
| `kind` | [Identifier](../shared/identifiers.md) (optional) | Optional extensible distinction such as person or role; omitted means unspecified. |

Descriptions, notes, links, tags, and relationship records remain authored metadata. Identity, allowed kind values, and relationship endpoint rules remain open.

## State properties [Proposed schema]

| Property | Type | Meaning and source |
|---|---|---|
| `diagnostics` | Diagnostic collection | Invalid authored fields or unresolved relationship targets; empty after successful validation with no issues. |
| `relationships` | Relationship references to [Service](service.md) and other supported subjects | Read-only architectural interactions resolved from the existing metadata records; endpoint validation remains proposed. |

## Implementation readiness

**Delivery:** Can be implemented through existing subject and component extension points. The ArchDocs feature itself is not implemented.

**Prerequisites:** Optional later domain addition; no MVP inclusion or concrete schema selected.

## Direction [Proposed]

Actors can participate in typed relationships and context views. They are not user accounts and do not require organizational directories or authentication integrations. An enterprise software system remains a Service.

## Open decisions [Proposed]

Choose representation, identity, relationship endpoints, and view presentation. Actor need not implement component composition or deployment capabilities. Internal/external and diagram membership remain view-owned.

## UI [Planned]

If this optional subject is selected, its compact widget and detail page can show the chosen role/person description and architectural interactions. Reuse that presentation in context diagrams. Editing remains within the eventual Actor schema; no account/directory integration or deployment behavior is implied.

## Related specifications

[Overview and status legend](../overview.md) · [Platform readiness](../implementation-readiness.md)
