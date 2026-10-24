---
title: Contract Files and Discovery
navTitle: Contract Files and Discovery
status: Planned
---

# Contract Files and Discovery [Planned]

Keep the raw API document separate from an optional authored Contract definition. All ArchDocs types, fields, and discovery below are planned or proposed. Raw documents can be read in a regular editor without a Contract viewer.

## Assumed source and graph scope

Mount `repository/docs` as the documentation graph root. `Repository.json` defines a [SourceRepository](../subjects/source-repository.md) with `sourceRoot: ../`. Its illustrative project route is `/Repository/projects/Orders.Api`; exact generated aliases remain proposed.

```text
repository/
  src/Orders.Api/
    Orders.Api.csproj
    openapi.json
    orders.proto
  docs/
    Repository.json
    contracts/openapi-contract.json
    contracts/protobuf-contract.json
```

Paths to raw source files resolve relative to the defining descriptor. `codeProjectPath` resolves in the subject graph. It never means the filesystem path of a `.csproj`.

## OpenAPI source and explicit descriptor

An example `src/Orders.Api/openapi.json`:

```json
{
  "openapi": "3.0.3",
  "info": { "title": "Orders API", "version": "1.0.0" },
  "paths": {
    "/orders": {
      "get": {
        "responses": { "200": { "description": "Order list" } }
      }
    }
  }
}
```

The optional `docs/contracts/openapi-contract.json` configures a subject, with both references explicit:

```json
{
  "$type": "Namotion.ArchDocs.OpenApiContract",
  "name": "Orders API",
  "path": "../../src/Orders.Api/openapi.json",
  "codeProjectPath": "/Repository/projects/Orders.Api"
}
```

An adjacent descriptor uses `"path": "openapi.json"` instead. That adjacent descriptor must be loaded as a subject if it is intended to author one; the repository scanner does not mount every source file into the documentation tree. No descriptor is necessary for an unambiguous automatically discovered source.

The same OpenAPI input can use `openapi.yaml` or `openapi.yml`, with the descriptor's `path` changed accordingly:

```yaml
openapi: 3.0.3
info:
  title: Orders API
  version: 1.0.0
paths:
  /orders:
    get:
      responses:
        '200':
          description: Order list
```

These examples select files; supported specification revisions and external references still depend on the chosen analyzer. Selecting a file does not establish a deployed Endpoint or health.

## Protobuf source and explicit descriptor

`src/Orders.Api/orders.proto` can contain a data-only schema:

```protobuf
syntax = "proto3";
package orders;

message Order {
  string id = 1;
}
```

`docs/contracts/protobuf-contract.json`:

```json
{
  "$type": "Namotion.ArchDocs.ProtobufContract",
  "name": "Order messages",
  "path": "../../src/Orders.Api/orders.proto",
  "codeProjectPath": "/Repository/projects/Orders.Api"
}
```

An adjacent descriptor uses `"path": "orders.proto"`. This input does not require a gRPC service or Endpoint. Import roots, grouping multiple files, parser support, and any later binary-descriptor mode remain separate choices.

## Without authored descriptors

If the descriptors above are absent, the proposed [discovery rule](../analyzers/contract-discovery.md) recognizes the raw files inside the repository scan scope. With exactly one CodeProject source file in `src/Orders.Api`, both Contracts associate with that same project. The repository owns the generated subjects; their names/keys and raw source remain inspectable. No JSON descriptor is written automatically.

If `openapi.json` moves to `src/Orders.Api/specs`, there is no same-directory project. Discovery keeps it unassociated and reports a diagnostic, even though a project exists in the parent directory. Add an authored descriptor with `path` selecting the moved file and `codeProjectPath` selecting its intended project. The same applies if two project files share the raw document's directory: an explicit reference resolves the ambiguity. An invalid explicit reference remains an error rather than triggering a fallback guess.

An authored definition takes precedence over automatic discovery of the same raw source. Exact deduplication keys and authored/generated identity transitions remain open; implementations must diagnose conflicts and preserve traceability rather than silently create competing copies. A CodeProject's Contract list is the inverse of this association, so no separate editable project-side `hasContract` metadata entry is needed.

[Contract](../subjects/contract.md) · [OpenApiContract](../subjects/openapi-contract.md) · [ProtobufContract](../subjects/protobuf-contract.md) · [Library APIs](library-contracts.md) · [Other layouts](overview.md)
