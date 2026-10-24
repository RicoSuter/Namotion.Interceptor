---
title: Standalone JSON Catalog
navTitle: Standalone JSON Catalog
status: Planned
---

# Standalone JSON Catalog [Planned]

Use this when a team wants ordinary documentation pages plus a separately managed software catalog. It can be built through existing JSON/configurable subject and explicit widget entry points once the planned ArchDocs types and services exist. It does not depend on frontmatter child definitions or page-context slots.

## Assumed layout

Mount repository/docs as the documentation graph root. The analyzer reads the parent repository through an explicit raw source root. The .NET projects here are an analyzer example, not a requirement of Service or Host.

```text
repository/
  Commerce.slnx
  src/Orders.Api/Orders.Api.csproj
  src/Orders.Worker/Orders.Worker.csproj
  src/Orders.Contracts/Orders.Contracts.csproj
  docs/
    Repository.json
    catalog/Orders.json
    catalog/Orders.metadata.json
    deployment-units/Orders.json
    environments/Staging.json
    architecture.md
```

## Source and logical architecture [Planned]

Repository.json:

```json
{
  "$type": "Namotion.ArchDocs.SourceRepository",
  "sourceRoot": "../",
  "analyzers": [
    { "path": "/analyzers/CSharpProjects", "config": {} }
  ]
}
```

Assume the host has registered and published one shared [Analyzer](../subjects/analyzer.md) subject at the illustrative `/analyzers/CSharpProjects` route. This repository selects that existing subject; another repository can select the same instance. Publishing the singleton and its exact route are planned wiring, not an implemented built-in catalog. Repository-owned results stay under `/Repository`; the shared analyzer does not acquire those children. Omitted or empty selection does not silently enable every installed analyzer.

For an unambiguous executable project, a minimal project input could contain the following literal XML. The analyzer reads it without building or evaluating MSBuild. Projects with only a library output remain CodeProjects.

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
</Project>
```

Commerce.slnx can group the same projects:

```xml
<Solution>
  <Project Path="src/Orders.Api/Orders.Api.csproj" />
  <Project Path="src/Orders.Worker/Orders.Worker.csproj" />
  <Project Path="src/Orders.Contracts/Orders.Contracts.csproj" />
</Solution>
```

The repository owns one [CodeSolution](../subjects/code-solution.md) and the three CodeProjects separately. The solution references those projects without cloning them. A second solution can reference them too; solution membership does not establish a Service or deployment boundary.

catalog/Orders.json:

```json
{
  "$type": "Namotion.ArchDocs.Service",
  "name": "Orders",
  "componentSources": [
    { "path": "/Repository/projects/Orders.*" },
    { "path": "/Repository/hosts/Orders.*" }
  ]
}
```

The repository owns discovery results. Service membership references them, and can select libraries as well as Hosts. The generated names/paths are simplified for this example; duplicate project names need stable distinct keys.

## Release and environment [Planned]

deployment-units/Orders.json:

```json
{
  "$type": "Namotion.ArchDocs.DeploymentUnit",
  "name": "Orders",
  "componentSources": [
    { "path": "/catalog/Orders/hosts" }
  ]
}
```

environments/Staging.json:

```json
{
  "$type": "Namotion.ArchDocs.Environment",
  "name": "Staging",
  "deploymentUnitSources": [
    { "path": "/deployment-units/Orders" }
  ]
}
```

This declares one intended Deployment in Staging. It neither runs software nor establishes health. Another environment can select the same unit without cloning the Service or Hosts.

## Enrichment through existing storage [Planned]

catalog/Orders.metadata.json is the existing sidecar naming convention for Orders.json. Generic metadata storage is [Implemented]; ArchDocs relationship readers are [Planned]. This example relationship documents the Service from its architecture page without changing the Markdown source format:

```json
{
  "notes": "Released as one coordinated unit.",
  "relationships": [
    { "kind": "documentedBy", "target": "/architecture.md" }
  ]
}
```

Relationship kinds are extensible. This descriptive relationship does not assert a runtime call.

## Ordinary documentation [Planned]

architecture.md contains normal text and optional generic views. The widget type names are proposed; the body-block authoring mechanism already exists.

````markdown
# Architecture

Orders accepts work through an API and processes background work through a worker.

## Services

:: Namotion.ArchDocs.ServiceTable Path=/catalog
::

## Deployments

:: Namotion.ArchDocs.DeploymentMatrix Path=/
::
````

[Service](../subjects/service.md) · [Repository](../subjects/source-repository.md) · [DeploymentUnit](../subjects/deployment-unit.md) · [Environment](../subjects/environment.md) · [Other layouts](overview.md)
