---
title: Architecture Tests
navTitle: Architecture Tests
---

# Architecture Tests

These planned Analyzer subjects illustrate architecture tests with their explanations beside their JSON configuration. Analyzer types, evaluators, and CLI execution are not implemented. Repository.json proposes selecting the named children through `/ArchDocs/rules.md/*`.

## Host assignment

Every discovered Host must belong to at least one logical Service. Shared membership is allowed. If a Host is missing, edit the appropriate Service.md's `$service.componentSources`, correct an existing path analyzer, or define the missing Service. Selecting a CodeProject alone does not select its Host. Rerun validation after the edit.

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

This sample intends to release each application Host through at least one deployment unit. Update the relevant unit's componentSources when a new Host needs release membership. This is an explicit sample policy, not a generic requirement that all runnable processes be deployed this way.

:: Namotion.ArchDocs.MembershipAnalyzer#hostsHaveDeploymentUnit
```json
{
  "Name": "Application Hosts have release grouping",
  "Severity": "warning",
  "Documentation": "#host-release-coverage",
  "Minimum": 1,
  "MembersProperty": "components",
  "ContainerType": "Namotion.ArchDocs.DeploymentUnit",
  "SubjectSources": [
    {
      "Path": "/ArchDocs/Repository/hosts/*"
    }
  ],
  "ContainerSources": [
    {
      "Path": "/ArchDocs/deployment-units/*"
    }
  ]
}
```
::

The sample's current intended memberships cover its three application Hosts. This is expected model behavior, not evidence of a passing executable analyzer suite.

## Project categories

The [standalone analyzer](rules/projects-have-category.json) requires authored architectural categories for the discovered projects. Repository.json selects this analyzer through `/ArchDocs/rules/**` alongside the named rules above. The sample intentionally does not supply these category labels yet, so the future evaluator should report missing-category warnings until the documentation is enriched. Choose categories from project responsibilities and store them through the repository's descendant metadata; do not overwrite provider-owned technical classification.

The concrete RequiredPropertyAnalyzer type and `metadata.category` reader are proposed and unimplemented. [Analyzer authoring](../Docs/ArchDocs/rules/authoring.md) documents JSON files, Markdown blocks, directory/global selection, and the future agent/CI workflow.
