---
title: Verification Scenarios
navTitle: Verification Scenarios
status: Planned
---

# Verification Scenarios [Planned]

These scenarios capture intended behavior for the evolving capability. They are not a decision to include every scenario in the first implementation; select that boundary after reviewing the working sample.

1. Mount only a repository's documentation folder, configure a source root relative to a JSON file there, and analyze the surrounding checkout without mounting every source file as a subject.
2. Configure two repositories; discover their project-level elements without duplicating shared elements within one repository's graph.
3. Define a service using path-query entries selecting its repository-owned components, and query library projects matching `*.Test` through paths and filters without introducing named-selection entities.
4. Change the source or selectors and refresh; preserve unchanged generated subjects and authored enrichment while updating membership and project-reference edges.
5. Show an external code dependency without adding its target to the service's membership or asserting runtime communication.
6. Define a deployment unit selecting hosts from more than one service, and an environment selecting that unit; obtain exactly one generated Deployment for the pair.
7. Edit a deployment's manual version and URLs through the existing metadata store with domain-specific editing support; retain them across reconciliation and restart.
8. Remove or change an environment's unit selection while the unit stays attached elsewhere; update generated Deployments correctly.
9. Surface malformed selectors, unresolved references, uncertain analysis, and failed source access distinctly; retain the last successful graph on temporary analysis failure.
10. Detach an owner during queued work; prevent obsolete publication and release its subscriptions and generated children according to the normal lifecycle rules.
11. Embed a service summary and deployment matrix in Markdown using normal widget blocks; navigate from their entries to the existing subjects without maintaining duplicate service facts in the page.
12. Define independent Service and Environment children through `$service` and `$environment` in ordinary Markdown frontmatter; instantiate their nested `$type` declarations without a specialized page type, retain stable named identities through refresh, edit configuration through the owning file, and embed an optional summary without recursive rendering. Diagnose collisions with named inline blocks rather than silently choose a definition.
13. Edit page and child-scoped metadata and authored relationships through the metadata API; preserve the body and unrelated frontmatter, keep document and service relationship sources distinct, reapply descendant enrichment on later attach, and retain that data across refresh and restart.
14. Open an ordinary Markdown document with relationship metadata and no blocks or `$type`; expose applicable subject information through page UI slots while leaving its source and normal Markdown rendering intact.
15. Follow an ordinary Markdown link to a resolvable subject; offer a rich preview with title, subtitle, description, applicable facts, and navigation while retaining standard Markdown source and normal link navigation.
16. Explore service membership, dependencies, and deployments through the context slot and summary, and navigate from deployment-matrix entries to their units, environments, and deployments. Surface unavailable or uncertain facts explicitly.
17. Author the same Service type using standalone JSON or a Markdown child definition with the same configuration schema; after manually moving a definition, its enrichment, and updating references, keep the original document as documentation of the standalone subject.
18. Place a ServiceTable in an overview document, scoped to a project subtree; find nested services defined through JSON, frontmatter, and inline blocks, show each once with useful summaries and navigation, and update rows when subjects enter or leave the scope without rerunning source analysis.
19. Include service-specific and shared Resources through component-source paths; retain one shared resource identity and show typed API, database, and messaging relationships from services or explicit hosts.
20. Use the same widget configuration in an ordinary service page and an arc42 document; render a service overview and a detailed host/resource view from the same graph, with explicit scope and preserved relationship endpoints.
21. Place referenced service widgets on a manual canvas; keep layout local to that document and semantic connections in shared relationship metadata. Existing canvas limitations remain until connector support is implemented.
22. Open a JSON-defined Resource or DeploymentUnit through useful detail presentation and reuse its compact presentation in previews, context panels, and diagram boxes. This is the proposed page direction, subject to the open presentation-interface decision.
23. Display Payments inside the Commerce context and outside the Orders-only context using explicit per-view path sources; retain the same Service identity and relationships without writing an internal/external property to it.
24. Reference a Contract from a library CodeProject to describe its public API without creating a Host, network Interface, or Endpoint; distinguish authored content from analyzer-derived snapshots.
25. Expose an Interface from an appropriate subject, reference its Contract, and bind it to different Endpoints in different environments while retaining the logical Interface identity.
26. Select a Resource definition/configuration as an explicit release participant alongside Hosts; keep dependency-only references separate and do not imply provisioning or observed installation.
27. Use only the reference and a normal file editor to understand and change types, relationships, and memberships; follow references to authored definitions or documented discovery rules without a running UI.
28. Compare equivalent file and UI edits: obtain the same effective configuration, preserve unrelated content and metadata, and produce a focused readable diff.
29. Inspect malformed or unsupported configuration with readable source-located diagnostics and retain the original input for correction; ordinary documentation remains useful outside the widget renderer.
30. Discover a CodeSolution whose projects are already repository-owned; retain the same CodeProject identities when several solutions reference them, when a project belongs to no solution, or when a solution is removed. Do not infer Service or deployment membership.
31. Define OpenApiContract sources through `.json`, `.yaml`, and `.yml` paths and a ProtobufContract through a `.proto` path; resolve each relative to its defining descriptor or Markdown origin independently of the process working directory.
32. Discover raw Contracts without descriptors and associate only a unique same-directory CodeProject. Keep absent or ambiguous associations visible and unassociated until an explicit `codeProjectPath` resolves them; never search parents or guess from a Host.
33. Load an explicit Contract definition for an already discovered raw source; prefer the authored definition, diagnose conflicts, and preserve references/enrichment according to the selected identity-transition policy rather than silently publish duplicates.
34. Define `$service`, scope its metadata through `metadata.$subjects.service`, and reference it through `Path=service`. Keep the inline name on write, preserve unrelated entries, and diagnose declaration/property collisions. Expanded-key compatibility, if supported, needs owner-aware resolution rather than syntactic normalization.
35. Use specialized ServiceDiagram, ComponentDiagram, ContextDiagram, and DeploymentDiagram configurations against the same graph. Preserve identity and relationship provenance while allowing view-specific rendering, and keep an explicit empty source list distinct from omitted scoped defaults.
36. Author an Identifier as plain text without a corresponding subject, as a path to an ordinary Markdown page or generic subject, and as a path to the expected domain type. Preserve the identifier in each case, progressively enable links/rich UI, and keep typed graph behavior unavailable until a compatible subject resolves. An unavailable target/module must not change path meaning or discard the authored value. Select syntax, escaping, and equality rules separately.
37. Configure two SourceRepositories to select the same published singleton Analyzer. Resolve the same instance without cloning or transferring ownership, run against distinct source boundaries, and retain independent results, diagnostics, and cancellation. Deselecting or detaching one repository does not dispose the shared analyzer or disrupt the other repository's run.
38. Change analyzer selection/configuration or remove/replace a selected analyzer during queued work. Re-evaluate the current compatible inputs, diagnose unavailable selections, retain the last successful graph on failure, and prevent obsolete runs from publishing. Merely registering an analyzer must not execute it or silently select it everywhere.

These are planned verification scenarios, not existing passing tests. Fixture projects and example configuration do not implement ArchDocs. Choose the MVP and delivery order separately; retain the applicable invariants when each capability is implemented.

## Documentation independence [Planned]

The feature overview, element specifications, scope decisions, and usage examples must explain the feature without any session notes. Each example states its assumed graph root and identifies unimplemented behavior or provisional field names. Reusing a view in another documentation style does not create duplicate domain subjects. Exact-path references, wildcard queries, frontmatter authoring, and automatic context contributions retain their distinct prerequisites.

The [file-authoring acceptance checks](file-authoring.md#acceptance-checks-planned) apply in addition to these feature scenarios. Readability and source editing are product requirements, not only documentation-writing preferences.

[Overview](overview.md) · [Scope decisions](scope-and-decisions.md) · [Scenarios](scenarios/overview.md)
