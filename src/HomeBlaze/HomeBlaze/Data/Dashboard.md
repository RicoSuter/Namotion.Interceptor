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
| **Conveyor** | {{ Root.Children[demo].Children[conveyor.json].Temperature }} | {{ Root.Children[demo].Children[conveyor.json].CurrentSpeed }} | {{ Root.Children[demo].Children[conveyor.json].TargetSpeed }} |
| **Water Pump** | {{ Root.Children[demo].Children[water-pump.json].Temperature }} | {{ Root.Children[demo].Children[water-pump.json].CurrentSpeed }} | {{ Root.Children[demo].Children[water-pump.json].TargetSpeed }} |
| **Cooling Fan** | {{ Root.Children[demo].Children[cooling-fan.json].Temperature }} | {{ Root.Children[demo].Children[cooling-fan.json].CurrentSpeed }} | {{ Root.Children[demo].Children[cooling-fan.json].TargetSpeed }} |
| **Compressor** | {{ Root.Children[demo].Children[compressor.json].Temperature }} | {{ Root.Children[demo].Children[compressor.json].CurrentSpeed }} | {{ Root.Children[demo].Children[compressor.json].TargetSpeed }} |
| **Exhaust Fan** | {{ Root.Children[demo].Children[exhaust-fan.json].Temperature }} | {{ Root.Children[demo].Children[exhaust-fan.json].CurrentSpeed }} | {{ Root.Children[demo].Children[exhaust-fan.json].TargetSpeed }} |
