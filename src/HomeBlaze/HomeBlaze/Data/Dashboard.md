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
| **Conveyor** | {{ Root.Children[Demo].Children[Conveyor.json].Temperature }} | {{ Root.Children[Demo].Children[Conveyor.json].CurrentSpeed }} | {{ Root.Children[Demo].Children[Conveyor.json].TargetSpeed }} |
| **Water Pump** | {{ Root.Children[Demo].Children[WaterPump.json].Temperature }} | {{ Root.Children[Demo].Children[WaterPump.json].CurrentSpeed }} | {{ Root.Children[Demo].Children[WaterPump.json].TargetSpeed }} |
| **Cooling Fan** | {{ Root.Children[Demo].Children[CoolingFan.json].Temperature }} | {{ Root.Children[Demo].Children[CoolingFan.json].CurrentSpeed }} | {{ Root.Children[Demo].Children[CoolingFan.json].TargetSpeed }} |
| **Compressor** | {{ Root.Children[Demo].Children[Compressor.json].Temperature }} | {{ Root.Children[Demo].Children[Compressor.json].CurrentSpeed }} | {{ Root.Children[Demo].Children[Compressor.json].TargetSpeed }} |
| **Exhaust Fan** | {{ Root.Children[Demo].Children[ExhaustFan.json].Temperature }} | {{ Root.Children[Demo].Children[ExhaustFan.json].CurrentSpeed }} | {{ Root.Children[Demo].Children[ExhaustFan.json].TargetSpeed }} |
