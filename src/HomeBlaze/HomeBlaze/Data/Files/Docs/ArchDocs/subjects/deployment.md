---
title: Deployment
navTitle: Deployment
status: Planned
---

# Deployment [Planned]

## Class definition [Planned]

**Deployment** is an [Environment](environment.md)-owned generated subject class representing an enduring intended installation of one [DeploymentUnit](deployment-unit.md). Its stable identity is the environment/unit pair, rather than a rollout event. Generated identity and lifecycle are state; supported manual enrichment is authored through the Environment metadata writer.

Authored enrichment and relationships use the [existing metadata envelope](../shared/metadata-and-relationships.md), through the defining owner. They are not a second configuration store. Names in the tables are proposed serialized camelCase names; concrete CLR signatures remain open. State properties are read-only to authors.

## Configuration properties [Proposed schema]

| Property | Type | Meaning and source |
|---|---|---|
| None independently persisted | Not applicable | The Environment owns selection and generated descendants. Versions, URLs, and proposed bindings use the existing owner metadata route rather than a second Deployment definition. |

## State properties [Proposed schema]

| Property | Type | Meaning and source |
|---|---|---|
| `environment` | Reference to [Environment](environment.md) | Canonical owner and intended installation context. |
| `deploymentUnit` | Reference to [DeploymentUnit](deployment-unit.md) | Selected release unit; the pair with Environment establishes generated identity. |
| `components` | Reference collection of [Component](component.md) | Current release participants from the selected unit, without transferring their ownership. |
| `version` | `string?` | Intended version from typed owner metadata; omitted means unknown. Metadata remains the editable source. |
| `urls` | `string` collection | Relevant manually supplied access links from owner metadata; absent or empty provides no known links. |
| `endpoints` | Reference collection of [Endpoint](endpoint.md) | Related environment access bindings; canonical binding ownership/persistence remains open. |
| `resourceMappings` | Binding records referencing [Resource](resource.md) | Proposed intended concrete resource mappings, without replacing logical Resource identity; record schema remains open. |
| `observedStatus` | Optional observation record | Read-only observation only when an integration supplies evidence; otherwise unavailable. |
| `diagnostics` | Diagnostic collection | Selection, enrichment, or binding validation issues. |

Typed metadata accessors expose intended enrichment as readable subject state; they do not make it discovered or observed data. Component-specific version bindings, endpoint bindings, and resource mapping records still require a chosen schema.

## Implementation readiness

**Delivery:** Can be implemented through existing subject and component extension points. The ArchDocs feature itself is not implemented.

**Prerequisites:** Environment ownership, DeploymentUnit references, reconciliation, and enrichment through the existing metadata mechanism.

## UI [Planned]

**Summary widget:** Environment/DeploymentUnit identity, intended version and access details when supplied, plus distinct observed status only when an integration provides it.

**Detail page:** selected release components, Endpoint addresses, Resource mappings, provenance, diagnostics, and navigation to unit/environment subjects.

**Editing:** manual enrichment through the Environment's metadata writer and supported binding configuration. Do not create an independently persisted duplicate of the generated Deployment.

The general [DeploymentMatrix](../widgets/deployment-matrix.md) is a separate aggregating widget. Subject summary/detail components do not require page-context slots or a monitoring connector.

## Detailed behavior [Planned]

An enduring installation of a DeploymentUnit in an Environment, not an individual rollout event. Its version, URLs, and later observed state can change over time. The common case is one deployment per unit per environment; the model can be extended with installation identity if several installations of one unit in an environment are needed later.

Environment maintains one generated Deployment subject per selected unit. It reuses that subject while the environment/unit identity remains selected. It removes the generated subject when the pair is no longer selected. This maintains the current configured installations, not deployment history.

A generated deployment expresses the intended installation. It is not evidence that software is actually running. For the MVP, version and relevant URLs are manually configured data; no health or deployment connector populates them.

Use the existing metadata system for manually entered enrichment of generated subjects. The metadata of an environment stores entries for its Deployment descendants, keyed by their stable subject paths: in the sidecar of a JSON definition or the frontmatter of a Markdown definition. Version and URLs can use domain-specific metadata keys with typed accessors and editing support; notes, links, and tags use the existing keys. Configuration declares deployment-unit selection, and metadata holds enrichment. Do not create a second enrichment store inside the owner's configuration.

Deployment-specific bindings can supply component versions, endpoint addresses, and concrete resource mappings without replacing the shared logical component identities. The user accepted this distinction; exact binding subjects/records, authored versus observed fields, and persistence are still open. A configured mapping remains intended data until observed state supports it.

## Related specifications

[Overview and status legend](../overview.md) · [Platform readiness](../implementation-readiness.md)
