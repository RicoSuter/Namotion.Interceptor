---
title: ArchDocs Specifications
navTitle: ArchDocs Specifications
status: Planned
---

# ArchDocs Specifications [Planned]

The durable feature reference for the generic architecture-documentation domain, including element specifications, examples, usage scenarios, status, and scope decisions.

## Implementation readiness

**Delivery:** See the readiness matrix; subjects and explicit views can be extensions, while automatic Markdown/page integration needs host work.

**Prerequisites:** The design and MVP remain under discussion. These are element specifications, not implementation tasks.

## Intent

Describe and explore software through ordinary Markdown documentation, authored subject configuration, discovered facts, and reusable views over one shared graph. The model does not prescribe language, source layout, cloud, process topology, organizational structure, or documentation template. AppHost and Apimundo are reference samples, not special cases in the domain API.

ArchDocs is an architecture-validation framework with documentation and linked views. The primary goal is keeping documentation aligned with the codebase and interlinked with minimal duplicated authoring. Discover supported facts, apply reusable membership rules, and derive structural views and backlinks. Authors supply business intent, explanations, and facts that the analyzers cannot establish. Architecture validation should guide humans and agents toward correct source edits locally and in CI; operational dashboards are an optional additional direction.

## Specification map

- [Subject model](subject-model.md): conceptual UML of all subjects and capabilities, with focused ownership, reference, and inheritance views.
- [Scenarios](scenarios/overview.md): standalone JSON, frontmatter and inline Markdown, arc42, topics/decisions, libraries, and multiple repositories.
- [Scope and design decisions](scope-and-decisions.md) and [verification scenarios](verification-scenarios.md): rationale, open decisions, and behavior to verify.
- [File authoring and readability](file-authoring.md): understand and edit the complete authored model as text, independently of the UI.
- [Implementation readiness](implementation-readiness.md): implemented platform support, planned extensions, and required HomeBlaze changes.
- Subjects below: canonical semantics, configuration/state boundaries, references, subject-specific widgets/detail pages/editors, and unresolved schemas.
- [Analyzers](analyzers/overview.md): read-only source and contract analysis.
- [General widgets](widgets/overview.md): reusable aggregating views such as tables, deployment matrices, and architecture diagrams. Subject-specific presentation stays in each subject file.
- [Architecture tests and rules](rules/overview.md), [shared evaluation](rules/architecture-checks.md), and [CLI/CI validation](integration/cli-and-ci.md): selectable Rule subjects with actionable, source-located errors for humans and agents. Start with the [discovery and validation scenario](scenarios/architecture-validation.md).
- [Identifiers](shared/identifiers.md), [path queries](shared/path-queries.md), [metadata and relationships](shared/metadata-and-relationships.md), and [reconciliation](shared/reconciliation.md): shared behavior.
- [Markdown authoring](integration/markdown-authoring.md) and [page contributions](integration/page-contributions.md): optional host extensions.
- [Release and deployment integration](integration/delivery-pipelines.md): proposed connections to GitHub Actions and other pipeline providers, with explicit architecture mappings.

## Status and scope

All ArchDocs functionality is [Planned]. Existing host facilities are marked [Implemented] only in the readiness matrix and relevant integration sections. [Proposed] marks open schema or design choices. These tags track delivery state and do not select an MVP, settle every field name, or authorize product implementation. Readiness describes where work belongs, independently of delivery status.

## Subjects

All subjects, references, metadata, and widgets follow the [file-authoring requirement](file-authoring.md). Their serialized definitions and relationships must be understandable and editable by people and agents without opening HomeBlaze; useful UI is an additional presentation and editing path.

Each subject page begins with its class or capability definition, configuration properties, and read-only state properties. Related subject types link to their definitions. These tables describe a proposed schema; they do not declare implemented CLR signatures.

Within the property tables, present the subject's own master data first, then structural references and selections. See [property presentation and authoring](file-authoring.md#schemas-and-examples-planned) for the shared convention.

The groups below follow recommended product and reading priority. Implementation dependencies and the MVP scope remain separate decisions; delivery prerequisites are described in the linked specifications and the [readiness matrix](implementation-readiness.md).

### 1. Core architecture

Start here to describe the software, its runnable parts, and the capabilities it uses.

| Specification | Status | Delivery |
|---|---|---|
| [Service](subjects/service.md) | [Planned] | Extension; see prerequisites |
| [Host](subjects/host.md) | [Planned] | Extension; see prerequisites |
| [Resource](subjects/resource.md) | [Planned] | Extension; see prerequisites |
| [Component](subjects/component.md) | [Planned] | Extension; see prerequisites |

### 2. Source discovery

Connect the architecture to source code and enrich it through shared analyzers. Solution grouping adds useful build context after projects are available.

| Specification | Status | Delivery |
|---|---|---|
| [SourceRepository](subjects/source-repository.md) | [Planned] | Extension; see prerequisites |
| [CodeProject](subjects/code-project.md) | [Planned] | Extension; see prerequisites |
| [Analyzer](subjects/analyzer.md) | [Planned] | Shared singleton subject; registration and graph publication required |
| [CodeSolution](subjects/code-solution.md) | [Planned] | Extension; see prerequisites |

