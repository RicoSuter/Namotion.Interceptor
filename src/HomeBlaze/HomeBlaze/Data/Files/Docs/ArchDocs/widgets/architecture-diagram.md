---
title: Architecture Diagrams
navTitle: Architecture Diagrams
status: Planned
---

# Architecture Diagrams [Planned]

Render service, component, context, dependency, scenario, or deployment views without fixing a documentation format.

## Widget entry points [Proposed]

Use separate author-facing widgets with configurations appropriate to each view. Share graph queries, relationship readers, node summaries, navigation, and renderer infrastructure underneath them. Distinct entry points allow different boxes, labels, grouping, and layout behavior without a growing `View` switch that exposes irrelevant options to each author.

| Widget | Primary data | Configuration reference |
|---|---|---|
| [ServiceDiagram](service-diagram.md) | [Services](../subjects/service.md) and interactions between them | Service scope and optional source queries |
| [ComponentDiagram](component-diagram.md) | A Service's [Components](../subjects/component.md), such as [Hosts](../subjects/host.md) and [Resources](../subjects/resource.md) | Service reference and optional component queries |
| [ContextDiagram](context-diagram.md) | Internal/external Services and optional [Actors](../subjects/actor.md) | Explicit boundary selections per view |
| [DeploymentDiagram](deployment-diagram.md) | [Environments](../subjects/environment.md), [Deployments](../subjects/deployment.md), and [DeploymentUnits](../subjects/deployment-unit.md) | Environment selections and available bindings |
| [DependencyDiagram](dependency-diagram.md) | Discovered [CodeProject](../subjects/code-project.md) references, optionally grouped by Service or explicit layer/module queries | Project scope and optional group definitions |
| [ScenarioDiagram](scenario-diagram.md) | Authored ordered interactions referring to shared graph participants | Explicit participant references and steps |

These are proposed names and schemas, all unimplemented. This page owns common graph presentation behavior, not a second public configuration schema or a domain subject. The selected direction for structural diagrams is live widgets reusing the existing Canvas rendering infrastructure and subject widgets. Exact shared rendering APIs and layout algorithms remain open. Sequence rendering can have different needs from structural Canvas views. [ImpactView](impact-view.md) can use a graph, table, or list, and [architecture-check findings](architecture-check-results.md) can annotate compatible diagrams without becoming another independent architecture model.

## Implementation readiness

**Delivery:** Planned ArchDocs widgets using existing component registration and explicit Markdown embeds, plus generic Canvas additions or shared rendering APIs where required. Current Canvas placement does not implement automatic node sources, architecture connectors, or generated layout. No page-context slots are required for explicit embedding. The ArchDocs feature itself is not implemented.

**Prerequisites:** Shared scope queries, component/exposure/provision relationships, relationship readers, reusable Canvas rendering, node identity and layout persistence, and connector support.

## Behavior [Planned]

Select graph data independently of display settings. Context views choose their internal/external sets per diagram. Component views can show Hosts, Resources, and nested Services. Preserve relationship kinds, unknown endpoints, overlapping membership, and provenance; aggregation must not fabricate runtime order.

Manual layout belongs to the document/view; semantic connections belong to shared metadata. CanvasLayout placement and referenced subject widgets already exist [Implemented], but connector lines and generated architecture diagrams are [Planned]. Reuse that infrastructure for the live structural views. Exact grouping, layout, and connector-editing APIs remain open.

## Live Canvas composition [Planned]

The user selected a combination of automatic nodes and authored additions. A generic Canvas can select Services through a path/filter and also show manually added Resource references, notes, images, legends, or other widgets. ArchDocs supplies domain queries, relationship semantics, and suitable subject summaries; generic Canvas code keeps those domain concepts outside its own API.

| Part | Ownership and behavior |
|---|---|
| Automatic nodes | Resolved from configured selectors against the loaded subject graph. Diagram membership updates when those results change. |
| Manual subject nodes | Explicit references to existing subjects. Adding a Resource to the diagram does not change Service membership or deployment participation. |
| Manual content | Authored presentation such as notes, legends, images, groups, and other widgets. It remains part of the view across source refreshes. |
| Layout | Authored position and size overrides for automatic or manual nodes, associated with stable node/subject identity and readable references. Exact serialized keys remain open. |
| Connections | Supported model relationships retain kind, endpoints, and provenance. Decorative lines and annotations remain view data. |

