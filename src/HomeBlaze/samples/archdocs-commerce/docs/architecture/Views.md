---
title: Architecture Views
---

# Architecture Views

These views use the services, hosts, resources, and relationships defined elsewhere in this sample. Diagram contents follow those definitions; each view chooses a scope and level of detail.

## Service overview

:: Namotion.ArchDocs.ArchitectureDiagram Path=/ View=Services
::

Orders and Payments appear as service boxes with their declared HTTPS interaction. Messaging and data dependencies can be summarized or expanded to show the resources involved.

## Inside Orders

:: Namotion.ArchDocs.ArchitectureDiagram Path=/services/Orders.md/service View=Components
::

The view includes the Orders API, worker, database, and shared order-events queue. Relations with known host endpoints attach to those hosts. The service-level Payments interaction stays at the service boundary until its host endpoint is explicitly modeled. Shared resources retain one identity even when included in several services.

## Inside Payments

:: Namotion.ArchDocs.ArchitectureDiagram Path=/services/Payments.md/service View=Components
::

The Payments host consumes from the shared queue and writes to its database. The same queue and relationships are visible when exploring Orders, Payments, or the system overview.

## Production deployment

:: Namotion.ArchDocs.ArchitectureDiagram Path=/environments/Production View=Deployment
::

This view shows the intended production installations of the Orders and Payments units and the hosts selected by those units. Logical resources have not yet been mapped to environment-specific database or queue instances.

## Relation to arc42

Service overviews and service internals can support the hierarchical [building block view](https://docs.arc42.org/section-5/). A [context view](https://docs.arc42.org/section-3/) additionally needs an explicit system scope and its external communication partners. The [deployment view](https://docs.arc42.org/section-7/) includes the infrastructure and mappings used to run software; the current sample has logical environment/unit data, so physical nodes and resource instances would require more modeling. A [runtime view](https://docs.arc42.org/section-6/) describes scenarios, so sequence diagrams need declared interaction steps or runtime evidence in addition to static relationships.
