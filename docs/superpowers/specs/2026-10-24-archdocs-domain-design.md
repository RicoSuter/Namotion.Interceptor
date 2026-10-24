---
title: ArchDocs Working Domain Specification
navTitle: ArchDocs Domain Specification
---

# ArchDocs Working Domain Specification

Status: working specification under active discussion. This is the design record for intent, approaches, acceptance candidates, and scope decisions. The [ArchDocs element specifications](../../../src/HomeBlaze/HomeBlaze/Data/Files/Docs/ArchDocs/overview.md) are the canonical reference for individual subjects, analyzers, shared behavior, widgets, and implementation readiness. The [working sample](../../../src/HomeBlaze/HomeBlaze/Data/Files/ArchDocs/README.md) supplies concrete Markdown and JSON files for refining those contracts. It is not a finalized schema or implementation plan. The latest user direction is to refine the sample first, then select the MVP and plan implementation. Earlier first-iteration boundaries remain candidates until that review. Choices labeled "Proposed default" are recommendations, not decisions made by the user; unresolved choices are listed at the end.

Implementation plans, once the design and MVP are approved, belong in `docs/superpowers/plans/`. No implementation plan has been written yet.

## Intent

Make a documentation folder describe and explore the software around it. A repository's `/docs` folder contains ordinary Markdown, Markdown with embedded subject definitions, and authored subject JSON files. Source analysis discovers the code structure; authored subjects describe the logical architecture and deployment arrangement. The same graph is available to the subject browser, Markdown widgets, and queries.

