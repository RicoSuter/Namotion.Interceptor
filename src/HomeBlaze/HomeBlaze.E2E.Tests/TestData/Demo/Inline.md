---
title: Live Widgets Demo
navTitle: Widgets Demo
---

# Live Widgets Demo

This page demonstrates embedded subjects and live expressions.

## Embedded Motor Widget

Motor:

:: HomeBlaze.Samples.Motor#mymotor Name=MyMotor TargetSpeed=3000 SimulationInterval=00:00:01.500
::

## Live Expression

Temperature: {{ mymotor/Temperature }}

Speed: {{ mymotor/CurrentSpeed }}
