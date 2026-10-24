---
title: ArchDocs Commerce Sample
navTitle: ArchDocs Sample
---

# ArchDocs Commerce Working Sample

Use this folder to refine the authoring experience before finalizing the domain design, implementation plan, and MVP. It lives under the existing HomeBlaze Files root as ArchDocs, containing normal Markdown, proposed subject JSON definitions, and an internal .source directory with buildable source fixtures. The ArchDocs types, frontmatter child declarations, path queries, and architecture widgets are proposed functionality. The .NET projects are fixtures, not an implementation of that functionality.

The [working domain specification](../Docs/plans/2026-10-24-archdocs-domain-design.md) tracks the entity and field model, relationships, authoring rules, presentation contracts, and open decisions. Update it alongside these sample files when a model decision changes. The concrete sample field names remain proposals.

## Start here

Read [Architecture.md](Architecture.md) for the overview, [services/Orders/Service.md](services/Orders/Service.md) for a service defined in Markdown frontmatter, and [deployment-units/Orders.json](deployment-units/Orders.json) for a standalone subject definition. Revise these actual files while discussing the model. Type names, property names, and generated graph keys are working proposals.

[operations/Releasing.md](operations/Releasing.md) demonstrates crosscutting documentation: a topic page that brings existing services, deployment units, and environments into one view without defining duplicate domain subjects.

[ServiceDiagram.md](ServiceDiagram.md) references the two services as widgets on the existing CanvasLayout.

[arc42.md](arc42.md) is one ordinary Markdown document with all twelve arc42 sections. Its embedded widgets aggregate the same service, resource, relationship, and deployment definitions used elsewhere. Narrative, decisions, and an example runtime scenario are authored in the document.

```text
Files/ArchDocs/
├── README.md
├── Architecture.md
├── arc42.md
├── ServiceDiagram.md
├── Repository.json
├── resources/
│   └── OrderEvents.json
├── services/
│   ├── Orders/
│   │   ├── Service.md
│   │   └── resources/
│   │       └── Database.json
│   └── Payments/
│       ├── Service.md
│       └── resources/
│           └── Database.json
├── deployment-units/
│   ├── Orders.json
│   └── Payments.json
├── environments/
│   ├── Staging.json
│   └── Production.json
├── operations/
│   └── Releasing.md
└── .source/
    ├── Commerce.slnx
    ├── Directory.Build.props
    ├── Directory.Packages.props
    └── src/
        ├── Orders.Api/
        ├── Orders.Worker/
        ├── Orders.Contracts/
        └── Payments.Api/
```

## Storage and discovery

The sample uses the existing HomeBlaze storage root that mounts Files. It has no nested Root.json storage subject. Graph references begin at `/ArchDocs`, so the overview queries stay within the sample. [Repository.json](Repository.json) proposes a SourceRepository whose sourceRoot is resolved relative to that definition file, so `.source` points at the internal fixture checkout.

Storage already excludes dot-prefixed directories from subject discovery. The .source directory is therefore available to the analyzer as raw input without turning its projects or build outputs into documentation subjects. The ordinary application and device folders remain outside the sample's query scope. This is a development-data sample; its feature-specific types and widgets become functional when the proposed capability is implemented.

The three API/worker projects explicitly declare OutputType=Exe, and Orders.Contracts is a library. The .NET analyzer should discover four CodeProjects and three Hosts. The console programs provide small runnable entry points for project discovery; the sample's names describe their architectural roles.

Build from this folder with .NET 10:

```sh
dotnet build .source/Commerce.slnx
```

For example, run one fixture with:

```sh
dotnet run --project .source/src/Orders.Api/Orders.Api.csproj
```

The projects and central package settings are local to this folder and can be copied together. They do not reference the surrounding solution.

## Crosscutting and master-detail views

Documentation can follow topics, workflows, or decisions independently of the domain hierarchy. The release page references both services and embeds scoped views over their shared graph. An overview list can find Services, DeploymentUnits, or Environments by scope and type, while using their existing domain facts and presentation contributions.

Proposed interaction: selecting a row reveals that subject's details alongside the list, using the page's context slot where appropriate. Services expose hosts, projects, and relationships; units expose their selected hosts and deployments; environments expose their configured units and generated deployments. Links and previews allow drilling into related subjects or opening their documentation. The document stays visible and row selection is UI state, not a rewrite of its Markdown or a new domain subject. Exact table options and layout remain open; this sample does not introduce additional widget configuration to lock them in.

