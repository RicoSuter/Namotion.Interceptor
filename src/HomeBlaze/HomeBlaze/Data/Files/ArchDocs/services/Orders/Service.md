---
title: Orders
$service:
  $type: Namotion.ArchDocs.Service
  name: Orders
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

## Components

:: Namotion.ArchDocs.ArchitectureDiagram#components Path=service View=Components
::
