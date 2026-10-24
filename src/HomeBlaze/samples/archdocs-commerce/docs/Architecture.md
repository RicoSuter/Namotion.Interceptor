---
title: Commerce Architecture
---

# Commerce Architecture

The [Orders service](services/Orders.md) accepts and processes orders. The [Payments service](services/Payments.md) handles payments. Each service has its own deployment unit, installed in staging and production.

See [Releasing Commerce](operations/Releasing.md) for a crosscutting view of the services and their deployment arrangement.

The [service diagram](ServiceDiagram.md) places the same services as boxes on a canvas.

## Services

:: Namotion.ArchDocs.ServiceTable Path=/
::

## Deployments

:: Namotion.ArchDocs.DeploymentMatrix Path=/
::
