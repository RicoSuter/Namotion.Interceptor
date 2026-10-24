---
title: Orders
$service:
  $type: Namotion.ArchDocs.Service
  name: Orders
  componentSources:
    - path: /Repository/projects/Orders.*
    - path: /Repository/hosts/Orders.*
    - path: /resources/OrdersDatabase
    - path: /resources/OrderEvents
metadata:
  $subjects:
    "Children[service]":
      relationships:
        - kind: calls
          target: /services/Payments.md/service
          technology: HTTPS
          label: Authorize payment
        - kind: writes
          source: /Repository/hosts/Orders.Worker
          target: /resources/OrdersDatabase
          technology: PostgreSQL
        - kind: publishes
          source: /Repository/hosts/Orders.Worker
          target: /resources/OrderEvents
          technology: AMQP
          message: OrderPlaced
---

# Orders

Orders accepts orders through an API host and processes them through a worker host. Both projects reference the Orders.Contracts library.

The service uses the [Payments service](Payments.md) for payment processing.

The worker stores order data in the Orders database and publishes OrderPlaced messages to the shared order-events queue.

## Components

:: Namotion.ArchDocs.ArchitectureDiagram#components Path=service View=Components
::
