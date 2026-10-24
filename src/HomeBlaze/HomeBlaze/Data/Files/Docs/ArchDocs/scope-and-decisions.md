---
title: Scope and Design Decisions
navTitle: Scope and Design Decisions
status: Planned
---

# Scope and Design Decisions [Planned]

This feature reference is the durable specification. It contains the domain contracts, rationale, examples, verification scenarios, implementation status, and unresolved choices. Planning/session artifacts may reference it and can be discarded without losing feature requirements. AppHost and Apimundo are examples used to challenge the generic model; neither defines its required hierarchy or deployment platform.

## Goals

- Keep authored Markdown, JSON, metadata, type identifiers, links, selections, and contract references understandable and easy to edit directly by humans and AI agents. The [file-authoring requirement](file-authoring.md) applies to every subject and view.

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

## Design decisions and alternatives

### Host discovery and architectural membership

Three approaches were considered: declaring each host and enriching it; having each service scan its source; and having source repositories discover the code graph while services select from it. The chosen approach is repository-owned discovery. It handles shared projects, monorepos, and multiple repositories without repeated per-service discovery. Services express architectural intent rather than own independent copies of discovered hosts.

Proposed default: allow manual descriptions of hosts whose source is unavailable. Their exact authoring workflow is outside the repository-discovery slice.

### Storage access

The chosen MVP approach gives SourceRepository its own file access independently of the documentation mount. The alternative is to split the current storage implementation into a shared storage connection and separately mounted views. The latter is more general but is deferred. A hidden storage subject is insufficient because navigation visibility does not prevent scanning and subject creation.

### Generated subjects

A configurable SubjectProjection was considered. The chosen approach puts generation in the owning subject, using lifecycle and relevant change notifications to drive reconciliation. Shared infrastructure maintains keyed children without introducing another user-facing entity or query-to-object transformation language.

### Selector syntax

Structured selectors, named selections, and compact query syntax were discussed. The latest direction uses subject paths with `*` and `**` plus optional property filters, without a separate named-selection concept. Proposed default: configuration collections contain entries with `path` and optional `where`, reusing the existing expression parser for conditions. Keep source definitions distinct from resolved subject collections, illustratively `componentSources` versus `components` for services and deployment units, and `deploymentUnitSources` versus `deploymentUnits` for environments. These names remain proposed API choices. The [self-contained working sample](../../ArchDocs/README.md) is the current place to refine the file structure and schema before finalizing this design or its MVP.

### Markdown defines domain subjects

The alternatives considered were making the document itself a Service, giving a specialized MarkdownFile subclass a Service property, using existing inline subject blocks only, and defining named children generically in frontmatter. The user selected generic named frontmatter children: `$service` defines a child named `service`, `$environment` defines one named `environment`, and each value contains a `$type` plus normal subject configuration. A document defines subjects rather than becoming one of those domain types. This supersedes both service-as-MarkdownFile and ServiceMarkdownFile wrapper designs. Compared with existing inline blocks, the benefit is defining a subject outside the authored body without placing its widget there. It is an additional authoring location, not a different domain model.

## Implementation constraints

Follow the repository's subject-creation guidance and existing conventions: partial intercepted properties, configuration versus state, internal setters for owned state, dictionaries for stable discovered identity, replaced published collections, headless domain code, and separate UI code. Reuse title, icon, monitoring, last-updated, path-reference, and metadata interfaces where applicable.

The builder chooses exact project names, concrete interfaces, JSON property names, worker primitives, caching strategy, and the smallest reusable helper API that satisfies the contracts above. A proposed Namotion.ArchDocs name is inherited from PR #257, not a new user decision. An implementation plan must identify required public API changes, tests for selector and lifecycle correctness, and targeted UI tests for page contributions, link previews, and widgets. Long-running benchmarks or connector verification require agreement before they are scheduled; neither is implied by this design.

## Decisions still open

The following questions are tracked in the relevant element specification; this list provides cross-cutting scope context. A status of [Planned] does not settle these schemas or select an MVP.

- Context representation: internal/external is confirmed as diagram-relative; exact boundary fields, rendering behavior, optional neighbor discovery, and actor representation remain open. Neighboring software systems use Service.
- Service and Resource roles: Service represents a logical system/capability; Resources such as databases and cloud offerings appear alongside Hosts at component level. Host-to-Resource provides references are optional and many-to-many. Define shared interfaces and the exact persistence/navigation of provider relationships without assuming inheritance.
- Deployment participation: component-based membership is accepted; settle participant eligibility and representation of resource definitions/configuration versus dependencies on existing managed resources.
- Environment bindings: component versions, endpoint addresses, and concrete resource mappings are accepted; settle exact binding records, infrastructure references, ownership, and authored versus observed state.
- Interfaces, Endpoints, and Contracts: settle exposure/reference schemas, ownership, supported formats, source-document references, and version/snapshot identity. CodeProject public API Contracts do not require network Interfaces or Endpoints.
- Optional domain additions: refine generic Artifact and Actor models separately from the sample products, and decide their MVP inclusion.
- Executable roles: settle extensible execution-behavior and role fields for runnable process definitions; sample-specific subclasses are unnecessary.
- Presentation contracts: useful subject detail pages, navigation participation, shared page/widget/preview components, and exact UI slot registration.
- Final schemas and identity: CLR/serialized names, path casing and aliases, generated keys, persistent identity and rename behavior, and raw-source path matching where graph paths are insufficient.
- File ergonomics: evaluate readable type identifiers, reference/metadata syntax, complete minimal examples, schema tooling, source-located diagnostics, and focused writeback against the file-authoring requirement. Existing fully qualified type names and runtime-shaped scoped metadata keys need explicit review before finalizing the authoring schema.
- View configuration: generic diagram presets and customization, boundary selection, grouping shared components, manual/automatic layout interaction, and semantic connector editing.
- Runtime scenarios: plain authored Markdown suffices for the current example; a structured scenario/sequence model is not selected.
- Initial implementation: which candidate behaviors, exclusions, and UI surfaces form the MVP after sample review, including any long-running verification that must be agreed during planning.

## Related references

[Overview](overview.md) · [Readiness matrix](implementation-readiness.md) · [Usage examples](examples/overview.md) · [Verification scenarios](verification-scenarios.md)