### 3. Architecture tests

Select typed rules as architecture unit tests; reuse JSON definitions and documented Markdown children across repositories and UI/CLI execution.

| Specification | Status | Delivery |
|---|---|---|
| [Rule](subjects/rule.md) | [Planned] | Abstract base; shared configuration, presentation, and evaluation contract |
| [NamingRule](rules/naming-rule.md) | [Planned] | Typed property/pattern validation |
| [RequiredPropertyRule](rules/required-property-rule.md) | [Planned] | Typed required-value and metadata validation |
| [MembershipRule](rules/membership-rule.md) | [Planned] | Typed membership counts and selections |
| [AllowedDependenciesRule](rules/allowed-dependencies-rule.md) | [Planned] | Typed category-based dependency policy |
| [HostsAssignedToServicesRule](rules/hosts-assigned-to-services-rule.md) | [Planned] | Predefined Host-to-Service policy |

### 4. Deployment architecture

Describe coordinated releases and their intended installations in selected environments.

| Specification | Status | Delivery |
|---|---|---|
| [DeploymentUnit](subjects/deployment-unit.md) | [Planned] | Extension; see prerequisites |
| [Environment](subjects/environment.md) | [Planned] | Extension; see prerequisites |
| [Deployment](subjects/deployment.md) | [Planned] | Extension; see prerequisites |

### 5. Interfaces and public contracts

Add logical access, environment addresses, and public API detail as the documentation needs it.

| Specification | Status | Delivery |
|---|---|---|
| [Interface](subjects/interface.md) | [Planned] | Extension; see prerequisites |
| [Endpoint](subjects/endpoint.md) | [Planned] | Extension; see prerequisites |
| [Contract](subjects/contract.md) | [Planned] | Extension; see prerequisites |
| [OpenApiContract](subjects/openapi-contract.md) | [Planned] | Extension; see prerequisites |
| [CSharpApiContract](subjects/csharp-api-contract.md) | [Planned] | Extension; see prerequisites |
| [ProtobufContract](subjects/protobuf-contract.md) | [Planned] | Extension; see prerequisites |

### 6. Optional additions

Extend context views with people or roles, and describe produced packages or images when those details become useful.

| Specification | Status | Delivery |
|---|---|---|
| [Actor](subjects/actor.md) | [Planned] | Optional later concept |
| [Artifact](subjects/artifact.md) | [Planned] | Optional later concept |

## Root and documentation scope [Planned]

The existing loaded HomeBlaze root remains the runtime graph root. Queries and widgets can use an arbitrary supplied subject/folder scope. A mandatory ArchitectureModel or System root has not been selected. Documentation structure can follow services, topics, crosscutting concerns, or a mixture. The sample groups each service document and its dedicated resources in one folder and keeps shared resources in a root resources folder; explicit references, not folder names, establish membership.

## Graph rules [Planned]

Component is a shared capability, with Service, Host, Resource, and CodeProject participating. Services select components; Hosts can provide Resources; CodeProjects and Interfaces can reference Contracts; Endpoints bind Interfaces to environments; DeploymentUnits select explicit release participants; Environments own generated Deployments. Cross-references do not transfer ownership. Discovery and architectural membership are separate, and internal/external classification belongs to a view.

CodeSolution records source/build grouping and references existing repository-owned CodeProjects. Projects can belong to several solutions or none, and contribute to zero or many Hosts. Neither solution membership nor project references establish a Service or deployment boundary.

SourceRepositories select shared singleton Analyzer subjects and authored Rule definitions. Rules can live in one JSON file per definition or as named children in `rules.md`; repositories select page/directory/global suites with shared path queries. Rules consume available facts and authored architecture, and per-repository results stay on the invocation owner. See [rule authoring](rules/authoring.md). Selecting one references the existing instance; it does not create a repository-owned analyzer. Each repository owns its source boundary, analysis work, results, diagnostics, and generated subjects. Registering or publishing an Analyzer does not by itself execute analysis or select it for every repository.

Contract specializations are OpenApiContract, ProtobufContract, and CSharpApiContract. Authored OpenAPI/protobuf descriptors select their raw document through `path`; `codeProjectPath` explicitly selects an associated graph subject. Without an explicit association, discovery considers only a unique CodeProject in the raw document's own directory. See [contract files and discovery](scenarios/contract-files.md). Keep shared contract references independent of a parser or transport. Exact CLR interfaces, fields, ownership where noted, and version identity remain open in their specifications.

## Reference ownership

This feature reference is self-contained. Requirements, design decisions, examples, and verification scenarios belong here and must remain understandable without session notes or temporary planning artifacts. Keep one canonical page per concept and link to it from other pages. Document subject-specific widgets, detail pages, and editors in that subject's Markdown file. Put general and aggregating views in widgets/. Update status and readiness when implementation lands. Session notes and delivery plans may point here; the reference does not depend on them.

The [scenarios](scenarios/overview.md) contain the JSON and Markdown needed to understand different documentation styles. The separate [Commerce development fixture](../../ArchDocs/README.md) is optional editable test material with buildable discovery inputs; it is not part of published seed packaging.

## Related specifications

[Overview and status legend](overview.md) · [Platform readiness](implementation-readiness.md)
