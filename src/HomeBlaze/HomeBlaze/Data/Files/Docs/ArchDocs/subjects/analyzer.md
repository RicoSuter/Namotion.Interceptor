---
title: Analyzer
navTitle: Analyzer
status: Planned
---

# Analyzer [Planned]

## Class definition [Planned]

**Analyzer** defines the shared subject capability implemented by concrete source and contract analyzers. Each registered analyzer is a singleton subject that can be selected by several [SourceRepositories](source-repository.md). It declares supported inputs and options and performs read-only analysis when a repository invokes it. Selection retains the same subject identity without repository-owned copies. The exact CLR interface/base-class shape remains open.

Shared configuration and subject metadata belong to the analyzer's defining owner. For repository analysis, source boundaries, run options, analysis status, diagnostics, results, generated subjects, reconciliation workers, and cancellation belong to the invoking SourceRepository. Other consumers, such as authored contract analysis, retain their own result owners; a standalone-contract analyzer-reference API is not selected here. The analyzer is visible and selectable in the subject graph, rather than only an invisible DI service. Its existence does not select it or start repository analysis.

Names in the tables are proposed serialized camelCase names; concrete CLR signatures remain open. State properties are read-only to authors.

## Configuration properties [Proposed schema]

| Property | Type | Meaning and source |
|---|---|---|
| `name` | Optional `string` | Readable analyzer label. Does not supply persistent identity or select the analyzer for a repository. |
| `options` | Shared analyzer-option record | Implementation-supported settings shared by every caller. Exact fields and any supported repository-level overrides remain open; repository source roots and mutable per-run state are excluded. |

An implementation may require no editable shared options. Do not add a second configuration copy on each repository. Descriptive metadata uses the [existing metadata envelope](../shared/metadata-and-relationships.md). No repository selector is required on the Analyzer; repositories select it through their own `analyzerSources`.

## State properties [Proposed schema]

| Property | Type | Meaning and source |
|---|---|---|
| `identity` | Stable analyzer-identity value | Registration identity used in provenance and identity namespaces, independent of display name, option revisions, transient object reference, and current subject path. Exact persistence and representation remain open. |
| `implementationVersion` | Optional `string` | Available implementation version or equivalent revision for interpreting provenance; does not replace the analyzer's stable identity. |
| `capabilities` | Read-only capability/option descriptors | Supported source kinds, applicability constraints, produced fact kinds, and supported shared/run options. Exact descriptor schema remains open. |
| `availability` | Availability value | Whether this shared analyzer can accept supported work, independently of any repository's analysis status. Exact vocabulary remains proposed. |
| `diagnostics` | Diagnostic collection | Shared analyzer configuration or availability findings only. Repository invocation findings stay on the owning repository. |

There is no mutable `lastRepository`, last-run result graph, repository source boundary, or repository-owned cancellation state on the singleton. A repository detail view can show its own results alongside links to the selected Analyzer subjects.

## Implementation readiness

**Delivery:** Planned extension using ordinary subject/type and DI/module registration and existing component registration. No ArchDocs analyzer subject is implemented.

**Prerequisites:** Graph publication and authored configuration ownership for shared instances, stable analyzer identity, typed selection, safe concurrent invocation, and invalidation of selecting repositories. No separate executable plugin loader is required.

Existing hosted-subject registration provides a relevant lifetime primitive: `AddHostedSubject<T>` registers `T` with `TryAddSingleton` and resolves that same instance for hosted-service registration. The NukiBridge and LuxtronikHeatPump registration extensions use it. This establishes a DI instance shared within a service provider; it does not establish a selectable graph location or global per-type subject uniqueness. `SubjectFactory.CreateSubject` and configuration deserialization use `ActivatorUtilities.CreateInstance`, so ordinary subject construction does not by itself guarantee singleton reuse. These are existing platform behaviors, not an implemented ArchDocs registration contract.

## Identity and lifetime [Planned]

Proposed scope: one shared analyzer subject for each analyzer registration identity in the loaded application root, reused by all selecting repositories. A root or another shared configuration owner provides its canonical graph ownership; repository references do not take ownership or keep a removed shared input valid. App/root-scoped here does not promise process-global state, one instance across independent roots, or uniqueness of every subject of the same concrete CLR type.

The association between DI registration, graph publication, and the defining configuration owner remains a design choice. Confirm how startup, root replacement, and configuration reload preserve or replace a shared instance before implementation. The user requirement is shared singleton selection; whether separately configured variants of one implementation are supported, and how such variants receive distinct identities, remains open.

Persisted provenance must distinguish the selected analyzer identity, relevant implementation/configuration information, and source evidence from an optional current locator. Display-name edits and graph moves do not silently assign a different analyzer's identity to existing contributions. Exact identity representation and intentional replacement/rename rules remain proposed. The shared [Identifier](../shared/identifiers.md) syntax is still deferred; it does not substitute a plain string or generic subject for the compatible Analyzer subject needed to execute analysis.

## Concurrent calls and configuration [Planned]

Several repositories can invoke the same shared subject concurrently. Each call receives the invoking repository's bounded source access, supported run options, and cancellation, and returns its contribution to that owner. Calls must not leak source inputs, diagnostics, cancellation, or results between repositories. An implementation may coordinate its own resources, but cannot assume callers are serialized globally because each repository serializes its own worker.

Shared option changes and capability/availability changes invalidate all repositories that depend on them. Each invocation must use a consistent effective configuration; a repository cannot publish a result after the selected subject or relevant inputs have been superseded. The exact configuration snapshot/revision mechanism is an implementation decision. Option schemas must state which values are shared settings, which are repository run options, and how any explicitly supported overrides combine before implementation.

Analyzer detach or replacement makes the former selection unavailable and invalidates dependent work. The repository retains last successful results with their original provenance when required analysis cannot complete. Repository detach or cancellation fences that repository's publication and must not stop unrelated calls or dispose the shared analyzer. Shared-subject shutdown must respect its canonical owner and lifecycle while preventing obsolete results from being published. Detailed scheduling and publication rules belong to [SourceRepository](source-repository.md) and [reconciliation](../shared/reconciliation.md).

## UI [Planned]

**Summary widget:** analyzer name, supported source/fact kinds, and shared availability/configuration diagnostics. Use the same subject presentation when linked from a repository's resolved analyzer list.

**Detail page:** inspect capabilities, option definitions, shared configuration, stable identity, available implementation version, and shared diagnostics. Related repositories may be shown as references when available; their run state and results remain on their own detail pages.

**Editing:** edit supported shared settings through the analyzer's defining owner and make their effect on selecting repositories visible. Repository-specific selectors and run options are edited on SourceRepository. Do not create a duplicate singleton as part of picking a reference, or start a repository scan merely by opening or embedding the analyzer's UI.

These subject-specific components use existing registration and explicit Markdown embedding. Automatic page contributions remain the separate host extension; no new route or reference syntax is selected here.

## Related specifications

[Analyzer extension model](../analyzers/overview.md) · [SourceRepository](source-repository.md) · [Path queries](../shared/path-queries.md) · [Overview and status legend](../overview.md) · [Platform readiness](../implementation-readiness.md)
