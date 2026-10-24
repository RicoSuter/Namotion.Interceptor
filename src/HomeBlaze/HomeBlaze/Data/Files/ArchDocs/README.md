---
title: ArchDocs Commerce Sample
navTitle: ArchDocs Sample
---

# ArchDocs Commerce Working Sample

Use this folder to refine the authoring experience before finalizing the domain design, implementation plan, and MVP. It lives under the existing HomeBlaze Files root as ArchDocs, containing normal Markdown, proposed subject JSON definitions, and an internal .source directory with buildable source fixtures. The ArchDocs types, frontmatter child declarations, path queries, and architecture widgets are proposed functionality. The .NET projects are fixtures, not an implementation of that functionality.

The [feature reference](../Docs/ArchDocs/overview.md) is authoritative for subjects, analyzers, shared behavior, widgets, implementation readiness, and scope decisions. Its [usage examples](../Docs/ArchDocs/examples/overview.md) cover different documentation styles with fenced JSON and Markdown source. This folder is optional editable test material; update the relevant reference page alongside a sample when a model decision changes. Concrete sample field names remain proposals.

The first real target is the existing AppHost documentation, with its ordinary arc42 overview, concept pages, decisions, guidelines, and domain documents. It describes one coordinated application with Web and Worker hosts, shared resources, and a one-off migrations executable. Orders there is a module, while this Commerce fixture deliberately exercises two Services with separate deployment units. The broader reference experience is [Apimundo](https://apimundo.com/organizations/namotion/projects/apimundo?service=1&tab=overview&repository=Apimundo), including its repository graph, API and contract views, deployment matrix, and package links. The specification records these domain gaps; this sample does not yet demonstrate all of them or select their MVP scope.

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

The repository's `analyzerSources` selects a shared singleton [Analyzer](../Docs/ArchDocs/subjects/analyzer.md), assumed to be published by the host at the illustrative `/analyzers/CSharpProjects` route outside this sample subtree. This is a future capability prerequisite, not an existing built-in subject or an analyzer JSON instance owned by this repository. Other repositories can select the same singleton. The raw source fixtures remain in this folder; each repository owns its own analysis work, discovered subjects, status, and diagnostics. The singleton registration/publication mechanism and exact route remain proposed.

Storage already excludes dot-prefixed directories from subject discovery. The .source directory is therefore available to the analyzer as raw input without turning its projects or build outputs into documentation subjects. The ordinary application and device folders remain outside the sample's query scope. This is a development-data sample; its feature-specific types and widgets become functional when the proposed capability is implemented.

The three API/worker projects explicitly declare OutputType=Exe, and Orders.Contracts is a library. The .NET analyzer should discover four CodeProjects, three Hosts, and one CodeSolution for Commerce.slnx. That solution references the four repository-owned projects; it does not own copies of them or establish a Service or deployment boundary. The console programs provide small runnable entry points for project discovery; the sample's names describe their architectural roles.

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

Proposed interaction: selecting a row reveals that subject's details alongside the list, using the page's context slot where appropriate. Services expose hosts, projects, and relationships; units expose their selected components and deployments; environments expose their configured units and generated deployments. Links and previews allow drilling into related subjects or opening their documentation. The document stays visible and row selection is UI state, not a rewrite of its Markdown or a new domain subject. Exact table options and layout remain open; this sample does not introduce additional widget configuration to lock them in.

## Canvas diagrams

The service diagram composes existing CanvasLayout nodes with Widget references to the proposed Service subjects. Their service-summary widget supplies each box's presentation, reusing the same facts as other views. The existing canvas supports fixed node positions and sizes, edit-mode dragging, grid snapping, and width-based scaling. Named nodes keep their identities when the diagram is edited. Diagram layout belongs to this document, independently of the service definitions.

Service subjects and their widget are proposed functionality; CanvasLayout and Widget already exist. The current canvas does not support connector lines between nodes. The user wants typed connections backed by model relationships and automatic diagrams at several levels. The manual canvas example records box placement without inventing unsupported edge syntax. The ServiceDiagram, ComponentDiagram, ContextDiagram, and DeploymentDiagram blocks in arc42.md are proposed distinct widgets over shared rendering; they are not existing components.

## Reusable architecture widgets

All architecture widgets are independent of documentation format. They can be embedded in arc42, service documentation, design proposals, guides, ADRs, or dashboards using the same configuration and shared domain data. The Orders service page and arc42.md embed the same component-diagram widget; the release guide and arc42.md reuse the same table and matrix widgets. Document headings and file locations do not determine widget behavior.

Separate data selection from presentation. Scoped path queries and optional filters choose the subjects; presentation options can choose visible subject and relationship kinds, grouping, labels, detail, and layout. Services, Components, Context, and Deployment are proposed view presets, with exact customization options still to be refined in the sample. Presets provide useful defaults while allowing mixed subject kinds and custom views. Domain-specific tables and matrices remain reusable components over shared query and subject-presentation infrastructure. Subject types can supply their own widget presentation through the existing component-registration patterns.

## One arc42 document over the shared model

arc42.md is an ordinary MarkdownFile. It uses the twelve arc42 headings as document structure, not as separate domain subjects or a special document type. Service tables and context, building block, component, and deployment diagrams are embedded views, scoped by their paths. Their facts come from the existing domain subjects; the document supplies the explanations, decisions, and selected scenarios. Moving the document or reorganizing its headings does not redefine the architecture.

Internal/external is selected per context diagram. The Commerce document selects Orders and Payments as internal; the Orders service page selects Orders as internal and Payments as external. Both reference the same Service subjects. The proposed widget fields are InternalSources and ExternalSources, each using path-query entries. Their PascalCase keys follow the existing Markdown block configuration convention. This is view data rather than a global Service kind; neighboring systems can be operated by the same organization. Exact field names, overlapping-set diagnostics, and optional neighbor discovery remain presentation choices. Additional actors and neighboring systems can be added to the model later.

## Resources and typed relationships

Service represents a logical software capability or system, including neighboring systems in the enterprise. Resource describes a component-level dependency such as a database, queue, or consumed cloud offering. Resources appear alongside Hosts in a service's component view. A cloud product called a "service" does not automatically become an architecture Service. Internal/external remains relative to each diagram, independent of the subject's type.

The two Database.json definitions and shared order-events queue are Resources. Their hosting and API/protocol details are not yet specified. Services reference dependencies through componentSources alongside code and hosts. Membership does not imply exclusive ownership. DeploymentUnits now use componentSources too and can include Resource definitions/configuration as explicit release participants. This fixture still selects only the three application Hosts. Including another Service or Resource in an application's components does not automatically provision it or include its hosts transitively. Exact release-participant eligibility and resource binding fields remain open.

A Service can also reference a database Host directly. Resource is optional when the Host describes enough; model a particular database as a Resource only when that extra detail is useful. Component membership does not require deploying the database Host together with application Hosts. DeploymentUnit selections define that grouping. This sample keeps the database Resources because their underlying Hosts are unspecified.

Each service has a folder containing its documentation and its service-specific resources. The root resources folder holds shared resources such as OrderEvents. Folder placement organizes the files; explicit path references still define membership and relationships. Deployment units stay separate because one unit can select hosts from several services.

Relationships are authored metadata. Their kind expresses the interaction, such as calls, writes, publishes, or consumes; technology and optional message or label describe how it happens. The service-scoped relationship list defaults its source to that service, with an explicit source path when the interaction is known to belong to a particular Host. This lets generated and manually arranged diagrams use the same relationships.

Service page metadata uses the existing metadata envelope shape with a `$subjects` entry named `service`. The `$service` entry defines that child, and its metadata uses the same inline name as widgets and paths. This scopes relationships to the Service child rather than the Markdown document. Inline child paths already exist; frontmatter declarations and persistence of that envelope, typed relationship readers, source overrides, and all Resource types remain proposed functionality. The source fixtures do not implement the described API calls, database access, or messaging; those interactions are authored sample architecture facts. They must not be mistaken for analyzer-discovered facts. The [scoped metadata contract](../Docs/ArchDocs/shared/metadata-and-relationships.md#scoped-metadata-paths-planned) defines resolution and writes.

Generated service views can aggregate host-level interactions using service membership, retaining the endpoint facts and avoiding inferred runtime order. A detailed view preserves unknown host endpoints rather than attaching a service-level relationship to a guessed process. Consumes is stored as a dependency from the consumer to its queue; a message-flow presentation may reverse the drawn direction explicitly. Shared or overlapping membership must remain visible rather than establish a fictitious exclusive owner. The same queue is never copied into separate resource identities for the two services.

A Host can provide several Resources, and a Resource can have several providing Hosts. A managed cloud Resource can have no known Host. These references do not transfer subject ownership; their exact serialized representation remains open. The sample does not yet declare database Hosts or provider links.

The model supports environment-specific versions, endpoints, and resource mappings while retaining shared logical component identities. Their exact records remain open, as do links to provider services. The current deployment matrix contains only the three modeled application hosts; it does not invent database hosts or provision queue instances. Automatic arc42-style views need enough data for each view: building block views use structure and relationships, runtime views need scenarios, and complete deployment views need infrastructure mappings. Diagram scope and display settings belong to the view; semantic connections belong to the shared graph.

## Interfaces and contracts

The domain distinguishes Interface, Endpoint, and Contract. A Service, Host, or Resource can expose a logical Interface; an Endpoint supplies its environment-specific address or connection details. Both Interfaces and CodeProjects can reference Contracts. A CodeProject's Contract can describe its public types and members without a runnable Host or network endpoint. Formats and analyzers remain extensible. This fixture does not yet add contract files or extraction; the simple project-file analyzer is not expected to discover public API surfaces. The working specification records these concepts independently of this sample's language and topology.

## Proposed graph and paths

The storage root exposes JSON subjects by filename without extension and Markdown pages with their extension. A `$service` declaration adds a named `service` child to its containing page; the dollar sign is used only in the declaration. Absolute paths address `/ArchDocs` under the existing Files graph. Analyzer collection keys are simplified illustrative identities; the implementation must distinguish projects with duplicate names.

```text
ArchDocs (/ArchDocs)
├── Repository
│   ├── solutions
│   │   └── Commerce.slnx → references the four CodeProjects below
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
│   │   │   ├── components → references 3 CodeProjects, 2 Hosts, 1 Database Resource, and 1 Queue Resource
│   │   │   └── hosts → references both Orders Hosts
│   │   └── resources/Database → Resource, hosting unspecified
│   └── Payments
│       ├── Service.md/service
│       │   ├── components → references 1 CodeProject, 1 Host, 1 Database Resource, and 1 Queue Resource
│       │   └── hosts → references the Payments Host
│       └── resources/Database → Resource, hosting unspecified
├── resources
│   └── OrderEvents → shared by both services
├── deployment-units
│   ├── Orders → references both Orders Hosts
│   └── Payments → references the Payments Host
└── environments
    ├── Staging/deployments → Orders and Payments
    └── Production/deployments → Orders and Payments
```

The repository owns discovered solutions, projects, and hosts. Commerce.slnx is a CodeSolution with membership references to the same four CodeProjects in the projects collection. The projects and hosts collections retain their distinct path segments even when their entries share a key. Services and deployment units reference these subjects rather than create copies. A Service's hosts collection contains the Host subjects in its resolved components. Source query entries and resolved collections remain separate: componentSources defines membership and components contains resolved references, with hosts as a typed subset where useful.

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

To deploy only the Orders worker, change the Orders unit's componentSources to:

```json
[
  {
    "path": "/ArchDocs/services/Orders/Service.md/service/hosts/*",
    "where": "Name == \"Orders.Worker\""
  }
]
```

This predicate assumes the analyzer reports that project name as the Host's Name. Stable identity remains the graph key rather than its display name.

To deploy hosts from both services together, add another componentSources entry to one unit:

```json
{
  "path": "/ArchDocs/services/Payments/Service.md/service/hosts/*"
}
```

Multiple query entries combine their results and remove duplicates. Services and deployment units do not need to have matching boundaries.

To make a service standalone later, its `$service` value can move into its own subject JSON file. Move its child-scoped metadata too and update paths that address the service. Its Markdown page can then describe it through a metadata relationship. The domain type and configuration schema stay the same.

## Decisions still open

Use the files to settle configuration names, analyzer-generated identities, metadata representation, and the amount of UI required for the first implementation. The context slot and ordinary-link previews should expose useful subject information without requiring the overview's optional widget blocks. This sample does not approve an implementation plan or fix the final MVP boundary.
