---
title: MembershipAnalyzer
navTitle: MembershipAnalyzer
status: Planned
---

# MembershipAnalyzer [Planned]

## Class definition [Planned]

**MembershipAnalyzer : [Analyzer](../subjects/analyzer.md)** compares selected subject identities with referenced collections of compatible containers. JSON uses `$type: Namotion.ArchDocs.MembershipAnalyzer`. It supports reusable coverage and assignment policies without a separate behavior discriminator.

## Configuration properties [Proposed schema]

| Property | Type | Meaning |
|---|---|---|
| `minimum` | Nonnegative integer | Required minimum matching containers per subject; examples use one. |
| `maximum` | Optional nonnegative integer | Maximum matching containers; omitted means no upper limit. Validate bounds together. |
| `membersProperty` | Collection-property path | Referenced collection to inspect; invalid or unavailable collections are configuration errors. |
| `containerType` | Registered subject-type identifier | Compatible container filter; it does not instantiate or convert subjects. This selects data types, while the Analyzer's `$type` selects its implementation. |
| `subjectSources` | [PathQuery](../shared/path-queries.md) collection | Subjects to validate. |
| `containerSources` | PathQuery collection | Candidate containers. |

## State properties [Proposed schema]

Inherit Analyzer's capabilities, definition location, and definition diagnostics. Match counts, unassigned subjects, and other findings belong to the invoking repository/runner.

## Behavior and readiness

**Area:** ArchDocs. **Delivery:** Planned concrete subject and evaluator using normal loading and shared evaluation. Queries, compatible collection readers, identity comparison, and source provenance are prerequisites.

A minimum of one with no maximum permits overlapping membership. Complete empty subject selection reports zero evaluated subjects; unresolved explicit scopes remain errors. Selecting a nested Service or Resource does not implicitly select its underlying Hosts. Inherit Analyzer's common presentation with typed count, collection, and query fields.

[Host assignment specialization](hosts-assigned-to-services-analyzer.md) · [Authoring example](authoring.md#a-rulesmd-document-proposed)
