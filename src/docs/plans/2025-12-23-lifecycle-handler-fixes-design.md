# Lifecycle Handler Fixes

## Overview

Fixes for issues found during code review of the `feature/lifecycle-and-hosting-improvements` branch, specifically in how handlers consume the new `IsFirstAttach`/`IsLastDetach` flags.

---

## Fix 1: Remove incorrect DetachHostedService call for subject-as-service

**File:** `src/Namotion.Interceptor.Hosting/HostedServiceHandler.cs`
**Line:** 51

**Current code:**
```csharp
if (change.Subject is IHostedService hostedService)
{
    DetachHostedService(hostedService);
    change.Subject.DetachHostedService(hostedService);  // ← Remove this line
}
```

**Problem:** When `change.Subject` IS a hosted service, calling `change.Subject.DetachHostedService(hostedService)` tries to remove the subject from its own "attached services" list. The extension method is for services attached TO a subject, not for when the subject IS a hosted service.

**Fix:** Remove line 51.

**Status:** Approved

---

## Fix 2: Simplify redundant DetachHostedService calls in loop

**File:** `src/Namotion.Interceptor.Hosting/HostedServiceHandler.cs`
**Lines:** 54-58

**Current code:**
```csharp
foreach (var attachedHostedService in change.Subject.GetAttachedHostedServices().ToList())
{
    DetachHostedService(attachedHostedService);
    change.Subject.DetachHostedService(attachedHostedService);
}
```

**Problem:** `DetachHostedService` is called twice - redundant.

**Fix:** Use extension method only (handles both data cleanup AND stopping):
```csharp
foreach (var attachedHostedService in change.Subject.GetAttachedHostedServices().ToList())
{
    change.Subject.DetachHostedService(attachedHostedService);
}
```

**Why different logic for subject-as-service vs attached services:**
- Subject IS a hosted service → use internal `DetachHostedService` directly (subject isn't in its own attached list)
- Attached services → use extension method (cleans up data + stops service)

**Status:** Approved

---

## Fix 3: Fix ContextInheritanceHandler.DetachSubject asymmetry

**File:** `src/Namotion.Interceptor.Tracking/Lifecycle/ContextInheritanceHandler.cs`

**Current code:**
```csharp
// AttachSubject - only on FIRST property attachment
if (change is { ReferenceCount: 1, Property: not null })
{
    change.Subject.Context.AddFallbackContext(parent.Context);
}

// DetachSubject - on EVERY property detach (BUG)
if (change.Property is not null)
{
    change.Subject.Context.RemoveFallbackContext(parent.Context);
}
```

**Problem:** Asymmetric behavior causes bug in multi-property scenarios:
```csharp
parent.Mother = child;  // RefCount=1, adds fallback ✅
parent.Father = child;  // RefCount=2, no-op ✅
parent.Mother = null;   // RefCount=1, REMOVES fallback ❌ (still attached via Father!)
```

**Fix:** Make symmetric - only remove on last property detachment:
```csharp
public void DetachSubject(SubjectLifecycleChange change)
{
    if (change is { ReferenceCount: 0, Property: not null })
    {
        var parent = change.Property.Value.Subject;
        change.Subject.Context.RemoveFallbackContext(parent.Context);
    }
}
```

**Behavior after fix:**
| Operation | Condition | When fires |
|-----------|-----------|------------|
| Add fallback | `ReferenceCount: 1, Property: not null` | First property attachment |
| Remove fallback | `ReferenceCount: 0, Property: not null` | Last property detachment |

**Status:** Approved

---

## New Test: Multi-property context inheritance

**File:** `src/Namotion.Interceptor.Tracking.Tests/ContextInheritanceHandlerTests.cs`

**Test:** Verify context is retained when subject is still attached via another property.

```csharp
[Fact]
public void WhenSubjectAttachedToMultipleProperties_ThenContextRetainedUntilLastDetach()
{
    // Arrange
    var context = InterceptorSubjectContext
        .Create()
        .WithContextInheritance();

    var parent = new Person(context) { FirstName = "Parent" };
    var child = new Person { FirstName = "Child" };

    // Act - attach to two properties
    parent.Mother = child;  // RefCount=1
    parent.Father = child;  // RefCount=2

    // Assert - child has context
    Assert.Equal(
        context.GetServices<ILifecycleInterceptor>().FirstOrDefault(),
        child.GetServices<ILifecycleInterceptor>().FirstOrDefault());

    // Act - detach from first property
    parent.Mother = null;  // RefCount=1, still attached via Father

    // Assert - child STILL has context (this is the bug fix)
    Assert.Equal(
        context.GetServices<ILifecycleInterceptor>().FirstOrDefault(),
        child.GetServices<ILifecycleInterceptor>().FirstOrDefault());

    // Act - detach from last property
    parent.Father = null;  // RefCount=0

    // Assert - child no longer has context
    Assert.Empty(child.GetServices<ILifecycleInterceptor>());
}
```

**Status:** To be added

---
