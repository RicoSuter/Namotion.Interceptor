---
title: Implementation Readiness
navTitle: Implementation Readiness
status: Planned
---

# Implementation Readiness [Planned]

Separate already implemented platform facilities, extension work, and changes required in HomeBlaze.

## Implementation readiness

**Delivery:** This is a dependency classification, not an implementation plan or MVP commitment.

**Prerequisites:** Current source evidence below; individual subject and feature specifications list their domain prerequisites.

## Status legend

[Implemented] means behavior exists in this checkout. [Planned] means specified but unimplemented and does not select it for the MVP. [Proposed] marks a design or schema choice awaiting resolution. Existing platform support is not evidence that an ArchDocs type or widget exists.

Delivery readiness is a separate axis: **Extension** can use existing host contracts once its listed domain dependencies exist; **HomeBlaze change** needs storage/rendering/page-shell work; **Optional shared extraction** improves reuse and does not block an extension implementation. Ready as an extension does not mean every API signature or product decision is final.

## Capability matrix

Area names identify implementation responsibility. ArchDocs covers the domain extension, providers, widgets, and provider adapters; HomeBlaze covers generic authoring/storage/UI integration; Namotion.Interceptor covers reusable subject helpers where their dependencies fit. Multiple areas indicate shared work. These are ownership areas, not fixed project/package names, and optional helper extraction does not require moving domain behavior into core.

| Capability | Area | Status | Delivery and dependency |
|---|---|---|---|
| Subject JSON configuration and registration | HomeBlaze | [Implemented] | Existing platform |
| Registered subject page, edit, and widget components | HomeBlaze | [Implemented] | Existing platform; IPage is navigation metadata |
| Explicit configurable body blocks and named block references | HomeBlaze | [Implemented] | Existing Markdown authoring; new types still need registration/components |
| JSON-sidecar metadata and descendant enrichment | HomeBlaze | [Implemented] | Existing metadata store and writer |
| Inline paths for named Markdown children and registered path writing | HomeBlaze | [Implemented] | Reuse child names such as `service`; frontmatter loading/persistence remains separate planned work |
| Plain links and manual CanvasLayout nodes | HomeBlaze | [Implemented] | Normal navigation/placement; no semantic connector lines |
| All ArchDocs subject types and contract specializations | ArchDocs | [Planned] | Extension; capability model, references, and registration |
| Identifiers with plain-text, generic-link, and typed presentation | ArchDocs / HomeBlaze | [Planned] | Shared authoring/resolution/presentation behavior; existing path resolution and pickers are primitives; exact syntax remains deferred |
| Repository/solution/project discovery and contract-file association/parsing | ArchDocs | [Planned] | Extension; selected read-only source and provider contracts; file recognition/association does not require full parsing |
| Shared singleton Provider subjects selectable by repositories | ArchDocs | [Planned] | Extension with singleton registration and graph-publication wiring; DI singleton registration alone does not publish a selectable subject |
| Generic wildcard selection and typed relationship readers | ArchDocs; optional shared helpers in Namotion.Interceptor | [Planned] | Extension/shared services; existing resolver alone is insufficient |
| Reconciled repository children and Environment Deployments | ArchDocs | [Planned] | Extension; lifecycle, identity, worker, and metadata prerequisites |
| Subject-specific widgets, detail pages, and editors | ArchDocs | [Planned] | Extension; specified with each subject, with domain/parser prerequisites as applicable |
| General tables, matrix, and diagrams | ArchDocs | [Planned] | Registered views specified in widgets/, with domain/query prerequisites; live structural diagrams additionally need the shared Canvas capabilities below |
| Live Canvas sources, manual additions, and layout overrides | HomeBlaze / ArchDocs | [Planned] | Reuse existing Canvas/widget rendering; add generic automatic-node, stable-layout, and connector support where needed, with ArchDocs queries and relationship adapters. No automatic-node delete/hide action in the first version |
| Dependency/layer grouping and impact views | ArchDocs | [Planned] | Shared graph queries, evidence-preserving aggregation, reverse-reference traversal, and supported documentation backlinks; see [DependencyDiagram](widgets/dependency-diagram.md) and [ImpactView](widgets/impact-view.md) |
| Linked authored interaction scenarios | ArchDocs | [Planned] | [ScenarioDiagram](widgets/scenario-diagram.md), participant validation, and sequence rendering; trace ingestion is separate |
| Documented Analyzer subjects and architecture-test suites | ArchDocs | [Planned] | [Analyzer definitions](subjects/analyzer.md), JSON/named Markdown authoring, exact/page/directory/global selection, repository-owned results, and generic/predefined concrete Analyzer types |
| File-authored architecture rules and actionable findings | ArchDocs | [Planned] | Headless [analyzer evaluator](rules/architecture-checks.md), current graph, required-fact validation, source provenance, remediation/help links, and registered concrete Analyzer types |
| CLI/CI architecture validation and agent-readable reports | ArchDocs / HomeBlaze | [Planned] | [Headless command adapter](integration/cli-and-ci.md) over normal shared graph loading, lifecycle, and provider publication; explicit readiness for current discovery/reconciliation, shared rules, text/JSON reports, and unsuccessful exits for violations or incomplete validation |
| Supported Markdown-reference indexing and backlinks | ArchDocs | [Planned] | Link parsing, file/subject target mapping, invalidation, and reference provenance; distinct from runtime relationships and a general prose-extraction engine |
| Architecture-check result widgets and diagram annotations | ArchDocs | [Planned] | Reuse shared findings through [ArchitectureCheckResults](widgets/architecture-check-results.md); automatic surrounding-page placement separately needs host work |
| Generic $name frontmatter subject children | HomeBlaze | [Planned] | HomeBlaze change: parsing, lifecycle, collision handling, and writeback |
| Metadata envelope persistence in Markdown frontmatter | HomeBlaze | [Planned] | HomeBlaze change: storage apply/write and preservation of unrelated content |
| Page-level actions/context/additional-view contributions | HomeBlaze | [Planned] | HomeBlaze change: page shell and ordered inherited multi-contributor discovery |
| Rich overlays for ordinary Markdown links | HomeBlaze | [Planned] | HomeBlaze change: renderer integration, access/navigation, and preview lifecycle |
| Manual canvas semantic connector editing | HomeBlaze / ArchDocs | [Planned] | Generic Canvas connection support plus typed relationship editing through canonical model writers; current Canvas does not persist connectors |
| Shared reconciliation/selection utilities | Namotion.Interceptor | [Planned] | Optional shared extraction; no Namotion.Interceptor.Utils project exists yet |
| Readable file schemas, complete authoring examples, diagnostics, and focused writeback | ArchDocs / HomeBlaze | [Planned] | Cross-cutting requirement; applies to extensions and to host storage/writer changes where needed; exact tooling remains open |
| Portable diagram publishing | ArchDocs; HomeBlaze for automatic image replacement | [Proposed] | Deferred beyond the initial live-widget direction; Mermaid/SVG format, generation commands, and refresh policy are not selected |
| Pipeline/release/deployment provider integration | ArchDocs | [Proposed] | Optional connector and explicit mappings; see [release and deployment integration](integration/delivery-pipelines.md); observation versus control and MVP inclusion remain open |

