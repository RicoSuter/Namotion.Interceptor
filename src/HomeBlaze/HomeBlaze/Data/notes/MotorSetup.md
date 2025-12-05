# Motor Setup Guide

This guide explains how to configure the sample motor.

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

```json
{
    "Type": "HomeBlaze.Core.Subjects.Motor",
    "Name": "Cooling Fan",
    "TargetSpeed": 1500,
    "SimulationInterval": "00:00:01"
}
```
