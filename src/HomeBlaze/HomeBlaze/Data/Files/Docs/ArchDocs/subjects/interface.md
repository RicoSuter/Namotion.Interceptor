---
title: Interface
navTitle: Interface
status: Planned
---

# Interface [Planned]

## Class definition [Planned]

**Interface** is a subject class describing a logical exposed capability and its access mechanism or protocol, independently of an address. It can be exposed by [Services](service.md), [Hosts](host.md), or [Resources](resource.md), reference [Contracts](contract.md), and have environment-specific [Endpoints](endpoint.md). Its defining file or provider owns it; exposure ownership and the canonical authored relationship location remain open.

Authored enrichment and relationships use the [existing metadata envelope](../shared/metadata-and-relationships.md), through the defining owner. They are not a second configuration store. Names in the tables are proposed serialized camelCase names; concrete CLR signatures remain open. State properties are read-only to authors.

## Configuration properties [Proposed schema]

| Property | Type | Meaning and source |
|---|---|---|
| `name` | `string` | Readable logical capability name. |
| `protocol` | [Identifier](../shared/identifiers.md) (optional) | Optional extensible access mechanism; omitted means unspecified and does not default to HTTP. |
| `technology` | [Identifier](../shared/identifiers.md) (optional) | Optional descriptive technology. |
| `contractPaths` | Subject-path collection targeting [Contract](contract.md) | Proposed public-surface references; absent or empty declares no known Contracts. Exact canonical authored association remains open. |

Description and exposure records use authored metadata where that canonical relationship source is selected. Discovered name/protocol/technology facts remain read-only provider state; explicit overrides and the final association schema remain open.

## State properties [Proposed schema]

| Property | Type | Meaning and source |
|---|---|---|
| `provenance` | Provenance records | Defining-file/provider source for authored or discovered facts. |
| `diagnostics` | Diagnostic collection | Unresolved references and exposure/public-surface validation issues. |
| `contracts` | Reference collection of [Contract](contract.md) | Resolved public-surface references; no cloning or ownership transfer. |
| `exposingSubjects` | References to [Service](service.md), [Host](host.md), or [Resource](resource.md) | Subjects exposing this logical capability, resolved from canonical exposure records; cardinalities remain proposed. |
| `endpoints` | Reference collection of [Endpoint](endpoint.md) | Environment-specific access bindings for this Interface. |

## Implementation readiness

**Delivery:** Can be implemented through existing subject and component extension points. The ArchDocs feature itself is not implemented.

**Prerequisites:** Contract references, subject-path resolution, and exposure relationships; contract parsing is optional.

An Interface is independent of a concrete address and can be documented with implementation details unknown. A database protocol or message interface is valid; HTTP is not assumed. A CodeProject can reference a programming API Contract directly without creating an Interface.

## Open schema choices [Proposed]

Choose the exposure relationship and its canonical authored location, shared identity, and cardinalities. Do not duplicate the same Interface to show it from different subjects. Exact field names and ownership remain open.

## UI [Planned]

**Summary widget:** logical interface name, access mechanism/protocol, and Contract references.

**Detail page:** exposing subjects, public-surface Contracts, environment Endpoints, source/provenance, and diagnostics. Show an unknown implementation without fabricating a Host.

**Editing:** the supported logical description and exposure/Contract references once their schemas are selected. Environment addresses belong to Endpoint bindings.

## Related specifications

[Overview and status legend](../overview.md) · [Platform readiness](../implementation-readiness.md)
