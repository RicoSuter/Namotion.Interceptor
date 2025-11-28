# Cache Invalidation via Lifecycle Events

## Status Summary

### COMPLETED
- [x] Step 1: Add events to LifecycleInterceptor (SubjectAttached/SubjectDetached, fire outside lock)
- [x] Step 2: Make ReferenceCountKey internal in LifecycleInterceptor
- [x] Step 3: Expose ReferenceCount on RegisteredSubject (via GetReferenceCount extension method)
- [x] Step 4: Add TryGetLifecycleInterceptor extension method
- [x] Step 5: Update MqttSubjectClientSource with lifecycle events
- [x] Step 6: Update MqttSubjectServerBackgroundService with lifecycle events
- [x] Remove TODO(memory) comments from MQTT sources
- [x] Build and test - all tests pass

### REMAINING (Optional)
- [ ] Step 7: Add unit tests for cache invalidation
- [ ] OPC UA analysis - see notes below

### OPC UA Analysis

After analyzing the OPC UA sources, lifecycle events are **NOT required** for the following reasons:

1. **SubscriptionManager** (`_monitoredItems`, `_subscriptions`):
   - Items are tied to OPC UA subscription lifecycle
   - Cleanup happens via `FilterOutFailedMonitoredItemsAsync` and `Dispose`
   - Not a memory leak - managed by subscription/connection lifecycle

2. **PollingManager** (`_pollingItems`):
   - Items added when subscriptions fail, removed via `RemoveItem` or `Dispose`
   - Tied to polling lifecycle, not subject lifecycle
   - Not a memory leak - managed by polling lifecycle

3. **CustomNodeManager** (`_subjects`):
   - Creates address space once at server startup
   - OPC UA server doesn't support dynamic subject changes at runtime
   - Nodes remain until server shutdown (by design)
   - Not a runtime memory leak - static address space

---

## Problem

The MQTT client and server maintain caches that map between `PropertyReference` and MQTT topics/paths:

**MqttSubjectClientSource:**
- `_topicToProperty: ConcurrentDictionary<string, PropertyReference?>`
- `_propertyToTopic: ConcurrentDictionary<PropertyReference, string?>`

**MqttSubjectServerBackgroundService:**
- `_propertyToTopic: ConcurrentDictionary<PropertyReference, string?>`
- `_pathToProperty: ConcurrentDictionary<string, PropertyReference?>`

When subjects are detached from the object graph, cache entries become stale but are not cleaned up until disposal. This leads to memory leaks in long-running services with dynamic object graphs.

## Solution

Add `SubjectAttached` and `SubjectDetached` events to `LifecycleInterceptor` that sources can subscribe to for cache invalidation.

## Design Decisions

### Events vs ILifecycleHandler

**Decision**: Use events (not `ILifecycleHandler`) for cache invalidation.

**Rationale**:
- `ILifecycleHandler` implementations are "static" - registered at startup via DI
- Sources are "dynamic" - created/disposed at runtime based on configuration
- Events are better suited for dynamic subscribers that come and go

### Events Fire Outside Lock

**Decision**: Collect events inside lock, invoke outside lock.

**Rationale**:
- `ILifecycleHandler` methods stay inside lock (existing behavior, handlers are fast)
- Event invocation moves outside lock to avoid increasing lock hold time
- Handlers iterate properties and do dictionary operations - microseconds, not nanoseconds
- Lock hold time must stay < 1μs for high-throughput industrial scenarios

**Implementation**: Uses thread-static pooling for change lists to avoid allocations.

### Consumer-Side Attachment Check

**Problem**: Race condition where cache is repopulated after subject detachment.

**Solution**: Sources check if subject is still attached BEFORE cache lookup (never cache detachment state).

```csharp
private string? TryGetTopicForProperty(PropertyReference propertyReference, RegisteredSubjectProperty property)
{
    // Always resolve fresh - don't cache detachment state
    var registeredSubject = propertyReference.Subject.TryGetRegisteredSubject();
    if (registeredSubject is null || registeredSubject.ReferenceCount <= 0)
    {
        return null;  // Don't touch cache for detached subjects
    }

    return _propertyToTopic.GetOrAdd(propertyReference, ...);
}
```

### No Exception Handling in LifecycleInterceptor

The `LifecycleInterceptor` does NOT wrap handler/event invocations in try-catch:
- Exception handling has performance overhead
- Clear contract: `ILifecycleHandler` implementations and event handlers MUST be exception-free
- Handlers must handle their own exceptions internally if needed
- An unhandled exception in a handler is a bug that should surface immediately

## Files Modified

### Namotion.Interceptor.Tracking

**LifecycleInterceptor.cs:**
- Added `SubjectAttached` and `SubjectDetached` events
- Events fire outside lock using thread-static pooled change lists
- `ReferenceCountKey` changed to `internal`

**LifecycleInterceptorExtensions.cs:**
- Added `TryGetLifecycleInterceptor(this IInterceptorSubjectContext)` extension
- Added `GetReferenceCount(this IInterceptorSubject)` extension

### Namotion.Interceptor.Registry

**RegisteredSubject.cs:**
- Added `ReferenceCount` property (delegates to `Subject.GetReferenceCount()`)

### Namotion.Interceptor.Mqtt

**MqttSubjectClientSource.cs:**
- Added `_lifecycleInterceptor` field
- Subscribe to `SubjectDetached` event in `StartListeningAsync`
- `TryGetTopicForProperty` checks `ReferenceCount` BEFORE cache lookup
- `TryGetPropertyForTopic` checks `ReferenceCount` AFTER cache lookup
- Added `OnSubjectDetached` handler to clean up caches
- Unsubscribe in `DisposeAsync`
- Removed `// TODO(memory)` comment

**MqttSubjectServerBackgroundService.cs:**
- Same pattern as client source
- Added `_lifecycleInterceptor` field
- Subscribe/unsubscribe to lifecycle events
- `TryGetTopicForProperty` and `TryGetPropertyForTopic` check `ReferenceCount`
- Added `OnSubjectDetached` handler to clean up caches
- Removed `// TODO(memory)` comment
