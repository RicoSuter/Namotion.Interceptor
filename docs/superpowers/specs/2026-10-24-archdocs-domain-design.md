---
title: ArchDocs Working Domain Specification
navTitle: ArchDocs Domain Specification
---

# ArchDocs Working Domain Specification

Status: working specification under active discussion. This is the canonical reference for entities, their data, relationships, ownership, authoring, and presentation contracts. The [working sample](../../../src/HomeBlaze/HomeBlaze/Data/Files/ArchDocs/README.md) supplies concrete Markdown and JSON files for refining those contracts. It is not a finalized schema or implementation plan. The latest user direction is to refine the sample first, then select the MVP and plan implementation. Earlier first-iteration boundaries remain candidates until that review. Choices labeled "Proposed default" are recommendations, not decisions made by the user; unresolved choices are listed at the end.

Implementation plans, once the design and MVP are approved, belong in `docs/superpowers/plans/`. No implementation plan has been written yet.

## Intent

Make a documentation folder describe and explore the software around it. A repository's `/docs` folder contains ordinary Markdown, Markdown with embedded subject definitions, and authored subject JSON files. Source analysis discovers the code structure; authored subjects describe the logical architecture and deployment arrangement. The same graph is available to the subject browser, Markdown widgets, and queries.

This specification and working sample continue [PR #257, HomeBlaze: ArchDocs](https://github.com/RicoSuter/Namotion.Interceptor/pull/257), replacing its original 200-line design with the current domain discussion. They refine entity boundaries and add repository analysis and path queries. The PR contains documentation, proposed configuration, and source fixtures; it does not implement ArchDocs product behavior.

### Reference use cases: AppHost documentation and Apimundo

The user identified AppHost as the first real documentation target and the [Apimundo project site](https://apimundo.com/organizations/namotion/projects/apimundo?service=1&tab=overview&repository=Apimundo) as the domain and exploration experience to rebuild. These guide the broader design; they do not imply that all observed features belong in the MVP or that implementation has been authorized.

AppHost already has ordinary Markdown documentation: one arc42 architecture overview, crosscutting concept documents, ADRs, product decision records, guidelines, and an Orders sample domain document. Its architecture describes one coordinated application/release containing Web and Worker processes, shared libraries, a migrations executable, and resources. Orders is a module inside that application, not an independently deployed Service. Keep those existing document roles and enrich them with subject definitions, metadata, and optional views. The two-service Commerce fixture remains useful for a different topology.

The inspected Aspire composition defines PostgreSQL with an appdb database, RabbitMQ, migrations, Web, and Worker. Its local orchestration project is itself executable; migrations run to completion before Web and Worker start. A runnable project is therefore not sufficient evidence of an always-running production Host or deployment membership. The domain must accommodate one-off executable roles and authored selection of deployed processes. Whether orchestration and release steps need distinct types or role metadata remains open. The documentation also distinguishes current local resources from proposed Azure topology and future features; these statuses must remain explicit rather than present proposed infrastructure as discovered deployed state.

The Apimundo service and project views were inspected on 2026-10-24. The following capabilities inform the domain without fixing their replacement schemas:

| Observed capability | Current model fit or gap |
|---|---|
| Project/service overview and Markdown descriptions | Scoped documentation and Service summaries; organization/project boundaries need not become mandatory graph roots |
| Repository code-project dependency graph | SourceRepository and CodeProject discovery, shared references, and graph presentation |
| Component/artifact inventory | Service, Host, and Resource cover part of this; people/actors and their relationships remain open |
| Service-by-environment matrix, deployment detail, version and status | Environment, DeploymentUnit, and Deployment; observed health and monitoring integrations remain separate future choices |
| Logical API, environment-specific endpoint, and versioned specification | Currently an explicit gap; distinguish interface definitions, deployed addresses, and contract documents before choosing entity names or ownership |
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

Structured selectors, named selections, and compact query syntax were discussed. The latest direction uses subject paths with `*` and `**` plus optional property filters, without a separate named-selection concept. Proposed default: configuration collections contain entries with `path` and optional `where`, reusing the existing expression parser for conditions. Keep source definitions distinct from resolved subject collections, illustratively `componentSources` versus `components`, `hostSources` versus `hosts`, and `deploymentUnitSources` versus `deploymentUnits`. These names remain proposed API choices. The [self-contained working sample](../../../src/HomeBlaze/HomeBlaze/Data/Files/ArchDocs/README.md) is the current place to refine the file structure and schema before finalizing this design or its MVP.

### Markdown defines domain subjects

The alternatives considered were making the document itself a Service, giving a specialized MarkdownFile subclass a Service property, using existing inline subject blocks only, and defining named children generically in frontmatter. The user selected generic named frontmatter children: `$service` defines a child named `service`, `$environment` defines one named `environment`, and each value contains a `$type` plus normal subject configuration. A document defines subjects rather than becoming one of those domain types. This supersedes both service-as-MarkdownFile and ServiceMarkdownFile wrapper designs. Compared with existing inline blocks, the benefit is defining a subject outside the authored body without placing its widget there. It is an additional authoring location, not a different domain model.

## Markdown subject definitions and metadata

Markdown remains an ordinary MarkdownFile with its normal renderer, editor, navigation, expressions, and blocks. A top-level frontmatter key `$<name>` defines a child subject named `<name>`. Its value contains the registered subject's `$type` and configuration using the standalone configuration schema. The child name is independent of its type: `$orders` could define a Service just as `$service` can. Multiple named children are supported in one document. Exact registered type names remain builder choices.

```yaml
---
title: Orders
$service:
  $type: Namotion.ArchDocs.Service
  name: Orders
  componentSources: []
$environment:
  $type: Namotion.ArchDocs.Environment
  name: Staging
---
# Orders
Regular documentation here.
```

The document and its domain children are distinct subjects. No domain-specific MarkdownFile subclass or declared page type is needed. Domain subjects do not inherit MarkdownFile or depend on Markdown storage. The file owns their configuration persistence and provides its origin for relative file access; domain subjects must not infer that origin from the process working directory. A page that only describes an existing service can instead reference it through metadata without defining another instance. Keep domain logic headless and Blazor contributions in separate UI code. Custom page subclasses are deferred; if introduced later, they must inherit MarkdownFile and retain normal Markdown behavior.

Reuse registered configurable-subject loading and normal lifecycle rules for these children, as existing inline blocks already do. Their stable identity is their declared child name, independent of the entry's position in the source. Frontmatter and named inline subjects share the page's child-name namespace: duplicates must produce diagnostics rather than silently shadow or instantiate competing definitions. Reserve framework keys such as `$type` so they cannot accidentally become child declarations, and validate declaration names and values. Same-name, same-type refreshes preserve compatible children; removed children detach and type changes replace subjects through normal lifecycle handling. Invalid or unavailable types need visible diagnostics and preservation of source content, while the document remains readable. UI configuration edits route back to the corresponding `$<name>` entry through the owning file's configuration writer. Exact storage interfaces and recovery behavior are builder choices.

A service-summary widget reads the Service child without rendering the Markdown body again. The page's context slot and link previews can discover applicable contributions from its defined children, while keeping the page as the navigation target. Domain links and selectors can address the child explicitly; a page link is not silently rewritten into a service reference. Keep generic widget cycle protection intact. Defining a service as a child allows a widget in its parent document to refer to that child without referring back to the containing Markdown page.

Use one metadata model and API with two persistence representations. JSON subjects keep metadata in their existing sidecar; Markdown keeps it under `metadata` in frontmatter. Frontmatter is the canonical metadata store for a Markdown document, including scoped entries for its defined children and their generated descendants; do not introduce an additional active metadata sidecar for that document. Page metadata belongs to the document, and child-scoped metadata belongs to the defined subject. Existing page display/navigation frontmatter remains supported. Define how those existing fields relate to metadata without creating two independently editable copies of the same value.

Extend Markdown loading to read generic child declarations and metadata in addition to the existing fixed display fields; FileSubjectFactory can continue choosing MarkdownFile by extension. The current inline parser accepts registered configurable types, preserves compatible named subjects, and writes configuration edits back into their blocks. Reuse those contracts and suitable helpers for frontmatter rather than introduce a competing subject lifecycle. Frontmatter definitions contribute children without inserting render segments into the Markdown body. Frontmatter edits through the UI must preserve the body and unrelated keys. Use the existing metadata API for metadata changes, and the configuration writer for declared configuration. Each write must serialize with other writes to the same document. Child definitions, configuration, metadata, and content must come from a compatible load; invalid or superseded loads cannot publish a mismatched combination.

Moving a definition into JSON reuses the same domain type and configuration schema. Its persisted enrichment must move with it, and path-based references must be updated when its canonical path changes. Automatic extraction and identity-preserving relocation tooling are not MVP requirements. The original page can then describe the standalone subject through a metadata relationship.

Typed relationships are structured metadata, with a kind and target reference and optional descriptive data such as technology or a label. Their source defaults to the owning subject: page metadata originates at the document, while child-scoped service metadata originates at that service. A typed reader exposes those entries to graph queries and widgets. Analyzer-produced relationships remain discovered state with provenance. Authored relationship edits reconcile the effective graph through the same change-driven pattern; they do not require independent relationship JSON files.

An ordinary Markdown link can establish a document reference when a later analyzer supports it, but a link alone does not assert a runtime dependency. A general Markdown extraction-rule engine is deferred; structured frontmatter and the existing subject blocks provide explicit authoring for the MVP.

## Page UI extension slots

Regular Markdown remains the primary authoring experience for documentation and designs. Generic metadata can make richer information available without widget blocks, a custom document type, or layout directives in the source. An ordinary MarkdownFile can define a Service through `$service` or reference a standalone service through a `documents` relationship. Both retain normal Markdown rendering and can offer the service's code and deployment information. UI contributions can cover several defined subjects without making one the page's implicit identity. Explicit widget blocks remain an optional way to place a widget at a specific point in the authored content.

The user proposed additional slots where subject-specific UI can contribute around the normal page. Extend the existing component-registration model with page-level contribution points. Proposed initial slots are:

| Slot | Purpose | Example |
|---|---|---|
| Page actions | Contextual commands in the page toolbar or menu | Explore selected projects or refresh analysis |
| Context panel | Optional information alongside the document | Related subjects, authored relationships, host summary, deployments |
| Additional views | Alternate views reachable from the page | Service details or deployment matrix |

Optional positions above or below the content can host a summary later if that placement is wanted. Exact names and presentation remain builder and visual-design decisions. Slots are in the application page frame, so the Markdown body keeps its normal renderer and portable source. These UI extension points complement the existing `[Slot]` authoring mechanism, which fills widget content from Markdown blocks; declaring a UI extension does not require an author to add a content-slot block.

UI modules register contributions based on the current subject's type or interfaces and can test relevant metadata and resolved related subjects. An ordinary page with relationship metadata can therefore receive a context contribution without `$type`. Domain subjects expose facts and capabilities; their UI assemblies provide the components. Metadata describes meaning and relationships, not component class names, UI layout, or arbitrary executable rendering instructions.

Support multiple applicable contributors with deterministic ordering and stable identities. Empty or inapplicable contributions occupy no space. Contributions refresh when the metadata or the facts they read change, and release subscriptions and resources when the page or contribution leaves. Visibility and placement are application/user preferences unless an author explicitly embeds a widget. An extension failure should leave the document readable and identify the failed contribution rather than replace the whole page.

The current component registry resolves one Page/Edit/Setup/Widget component, with inheritance/interface fallback; its GetComponents enumeration only matches the exact subject type. Reuse the registration and context patterns, while defining ordered multi-contributor discovery for the new slots. Do not assume the existing single-component lookup already implements those semantics. Keep page-specific contribution dependencies in the UI layers rather than Namotion.Interceptor.Utils.

### Markdown link previews

The user confirmed that the context slot provides information without authored blocks and also wants ordinary Markdown links to act as entry points to richer information. When a link resolves to a subject, offer an overlay card with its title, subtitle, description, and applicable information. The Markdown source remains a standard link, with normal navigation preserved. This presentation does not turn a document reference into a typed architectural dependency.

Proposed default: provide a generic subject preview with an explicit open action and allow subject UI modules to contribute a compact summary through the same registration patterns. Reuse subject presentation data between previews and the context slot. Pointer hover and keyboard focus should expose the preview; touch needs an accessible explicit preview interaction. Unresolved or external links retain ordinary link behavior. Preview errors must not prevent navigation. Exact gesture behavior and card layout remain visual-design decisions; ordinary images keep their image semantics.

The user emphasized that rich, useful UI is part of the feature. Compact previews limit the initial footprint, not the information available: offer relevant facts and navigation into the context slot or an applicable additional view. Context slots, previews, service summaries, and deployment matrices are intended capabilities; their first-iteration boundary will be selected after the sample review. Proposed default: compose reusable subject presentation and contributions across these surfaces, with progressive disclosure for larger lists and details. Service UI makes selected hosts/projects, resources, relationships, and deployments explorable; deployment UI makes units, environments, manual versions, and links explorable. Provide clear loading, empty, unresolved, and error states and preserve the provenance of displayed facts. Exact styling and interactions remain visual-design decisions.

### Subject detail pages

The user asked whether JSON-defined subjects such as Resource should also have pages. Proposed direction: architecture subjects expose useful full-page details as well as compact widgets, previews, and context contributions, regardless of whether their configuration lives in JSON, a Markdown child declaration, or an inline block. Reuse a common detail presentation where practical and specialize for the available capabilities. Page availability and sidebar placement are separate decisions.

The current IPage interface supplies navigation title, icon, location, and alignment; it does not itself render a page. Rendering requires a registered Page component, and the component registry supports interface and base-class fallbacks. Keep UI registrations in UI assemblies. Exact shared presentation interfaces and whether each domain type implements a navigation interface are not yet decided. Resource does not need to inherit Service to reuse its page infrastructure.

### Generic widgets and architecture documents

Widgets are reusable architecture views independent of documentation format. Path queries and optional filters select data; presentation controls can select grouping, labels, visible subject and relationship kinds, detail, and layout. Services, Components, Context, and Deployment are proposed diagram presets, not arc42 section types or a fixed hierarchy imposed on the model. Exact configuration options remain under discussion. ServiceTable and DeploymentMatrix can supply domain-specific defaults over shared query and subject-presentation infrastructure.

Crosscutting pages can aggregate several services, deployment units, environments, and resources. Proposed master-detail behavior keeps the document visible while selection reveals details alongside the list, using the context slot where appropriate. Selection is local UI state and does not create a new domain subject. Links, previews, and drill-down views address the same underlying subjects.

The user wants one ordinary arc42 Markdown document with authored explanations and embedded tables, diagrams, and summaries over the shared domain. The same widgets also work in service pages, guides, ADRs, or other architecture documentation. The sample's arc42.md demonstrates all twelve headings without a special document type or one subject per section. It is a composition of reusable views, not a separate architecture model.

Manual canvases and generated diagrams share subjects and semantic relationships. A semantic connection authored on a diagram should create or edit the typed model relationship; position, size, grouping, and visual annotations belong to that view. Existing CanvasLayout supports positioned Widget references, dragging, snapping, and scaling, but connector lines need additional support. Auto-layout, manual arrangement, and their combination remain presentation choices. Runtime scenario order is authored separately from static relationships.

## Current data-model reference

The tables below record the logical model and current field proposals. Lowercase names match the sample's serialized configuration and illustrative graph paths; exact CLR names, path casing/aliases, identifiers, and public API signatures are not fixed. Authored query entries are separate from resolved collections, and references never transfer ownership or clone subjects. Existing title, icon, description, notes, tags, and links should be reused through the appropriate subject interfaces and metadata rather than repeated in a new generic enrichment store.

| Entity | Authored configuration | Resolved or discovered data | Ownership and cardinality |
|---|---|---|---|
| SourceRepository | `sourceRoot`; analyzer/scan options remain to be specified | `projects`, `hosts`, analysis status, diagnostics, last successful analysis, fact provenance | Owns discovered CodeProject and Host subjects; several repositories per graph |
| CodeProject | Discovered by an analyzer; manual enrichment through owner metadata | Stable repository-relative identity, `name`, source path, language, project classification, literal project references and other supported facts | Owned once by its SourceRepository; referenced by services, hosts, and other projects |
| Host | Analyzer-discovered or manually authored when source discovery is unavailable; exact manual schema remains open | Stable identity, `name`, optional entry CodeProject reference, known source/runtime/packaging facts | SourceRepository owns discovered hosts; an authored definition owns a manual host; either can be referenced by services or units |
| Service | `name`, `componentSources: PathQuery[]`; proposed extensible `kind` and optional `technology` | `components`, the direct Host subset `hosts`, query diagnostics; API/interface descriptions remain to be specified | Logical software capability or system, including neighboring enterprise systems; references existing subjects rather than cloning them |
| Resource | `name`, extensible `kind`, optional `technology` | Component-level dependencies such as databases, queues, or cloud offerings; relationships and reverse-use views; hosting and environment-instance mappings remain open | Authored once in JSON or as a child definition; service-specific or shared |
| DeploymentUnit | `name`, `hostSources: PathQuery[]` | Resolved `hosts`, query diagnostics | References hosts from one or several services; does not own the Host subjects |
| Environment | `name`, `deploymentUnitSources: PathQuery[]` | Resolved `deploymentUnits`, generated `deployments`, query diagnostics | Owns one enduring Deployment for each selected unit |
| Deployment | Generated environment and deployment-unit references; authored enrichment through the environment's metadata | Environment/unit identity, access to the unit's selected hosts; version and URLs through metadata | Owned by its Environment; common case is one per environment/unit pair |
| MarkdownFile, existing | Ordinary display frontmatter, `$<name>` child declarations, `metadata`, and Markdown content | Named configured children, parsed content blocks, and normal page rendering | Owns its declared subject children; remains distinct from a Service or Resource it defines |

Stable identity is a required semantic contract, not a decision to add an `id` field to every configuration. The generated-child lifecycle section defines identity requirements; exact serialized reference and identifier representations remain open. Discovered facts need provenance, uncertainty, and diagnostics; manual values must not silently overwrite them. A Deployment describes intent until observed data is supplied.

### Supporting records

| Record | Fields | Meaning |
|---|---|---|
| PathQuery | `path: string`, optional `where: string` | Resolve a subject, collection, or wildcard pattern, then filter matches; source lists combine and deduplicate results |
| Authored relationship | `kind`, `target`; optional `source`, `technology`, `label`, `message` | Metadata record; source defaults to the subject owning the scoped relationship list |
| Discovered fact or relationship | Value/endpoints plus source location, analyzer identity, uncertainty, and diagnostics as applicable | Analyzer result with provenance; concrete representation remains to be designed |
| View configuration | Data scope/query plus presentation settings | Widget or document-owned configuration; does not redefine subjects or imply architectural relationships |
| Context boundary, view-owned | Proposed `internalSources: PathQuery[]` and `externalSources: PathQuery[]` | Resolve the subjects shown inside and outside this diagram; the classification does not modify their domain subjects |

Relationship kinds describe interaction semantics, such as calls, publishes, consumes, reads, writes, or documents. Technology describes the mechanism, such as HTTPS, AMQP, or PostgreSQL; a queue is a target Resource rather than an interaction kind. The sample uses `message` for OrderPlaced. These are extensible values, not a closed runtime dependency taxonomy. Source overrides allow service-scoped authored metadata to describe known Host endpoints. Service-level edges retain unknown host endpoints rather than assigning them to guessed processes.

Authored relationships use the existing metadata API. JSON definitions persist through their metadata sidecar; Markdown persists the same envelope under frontmatter `metadata`. In the sample, `metadata.$subjects["Children[service]"].relationships` scopes the list to the defined Service child. This serialization is a working example of the existing metadata envelope; frontmatter persistence and its relationship readers are proposed functionality. Page-level metadata remains separate from child-level metadata. Analyzer-produced relationships remain discovered state.

## Domain

### Root, scope, and organization

The existing loaded HomeBlaze root remains the runtime graph root. Queries and widgets can use an arbitrary supplied subject/folder scope. A mandatory ArchitectureModel or System root has not been selected. Documentation structure can follow services, topics, crosscutting concerns, or a mixture. The sample groups each service document and its dedicated resources in one folder and keeps shared resources in a root resources folder; explicit references, not folder names, establish membership.

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

A service contains path-based component-source configuration and architectural descriptions and relationships. Its resolved components reference repository-owned code/hosts and authored resources; they do not clone them. It exposes its selected Host components through a resolved hosts collection, so a deployment unit can reference `service/hosts` directly. Additional typed subsets can be queried with ordinary paths and filters rather than a named-selection dictionary. Membership can overlap when explicitly configured. Shared code and shared resources retain one subject identity.

The user confirmed that neighboring enterprise systems are also modeled as Services. A service can be documented without known repositories or hosts. Resources sit alongside hosts at the component level: a database or consumed cloud offering can be a Resource, while an enterprise software system is a Service. The term "service" in a cloud provider's product name does not by itself make that offering an architecture Service. A provider system can still be documented as a Service when that logical system is relevant, without requiring a second subject for every resource. Exact provider/resource links remain open.

The sample uses Resource for the two database definitions. Their hosting and interface details are not yet supplied. Adding a resource or another service to an application's components does not automatically add underlying hosts to the application's resolved hosts. The direct Host subset remains explicit. APIs and interfaces can describe database protocols as well as HTTP; their structured representation and applicability across subjects remain to be specified.

### Resource

A reusable component-level dependency such as a database, queue, topic, storage bucket, or consumed cloud offering. A Service's component view can show Resources alongside Hosts. Services can include resources in components, and typed relationships can originate at a Service or a particular Host. A resource can be referenced by several services, and membership does not imply exclusive ownership. Including it in a service does not automatically provision it or include it in the current host-only DeploymentUnit proposal.

Host describes a runnable process; Resource describes a capability consumed by components. The user accepted a many-to-many provides relationship: a Host can provide zero or more Resources, and a Resource can have zero or more providing Hosts. This can describe a resource provided by several database-cluster processes without requiring a known Host for a managed cloud resource. Providing a resource is a reference relationship and does not transfer subject ownership. Exact serialized fields, the authored source of the relationship, and whether inverse navigation is derived remain open. Shared capabilities and presentation can be expressed through interfaces; Resource-to-Service inheritance has not been chosen.

The user confirmed that a Service can reference a database Host directly through componentSources. Resource is optional when that Host reference provides enough detail. For example, a PostgreSQL process can be a Host, and individual databases it exposes can be Resources when those distinctions matter. Neither an intermediate Resource nor a complete provider model is required. Component membership alone does not require deploying that Host together with the application's other Hosts; DeploymentUnit selections determine that grouping.

The sample resources are logical definitions independent of environments. Whether to represent environment-specific instances as separate subjects, mappings, or other data is not decided. Do not imply resource provisioning or a particular deployment lifecycle from the presence of a Resource subject.

### Internal/external classification and neighboring systems

The user confirmed that internal/external is relative to the diagram's documented system boundary, including neighboring systems operated by the same organization. The author chooses what is internal and external for each context diagram. Payments can be internal in the Commerce context and external in an Orders context without changing the Service subject. Organizational ownership is a separate concern and is not requested as a substitute for this boundary classification.

Proposed field shape: the diagram has internalSources and externalSources lists using the shared PathQuery record, with resolved internal and external sets as view state. Exact field spellings and rendering details remain open. The sample demonstrates both contexts using the same Orders and Payments subjects. Its block configuration uses PascalCase keys, as existing Markdown configuration fences do; persisted subject/frontmatter configuration follows its own naming convention.

Proposed defaults: explicit lists control the visible participants and relationships between them; diagnose an identity selected into both sets instead of cloning it or silently choosing a side. Optional automatic discovery of neighboring participants can be considered separately. Boundary membership and layout are view configuration, while semantic relationships stay in the shared model. No global internal/external flag or separate external-Service subtype is needed. Actors and their representation remain open; no mandatory System root has been selected.

### DeploymentUnit

A configurable selection of hosts deployed together. It can select all hosts of one service, a subset, or hosts from several services. There is no ownership or cardinality restriction that forces a deployment unit to correspond to one service.

The common arrangement is one service associated with one deployment unit. Service boundaries remain logical boundaries when the deployment arrangement changes.

### Environment

A configurable subject describing a deployment context, such as staging or production. It declares its deployment units and owns the generated Deployment subjects for those selections. Environments can be authored as JSON files or child definitions in Markdown frontmatter.

It does not generate deployments for every service or every deployment unit in the graph. Only declared selections produce deployments.

### Deployment

An enduring installation of a DeploymentUnit in an Environment, not an individual rollout event. Its version, URLs, and later observed state can change over time. The common case is one deployment per unit per environment; the model can be extended with installation identity if several installations of one unit in an environment are needed later.

Environment maintains one generated Deployment subject per selected unit. It reuses that subject while the environment/unit identity remains selected. It removes the generated subject when the pair is no longer selected. This maintains the current configured installations, not deployment history.

A generated deployment expresses the intended installation. It is not evidence that software is actually running. For the MVP, version and relevant URLs are manually configured data; no health or deployment connector populates them.

Use the existing metadata system for manually entered enrichment of generated subjects. The metadata of an environment stores entries for its Deployment descendants, keyed by their stable subject paths: in the sidecar of a JSON definition or the frontmatter of a Markdown definition. Version and URLs can use domain-specific metadata keys with typed accessors and editing support; notes, links, and tags use the existing keys. Configuration declares deployment-unit selection, and metadata holds enrichment. Do not create a second enrichment store inside the owner's configuration.

## Path queries and relationships

A path-query entry resolves subjects from an exact subject path, a collection-property path, or a subject-path pattern, then optionally applies a `where` property predicate. An exact collection path produces its members; an explicit `/*` can also select those immediate members. The mechanism should be usable outside this domain rather than restricted to an Artifact base class. Repository file-location predicates remain separate from subject-path matching.

Filters within one selector combine with AND. Proposed default: a list of selectors combines by set union; exclusions subtract from that result; results are deduplicated by identity. Support explicit references as the simplest selector. Deployment-unit selections validate that their results are hosts, and environment selections validate that theirs are deployment units. A wrong result kind produces a diagnostic rather than being converted into another kind of entity.

Consumers reference resolved subject properties through ordinary paths. A deployment unit can therefore select `service/hosts`, with no separate `selection` or `from` field. Proposed default: detect cyclic query dependencies and report invalid configuration; do not recursively expand them forever.

Source paths are repository-relative file locations. Subject paths identify subjects in the object graph. Namespaces are another future filter. These must not be silently treated as the same address space.

The current checkout has subtree matching in AgentWatchPaths, path-prefix/type/property filtering in the MCP search tool, and name wildcards in NuGetPackageNameMatcher. It does not have a general reusable path-glob matcher. Reuse the applicable behavior without introducing a dependency on the AI or NuGet plugin assembly. The NuGet matcher's `*` matches one or more characters and has no path-segment semantics, so it cannot be adopted as a path matcher unchanged.

Path globs use `/` separators, `*` within a path segment, and `**` across path segments. `*` does not cross a segment boundary, and `**` can span zero or more segments. Wildcards match subject-path segments or keys rather than display names; property conditions use `where`. Remaining matching rules, including casing and empty matches, must be specified once and tested by the implementation. The builder may choose an existing suitable dependency after checking the repository's requirements.

Selectors determine membership. Dependencies outside the selected scope remain external relationships and are not automatically added. Transitive membership expansion is deferred. Project references describe code dependencies; they do not by themselves establish runtime communication between services. Authored architectural relationships and their evidence remain distinguishable from discovered code-reference edges.

Unresolved explicit references and invalid predicates are diagnostics. A syntactically valid selection with no matches is an empty result, distinguishable from failed analysis. A temporary input failure must not erase the last successful discovered graph or the authored selection.

Proposed default: successful publication is atomic per repository. An incomplete directory listing, a required source read failure, or a malformed project that prevents analysis fails that repository's pass and retains its entire last successful graph. Other repositories can publish independently. Preserve the failure diagnostics separately; on an initial failed pass there is no successful graph to show yet. A complete pass can publish confirmed removals. An unresolved condition or reference that the static analyzer intentionally represents as unknown is a diagnostic on a successful pass, not a reason to pretend the pass failed. Partial per-project stale-result merging is deferred.

## Source access and documentation mounts

A typical setup mounts `/repo/docs` in HomeBlaze and configures SourceRepository to analyze `/repo`. It does not mount the whole repository merely to make its contents readable. SourceRepository exposes discovered architectural elements rather than a file subject for every source file.

The MVP supports local folders only. Proposed default: use FluentStorage's raw listing, read, and metadata capabilities where practical, behind the smallest access contract needed by analyzers. Reuse existing suitable contracts before introducing another abstraction. The current IStorageContainer includes subject lifecycle and writes and has no general raw recursive enumeration API, so it is not automatically the analyzer contract.

File locations such as `../` resolve relative to the defining configuration file's directory when it has a local file origin. SourceRepository then uses its resolved root as the boundary for source traversal. Subject references continue to use the existing subject path resolver. A relative file location must not be interpreted as a request to traverse above a mounted blob container through IStorageContainer.

For the MVP, defining SourceRepository in a JSON file or Markdown frontmatter provides an unambiguous local origin through the owning file. Proposed default: where no local file origin exists, require an absolute source root and report a configuration diagnostic for an ambiguous relative path. Do not guess from the process working directory. Future storage backends can supply a storage identity and a root path instead of a local filesystem location.

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

Candidate placement: a dedicated `Namotion.Interceptor.Utils` library for reusable subject helpers, including selection and keyed reconciliation where their dependencies fit. No such project exists in the current checkout. Keep its public API headless and independent of ArchDocs, storage, and device implementations. Keep pure collection primitives separate from helpers that require tracking or hosting, and do not make the core library depend on the utility package. Final package naming, dependency boundaries, and any later promotion of primitives into core remain implementation-design decisions.

Proposed rollout: establish the small contracts against SourceRepository and Environment and validate them against at least one representative existing reconciliation use before declaring the API broadly reusable. Agree any existing-consumer migration separately; avoid coupling the architecture MVP to a repository-wide refactor. Core publication is a later API-placement decision unless the extraction clearly fits the existing dependency graph and is approved with the design.

Connector CollectionDiffBuilder produces ordered transport patches rather than maintains discovered child identity, and LifecycleInterceptor reconciles references under its own graph invariants. Similar terminology does not make those implementations interchangeable with the proposed collection helper. Reuse individual primitives there only when their contracts actually match.

## Authored data and discovered state

Authored configuration determines logical intent, selectors, and source locations. Metadata holds authored relationships, manual descriptive enrichment, and manual deployment details. Analyzer results and generated child collections are state. Computed summaries and resolved views use the existing derived-property mechanism where appropriate; generated objects are not recreated by a derived getter.

Enrichment has one canonical persisted home per owner, as specified in Markdown Subject Definitions and Metadata. StorageMetadataApplier already applies a stored JSON subject's sidecar to its descendants, including children that attach after the sidecar was loaded, and retains unknown keys and JSON values. Extend equivalent behavior to Markdown frontmatter. Thus repository metadata can enrich discovered project and host children, and environment metadata can enrich generated deployments, provided their canonical ownership and paths are stable. Existing metadata limits still apply. Add typed readers, validation, and editors for domain-specific fields; generic preservation of a key does not make it a typed configuration property or an automatically generated editor field. Do not persist competing editable copies of discovered facts. Define any supported override explicitly and show the discovered value's provenance separately.

The basic subject UI should expose applicable configuration, state, diagnostics, and refresh operations through existing interfaces and generated editors. Project reference edges and selection membership must be traversable through the subject graph without requiring a custom visual graph renderer.

The earlier UI slice used existing subject UI plus a service-summary widget and deployment matrix. Discussion has since expanded the desired capability to include resource details, master-detail exploration, manual canvases, and generated diagrams at several levels. Their first-iteration placement is now open pending the sample review. A service summary reads the Service and its selected code/hosts, resources, relationships, and deployments. Proposed default: show useful descriptive facts, counts, and links without duplicating the domain configuration. A document defining a service retains normal Markdown rendering; summaries are optional embeds or page contributions. The deployment matrix shows units against environments using declared deployments and manual versions/links; it must not display an unobserved deployment as healthy. Project-reference edges remain navigable independently of a custom diagram renderer.

The user also proposed a ServiceTable widget scoped to a subject subtree, illustratively `ServiceTable Path=Project1/`. It finds Service subjects within that scope regardless of whether their definitions came from JSON, frontmatter, or inline blocks, and presents a service-oriented overview. Proposed default: include owned descendants recursively, deduplicate subject identities, and do not expand scope by following architectural relationship edges or selection references. Use shared scope/type-selection infrastructure rather than a separate filesystem scan or analyzer. Proposed columns include service name and description, host/project counts, and deployment summaries with navigation and previews. Rows and displayed facts follow graph changes. Existing SubjectTable already supports a source and type filter, but lists the source's immediate subjects rather than recursively discovering services under document children, and does not provide architecture-specific presentation. Reuse suitable table and query primitives; exact component names, path-property naming, and presentation remain builder choices.

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

These scenarios describe future implementation and testing. The working sample contains fixture projects and proposed data definitions, but no ArchDocs product implementation has been authorized by this specification.

## Constraints and builder choices

Follow the repository's subject-creation guidance and existing conventions: partial intercepted properties, configuration versus state, internal setters for owned state, dictionaries for stable discovered identity, replaced published collections, headless domain code, and separate UI code. Reuse title, icon, monitoring, last-updated, path-reference, and metadata interfaces where applicable.

The builder chooses exact project names, concrete interfaces, JSON property names, worker primitives, caching strategy, and the smallest reusable helper API that satisfies the contracts above. A proposed Namotion.ArchDocs name is inherited from PR #257, not a new user decision. An implementation plan must identify required public API changes, tests for selector and lifecycle correctness, and targeted UI tests for page contributions, link previews, and widgets. Long-running benchmarks or connector verification require agreement before they are scheduled; neither is implied by this design.

## Open decisions and review status

The sample files and this specification should be updated together as the model changes. Entity semantics, field proposals, metadata shape, and view behavior are recorded here; the sample README explains how its concrete arrangement works. Earlier builder checks found no remaining planning questions for the narrower design at that point. Subsequent resources, diagrams, page presentation, and boundary discussion extend it, so that result is not approval of the current broader specification.

The following decisions remain open:

- Context representation: internal/external is confirmed as diagram-relative; exact boundary fields, rendering behavior, optional neighbor discovery, and actor representation remain open. Neighboring software systems use Service.
- Service and Resource roles: Service represents a logical system/capability; Resources such as databases and cloud offerings appear alongside Hosts at component level. Host-to-Resource provides references are optional and many-to-many. Define shared interfaces and the exact persistence/navigation of provider relationships without assuming inheritance.
- Deployment participation: the current DeploymentUnit schema selects Hosts. Decide how Resources participate in units and environment deployments, including the distinction between using an existing managed resource and deploying or provisioning one.
- Resource realization: environment-specific resource instances, infrastructure targets, and their mappings to logical resources and deployments.
- Interfaces and endpoints: represent the APIs/protocols exposed by services and hosts, including database protocols, without assuming every API is HTTP.
- Reference-domain coverage: decide the logical interface, environment endpoint, contract-document/version, package/artifact, and actor concepts demonstrated by Apimundo; determine their ownership, relationships, and MVP scope separately.
- Executable roles: represent migrations/release steps and local orchestration in the AppHost use case without treating every runnable project as an always-running deployed process.
- Presentation contracts: useful subject detail pages, navigation participation, shared page/widget/preview components, and exact UI slot registration.
- Final schemas and identity: CLR/serialized names, path casing and aliases, generated keys, persistent identity and rename behavior, and raw-source path matching where graph paths are insufficient.
- View configuration: generic diagram presets and customization, boundary selection, grouping shared components, manual/automatic layout interaction, and semantic connector editing.
- Runtime scenarios: plain authored Markdown suffices for the current example; a structured scenario/sequence model is not selected.
- Initial implementation: which candidate behaviors, exclusions, and UI surfaces form the MVP after sample review, including any long-running verification that must be agreed during planning.

Useful rich UI, format-independent widgets, ordinary Markdown authoring, named frontmatter children, generic path queries, repository-owned discovery, and Environment-owned deployments are the current direction. Remaining open decisions must not be presented as implemented or user-approved schema. Finalize the domain and MVP, obtain design approval, and only then produce the implementation plan.
