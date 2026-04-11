# Namotion.ArchDocs: Design

A HomeBlaze subject library for documenting and monitoring software system architectures. Models services, their protocol endpoints, environments, and deployments with active health checking.

## Entities

### Service

Top-level IConfigurable subject. Implements IPage. Represents a microservice, webapp, or any deployable software component.

| Property | Type | Attribute | Notes |
|----------|------|-----------|-------|
| Id | string | `[Configuration(IsIdentifier)]` | GUID |
| Title | string | `[Configuration]` | Display name |
| Summary | string? | `[Configuration]` | Short text — one-liner |
| Owner | string? | `[Configuration]` | Team or person |
| RepositoryUrl | string? | `[Configuration]` | Source repository |
| Tags | string[]? | `[Configuration]` | Labels/categories |
| Description | string? | `[Configuration]` | Markdown — what this service does |
| Documentation | string? | `[Configuration]` | Markdown — architecture notes, decisions |
| Endpoints | ServiceEndpoint[] | `[Configuration]` | Child subjects — protocol interfaces |

### ServiceEndpoint

Child of Service. Describes a protocol interface the service exposes (e.g., "HTTP API", "GraphQL API", "OPC UA Server").

| Property | Type | Attribute | Notes |
|----------|------|-----------|-------|
| Title | string | `[Configuration]` | e.g., "HTTP API" |
| Type | EndpointType | `[Configuration]` | Enum: Http, GraphQL, OpcUa, Mqtt, WebSocket, Grpc, Other |
| Description | string? | `[Configuration]` | Markdown — API surface documentation |

### ServiceEnvironment

Top-level IConfigurable subject. Implements IPage. Represents a deployment target (production, staging, etc.). Named `ServiceEnvironment` to avoid conflict with `System.Environment`.

| Property | Type | Attribute | Notes |
|----------|------|-----------|-------|
| Id | string | `[Configuration(IsIdentifier)]` | GUID |
| Title | string | `[Configuration]` | e.g., "Production" |
| Summary | string? | `[Configuration]` | Short text — one-liner |
| Position | int | `[Configuration]` | Sort order (lower = first) |
| BaseUrl | string? | `[Configuration]` | e.g., `https://staging.example.com` |
| Description | string? | `[Configuration]` | Markdown — purpose of this environment |
| Notes | string? | `[Configuration]` | Markdown — access instructions, VPN, credentials |

### Deployment

Top-level IConfigurable subject. Implements IPage. BackgroundService that polls health. Represents a specific service deployed to a specific environment.

| Property | Type | Attribute | Notes |
|----------|------|-----------|-------|
| Id | string | `[Configuration(IsIdentifier)]` | GUID |
| Title | string | `[Configuration]` | e.g., "User API (Production)" |
| ServicePath | string? | `[Configuration]` | Path reference to Service |
| EnvironmentPath | string? | `[Configuration]` | Path reference to ServiceEnvironment |
| HealthCheckUrl | string? | `[Configuration]` | e.g., `https://api.example.com/health` |
| HealthCheckIntervalSeconds | int | `[Configuration]` | Default 60 |
| WebUrl | string? | `[Configuration]` | Link to running web app |
| DocumentationUrl | string? | `[Configuration]` | Link to hosted docs |
| TestsUrl | string? | `[Configuration]` | Link to test results / CI |
| ReleasesUrl | string? | `[Configuration]` | Link to release pipeline / changelog |
| Version | string? | `[Configuration]` | Currently deployed version |
| Notes | string? | `[Configuration]` | Markdown — operational details |
| IsHealthy | bool? | `[State]` | Last health check result |
| LastHealthCheckTime | DateTimeOffset? | `[State]` | When last checked |
| LastHealthCheckError | string? | `[State]` | Error message if unhealthy |
| Endpoints | DeploymentEndpoint[] | `[Configuration]` | Child subjects |

Health check logic: HTTP GET to `HealthCheckUrl`, 2xx = healthy. Runs on `HealthCheckIntervalSeconds` interval in `ExecuteAsync`.

### DeploymentEndpoint

Child of Deployment. Concrete details for a ServiceEndpoint in a specific deployment.

| Property | Type | Attribute | Notes |
|----------|------|-----------|-------|
| Title | string | `[Configuration]` | e.g., "HTTP API" |
| ServiceEndpointPath | string? | `[Configuration]` | Path reference to ServiceEndpoint |
| Url | string? | `[Configuration]` | Concrete API URL |
| WebUrl | string? | `[Configuration]` | Human-readable endpoint URL |
| OperationPathPrefix | string? | `[Configuration]` | e.g., `/api/v2` |
| SpecificationUrl | string? | `[Configuration]` | e.g., OpenAPI spec URL |
| Notes | string? | `[Configuration]` | Markdown |

