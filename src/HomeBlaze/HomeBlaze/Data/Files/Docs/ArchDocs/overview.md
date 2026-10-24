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

## Specification map

- [Usage examples](examples/overview.md): standalone JSON, frontmatter and inline Markdown, arc42, topics/decisions, libraries, and multiple repositories.
- [Scope and design decisions](scope-and-decisions.md) and [verification scenarios](verification-scenarios.md): rationale, open decisions, and behavior to verify.
- [File authoring and readability](file-authoring.md): understand and edit the complete authored model as text, independently of the UI.
- [Implementation readiness](implementation-readiness.md): implemented platform support, planned extensions, and required HomeBlaze changes.
- Subjects below: canonical semantics, configuration/state boundaries, references, subject-specific widgets/detail pages/editors, and unresolved schemas.
- [Analyzers](analyzers/overview.md): read-only source and contract analysis.
- [General widgets](widgets/overview.md): reusable aggregating views such as tables, deployment matrices, and architecture diagrams. Subject-specific presentation stays in each subject file.
- [Path queries](shared/path-queries.md), [metadata and relationships](shared/metadata-and-relationships.md), and [reconciliation](shared/reconciliation.md): shared behavior.
- [Markdown authoring](integration/markdown-authoring.md) and [page contributions](integration/page-contributions.md): optional host extensions.

## Status and scope

All ArchDocs functionality is [Planned]. Existing host facilities are marked [Implemented] only in the readiness matrix and relevant integration sections. [Proposed] marks open schema or design choices. These tags track delivery state and do not select an MVP, settle every field name, or authorize product implementation. Readiness describes where work belongs, independently of delivery status.

## Subjects

All subjects, references, metadata, and widgets follow the [file-authoring requirement](file-authoring.md). Their serialized definitions and relationships must be understandable and editable by people and agents without opening HomeBlaze; useful UI is an additional presentation and editing path.

| Specification | Status | Delivery |
|---|---|---|
| [Component](subjects/component.md) | [Planned] | Extension; see prerequisites |
| [SourceRepository](subjects/source-repository.md) | [Planned] | Extension; see prerequisites |
| [CodeProject](subjects/code-project.md) | [Planned] | Extension; see prerequisites |
| [Host](subjects/host.md) | [Planned] | Extension; see prerequisites |
| [Service](subjects/service.md) | [Planned] | Extension; see prerequisites |
| [Resource](subjects/resource.md) | [Planned] | Extension; see prerequisites |
| [Interface](subjects/interface.md) | [Planned] | Extension; see prerequisites |
| [Endpoint](subjects/endpoint.md) | [Planned] | Extension; see prerequisites |
| [Contract](subjects/contract.md) | [Planned] | Extension; see prerequisites |
| [OpenApiContract](subjects/openapi-contract.md) | [Planned] | Extension; see prerequisites |
| [ProtobufContract](subjects/protobuf-contract.md) | [Planned] | Extension; see prerequisites |
| [CSharpApiContract](subjects/csharp-api-contract.md) | [Planned] | Extension; see prerequisites |
| [DeploymentUnit](subjects/deployment-unit.md) | [Planned] | Extension; see prerequisites |
| [Environment](subjects/environment.md) | [Planned] | Extension; see prerequisites |
| [Deployment](subjects/deployment.md) | [Planned] | Extension; see prerequisites |
| [Artifact](subjects/artifact.md) | [Planned] | Optional later concept |
| [Actor](subjects/actor.md) | [Planned] | Optional later concept |

## Root and documentation scope [Planned]

The existing loaded HomeBlaze root remains the runtime graph root. Queries and widgets can use an arbitrary supplied subject/folder scope. A mandatory ArchitectureModel or System root has not been selected. Documentation structure can follow services, topics, crosscutting concerns, or a mixture. The sample groups each service document and its dedicated resources in one folder and keeps shared resources in a root resources folder; explicit references, not folder names, establish membership.

## Graph rules [Planned]

Component is a shared capability, with Service, Host, Resource, and CodeProject participating. Services select components; Hosts can provide Resources; CodeProjects and Interfaces can reference Contracts; Endpoints bind Interfaces to environments; DeploymentUnits select explicit release participants; Environments own generated Deployments. Cross-references do not transfer ownership. Discovery and architectural membership are separate, and internal/external classification belongs to a view.

Contract specializations are OpenApiContract, ProtobufContract, and CSharpApiContract. Keep shared contract references independent of a parser or transport. Exact CLR interfaces, fields, ownership where noted, and version identity remain open in their specifications.

## Reference ownership

This feature reference is self-contained. Requirements, design decisions, examples, and verification scenarios belong here and must remain understandable without session notes or temporary planning artifacts. Keep one canonical page per concept and link to it from other pages. Document subject-specific widgets, detail pages, and editors in that subject's Markdown file. Put general and aggregating views in widgets/. Update status and readiness when implementation lands. Session notes and delivery plans may point here; the reference does not depend on them.

The [usage examples](examples/overview.md) contain the JSON and Markdown needed to understand different documentation styles. The separate [Commerce development fixture](../../ArchDocs/README.md) is optional editable test material with buildable discovery inputs; it is not part of published seed packaging.

## Related specifications

[Overview and status legend](overview.md) · [Platform readiness](implementation-readiness.md)
