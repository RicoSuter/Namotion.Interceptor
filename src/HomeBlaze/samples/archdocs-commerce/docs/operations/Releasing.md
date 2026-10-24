---
title: Releasing Commerce
---

# Releasing Commerce

A release can involve the [Orders service](../services/Orders.md), the [Payments service](../services/Payments.md), or both. Orders has an API host and a worker host in one deployment unit; Payments has its own API host and deployment unit.

This page brings the services and their environment deployments together. The same service and deployment definitions are used by the [architecture overview](../Architecture.md).

## Services involved

:: Namotion.ArchDocs.ServiceTable Path=/
::

## Staging and production

Both environments include the Orders and Payments deployment units. Their installed versions and URLs can be recorded independently for each deployment.

:: Namotion.ArchDocs.DeploymentMatrix Path=/
::

## Release considerations

The Orders API and worker are grouped into one deployment unit. Payments can be released separately. A release spanning both services still consists of two deployment units, each with an installation in the selected environment.
