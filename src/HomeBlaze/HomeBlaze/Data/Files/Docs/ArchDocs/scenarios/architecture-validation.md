---
title: Keep Documentation Aligned with Code
navTitle: Discovery and Validation
status: Planned
---

# Keep Documentation Aligned with Code [Planned]

Use discovery, reusable membership rules, linked views, and architecture validation to minimize duplicated information. The workflow is independent of documentation style and can run locally, in HomeBlaze, and in CI. All ArchDocs types, widgets, rule schemas, and commands in this scenario remain planned or proposed.

## Minimal authored information

Define a SourceRepository with a file-relative source root and selected analyzers. Define logical Services with component-source rules broad enough to include their discovered parts. Keep business responsibilities, intended boundaries, decisions, and unsupported relationships as authored information. Reuse those definitions from every document rather than maintaining a separate list for each diagram.

The [JSON catalog](json-catalog.md) shows `/Repository` and Services under `/catalog`. The [membership policy](../shared/architecture-checks.md#every-host-belongs-to-a-service-planned) supplies the proposed root `.metadata.json` rule requiring every discovered Host to appear in at least one Service's direct Host set. This is an explicit team policy, not a generic restriction on all Hosts. Multiple Services may select the same Host.

Add a real `guides/assign-hosts.md` document beside the catalog with the supported syntax and correction steps. A minimal guide could contain:

````markdown
# Assign a Host to a Service

Find the Host's discovered graph path in the validation error. Identify the logical Service it implements. Update that Service's componentSources so its resolved components include the Host, or define the missing Service.

For a JSON-defined Service, an explicit proposed entry is:

```json
{ "path": "/Repository/hosts/Orders.Worker" }
```

Add the entry to the existing componentSources array; preserve its other members and metadata. If a naming or path rule already describes the Service, correct that rule instead of adding redundant individual entries. The same Service configuration can live under a named Markdown frontmatter child.

Selecting only a CodeProject does not implicitly select its Host. Check the Service's resolved hosts collection, then run architecture validation again. Choose the logical boundary deliberately; the tool cannot decide business ownership from a project name.
````

The guide and error can link to the canonical [Service](../subjects/service.md), [path query](../shared/path-queries.md), and [authoring](../file-authoring.md) reference. Final examples must use the implemented schema when it exists; the snippets here illustrate proposed syntax.

## Add a runnable project

1. A developer adds `Orders.Worker.csproj` in the source checkout.
2. The selected analyzer discovers a CodeProject and, where supported, its Host with source provenance.
3. Existing Service membership rules are reevaluated. A matching rule includes the Host without another manually maintained architecture entry.
4. If no Service includes it, the membership check reports an error with the Host path, project file, rule location, remediation, and `guides/assign-hosts.md` link.
5. A human or agent follows the guide, edits the appropriate Service's canonical JSON or Markdown configuration, and reruns the same validation command.
6. A complete successful pass confirms membership. Dependency views, Service summaries, and linked documentation use the updated shared graph.

If source analysis fails, report incomplete validation rather than pretending that no Host needs assignment. A new library may create a CodeProject without a Host and therefore does not violate this particular Host policy. Other coverage rules can apply independently.

## Run locally and in CI [Proposed]

The future [CLI](../integration/cli-and-ci.md) provides equivalent readable and structured diagnostics:

```shell
archdocs check --docs ./docs --format text
archdocs check --docs ./docs --format json --output ./artifacts/architecture-checks.json
```

The command names are illustrative and not runnable today. A CI job would install/configure the selected future CLI and analyzers, run against its checkout, and fail on the configured severity threshold. The local agent loop is run, inspect findings and guides, edit canonical definitions, and rerun. Validation itself does not modify documentation or start HomeBlaze.

## Reuse the results in documents

Explicit planned widgets can show the same evidence in an arc42 document, service page, guide, or architecture decision:

````markdown
:: Namotion.ArchDocs.DependencyDiagram Path=/Repository
::

:: Namotion.ArchDocs.ArchitectureCheckResults Path=/
::
````

Use [ImpactView](../widgets/impact-view.md) to find known dependents and referencing documentation. Those documents are candidates for review, not proof that their prose is stale or correct. Use [ScenarioDiagram](../widgets/scenario-diagram.md) when ordered interactions are useful; the author supplies the sequence and the graph supplies participant identities and links.

[Scenarios](overview.md) · [Architecture checks](../shared/architecture-checks.md) · [CLI and CI](../integration/cli-and-ci.md) · [General widgets](../widgets/overview.md)
