---
title: Dashboard
icon: Dashboard
location: AppBar
position: 10
alignment: Left
---

# Dashboard

Welcome to your HomeBlaze dashboard.

## Motor Status

| Motor | Temperature | Speed | Target |
|-------|-------------|-------|--------|
| **Conveyor** | {{ Root.Children[Demo].Children[Conveyor].Temperature }} | {{ Root.Children[Demo].Children[Conveyor].CurrentSpeed }} | {{ Root.Children[Demo].Children[Conveyor].TargetSpeed }} |
| **Water Pump** | {{ Root.Children[Demo].Children[WaterPump].Temperature }} | {{ Root.Children[Demo].Children[WaterPump].CurrentSpeed }} | {{ Root.Children[Demo].Children[WaterPump].TargetSpeed }} |
| **Cooling Fan** | {{ Root.Children[Demo].Children[CoolingFan].Temperature }} | {{ Root.Children[Demo].Children[CoolingFan].CurrentSpeed }} | {{ Root.Children[Demo].Children[CoolingFan].TargetSpeed }} |
| **Compressor** | {{ Root.Children[Demo].Children[Compressor].Temperature }} | {{ Root.Children[Demo].Children[Compressor].CurrentSpeed }} | {{ Root.Children[Demo].Children[Compressor].TargetSpeed }} |
| **Exhaust Fan** | {{ Root.Children[Demo].Children[ExhaustFan].Temperature }} | {{ Root.Children[Demo].Children[ExhaustFan].CurrentSpeed }} | {{ Root.Children[Demo].Children[ExhaustFan].TargetSpeed }} |
