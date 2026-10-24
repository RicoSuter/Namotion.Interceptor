---
title: OpenApiContract
navTitle: OpenApiContract
status: Planned
---

# OpenApiContract [Planned]

## Class definition [Planned]

OpenApiContract derives from [Contract](contract.md) and describes an OpenAPI public API document. It retains the common identity, association, provenance, and optional version semantics and can be referenced by an [Interface](interface.md) or [CodeProject](code-project.md).

## Configuration properties [Proposed schema]

| Property | Type | Required | Contract |
|---|---|---|---|
| Inherited master data | [Contract configuration](contract.md#configuration-properties-proposed-schema) | As specified by Contract | Optional `name` and `version`. |
| `path` | Local file-location `string` | Yes for an authored descriptor | Selects an OpenAPI `.json`, `.yaml`, or `.yml` file. Resolve relative locations from the defining descriptor file's directory/local origin, never the process working directory. |
| Inherited project identifier | [Contract configuration](contract.md#configuration-properties-proposed-schema) | No | Optional `codeProjectPath`; see Contract for identifier resolution. |

For example, `openapi-contract.json` can set `path` to `openapi.json` and `codeProjectPath` to the intended CodeProject subject. `path` selects source bytes; `codeProjectPath` selects a graph subject. They use different resolvers. A relative `path` without a local file origin is invalid; require an unambiguous absolute local path or a supported source origin and retain a configuration diagnostic. Source access remains within the declared access boundary.

Automatic discovery already knows the raw document's file location and does not require or write a descriptor with `path`. See [Contract file discovery](../analyzers/contract-discovery.md) for recognized raw filenames, project association, and explicit-definition precedence.

## State properties [Proposed schema]

| State | Type | Meaning |
|---|---|---|
| Inherited own state | [Contract state](contract.md#state-properties-proposed-schema) | Source/provenance and diagnostics. |
| `surface` | Optional structured OpenAPI data | Operations, parameters, requests/responses, and schemas when a selected parser supports them. Exact member name, data types, and public ABI remain proposed. |
| Inherited reference state | [Contract state](contract.md#state-properties-proposed-schema) | Resolved CodeProject and referring subjects. |

Raw document access is useful before parsing exists and after a parse error. An unsupported document must not appear as a successfully parsed empty API.

## Implementation readiness

**Delivery:** Can be implemented through existing subject and component extension points. The ArchDocs feature, proposed fields, file discovery, and OpenAPI parsing are not implemented.

**Prerequisites:** Contract, local file origins, project reference resolution, and optional OpenAPI analyzer/presentation.

## Scope and open choices [Proposed]

Supported OpenAPI revisions, external-reference policy, parsed model shape, parser dependency, source/revision identity, and comparison remain unselected. Recognizing a `.json`, `.yaml`, or `.yml` input does not guarantee support for its specification revision or references. No network polling or endpoint probing is implied.

## UI [Planned]

The specialized widget/detail view extends the [Contract presentation](contract.md#ui-planned) with supported operations, parameters, request/response details, and schemas. Offer raw source access, project association provenance, and parser/configuration diagnostics, retaining the common summary when parsing is unavailable.

Operation/schema navigation belongs to this subject. Supported revisions, parsing, comparison, and compatibility analysis remain separate decisions.

## Usage examples

[Contract files and discovery](../scenarios/contract-files.md)

## Related specifications

[OpenAPI analyzer](../analyzers/openapi-analyzer.md) · [Overview and status legend](../overview.md) · [Platform readiness](../implementation-readiness.md)
