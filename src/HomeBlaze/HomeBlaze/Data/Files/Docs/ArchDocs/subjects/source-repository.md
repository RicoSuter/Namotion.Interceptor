---
title: SourceRepository
navTitle: SourceRepository
status: Planned
---

# SourceRepository [Planned]

## Class definition [Planned]

**SourceRepository** is an authored subject class identifying an existing source directory or checkout, selecting shared singleton [Analyzer subjects](analyzer.md), and owning analyzer-discovered [CodeSolutions](code-solution.md), [CodeProjects](code-project.md), [Hosts](host.md), and optional [Contracts](contract.md). Its defining file owns configuration and descendant enrichment. It coordinates read-only discovery and stable keyed reconciliation; it does not require Git or perform Git operations. Selected analyzers retain their shared owner and identity.

Authored enrichment and relationships use the [existing metadata envelope](../shared/metadata-and-relationships.md), through the defining owner. They are not a second configuration store. Names in the tables are proposed serialized camelCase names; concrete CLR signatures remain open. State properties are read-only to authors.

## Configuration properties [Proposed schema]

| Property | Type | Meaning and source |
|---|---|---|
| `sourceRoot` | `string` | Required local source directory. Relative file locations resolve from the defining configuration file; no process-working-directory default. |
| `scanOptions` | Scan-option record | Proposed inclusion, exclusion, traversal, and link policies; exact fields/defaults remain open. |
| `analyzerOptions` | Analyzer run-option entries | Repository-specific options supported by selected [Analyzers](analyzer.md), associated with their stable identities. Does not configure or own the shared subjects; exact entry shape and allowed overrides remain open. |
| `analyzerSources` | [PathQuery](../shared/path-queries.md) collection selecting [Analyzer](analyzer.md) | Explicit selection of existing shared analyzer subjects. Proposed default: omitted or empty selects none. Registration alone never selects an analyzer. |

No analyzer execution, source mount, or traversal policy is implied by an omitted proposed option. Scan defaults and analyzer-specific option precedence must be documented before implementation. Options for an unselected or unavailable analyzer produce a diagnostic and do not select or instantiate it. A source root without a local file origin needs an explicit absolute location unless a future backend supplies another unambiguous source identity. Analyzer selectors require actual compatible subjects; the shared [Identifier](../shared/identifiers.md) convention does not make a plain name, generic subject link, or unresolved path executable.

## State properties [Proposed schema]

| Property | Type | Meaning and source |
|---|---|---|
| `analysisStatus` | Analysis-status value | Distinguishes not analyzed, current work, success, failure, and cancellation; exact vocabulary remains proposed. |
| `diagnostics` | Diagnostic collection | Readable source-located configuration and analyzer findings, kept separate from last successful results. |
| `lastSuccessfulAnalysis` | Optional UTC timestamp | Time of the last complete successful publication; absent before any successful pass. |
| `provenance` | Provenance records | Stable selected-analyzer identities, implementation/configuration information needed to interpret the published facts, and supporting source locations. Exact record shape remains proposed. |
| `analyzers` | Reference collection of [Analyzer](analyzer.md) | Resolved, deduplicated selections referencing shared singleton subjects without cloning or ownership transfer. |
| `solutions` | Keyed owned collection of [CodeSolution](code-solution.md) | Solution-file discovery results with stable repository-relative identity; grouping only. |
| `projects` | Keyed owned collection of [CodeProject](code-project.md) | Discovered code/build projects, shared by all referring solutions. |
| `hosts` | Keyed owned collection of [Host](host.md) | Supported runnable process definitions; discovery does not select Service/release membership. |
| `contracts` | Keyed owned collection of [Contract](contract.md) | Optional raw-file discovery results; explicit authored definitions take precedence according to the [contract discovery rules](../analyzers/contract-discovery.md). |

A successful empty result is distinct from failure. A failed pass retains the entire last successful graph, including solution/project memberships, under [reconciliation](../shared/reconciliation.md) and [publication rules](../shared/path-queries.md).

## Implementation readiness

**Delivery:** Can be implemented through existing subject and component extension points. The ArchDocs feature itself is not implemented.

**Prerequisites:** Read-only local source access, shared analyzer subject registration/publication, typed analyzer selection, and keyed reconciliation. Frontmatter authoring additionally needs the Markdown extension.

Several repositories can coexist. A repository need not be managed by Git. Generated identity is repository-relative and independent of display names and scan order.

## UI [Planned]

**Summary widget:** source scope, selected analyzers and their availability, analysis state, last successful analysis, and discovered project/Host counts with diagnostics.

