---
title: Metadata and Relationships
navTitle: Metadata and Relationships
status: Planned
---

# Metadata and Relationships [Planned]

Keep authored intent, enrichment, discovered facts, and presentation state distinguishable.

## Implementation readiness

**Delivery:** Typed relationship readers and JSON-sidecar enrichment can be implemented as extensions. Markdown frontmatter persistence requires HomeBlaze changes.

**Prerequisites:** Existing metadata API/store, stable ownership paths, and the planned typed relationship vocabulary.

## Current storage [Implemented]

JSON-sidecar metadata, descendant application, unknown-key preservation, and metadata write methods already exist. See [Metadata design](../../architecture/design/metadata.md) for their canonical implemented behavior. Generic storage does not yet provide typed ArchDocs relationship validation or editors.

MarkdownFile's Children property already uses InlinePaths, and the registry path writer emits an unambiguous child name directly. The metadata path normalizer only normalizes syntax: bracket and slash collection notation retain the same property segment. It does not resolve aliases or remove a Children segment. An expanded collection path and a bare child name therefore do not become equivalent metadata keys through normalization alone. Markdown frontmatter metadata loading and writing remain planned.

## Relationship record [Planned]

Authored metadata uses kind and target, with optional source, technology, label, or message. Source defaults to the metadata-owning subject. A page's relationship starts at the page; child-scoped metadata starts at that child. Code dependencies and runtime interactions retain separate meanings and provenance. Provide/consume relationships are references, not ownership edges. Internal/external classification and node positions belong to each view.

## Open schema [Proposed]

Settle typed readers, validation, inverse navigation, and editor fields. Keep one authoritative persisted entry rather than editable copies on both endpoints.

The [file-authoring requirement](../file-authoring.md) applies to all metadata and relationship entries. A person or agent must be able to identify the owning subject, source, target, and interaction from the text. Review scoped keys and reference syntax for readability, document their resolution, and preserve unknown/unrelated entries through UI writes. Semantic relationships must not exist only in a rendered diagram or a private UI store.

## Scoped metadata paths [Planned]

Keep the existing metadata envelope. Metadata at its root belongs to the persisted subject; `$subjects` holds metadata for owned descendants by paths relative to that subject. In Markdown, a frontmatter `$service` declaration defines the child named `service`, and its enrichment uses that name directly:

```yaml
metadata:
  $subjects:
    service:
      relationships:
        - kind: calls
          target: /catalog/Payments
          technology: HTTPS
```

This entry enriches the existing child and does not declare another Service. The relationship source defaults to that child. The containing page retains its own metadata and navigation identity. `$service` configuration and `metadata.$subjects.service` enrichment have distinct roles and share the same child's stable identity.

Use the same InlinePaths ownership path when applying and writing scoped metadata. Writers must keep the readable `service` key, preserve unrelated entries, and serialize with other changes to the document. If compatibility with an expanded collection path is offered, resolve it against its owner and diagnose multiple entries for the same target before merging; the current syntactic normalizer does not provide that conversion. Missing targets retain their authored entries for later children or correction, with diagnostics as specified by the owning feature.

Only a collection explicitly published through InlinePaths loses its property segment. Ordinary generated collections keep meaningful segments, so repository projects and hosts remain distinguishable even when a project and its Host have the same key. Child names that collide with published properties or other declarations need visible diagnostics rather than silent shadowing. Lifecycle and configuration writeback follow the [Markdown authoring contract](../integration/markdown-authoring.md).

## Detailed behavior [Planned]

Authored configuration determines logical intent, selectors, and source locations. Metadata holds authored relationships, manual descriptive enrichment, and manual deployment details. Analyzer results and generated child collections are state. Computed summaries and resolved views use the existing derived-property mechanism where appropriate; generated objects are not recreated by a derived getter.

Enrichment has one canonical persisted home per owner: a JSON subject's sidecar or a Markdown document's frontmatter metadata, including its owned descendants. A Markdown document must not gain a second active metadata sidecar. StorageMetadataApplier already applies a stored JSON subject's sidecar to its descendants, including children that attach after the sidecar was loaded, and retains unknown keys and JSON values. Extend equivalent behavior to Markdown frontmatter. Thus repository metadata can enrich discovered project and host children, and environment metadata can enrich generated deployments, provided their canonical ownership and paths are stable. Existing metadata limits still apply. Add typed readers, validation, and editors for domain-specific fields; generic preservation of a key does not make it a typed configuration property or an automatically generated editor field. Do not persist competing editable copies of discovered facts. Define any supported override explicitly and show the discovered value's provenance separately.

The basic subject UI should expose applicable configuration, state, diagnostics, and refresh operations through existing interfaces and generated editors. Project reference edges and selection membership must be traversable through the subject graph without requiring a custom visual graph renderer.

## Related specifications

[Overview and status legend](../overview.md) · [Platform readiness](../implementation-readiness.md)
