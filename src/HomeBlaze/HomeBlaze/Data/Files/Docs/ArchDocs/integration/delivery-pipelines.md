---
title: Release and Deployment Integration
navTitle: Releases and Pipelines
status: Planned
---

# Release and Deployment Integration [Proposed]

Connect architecture documentation to the systems that build, release, and deploy the software. Keep the domain reusable across GitHub Actions and other pipeline providers. This is a proposed extension direction, not an implemented connector or a selected MVP requirement.

## Implementation readiness

**Area:** ArchDocs, including provider adapters and subject-specific presentation. Reuse existing HomeBlaze configuration, metadata, and UI entry points; generic host changes are separate when needed.

**Delivery:** Optional provider integration with explicit mappings to the architecture model. SourceRepository remains usable with local folders and no Git hosting provider.

**Prerequisites:** A provider connection, stable external identities, explicit architecture references, observed-state ownership/provenance, and a selected synchronization scope. Whether to expose remote control operations is still open.

## Concepts and mappings [Proposed]

| Concept or external record | Architecture connection | Meaning |
|---|---|---|
| Pipeline definition | [SourceRepository](../subjects/source-repository.md), [DeploymentUnit](../subjects/deployment-unit.md), or related documentation | A reusable build/release/deployment workflow. A workflow can serve several units and environments. |
| Pipeline run | Pipeline definition and affected units/installation targets | One execution with its own provider identity, source revision, outcome, timestamps, and log links. |
| Release | [Artifact](../subjects/artifact.md), source revision, and related units | A named/versioned delivery outcome or set of artifacts; it is distinct from an installation in an environment. |
| Deployment attempt or provider deployment record | [Deployment](../subjects/deployment.md), [Environment](../subjects/environment.md), and DeploymentUnit | Evidence about a deployment request/execution targeting an intended installation. Many attempts can relate to the same enduring Deployment. |

Pipeline, run, and release are proposed integration concepts. Whether each becomes a first-class subject, a shared capability on provider subjects, or an observed record remains to be refined. They are not yet additional settled core subject types. Provider connection/credential configuration belongs to the integration; domain configuration references that connection and its published records.

Keep mappings explicit. A workflow's name or a folder name must not silently determine its Service or DeploymentUnit. Provider environment names can map to plain [Identifiers](../shared/identifiers.md), generic subject links, or typed Environment subjects; typed installation behavior requires the compatible domain targets. A single workflow can deploy several units, and a unit can use different workflows for different environments.

## GitHub Actions adapter [Proposed]

GitHub exposes separate APIs for [workflow definitions](https://docs.github.com/en/rest/actions/workflows), [workflow runs](https://docs.github.com/en/rest/actions/workflow-runs), [releases](https://docs.github.com/en/rest/releases/releases), and [deployment records/statuses](https://docs.github.com/en/rest/deployments/deployments). An adapter can map those provider objects onto the concepts above while retaining their original identities, URLs, and provenance. The integration must state which records it actually observes; a repository need not publish every category.

A GitHub deployment record describes a request to deploy a ref and has its own statuses. It is not the same identity or lifecycle as ArchDocs' enduring intended Deployment. A successful build or published release does not establish installation or health. Even a reported successful deployment is provider evidence; retain the source and freshness rather than treating it as a runtime health measurement.

Proposed useful presentation includes linked workflows, recent runs, release/artifact versions, deployment attempts per environment/unit, diagnostics, and links to provider logs. Reuse the Service, DeploymentUnit, Environment, and Deployment detail views and general matrices where the data applies.

## File-authored binding example [Proposed]

Assume a provider connection publishes a pipeline subject at the illustrative `/integrations/Delivery/pipelines/OrdersRelease` route. A DeploymentUnit's existing metadata sidecar can identify that pipeline through the shared relationship model:

```json
{
  "relationships": [
    {
      "kind": "releasedBy",
      "target": "/integrations/Delivery/pipelines/OrdersRelease"
    }
  ]
}
```

The relationship kind and route are proposed. Environment/job/output mappings need a richer binding shape when one relation is insufficient; its schema and canonical owner remain open. Keep authored intent and mappings in their existing owner files, and provider-derived status/history in connector-owned state. Synchronization must not overwrite authored versions or claim that a new intended installation exists merely because a remote run was observed.

## Scope choices to refine

- **Linking:** reference workflows and provider pages without synchronizing their state.
- **Observation:** read run, release, and deployment information through a connector, using polling or provider events as selected later.
- **Control:** optionally expose explicit start, re-run, cancel, or release/deploy operations where the provider supports them. Exact operations, permissions, workflow inputs, and approval behavior need separate design; selecting a pipeline or opening documentation does not invoke it.

Also settle external-to-domain identity mappings, run/history retention, artifact/version correlation, stale/unavailable provider behavior, and ownership of observed records. Provider adapters should share reusable concepts while keeping provider-specific configuration and capabilities explicit.

[DeploymentUnit](../subjects/deployment-unit.md) · [Deployment](../subjects/deployment.md) · [Metadata and relationships](../shared/metadata-and-relationships.md) · [Readiness](../implementation-readiness.md)
