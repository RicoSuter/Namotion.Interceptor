---
title: Library Public API Documentation
navTitle: Library Public API Documentation
status: Planned
---

# Library Public API Documentation [Planned]

A library can be documented without a runnable process, environment, or network endpoint. It is a CodeProject with one or more Contract references. Optional Artifact subjects can later describe released packages independently of the source project.

## Proposed layout

```text
repository/
  src/Shared.Contracts/Shared.Contracts.csproj
  src/Shared.Contracts/Order.cs
  docs/
    Repository.json
    Repository.metadata.json
    contracts/PublicApi.json
    libraries/shared-contracts.md
```

Repository.json uses the parent source root as in the JSON catalog. The project analyzer discovers Shared.Contracts as a library CodeProject and does not create a Host merely because the project has a public API.

## Source surface

A public source surface might include:

```csharp
public sealed record Order(Guid Id, decimal Total);
```

The public API Contract can describe that type and its members. A source symbol extractor or compiled-metadata extractor is a separate analyzer choice; the simple csproj XML scan cannot infer the surface.

## Contract identity [Planned] / [Proposed schema]

The proposed type discriminator in contracts/PublicApi.json selects the specialization:

```json
{
  "$type": "Namotion.ArchDocs.CSharpApiContract"
}
```

This is a type-selection skeleton, not a complete extraction configuration. Source/snapshot fields, version identity, and parser inputs remain open in the Contract specifications. It can later point to supplied content or analyzer-produced data without introducing network semantics.

An illustrative Repository.metadata.json entry links the discovered project to that Contract using the planned typed relationship reader over the existing metadata envelope:

```json
{
  "$subjects": {
    "Projects[Shared.Contracts]": {
      "relationships": [
        { "kind": "hasContract", "target": "/contracts/PublicApi" }
      ]
    }
  }
}
```

The Projects collection name and generated route aliases remain schema proposals. hasContract illustrates an extensible relationship kind; it is not an alternative mandatory relationship entity or an independently editable duplicate of a capability property.

In this example, Projects[Shared.Contracts] means the generated Shared.Contracts CodeProject owned by Repository, whose illustrative graph route is /Repository/projects/Shared.Contracts. It scopes enrichment to that existing subject; it does not define another project. The [file-authoring requirement](../file-authoring.md) requires this mapping to be understandable in text and calls for reviewing whether the final metadata/reference syntax can be simpler.

## Documentation view [Planned]

The library page can reference the CodeProject and Contract in prose or metadata and explicitly embed the Contract's registered widget:

````markdown
# Shared contracts library

Explain the library's purpose, supported use, and compatibility policy here.

:: HomeBlaze.Components.Widget Path=/contracts/PublicApi
::
````

Before a specialized viewer exists, the common Contract presentation can show available source/version/provenance information. Comparison and compatibility assessment are separate features. The same authoring pattern can reference OpenApiContract or ProtobufContract where appropriate without changing the generic CodeProject-to-Contract relationship.

[CodeProject](../subjects/code-project.md) · [Contract](../subjects/contract.md) · [CSharpApiContract](../subjects/csharp-api-contract.md) · [Examples](overview.md)
