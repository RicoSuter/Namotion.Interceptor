---
title: Markdown Authoring Variants
navTitle: Markdown Authoring Variants
status: Planned
---

# Markdown Authoring Variants [Planned]

These are alternative ways to author the same logical Service. The document stays a MarkdownFile in all cases. Source repositories, deployment units, and environments can still live in separate JSON files as in the [JSON catalog](json-catalog.md).

## A page defining a child in frontmatter [Planned]

Suppose docs is mounted at the graph root and the page is services/Orders.md. The child route is /services/Orders.md/service. A Service is defined independently of whether its widget appears in the body.

````markdown
---
title: Orders
$service:
  $type: Namotion.ArchDocs.Service
  name: Orders
  componentSources:
    - path: /Repository/hosts/Orders.*
metadata:
  $subjects:
    service:
      relationships:
        - kind: calls
          target: /catalog/Payments
          technology: HTTPS
---

# Orders

Orders accepts and processes customer orders.

:: HomeBlaze.Components.Widget Path=service
::
````

Payments is assumed to be a separately defined Service. The `$service` entry declares the child, while `metadata.$subjects.service` enriches it using the same inline name as `Path=service`. The relationship belongs to the Service child rather than the page. Inline child paths already exist; generic frontmatter children and metadata persistence require HomeBlaze changes. Optional context contributions can expose related information even if the explicit Widget block is omitted, once the page-contribution extension exists.

## A named body block [Implemented entry point] / [Planned subject]

Registered configurable subjects can already be created by body blocks. After Service and its widget are implemented, the following is an alternative declaration. Body-block configuration uses C# property casing; the exact frontmatter naming contract is still proposed independently.

````markdown
# Orders

:: Namotion.ArchDocs.Service#service
```json
{
  "Name": "Orders",
  "ComponentSources": [
    { "Path": "/Repository/hosts/Orders.*" }
  ]
}
```
::
````

Use either the frontmatter declaration or this block for the child named service. Defining both with the same name is a collision, not a way to reference the existing child.

## A page that only describes a standalone subject [Planned]

Keep Orders in catalog/Orders.json. The page has no subject declaration and can remain ordinary prose with a regular Markdown link. The sample target page is /catalog/Orders, while the page itself remains independently addressable.

````markdown
---
title: Order processing
metadata:
  relationships:
    - kind: documents
      target: /catalog/Orders
---

# Order processing

See the [architecture overview](../architecture.md) for the complete catalog.
````

This metadata does not create a second Orders Service. Frontmatter metadata persistence and automatic related-subject context need host work. A purely textual document works today; an explicit Widget referencing /catalog/Orders is an independent future ArchDocs option through the existing block mechanism.

## Moving between layouts

Extracting a frontmatter definition into catalog/Orders.json uses the same Service schema. Move its enrichment to the JSON sidecar and update consumers from /services/Orders.md/service to /catalog/Orders. No automatic path-preserving relocation is promised. The original page can then reference the standalone Service through metadata.

[Authoring contract](../integration/markdown-authoring.md) · [Readiness](../implementation-readiness.md) · [Examples](overview.md)
