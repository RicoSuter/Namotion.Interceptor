---
title: Architecture Tests and Rules
navTitle: Architecture Tests
status: Planned
---

# Architecture Tests and Rules [Planned]

ArchDocs is an architecture-validation framework with documentation and linked views over the same subject graph. [Rules](../subjects/rule.md) act as architecture unit tests: enable a policy, run it against current facts and authored intent, and use its findings to correct the architecture documentation or implementation.

## Implementation readiness

**Area:** ArchDocs; normal subject loading and headless host composition reuse HomeBlaze services.

**Delivery:** Planned subjects, generic evaluators, predefined policy definitions, shared findings, and UI/CLI adapters. None of these rule capabilities are implemented.

**Prerequisites:** Readable rule configuration, normal JSON/Markdown subject loading, repository-owned discovery/results, shared selectors, supported analyzer facts, and current-graph readiness.

## Rule reference

- [Rule subject](../subjects/rule.md): configuration/state contract and subject-specific presentation.
- [Authoring and selection](authoring.md): `rules.md`, one JSON file per rule, exact paths, and wildcard suites.
- [Generic rule families](generic-rules.md): naming, metadata, coverage, dependencies, cycles, references, and contracts. Concrete specifications include [NamingRule](naming-rule.md), [RequiredPropertyRule](required-property-rule.md), [MembershipRule](membership-rule.md), and [AllowedDependenciesRule](allowed-dependencies-rule.md).
- [Predefined policies](predefined-rules.md): domain-specific defaults over the generic capabilities.
- [Evaluation and findings](architecture-checks.md): shared execution semantics and actionable diagnostics.
- [CLI and CI](../integration/cli-and-ci.md): normal graph loading, readiness, reports, and exit status.

## Repository selection [Planned]

A SourceRepository's proposed `rules` list contains shared PathQuery records; read-only `resolvedRules` references the selected definitions. Select one Rule, the children of a Markdown page, a directory subtree, or every Rule in the loaded graph. `*` stays within one path segment and `**` spans zero or more segments. A Rule can be selected by several repositories; results are identified by invocation owner, Rule identity, and the current inputs.

Rule selection enumerates compatible Rule subjects. Wildcard/directory queries filter out ordinary Markdown pages, folders, and other subject kinds. An exact incompatible target, unavailable explicit target, invalid predicate, or unavailable concrete rule type is a diagnostic. Deduplicate overlapping selectors by Rule identity. Selecting two independently authored Rule definitions with equivalent configuration does not merge their identities.

Proposed default: omitted or empty `rules` selects none; use an explicit global query to enable all loaded definitions. Loading or registering a Rule never selects or executes it by itself. A successful empty query reports zero selected tests; required-count policies can prevent an unintentionally empty suite. Display selected test counts in CLI/UI reports.

Definitions from a predefined catalog must be published at documented graph paths, just like shared analyzers. Available implementation types alone are not selectable definitions. Exact catalog packaging/publication is open. Global means the loaded graph, not every file on disk or an implicit search of the machine.

## Inputs and invocation [Planned]

Each invocation receives the selected repository, the explicit loaded architecture/documentation scope, and available current analyzer facts/provenance. Domain-specific defaults can inspect the invoking repository's Hosts without hard-coding `/Repository`; a generic configured rule can select explicit graph scopes. Absolute selectors remain absolute, and definition-relative references keep their normal origin semantics rather than silently changing meaning for each caller.

Rules declare required facts/capabilities. Use the repository's selected analyzers and normal workers to produce those inputs, then evaluate after required discovery and memberships settle. Missing capabilities, failed analysis, or unavailable architecture context make a check incomplete. Automatic selection of additional analyzers is not implied by selecting a rule; any future dependency-selection behavior must be explicit. A Host-to-Service policy needs authored Service definitions as well as discovered Hosts.

Definitions are read-only during a run. Several repositories may evaluate one definition concurrently with independent inputs/results/cancellation. Changes to a selected definition or wildcard membership invalidate affected results. A rule-file load failure must not be mistaken for a successful removal of every rule. Failed or obsolete work cannot publish a current passing result.

## Agent workflow [Planned]

Run many selected rules, collect actionable errors, inspect their definitions and explanations, edit canonical files, and rerun. A finding identifies what was tested and what evidence failed it, with the rule's document as a natural help destination. Naming, membership, metadata, and reference errors can point directly to editable documentation/configuration.

Code-derived facts remain evidence. If a naming or dependency constraint is actually violated by code, report a code change or an explicit architecture decision as the required action. Do not hide the violation by inventing documentation facts, overriding discovery, or weakening enabled policies merely to obtain a passing result. The framework validates supported facts and references; it cannot establish the truth of arbitrary prose.
