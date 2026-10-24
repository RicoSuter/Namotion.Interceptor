---
title: Subject Summaries and Details
navTitle: Subject Summaries and Details
status: Planned
---

# Subject Summaries and Details [Planned]

Provide useful summaries and navigable details for JSON subjects, referenced subjects, and optional Markdown embeds.

## Implementation readiness

**Delivery:** Can be implemented through existing subject and component extension points. The ArchDocs feature itself is not implemented.

**Prerequisites:** Domain subjects and registered Widget/Page/Edit components; no page-context slots required.

## Behavior [Planned]

Show available descriptive facts, component memberships, contracts, interfaces, relationships, deployments, diagnostics, and provenance according to capabilities. Reuse compact presentation in tables, diagram nodes, and later link previews. A Markdown document defining a subject remains an ordinary document.

## Platform boundary [Implemented]

The current component registry resolves a single Page/Edit/Widget registration with base/interface fallback. IPage provides navigation metadata and does not render a page. Keep UI registrations separate from headless domain types.

## Related specifications

[Overview and status legend](../overview.md) · [Platform readiness](../implementation-readiness.md)
