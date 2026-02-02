# Issue #181: ISubjectPropertyInitializer Not Called for Dynamic Properties

## Overview

Fix the bug where `ISubjectPropertyInitializer` implementations are not invoked when properties are added dynamically via `AddProperty()` or `AddDerivedProperty()`.

**GitHub Issue:** https://github.com/RicoSuter/Namotion.Interceptor/issues/181

## Problem

When a property is added dynamically (e.g., via `ILifecycleHandler` during context attach), registered `ISubjectPropertyInitializer` services are not called for the new property, even though they are called for statically generated properties.

## Root Cause Hypothesis

The flow for dynamic properties is:
1. `RegisteredSubject.AddProperty()` calls `Subject.AddProperties()`
2. `AddPropertyInternal()` calls `Subject.AttachSubjectProperty()`
3. `AttachSubjectProperty()` calls `IPropertyLifecycleHandler.AttachProperty()` on all handlers
4. `SubjectRegistry.AttachProperty()` should call initializers

**Likely Issue:** In `SubjectRegistry.AttachProperty()`, the `TryGetRegisteredProperty()` lookup may fail for dynamic properties because the property isn't yet visible in the registry's `_knownSubjects` dictionary at the time of the call.

---

## Task 1: Create Regression Tests

**File:** `src/Namotion.Interceptor.Registry.Tests/DynamicPropertyInitializerTests.cs`

```csharp
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Tests.Models;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Registry.Tests;

public class DynamicPropertyInitializerTests
{
    [Fact]
    public void WhenAddingDynamicProperty_ThenInitializersAreCalled()
    {
        // Arrange
        var initializedProperties = new List<string>();
        var initializer = new TestPropertyInitializer(p => initializedProperties.Add(p.Name));

        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithService<ISubjectPropertyInitializer>(initializer);

        var person = new Person(context) { FirstName = "John" };

        // Act
        person.TryGetRegisteredSubject()!
            .AddProperty("DynamicProp", typeof(string), _ => "test", null);

        // Assert
        Assert.Contains("DynamicProp", initializedProperties);
    }

    [Fact]
    public void WhenAddingDynamicDerivedProperty_ThenInitializersAreCalled()
    {
        // Arrange
        var initializedProperties = new List<string>();
        var initializer = new TestPropertyInitializer(p => initializedProperties.Add(p.Name));

        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithDerivedPropertyChangeDetection()
            .WithService<ISubjectPropertyInitializer>(initializer);

        var person = new Person(context) { FirstName = "John" };

        // Act
        person.TryGetRegisteredSubject()!
            .AddDerivedProperty<string>("DynamicDerived", p => ((Person)p).FirstName + "!");

        // Assert
        Assert.Contains("DynamicDerived", initializedProperties);
    }

    [Fact]
    public void WhenAddingPropertyViaLifecycleHandler_ThenInitializersAreCalled()
    {
        // Arrange - mirrors issue #181 exactly
        var initializedProperties = new List<string>();
        var initializer = new TestPropertyInitializer(p => initializedProperties.Add(p.Name));
        var lifecycleHandler = new PropertyAddingLifecycleHandler();

        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithLifecycle()
            .WithService<ISubjectPropertyInitializer>(initializer)
            .WithService<ILifecycleHandler>(lifecycleHandler);

        // Act - property added during context attach via lifecycle handler
        var person = new Person(context) { FirstName = "John" };

        // Assert
        Assert.Contains("DynamicFromLifecycle", initializedProperties);
    }

    private class TestPropertyInitializer : ISubjectPropertyInitializer
    {
        private readonly Action<RegisteredSubjectProperty> _onInitialize;

        public TestPropertyInitializer(Action<RegisteredSubjectProperty> onInitialize)
            => _onInitialize = onInitialize;

        public void InitializeProperty(RegisteredSubjectProperty property)
            => _onInitialize(property);
    }

    private class PropertyAddingLifecycleHandler : ILifecycleHandler
    {
        public void HandleLifecycleChange(SubjectLifecycleChange change)
        {
            if (change.IsContextAttach)
            {
                var registered = change.Subject.TryGetRegisteredSubject();
                registered?.AddProperty("DynamicFromLifecycle", typeof(string), _ => "test", null);
            }
        }
    }
}
```

**Verification:** Run tests. They should fail, confirming the bug.

---

## Task 2: Investigate Root Cause

**Files to examine:**
- `src/Namotion.Interceptor.Registry/Abstractions/RegisteredSubject.cs` - `AddProperty()`, `AddPropertyInternal()`
- `src/Namotion.Interceptor.Registry/SubjectRegistry.cs` - `AttachProperty()`
- `src/Namotion.Interceptor/InterceptorSubject.cs` - `AttachSubjectProperty()`

**Investigation steps:**
1. Run failing test with debugger
2. Check if `SubjectRegistry.AttachProperty()` is reached
3. Check if `TryGetRegisteredProperty()` returns null for the new property
4. Trace the property registration flow

---

## Task 3: Implement Fix

Based on investigation, the fix likely involves one of:

**Option A:** Ensure the property is added to `RegisteredSubject._properties` before calling `AttachSubjectProperty()`

**Option B:** In `SubjectRegistry.AttachProperty()`, handle the case where the property was just added and look it up differently

**Option C:** Pass the `RegisteredSubjectProperty` directly to the initializers instead of looking it up

Document the actual fix here after investigation.

---

## Task 4: Verify Fix

```bash
dotnet test src/Namotion.Interceptor.slnx
```

All tests should pass, including the new regression tests.

---

## Files to Create/Modify

**New Files:**
- `src/Namotion.Interceptor.Registry.Tests/DynamicPropertyInitializerTests.cs`

**Modified Files:**
- TBD based on investigation (likely `SubjectRegistry.cs` or `RegisteredSubject.cs`)