### DeploymentMatrix

Standalone widget subject. No IPage. Queries the registry for all Services, Environments, and Deployments, renders a cross-table (services × environments) with health indicators. Can be embedded in any markdown page.

| Property | Type | Attribute | Notes |
|----------|------|-----------|-------|
| Id | string | `[Configuration(IsIdentifier)]` | GUID |
| TagFilter | string[]? | `[Configuration]` | Only show services with these tags |

## Relationships

```
Service (top-level, services/ folder, IPage)
  └── ServiceEndpoint[] (child, persisted in Service JSON)

ServiceEnvironment (top-level, environments/ folder, IPage)

Deployment (top-level, deployments/ folder, IPage, BackgroundService)
  ├── → Service (path reference)
  ├── → ServiceEnvironment (path reference)
  └── DeploymentEndpoint[] (child, persisted in Deployment JSON)
        └── → ServiceEndpoint (path reference)

DeploymentMatrix (standalone widget, embeddable in markdown pages)
  └── resolves all Services, Environments, Deployments from registry
```

All cross-entity relationships use path references (strings resolved at runtime via SubjectPathResolver). Child subjects are owned arrays persisted in their parent's JSON.

## Folder Structure

```
Data/
  ArchDocs/
    services/
      user-api.json
      order-api.json
    environments/
      production.json
      staging.json
    deployments/
      user-api-production.json
      user-api-staging.json
```

## Project Structure

```
src/HomeBlaze/
  Namotion.ArchDocs/
    Namotion.ArchDocs.csproj
    Service.cs
    ServiceEndpoint.cs
    ServiceEnvironment.cs
    Deployment.cs
    DeploymentEndpoint.cs
    DeploymentMatrix.cs
    EndpointType.cs
    ArchDocsServiceCollectionExtensions.cs
  Namotion.ArchDocs.HomeBlaze/
    Namotion.ArchDocs.HomeBlaze.csproj
    _Imports.razor
    ServiceWidget.razor
    ServiceEditComponent.razor
    ServicePageComponent.razor
    EnvironmentWidget.razor
    EnvironmentEditComponent.razor
    EnvironmentPageComponent.razor
    DeploymentWidget.razor
    DeploymentEditComponent.razor
    DeploymentPageComponent.razor
    DeploymentMatrixWidget.razor
```

## UI Components

### Widgets (compact, embeddable in markdown pages)
- **ServiceWidget** — title, summary, endpoint count, owner, aggregated health across deployments
- **EnvironmentWidget** — title, base URL, deployment count, aggregated health
- **DeploymentWidget** — title, health indicator (green/red/grey), version, last check time
- **DeploymentMatrixWidget** — services × environments cross-table with health indicators

### Page Components (full page views, IPage navigation)
- **ServicePageComponent** — description/documentation rendered as markdown, endpoints list, all deployments referencing this service with health status
- **EnvironmentPageComponent** — description/notes rendered as markdown, all deployments in this environment with health status
- **DeploymentPageComponent** — notes rendered as markdown, health status details, deployment endpoints with links

### Edit Components (configuration forms)
- **ServiceEditComponent** — all config fields, manage endpoints array
- **EnvironmentEditComponent** — all config fields
- **DeploymentEditComponent** — all config fields, manage deployment endpoints, path pickers for Service and Environment

No setup components — all subjects are simple create-and-configure.

## Future Considerations (not v1)

- **EndpointDependency** — cross-service dependency graph (Service depends on another Service's Endpoint)
- **EndpointDocument** — versioned API spec tracking per endpoint per environment (version, hash, commit, timestamp)
- **Monitoring integration** — ApplicationInsights, Seq links on Environment
- **Auto-update documents** — automatically fetch and track spec changes

## Feature Checklist

1. Service subject with child ServiceEndpoints, IPage
2. ServiceEnvironment subject, IPage
3. Deployment subject (BackgroundService) with child DeploymentEndpoints, IPage
4. DeploymentMatrix widget subject
5. Health check polling (HTTP GET, 2xx = healthy)
6. Path references: Deployment → Service, Deployment → Environment, DeploymentEndpoint → ServiceEndpoint
7. Rich markdown fields on all subjects
8. Blazor UI: widgets + page components + edit components for all top-level subjects
9. DeploymentMatrix widget component (services × environments cross-table)
10. Documentation markdown file
11. JSON config templates
12. Solution integration + HomeBlaze registration
