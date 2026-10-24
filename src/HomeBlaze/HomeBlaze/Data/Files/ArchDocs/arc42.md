---
title: Commerce Architecture Documentation
---

# Commerce Architecture Documentation

This document follows [arc42](https://docs.arc42.org/home/). It combines authored explanations with views over the services, resources, relationships, and deployments defined elsewhere in this documentation.

## 1. Introduction and goals

Commerce accepts orders and handles payment processing. Its main capabilities are described by the [Orders service](services/Orders/Service.md) and [Payments service](services/Payments/Service.md).

The architecture should allow the two services to be released independently while keeping the Orders API and worker in one release unit.

## 2. Architecture constraints

The code is organized into .NET projects. Orders uses a shared contracts library across its API and worker. PostgreSQL stores service data, and RabbitMQ carries order events.

## 3. Context and scope

The Commerce boundary includes Orders and Payments. Both are internal in this view. In the [Orders-only context](services/Orders/Service.md), Payments is outside the chosen boundary. Additional external participants can be selected as they are modeled.

:: Namotion.ArchDocs.ArchitectureDiagram#context Path=/ArchDocs View=Context
```json
{
  "InternalSources": [
    { "Path": "/ArchDocs/services/Orders/Service.md/service" },
    { "Path": "/ArchDocs/services/Payments/Service.md/service" }
  ],
  "ExternalSources": []
}
```
::

## 4. Solution strategy

Orders separates its entry API from background processing. Payments has an independent service and deployment unit. Typed relationships describe synchronous calls, messaging, and resource access.

## 5. Building block view

### Services

:: Namotion.ArchDocs.ServiceTable#services Path=/ArchDocs/services
::

:: Namotion.ArchDocs.ArchitectureDiagram#serviceOverview Path=/ArchDocs/services View=Services
::

### Inside Orders

:: Namotion.ArchDocs.ArchitectureDiagram#ordersComponents Path=/ArchDocs/services/Orders/Service.md/service View=Components
::

Host-specific relationships attach to their declared hosts. The service-level Payments call stays at the Orders boundary until its host endpoint is specified. Shared resources retain one identity across the views.

### Inside Payments

:: Namotion.ArchDocs.ArchitectureDiagram#paymentsComponents Path=/ArchDocs/services/Payments/Service.md/service View=Components
::

The [manually arranged canvas](ServiceDiagram.md) provides another presentation of these same services.

## 6. Runtime view

An authored example of order processing:

1. Orders requests payment authorization from Payments through HTTPS.
2. The Orders worker writes order data to its database.
3. The worker publishes OrderPlaced to the order-events queue.
4. The Payments host consumes the message and writes payment data to its database.

The ordering above is authored scenario information. Static relationships provide the participants and interaction kinds without implying a particular execution order.

## 7. Deployment view

Both environments include the declared Orders and Payments deployment units. Their installations can have different versions and URLs.

:: Namotion.ArchDocs.DeploymentMatrix#deployments Path=/ArchDocs/environments
::

:: Namotion.ArchDocs.ArchitectureDiagram#productionDeployment Path=/ArchDocs/environments/Production View=Deployment
::

The current model describes intended installations and host grouping. Physical infrastructure and environment-specific database and queue instances still need to be mapped.

## 8. Crosscutting concepts

Services communicate through synchronous calls and asynchronous messages. Typed relationships record the interaction kind, transport technology, and message name where known. The [release guide](operations/Releasing.md) brings service and deployment information together for operational planning.

## 9. Architecture decisions

The Orders API and worker share a deployment unit. Payments uses a separate deployment unit. Logical service boundaries and deployment grouping are configured independently, allowing this arrangement to change without redefining the services.

## 10. Quality requirements

The independent-release goal in section 1 applies to the deployment arrangement shown in section 7. Quantified latency, availability, and recovery scenarios remain to be specified.

## 11. Risks and technical debt

The order-events queue is shared by both services, so message-contract changes require coordination. The service-level payment call does not yet identify its source host. Infrastructure mappings and external context participants remain incomplete.

## 12. Glossary

- **Service:** a logical software capability that can contain several hosts and resources.
- **Host:** a runnable software entry point, rather than a server or virtual machine.
- **Resource:** a dependency such as a database or queue.
- **Deployment unit:** hosts released together.
- **Deployment:** the intended installation of one deployment unit in one environment.
