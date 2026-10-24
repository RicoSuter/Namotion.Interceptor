---
title: Scope and Design Decisions
navTitle: Scope and Design Decisions
status: Planned
---

# Scope and Design Decisions [Planned]

This feature reference is the durable specification. It contains the domain contracts, rationale, examples, verification scenarios, implementation status, and unresolved choices. Planning/session artifacts may reference it and can be discarded without losing feature requirements. AppHost and Apimundo are examples used to challenge the generic model; neither defines its required hierarchy or deployment platform.

## Goals

- Keep authored Markdown, JSON, metadata, type identifiers, links, selections, and contract references understandable and easy to edit directly by humans and AI agents. The [file-authoring requirement](file-authoring.md) applies to every subject and view. Consulting this reference for syntax and supported capabilities is expected.

- Keep documentation aligned with the codebase and interlinked through discovery, reusable membership rules, generated views, backlinks, and actionable validation, with minimal duplicated authoring.
- Support several source repositories without assuming one service per repository.
- Discover as much factual information as the supported analyzers can establish, with authored enrichment in JSON and the UI.
- Describe services independently of source layout, language, process count, and deployment topology.
- Select discovered subjects by scope, kind, name, and other properties instead of maintaining lists of every discovered item.
- Keep generated subjects stable across successful refreshes and preserve authored enrichment.
- Reuse existing storage, registry, expressions, lifecycle, metadata, and subject UI capabilities where they fit.
- Provide rich previews, contextual exploration, and useful service and deployment views while keeping Markdown portable and UI configuration out of authored metadata.
- Define architecture policies as readable data and run the same validation from HomeBlaze, a local CLI, and CI. Errors should help humans and agents identify the affected subject, edit the canonical documentation/configuration, and rerun the checks.

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

### Shared analyzer subjects

The user selected analyzers as shared singleton subjects that SourceRepositories can select. Proposed configuration `analyzers` is a list of records containing `path` to a shared Analyzer and optional per-repository `config`, keeping the reference and its supported run configuration together. Read-only `resolvedAnalyzers` contains the resolved shared references. This replaces the earlier split selection/options shape and a model where analyzers are only invisible DI services or independent instances created for every repository. Normal registration remains applicable; no additional executable plugin loader is required.

Repository-specific source scope, run inputs/options, results, diagnostics, generated identities, cancellation, and reconciliation remain with the repository. Shared analyzer configuration/capabilities belong to the Analyzer subject. Repositories do not take ownership of or dispose the selected singleton. Existing hosted-subject registration can share one DI instance, but its publication into the subject graph and the exact application/root lifetime still require wiring. See [Analyzer](subjects/analyzer.md) and the [analyzer contract](analyzers/overview.md) for the canonical semantics and remaining scope decisions.

### Source/build groupings and Contracts

CodeSolution represents a source/build grouping and references repository-owned CodeProjects. Projects can belong to several solutions or none; this does not establish a Service or deployment boundary. The first proposed solution input is basic `.slnx` XML, alongside literal `.csproj` XML. Other formats require explicit analyzer support.

An authored OpenApiContract or ProtobufContract selects raw source through `path`. Contract.codeProjectPath holds an explicit project Identifier; only a compatible target supplies the typed association and CodeProject.contracts inverse. Without an explicit identifier, only a unique CodeProject in the raw document's own directory is selected; do not search parents or infer through a Host. Missing or ambiguous inference needs explicit configuration and visible diagnostics. Plain identifiers or generic subject links remain valid descriptive alternatives. Automatic discovery never writes descriptor JSON. Explicit definitions take precedence over automatic same-source candidates; exact identity handover and conflict handling remain open in [contract discovery](analyzers/contract-discovery.md).

### Storage access

The chosen MVP approach gives SourceRepository its own file access independently of the documentation mount. The alternative is to split the current storage implementation into a shared storage connection and separately mounted views. The latter is more general but is deferred. A hidden storage subject is insufficient because navigation visibility does not prevent scanning and subject creation.

### Generated subjects

A configurable SubjectProjection was considered. The chosen approach puts generation in the owning subject, using lifecycle and relevant change notifications to drive reconciliation. Shared infrastructure maintains keyed children without introducing another user-facing entity or query-to-object transformation language.

### Selector syntax

