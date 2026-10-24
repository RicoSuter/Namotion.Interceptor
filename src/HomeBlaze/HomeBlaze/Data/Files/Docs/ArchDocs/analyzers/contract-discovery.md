---
title: Contract File Discovery
navTitle: Contract File Discovery
status: Planned
---

# Contract File Discovery [Planned]

Discover [Contracts](../subjects/contract.md) from raw documents while allowing an authored definition to supply explicit configuration. Discovery, project association, parsing, and presentation are separate capabilities.

Expose repository discovery through a shared singleton [Analyzer subject](../subjects/analyzer.md). Repositories select the existing subject and retain separate source boundaries, result ownership, diagnostics, and cancellation. Merely installing this capability does not enable it in every repository.

## Implementation readiness

**Delivery:** Extension work using local source access, subject registration, and reconciliation. Contract discovery is not implemented.

**Prerequisites:** [SourceRepository](../subjects/source-repository.md), discovered [CodeProjects](../subjects/code-project.md), stable source identity, and explicit definition/reference resolution.

## File inputs [Proposed schema]

| Input | Selection | Result |
|---|---|---|
| `openapi.json`, `openapi.yaml`, or `openapi.yml` | Recognized raw filenames inside the configured repository scan scope | A discovered [OpenApiContract](../subjects/openapi-contract.md) candidate |
| A `.proto` file | Supported raw source files inside the configured scan scope | A discovered [ProtobufContract](../subjects/protobuf-contract.md) candidate; multi-file/import grouping remains open |
| An authored OpenApiContract or ProtobufContract descriptor | Required `path` identifies the raw local document, including other filenames or directories | An authored Contract subject with an explicit source |

Recognized discovery filenames are an initial proposal, not a claim that arbitrary JSON/YAML files are API documents. Descriptor filenames such as `openapi-contract.json` are readable conventions; `$type`, not that filename, selects the subject type. Discovery does not create or modify descriptor JSON. Parsing support and supported format revisions are specified by the format analyzer.

Relative descriptor `path` values resolve from the defining file's directory/local origin. A frontmatter definition has the Markdown file's origin. Discovery already has a raw file location. No input uses the process working directory implicitly. Source access must remain inside the declared access scope; a configured descriptor can select a different accessible directory explicitly.

## Project association [Planned]

1. If the Contract defines `codeProjectPath`, preserve that [Identifier](../shared/identifiers.md) and resolve it where it is a subject path. Plain text or a generic subject link remains useful; only a compatible CodeProject supplies the typed association. An unresolved path produces a diagnostic. An explicit identifier is never replaced by inference.
2. Otherwise, compare the raw document's directory with the source directories of discovered CodeProjects in its repository scope. Exactly one project in that directory supplies the association.
3. If there are zero or multiple same-directory projects, retain an unassociated Contract with a source-located diagnostic. An authored definition must supply `codeProjectPath` to resolve it.

The raw document's directory controls this rule, even if the descriptor is stored elsewhere. Do not walk parent directories, select a nearest project, or guess from a Host. A library project is a valid target. The resolved Contract.codeProject reference supplies the read-only CodeProject.contracts inverse; do not author a second `hasContract` fact on the project.

## Ownership and explicit definitions [Planned]

The repository owns auto-discovered Contracts. A JSON descriptor or Markdown declaration owns its authored Contract; references from projects and Interfaces do not transfer ownership. Authored definitions take precedence over an automatic candidate for the same raw source. One effective definition should represent that source rather than publishing an authored subject and an indistinguishable automatic duplicate.

Canonical source identity, conflicting explicit definitions, overlapping repository roots, multi-file protobuf identities, and identity handover when an authored definition is added or removed still need design. Do not silently merge incompatible definitions or lose references/enrichment during an ownership transition. Report conflicts visibly until a defined resolution applies. The general [reconciliation contract](../shared/reconciliation.md) still applies to generated state and failed passes.

## Verification criteria [Planned]

Cover same-directory unique, absent, and ambiguous projects; an explicit cross-directory association; descriptor-relative file paths; raw `.json`, `.yaml`, `.yml`, and `.proto` sources; invalid explicit references; suppression of same-source automatic duplicates; and visible conflicts. Raw source remains accessible when association or parsing is unavailable.

[Contract examples](../examples/contract-files.md) · [Analyzer model](overview.md) · [OpenAPI analyzer](openapi-analyzer.md) · [Protobuf analyzer](protobuf-analyzer.md)
