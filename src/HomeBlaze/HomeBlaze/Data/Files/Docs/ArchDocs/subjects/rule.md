---
title: Rule
navTitle: Rule
status: Planned
---

# Rule [Planned]

## Class definition [Planned]

**Rule** is an authored architecture-test subject. A registered check kind evaluates its configuration against an invocation's repository, available analyzer facts, and loaded architecture/documentation scope. JSON files, named Markdown blocks, and planned frontmatter children use the same subject model. Several repositories can select one definition without cloning it or transferring ownership.

Rule definitions have ordinary subject identity and lifecycle; there is no singleton-per-rule-type requirement. One `rules.md` can contain several differently configured Rule children. Evaluation results, cancellation, and current-run state belong to the invoking [SourceRepository](source-repository.md) or runner, not mutable last-run properties on the shared definition. The exact CLR/evaluator interfaces remain open.

## Configuration properties [Proposed schema]

| Property | Type | Meaning and source |
|---|---|---|
| `name` | Optional `string` | Human-readable test name; the owning file and named child identify the definition independently of its display name. |
| `kind` | Registered check-kind `string` | Required evaluator or predefined policy identifier; unsupported kinds produce configuration errors. |
| `severity` | Severity value | Proposed default `error`; supported values `error`, `warning`, and `info`. The runner's failure threshold is separate. |
| `message` | Optional `string` | Rule-specific explanation; the evaluator adds concrete affected subjects and evidence. |
| `remediation` | Optional `string` | Advice for a human or agent; never an automatically applied edit. |
| `documentation` | Optional documentation link | Explicit guide/section, resolved from the definition's file origin. A Markdown-owned rule can fall back to its containing document. |
| `config` | Check-kind configuration record | Kind-specific scalar constraints and graph selectors, validated against the registered schema. Reuse shared [PathQuery](../shared/path-queries.md) records where graph selection is needed. |

Names are proposed camelCase serialized names; existing Markdown body-block configuration uses PascalCase. [Rule authoring](../rules/authoring.md) gives both forms. Rule configuration uses its normal owning file; descriptions and ordinary enrichment retain their metadata/Markdown owner.

## State properties [Proposed schema]

| Property | Type | Meaning and source |
|---|---|---|
| `capabilities` | Check descriptor | Required facts, supported configuration, applicability, and possible finding kinds. Missing required facts cannot be treated as a passing test. |
| `diagnostics` | Diagnostic collection | Definition/configuration problems only. Per-repository findings and incomplete evaluations remain with their run owner. |
| `definitionLocation` | Source-origin descriptor | Owning file and named block/child or JSON location, with precise spans only when known. |

## Implementation readiness

**Area:** ArchDocs.

**Delivery:** Planned configurable subject and registered evaluator contracts using existing subject/loading/component extension points. The Rule type and all evaluators are unimplemented. Generic frontmatter declarations still need the documented HomeBlaze extension; existing named body blocks and standalone JSON are available entry points after Rule registration.

**Prerequisites:** Normal subject identity/loading, check schemas and registration, source provenance, repository selection, invocation context, and shared [evaluation/finding contracts](../rules/architecture-checks.md).

## UI [Planned]

**Summary widget:** test name, policy kind, severity, requirements, and definition diagnostics. It can appear at a named block in the Markdown explanation.

**Detail page:** inspect policy configuration, source/help document, required facts, and references to selecting repositories. Link to their results without storing one global last-run result on the Rule.

**Editing:** write the canonical JSON file, named Markdown block, or planned frontmatter entry and preserve surrounding prose. Several configured rules of the same kind remain independent definitions. Opening a rule does not run it automatically.

[Rule overview](../rules/overview.md) · [Generic rule families](../rules/generic-rules.md) · [Predefined policies](../rules/predefined-rules.md)
