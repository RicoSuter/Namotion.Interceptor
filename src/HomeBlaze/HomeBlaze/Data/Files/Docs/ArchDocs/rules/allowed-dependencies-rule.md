---
title: AllowedDependenciesRule
navTitle: AllowedDependenciesRule
status: Planned
---

# AllowedDependenciesRule [Planned]

## Class definition [Planned]

**AllowedDependenciesRule : [Rule](../subjects/rule.md)** constrains supported outgoing dependency edges using explicit source scope and category allowlists. JSON uses `$type: Namotion.ArchDocs.AllowedDependenciesRule`; all policy fields are typed properties alongside common Rule properties.

## Configuration properties [Proposed schema]

| Property | Type | Meaning |
|---|---|---|
| `categoryProperty` | Property-path `string` | Classification accessor, illustratively `metadata.category`. Final reader syntax remains open. |
| `fromCategory` | [Identifier](../shared/identifiers.md) | Category whose outgoing dependencies are constrained. |
| `allowedTargetCategories` | Identifier collection | Known target categories allowed by this policy. Identifier comparison semantics must be finalized before implementation. |
| `relationshipKinds` | `string` collection | Evidence channels to evaluate, such as proposed `projectReference`. These classify edges and do not select the Rule implementation. |
| `subjectSources` | [PathQuery](../shared/path-queries.md) collection | Subjects whose outgoing edges are checked. |

## State properties [Proposed schema]

Inherit Rule's capabilities, definition location, and definition diagnostics. Offending edges, categories, source evidence, and incomplete inputs are invocation-owned results.

## Behavior and readiness

**Area:** ArchDocs. **Delivery:** Planned concrete subject and evaluator using normal loading and shared evaluation. Supported dependency evidence, classification readers, and query resolution are prerequisites.

A known target outside the allowlist violates the policy. Missing categories, unresolved targets, and unavailable required evidence remain explicit findings/incomplete checks rather than being permitted by default. Outside-scope targets remain relevant endpoints without changing Service membership. Code dependencies and runtime interactions remain distinct. Inherit Rule presentation with typed category, evidence, and query controls.

[Category policy example](generic-rules.md#allowed-dependencies-by-category-proposed) · [Authoring](authoring.md)
