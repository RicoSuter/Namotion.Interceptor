---
title: Rule
navTitle: Rule
status: Planned
---

# Rule [Planned]

## Class definition [Planned]

**Rule** is the abstract base subject for authored architecture tests. Concrete registered subject types implement checks against an invocation's repository, available analyzer facts, and loaded architecture/documentation scope. `$type` selects the concrete implementation in JSON/frontmatter; the type in a named Markdown block serves the same purpose. Several repositories can select one definition without cloning it or transferring ownership.

Rule definitions have ordinary subject identity and lifecycle; there is no singleton-per-rule-type requirement. One `rules.md` can contain several differently configured concrete Rule children. Evaluation results, cancellation, and current-run state belong to the invoking [SourceRepository](source-repository.md) or runner, not mutable last-run properties on the shared definition. Concrete types extend the common configuration with their own typed properties. Exact CLR evaluation signatures remain open.

## Configuration properties [Proposed schema]

| Property | Type | Meaning and source |
|---|---|---|
| `name` | Optional `string` | Human-readable test name; the owning file and named child identify the definition independently of its display name. |
| `severity` | Severity value | Proposed default `error`; supported values `error`, `warning`, and `info`. The runner's failure threshold is separate. |
| `message` | Optional `string` | Rule-specific explanation; the evaluator adds concrete affected subjects and evidence. |
| `remediation` | Optional `string` | Advice for a human or agent; never an automatically applied edit. |
| `documentation` | Optional documentation link | Explicit guide/section, resolved from the definition's file origin. A Markdown-owned rule can fall back to its containing document. |

Names are proposed camelCase serialized names; existing Markdown body-block configuration uses PascalCase. [Rule authoring](../rules/authoring.md) gives both forms. `$type` is the implementation discriminator; Rule has no `kind` dispatch field or generic `config` bag. Concrete properties use the normal subject schema, validation, and editor metadata. Rule configuration uses its normal owning file; descriptions and ordinary enrichment retain their metadata/Markdown owner.

Concrete specifications: [NamingRule](../rules/naming-rule.md), [RequiredPropertyRule](../rules/required-property-rule.md), [MembershipRule](../rules/membership-rule.md), [AllowedDependenciesRule](../rules/allowed-dependencies-rule.md), and [HostsAssignedToServicesRule](../rules/hosts-assigned-to-services-rule.md). The abstract Rule itself is not an authored `$type` target. Wildcard suites select all compatible concrete Rule instances.

## State properties [Proposed schema]

| Property | Type | Meaning and source |
|---|---|---|
| `capabilities` | Check descriptor | Required facts, supported configuration, applicability, and possible finding kinds. Missing required facts cannot be treated as a passing test. |
| `diagnostics` | Diagnostic collection | Definition/configuration problems only. Per-repository findings and incomplete evaluations remain with their run owner. |
| `definitionLocation` | Source-origin descriptor | Owning file and named block/child or JSON location, with precise spans only when known. |

## Implementation readiness

**Area:** ArchDocs.

**Delivery:** Planned abstract subject contract and concrete configurable types using existing subject/loading/component extension points. All Rule types/evaluators are unimplemented. Generic frontmatter declarations still need the documented HomeBlaze extension; existing named body blocks and standalone JSON are available entry points after concrete Rule registration.

**Prerequisites:** Normal subject identity/loading, check schemas and registration, source provenance, repository selection, invocation context, and shared [evaluation/finding contracts](../rules/architecture-checks.md).

## UI [Planned]

**Summary widget:** test name, concrete rule type, severity, requirements, and definition diagnostics. It can appear at a named block in the Markdown explanation.

**Detail page:** inspect policy configuration, source/help document, required facts, and references to selecting repositories. Link to their results without storing one global last-run result on the Rule.

**Editing:** write typed properties to the canonical JSON file, named Markdown block, or planned frontmatter entry and preserve surrounding prose. Several configured rules of the same concrete type remain independent definitions. Opening a rule does not run it automatically.

[Rule overview](../rules/overview.md) · [Generic rule families](../rules/generic-rules.md) · [Predefined policies](../rules/predefined-rules.md)
