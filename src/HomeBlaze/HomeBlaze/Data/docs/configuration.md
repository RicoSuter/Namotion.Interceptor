---
title: Configuration Guide
navTitle: Configuration
order: 2
---

# Configuration

## root.json

The `root.json` file defines the storage location:

```json
{
    "Type": "HomeBlaze.Storage.FluentStorageContainer",
    "Path": "./Data"
}
```

## Adding Subjects

Create JSON files with a `Type` discriminator to add subjects.

### Example: Adding a Motor

Create a file like `demo/my-motor.json`:

```json
{
    "type": "HomeBlaze.Samples.Motor",
    "name": "My Custom Motor",
    "targetSpeed": 2000,
    "simulationInterval": "00:00:01"
}
```

The motor will automatically appear in the object browser!

## Demo Configuration

The demo includes 5 pre-configured motors in the `demo/` folder:
- Conveyor Belt (600 RPM)
- Exhaust Fan (1,500 RPM)
- Cooling Fan (1,800 RPM)
- Water Pump (2,400 RPM)
- Compressor (3,000 RPM)

👉 **[View Demo Setup Guide](../demo/setup.md)**

