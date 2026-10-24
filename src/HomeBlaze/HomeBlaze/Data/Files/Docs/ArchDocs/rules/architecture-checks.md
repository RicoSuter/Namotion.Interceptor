---
title: Architecture Checks
navTitle: Architecture Checks
status: Planned
---

# Architecture Checks [Planned]

Evaluate selected architecture-test subjects against the current discovered and authored graph. The shared evaluator serves HomeBlaze and a headless CLI/CI host and produces actionable findings for humans and agents. [Rule definitions, families, and selection](overview.md) are specified in rules/; this page owns common execution and diagnostic behavior.

## Implementation readiness

**Area:** ArchDocs. Evaluation and diagnostic records are headless domain capabilities; UI and CLI are adapters over the same graph and rule contracts.

**Delivery:** Planned shared evaluation with [CLI/CI execution](../integration/cli-and-ci.md) and [ArchitectureCheckResults](../widgets/architecture-check-results.md). Rule subjects and the evaluator are unimplemented.

**Prerequisites:** Normal subject loading, repository-selected Rules, supported analyzer facts, source provenance, shared selectors, and a complete evaluation scope. Missing required analysis must not become a successful check.

## Definitions and run ownership [Planned]

A [Rule](../subjects/rule.md) has one canonical JSON or Markdown owner. The [authoring reference](authoring.md) shows named JSON-configured children in `rules.md`, one JSON file per definition, and page/directory/global suites. Repositories select existing definitions through `rules` and expose read-only `resolvedRules`; configuration stays on the definition, while per-invocation findings and cancellation stay with the repository/runner.

Generic and predefined check kinds consume the invocation's repository, explicit architecture/documentation scope, and available current facts. Rule definitions declare requirements and cannot assume that a plain name or unavailable analyzer supplies evidence. A documented preset can provide defaults for the invoking repository without rewriting explicit absolute selectors.

The initial concrete policy is [every Host belongs to a Service](predefined-rules.md#every-host-belongs-to-a-service-planned). Naming, category-based dependency constraints, coverage, cycles, required metadata, and reference checks are documented in the [generic family catalog](generic-rules.md). Enabling many selected tests should produce a combined report rather than hiding all but the first violation.

## Findings [Planned]

Each finding needs a stable Rule identity/path, severity, readable message, affected subject identities/paths, relevant relationship evidence, and source locations. Keep the Rule definition/block location, discovered source location, and editable documentation/configuration location distinct. Include remediation and a resolvable help link when available. A Markdown-owned Rule can link to the explanation beside its block, so no separate help document is required.

Never fabricate line numbers or a guaranteed edit target when the analyzer cannot establish them; report the available file, named block, property, or JSON pointer instead. For an unassigned Host, identify the Host/project and suggest updating an appropriate Service's componentSources or defining the missing Service. Do not guess business ownership from its name. For a forbidden dependency, identify the actual edge and policy, including the source evidence and relevant categories.

Findings should identify the kind of correction needed: authored documentation/configuration, source code, missing capability/input, or an architecture decision. The agent workflow is inspect the finding and explanation, edit the canonical source, and rerun. Validation itself does not modify files. Repairing documentation must not invent facts, change discovered values, or weaken intended rules merely to suppress a real violation.

## Current evaluation and completeness [Planned]

Evaluation consumes one complete coherent graph and its analysis diagnostics. Results distinguish passes, violations, invalid configuration, and checks that could not be evaluated. A last-successful graph remains useful in the UI with its age and failure state, but cannot yield a clean current CI result after required analysis fails. Report selected test counts, evaluated subject counts, and required capability coverage, including genuinely empty scopes.

A valid empty selection is distinct from an unresolved explicit path or failed definition load. Rule-suite enumeration filters compatible Rules out of document/folder subtrees; it does not convert other subjects into tests. Overlapping selections deduplicate by Rule identity, while independently authored equivalent tests remain distinct. Shared definitions evaluated for two repositories retain two invocation contexts and result sets.

Use the same evaluation and findings in UI and CLI. Presentation, command arguments, and serialization are adapters. Deterministic ordering and stable finding keys make reports reviewable. Time, revision, and freshness are explicit report context rather than hidden inputs to rule truth. Changes during evaluation invalidate obsolete results; canceled or superseded work cannot publish a current pass. Warning thresholds, exclusions, and any future suppressions must be explicit; baseline/suppression syntax remains deferred.

[Architecture tests](overview.md) · [CLI and CI](../integration/cli-and-ci.md) · [Results widget](../widgets/architecture-check-results.md) · [Validation scenario](../scenarios/architecture-validation.md)
