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
