---
title: ArchitectureCheckResults
navTitle: ArchitectureCheckResults
status: Planned
---

# ArchitectureCheckResults [Planned]

Display the shared [architecture-check](../shared/architecture-checks.md) results with useful errors, source links, and remediation guidance. Reuse the same finding records produced by the [CLI](../integration/cli-and-ci.md); rendering a widget must not introduce a second rule evaluator or silently fix inputs.

## Configuration properties [Proposed schema]

| Property | Type | Default and meaning |
|---|---|---|
| `Path` | Subject/folder path `string` | Required scope for repository discovery and result aggregation. Each repository evaluates its configured Rule selections against required graph inputs. |
| `RepositorySources` | Optional [PathQuery](../shared/path-queries.md) list selecting [SourceRepository](../subjects/source-repository.md) | Omitted: repositories in scope. Empty: none. |
| `RuleSources` | Optional PathQuery list selecting [Rule](../subjects/rule.md) | Omitted: show results for all rules selected by those repositories. Empty: show no rule results. A display filter does not enable otherwise unselected rules. |

## Markdown example [Proposed]

````markdown
:: Namotion.ArchDocs.ArchitectureCheckResults Path=/
::
````

## Behavior and limits [Planned]

Show violation counts by severity, invalid configuration, incomplete checks, evaluated counts, and analysis freshness. Each finding identifies its rule and affected subjects, links to the discovered source and editable definition where known, and offers the rule's remediation text and guide. A green state requires complete current evaluation according to the selected scope and threshold.

Refresh/evaluation is an explicit shared operation and may also respond to settled graph changes. Several widgets reuse results for the same inputs; opening a view does not start unrelated discovery or remote operations. Exact evaluation ownership, scheduling, caching, and result lifetime remain design choices. An outdated report cannot be shown as a current success.

Selecting a finding can reveal the subject summary, rule definition, and help document in master-detail UI. Navigation uses existing mechanisms. Editing remains with the canonical file owner and should produce a focused diff; UI-only rule definitions and hidden suppressions are not supported by the design. Diagram annotations reuse these same results and source evidence.

## Implementation readiness

**Area:** ArchDocs. Automatic page contributions separately need HomeBlaze extensions.

**Delivery:** Planned extension through existing component registration and explicit Markdown embedding.

**Prerequisites:** Headless rule evaluation, structured finding records, supported rule loading, source/reference navigation, and summary components. The widget does not require the CLI executable to be running.

[Validation scenario](../scenarios/architecture-validation.md) · [General widgets](overview.md) · [Readiness](../implementation-readiness.md)
