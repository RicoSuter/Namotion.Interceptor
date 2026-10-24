---
title: RequiredPropertyRule
navTitle: RequiredPropertyRule
status: Planned
---

# RequiredPropertyRule [Planned]

## Class definition [Planned]

**RequiredPropertyRule : [Rule](../subjects/rule.md)** requires an applicable property or metadata value for selected subjects. JSON uses `$type: Namotion.ArchDocs.RequiredPropertyRule`; its properties extend the common Rule configuration directly.

## Configuration properties [Proposed schema]

| Property | Type | Meaning |
|---|---|---|
| `property` | Property-path `string` | Required value, such as proposed `metadata.category`. Supported accessor syntax and empty-value semantics remain open. |
| `subjectSources` | [PathQuery](../shared/path-queries.md) collection | Subjects that must provide the value. |

## State properties [Proposed schema]

Inherit Rule's capabilities, definition location, and definition diagnostics. Per-subject missing-value findings belong to the invoking repository/runner.

## Behavior and readiness

**Area:** ArchDocs. **Delivery:** Planned concrete subject and evaluator using normal loading and shared evaluation. Supported property/metadata access and canonical edit locations are prerequisites.

Report the affected subject, missing value, and authored owner that can supply it. Missing supported metadata differs from unavailable required analysis. Architectural labels must not replace analyzer-owned technical facts. Inherit Rule's summary/detail/editor presentation with typed property and scope controls.

The [working JSON definition](../../../ArchDocs/rules/projects-have-category.json) intentionally finds missing category labels once the future capability is implemented. Its explanation lives in [rules.md](../../../ArchDocs/rules.md#project-categories).

[Generic families](generic-rules.md) · [Authoring](authoring.md)
