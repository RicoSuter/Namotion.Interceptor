---
title: Payments
$service:
  $type: Namotion.ArchDocs.Service
  name: Payments
  componentSources:
    - path: /ArchDocs/Repository/projects/Payments.*
    - path: /ArchDocs/Repository/hosts/Payments.*
    - path: /ArchDocs/resources/PaymentsDatabase
    - path: /ArchDocs/resources/OrderEvents
metadata:
  $subjects:
    "Children[service]":
      relationships:
        - kind: consumes
          source: /ArchDocs/Repository/hosts/Payments.Api
          target: /ArchDocs/resources/OrderEvents
          technology: AMQP
          message: OrderPlaced
        - kind: writes
          source: /ArchDocs/Repository/hosts/Payments.Api
          target: /ArchDocs/resources/PaymentsDatabase
          technology: PostgreSQL
---

# Payments

Payments handles payment processing through one API host. It is deployed independently of the [Orders service](Orders.md).

Its host consumes OrderPlaced messages from the shared order-events queue and stores payment data in the Payments database.
