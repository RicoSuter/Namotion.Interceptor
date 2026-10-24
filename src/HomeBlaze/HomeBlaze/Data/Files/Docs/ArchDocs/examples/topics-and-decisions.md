---
title: Topics, Guides, and Decision Records
navTitle: Topics, Guides, and Decision Records
status: Planned
---

# Topics, Guides, and Decision Records [Planned]

A documentation hierarchy can follow concerns and workflows instead of Services. Keep subject definitions in one catalog and reference them wherever useful. The same model can support tutorials, handbooks, operations guides, ADRs, and a service catalog without moving the architecture subjects into those document trees.

## Topic-led structure

```text
docs/
  catalog/Orders.json
  catalog/Payments.json
  deployment-units/
  environments/
  guides/onboarding.md
  concepts/messaging.md
  operations/releasing.md
  decisions/0007-separate-worker.md
```

The catalog, units, and environments use the same definitions as the JSON example. Topic folders do not own duplicate copies of those Services.

## An operations guide [Planned]

The optional widgets are the same ones used in the architecture overview.

````markdown
# Releasing the application

Before a release, review the schema and contract compatibility checks.

## Release units and environments

:: Namotion.ArchDocs.DeploymentMatrix Path=/
::

## Systems affected

:: Namotion.ArchDocs.ServiceTable Path=/catalog
::

The table describes intended installations; verify observed state through the relevant operational tooling.
````

## A decision describing existing subjects [Planned]

This decision page references a Service through ordinary metadata and optionally embeds its existing summary. It does not turn an ADR into a Service or select a deployment topology by folder placement.

````markdown
---
title: Separate background processing
metadata:
  relationships:
    - kind: documents
      target: /catalog/Orders
---

# Separate background processing

## Context

Document the workload requirement and the evidence behind it.

## Decision

Run background processing in a separate process while retaining a coordinated release.

:: HomeBlaze.Components.Widget Path=/catalog/Orders
::

## Consequences

Explain the operational and compatibility costs.
````

Generic metadata storage for JSON subjects and body-block entry points exist. Frontmatter metadata, automatic page context, and rich overlays on normal links are separate planned host extensions. Master-detail can start inside a table widget without requiring document-level context slots.

[Page contributions](../integration/page-contributions.md) · [ServiceTable](../widgets/service-table.md) · [Examples](overview.md)
