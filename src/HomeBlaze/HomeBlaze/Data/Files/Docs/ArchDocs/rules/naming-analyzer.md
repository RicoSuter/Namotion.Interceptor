---
title: NamingAnalyzer
navTitle: NamingAnalyzer
status: Planned
---

# NamingAnalyzer [Planned]

## Class definition [Planned]

**NamingAnalyzer : [Analyzer](../subjects/analyzer.md)** checks a selected scalar property against a documented naming pattern. Register the concrete subject normally; JSON uses `$type: Namotion.ArchDocs.NamingAnalyzer`. The properties below extend Analyzer's common configuration directly.

## Configuration properties [Proposed schema]

| Property | Type | Meaning |
|---|---|---|
| `property` | Property-path `string` | Scalar value to inspect, such as a project name. Property/metadata accessor syntax remains to be finalized. |
| `pattern` | `string` | Required expected naming pattern. |
| `patternSyntax` | Pattern-syntax value | Explicit matching language; a simple glob is the proposed initial option. Supported syntaxes and exact matching semantics remain open. |
| `ignoreCase` | `bool` | Proposed default `false`; record case behavior explicitly. |
| `subjectSources` | [PathQuery](../shared/path-queries.md) collection | Subjects whose supported property values are checked. |

## State properties [Proposed schema]

Inherit Analyzer's capabilities, definition location, and definition diagnostics. Findings retain actual value, expected pattern, and source evidence on the invoking repository/runner; they are not mutable state shared between invocations on this definition.

## Behavior and readiness

**Area:** ArchDocs. **Delivery:** Planned concrete subject and evaluator using normal loading and shared evaluation. Pattern validation and property readers are prerequisites; all fields remain proposed.

Basic project XML can support literal project-name/path checks. Namespace, type, and member naming require additional providers. A discovered name violation can require editing the project/code; changing documentation to conceal the source value is not a repair. Inherit Analyzer's summary/detail/editor presentation, adding typed pattern and property controls.

[Generic families](generic-rules.md) · [Authoring](authoring.md)
