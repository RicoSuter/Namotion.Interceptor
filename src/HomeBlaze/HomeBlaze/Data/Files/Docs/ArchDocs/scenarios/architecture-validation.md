---
title: Keep Documentation Aligned with Code
navTitle: Discovery and Validation
status: Planned
---

# Keep Documentation Aligned with Code [Planned]

Use discovery, reusable membership rules, linked views, and architecture unit tests to minimize duplicated information. The workflow is independent of documentation style and runs against the same subject model locally, in HomeBlaze, and in CI. All Analyzer types, evaluators, widgets, and commands remain planned or proposed.

## Define and enable documented tests

Define a SourceRepository with a file-relative source root and selected providers. Define logical Services with component-source queries broad enough to include their discovered parts. Keep responsibilities, intended boundaries, decisions, and unsupported relationships as authored information.

The [JSON catalog](json-catalog.md) shows `/Repository` and Services under `/catalog`. Add an ordinary `rules.md` using the [complete authoring example](../rules/authoring.md#a-rulesmd-document-proposed): each named Analyzer block has its explanation and remediation next to its JSON configuration. Alternatively, use [one JSON file per analyzer](../rules/authoring.md#one-json-file-per-analyzer-proposed) and link it to a normal policy document.

Select `/rules.md/*` to enable that page's tests, `/rules/**` for a nested catalog, or `/**` for every Analyzer in the loaded graph. These are repository `analyzers` path queries, not a separate suite entity. A predefined Host-assignment analyzer requires every discovered Host to appear in at least one Service's direct Host set. This is an explicit team policy; overlapping membership remains valid.

A useful policy document explains that an error's Host path can be added to the appropriate Service's `componentSources`, or that an existing broad selection query should be corrected. Selecting a CodeProject alone does not implicitly select its Host. Link to the canonical [Service](../subjects/service.md), [path queries](../shared/path-queries.md), and [file authoring](../file-authoring.md) syntax so a human or agent can make the intended edit.

## Add a runnable project

1. A developer adds `Orders.Worker.csproj` in the source checkout.
2. The selected provider discovers its CodeProject and, where supported, Host with source provenance.
3. Service membership queries are reevaluated. A matching query includes the Host without another manually maintained architecture entry.
4. The repository resolves selected Analyzer subjects and evaluates them after required discovery and memberships settle.
5. If no Service includes the Host, the test reports its graph path, project file, Analyzer file/block, remediation, and the explanation in `rules.md`.
6. A human or agent reads that explanation, edits the appropriate Service's canonical JSON or Markdown configuration, and reruns validation.
7. A complete successful pass confirms membership. Dependency views, summaries, backlinks, and other tests use the updated shared graph.

A new library can create a CodeProject without a Host and does not violate this particular Host policy. Independent naming, required-category, and dependency rules can validate it. A real code dependency violation requires a code change or an explicit architecture decision; inventing documentation labels is not a valid repair. Failed analysis or missing policy context produces incomplete validation, not a passing empty suite.

## Run locally and in CI [Proposed]

The future [CLI](../integration/cli-and-ci.md) provides readable and structured diagnostics:

```shell
archdocs check --docs ./docs --format text
archdocs check --docs ./docs --format json --output ./artifacts/architecture-checks.json
```

The commands are illustrative and not runnable today. The CLI starts a minimal normal subject host for the explicit documentation scope, loads Markdown and ArchDocs subjects, and publishes required provider/catalog subjects through shared mechanisms. SourceRepositories run selected providers and select Analyzer definitions; the host waits for their required graph inputs before checking. An explicit normal root configuration can cover multiple documentation mounts. A separate parser/scanner, unrelated device graph, or running web server is not required.

The local/CI agent loop is run, inspect findings and analyzer explanations, edit canonical sources, and rerun. The runner reports selected test/evaluated counts, returns unsuccessful status for policy failures or incomplete evaluation, and shuts down normally. Validation does not write corrections itself.

## Reuse the evidence in documents

```markdown
:: Namotion.ArchDocs.DependencyDiagram Path=/Repository
::

:: Namotion.ArchDocs.ArchitectureCheckResults Path=/
::
```

Use [ImpactView](../widgets/impact-view.md) to find known dependents and documents to review. Use [ScenarioDiagram](../widgets/scenario-diagram.md) when ordered interactions matter; authors supply order and the graph supplies participant identities and links. The separate [Commerce development fixture](../../../ArchDocs/rules.md) demonstrates two rules on one Markdown page with repository wildcard selection.

[Scenarios](overview.md) · [Architecture tests](../rules/overview.md) · [CLI and CI](../integration/cli-and-ci.md)
