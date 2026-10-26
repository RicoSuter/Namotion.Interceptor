---
title: General Architecture Widgets
navTitle: General Widgets
status: Planned
---

# General Architecture Widgets [Planned]

General and aggregating views of the same graph, independent of document format. Subject-specific widgets, detail pages, and editors are specified in the relevant subject Markdown file.

## Implementation readiness

**Delivery:** Can be implemented through existing subject and component extension points. The ArchDocs feature itself is not implemented.

**Prerequisites:** Domain subjects, shared queries and relationship readers, plus registered Blazor components.

## Existing presentation entry points [Implemented]

Registered Widget, Page, and Edit components, explicit Markdown blocks, subject references, and manual CanvasLayout placement are available. IPage supplies navigation information; rendering still requires a registered Page component.

## General views [Planned]

- [ServiceTable](service-table.md)
- [DeploymentMatrix](deployment-matrix.md)
- [Architecture diagrams](architecture-diagram.md): shared behavior and rendering infrastructure
- [ServiceDiagram](service-diagram.md), [ComponentDiagram](component-diagram.md), [ContextDiagram](context-diagram.md), and [DeploymentDiagram](deployment-diagram.md): proposed specialized entry points
- [DependencyDiagram](dependency-diagram.md): discovered project/library references and optional Service or layer grouping
- [ImpactView](impact-view.md): known dependents and referencing documentation, with evidence explaining each result
- [ArchitectureCheckResults](architecture-check-results.md): shared validation errors, source links, remediation, and help documents
- [ScenarioDiagram](scenario-diagram.md): authored ordered interactions with linked graph participants

All ArchDocs views are unimplemented. Their explicit rendering uses registered components after domain prerequisites; live structural diagrams reuse Canvas and need its planned generic additions or shared rendering APIs. Automatic page-context insertion and ordinary-link overlays need the separate page-contribution extension. Reuse underlying queries and summary components across surfaces.

Prioritize views that reuse discovered facts and existing membership rules. Dependency and grouped views avoid maintaining parallel diagrams; impact/backlink exploration helps identify documents to review; validation highlights missing assignments and explicit architectural constraints. Scenario order still needs authored steps or separately supported runtime evidence. These are accepted feature directions with proposed schemas, not a selected MVP or an implementation plan.

## Subject-specific presentation

See each [subject specification](../overview.md#subjects) for its UI section. For example, [Service](../subjects/service.md#ui-planned) owns its summary/detail/edit behavior, [Contract](../subjects/contract.md#ui-planned) owns common Contract presentation, and [OpenApiContract](../subjects/openapi-contract.md#ui-planned), [ProtobufContract](../subjects/protobuf-contract.md#ui-planned), and [CSharpApiContract](../subjects/csharp-api-contract.md#ui-planned) own their specialized viewers. Aggregating widgets reuse those presentations without maintaining separate subject-widget specifications.

## Detailed behavior [Planned]

Widgets are reusable architecture views independent of documentation format. Path queries and optional filters select data; presentation controls can select grouping, labels, visible subject and relationship kinds, detail, and layout. The proposed ServiceDiagram, ComponentDiagram, ContextDiagram, and DeploymentDiagram widgets have distinct configurations and can render differently while sharing graph/query/renderer infrastructure. Their names do not prescribe arc42 sections or a hierarchy in the model. Each widget page provides linked input types, defaults, and copyable block examples. Exact configuration options remain under discussion. ServiceTable and DeploymentMatrix can supply domain-specific defaults over shared query and subject-presentation infrastructure.

Crosscutting pages can aggregate several services, deployment units, environments, and resources. Proposed master-detail behavior keeps the document visible while selection reveals details alongside the list, using the context slot where appropriate. Selection is local UI state and does not create a new domain subject. Links, previews, and drill-down views address the same underlying subjects.

The user wants one ordinary arc42 Markdown document with authored explanations and embedded tables, diagrams, and summaries over the shared domain. The same widgets also work in service pages, guides, ADRs, or other architecture documentation. The sample's arc42.md demonstrates all twelve headings without a special document type or one subject per section. It is a composition of reusable views, not a separate architecture model.

The selected initial direction is live Canvas-based structural widgets combining selector-driven nodes, manual subject/content additions, and saved layout. See the canonical [live Canvas composition and refresh contract](architecture-diagram.md#live-canvas-composition-planned), including the first-version restriction against deleting automatic nodes. Existing CanvasLayout supports positioned Widget references, dragging, snapping, and scaling; automatic sources, layout coordination, and semantic connectors remain planned. Portable diagram generation/export is deferred. Runtime scenario order remains authored separately from static relationships.

## Related specifications

[Overview and status legend](../overview.md) · [Platform readiness](../implementation-readiness.md)