This specification and working sample continue [PR #257, HomeBlaze: ArchDocs](https://github.com/RicoSuter/Namotion.Interceptor/pull/257), replacing its original 200-line design with the current domain discussion. They refine entity boundaries and add repository analysis and path queries. The PR contains documentation, proposed configuration, and source fixtures; it does not implement ArchDocs product behavior.

### Reference use cases: AppHost documentation and Apimundo

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

## Goals

- Support several source repositories without assuming one service per repository.
- Discover as much factual information as the supported analyzers can establish, with authored enrichment in JSON and the UI.
- Describe services independently of source layout, language, process count, and deployment topology.
- Select discovered subjects by scope, kind, name, and other properties instead of maintaining lists of every discovered item.
- Keep generated subjects stable across successful refreshes and preserve authored enrichment.
- Reuse existing storage, registry, expressions, lifecycle, metadata, and subject UI capabilities where they fit.
- Provide rich previews, contextual exploration, and useful service and deployment views while keeping Markdown portable and UI configuration out of authored metadata.

## Candidate first-iteration exclusions

- Cloning, fetching, checking out, or updating Git repositories.
- Namespace, type, method, or call-graph analysis.
- Automatic inclusion of a selected element's transitive dependencies.
- GitHub workflow, release, registry, or deployment synchronization.
- Health polling, actual deployment execution, or individual rollout history.
- A configurable projection entity or a general transformation language.
- Shared storage connections with separately configurable subject-tree views.

An analyzer extension can add additional discovery later. These are the earlier proposed exclusions, retained for choosing the first implementation after the sample and domain review; they do not constrain the full domain vocabulary.

## Approaches

### Host discovery and architectural membership

Three approaches were considered: declaring each host and enriching it; having each service scan its source; and having source repositories discover the code graph while services select from it. The chosen approach is repository-owned discovery. It handles shared projects, monorepos, and multiple repositories without repeated per-service discovery. Services express architectural intent rather than own independent copies of discovered hosts.

Proposed default: allow manual descriptions of hosts whose source is unavailable. Their exact authoring workflow is outside the repository-discovery slice.

### Storage access

The chosen MVP approach gives SourceRepository its own file access independently of the documentation mount. The alternative is to split the current storage implementation into a shared storage connection and separately mounted views. The latter is more general but is deferred. A hidden storage subject is insufficient because navigation visibility does not prevent scanning and subject creation.

### Generated subjects

A configurable SubjectProjection was considered. The chosen approach puts generation in the owning subject, using lifecycle and relevant change notifications to drive reconciliation. Shared infrastructure maintains keyed children without introducing another user-facing entity or query-to-object transformation language.

### Selector syntax

Structured selectors, named selections, and compact query syntax were discussed. The latest direction uses subject paths with `*` and `**` plus optional property filters, without a separate named-selection concept. Proposed default: configuration collections contain entries with `path` and optional `where`, reusing the existing expression parser for conditions. Keep source definitions distinct from resolved subject collections, illustratively `componentSources` versus `components` for services and deployment units, and `deploymentUnitSources` versus `deploymentUnits` for environments. These names remain proposed API choices. The [self-contained working sample](../../../src/HomeBlaze/HomeBlaze/Data/Files/ArchDocs/README.md) is the current place to refine the file structure and schema before finalizing this design or its MVP.

### Markdown defines domain subjects

The alternatives considered were making the document itself a Service, giving a specialized MarkdownFile subclass a Service property, using existing inline subject blocks only, and defining named children generically in frontmatter. The user selected generic named frontmatter children: `$service` defines a child named `service`, `$environment` defines one named `environment`, and each value contains a `$type` plus normal subject configuration. A document defines subjects rather than becoming one of those domain types. This supersedes both service-as-MarkdownFile and ServiceMarkdownFile wrapper designs. Compared with existing inline blocks, the benefit is defining a subject outside the authored body without placing its widget there. It is an additional authoring location, not a different domain model.

## Markdown subject definitions and metadata

Detailed behavior is maintained in [Markdown subject authoring](../../../src/HomeBlaze/HomeBlaze/Data/Files/Docs/ArchDocs/integration/markdown-authoring.md); [Metadata and relationships](../../../src/HomeBlaze/HomeBlaze/Data/Files/Docs/ArchDocs/shared/metadata-and-relationships.md).

## Page UI extension slots

Detailed behavior is maintained in [Page contributions and link previews](../../../src/HomeBlaze/HomeBlaze/Data/Files/Docs/ArchDocs/integration/page-contributions.md); [Architecture widgets](../../../src/HomeBlaze/HomeBlaze/Data/Files/Docs/ArchDocs/widgets/overview.md).

## Current data-model reference

Detailed behavior is maintained in [Subject specifications and status](../../../src/HomeBlaze/HomeBlaze/Data/Files/Docs/ArchDocs/overview.md); [Component capabilities](../../../src/HomeBlaze/HomeBlaze/Data/Files/Docs/ArchDocs/subjects/component.md).

## Domain

Detailed behavior is maintained in [Subject specifications](../../../src/HomeBlaze/HomeBlaze/Data/Files/Docs/ArchDocs/overview.md).

## Path queries and relationships

Detailed behavior is maintained in [Path queries](../../../src/HomeBlaze/HomeBlaze/Data/Files/Docs/ArchDocs/shared/path-queries.md); [Metadata and relationships](../../../src/HomeBlaze/HomeBlaze/Data/Files/Docs/ArchDocs/shared/metadata-and-relationships.md).

## Source access and documentation mounts

Detailed behavior is maintained in [SourceRepository](../../../src/HomeBlaze/HomeBlaze/Data/Files/Docs/ArchDocs/subjects/source-repository.md).

## Analyzer extension point and initial .NET analysis

Detailed behavior is maintained in [Analyzer extension model](../../../src/HomeBlaze/HomeBlaze/Data/Files/Docs/ArchDocs/analyzers/overview.md); [C# project analyzer](../../../src/HomeBlaze/HomeBlaze/Data/Files/Docs/ArchDocs/analyzers/csharpanalyzer.md).

## Generated child lifecycle

Detailed behavior is maintained in [Generated subject reconciliation](../../../src/HomeBlaze/HomeBlaze/Data/Files/Docs/ArchDocs/shared/reconciliation.md).

## Authored data and discovered state

Detailed behavior is maintained in [Metadata and relationships](../../../src/HomeBlaze/HomeBlaze/Data/Files/Docs/ArchDocs/shared/metadata-and-relationships.md); [Architecture widgets](../../../src/HomeBlaze/HomeBlaze/Data/Files/Docs/ArchDocs/widgets/overview.md).

## Candidate acceptance scenarios

These scenarios capture intended behavior for the evolving capability. They are not a decision to include every scenario in the first implementation; select that boundary after reviewing the working sample.

1. Mount only a repository's documentation folder, configure a source root relative to a JSON file there, and analyze the surrounding checkout without mounting every source file as a subject.
2. Configure two repositories; discover their project-level elements without duplicating shared elements within one repository's graph.
3. Define a service using path-query entries selecting its repository-owned components, and query library projects matching `*.Test` through paths and filters without introducing named-selection entities.
4. Change the source or selectors and refresh; preserve unchanged generated subjects and authored enrichment while updating membership and project-reference edges.
5. Show an external code dependency without adding its target to the service's membership or asserting runtime communication.
6. Define a deployment unit selecting hosts from more than one service, and an environment selecting that unit; obtain exactly one generated Deployment for the pair.
7. Edit a deployment's manual version and URLs through the existing metadata store with domain-specific editing support; retain them across reconciliation and restart.
8. Remove or change an environment's unit selection while the unit stays attached elsewhere; update generated Deployments correctly.
9. Surface malformed selectors, unresolved references, uncertain analysis, and failed source access distinctly; retain the last successful graph on temporary analysis failure.
10. Detach an owner during queued work; prevent obsolete publication and release its subscriptions and generated children according to the normal lifecycle rules.
11. Embed a service summary and deployment matrix in Markdown using normal widget blocks; navigate from their entries to the existing subjects without maintaining duplicate service facts in the page.
12. Define independent Service and Environment children through `$service` and `$environment` in ordinary Markdown frontmatter; instantiate their nested `$type` declarations without a specialized page type, retain stable named identities through refresh, edit configuration through the owning file, and embed an optional summary without recursive rendering. Diagnose collisions with named inline blocks rather than silently choose a definition.
13. Edit page and child-scoped metadata and authored relationships through the metadata API; preserve the body and unrelated frontmatter, keep document and service relationship sources distinct, reapply descendant enrichment on later attach, and retain that data across refresh and restart.
14. Open an ordinary Markdown document with relationship metadata and no blocks or `$type`; expose applicable subject information through page UI slots while leaving its source and normal Markdown rendering intact.
15. Follow an ordinary Markdown link to a resolvable subject; offer a rich preview with title, subtitle, description, applicable facts, and navigation while retaining standard Markdown source and normal link navigation.
16. Explore service membership, dependencies, and deployments through the context slot and summary, and navigate from deployment-matrix entries to their units, environments, and deployments. Surface unavailable or uncertain facts explicitly.
17. Author the same Service type using standalone JSON or a Markdown child definition with the same configuration schema; after manually moving a definition, its enrichment, and updating references, keep the original document as documentation of the standalone subject.
18. Place a ServiceTable in an overview document, scoped to a project subtree; find nested services defined through JSON, frontmatter, and inline blocks, show each once with useful summaries and navigation, and update rows when subjects enter or leave the scope without rerunning source analysis.
19. Include service-specific and shared Resources through component-source paths; retain one shared resource identity and show typed API, database, and messaging relationships from services or explicit hosts.
20. Use the same widget configuration in an ordinary service page and an arc42 document; render a service overview and a detailed host/resource view from the same graph, with explicit scope and preserved relationship endpoints.
21. Place referenced service widgets on a manual canvas; keep layout local to that document and semantic connections in shared relationship metadata. Existing canvas limitations remain until connector support is implemented.
22. Open a JSON-defined Resource or DeploymentUnit through useful detail presentation and reuse its compact presentation in previews, context panels, and diagram boxes. This is the proposed page direction, subject to the open presentation-interface decision.
23. Display Payments inside the Commerce context and outside the Orders-only context using explicit per-view path sources; retain the same Service identity and relationships without writing an internal/external property to it.
24. Reference a Contract from a library CodeProject to describe its public API without creating a Host, network Interface, or Endpoint; distinguish authored content from analyzer-derived snapshots.
25. Expose an Interface from an appropriate subject, reference its Contract, and bind it to different Endpoints in different environments while retaining the logical Interface identity.
26. Select a Resource definition/configuration as an explicit release participant alongside Hosts; keep dependency-only references separate and do not imply provisioning or observed installation.

These scenarios describe future implementation and testing. The working sample contains fixture projects and proposed data definitions, but no ArchDocs product implementation has been authorized by this specification.

## Constraints and builder choices

Follow the repository's subject-creation guidance and existing conventions: partial intercepted properties, configuration versus state, internal setters for owned state, dictionaries for stable discovered identity, replaced published collections, headless domain code, and separate UI code. Reuse title, icon, monitoring, last-updated, path-reference, and metadata interfaces where applicable.

The builder chooses exact project names, concrete interfaces, JSON property names, worker primitives, caching strategy, and the smallest reusable helper API that satisfies the contracts above. A proposed Namotion.ArchDocs name is inherited from PR #257, not a new user decision. An implementation plan must identify required public API changes, tests for selector and lifecycle correctness, and targeted UI tests for page contributions, link previews, and widgets. Long-running benchmarks or connector verification require agreement before they are scheduled; neither is implied by this design.

## Open decisions and review status

The element specifications and sample files should be updated together as the model changes. This design record links their canonical detailed behavior and retains cross-cutting scope decisions; the sample README explains its concrete arrangement. Earlier builder checks found no remaining planning questions for the narrower design at that point. Subsequent resources, diagrams, page presentation, and boundary discussion extend it, so that result is not approval of the current broader specification.

The following decisions remain open:

- Context representation: internal/external is confirmed as diagram-relative; exact boundary fields, rendering behavior, optional neighbor discovery, and actor representation remain open. Neighboring software systems use Service.
- Service and Resource roles: Service represents a logical system/capability; Resources such as databases and cloud offerings appear alongside Hosts at component level. Host-to-Resource provides references are optional and many-to-many. Define shared interfaces and the exact persistence/navigation of provider relationships without assuming inheritance.
- Deployment participation: component-based membership is accepted; settle participant eligibility and representation of resource definitions/configuration versus dependencies on existing managed resources.
- Environment bindings: component versions, endpoint addresses, and concrete resource mappings are accepted; settle exact binding records, infrastructure references, ownership, and authored versus observed state.
- Interfaces, Endpoints, and Contracts: settle exposure/reference schemas, ownership, supported formats, source-document references, and version/snapshot identity. CodeProject public API Contracts do not require network Interfaces or Endpoints.
- Optional domain additions: refine generic Artifact and Actor models separately from the sample products, and decide their MVP inclusion.
- Executable roles: settle extensible execution-behavior and role fields for runnable process definitions; sample-specific subclasses are unnecessary.
- Presentation contracts: useful subject detail pages, navigation participation, shared page/widget/preview components, and exact UI slot registration.
- Final schemas and identity: CLR/serialized names, path casing and aliases, generated keys, persistent identity and rename behavior, and raw-source path matching where graph paths are insufficient.
- View configuration: generic diagram presets and customization, boundary selection, grouping shared components, manual/automatic layout interaction, and semantic connector editing.
- Runtime scenarios: plain authored Markdown suffices for the current example; a structured scenario/sequence model is not selected.
- Initial implementation: which candidate behaviors, exclusions, and UI surfaces form the MVP after sample review, including any long-running verification that must be agreed during planning.

Useful rich UI, format-independent widgets, ordinary Markdown authoring, named frontmatter children, generic path queries, repository-owned discovery, and Environment-owned deployments are the current direction. Remaining open decisions must not be presented as implemented or user-approved schema. Finalize the domain and MVP, obtain design approval, and only then produce the implementation plan.
