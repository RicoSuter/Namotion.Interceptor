---
title: Generic Analyzer Families
navTitle: Generic Analyzer Families
status: Planned
---

# Generic Analyzer Families [Planned]

Provide reusable concrete [Analyzer](../subjects/analyzer.md) types over subject properties, selectors, memberships, and supported relationships. The framework should support enabling many tests and collecting their findings in one run. The catalog below describes planned families; it does not select every analyzer type for the MVP. Several authored policies can configure the same concrete type.

## Implementation readiness

**Area:** ArchDocs. Pure graph/property checking primitives may later be reusable outside this domain, with placement chosen according to dependencies.

**Delivery:** Planned registered concrete Analyzer subjects with typed properties and shared evaluation primitives. Names, patterns, and detailed configuration fields are proposed.

**Prerequisites:** Current facts, shared queries, property/metadata access, relationship provenance, and concrete-type schemas. A family must report missing required evidence rather than silently accept an unsupported check.

## Useful policies

| Family | Examples | Required evidence and corrective guidance |
|---|---|---|
| Naming | Project suffixes, Host names, Service names, consistent casing, identifier patterns | Selected property values and a documented glob/regex convention. Report the value, expected pattern, and editable owner; a discovered project name may need a code/project-file change. |
| Required properties and metadata | Name, responsibility, category, layer, owner, technology, or documentation link | Applicable subjects and supported property/metadata access. Report the missing key and canonical configuration/metadata file. |
| Membership and coverage | Every Host belongs to a Service; production Hosts have release grouping; projects are classified | Subject and container selections plus membership references and minimum/maximum counts. Report unmatched subjects or conflicting assignments. |
| Allowed/forbidden dependencies | Domain may use Domain/Shared; tests cannot be production dependencies; prohibit references across chosen boundaries | Explicit source/target groups and edge kinds. Report the offending source-target edge and its evidence. |
| Acyclic dependencies | No project cycles, no cycles between selected layers or services | A complete selected graph for the chosen relationship kinds. Report an actual cycle path. |
| References and documentation | No broken subject links; important components have documentation; required descriptions/links exist | Supported reference parsing and source-to-subject mapping. Report the broken location or missing reference; do not claim arbitrary prose has been semantically verified. |
| Interface and contract coverage | Exposed interfaces have a Contract; selected projects expose required public API documentation | Supported exposure/association facts and relevant providers. Distinguish absent evidence from known missing associations. |
| Deployment consistency | Selected units exist, intended environment coverage, required bindings are documented | Authored deployment selections/bindings. Static documentation cannot establish runtime installation or health. |
| Selection and suite completeness | A membership selector still matches; at least one test executes; required categories are populated | Successful discovery and explicit expected counts. Ordinary empty selection remains valid unless a policy constrains it. |

## Naming and classification [Planned]

Naming checks can target any supported scalar property and be scoped to projects, Hosts, Services, or other subjects. Basic project XML can supply literal project names and paths. Namespace/type/member naming requires additional semantic providers; the basic project provider does not promise those facts. Settle pattern syntax and case behavior before implementing a matcher.

Architectural category/layer/owner labels can be authored as generic metadata or other supported classification properties. Their values follow the shared [Identifier](../shared/identifiers.md) model. Existing provider-owned `CodeProject.classification` records technical facts such as executable/library/test; it must not be overwritten to represent an authored Domain/Infrastructure layer. Categories can also be derived by explicit documented mapping rules over discovered facts; those mappings need their own selected capability and provenance, not hidden folder-name guesses.

Metadata preservation already exists, but typed category readers, query access, mapping-analyzer evaluation, and the final category comparison semantics still need implementation. A required-category analyzer can identify unclassified elements before a dependency policy runs; missing classification must remain visible rather than permitting every dependency by default. No mandatory Category or Layer subject is introduced.

The proposed [RequiredPropertyAnalyzer](required-property-analyzer.md) selects subjects through its `subjectSources` and requires a nonempty value at `property`. The supported property/metadata accessor and empty-value semantics need a final schema. The [working JSON analyzer](../../../ArchDocs/rules/projects-have-category.json) illustrates `metadata.category`, with its explanation in the sample's [rules.md](../../../ArchDocs/rules.md#project-categories). The currently missing labels are intentional example findings for the future agent-edit loop, not an implemented validation test.

## Generic membership configuration [Proposed]

The proposed [MembershipAnalyzer](membership-analyzer.md) compares selected subject identities with referenced collections of compatible containers. Its typed properties include `minimum`, optional `maximum`, `membersProperty`, `containerType`, `subjectSources`, and `containerSources`. See its canonical property table for validation and empty-scope behavior. [Analyzer authoring](authoring.md) supplies complete Markdown and JSON entry points.

## Allowed dependencies by category [Proposed]

This illustrative standalone Analyzer constrains code-reference edges for selected projects:

```json
{
  "$type": "Namotion.ArchDocs.AllowedDependenciesAnalyzer",
  "name": "Domain depends only on Domain or Shared",
  "severity": "error",
  "categoryProperty": "metadata.category",
  "fromCategory": "domain",
  "allowedTargetCategories": [
    "domain",
    "shared"
  ],
  "relationshipKinds": [
    "projectReference"
  ],
  "subjectSources": [
    {
      "path": "/Repository/projects/*"
    }
  ]
}
```
The [AllowedDependenciesAnalyzer](allowed-dependencies-analyzer.md) properties, metadata accessor, relationship identifier, and category vocabulary are proposed. Its `$type` selects the registered concrete implementation; category and relationship values describe the data being checked. A future registered schema must define these precisely. A known `domain -> infrastructure` edge violates this allowlist. Missing category, unresolved target, or unsupported/uncertain dependency evidence produces an explicit classification/reference/incomplete finding, rather than being interpreted as allowed. Out-of-scope targets are still relevant endpoints of selected outgoing edges; inspecting them does not add them to Service membership.

Select edge kinds explicitly. Project references, runtime API calls, messages, and documentation links have different meanings. A per-category allowlist and a forbidden source/target pair are complementary policy forms; evaluation precedence and default-deny behavior must be documented by the selected concrete analyzer type. For this allowlist example, targets outside the allowed set fail once their category is known.

## Agent-oriented findings [Planned]

Each evaluator should explain the expected condition, actual evidence, source locations, and the relevant correction path. Missing authored metadata may be repaired through the owning sidecar/frontmatter. Missing Service assignment may be repaired through component selectors. A real code naming/dependency violation needs a code change or an explicit decision to change the intended architecture. Changing a label to misrepresent the code or silently lowering an analyzer's severity is not a valid automatic documentation repair.

[Authoring](authoring.md) · [Predefined policies](predefined-rules.md) · [Evaluation and findings](architecture-checks.md)
