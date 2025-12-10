---
title: Motor Setup Guide
navTitle: Motor Setup
order: 1
---

# Motor Setup Guide

> **Note:** This guide refers to the motor examples in the `demo/` folder. See [Demo Setup Guide](../demo/setup.md) for a complete overview.

This guide explains how to configure motor subjects.

## Configuration Properties

| Property | Type | Description |
|----------|------|-------------|
| `Name` | string | Display name for the motor |
| `TargetSpeed` | int | Target RPM (0-3000 recommended) |
| `SimulationInterval` | TimeSpan | Update interval for simulation |

## Live Properties

These properties are updated in real-time by the simulation:

- **CurrentSpeed** - Current RPM, approaches TargetSpeed gradually
- **Temperature** - Simulated temperature based on speed
- **Status** - Running, Stopped, Starting, etc.

## Derived Properties

- **SpeedDelta** - Difference between target and current speed
- **IsAtTargetSpeed** - True when within 50 RPM of target

## Example Configuration

See the demo files for real examples:
- `demo/conveyor.json` - Slow speed (600 RPM) for material handling
- `demo/exhaust-fan.json` - Medium speed (1500 RPM) for ventilation
- `demo/compressor.json` - High speed (3000 RPM) for air compression

```json
{
    "Type": "HomeBlaze.Samples.Motor",
    "Name": "Cooling Fan",
    "TargetSpeed": 1800,
    "SimulationInterval": "00:00:01"
}
```

👉 **[Explore all demo motors](../demo/setup.md)**

