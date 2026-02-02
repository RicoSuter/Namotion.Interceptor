# OPC UA Graph Sync - Changed Files Summary

Total: 77 files changed

## Core Connectors Layer (Generic)

| File | Purpose | Lines |
|------|---------|-------|
| `GraphChangePublisher.cs` | **NEW** - Base class for publishing structural changes (add/remove subjects) | ~115 |
| `GraphChangeApplier.cs` | **NEW** - Applies external changes to C# model (symmetric to Publisher) | ~200 |
| `ConnectorSubjectMapping.cs` | **NEW** - Bidirectional subject ↔ external ID mapping | ~100 |
| `ConnectorReferenceCounter.cs` | **NEW** - Reference counting for shared subjects | ~80 |

## OPC UA Client - Graph Sync

| File | Purpose | Lines |
|------|---------|-------|
| `OpcUaClientGraphChangeReceiver.cs` | **NEW** - Receives OPC UA → C# model changes (ModelChangeEvents/resync) | ~1340 |
| `OpcUaClientGraphChangeSender.cs` | **NEW** - Sends C# model → OPC UA changes (AddNodes/MonitoredItems) | ~590 |
| `OpcUaClientGraphChangeDispatcher.cs` | **NEW** - Dispatches change processing to receiver | ~100 |
| `OpcUaClientGraphChangeTrigger.cs` | **NEW** - Triggers resync when ModelChangeEvents arrive | ~50 |
| `OpcUaClientPropertyWriter.cs` | **NEW** - Writes property values to server | ~100 |
| `OpcUaSubjectClientSource.cs` | Modified - Integrates graph sync components | ~500 |
| `OpcUaSubjectLoader.cs` | Modified - Subject loading and NodeId registration | ~300 |

## OPC UA Server - Graph Sync

| File | Purpose | Lines |
|------|---------|-------|
| `OpcUaServerGraphChangeReceiver.cs` | **NEW** - Receives external AddNodes/DeleteNodes → C# model | ~495 |
| `OpcUaServerGraphChangeSender.cs` | **NEW** - Sends C# model → OPC UA nodes (creates/removes nodes) | ~45 |
| `OpcUaServerGraphChangePublisher.cs` | **NEW** - Publishes ModelChangeEvents for structural changes | ~100 |
| `OpcUaServerNodeCreator.cs` | **NEW** - Creates OPC UA nodes from subjects | ~300 |
| `OpcUaServerExternalNodeValidator.cs` | **NEW** - Validates external node management requests | ~50 |
| `CustomNodeManager.cs` | Modified - Dynamic node management, reindexing | ~600 |
| `OpcUaSubjectServer.cs` | Modified - Integrates graph sync components | ~400 |

## Shared/Utility

| File | Purpose |
|------|---------|
| `OpcUaHelper.cs` | **NEW** - Browse, find parent/child, parse collection indices |
| `OpcUaTypeRegistry.cs` | **NEW** - TypeDefinition ↔ C# type mapping |
| `CollectionNodeStructure.cs` | **NEW** - Enum for Container vs Flat collection modes |

## Architecture

**Symmetric Design:**
```
C# Model Changes           OPC UA Changes
       ↓                         ↓
GraphChangePublisher      GraphChangeApplier
       ↓                         ↓
   (abstract)                 (concrete)
       ↓                         ↓
OpcUa*GraphChangeSender   OpcUa*GraphChangeReceiver
```

**Client Side:**
- `OpcUaClientGraphChangeSender` → Processes C# changes, calls AddNodes/creates MonitoredItems
- `OpcUaClientGraphChangeReceiver` → Processes ModelChangeEvents/resync, updates C# model

**Server Side:**
- `OpcUaServerGraphChangeSender` → Processes C# changes, creates/removes OPC UA nodes
- `OpcUaServerGraphChangeReceiver` → Processes AddNodes/DeleteNodes from clients
