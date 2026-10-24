---
title: Usage Examples and Scenarios
navTitle: Usage Examples and Scenarios
status: Planned
---

# Usage Examples and Scenarios [Planned]

The examples here are part of the feature reference. They are ordinary Markdown with fenced JSON, YAML, C#, and Markdown source, so reading this documentation does not instantiate the proposed ArchDocs subjects or execute analyzers. Core types, exact field names, generated route aliases, and widgets remain [Planned] or [Proposed] as their element pages specify. These examples express the intended authoring experience, not a final supported schema.

Every final example must satisfy [file authoring and readability](../file-authoring.md): it must make types, fields, source ownership, and reference resolution understandable to people and agents in a normal editor. Provisional type names, scoped metadata keys, and incomplete Contract skeletons are explicit schema work still to settle; a UI cannot be required to fill in their meaning.

## Choose a documentation style

| Scenario | Documentation arrangement | Planned prerequisites |
|---|---|---|
| [JSON catalog](json-catalog.md) | Architecture pages reference separately authored subject JSON files | Domain types, project analyzer, queries, explicit widgets; existing JSON/widget entry points |
| [Markdown definitions](markdown-authoring.md) | A page defines children in frontmatter, uses an inline block, or only references another subject | Frontmatter declarations/metadata need host changes; body-block subject loading already exists |
| [One arc42 document](arc42.md) | One normal document aggregates graph views between authored sections | Same generic views as other documents; no arc42-specific subject |
| [Topics, guides, and decisions](topics-and-decisions.md) | Crosscutting pages reference Services/units/resources stored elsewhere | Same queries and widgets; automatic context panes are optional additional host work |
| [Library public APIs](library-contracts.md) | Library documentation links CodeProject public API Contracts | Contract subjects; optional public API extraction; no Host or deployed Endpoint required |
| [Multiple repositories and shared resources](repositories-and-shared-components.md) | Logical architecture crosses source and folder boundaries | Multiple SourceRepositories, explicit path queries, shared identities, metadata relationships |

These are alternative and composable layouts, not a required folder hierarchy. Begin with the JSON catalog for a complete minimal set of proposed subject definitions. Other pages change the authoring layout while keeping the same semantics. The reference does not require a mandatory Project or ArchitectureModel root.

## Path conventions

Each example states its documentation mount and graph paths. Source file locations resolve relative to their defining file. Subject paths resolve in the graph. JSON filenames lose .json in subject routes; Markdown pages keep .md, with named child routes appended. Generated collection/key aliases are illustrative until the corresponding schemas are finalized. Moving a definition requires updating references to its canonical path.

## Existing reference cases

[Reference cases](reference-cases.md) records how AppHost and Apimundo exercise the generic model. The separate [Commerce development fixture](../../../ArchDocs/README.md) contains editable files and buildable discovery inputs for a two-service arrangement. It is optional test material, not required reading for the feature contracts and not part of published seed packaging. The examples on these pages are included with the feature documentation.

[Feature overview](../overview.md) · [Readiness](../implementation-readiness.md) · [Verification scenarios](../verification-scenarios.md)