Structured selectors, named selections, and compact query syntax were discussed. The latest direction uses subject paths with `*` and `**` plus optional property filters, without a separate named-selection concept. Proposed default: configuration collections contain entries with `path` and optional `where`, reusing the existing expression parser for conditions. Keep source definitions distinct from resolved subject collections, illustratively `componentSources` versus `components` for services and deployment units, and `deploymentUnitSources` versus `deploymentUnits` for environments. These names remain proposed API choices. The [self-contained working sample](../../ArchDocs/README.md) is the current place to refine the file structure and schema before finalizing this design or its MVP.

### Markdown defines domain subjects

The alternatives considered were making the document itself a Service, giving a specialized MarkdownFile subclass a Service property, using existing inline subject blocks only, and defining named children generically in frontmatter. The user selected generic named frontmatter children: `$service` defines a child named `service`, `$environment` defines one named `environment`, and each value contains a `$type` plus normal subject configuration. A document defines subjects rather than becoming one of those domain types. This supersedes both service-as-MarkdownFile and ServiceMarkdownFile wrapper designs. Compared with existing inline blocks, the benefit is defining a subject outside the authored body without placing its widget there. It is an additional authoring location, not a different domain model.

Use the inline child name directly for scoped metadata too: `metadata.$subjects.service`, matching `Path=service`. Existing InlinePaths resolution and registered path writing support this shape. Frontmatter parsing/persistence is planned. Expanded collection keys do not normalize into bare child names automatically; any compatibility route needs owner-aware resolution and conflict handling.

### Master data and identifiers

Present a subject's own master data first, then structural references and selections, within configuration and state tables. Group by semantic role rather than serialization shape: kind and technology remain master data even when linked.

The user selected three levels for scalar [Identifiers](shared/identifiers.md): a plain string, a path to any subject or ordinary Markdown page providing a generic link, and a path to the expected subject type enabling rich UI and applicable domain behavior. All three are identifiers. A corresponding subject or subject type is optional for descriptive data; typed structural operations still require their actual compatible subjects. Exact syntax is deferred, with a compact options list in the identifier specification. Automatic name resolution, equality/grouping, and rename behavior remain open.

### Discovery, useful views, and executable architecture policies

The user confirmed adding project/library dependency views, grouping by logical boundaries, impact/backlink exploration, architecture checks, and linked interaction scenarios. Existing Service membership selectors remain the reusable way to connect discovered facts to logical architecture. Dependency aggregation preserves code-reference evidence; it does not infer runtime communication. Scenario order remains authored unless a separately supported adapter supplies observed evidence.

Architecture checks must also run from a headless CLI in local development and CI. The concrete initial policy example is that every in-scope Host must be assigned to at least one Service. This is an explicit rule rather than a hard domain invariant, and overlapping assignments remain valid. Findings explain how to update documentation/configuration and provide source locations and relevant help links in readable and agent-consumable output. The same evaluator serves the UI and CLI. Validation itself does not rewrite files, choose business ownership, or require a running HomeBlaze instance.

The user confirmed that the CLI starts the same configured subject graph and loads it normally. Shared root/storage loading and lifecycle start SourceRepositories, whose existing discovery design invokes their configured analyzers and reconciles generated subjects. The CLI waits for that work and dependent memberships to settle, validates the graph, reports findings, and shuts down normally. It is a command adapter over the shared host rather than another parser/scanner or an export-only validation model. Shared host composition and completion/readiness contracts still need implementation.

The user selected an architecture-validation framework with documented Rule subjects acting as architecture unit tests. One `rules.md` can define several named subjects through JSON-configured body blocks, and one JSON file per rule is equally supported. Repositories select exact/page/directory/global suites through shared `*`/`**` path queries. Generic and domain-specific concrete Rule subjects are documented under rules/. The abstract Rule base provides common properties; normal `$type` discrimination selects implementations with typed properties directly on the definition. Named Markdown blocks use the same concrete type in the block header. There is no separate `kind` dispatch or generic `config` bag for rules. Metadata remains normal enrichment. Exact schemas, registration, CLI packaging, reports, thresholds, and the selected first release remain design choices. See [architecture checks](rules/architecture-checks.md), [CLI/CI](integration/cli-and-ci.md), and the [validation scenario](scenarios/architecture-validation.md). These accepted directions do not select all of them for the MVP.