Specialized views retain their documented participant-selection and boundary contracts. A manually added Resource remains a Resource; a note or arbitrary widget is not implicitly a Service, context participant, or deployment. Exact manual-addition fields and any view-specific eligibility rules still need schemas. Proposed default: an explicit reference to an already selected subject reuses its subject node. Automatic-node identity must not depend on enumeration order or display title; independently authored content nodes retain their own identity.

Existing nodes keep saved manual positions when automatic selection refreshes. New automatic nodes receive an initial position without rearranging manually positioned nodes. Manual additions remain. Positioning an automatic node does not turn it into an explicit manual selection; a subject that no longer matches its selector leaves the automatic set. A failed repository pass retains its last successful graph under the existing reconciliation contract. Placement algorithms, collision handling, and retention of unused layout overrides remain implementation choices.

**First-version constraint:** automatic nodes have no delete or hide action in the UI. Change the selector to change that set. Do not introduce per-node exclusion state as a side effect of a delete gesture. Removing a manual diagram node removes its view entry, not the referenced subject.

Creating a Resource from diagram UI must save a real subject definition through a selected canonical JSON/Markdown owner, then add a reference to it. The diagram is not a second architecture store. Editing a typed semantic connection uses the canonical relationship writer; adding a decorative connection changes view configuration only. Connector editing remains planned work, not an existing Canvas feature.

## Refresh and writeback [Planned]

1. Opening the document resolves the configured diagram widget and its selectors against the loaded graph. Required discovery may still be loading; expose loading, unavailable inputs, and any retained last-successful facts clearly.
2. Repository discovery and reconciliation run independently of widget rendering. When they publish changed subjects or facts, affected selections, nodes, relationships, and summaries update.
3. Relevant selector, metadata, relationship, and subject-presentation changes update their corresponding view data. Layout changes update placement. Repeated renders and reopening the page retain saved positions.
4. Explicit UI edits save the affected view configuration through its normal owning-file writer. Preserve unrelated prose, configuration, metadata, manual content, and other node positions.

Rendering a widget must not start a repository scan, refresh a Provider, write Markdown, or regenerate an export. Repository refresh remains a separate operation or configured background behavior. Keep graph querying and reconciliation separate from drawing so display updates do not reset layout or recreate unrelated subjects. Exact tracking, scheduling, and readiness APIs remain implementation choices.

## Markdown and portable output [Planned]

The selected initial authoring surface is ordinary explicit Markdown widget blocks using the registered view types. Existing manually authored Mermaid fences continue to serve explanatory diagrams independently. Prose, links, and ordinary diagrams retain their usual external-reader meaning; live widgets require HomeBlaze or a separately supported renderer.

Portable diagram publishing is deferred. Generated Mermaid sections, SVG snapshots, image-to-live-diagram replacement, and new export/generation commands are not prerequisites for the initial live widgets. No output format, automatic regeneration policy, or generated-Markdown writeback scheme is selected. A future explicit export can consume the same resolved subjects, relationships, and view layout; validation remains read-only. This chooses the diagram direction without approving a complete MVP or product implementation.

## Runtime and deployment limits [Planned]

Runtime sequences need authored scenarios; the proposed ScenarioDiagram supplies linked participants and explicit steps rather than inferring order from dependencies. Complete deployment views need environment bindings and infrastructure mappings. Ordinary arc42 headings do not add missing graph facts. Actors are optional until their model is selected.

## Context boundaries [Planned]

The user confirmed that internal/external is relative to the diagram's documented system boundary, including neighboring systems operated by the same organization. The author chooses what is internal and external for each context diagram. Payments can be internal in the Commerce context and external in an Orders context without changing the Service subject. Organizational ownership is a separate concern and is not requested as a substitute for this boundary classification.

The proposed [ContextDiagram configuration](context-diagram.md#configuration-properties-proposed-schema) has InternalSources and ExternalSources lists using the shared PathQuery record, with resolved internal and external sets as view state. Exact field spellings and rendering details remain open. The sample demonstrates both contexts using the same Orders and Payments subjects. Its block configuration uses PascalCase keys, as existing Markdown configuration fences do; persisted subject/frontmatter configuration follows its own naming convention.

Proposed defaults: explicit lists control the visible participants and relationships between them; diagnose an identity selected into both sets instead of cloning it or silently choosing a side. Optional automatic discovery of neighboring participants can be considered separately. Boundary membership and layout are view configuration, while semantic relationships stay in the shared model. No global internal/external flag or separate external-Service subtype is needed. Actors and their representation remain open; no mandatory System root has been selected.

## Related specifications

[Overview and status legend](../overview.md) · [Platform readiness](../implementation-readiness.md)
