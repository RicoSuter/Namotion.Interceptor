# Cache Invalidation via Lifecycle Events

## Status Summary

### COMPLETED
- [x] Step 1: Add events to LifecycleInterceptor (SubjectAttached/SubjectDetached, fire outside lock)
- [x] Step 2: ~~Make ReferenceCountKey internal~~ → Changed to private (implementation detail)
- [x] Step 3: Expose ReferenceCount on RegisteredSubject (via GetReferenceCount extension method)
- [x] Step 4: Add TryGetLifecycleInterceptor extension method
- [x] Step 5: Update MqttSubjectClientSource with lifecycle events
- [x] Step 6: Update MqttSubjectServerBackgroundService with lifecycle events
- [x] Remove TODO(memory) comments from MQTT sources
- [x] Build and test - all tests pass

### POST-REVIEW FIXES (PR #111 Review)
- [x] Fix race condition in cache methods (check AFTER GetOrAdd, not BEFORE)
- [x] Add IncrementReferenceCount/DecrementReferenceCount extension methods (avoid key duplication)
- [x] Remove stale cache entries when detecting detached subjects (memory cleanup)
- [x] Move events inside lock (same semantics as ILifecycleHandler, eliminates collector lists)

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

### Events Fire Inside Lock

**Decision**: Events fire inside the lock, same as `ILifecycleHandler` methods.

**Rationale**:
- Consistent semantics with `ILifecycleHandler` - both invoked inside lock
- Strong ordering guarantees - no race between attach/detach events
- Simpler code - no collector lists, no delegate capture, no post-lock iteration
- Event handlers are expected to be fast (dictionary operations - microseconds)

**Contract**: Event handlers must be exception-free and fast (invoked inside lock).

### Consumer-Side Attachment Check

**Problem**: Race condition where cache is populated during/after subject detachment.

**Initial Approach (FLAWED)**: Check BEFORE cache lookup.
- Race: Thread A checks ReferenceCount > 0, Thread B detaches subject, Thread A adds to cache
- Result: Stale entry in cache that won't be cleaned (OnSubjectDetached already ran)

**Solution**: Check AFTER cache lookup (not before).
- Even if a stale entry is cached, we won't USE it
- The value is validated before being returned
- OnSubjectDetached provides best-effort cleanup
- Stale entries are harmless (unused until GC or next cleanup)

```csharp
private string? TryGetTopicForProperty(PropertyReference propertyReference, RegisteredSubjectProperty property)
{
    // Get or add to cache (may add stale entry - that's OK)
    var topic = _propertyToTopic.GetOrAdd(propertyReference, ...);

    // Check AFTER: validate subject is still attached before using cached value
    var registeredSubject = propertyReference.Subject.TryGetRegisteredSubject();
    if (registeredSubject is null || registeredSubject.ReferenceCount <= 0)
    {
        // Remove stale entry to prevent memory leak
        _propertyToTopic.TryRemove(propertyReference, out _);
        return null;  // Don't use cached value for detached subjects
    }

    return topic;
}
```

**Thread-Safety Guarantee**: Even with concurrent attach/detach, we never return stale data.
**Memory Cleanup**: Stale entries are removed immediately when detected, supplementing the OnSubjectDetached handler.

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
- Events fire inside lock (same semantics as `ILifecycleHandler`)
- Removed `ReferenceCountKey` constant (moved to extension methods)
- Uses `IncrementReferenceCount()`/`DecrementReferenceCount()` extension methods
- Simplified: no collector lists, no delegate capture, direct `?.Invoke()` inside lock

**LifecycleInterceptorExtensions.cs:**
- Added `TryGetLifecycleInterceptor(this IInterceptorSubjectContext)` extension
- Added `GetReferenceCount(this IInterceptorSubject)` extension
- Added `IncrementReferenceCount(this IInterceptorSubject)` internal extension (avoids key duplication)
- Added `DecrementReferenceCount(this IInterceptorSubject)` internal extension (avoids key duplication)

### Namotion.Interceptor.Registry

**RegisteredSubject.cs:**
- Added `ReferenceCount` property (delegates to `Subject.GetReferenceCount()`)

### Namotion.Interceptor.Mqtt

**MqttSubjectClientSource.cs:**
- Added `_lifecycleInterceptor` field
- Subscribe to `SubjectDetached` event in `StartListeningAsync`
- `TryGetTopicForProperty` checks `ReferenceCount` AFTER cache lookup + removes stale entries (race-free)
- `TryGetPropertyForTopic` checks `ReferenceCount` AFTER cache lookup + removes stale entries (race-free)
- Added `OnSubjectDetached` handler to clean up caches (best-effort cleanup)
- Unsubscribe in `DisposeAsync`
- Removed `// TODO(memory)` comment

**MqttSubjectServerBackgroundService.cs:**
- Same pattern as client source
- Added `_lifecycleInterceptor` field
- Subscribe/unsubscribe to lifecycle events
- Both cache methods check `ReferenceCount` AFTER cache lookup + remove stale entries (race-free)
- Added `OnSubjectDetached` handler to clean up caches (best-effort cleanup)
- Removed `// TODO(memory)` comment