**Detail page:** browse discovered elements, project dependencies, provenance, diagnostics, and refresh status. Link selected analyzers to their shared subject presentation and distinguish current selections from the analyzers recorded in last successful provenance. Use an explicit repository refresh operation independently of a visual graph renderer.

**Editing:** change source/scan configuration, analyzer selectors, and repository-specific run options where supported; enrich generated descendants through owner metadata. Pick existing compatible Analyzer subjects and show unresolved selections. Shared analyzer configuration is edited on the [Analyzer](analyzer.md) subject, with its effect on all selecting repositories visible. Generated identities and discovered facts are not independently editable configuration.

These components use existing registration and explicit Markdown embedding. Automatic surrounding page contributions need the separate host extension.

## Detailed behavior [Planned]

A configurable subject that identifies the source root of an existing local checkout and coordinates analyzers. It owns the discovered code subjects and reports analysis status, diagnostics, and the time of the last successful analysis. It does not require a Git-managed folder or perform Git operations.

Several SourceRepository subjects can coexist. A repository may contain hosts for several services, and a service can select elements from several repositories.

Analyzer-produced facts remain distinct from authored settings. Facts have enough provenance to identify the analyzer and supporting source location. A failed analysis is distinct from a successful analysis that found no results.

### Shared analyzer selection and ownership

`analyzerSources` follows shared [path queries](../shared/path-queries.md), while `analyzers` exposes the resolved references. Several repositories can select the same Analyzer singleton. Selectors do not instantiate analyzers, give repositories a canonical ownership edge to them, or execute analysis merely because the subject exists. An unresolved explicit selector or incompatible result is a source-located configuration diagnostic. Preserve the authored selector and the entire last successful graph when required selected inputs cannot be evaluated.

The repository owns its source boundary, run options, analysis status, diagnostics, last successful graph, generated children, reconciliation worker, and invocation cancellation. Shared analyzer configuration and capabilities belong to the Analyzer subject. A repository's supported per-run options must not mutate that subject or another repository's inputs. The [Analyzer contract](analyzer.md#concurrent-calls-and-configuration-planned) defines safe concurrent calls; no repository result is stored as mutable singleton state.

Selection changes, selected-subject replacement or detach, and relevant shared analyzer configuration/capability changes invalidate the repository's current inputs. The repository re-evaluates through its serialized/coalesced worker; stale calls cannot publish after replacement, cancellation, or detach. A removed or unavailable required target retains failure diagnostics separately from last successful provenance. Once inputs settle, each repository can complete and publish independently. Deliberately removing a selection removes its former contributions only through a complete successful reconciliation, including when the new analyzer selection is empty.

Provenance retains the stable analyzer registration identity and relevant implementation/configuration information for each published contribution, independently of the current display name or subject path. Exact identity representation and rename/replacement behavior remain proposed in [Analyzer](analyzer.md#identity-and-lifetime-planned); a new instance or changed locator must not silently attribute an old result to a different analyzer.

### Source access and documentation mounts

A typical setup mounts `/repo/docs` in HomeBlaze and configures SourceRepository to analyze `/repo`. It does not mount the whole repository merely to make its contents readable. SourceRepository exposes discovered architectural elements rather than a file subject for every source file.

The candidate local-source slice covers local folders; an MVP has not been selected. Proposed default: use FluentStorage's raw listing, read, and metadata capabilities where practical, behind the smallest access contract needed by analyzers. Reuse existing suitable contracts before introducing another abstraction. The current IStorageContainer includes subject lifecycle and writes and has no general raw recursive enumeration API, so it is not automatically the analyzer contract.

File locations such as `../` resolve relative to the defining configuration file's directory when it has a local file origin. SourceRepository then uses its resolved root as the boundary for source traversal. Subject references continue to use the existing subject path resolver. A relative file location must not be interpreted as a request to traverse above a mounted blob container through IStorageContainer.

For the proposed local-source slice, defining SourceRepository in a JSON file or Markdown frontmatter provides an unambiguous local origin through the owning file. Proposed default: where no local file origin exists, require an absolute source root and report a configuration diagnostic for an ambiguous relative path. Do not guess from the process working directory. Future storage backends can supply a storage identity and a root path instead of a local filesystem location.

## Usage examples

[JSON catalog](../examples/json-catalog.md) · [Multiple repositories](../examples/repositories-and-shared-components.md)

## Related specifications

[Analyzer](analyzer.md) · [Analyzer extension model](../analyzers/overview.md) · [Overview and status legend](../overview.md) · [Platform readiness](../implementation-readiness.md)
