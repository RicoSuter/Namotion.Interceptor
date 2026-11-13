---
title: One arc42 Document
navTitle: One arc42 Document
status: Planned
---

# One arc42 Document [Planned]

Use one ordinary Markdown document for an architecture overview. It can link detailed concept pages and decisions while aggregating catalog subjects through generic widgets. The headings are document structure, not domain entities. Other architecture templates can use the same views.

Assume the [JSON catalog](json-catalog.md) defines Orders and that catalog/Payments.json defines Payments. Both are logical Services. This example changes only the document body; it does not redefine them. Proposed views remain [Planned].

````markdown
# Architecture

## 1. Introduction and goals

Explain the business purpose and quality goals here.

## 2. Constraints

Record the constraints that matter to this system.

## 3. Context and scope

This document treats Orders as internal and Payments as a neighboring system.

:: Namotion.ArchDocs.ContextDiagram#context Path=/catalog
```json
{
  "InternalSources": [{ "Path": "/catalog/Orders" }],
  "ExternalSources": [{ "Path": "/catalog/Payments" }]
}
```
::

## 4. Solution strategy

Explain the architectural choices and their trade-offs.

## 5. Building block view

:: Namotion.ArchDocs.ServiceTable Path=/catalog
::

:: Namotion.ArchDocs.ComponentDiagram#components Path=/catalog/Orders
::

:: Namotion.ArchDocs.DependencyDiagram Path=/Repository
::

## 6. Runtime view

Describe selected scenarios and their order explicitly. A dependency graph does not establish runtime ordering.

:: Namotion.ArchDocs.ScenarioDiagram Path=/catalog
```json
{
  "Steps": [
    { "From": "/catalog/Orders", "To": "/catalog/Payments", "Label": "Authorize payment", "Kind": "request" },
    { "From": "/catalog/Payments", "To": "/catalog/Orders", "Label": "Authorization result", "Kind": "response" }
  ]
}
```
::

## 7. Deployment view

:: Namotion.ArchDocs.DeploymentMatrix Path=/
::

## 8. Crosscutting concepts

Link messaging, persistence, security, and other concept documents here.

## 9. Architecture decisions

Link the relevant decision records.

## 10. Quality requirements

Describe measurable scenarios and expectations.

:: Namotion.ArchDocs.ArchitectureCheckResults Path=/
::

## 11. Risks and technical debt

Record known risks and unresolved design decisions.

## 12. Glossary

Define the important terms used by this documentation.
````

Another context view can put both Orders and Payments inside its boundary without changing either Service. Diagrams aggregate only the supplied graph facts. Complete deployment topology requires concrete bindings; runtime sequences need authored scenarios. The [validation scenario](architecture-validation.md) adds rules and guide links that make the result widget useful and explains the same checks through a CLI. Keep these facts and explanations visible rather than inventing a complete architecture from incomplete inputs.

[Architecture diagrams](../widgets/architecture-diagram.md) · [Widget reference](../widgets/overview.md) · [Scenarios](overview.md)
