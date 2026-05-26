---
title: Runtime Scenarios
navTitle: Runtime Scenarios
position: 3
---

# Runtime Scenarios

This document describes what happens at runtime for key system flows. Each scenario walks through the component interactions step by step.

## Startup

```
Program.cs
  -> AddHomeBlazeHost() registers all services
  -> RootManager loads root.json
  -> FluentStorageContainer scans storage
  -> Subjects created with InterceptorSubjectContext
     -> Source generator interceptors attached
     -> Registry populated (if WithRegistry)
     -> Lifecycle callbacks fire (if WithLifecycle)
  -> Blazor Server starts, SignalR circuit ready
```

## Property Change Propagation

When a property value changes on a subject:

```
subject.Temperature = 23.5
  -> Generated setter calls WriteProperty on context
  -> IWriteInterceptor chain executes:
     1. PropertyChangedInterceptor records old value
     2. DerivedPropertyInterceptor marks dependents dirty
     3. Registry updates tracked value
  -> PropertyChanged event fires
  -> Blazor UI re-renders bound components
  -> If WebSocket connector attached:
     -> SubjectUpdateFactory builds incremental update
     -> Property serialized as { kind: "Value", value: 23.5 }
     -> Sent to connected peers
```

## Satellite Connects to Central (WebSocket)

```
Satellite                              Central
  |                                      |
  |--- Hello (satelliteId, token) ------>|
  |                                      | Authenticates, creates session
  |<-- Welcome (full subject snapshot) --|
  |                                      |
  | SubjectUpdateApplier processes snapshot:
  |   For each subject in update:
  |     -> SubjectFactory creates instance
  |        (concrete if type available,
  |         dynamic proxy otherwise)
  |     -> Properties applied from wire
  |     -> Registry attributes applied
  |   Subject graph attached to central's tree
  |                                      |
  |--- SubjectUpdate (incremental) ----->|
  |                                      | SubjectUpdateApplier applies delta
  |<-- SubjectUpdate (incremental) ------|
  |  SubjectUpdateApplier applies delta  |
```

## Subject Creation from Storage

When a JSON file is loaded from storage:

```
FluentStorageContainer.ScanAsync()
  -> Finds demo/motor.json
  -> ConfigurableSubjectSerializer.DeserializeAsync()
     -> Reads $type discriminator
     -> SubjectTypeRegistry resolves type name to CLR type
     -> Creates instance via InterceptorSubjectContext
     -> Deserializes [Configuration] properties from JSON
  -> Subject attached as child of folder
  -> IConfigurable.ApplyConfigurationAsync() called
```

## Operation Invocation

When an operation is executed from the UI:

```
User clicks "Emergency Stop" button
  -> SubjectPropertyPanel reads [Operation] metadata from registry (MethodMetadata)
  -> If RequiresConfirmation: shows confirmation dialog
  -> MethodMetadata.InvokeAsync(parameters)
     -> Parameter conversion (string inputs -> typed values)
     -> Method invoked via delegate
     -> Result returned directly (or null for void)
  -> UI refreshes to reflect state changes
```

## OPC UA Subject Discovery

```
OpcUaClient connects to OPC UA server
  -> Browses server address space
  -> For each discovered node:
     -> OpcUaSubjectFactory.CreateSubject()
        (concrete if type available, dynamic otherwise)
     -> Properties mapped from OPC UA node attributes
     -> Registry attributes set (state metadata)
  -> Subscriptions created for monitored items
  -> Value changes flow through property interception
```