## A delivery boundary that is possible now [Proposed]

An extension can start with JSON subjects, explicit body blocks, source discovery, exact-path membership, typed relationships in JSON-sidecar metadata, and explicitly embedded views. General selectors and reconciliation still need implementation, but do not require the page-shell changes. Build the same domain model so frontmatter and automatic page contributions can be added later without replacing its subjects. This is an available sequencing option, not an approved MVP or task plan.

## Source evidence [Implemented]

Readiness for extension implementation does not waive the [file-authoring requirement](file-authoring.md). Existing serializers and entry points must be evaluated against its preservation and readability guarantees; not all planned guarantees follow from the ability to load JSON or render Markdown.

- [Subject registration and JSON configuration](../../../../../HomeBlaze.Abstractions/IConfigurable.cs)
- [JSON configuration serializer](../../../../../HomeBlaze.Services/ConfigurableSubjectSerializer.cs)
- [Root loading and context attachment](../../../../../HomeBlaze.Services/RootManager.cs)
- [Shared subject context with tracking, registry, lifecycle, and hosted services](../../../../../HomeBlaze.Services/SubjectContextFactory.cs)
- [Hosted storage loading](../../../../../HomeBlaze.Storage/FluentStorageContainer.cs)
- [Explicit body-block subjects](../../../../../HomeBlaze.Storage/Internal/MarkdownContentParser.cs)
- [Widget/Page/Edit resolution](../../../../../HomeBlaze.Services/Components/SubjectComponentRegistry.cs)
- [Page navigation interface](../../../../../HomeBlaze.Components.Abstractions/Pages/IPage.cs)
- [Current frontmatter fields](../../../../../HomeBlaze.Storage/Files/MarkdownFrontmatter.cs)
- [Current Markdown child lifecycle](../../../../../HomeBlaze.Storage/Files/MarkdownFile.cs)
- [JSON metadata writer](../../../../../HomeBlaze.Storage/Metadata/StorageMetadataWriter.cs)
- [Immediate subject table rows](../../../../../HomeBlaze.Components/SubjectTableRows.cs)
- [Manual canvas](../../../../../HomeBlaze.Components/CanvasLayout.cs)

The source was inspected while splitting this specification. Update status and evidence when implementations land; a documentation-only PR cannot mark the feature implemented.

## Related specifications

[Overview and status legend](overview.md) · [Platform readiness](implementation-readiness.md)
