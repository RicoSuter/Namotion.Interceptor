---
title: Analyzer Extension Model
navTitle: Analyzer Extension Model
status: Planned
---

# Analyzer Extension Model [Planned]

Analyzers contribute facts and contracts through an extensible, read-only discovery model.

## Implementation readiness

**Delivery:** Can be implemented through existing subject and component extension points. The ArchDocs feature itself is not implemented.

**Prerequisites:** SourceRepository owns the result; existing DI/module registration can register implementations.

## Contract [Planned]

An analyzer declares applicability, reads through an explicit source boundary, and returns identities, facts, references, diagnostics, and provenance. Multiple analyzers can enrich or link the same element; do not duplicate a Host merely because container and language analyzers both describe it.

Use normal typed DI/module registration. No separate executable plugin loader is required. Reconciliation publishes compatible results through the owner; parser objects should not become untracked competing state.

## Categories [Planned]

- [C# project analyzer](csharpanalyzer.md): basic solution/project XML, source/build groupings, and project-level elements.
- [Contract file discovery](contract-discovery.md): raw file recognition, same-directory project association, and explicit-definition precedence independently of parsing.
- [C# public API analyzer](csharp-api-analyzer.md): optional programming API Contract extraction.
- [OpenAPI analyzer](openapi-analyzer.md): optional OpenApiContract parsing.
- [Protobuf analyzer](protobuf-analyzer.md): optional ProtobufContract parsing.

Container packaging, further languages, other contract formats, and remote sources can be added later without changing Service semantics. Version comparison and compatibility analysis are separate capabilities.

## Scheduling and failures [Planned]

Initial and explicit refresh are required for the candidate project slice. Automatic file-change refresh is proposed, coalesced by the owner; events only invalidate the inventory. Per-repository successful publication is atomic, failures retain the last successful graph, and detach fences stale publication. See shared reconciliation and query specifications for full contracts.

## Related specifications

[Overview and status legend](../overview.md) · [Platform readiness](../implementation-readiness.md)
