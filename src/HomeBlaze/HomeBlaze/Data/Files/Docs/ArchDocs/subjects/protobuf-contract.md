---
title: ProtobufContract
navTitle: ProtobufContract
status: Planned
---

# ProtobufContract [Planned]

## Class definition [Planned]

ProtobufContract derives from [Contract](contract.md) and describes protobuf definitions. It can describe a data-only schema without a deployed gRPC endpoint. The common [CodeProject](code-project.md) association does not require a Host or Interface.

## Configuration properties [Proposed schema]

| Property | Type | Required | Contract |
|---|---|---|---|
| Inherited master data | [Contract configuration](contract.md#configuration-properties-proposed-schema) | As specified by Contract | Optional `name` and `version`. |
| `path` | Local file-location `string` | Yes for an authored descriptor | Selects the initial `.proto` source file. Resolve relative locations from the defining descriptor file's directory/local origin, never the process working directory. |
| Inherited project identifier | [Contract configuration](contract.md#configuration-properties-proposed-schema) | No | Optional `codeProjectPath`; see Contract for identifier resolution. |

For example, `protobuf-contract.json` can set `path` to `orders.proto` and `codeProjectPath` to the intended CodeProject subject. `path` selects source bytes; `codeProjectPath` selects a graph subject. They use different resolvers. A relative `path` without a local file origin is invalid; require an unambiguous absolute local path or a supported source origin and retain a configuration diagnostic. Source access remains within the declared access boundary.

Automatic discovery already knows the raw `.proto` location and does not require or write a descriptor with `path`. See [Contract file discovery](../providers/contract-discovery.md) for association and explicit-definition precedence. Binary descriptor input is not part of this initial proposed `path` contract.

## State properties [Proposed schema]

| State | Type | Meaning |
|---|---|---|
| Inherited own state | [Contract state](contract.md#state-properties-proposed-schema) | Source/provenance and diagnostics. |
| `surface` | Optional structured protobuf data | Supported services, RPCs, messages, fields, enums, and import information. Exact member name, data types, and public ABI remain proposed. |
| Inherited reference state | [Contract state](contract.md#state-properties-proposed-schema) | Resolved CodeProject and referring subjects. |

Imports require explicit source scope and diagnostics. Missing imports must not produce a falsely complete parsed surface. Raw source remains accessible when imports or parsing fail.

## Implementation readiness

**Delivery:** Can be implemented through existing subject and component extension points. The ArchDocs feature, proposed fields, file discovery, and protobuf parsing are not implemented.

**Prerequisites:** Contract, local file origins, project reference resolution, and optional protobuf provider/presentation.

## Scope and open choices [Proposed]

The initial file input is `.proto` source. Import roots, supported language revisions, parsed model shape, parser dependency, and any later descriptor-input mode remain open. Running a compiler or obtaining external imports is not authorized merely by selecting a source file. Source/revision identity and comparison remain separate choices.

## UI [Planned]

The specialized widget/detail view extends the [Contract presentation](contract.md#ui-planned) with supported services, RPCs, messages, fields, enums, and import diagnostics. A data-only contract remains useful without a deployed gRPC endpoint.

Retain raw source access and generic presentation when structured parsing is unavailable. Parsed definitions are provider state; source selection and explicit project association use the appropriate defining file's writer.

## Usage examples

[Contract files and discovery](../scenarios/contract-files.md)

## Related specifications

[Protobuf provider](../providers/protobuf-provider.md) · [Overview and status legend](../overview.md) · [Platform readiness](../implementation-readiness.md)