## Canvas diagrams

The service diagram composes existing CanvasLayout nodes with Widget references to the proposed Service subjects. Their service-summary widget supplies each box's presentation, reusing the same facts as other views. The existing canvas supports fixed node positions and sizes, edit-mode dragging, grid snapping, and width-based scaling. Named nodes keep their identities when the diagram is edited. Diagram layout belongs to this document, independently of the service definitions.

Service subjects and their widget are proposed functionality; CanvasLayout and Widget already exist. The current canvas does not support connector lines between nodes. The user wants typed connections backed by model relationships and automatic diagrams at several levels. The manual canvas example records box placement without inventing unsupported edge syntax. The generated ArchitectureDiagram blocks in arc42.md are proposed UI, including their View options; they are not existing components.

## Reusable architecture widgets

All architecture widgets are independent of documentation format. They can be embedded in arc42, service documentation, design proposals, guides, ADRs, or dashboards using the same configuration and shared domain data. The Orders service page and arc42.md embed the same component-diagram widget; the release guide and arc42.md reuse the same table and matrix widgets. Document headings and file locations do not determine widget behavior.

Separate data selection from presentation. Scoped path queries and optional filters choose the subjects; presentation options can choose visible subject and relationship kinds, grouping, labels, detail, and layout. Services, Components, Context, and Deployment are proposed view presets, with exact customization options still to be refined in the sample. Presets provide useful defaults while allowing mixed subject kinds and custom views. Domain-specific tables and matrices remain reusable components over shared query and subject-presentation infrastructure. Subject types can supply their own widget presentation through the existing component-registration patterns.

## One arc42 document over the shared model

arc42.md is an ordinary MarkdownFile. It uses the twelve arc42 headings as document structure, not as separate domain subjects or a special document type. Service tables and context, building block, component, and deployment diagrams are embedded views, scoped by their paths. Their facts come from the existing domain subjects; the document supplies the explanations, decisions, and selected scenarios. Moving the document or reorganizing its headings does not redefine the architecture.

The context widget proposes using the selected services and their component membership as the system boundary. External actors and neighboring systems are not yet modeled in this sample, and exact boundary configuration remains open. Infrastructure mappings are likewise incomplete. These are sample-data/model gaps rather than a reason to invent them during rendering. Widgets should expose missing data while the author continues working on the same document.

## Resources and typed relationships

Resource is a proposed generic domain subject with an extensible kind and descriptive technology. This sample defines two logical databases and one shared queue. Services include resources through their normal componentSources alongside code and hosts. Membership does not imply exclusive ownership: both services reference the same queue. A DeploymentUnit still selects Host subjects; including a resource in a service does not automatically provision it or make it part of a host-only deployment unit.

Each service has a folder containing its documentation and its service-specific resources. The root resources folder holds shared resources such as OrderEvents. Folder placement organizes the files; explicit path references still define membership and relationships. Deployment units stay separate because one unit can select hosts from several services.

Relationships are authored metadata. Their kind expresses the interaction, such as calls, writes, publishes, or consumes; technology and optional message or label describe how it happens. The service-scoped relationship list defaults its source to that service, with an explicit source path when the interaction is known to belong to a particular Host. This lets generated and manually arranged diagrams use the same relationships.

Service page metadata uses the existing metadata envelope shape with a `$subjects` entry for `Children[service]`. This scopes relationships to the Service child rather than the Markdown document. Frontmatter persistence of that envelope, typed relationship readers, source overrides, and all Resource types remain proposed functionality. The source fixtures do not implement the described API calls, database access, or messaging; those interactions are authored sample architecture facts. They must not be mistaken for analyzer-discovered facts.

Generated service views can aggregate host-level interactions using service membership, retaining the endpoint facts and avoiding inferred runtime order. A detailed view preserves unknown host endpoints rather than attaching a service-level relationship to a guessed process. Consumes is stored as a dependency from the consumer to its queue; a message-flow presentation may reverse the drawn direction explicitly. Shared or overlapping membership must remain visible rather than establish a fictitious exclusive owner. The same queue is never copied into separate resource identities for the two services.

