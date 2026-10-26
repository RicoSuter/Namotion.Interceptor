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
| `address` | `string?` | Optional protocol-appropriate address; an HTTP URL is not assumed. |
| `connectionBinding` | Protocol-specific binding record | Optional structured access details where an address alone is insufficient; schema, defaults, and persistence remain open. |
| `interfacePath` | [Identifier](../shared/identifiers.md), expected [Interface](interface.md) | Explicit logical interface identifier; proposed required field. A compatible target enables typed navigation and behavior. |
| `environmentPath` | [Identifier](../shared/identifiers.md), expected [Environment](environment.md) | Explicit environment identifier; proposed required field. Plain text such as production is useful without an Environment subject. |
| `deploymentPath` | Optional [Identifier](../shared/identifiers.md), expected [Deployment](deployment.md) | Optional installation identifier. Omitted means no known installation; plain text or a generic subject link does not establish a typed Deployment association. |

When a binding is generated, its source-owned address/details are read-only facts and authored enrichment follows its defining owner. The canonical authored source and supported overrides must be settled before these proposals become a concrete schema.

Each scalar identifier supports plain text, a generic subject link, or a typed subject with richer UI. Typed state below remains unavailable until a compatible subject resolves. This preserves useful access documentation before domain subjects exist; it does not generate missing Environments or Deployments. The proposed field names ending in Path remain subject to the deferred identifier syntax/naming decision.

## State properties [Proposed schema]

| Property | Type | Meaning and source |
|---|---|---|
| `provenance` | Provenance records | Authored origin or supporting source for discovered/observed bindings. |
| `observedStatus` | Optional observation record | Evidence-backed status only when provided; a configured address does not establish connectivity or health. |
| `diagnostics` | Diagnostic collection | Unresolved references, incompatible protocols, and binding validation issues. |
| `interface` | Optional reference to [Interface](interface.md) | Resolved typed logical interface; a plain identifier or generic link remains independently presentable. |
| `environment` | Optional reference to [Environment](environment.md) | Resolved typed environment context; unavailable for a plain identifier or generic subject link. |
| `deployment` | Optional reference to [Deployment](deployment.md) | Resolved installation when explicitly associated. |
| `contracts` | Reference collection of [Contract](contract.md) | Public surfaces reached through the referenced Interface, not separately owned copies. |

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
