---
title: Multiple Repositories and Shared Components
navTitle: Multiple Repositories and Shared Components
status: Planned
---

# Multiple Repositories and Shared Components [Planned]

Use this when logical architecture crosses repository and documentation boundaries. A repository does not define a Service boundary, and a component can have several consumers without several subject identities.

## Source layout

Mount docs at the documentation graph root. Suppose repository definitions live in docs/repositories and raw source directories are siblings of docs:

```text
workspace/
  orders-source/
  shared-source/
  docs/
    repositories/Orders.json
    repositories/Shared.json
    catalog/Orders.json
    catalog/Payments.json
    resources/OrderEvents.json
    deployment-units/
    environments/
```

repositories/Orders.json:

```json
{
  "$type": "Namotion.ArchDocs.SourceRepository",
  "sourceRoot": "../../orders-source",
  "analyzerSources": [
    { "path": "/analyzers/CSharpProjects" }
  ]
}
```

repositories/Shared.json:

```json
{
  "$type": "Namotion.ArchDocs.SourceRepository",
  "sourceRoot": "../../shared-source",
  "analyzerSources": [
    { "path": "/analyzers/CSharpProjects" }
  ]
}
```

Each sourceRoot resolves from its defining file, not the documentation graph or the process working directory. Different analyzers can later describe different languages under those roots.

Both definitions select the same shared singleton [Analyzer](../subjects/analyzer.md), assumed to be published at the illustrative `/analyzers/CSharpProjects` route. The host's publication mechanism and route remain proposed. Each repository supplies its own input context and owns its analysis worker, results, diagnostics, and cancellation; neither creates or owns a copy of the Analyzer. Selection can differ by repository when additional analyzer subjects exist.

## Logical membership across repositories [Planned]

catalog/Orders.json can combine discovered elements and one shared Resource:

```json
{
  "$type": "Namotion.ArchDocs.Service",
  "name": "Orders",
  "componentSources": [
    { "path": "/repositories/Orders/hosts/**" },
    { "path": "/repositories/Shared/projects/Shared.*" },
    { "path": "/resources/OrderEvents" }
  ]
}
```

resources/OrderEvents.json:

```json
{
  "$type": "Namotion.ArchDocs.Resource",
  "name": "Order events",
  "kind": "Queue",
  "technology": "AMQP"
}
```

Payments can reference the same /resources/OrderEvents. Neither Service receives a cloned queue. Services can also select a concrete provider Host directly when that is sufficient; Resource is optional when it adds no useful distinction.

## Relationship versus release membership [Planned]

An Orders.metadata.json sidecar can describe an interaction independently of deployment grouping:

```json
{
  "relationships": [
    {
      "kind": "publishes",
      "target": "/resources/OrderEvents",
      "technology": "AMQP",
      "message": "OrderPlaced"
    }
  ]
}
```

A deployment unit might select only /catalog/Orders/hosts. If it also manages a Resource definition/configuration as part of the coordinated release, it can explicitly select /resources/OrderEvents as another participant. That choice does not assert automatic provisioning. Referencing an existing managed queue as a dependency does not require putting it in the release unit.

The same logical queue can have different environment-specific bindings. Exact binding fields remain open; do not make copies of the logical Resource merely to show Staging and Production. Provider Hosts, Endpoint bindings, component membership, and relationships retain separate meanings.

[Queries](../shared/path-queries.md) · [Resource](../subjects/resource.md) · [DeploymentUnit](../subjects/deployment-unit.md) · [Examples](overview.md)
