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

The proposed definition in contracts/PublicApi.json selects the specialization and its project explicitly:

```json
{
  "$type": "Namotion.ArchDocs.CSharpApiContract",
  "name": "Shared contracts public API",
  "codeProjectPath": "/Repository/projects/Shared.Contracts"
}
```

This defines the Contract and its association, not a complete extraction configuration. Source/snapshot formats, version identity, and extraction inputs remain open in the Contract specifications. No surface is invented before a supported extractor or supplied snapshot exists.

The explicit `codeProjectPath` resolves to the discovered library CodeProject. Its read-only `contracts` list refers back to this same subject. No editable project-side `hasContract` relationship duplicates the association. Generated route aliases remain proposed and must be documented and readable under the [file-authoring requirement](../file-authoring.md). OpenAPI/protobuf file inputs have separate [complete descriptor examples](contract-files.md).

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
