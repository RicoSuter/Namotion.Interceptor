---
title: Orders
$service:
  $type: Namotion.ArchDocs.Service
  name: Orders
  componentSources:
    - path: /Repository/projects/Orders.*
    - path: /Repository/hosts/Orders.*
---

# Orders

Orders accepts orders through an API host and processes them through a worker host. Both projects reference the Orders.Contracts library.

The service uses the [Payments service](Payments.md) for payment processing.
