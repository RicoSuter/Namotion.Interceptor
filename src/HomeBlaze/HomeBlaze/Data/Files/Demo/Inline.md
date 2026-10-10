---
title: Live Widgets Demo
navTitle: Widgets Demo
position: 5
---

# Live Widgets Demo

This page demonstrates embedded subjects and live expressions.

## Embedded Motor Widget

Motor:

:::HomeBlaze.Samples.Motor#mymotor name=MyMotor targetSpeed=3000 simulationInterval=00:00:01.500
:::

## Live Expression

Temperature: {{ mymotor/Temperature }}

Speed: {{ mymotor/CurrentSpeed }}

## Widget Reference

Reference an existing subject's widget by path instead of embedding it inline:

:::HomeBlaze.Components.Widget#conveyorRef path=../../Conveyor
:::

For more information on creating pages with embedded subjects, see [Pages Documentation](../Docs/administration/pages.md).