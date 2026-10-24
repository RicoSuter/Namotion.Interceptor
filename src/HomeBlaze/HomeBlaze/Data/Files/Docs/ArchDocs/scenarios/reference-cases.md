---
title: Reference Cases
navTitle: Reference Cases
status: Planned
---

# Reference Cases [Planned]

These cases exercise the generic model and are not required folder structures, entity hierarchies, or platform choices. The observations are dated reference material, not a statement that every observed capability is implemented in ArchDocs.

The user identified AppHost as the first real documentation target and the [Apimundo project site](https://apimundo.com/organizations/namotion/projects/apimundo?service=1&tab=overview&repository=Apimundo) as the domain and exploration experience to rebuild. These guide the broader design; they do not imply that all observed features belong in the MVP or that implementation has been authorized.

AppHost already has ordinary Markdown documentation: one arc42 architecture overview, crosscutting concept documents, ADRs, product decision records, guidelines, and an Orders sample domain document. Its architecture describes one coordinated application/release containing Web and Worker processes, shared libraries, a migrations executable, and resources. Orders is a module inside that application, not an independently deployed Service. Keep those existing document roles and enrich them with subject definitions, metadata, and optional views. The two-service Commerce fixture remains useful for a different topology.

The inspected Aspire composition defines PostgreSQL with an appdb database, RabbitMQ, migrations, Web, and Worker. Its local orchestration project is itself executable; migrations run to completion before Web and Worker start. A runnable project is therefore not sufficient evidence of an always-running production Host or deployment membership. Generic execution behavior and descriptive roles accommodate these differences without sample-specific types. The documentation also distinguishes current local resources from proposed Azure topology and future features; these statuses must remain explicit rather than present proposed infrastructure as discovered deployed state.

The Apimundo service and project views were inspected on 2026-10-24. The following capabilities inform the domain without fixing their replacement schemas:

| Observed capability | Current model fit or gap |
|---|---|
| Project/service overview and Markdown descriptions | Scoped documentation and Service summaries; organization/project boundaries need not become mandatory graph roots |
| Repository code-project dependency graph | SourceRepository and CodeProject discovery, shared references, and graph presentation |
| Component/artifact inventory | Service, Host, and Resource cover part of this; people/actors and their relationships remain open |
| Service-by-environment matrix, deployment detail, version and status | Environment, DeploymentUnit, and Deployment; observed health and monitoring integrations remain separate future choices |
| Logical API, environment-specific endpoint, and versioned specification | Interface, Endpoint, and reusable Contract are now accepted concepts; exact schema, ownership, and version representation remain open |
| API operations, schemas, specification view, and version selection | Contract parsing and exploration extend beyond the simple project-file analyzer; MVP inclusion is undecided |
| SDK package references and feeds | Package/artifact identities, versions, and their links to services/projects remain to be designed |

The goal is to make these connected views available through ordinary documentation and the shared subject graph. Rebuilding the reference domain does not require copying its fixed navigation, its global Internal/External taxonomy, or its exact entity hierarchy. The agreed diagram-relative boundary model remains in force.

[Scenarios](overview.md) · [Scope and decisions](../scope-and-decisions.md)
