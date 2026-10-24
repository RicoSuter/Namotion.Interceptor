---
title: Deployment
navTitle: Deployment
status: Planned
---

# Deployment [Planned]

Represents an enduring intended installation of one DeploymentUnit in an Environment.

## Implementation readiness

**Delivery:** Can be implemented through existing subject and component extension points. The ArchDocs feature itself is not implemented.

**Prerequisites:** Environment ownership, DeploymentUnit references, reconciliation, and enrichment through the existing metadata mechanism.

## Data [Planned]

| Generated identity | Authored enrichment or bindings |
|---|---|
| Environment and DeploymentUnit | Versions, relevant URLs, Endpoint addresses, and concrete Resource mappings; exact binding records remain open |

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
