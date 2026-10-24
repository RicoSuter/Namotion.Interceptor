---
title: File Authoring and Readability
navTitle: File Authoring
status: Planned
---

# File Authoring and Readability [Planned]

The authored model must be easy for humans and AI agents to understand and edit directly in Markdown, JSON, YAML frontmatter, contract source, and other supported file formats. HomeBlaze provides another way to explore and edit that model. The UI must not be required to understand a definition, recover its meaning, or create and change supported authored data.

Consulting this feature reference to learn syntax, supported types and capabilities, defaults, and reference rules is expected. Readable files can rely on documented conventions; they must be understandable and editable with this reference and a normal file editor, without requiring UI-only knowledge or hidden state.

## Implementation readiness

Existing JSON files, Markdown, sidecars, and explicit blocks provide text authoring entry points [Implemented]. The ArchDocs naming/reference conventions, complete schemas/examples, typed validation, and the preservation guarantees below are [Planned]. Existing entry points do not establish that every planned write path or editor capability is already implemented. Frontmatter children/metadata and their writeback still need the host changes in the readiness matrix.

## Understand the model from source [Planned]

- Use descriptive property names, type identifiers, relationship kinds, filenames, and declared child names. Avoid compact encodings that require the renderer or runtime object inspection to interpret.
- Type discriminators must identify the domain concept clearly and have a documented mapping to its schema. Do not require assembly versions, process-specific identifiers, or generated UI state to understand a type. Exact type names and any readable aliases remain schema decisions.
- References must have a documented, predictable interpretation from the authored files and the declared source/mount scope. Show the target definition or how discovery produces it. Keep file links, source locations, subject references, and wildcard selections visibly distinguishable in the reference documentation.
- Prefer meaningful named identities where appropriate. When an opaque stable identifier is necessary, retain a readable name and make its definition/reference discoverable. Do not make ordering or a display label the hidden identity rule.
- State defaults, units, allowed values, optional fields, and absent-versus-empty meaning in the element specification. Examples must not depend on hidden UI defaults or an undocumented selection state.
- Explain what is authored, discovered, derived, or observed. Generated subject references must be traceable to the owning repository/analyzer or environment rule; no UI-only architecture facts or relationship semantics are allowed.

Ordinary Markdown prose, links, and document organization remain useful in a regular editor or renderer. Optional widget blocks and metadata must be understandable in source even when their richer presentation is unavailable. Binary source artifacts can be referenced where necessary, but their authored identity, purpose, provenance, and source relationships must remain readable; they must not become opaque substitutes for the authored architecture model.

## Edit without HomeBlaze [Planned]

A supported file editor must be sufficient to author definitions, metadata, relationships, type choices, component selections, and view configuration. The corresponding UI must operate on the same canonical file representation and must not keep a competing authoritative store. Temporary row selection or other transient interaction state can remain local UI state.

Definitions may live in JSON, named Markdown frontmatter, or explicit blocks where supported. Their common domain semantics must be documented, including the currently different property casing of JSON files and body blocks. Existing conventions remain usable; new format-specific differences need a clear reason and explicit examples. Do not silently require users to learn runtime collection or CLR naming details where a simpler supported authoring representation can serve the same purpose.

Text editing and UI editing must converge on the same effective configuration after successful reload. Unsupported or invalid input must remain available for correction rather than being silently deleted or converted into a different entity. Diagnostics must identify the source file and relevant field, reference, or location in a readable form; a diagram highlight alone is insufficient.

This requirement does not require committing every discovered fact into authored configuration. Discovered and derived facts must also be inspectable without the graphical UI in a readable, machine-consumable form, through subject/API data, source-linked snapshots, or exports as appropriate. Facts must remain distinguishable and traceable to their source and analyzer. Where snapshots or exports are part of a capability, their format and provenance must be understandable independently of a widget. Exact generated-data inspection/export surfaces remain to be designed with those capabilities.

## Changes should be easy to review [Planned]

Use readable indentation, stable names and output ordering, and focused files. Avoid gratuitous reformatting, property reordering, or unrelated source changes when editing one value. Preserve unrelated Markdown content, frontmatter entries, metadata keys, and comments where the format supports them. Keep authored content separate from generated output so refreshing an analyzer does not rewrite human explanations or authored overrides.

Persist semantic diagram connections as ordinary model relationships and keep positions/presentation in explicit view configuration. Source edits must express both without a private UI serialization step. Continue using the existing metadata envelope and owner write routes; improve ergonomics through deliberate schema choices rather than a second enrichment store.

## Schemas and examples [Planned]

Each implemented authoring surface needs an accurate field/type reference and a complete, copyable minimal example. Machine-readable schema or type metadata should support validation and editor/agent tooling; exact schema format, publication, and editor integration remain design choices. A UI can assist discovery and completion but cannot be the only place where valid fields and reference rules are defined.

Present the subject's own master data first, followed by structural references and selections, within both configuration and state tables. Own data includes names, classifications, technology, versions, source facts, status, and diagnostics; the reference group includes related subjects, memberships, and graph selectors. Use the same order in authoring examples and property editors where applicable. Group by the field's meaning: a classification remains master data even if it later supports a link, while a subject reference remains a reference even if serialized as a string. Inherited fields follow the same grouping and link to their canonical base definition.

Current examples deliberately mark provisional fields and incomplete extraction configurations. Before implementing the corresponding authoring surface, settle those fields and provide a full source-editable example. The [contract-file examples](scenarios/contract-files.md) distinguish raw source, descriptor `path`, and `codeProjectPath`; C# extraction/snapshot formats still need a complete example after that input contract is selected.

Named Markdown children use their inline name directly in paths and scoped metadata: `$service` declares the child, `Path=service` references it, and `metadata.$subjects.service` enriches it. This reuses existing InlinePaths behavior; frontmatter persistence remains planned. See [scoped metadata paths](shared/metadata-and-relationships.md#scoped-metadata-paths-planned) for resolution, writeback, and compatibility limits. Ordinary generated collections retain meaningful collection segments.

The current fully qualified $type names, generated keys, and other reference conventions still need ergonomics review. Evaluate readable type identifiers and stable reference rules before declaring the final schema. Aliases or syntax improvements that need host changes must be marked as such; they are not already supported merely because an example would be clearer.

## Identifiers [Planned]

[Identifiers](shared/identifiers.md) support three levels: plain text, a path to any subject or ordinary Markdown page with generic link presentation, and a path to the expected subject type with rich domain presentation. All three identify the configured concept, including before its richer subject type is available. The canonical identifier specification contains the compact list of syntax options; that decision is deferred. Property presentation order is independent of identifier resolution.

## Acceptance checks [Planned]

1. Starting only with the reference and a normal file editor, understand and change a subject's type, logical membership, and relationships without opening HomeBlaze.
2. Trace a link/reference from its source to an authored definition or a documented discovery rule, including a Markdown child and a shared component.
3. Load the edited files and obtain the same model as equivalent UI edits, with authored and generated facts still distinct.
4. Make one UI edit and review a focused diff that preserves unrelated prose, frontmatter, comments, and unknown metadata.
5. Diagnose malformed types, fields, selectors, and unresolved references from readable source-located diagnostics, while retaining the original source.
6. Read the Markdown meaningfully without the widget renderer, and inspect contract/source information without relying on a specialized viewer.

[Overview](overview.md) · [Scenarios](scenarios/overview.md) · [Readiness](implementation-readiness.md) · [Verification scenarios](verification-scenarios.md)
