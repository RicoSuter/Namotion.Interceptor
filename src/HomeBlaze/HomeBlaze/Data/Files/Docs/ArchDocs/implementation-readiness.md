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

| Capability | Status | Delivery and dependency |
|---|---|---|
| Subject JSON configuration and registration | [Implemented] | Existing platform |
| Registered subject page, edit, and widget components | [Implemented] | Existing platform; IPage is navigation metadata |
| Explicit configurable body blocks and named block references | [Implemented] | Existing Markdown authoring; new types still need registration/components |
| JSON-sidecar metadata and descendant enrichment | [Implemented] | Existing metadata store and writer |
| Inline paths for named Markdown children and registered path writing | [Implemented] | Reuse child names such as `service`; frontmatter loading/persistence remains separate planned work |
| Plain links and manual CanvasLayout nodes | [Implemented] | Normal navigation/placement; no semantic connector lines |
| All ArchDocs subject types and contract specializations | [Planned] | Extension; capability model, references, and registration |
| Repository/solution/project discovery and contract-file association/parsing | [Planned] | Extension; selected read-only source and analyzer contracts; file recognition/association does not require full parsing |
| Generic wildcard selection and typed relationship readers | [Planned] | Extension/shared services; existing resolver alone is insufficient |
| Reconciled repository children and Environment Deployments | [Planned] | Extension; lifecycle, identity, worker, and metadata prerequisites |
| Subject-specific widgets, detail pages, and editors | [Planned] | Extension; specified with each subject, with domain/parser prerequisites as applicable |
| General tables, matrix, and diagrams | [Planned] | Extension; specified in widgets/, with domain/query/renderer prerequisites |
| Generic $name frontmatter subject children | [Planned] | HomeBlaze change: parsing, lifecycle, collision handling, and writeback |
| Metadata envelope persistence in Markdown frontmatter | [Planned] | HomeBlaze change: storage apply/write and preservation of unrelated content |
| Page-level actions/context/additional-view contributions | [Planned] | HomeBlaze change: page shell and ordered inherited multi-contributor discovery |
| Rich overlays for ordinary Markdown links | [Planned] | HomeBlaze change: renderer integration, access/navigation, and preview lifecycle |
| Manual canvas semantic connector editing | [Planned] | HomeBlaze canvas extension plus relationship editing; not required by a separate generated diagram widget |
| Shared reconciliation/selection utilities | [Planned] | Optional shared extraction; no Namotion.Interceptor.Utils project exists yet |
| Readable file schemas, complete authoring examples, diagnostics, and focused writeback | [Planned] | Cross-cutting requirement; applies to extensions and to host storage/writer changes where needed; exact tooling remains open |

## A delivery boundary that is possible now [Proposed]

An extension can start with JSON subjects, explicit body blocks, source discovery, exact-path membership, typed relationships in JSON-sidecar metadata, and explicitly embedded views. General selectors and reconciliation still need implementation, but do not require the page-shell changes. Build the same domain model so frontmatter and automatic page contributions can be added later without replacing its subjects. This is an available sequencing option, not an approved MVP or task plan.

## Source evidence [Implemented]

Readiness for extension implementation does not waive the [file-authoring requirement](file-authoring.md). Existing serializers and entry points must be evaluated against its preservation and readability guarantees; not all planned guarantees follow from the ability to load JSON or render Markdown.

- [Subject registration and JSON configuration](../../../../../HomeBlaze.Abstractions/IConfigurable.cs)
- [JSON configuration serializer](../../../../../HomeBlaze.Services/ConfigurableSubjectSerializer.cs)
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
