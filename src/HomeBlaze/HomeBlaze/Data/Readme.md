---
title: HomeBlaze v2
navTitle: Home
order: 0
---

# HomeBlaze v2

Welcome to HomeBlaze v2, a complete rewrite built on Namotion.Interceptor.

## Features

- **File-system-backed state model** - Your data structure mirrors the file system
- **Live tracking** - All property changes are automatically tracked
- **Auto-persistence** - Configuration changes are saved automatically
- **Protocol exposure** - Access your data via OPC UA and MQTT

## Getting Started

1. Edit `root.json` to configure your storage path
2. Add JSON files with `"Type"` discriminator to create subjects
3. Add markdown files for documentation
4. Browse everything in the UI

## Sample Files

- `motor.json` - A sample motor with simulated sensors
- `notes/` - Documentation folder (you're reading from here!)
