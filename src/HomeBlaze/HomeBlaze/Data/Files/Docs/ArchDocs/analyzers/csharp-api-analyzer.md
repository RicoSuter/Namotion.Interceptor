---
title: C# Public API Analyzer
navTitle: C# Public API Analyzer
status: Planned
---

# C# Public API Analyzer [Planned]

Extract a CodeProject public programming surface as a CSharpApiContract.

## Implementation readiness

**Delivery:** Can be implemented through existing subject and component extension points. The ArchDocs feature itself is not implemented.

**Prerequisites:** CSharpApiContract and source/assembly inputs. Extraction mode and dependencies remain open; no HomeBlaze page-shell extension is required.

## Output [Planned]

Public types, members, signatures, source/revision provenance, and extraction diagnostics. A library can produce a Contract without any Host or network endpoint.

## Decisions before extraction [Proposed]

Choose source-symbol versus compiled-metadata analysis, visibility rules, target-framework handling, unresolved-input behavior, and snapshot format. Do not make the basic project analyzer build projects to satisfy this capability. Source discovery, extraction, version comparison, and compatibility assessment remain separable.

## Verification criteria [Planned]

Pin the selected public-surface rules, provenance, ambiguous target handling, deterministic output, and diagnostics for unsupported inputs once extraction scope is selected.

## Related specifications

[Overview and status legend](../overview.md) · [Platform readiness](../implementation-readiness.md)
