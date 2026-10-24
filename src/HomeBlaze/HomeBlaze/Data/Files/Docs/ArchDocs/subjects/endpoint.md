---
title: Endpoint
navTitle: Endpoint
status: Planned
---

# Endpoint [Planned]

## Class definition [Planned]

**Endpoint** is a proposed subject class supplying environment-specific access details for a logical [Interface](interface.md). It references an [Environment](environment.md) and optionally a [Deployment](deployment.md). Defining owner, persistence, and generated-versus-authored lifecycle remain open; these proposed fields do not choose a second binding store.

Authored enrichment and relationships use the [existing metadata envelope](../shared/metadata-and-relationships.md), through the defining owner. They are not a second configuration store. Names in the tables are proposed serialized camelCase names; concrete CLR signatures remain open. State properties are read-only to authors.

## Configuration properties [Proposed schema]

| Property | Type | Meaning and source |
|---|---|---|
| `interfacePath` | Subject path targeting [Interface](interface.md) | Explicit logical interface reference; proposed required field. |
| `environmentPath` | Subject path targeting [Environment](environment.md) | Explicit environment reference; proposed required field. |
| `deploymentPath` | Optional subject path targeting [Deployment](deployment.md) | Optional installation association; omitted means no known Deployment. |
| `address` | `string?` | Optional protocol-appropriate address; an HTTP URL is not assumed. |
| `connectionBinding` | Protocol-specific binding record | Optional structured access details where an address alone is insufficient; schema, defaults, and persistence remain open. |

When a binding is generated, its source-owned address/details are read-only facts and authored enrichment follows its defining owner. The canonical authored source and supported overrides must be settled before these proposals become a concrete schema.

## State properties [Proposed schema]

| Property | Type | Meaning and source |
|---|---|---|
| `interface` | Reference to [Interface](interface.md) | Resolved logical interface; unresolved input retains a diagnostic. |
| `environment` | Reference to [Environment](environment.md) | Resolved environment context. |
| `deployment` | Optional reference to [Deployment](deployment.md) | Resolved installation when explicitly associated. |
| `contracts` | Reference collection of [Contract](contract.md) | Public surfaces reached through the referenced Interface, not separately owned copies. |
| `provenance` | Provenance records | Authored origin or supporting source for discovered/observed bindings. |
| `observedStatus` | Optional observation record | Evidence-backed status only when provided; a configured address does not establish connectivity or health. |
| `diagnostics` | Diagnostic collection | Unresolved references, incompatible protocols, and binding validation issues. |

## Implementation readiness

**Delivery:** Can be implemented through existing subject and component extension points. The ArchDocs feature itself is not implemented.

**Prerequisites:** Interface and Environment references; an optional Deployment reference and its bindings.

The same Interface can have different Endpoints in different environments. Endpoint describes access; Contract describes the public surface. An address is not evidence of successful access or health. Keep intended and observed data distinguishable.

## Open schema choices [Proposed]

Settle defining owner, persistence, generated-versus-authored lifecycle, protocol-specific bindings, and multiple endpoints in one environment. This page does not assume an HTTP URL is sufficient for every protocol.

## UI [Planned]

**Summary widget:** logical Interface, Environment, optional Deployment, and available address/connection binding.

**Detail page:** access information, related Contract and deployment navigation, provenance, and distinct intended/observed status.

**Editing:** authored binding details through the defining owner after its schema is selected. Displaying a configured address does not establish connectivity or health. Protocol-appropriate bindings are not limited to HTTP URLs.

## Related specifications

[Overview and status legend](../overview.md) · [Platform readiness](../implementation-readiness.md)