For now, resources describe logical dependencies independently of environments. Mapping them to actual database, queue, or broker instances per environment remains an open design choice. Automatic arc42-style views need enough data for each view: building block views use structure and relationships, runtime views need scenarios, and complete deployment views need infrastructure mappings. Diagram scope and display settings belong to the view; semantic connections belong to the shared graph.

## Proposed graph and paths

The storage root exposes JSON subjects by filename without extension and Markdown pages with their extension. A `$service` declaration adds a named `service` child to its containing page; the dollar sign is used only in the declaration. Absolute paths address `/ArchDocs` under the existing Files graph. Analyzer collection keys are simplified illustrative identities; the implementation must distinguish projects with duplicate names.

```text
ArchDocs (/ArchDocs)
├── Repository
│   ├── projects
│   │   ├── Orders.Api
│   │   ├── Orders.Worker
│   │   ├── Orders.Contracts
│   │   └── Payments.Api
│   └── hosts
│       ├── Orders.Api
│       ├── Orders.Worker
│       └── Payments.Api
├── services
│   ├── Orders
│   │   ├── Service.md/service
│   │   │   ├── components → references 3 CodeProjects, 2 Hosts, and 2 Resources
│   │   │   └── hosts → references both Orders Hosts
│   │   └── resources/Database
│   └── Payments
│       ├── Service.md/service
│       │   ├── components → references 1 CodeProject, 1 Host, and 2 Resources
│       │   └── hosts → references the Payments Host
│       └── resources/Database
├── resources
│   └── OrderEvents → shared by both services
├── deployment-units
│   ├── Orders → references both Orders Hosts
│   └── Payments → references the Payments Host
└── environments
    ├── Staging/deployments → Orders and Payments
    └── Production/deployments → Orders and Payments
```

The repository owns discovered projects and hosts. Services and deployment units reference them rather than create copies. A Service's hosts collection contains the Host subjects in its resolved components. Source query entries and resolved collections remain separate, such as hostSources and hosts.

An exact subject path selects one subject. A collection-property path selects its members; adding `/*` explicitly selects those direct members too. `*` stays within one path segment, and `**` spans any number of path segments. Optional `where` filters use property conditions. For example, `/ArchDocs/Repository/**/Orders.*` illustrates recursive matching. A path targets graph subjects, not source files.

The absolute references keep this example easy to follow. Moving the sample under another graph root would require adjusting its absolute references or using suitable scoped relative paths. Source-file access still resolves from the definition's local origin.

## Expected deployments

Each environment declares the two deployment units and generates one enduring Deployment per unit, producing four deployments:

| Environment | Deployment unit | Hosts |
|---|---|---|
| Staging | Orders | Orders.Api, Orders.Worker |
| Staging | Payments | Payments.Api |
| Production | Orders | Orders.Api, Orders.Worker |
| Production | Payments | Payments.Api |

These are intended installations. Creating subjects does not deploy software. Installed versions and URLs can be enriched through metadata, and observed health remains unknown without a connector. Metadata editing and persistence are part of the broader proposed capability; this first sample focuses on definitions and references.

## Variations to discuss

To deploy only the Orders worker, change the Orders unit's hostSources to:

```json
[
  {
    "path": "/ArchDocs/services/Orders/Service.md/service/hosts/*",
    "where": "Name == \"Orders.Worker\""
  }
]
```

This predicate assumes the analyzer reports that project name as the Host's Name. Stable identity remains the graph key rather than its display name.

To deploy hosts from both services together, add another hostSources entry to one unit:

```json
{
  "path": "/ArchDocs/services/Payments/Service.md/service/hosts/*"
}
```

Multiple query entries combine their results and remove duplicates. Services and deployment units do not need to have matching boundaries.

To make a service standalone later, its `$service` value can move into its own subject JSON file. Move its child-scoped metadata too and update paths that address the service. Its Markdown page can then describe it through a metadata relationship. The domain type and configuration schema stay the same.

## Decisions still open

Use the files to settle configuration names, analyzer-generated identities, metadata representation, and the amount of UI required for the first implementation. The context slot and ordinary-link previews should expose useful subject information without requiring the overview's optional widget blocks. This sample does not approve an implementation plan or fix the final MVP boundary.
