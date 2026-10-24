---
title: ArchDocs Domain and MVP Design
navTitle: ArchDocs Domain and MVP
---

# ArchDocs Domain and MVP Design

Status: draft for review. This document records the agreed domain and the proposed first implementation boundary. It does not authorize implementation. Choices labeled "Proposed default" are recommendations, not decisions made by the user.

## Intent

Make a documentation folder describe and explore the software around it. A repository's `/docs` folder contains ordinary Markdown, typed Markdown subject documents, and authored subject JSON files. Source analysis discovers the code structure; authored subjects describe the logical architecture and deployment arrangement. The same graph is available to the subject browser, Markdown widgets, and queries.

Use the design in [PR #257, HomeBlaze: ArchDocs](https://github.com/RicoSuter/Namotion.Interceptor/pull/257) as prior work. That open PR contains only a 200-line design document, not an implementation. This design refines its entity boundaries and adds repository analysis and selections.

## Goals

- Support several source repositories without assuming one service per repository.
- Discover as much factual information as the supported analyzers can establish, with authored enrichment in JSON and the UI.
- Describe services independently of source layout, language, process count, and deployment topology.
- Select discovered subjects by scope, kind, name, and other properties instead of maintaining lists of every discovered item.
- Keep generated subjects stable across successful refreshes and preserve authored enrichment.
- Reuse existing storage, registry, expressions, lifecycle, metadata, and subject UI capabilities where they fit.

## Non-goals for the MVP

- Cloning, fetching, checking out, or updating Git repositories.
- Namespace, type, method, or call-graph analysis.
- Automatic inclusion of a selected element's transitive dependencies.
- GitHub workflow, release, registry, or deployment synchronization.
- Health polling, actual deployment execution, or individual rollout history.
- A configurable projection entity or a general transformation language.
- Shared storage connections with separately configurable subject-tree views.

An analyzer extension can add additional discovery later. These exclusions constrain the first implementation, not the domain vocabulary.

## Approaches

### Host discovery and architectural membership

Three approaches were considered: declaring each host and enriching it; having each service scan its source; and having source repositories discover the code graph while services select from it. The chosen approach is repository-owned discovery. It handles shared projects, monorepos, and multiple repositories without repeated per-service discovery. Services express architectural intent rather than own independent copies of discovered hosts.

Proposed default: allow manual descriptions of hosts whose source is unavailable. Their exact authoring workflow is outside the repository-discovery slice.

### Storage access

The chosen MVP approach gives SourceRepository its own file access independently of the documentation mount. The alternative is to split the current storage implementation into a shared storage connection and separately mounted views. The latter is more general but is deferred. A hidden storage subject is insufficient because navigation visibility does not prevent scanning and subject creation.

### Generated subjects

A configurable SubjectProjection was considered. The chosen approach puts generation in the owning subject, using lifecycle and relevant change notifications to drive reconciliation. Shared infrastructure maintains keyed children without introducing another user-facing entity or query-to-object transformation language.

### Selector syntax

Structured selectors and a compact query language were discussed. Proposed default: use structured JSON selectors for the initial API and reuse the existing expression parser for property conditions. A compact syntax can be added later without changing selection semantics. The exact JSON property names are left to the implementation design.

### Typed Markdown

The user chose MarkdownFile inheritance for typed Markdown subjects. A registered type declared by frontmatter must derive from MarkdownFile and retain the normal Markdown page renderer, editor, navigation, expressions, and blocks. A custom type can supply an optional widget that the author embeds in Markdown. Loading a typed page does not automatically replace its UI or insert a custom summary. This replaces the earlier proposal to keep the file's declared type entirely independent of MarkdownFile.

## Typed Markdown and metadata

Ordinary Markdown remains MarkdownFile. A `$type` discriminator in YAML frontmatter selects a registered MarkdownFile subclass, such as a service document. The file itself is that specialized subject, rather than a page that merely references a separately defined service. Configuration fields sit beside `$type`, following the existing JSON configuration naming conventions. An optional `metadata` object contains enrichment. The remaining document body is ordinary Markdown. Exact registered type names are builder choices.

This is a generic storage capability for eligible MarkdownFile subclasses, not an architecture-specific parser or support for arbitrary unrelated configurable types in a Markdown file. Generic headless analysis and domain helpers stay independent of page rendering; Markdown-specific subjects accept the storage dependency imposed by inheriting MarkdownFile. Keep Blazor components in their separate UI assembly.

The existing component registry already resolves base-class registrations, and the normal Markdown page component accepts MarkdownFile subclasses. Typed documents should use that existing rendering path and may register widget components independently of their page component. A service-summary widget is explicitly embedded by the author and reads the service's facts without rendering its Markdown body again. The generic Widget reference currently rejects ancestors to prevent recursion, so embedding a page's own summary needs a dedicated safe summary block or an equally explicit mechanism; do not remove general cycle protection.

Use one metadata model and API with two persistence representations. JSON subjects keep metadata in their existing sidecar; ordinary and typed Markdown keep it under `metadata` in frontmatter. Frontmatter is the canonical metadata store for a Markdown document, including entries for its generated descendants; do not introduce an additional active metadata sidecar for that document. Existing untyped page display/navigation frontmatter remains supported. Define how those existing fields relate to metadata without creating two independently editable copies of the same value.

Extend existing file loading to inspect Markdown frontmatter before selecting the concrete type. This is not implemented today: FileSubjectFactory chooses MarkdownFile by extension, and MarkdownFrontmatter reads a fixed set of display fields. Same-type refreshes preserve the subject where possible. A declared type change requires replacement through the normal lifecycle. Invalid or unavailable declared types need a visible diagnostic and preservation of the source content, not silent interpretation as a successful instance of another type. Exact recovery behavior is left to implementation design.

Frontmatter edits through the UI must preserve the Markdown body and unrelated frontmatter keys. Use the existing metadata API for metadata changes, and the configuration writer for declared configuration. Each write must serialize with other writes to the same document. Type, configuration, metadata, and content must come from a compatible load; invalid or superseded loads cannot publish a mismatched combination.

Typed relationships are structured metadata, with a kind and target reference and optional descriptive data such as technology or a label. Their source defaults to the owning subject, so relationships on a typed service document originate at that service. A typed reader exposes those entries to graph queries and widgets. Analyzer-produced relationships remain discovered state with provenance. Authored relationship edits reconcile the effective graph through the same change-driven pattern; they do not require independent relationship JSON files.

An ordinary Markdown link can establish a document reference when a later analyzer supports it, but a link alone does not assert a runtime dependency. A general Markdown extraction-rule engine is deferred; structured frontmatter and the existing subject blocks provide explicit authoring for the MVP.

## Domain

### SourceRepository

A configurable subject that identifies the source root of an existing local checkout and coordinates analyzers. It owns the discovered code subjects and reports analysis status, diagnostics, and the time of the last successful analysis. It does not require a Git-managed folder or perform Git operations.

Several SourceRepository subjects can coexist. A repository may contain hosts for several services, and a service can select elements from several repositories.

Analyzer-produced facts remain distinct from authored settings. Facts have enough provenance to identify the analyzer and supporting source location. A failed analysis is distinct from a successful analysis that found no results.

### Discovered code elements

The common model permits different kinds of discovered subjects and typed relationships. An analyzer need not support every kind. The MVP .NET analyzer discovers code projects, runnable hosts, libraries represented by their code projects, and project references.

A CodeProject represents a source/build project, such as a `.csproj`. A Host represents a runnable software entry point and refers to its entry CodeProject when known. A host can depend on several projects, and projects can contribute to several hosts. A library project does not become a host merely because it is referenced.

Host and CodeProject are distinct concepts even when a simple application has one of each. Source location, analyzer identity, display name, and language are descriptive data, not the definition of a service.

Common classification must allow selectors to express "library named `*.Test`" without depending on an analyzer's concrete CLR type. Proposed default: expose analyzer-independent classifications for projects, hosts, and library projects; the exact interface and type arrangement is a builder decision. Namespace and type classifications can be introduced by later analyzers.

### Service

A configurable subject representing a logical software capability or bounded context. "Application" is not an additional entity. A service can be implemented by several hosts and can select libraries or other code elements as well.

A service contains named selections, for example components and tests, and architectural descriptions and relationships. Selections refer to repository-owned subjects; they do not clone them. Membership can overlap when explicitly configured. Shared code remains one discovered element.

### DeploymentUnit

A configurable selection of hosts deployed together. It can select all hosts of one service, a subset, or hosts from several services. There is no ownership or cardinality restriction that forces a deployment unit to correspond to one service.

The common arrangement is one service associated with one deployment unit. Service boundaries remain logical boundaries when the deployment arrangement changes.

### Environment

A configurable subject describing a deployment context, such as staging or production. It declares its deployment units and owns the generated Deployment subjects for those selections. Environments can be authored as JSON files or typed Markdown documents.

It does not generate deployments for every service or every deployment unit in the graph. Only declared selections produce deployments.

### Deployment

An enduring installation of a DeploymentUnit in an Environment, not an individual rollout event. Its version, URLs, and later observed state can change over time. The common case is one deployment per unit per environment; the model can be extended with installation identity if several installations of one unit in an environment are needed later.

Environment maintains one generated Deployment subject per selected unit. It reuses that subject while the environment/unit identity remains selected. It removes the generated subject when the pair is no longer selected. This maintains the current configured installations, not deployment history.

A generated deployment expresses the intended installation. It is not evidence that software is actually running. For the MVP, version and relevant URLs are manually configured data; no health or deployment connector populates them.

Use the existing metadata system for manually entered enrichment of generated subjects. The metadata of an environment stores entries for its Deployment descendants, keyed by their stable subject paths: in the sidecar of a JSON definition or the frontmatter of a Markdown definition. Version and URLs can use domain-specific metadata keys with typed accessors and editing support; notes, links, and tags use the existing keys. Configuration declares deployment-unit selection, and metadata holds enrichment. Do not create a second enrichment store inside the owner's configuration.

## Selections and relationships

A selector resolves a set of subjects. Scope, kind, source path, name matching, property predicates, and explicit references are useful building blocks. The mechanism should be usable outside this domain rather than restricted to an Artifact base class.

Filters within one selector combine with AND. Proposed default: a list of selectors combines by set union; exclusions subtract from that result; results are deduplicated by identity. Support explicit references as the simplest selector. Deployment-unit selections validate that their results are hosts, and environment selections validate that theirs are deployment units. A wrong result kind produces a diagnostic rather than being converted into another kind of entity.

Services expose named selections that other selectors can use as a scope. A deployment unit can therefore select the hosts in a service's component selection. Proposed default: detect cycles between named selections and report an invalid configuration; do not recursively expand them forever.

Source paths are repository-relative file locations. Subject paths identify subjects in the object graph. Namespaces are another future filter. These must not be silently treated as the same address space.

The current checkout has subtree matching in AgentWatchPaths, path-prefix/type/property filtering in the MCP search tool, and name wildcards in NuGetPackageNameMatcher. It does not have a general reusable path-glob matcher. Reuse the applicable behavior without introducing a dependency on the AI or NuGet plugin assembly. The NuGet matcher's `*` matches one or more characters and has no path-segment semantics, so it cannot be adopted as a path matcher unchanged.

Proposed default: path globs use `/` separators, `*` within a path segment, and `**` across path segments. Name patterns support `*`. Matching rules, including casing and empty matches, must be specified once and tested by the implementation. The builder may choose an existing suitable dependency after checking the repository's requirements.

Selectors determine membership. Dependencies outside the selected scope remain external relationships and are not automatically added. Transitive membership expansion is deferred. Project references describe code dependencies; they do not by themselves establish runtime communication between services. Authored architectural relationships and their evidence remain distinguishable from discovered code-reference edges.

Unresolved explicit references and invalid predicates are diagnostics. A syntactically valid selection with no matches is an empty result, distinguishable from failed analysis. A temporary input failure must not erase the last successful discovered graph or the authored selection.

Proposed default: successful publication is atomic per repository. An incomplete directory listing, a required source read failure, or a malformed project that prevents analysis fails that repository's pass and retains its entire last successful graph. Other repositories can publish independently. Preserve the failure diagnostics separately; on an initial failed pass there is no successful graph to show yet. A complete pass can publish confirmed removals. An unresolved condition or reference that the static analyzer intentionally represents as unknown is a diagnostic on a successful pass, not a reason to pretend the pass failed. Partial per-project stale-result merging is deferred.

## Source access and documentation mounts

A typical setup mounts `/repo/docs` in HomeBlaze and configures SourceRepository to analyze `/repo`. It does not mount the whole repository merely to make its contents readable. SourceRepository exposes discovered architectural elements rather than a file subject for every source file.

The MVP supports local folders only. Proposed default: use FluentStorage's raw listing, read, and metadata capabilities where practical, behind the smallest access contract needed by analyzers. Reuse existing suitable contracts before introducing another abstraction. The current IStorageContainer includes subject lifecycle and writes and has no general raw recursive enumeration API, so it is not automatically the analyzer contract.

File locations such as `../` resolve relative to the defining configuration file's directory when it has a local file origin. SourceRepository then uses its resolved root as the boundary for source traversal. Subject references continue to use the existing subject path resolver. A relative file location must not be interpreted as a request to traverse above a mounted blob container through IStorageContainer.

For the MVP, defining SourceRepository in its own JSON or typed Markdown file provides an unambiguous local origin. Proposed default: where no local file origin exists, require an absolute source root and report a configuration diagnostic for an ambiguous relative path. Do not guess from the process working directory. Future storage backends can supply a storage identity and a root path instead of a local filesystem location.

## Analyzer extension point and initial .NET analysis

Analyzers contribute to a common graph and can enrich the same element. Discovery is not an exclusive choice between ".NET host" and "containerized host". A later container analyzer can associate packaging with a host discovered by .NET analysis.

Proposed default: register typed analyzer implementations through the existing dependency-injection and module registration patterns. Do not add a separate plugin loader or arbitrary executable analyzer configuration. The analyzer contract describes applicability, read-only source access, a result with provenance and diagnostics, and enough identity to merge or link contributions.

The MVP analyzer works at project level by reading basic XML elements of `.csproj` files. The user explicitly chose this simple starting point to obtain useful subjects. Do not restore, build, execute, or evaluate projects through MSBuild. Capture literal project properties and project references that can be read directly; classify hosts or libraries only where the supported XML establishes that classification. Conditions, imports, expressions, generated references, and unavailable files can remain unknown or unresolved with diagnostics. Comprehensive MSBuild interpretation is outside the MVP.

Proposed default: exclude generated output, dependency caches, and version-control internals from source discovery. Make the scan scope configurable. Exact exclusions and handling of links, duplicate roots, and malformed input are builder decisions within the read-only local-source boundary.

Provide analysis on initial activation and an explicit refresh operation. Proposed default: automatic refresh on relevant file changes, coalesced into work on the owning subject's worker. File events are invalidations, not a source of authoritative inventory. Initial and explicit refreshes reconcile against the current source. Exact fallback rescan scheduling is left to implementation planning.

## Generated child lifecycle

SourceRepository owns discovery results. Environment owns Deployment children. Each uses shared keyed reconciliation infrastructure to preserve matching instances, add missing subjects, remove subjects no longer desired, and publish replacement collections only when needed. Do not recreate all subjects on each refresh or mutate an already-published collection in place.

Lifecycle attach/detach and property-reference changes provide invalidation signals. Relevant configuration/property changes also trigger reconciliation because a subject can remain attached while its selection or filter match changes. An initial reconciliation accounts for already attached inputs. Subscription establishment and the initial snapshot must not miss concurrent changes.

Lifecycle callbacks are fast, exception-free bookkeeping and signaling. They run under the lifecycle lock. Do not perform file analysis or substantial graph changes directly inside these callbacks. A serialized worker re-evaluates current inputs and publishes changes outside the callback. Coalescing is allowed provided it cannot lose the final invalidation.

The owning subject's detach stops subscriptions and background work. Canceled or superseded work cannot republish children for an owner that is no longer active. Inputs changing during a pass schedule a later pass or invalidate that pass's publication. Once writes and source changes settle, generated collections agree with the current successfully evaluated inputs.

Use stable keyed dictionaries for generated children, not positional arrays. Proposed default: repository element keys derive from a repository identity, analyzer identity namespace, element kind, and normalized repository-relative identity. A project-file move is a removal and addition unless a later explicit identity mapping preserves it. Do not claim rename detection that the analyzer does not implement. Unchanged files retain keys across root relocation and restart.

Deployment keys derive from the environment and deployment-unit identities. Persist authored identities or use the existing persistent subject reference conventions consistently; do not use discovery order, transient object references, display names, or timestamps as persisted keys. Exact serialized identifier fields and reference formatting are builder decisions, and their rename behavior must be documented.

Use the existing path-reference patterns to reference subjects without inadvertently creating ownership cycles or extra canonical parents. Where an actual shared subject collection is useful, follow the existing shared-child ownership and detach rules.

Shared infrastructure is a helper or library API, not a new domain subject. Domain-specific selection and object creation remain with the owner. The exact placement should avoid introducing application dependencies into the core libraries or granting cross-library internal access.

### Reuse beyond ArchDocs

The shared-helper direction also applies to existing implementations. Sonos preserves players and groups by identity; Hue preserves discovered devices and builds rooms and zones; SunSpec reuses units and compatible model subjects; FritzBox preserves clients and access-point memberships; installation client reconciliation merges configured clients with live connections; storage and Markdown reloads preserve file and widget subjects when their identities and types remain compatible. These are candidate consumers, not a commitment to migrate all of them as part of this MVP.

There is already repeated dictionary equality by key and reference identity in HueBridge.DictionaryEquals, SonosSystem.HaveSameEntries, SunSpecDiscovery.HaveSameUnits, and StorageReconciler.AssignChildren. This is a narrower initial extraction than a complete discovery coordinator. FritzBox also demonstrates why caller policy matters: an absent client remains for one more poll so its departure can be recorded before detach. Installation clients retain named entries even when disconnected. A generic helper must not assume every missing item is immediately removed or disposed.

Proposed default: separate collection reconciliation primitives from work scheduling and lifecycle bindings. Collection primitives can cover identity comparison, reuse versus replacement, creation, caller-controlled retention, and returning the original published collection when membership and instance identity are unchanged. Allocate replacement collections lazily where practical. The caller owns synchronization, publication, subject-state updates, and disposal/ownership policy; the helper does not promise a transaction across mutable child properties.

A second helper can coalesce invalidations, serialize passes, preserve invalidations arriving during a pass, and fence obsolete work. Storage's ReconcileTrigger provides an existing reference, but its path batching, quiet periods, retries, and storage-refusal behavior are not all universal defaults. Subscription/lifecycle integration belongs in a layer that already depends on tracking or hosting. Pure collection helpers should have no dependency on storage, UI, device protocols, or the tracking runtime; Namotion.Interceptor itself is a possible eventual home.

Proposed rollout: establish the small contracts against SourceRepository and Environment and validate them against at least one representative existing reconciliation use before declaring the API broadly reusable. Agree any existing-consumer migration separately; avoid coupling the architecture MVP to a repository-wide refactor. Core publication is a later API-placement decision unless the extraction clearly fits the existing dependency graph and is approved with the design.

Connector CollectionDiffBuilder produces ordered transport patches rather than maintains discovered child identity, and LifecycleInterceptor reconciles references under its own graph invariants. Similar terminology does not make those implementations interchangeable with the proposed collection helper. Reuse individual primitives there only when their contracts actually match.

## Authored data and discovered state

Authored configuration determines logical intent, selectors, and source locations. Metadata holds authored relationships, manual descriptive enrichment, and manual deployment details. Analyzer results and generated child collections are state. Computed summaries and resolved views use the existing derived-property mechanism where appropriate; generated objects are not recreated by a derived getter.

Enrichment has one canonical persisted home per owner, as specified in Typed Markdown and Metadata. StorageMetadataApplier already applies a stored JSON subject's sidecar to its descendants, including children that attach after the sidecar was loaded, and retains unknown keys and JSON values. Extend equivalent behavior to Markdown frontmatter. Thus repository metadata can enrich discovered project and host children, and environment metadata can enrich generated deployments, provided their canonical ownership and paths are stable. Existing metadata limits still apply. Add typed readers, validation, and editors for domain-specific fields; generic preservation of a key does not make it a typed configuration property or an automatically generated editor field. Do not persist competing editable copies of discovered facts. Define any supported override explicitly and show the discovered value's provenance separately.

The basic subject UI should expose applicable configuration, state, diagnostics, and refresh operations through existing interfaces and generated editors. Project reference edges and selection membership must be traversable through the subject graph without requiring a custom visual graph renderer.

The user selected existing subject UI plus an optional Markdown service-summary widget and a deployment matrix for the MVP. A service summary reads a Service subject and summarizes its selected code/hosts and deployments. Proposed default: show its name and description, selected host/project counts, and links to corresponding subjects and deployments. A typed service document retains the normal Markdown UI, and its author chooses where to embed a summary. The deployment matrix shows units against environments using declared deployments and manual versions/links; it must not display an unobserved deployment as healthy. Exact widget presentation remains to be designed. An interactive dependency-graph renderer is deferred; project-reference edges remain navigable through the subject graph.

## MVP acceptance scenarios

1. Mount only a repository's documentation folder, configure a source root relative to a JSON file there, and analyze the surrounding checkout without mounting every source file as a subject.
2. Configure two repositories; discover their project-level elements without duplicating shared elements within one repository's graph.
3. Define a service selecting all elements under a source folder and a named test selection filtering library projects by `*.Test`.
4. Change the source or selectors and refresh; preserve unchanged generated subjects and authored enrichment while updating membership and project-reference edges.
5. Show an external code dependency without adding its target to the service's membership or asserting runtime communication.
6. Define a deployment unit selecting hosts from more than one service, and an environment selecting that unit; obtain exactly one generated Deployment for the pair.
7. Edit a deployment's manual version and URLs through the existing metadata store with domain-specific editing support; retain them across reconciliation and restart.
8. Remove or change an environment's unit selection while the unit stays attached elsewhere; update generated Deployments correctly.
9. Surface malformed selectors, unresolved references, uncertain analysis, and failed source access distinctly; retain the last successful graph on temporary analysis failure.
10. Detach an owner during queued work; prevent obsolete publication and release its subscriptions and generated children according to the normal lifecycle rules.
11. Embed a service summary and deployment matrix in Markdown using normal widget blocks; navigate from their entries to the existing subjects without maintaining duplicate service facts in the page.
12. Define a service in typed Markdown frontmatter; resolve it as the declared MarkdownFile subclass, render and edit it through the normal Markdown UI, and embed its optional summary without recursive rendering.
13. Edit metadata and authored relationships on ordinary and typed Markdown through the metadata API; preserve the body and unrelated frontmatter, reapply descendant enrichment on later attach, and retain that data across refresh and restart.

These are requirements for future implementation and testing. No product code, tests, dependencies, or projects are added by this design document.

## Constraints and builder choices

Follow the repository's subject-creation guidance and existing conventions: partial intercepted properties, configuration versus state, internal setters for owned state, dictionaries for stable discovered identity, replaced published collections, headless domain code, and separate UI code. Reuse title, icon, monitoring, last-updated, path-reference, and metadata interfaces where applicable.

The builder chooses exact project names, concrete interfaces, JSON property names, worker primitives, caching strategy, and the smallest reusable helper API that satisfies the contracts above. A proposed Namotion.ArchDocs name is inherited from PR #257, not a new user decision. An implementation plan must identify required public API changes, tests for selector and lifecycle correctness, and targeted UI tests if custom UI enters scope. Long-running benchmarks or connector verification require agreement before they are scheduled; neither is implied by this design.

## Review status and remaining presentation work

The user selected basic `.csproj` XML discovery, existing subject UI plus optional summary/matrix widgets, and reuse of metadata for enrichment and authored relationships. Typed Markdown subjects inherit MarkdownFile, keep its normal UI, and store configuration beside `$type` and enrichment under `metadata` in frontmatter. Ordinary Markdown gains the same frontmatter metadata support. The shared reconciliation approach and Environment declaring deployment units are agreed. A builder check identified the partial-analysis failure boundary; the proposed default in this document retains the entire last successful graph of a repository whose pass fails, while other repositories can publish independently.

The description and exact visual contents of the service summary and deployment matrix need user review. Their proposed behavior above is sufficient to discuss scope, not approval of a visual design. This domain and MVP draft must be approved before implementation planning begins.
