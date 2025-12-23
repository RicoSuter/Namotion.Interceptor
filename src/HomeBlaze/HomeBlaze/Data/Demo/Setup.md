---
title: Demo Setup Guide
navTitle: Demo Setup
position: 1
---

# Demo Setup Guide

This demo showcases a simple industrial facility monitoring system with 5 motors representing common industrial equipment.

## Demo Overview

The demo simulates a small factory with the following equipment (live data):

| Equipment | Current Speed | Target Speed | Temperature | Status |
|-----------|---------------|--------------|-------------|--------|
| **Conveyor Belt** | {{ Root.Children[Demo].Children[Conveyor].CurrentSpeed }} | {{ Root.Children[Demo].Children[Conveyor].TargetSpeed }} | {{ Root.Children[Demo].Children[Conveyor].Temperature }} | {{ Root.Children[Demo].Children[Conveyor].Status }} |
| **Exhaust Fan** | {{ Root.Children[Demo].Children[ExhaustFan].CurrentSpeed }} | {{ Root.Children[Demo].Children[ExhaustFan].TargetSpeed }} | {{ Root.Children[Demo].Children[ExhaustFan].Temperature }} | {{ Root.Children[Demo].Children[ExhaustFan].Status }} |
| **Cooling Fan** | {{ Root.Children[Demo].Children[CoolingFan].CurrentSpeed }} | {{ Root.Children[Demo].Children[CoolingFan].TargetSpeed }} | {{ Root.Children[Demo].Children[CoolingFan].Temperature }} | {{ Root.Children[Demo].Children[CoolingFan].Status }} |
| **Water Pump** | {{ Root.Children[Demo].Children[WaterPump].CurrentSpeed }} | {{ Root.Children[Demo].Children[WaterPump].TargetSpeed }} | {{ Root.Children[Demo].Children[WaterPump].Temperature }} | {{ Root.Children[Demo].Children[WaterPump].Status }} |
| **Compressor** | {{ Root.Children[Demo].Children[Compressor].CurrentSpeed }} | {{ Root.Children[Demo].Children[Compressor].TargetSpeed }} | {{ Root.Children[Demo].Children[Compressor].Temperature }} | {{ Root.Children[Demo].Children[Compressor].Status }} |

## Motor Properties

Each motor demonstrates the full range of HomeBlaze features:

### Configuration Properties
These properties are persisted to JSON files and can be edited:
- **Name** - Display name for the motor
- **TargetSpeed** - Target RPM (revolutions per minute)
- **SimulationInterval** - How often the simulation updates

### State Properties
These properties are updated in real-time during simulation:
- **CurrentSpeed** - Current RPM (gradually approaches target)
- **Temperature** - Simulated temperature based on speed
- **Status** - Running state (Stopped, Starting, Running, Stopping)

### Derived Properties
These properties auto-calculate when dependencies change:
- **SpeedDelta** - Difference between target and current speed
- **IsAtTargetSpeed** - True when within 50 RPM of target

## File Structure

```
Data/
  demo/
    Conveyor.json         # 600 RPM conveyor
    ExhaustFan.json      # 1500 RPM exhaust system
    CoolingFan.json      # 1800 RPM HVAC system
    WaterPump.json       # 2400 RPM water circulation
    Compressor.json       # 3000 RPM compressed air
    Setup.md             # This file
```

## Exploring the Demo

### 1. Browse the Object Graph
Navigate to **Browser** to see the live object hierarchy. Each motor appears as a tracked subject with real-time property updates.

### 2. View Motor Details
Click on any motor to see:
- Configuration settings
- Current state values
- Derived calculations
- Property change history (if tracking enabled)

### 3. Edit Configuration
Click the **Edit** button on any motor to modify:
- Target speed (0-3500 RPM recommended)
- Simulation interval (faster = more updates)
- Motor name

Changes save immediately to the JSON file!

### 4. Watch Real-Time Updates
Observe how:
- CurrentSpeed gradually ramps to TargetSpeed
- Temperature increases with speed
- Status transitions (Stopped → Starting → Running)
- Derived properties update automatically

### 5. External File Editing
Try editing a JSON file externally:
```bash
# Edit demo/Conveyor.json and change targetSpeed to 1000
# Watch the UI update automatically via FileSystemWatcher!
```

## Typical Use Cases

### Scenario 1: Speed Adjustment
1. Navigate to the Conveyor Belt motor
2. Click Edit and change TargetSpeed from 600 to 1000
3. Watch CurrentSpeed ramp up over several seconds
4. Observe Temperature increase proportionally

### Scenario 2: System Monitoring
1. Open the Browser view
2. Expand the demo folder
3. See all 5 motors with live state updates
4. Use filters to show only running motors

### Scenario 3: Protocol Exposure (Coming Soon)
Protocol exposure is planned for future releases:
- **OPC UA** - Browse nodes via OPC UA server
- **MQTT** - Subscribe to state changes
- **GraphQL** - Query subjects via GraphQL API

## Adding More Motors

To add a new motor to the demo:

1. Create a new JSON file in the `demo` folder:
```json
{
  "$type": "HomeBlaze.Samples.Motor",
  "name": "My Custom Motor",
  "targetSpeed": 2000,
  "simulationInterval": "00:00:01"
}
```

2. The motor appears immediately in the UI (file watching)
3. It's automatically tracked and exposed via protocols

## Removing the Demo

To start with a clean slate:
1. Delete the `demo` folder
2. Create your own subject files
3. Add custom subject types by implementing `IInterceptorSubject`

## Next Steps

- Review [Building Custom Subjects](../docs/BuildingSubjects.md) to create your own subject types
- Check out [Architecture](../Docs/Architecture.md) to understand the system design
- Explore the source code in `HomeBlaze.Samples/Motor.cs`

---

*The demo data resets to defaults on application restart unless you commit your changes to source control.*