## Implementation constraints

Follow the repository's subject-creation guidance and existing conventions: partial intercepted properties, configuration versus state, internal setters for owned state, dictionaries for stable discovered identity, replaced published collections, headless domain code, and separate UI code. Reuse title, icon, monitoring, last-updated, path-reference, and metadata interfaces where applicable.

The builder chooses exact project names, concrete interfaces, JSON property names, worker primitives, caching strategy, and the smallest reusable helper API that satisfies the contracts above. A proposed Namotion.ArchDocs name is inherited from PR #257, not a new user decision. An implementation plan must identify required public API changes, tests for selector and lifecycle correctness, and targeted UI tests for page contributions, link previews, and widgets. Long-running benchmarks or connector verification require agreement before they are scheduled; neither is implied by this design.

## Decisions still open

The following questions are tracked in the relevant element specification; this list provides cross-cutting scope context. A status of [Planned] does not settle these schemas or select an MVP.

- Context representation: internal/external is confirmed as diagram-relative; exact boundary fields, rendering behavior, optional neighbor discovery, and actor representation remain open. Neighboring software systems use Service.
- Service and Resource roles: Service represents a logical system/capability; Resources such as databases and cloud offerings appear alongside Hosts at component level. Host-to-Resource provides references are optional and many-to-many. Define shared interfaces and the exact persistence/navigation of provider relationships without assuming inheritance.
- Deployment participation: component-based membership is accepted; settle participant eligibility and representation of resource definitions/configuration versus dependencies on existing managed resources.
- Environment bindings: component versions, endpoint addresses, and concrete resource mappings are accepted; settle exact binding records, infrastructure references, ownership, and authored versus observed state.
- Interfaces, Endpoints, and Contracts: settle exposure/reference schemas, ownership, parsed models, supported revisions, and version/snapshot identity. OpenAPI/protobuf file `path` and the same-directory project-association rule are specified. CodeProject public API Contracts do not require network Interfaces or Endpoints.
- Optional domain additions: refine generic Artifact and Actor models separately from the sample products, and decide their MVP inclusion.
- Executable roles: settle extensible execution-behavior and role fields for runnable process definitions; sample-specific subclasses are unnecessary.
- Presentation contracts: useful subject detail pages, navigation participation, shared page/widget/preview components, and exact UI slot registration.
- Final schemas and identity: CLR/serialized names, path casing and aliases, generated keys, persistent identity and rename behavior, and raw-source path matching where graph paths are insufficient.
- File ergonomics: evaluate readable type identifiers, generated reference syntax, complete minimal examples, schema tooling, source-located diagnostics, and focused writeback against the file-authoring requirement. Named Markdown children use inline keys; existing fully qualified type names and other generated routes still need review.
- View configuration: specialized structural views, DependencyDiagram, ImpactView, ArchitectureCheckResults, and ScenarioDiagram are documented with proposed schemas. Settle grouping shared components, traversal/aggregation, layout interaction, result scheduling, and renderer choices without introducing domain entities for every view.
- Runtime scenarios: linked participants with authored ordered steps are planned through ScenarioDiagram. Exact branching/loop schema, a reusable Scenario subject, and trace ingestion remain open.
- Architecture validation: shared UI/CLI execution through the normal graph host and actionable Host membership findings are accepted. Settle Rule/evaluator schemas, required-fact descriptors, predefined catalog publication, selection invalidation, shared host composition/analyzer publication, readiness for the current input generation, report/path contracts, severity thresholds, and any future suppression/baseline behavior. Incomplete required analysis cannot produce a clean pass.
- Initial implementation: which candidate behaviors, exclusions, and UI surfaces form the MVP after sample review, including any long-running verification that must be agreed during planning.
- Delivery integration: [pipeline/release/deployment connections](integration/delivery-pipelines.md) are a proposed generic extension, with GitHub Actions as an adapter example. Choose linking, observation, and optional control separately, and refine subject/record shapes and explicit mappings before implementation. Earlier candidate exclusions do not rule out this broader direction.

## Related references

[Overview](overview.md) · [Readiness matrix](implementation-readiness.md) · [Scenarios](scenarios/overview.md) · [Verification scenarios](verification-scenarios.md)
