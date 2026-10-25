---
title: Analyzer Authoring and Selection
navTitle: Authoring and Selection
status: Planned
---

# Analyzer Authoring and Selection [Planned]

Keep an analyzer's explanation, executable configuration, and remediation understandable in ordinary source files. Both named JSON-configured Markdown blocks and one JSON file per Analyzer use the same [Analyzer subject](../subjects/analyzer.md). Concrete analyzer type names and schemas below are proposed and unimplemented. `$type` or the named block type selects the implementation, and each concrete subject exposes its own typed properties.

## Implementation readiness

**Area:** ArchDocs. Reuse normal configurable-subject and Markdown child loading.

**Delivery:** Existing standalone JSON and named body-block entry points can load Analyzer subjects after registration. Analyzer types/evaluators are planned. Generic frontmatter children are an additional planned HomeBlaze authoring option, not a prerequisite for the body-block examples.

**Prerequisites:** Concrete Analyzer registration and typed schemas, shared path queries, definition provenance, and preservation of surrounding prose/configuration.

## A rules.md document [Proposed]

Assume the documentation folder is mounted as the graph root and contains `/Repository`, `/catalog`, and `rules.md`. A normal JSON code fence is illustrative source only; the named subject block makes the enclosed configuration an actual subject definition. Existing body-block configuration uses PascalCase:

````markdown
# Architecture tests

## Host assignment

Every discovered Host must belong to at least one Service. Shared membership is allowed. Add a matching componentSources entry or correct the existing Service selection analyzer, then rerun validation.

:: Namotion.ArchDocs.HostsAssignedToServicesAnalyzer#hostsHaveService
```json
{
  "Name": "Every Host belongs to a Service",
  "Severity": "error",
  "Documentation": "#host-assignment"
}
```
::

## Host release coverage

Every application Host in this documentation scope should be included in at least one deployment unit. Change the intended release grouping if a newly added Host is missing.

:: Namotion.ArchDocs.MembershipAnalyzer#hostsHaveDeploymentUnit
```json
{
  "Name": "Hosts have a deployment unit",
  "Severity": "warning",
  "Documentation": "#host-release-coverage",
  "Minimum": 1,
  "MembersProperty": "components",
  "ContainerType": "Namotion.ArchDocs.DeploymentUnit",
  "SubjectSources": [
    {
      "Path": "/Repository/hosts/*"
    }
  ],
  "ContainerSources": [
    {
      "Path": "/deployment-units/*"
    }
  ]
}
```
::
````

The graph paths are `/rules.md/hostsHaveService` and `/rules.md/hostsHaveDeploymentUnit`. The page stays an ordinary MarkdownFile; its named children supply Analyzer identity and registered summaries. A failing test can link to `rules.md#host-assignment`, where a human or agent reads the policy and its correction steps. The generic coverage analyzer is an example team policy, not a requirement that every Host in the domain be deployable.

## One JSON file per analyzer [Proposed]

An alternative `rules/hosts-have-service.json` defines the same policy independently of a Markdown child:

```json
{
  "$type": "Namotion.ArchDocs.HostsAssignedToServicesAnalyzer",
  "name": "Every Host belongs to a Service",
  "severity": "error",
  "documentation": "../rules.md#host-assignment"
}
```
Its graph path is `/rules/hosts-have-service`, without `.json`. Here `rules.md` can contain only explanations and links to standalone rules. Choose one canonical definition for a test; two independently declared subjects are two tests even when their settings match. JSON files use proposed camelCase configuration, while body blocks use the existing property-casing convention.

## Select tests through paths [Proposed]

SourceRepository configuration can use any of these `analyzers` entries:

| Selection | Entry | Meaning |
|---|---|---|
| One test | `{ "path": "/rules.md/hostsHaveService" }` | Select that named Analyzer. |
| All tests on one page | `{ "path": "/rules.md/*" }` | Select compatible immediate named children. |
| Standalone rules in a directory | `{ "path": "/rules/*" }` | Select compatible direct children such as JSON-defined Analyzer subjects. |
| A complete analyzer subtree | `{ "path": "/rules/**" }` | Include nested Analyzer subjects, including named children of Markdown pages. |
| All loaded tests | `{ "path": "/**" }` | Select every Analyzer definition in the loaded graph, including published catalog rules. |

For example:

```json
{
  "$type": "Namotion.ArchDocs.SourceRepository",
  "sourceRoot": "../",
  "providers": [{ "path": "/providers/CSharpProjects", "config": {} }],
  "analyzers": [{ "path": "/rules.md/*" }]
}
```

Lists combine by union and deduplicate identities; supported `where` predicates can refine selection. Configuration belongs to each Analyzer definition. Unlike provider invocation entries, analyzer selectors do not introduce a second per-repository `config` copy or an ambiguous wildcard override scheme. Analyzer and provider selection remain separate; each check declares the facts it needs.

## Shared definitions and context [Planned]

A shared predefined [HostsAssignedToServicesAnalyzer](hosts-assigned-to-services-analyzer.md) uses the invoking repository and documented architecture scope as inputs. Generic concrete rules expose explicit subject/group queries as typed properties directly on the definition. An explicit absolute path in shared configuration is not rewritten when another repository selects it. If definitions cross documentation roots, configure their normal graph mounts and dependencies explicitly; do not search unrelated folders or silently load a full unrelated application graph.

[Analyzer overview](overview.md) · [Generic families](generic-rules.md) · [Predefined policies](predefined-rules.md) · [CLI](../integration/cli-and-ci.md)
