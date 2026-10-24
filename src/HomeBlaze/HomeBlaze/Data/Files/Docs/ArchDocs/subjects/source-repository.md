---
title: SourceRepository
navTitle: SourceRepository
status: Planned
---

# SourceRepository [Planned]

Owns discovered software elements from an existing source directory or checkout.

## Implementation readiness

**Delivery:** Can be implemented through existing subject and component extension points. The ArchDocs feature itself is not implemented.

**Prerequisites:** Read-only local source access, analyzer registration, and keyed reconciliation. Frontmatter authoring additionally needs the Markdown extension.

## Data [Planned]

| Authored configuration | Owned state |
|---|---|
| sourceRoot; analyzer and scan options to refine | projects, hosts, analysis status, diagnostics, last successful analysis, and provenance |

Several repositories can coexist. A repository need not be managed by Git. Generated identity is repository-relative and independent of display names and scan order.

## UI [Planned]

Show source scope, last successful analysis, diagnostics, discovered collections, and an explicit refresh action through ordinary subject UI. A source failure retains the last successful graph.

## Detailed behavior [Planned]

A configurable subject that identifies the source root of an existing local checkout and coordinates analyzers. It owns the discovered code subjects and reports analysis status, diagnostics, and the time of the last successful analysis. It does not require a Git-managed folder or perform Git operations.

Several SourceRepository subjects can coexist. A repository may contain hosts for several services, and a service can select elements from several repositories.

Analyzer-produced facts remain distinct from authored settings. Facts have enough provenance to identify the analyzer and supporting source location. A failed analysis is distinct from a successful analysis that found no results.

### Source access and documentation mounts

A typical setup mounts `/repo/docs` in HomeBlaze and configures SourceRepository to analyze `/repo`. It does not mount the whole repository merely to make its contents readable. SourceRepository exposes discovered architectural elements rather than a file subject for every source file.

The MVP supports local folders only. Proposed default: use FluentStorage's raw listing, read, and metadata capabilities where practical, behind the smallest access contract needed by analyzers. Reuse existing suitable contracts before introducing another abstraction. The current IStorageContainer includes subject lifecycle and writes and has no general raw recursive enumeration API, so it is not automatically the analyzer contract.

File locations such as `../` resolve relative to the defining configuration file's directory when it has a local file origin. SourceRepository then uses its resolved root as the boundary for source traversal. Subject references continue to use the existing subject path resolver. A relative file location must not be interpreted as a request to traverse above a mounted blob container through IStorageContainer.

For the MVP, defining SourceRepository in a JSON file or Markdown frontmatter provides an unambiguous local origin through the owning file. Proposed default: where no local file origin exists, require an absolute source root and report a configuration diagnostic for an ambiguous relative path. Do not guess from the process working directory. Future storage backends can supply a storage identity and a root path instead of a local filesystem location.

## Usage examples

[JSON catalog](../examples/json-catalog.md) · [Multiple repositories](../examples/repositories-and-shared-components.md)

## Related specifications

[Overview and status legend](../overview.md) · [Platform readiness](../implementation-readiness.md)
