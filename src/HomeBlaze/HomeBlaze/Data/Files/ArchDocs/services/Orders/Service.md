---
title: Orders
$service:
  $type: Namotion.ArchDocs.Service
  name: Orders
  kind: Application
  componentSources:
    - path: /ArchDocs/Repository/projects/Orders.*
    - path: /ArchDocs/Repository/hosts/Orders.*
    - path: /ArchDocs/services/Orders/resources/Database
    - path: /ArchDocs/resources/OrderEvents
metadata:
  $subjects:
    "Children[service]":
      relationships:
        - kind: calls
          target: /ArchDocs/services/Payments/Service.md/service
          technology: HTTPS
          label: Authorize payment
        - kind: writes
          source: /ArchDocs/Repository/hosts/Orders.Worker
          target: /ArchDocs/services/Orders/resources/Database
          technology: PostgreSQL
        - kind: publishes
          source: /ArchDocs/Repository/hosts/Orders.Worker
          target: /ArchDocs/resources/OrderEvents
          technology: AMQP
          message: OrderPlaced
---

# Orders

Orders accepts orders through an API host and processes them through a worker host. Both projects reference the Orders.Contracts library.

The service uses the [Payments service](../Payments/Service.md) for payment processing.

The worker stores order data in the Orders database and publishes OrderPlaced messages to the shared order-events queue.

## Context

Orders is inside this view's boundary. Payments is a neighboring service in this view, while remaining inside the wider Commerce context.

:: Namotion.ArchDocs.ArchitectureDiagram#context Path=/ArchDocs View=Context
```json
{
  "InternalSources": [
    { "Path": "service" }
  ],
  "ExternalSources": [
    { "Path": "/ArchDocs/services/Payments/Service.md/service" }
  ]
}
```
::

## Components

:: Namotion.ArchDocs.ArchitectureDiagram#components Path=service View=Components
::
