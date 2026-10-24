# ArchDocs Commerce Working Sample

Use this folder to refine the authoring experience before finalizing the domain design, implementation plan, and MVP. It contains a sample checkout, normal Markdown documentation, proposed subject JSON definitions, and a docs-only storage root. The ArchDocs types, frontmatter child declarations, path queries, and architecture widgets are proposed functionality. The .NET projects are buildable fixtures, not an implementation of that functionality.

## Start here

Read [docs/Architecture.md](docs/Architecture.md) for the overview, [docs/services/Orders.md](docs/services/Orders.md) for a service defined in Markdown frontmatter, and [docs/deployment-units/Orders.json](docs/deployment-units/Orders.json) for a standalone subject definition. Revise these actual files while discussing the model. Type names, property names, and generated graph keys are working proposals.

[docs/operations/Releasing.md](docs/operations/Releasing.md) demonstrates crosscutting documentation: a topic page that brings existing services, deployment units, and environments into one view without defining duplicate domain subjects.

[docs/ServiceDiagram.md](docs/ServiceDiagram.md) references the two services as widgets on the existing CanvasLayout.

```text
archdocs-commerce/
├── Root.json
├── Commerce.slnx
├── Directory.Build.props
├── Directory.Packages.props
├── docs/
│   ├── Architecture.md
│   ├── ServiceDiagram.md
│   ├── Repository.json
│   ├── services/
│   │   ├── Orders.md
│   │   └── Payments.md
│   ├── deployment-units/
│   │   ├── Orders.json
│   │   └── Payments.json
│   ├── environments/
│   │   ├── Staging.json
│   │   └── Production.json
│   └── operations/
│       └── Releasing.md
└── src/
    ├── Orders.Api/
    ├── Orders.Worker/
    ├── Orders.Contracts/
    └── Payments.Api/
```

## Storage and discovery

[Root.json](Root.json) uses the existing FluentStorageContainer configuration to mount only docs. Its connectionString is relative to Root.json's directory. It does not mount the source folder as documentation. [Repository.json](docs/Repository.json) proposes a SourceRepository whose sourceRoot is resolved relative to that definition file, so `..` points at this sample's root.

The three API/worker projects explicitly declare OutputType=Exe, and Orders.Contracts is a library. The .NET analyzer should discover four CodeProjects and three Hosts. The console programs provide small runnable entry points for project discovery; the sample's names describe their architectural roles.

Build from this folder with .NET 10:

```sh
dotnet build Commerce.slnx
```

For example, run one fixture with:

```sh
dotnet run --project src/Orders.Api/Orders.Api.csproj
```

The projects and central package settings are local to this folder and can be copied together. They do not reference the surrounding solution.

## Crosscutting and master-detail views

Documentation can follow topics, workflows, or decisions independently of the domain hierarchy. The release page references both services and embeds scoped views over their shared graph. An overview list can find Services, DeploymentUnits, or Environments by scope and type, while using their existing domain facts and presentation contributions.

Proposed interaction: selecting a row reveals that subject's details alongside the list, using the page's context slot where appropriate. Services expose hosts, projects, and relationships; units expose their selected hosts and deployments; environments expose their configured units and generated deployments. Links and previews allow drilling into related subjects or opening their documentation. The document stays visible and row selection is UI state, not a rewrite of its Markdown or a new domain subject. Exact table options and layout remain open; this sample does not introduce additional widget configuration to lock them in.

## Canvas diagrams

The service diagram composes existing CanvasLayout nodes with Widget references to the proposed Service subjects. Their service-summary widget supplies each box's presentation, reusing the same facts as other views. The existing canvas supports fixed node positions and sizes, edit-mode dragging, grid snapping, and width-based scaling. Named nodes keep their identities when the diagram is edited. Diagram layout belongs to this document, independently of the service definitions.

Service subjects and their widget are proposed functionality; CanvasLayout and Widget already exist. The current canvas does not support connector lines between nodes. Whether to add manually authored connections, display model relationships, or both remains a design discussion. This example records the box composition without inventing unsupported edge syntax.

## Proposed graph and paths

The storage root exposes JSON subjects by filename without extension and Markdown pages with their extension. A `$service` declaration adds a named `service` child to its containing page; the dollar sign is used only in the declaration. Absolute paths below are relative to this sample's Root.json graph. Analyzer collection keys are simplified illustrative identities; the implementation must distinguish projects with duplicate names.

```text
root
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
│   ├── Orders.md/service
│   │   ├── components → references 3 CodeProjects and 2 Hosts
│   │   └── hosts → references both Orders Hosts
│   └── Payments.md/service
│       ├── components → references 1 CodeProject and 1 Host
│       └── hosts → references the Payments Host
├── deployment-units
│   ├── Orders → references both Orders Hosts
│   └── Payments → references the Payments Host
└── environments
    ├── Staging/deployments → Orders and Payments
    └── Production/deployments → Orders and Payments
```

The repository owns discovered projects and hosts. Services and deployment units reference them rather than create copies. A Service's hosts collection contains the Host subjects in its resolved components. Source query entries and resolved collections remain separate, such as hostSources and hosts.

An exact subject path selects one subject. A collection-property path selects its members; adding `/*` explicitly selects those direct members too. `*` stays within one path segment, and `**` spans any number of path segments. Optional `where` filters use property conditions. For example, `/Repository/**/Orders.*` illustrates recursive matching. A path targets graph subjects, not source files.

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
    "path": "/services/Orders.md/service/hosts/*",
    "where": "Name == \"Orders.Worker\""
  }
]
```

This predicate assumes the analyzer reports that project name as the Host's Name. Stable identity remains the graph key rather than its display name.

To deploy hosts from both services together, add another hostSources entry to one unit:

```json
{
  "path": "/services/Payments.md/service/hosts/*"
}
```

Multiple query entries combine their results and remove duplicates. Services and deployment units do not need to have matching boundaries.

To make a service standalone later, its `$service` value can move into its own subject JSON file. Move its child-scoped metadata too and update paths that address the service. Its Markdown page can then describe it through a metadata relationship. The domain type and configuration schema stay the same.

## Decisions still open

Use the files to settle configuration names, analyzer-generated identities, metadata representation, and the amount of UI required for the first implementation. The context slot and ordinary-link previews should expose useful subject information without requiring the overview's optional widget blocks. This sample does not approve an implementation plan or fix the final MVP boundary.
