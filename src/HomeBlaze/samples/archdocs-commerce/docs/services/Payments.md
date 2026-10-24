---
title: Payments
$service:
  $type: Namotion.ArchDocs.Service
  name: Payments
  componentSources:
    - path: /Repository/projects/Payments.*
    - path: /Repository/hosts/Payments.*
    - path: /resources/PaymentsDatabase
    - path: /resources/OrderEvents
metadata:
  $subjects:
    "Children[service]":
      relationships:
        - kind: consumes
          source: /Repository/hosts/Payments.Api
          target: /resources/OrderEvents
          technology: AMQP
          message: OrderPlaced
        - kind: writes
          source: /Repository/hosts/Payments.Api
          target: /resources/PaymentsDatabase
          technology: PostgreSQL
---

# Payments

Payments handles payment processing through one API host. It is deployed independently of the [Orders service](Orders.md).

Its host consumes OrderPlaced messages from the shared order-events queue and stores payment data in the Payments database.
