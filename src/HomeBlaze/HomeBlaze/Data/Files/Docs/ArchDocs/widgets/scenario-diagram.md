---
title: ScenarioDiagram
navTitle: ScenarioDiagram
status: Planned
---

# ScenarioDiagram [Planned]

Describe selected interactions as an ordered sequence referencing the same Services, Hosts, Resources, Interfaces, and Actors used elsewhere. This supports runtime sections in arc42 and use-case, integration, or operations documents in other styles. Static dependencies alone never supply runtime order.

## Configuration properties [Proposed schema]

| Property | Type | Default and meaning |
|---|---|---|
| `Path` | Subject/folder path `string` | Required graph scope for participant references. |
| `Steps` | Ordered step records | Each step supplies `From`, `To`, `Label`, and optional interaction `Kind`. Sequence is authored view data. Exact branch, repetition, and failure-path schema remains open. |

## Markdown example [Proposed]

````markdown
:: Namotion.ArchDocs.ScenarioDiagram Path=/catalog
```json
{
  "Steps": [
    {
      "From": "/catalog/Orders",
      "To": "/catalog/Payments",
      "Label": "Authorize payment",
      "Kind": "request"
    },
    {
      "From": "/catalog/Payments",
      "To": "/catalog/Orders",
      "Label": "Authorization result",
      "Kind": "response"
    }
  ]
}
```
::
````

## Behavior and limits [Planned]

Participant references provide current titles, rich summaries, navigation, and unresolved-reference diagnostics. Their identities are shared with structural views. Labels and order belong to the authored scenario; a label does not establish that a supported analyzer observed that behavior. Editing a scenario does not automatically persist static relationship metadata or duplicate a relationship on both endpoints.

A future scenario can link steps to known semantic relationships or trace evidence. Trace ingestion and observed sequence generation require separate adapters and are deferred. Presentation must distinguish authored examples from observed executions. A separate Scenario subject, durable scenario reuse outside a view, and reusable branch/loop modeling remain open; this widget does not add a settled subject to the domain inventory.

Ordinary Markdown lists and manually authored Mermaid sequences remain useful without this feature. The proposed widget adds linked participants and validation, with a renderer and supported notation to be selected.

## Implementation readiness

**Area:** ArchDocs.

**Delivery:** Planned extension through existing component registration and explicit Markdown embedding.

**Prerequisites:** Participant/reference resolution, shared summaries, configuration validation, and sequence rendering. Source analyzers or runtime monitoring are not required for an authored scenario.

[Architecture diagrams](architecture-diagram.md) · [One arc42 document](../scenarios/arc42.md) · [General widgets](overview.md)
