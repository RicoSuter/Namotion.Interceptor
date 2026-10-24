---
title: Predefined Architecture Policies
navTitle: Predefined Policies
status: Planned
---

# Predefined Architecture Policies [Planned]

Provide documented domain-specific defaults over generic rule capabilities. A preset can configure an existing concrete Rule type or use a specialized concrete subtype when behavior or input defaults differ. Normal `$type` discrimination selects the implementation in either case. Exact catalog packaging, names, default inputs, and extension contracts are proposed.

## Implementation readiness

**Area:** ArchDocs.

**Delivery:** Planned policy definitions/evaluators published through the normal subject/type registration and graph mechanisms. Registration alone does not execute them.

**Prerequisites:** The common Rule contract, generic primitives, supported fact requirements, defined invocation scope, and explicit repository selection.

## Every Host belongs to a Service [Planned]

The proposed [HostsAssignedToServicesRule](hosts-assigned-to-services-rule.md) checks every Host discovered by the invoking SourceRepository against the direct resolved Host collections of Services in the loaded architecture scope. At least one match passes; overlapping assignment is allowed. Selecting only a CodeProject, Resource, or nested Service does not implicitly select its underlying Hosts.

An unmatched Host produces a finding with its graph path, source project, Rule definition/block location, and remediation: update a Service's `componentSources`, correct its existing selection rule, or define the missing logical Service. Link to the defining `rules.md` section or explicit guide so a human or agent can learn the authoring syntax and rerun the check. The evaluator cannot infer business ownership from a name.

This policy is opt-in through repository rule selection, not an unconditional Host domain invariant. Complete discovery with zero Hosts reports zero evaluated subjects and zero violations; a separate count policy can require Hosts. Hosts with no selected/available Service context must not pass merely because the architecture definitions failed to load. Failed required discovery yields incomplete validation.

The generic [MembershipRule](membership-rule.md) can express similar policies using explicit queries, container type, member collection, and minimum/maximum counts. This predefined type removes repeated domain wiring by supplying documented repository-relative defaults. The [authoring reference](authoring.md) demonstrates both forms in one page and one JSON file per rule.

## Other useful presets [Planned]

- Important projects/Services have a responsibility, category, and documentation reference.
- Selected executable roles have intended deployment-unit membership.
- Exposed interfaces reference an available Contract when that policy applies.
- Every selected dependency target has the classification required by a layer policy.
- Architectural queries and required rule suites continue to select the expected elements.

Choose defaults by applicability and available evidence rather than forcing one architecture style. A utility library may need contracts/documentation without a Host or deployment. Broader presets must declare required analyzers and authored context; semantic naming, compatibility, or runtime checks are unavailable until their respective evidence sources exist.

[Rule overview](overview.md) · [Generic families](generic-rules.md) · [Rule subject](../subjects/rule.md)
