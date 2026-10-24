---
title: Architecture Widgets
navTitle: Architecture Widgets
status: Planned
---

# Architecture Widgets [Planned]

Reusable views of the same graph, independent of document format.

## Implementation readiness

**Delivery:** Can be implemented through existing subject and component extension points. The ArchDocs feature itself is not implemented.

**Prerequisites:** Domain subjects, shared queries and relationship readers, plus registered Blazor components.

## Existing presentation entry points [Implemented]

Registered Widget, Page, and Edit components, explicit Markdown blocks, subject references, and manual CanvasLayout placement are available. IPage supplies navigation information; rendering still requires a registered Page component.

## Views [Planned]

- [Subject summaries and detail pages](subject-details.md)
- [ServiceTable](service-table.md)
- [DeploymentMatrix](deployment-matrix.md)
- [ArchitectureDiagram](architecture-diagram.md)
- [Contract views](contract-view.md)

All ArchDocs views are unimplemented. Their explicit rendering can be built as extensions after domain prerequisites; automatic page-context insertion and ordinary-link overlays need the separate page-contribution extension. Reuse underlying queries and summary components across surfaces.

## Detailed behavior [Planned]

Widgets are reusable architecture views independent of documentation format. Path queries and optional filters select data; presentation controls can select grouping, labels, visible subject and relationship kinds, detail, and layout. Services, Components, Context, and Deployment are proposed diagram presets, not arc42 section types or a fixed hierarchy imposed on the model. Exact configuration options remain under discussion. ServiceTable and DeploymentMatrix can supply domain-specific defaults over shared query and subject-presentation infrastructure.

Crosscutting pages can aggregate several services, deployment units, environments, and resources. Proposed master-detail behavior keeps the document visible while selection reveals details alongside the list, using the context slot where appropriate. Selection is local UI state and does not create a new domain subject. Links, previews, and drill-down views address the same underlying subjects.

The user wants one ordinary arc42 Markdown document with authored explanations and embedded tables, diagrams, and summaries over the shared domain. The same widgets also work in service pages, guides, ADRs, or other architecture documentation. The sample's arc42.md demonstrates all twelve headings without a special document type or one subject per section. It is a composition of reusable views, not a separate architecture model.

Manual canvases and generated diagrams share subjects and semantic relationships. A semantic connection authored on a diagram should create or edit the typed model relationship; position, size, grouping, and visual annotations belong to that view. Existing CanvasLayout supports positioned Widget references, dragging, snapping, and scaling, but connector lines need additional support. Auto-layout, manual arrangement, and their combination remain presentation choices. Runtime scenario order is authored separately from static relationships.

## Related specifications

[Overview and status legend](../overview.md) · [Platform readiness](../implementation-readiness.md)
