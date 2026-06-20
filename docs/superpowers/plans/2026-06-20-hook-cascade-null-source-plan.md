# Hook Cascade and INPC Null-Source Publishing Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make framework-invoked consequence writes (generated property hooks `OnXChanging`/`OnXChanged`, INPC handler write-backs) publish as local origin (`Source = null`) so they flow to bound sources, matching what derived recalculations already do.

**Architecture:** Introduce a parameterless `SubjectChangeContext.WithLocalOrigin()` (resets source to null, preserves timestamps) as the single local-origin idiom and the `ChangeOrigin` (#342) forward-compat seam. The generator wraps implemented hook calls and the INPC raise in that scope; the existing derived handler migrates to it. Pay-nothing: hook scopes are emitted only for properties whose hooks are actually implemented (detected statically by the generator), and the INPC scope is entered only when a subscriber exists.

**Tech Stack:** .NET (C# 13 partial properties), Roslyn incremental source generator (`Namotion.Interceptor.Generator`), xUnit, Moq, Verify (snapshot tests), PublicApiGenerator (public API snapshots).

**Spec:** `docs/superpowers/specs/2026-06-11-hook-cascade-null-source-design.md`

---

## File Structure

**Core (`Namotion.Interceptor`)**
- Modify: `src/Namotion.Interceptor/SubjectChangeContext.cs` — add `WithLocalOrigin()`.

**Tracking (`Namotion.Interceptor.Tracking`)**
- Modify: `src/Namotion.Interceptor.Tracking/Change/DerivedPropertyChangeHandler.cs:357` — migrate the one `WithSource(null)` call to `WithLocalOrigin()`.

**Generator (`Namotion.Interceptor.Generator`)**
- Modify: `src/Namotion.Interceptor.Generator/Models/PropertyMetadata.cs` — add `HasChangingHook`, `HasChangedHook`.
- Modify: `src/Namotion.Interceptor.Generator/SubjectMetadataExtractor.cs` — detect implemented hook bodies by name across all partial declarations.
- Modify: `src/Namotion.Interceptor.Generator/SubjectCodeGenerator.cs` — wrap implemented hook calls (setter) and the INPC raise (`RaisePropertyChanged`) in `WithLocalOrigin()`.

**Tests**
- Create: `src/Namotion.Interceptor.Tests/SubjectChangeContextLocalOriginTests.cs` — unit test for `WithLocalOrigin()`.
- Modify: `src/Namotion.Interceptor.Tests/VerifyChecksTests.PublicApi.verified.txt` — accept new public API.
- Create: `src/Namotion.Interceptor.Generator.Tests/Models/PersonWithSelectiveHooks.cs` — model with one hooked + one non-hooked property.
- Create: `src/Namotion.Interceptor.Generator.Tests/HookScopeGenerationTests.cs` + its `.verified.txt` snapshot — pay-nothing pin.
- Modify: various `src/Namotion.Interceptor.Generator.Tests/**/*.verified.txt` — refresh after the `RaisePropertyChanged` shape change.
- Create: `src/Namotion.Interceptor.Connectors.Tests/Models/CascadingDevice.cs` — recreate the cascade model.
- Create: `src/Namotion.Interceptor.Connectors.Tests/Transactions/SubjectCascadeLocalOriginTests.cs` — cascade + derived + INPC behavior pins.

**Docs**
- Modify: `docs/connectors.md`, `docs/tracking-transactions.md`, `docs/generator.md`.

---

## Task 1: Add `WithLocalOrigin()` to the core change context

**Files:**
- Create: `src/Namotion.Interceptor.Tests/SubjectChangeContextLocalOriginTests.cs`
- Modify: `src/Namotion.Interceptor/SubjectChangeContext.cs` (add method after `WithSource`, around line 138)
- Modify: `src/Namotion.Interceptor.Tests/VerifyChecksTests.PublicApi.verified.txt`

- [ ] **Step 1: Write the failing test**

Create `src/Namotion.Interceptor.Tests/SubjectChangeContextLocalOriginTests.cs`:

```csharp
using Xunit;

namespace Namotion.Interceptor.Tests;

public class SubjectChangeContextLocalOriginTests
{
    [Fact]
    public void WhenWithLocalOriginEntered_ThenSourceIsNullAndTimestampsPreserved()
    {
        // Arrange
        var source = new object();
        var received = new DateTimeOffset(2026, 6, 20, 10, 0, 0, TimeSpan.Zero);

        // Act & Assert
        using (SubjectChangeContext.WithState(source, changed: null, received: received))
        {
            Assert.Same(source, SubjectChangeContext.Current.Source);

            using (SubjectChangeContext.WithLocalOrigin())
            {
                // Inside the local-origin scope the source is cleared...
                Assert.Null(SubjectChangeContext.Current.Source);
                // ...but the ambient received timestamp is preserved.
                Assert.Equal(received, SubjectChangeContext.Current.ReceivedTimestamp);
            }

            // After dispose the previous source is restored.
            Assert.Same(source, SubjectChangeContext.Current.Source);
        }

        Assert.Null(SubjectChangeContext.Current.Source);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails (does not compile)**

Run: `dotnet test src/Namotion.Interceptor.Tests --filter "FullyQualifiedName~SubjectChangeContextLocalOriginTests"`
Expected: FAIL — build error, `'SubjectChangeContext' does not contain a definition for 'WithLocalOrigin'`.

- [ ] **Step 3: Implement `WithLocalOrigin()`**

In `src/Namotion.Interceptor/SubjectChangeContext.cs`, add this method immediately after the `WithSource` method (after line 138, before `WithState`):

```csharp
    /// <summary>
    /// Enters a scope that marks writes inside it as local origin: resets the source to null while
    /// preserving the ambient changed and received timestamps. Used around framework-invoked
    /// consequence callbacks (generated property hooks, INotifyPropertyChanged raises, derived
    /// recalculations) so their writes flow to bound sources like any local write.
    /// Forward-compatibility seam for the typed ChangeOrigin discriminator (#342): encodes local
    /// origin as Source = null today; a future version sets Kind = Local without changing this
    /// signature or any call site.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SubjectChangeContextScope WithLocalOrigin()
    {
        var previousState = _current;
        _current = new SubjectChangeContext(
            previousState._changedTimestamp,
            previousState._receivedTimestamp,
            null);
        return new SubjectChangeContextScope(previousState);
    }
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test src/Namotion.Interceptor.Tests --filter "FullyQualifiedName~SubjectChangeContextLocalOriginTests"`
Expected: PASS.

- [ ] **Step 5: Refresh the public API snapshot**

The added public method breaks the public-API snapshot test. Run it first:

Run: `dotnet test src/Namotion.Interceptor.Tests --filter "FullyQualifiedName~VerifyChecksTests"`
Expected: FAIL — `VerifyChecksTests.PublicApi` reports a diff that adds `WithLocalOrigin`.

Then accept the snapshot. In `src/Namotion.Interceptor.Tests/VerifyChecksTests.PublicApi.verified.txt`, insert this line between the `WithChangedTimestamp` line and the `WithSource` line (alphabetical order is `WithChangedTimestamp` < `WithLocalOrigin` < `WithSource` < `WithState`):

```text
        public static Namotion.Interceptor.SubjectChangeContext.SubjectChangeContextScope WithLocalOrigin() { }
```

Re-run: `dotnet test src/Namotion.Interceptor.Tests --filter "FullyQualifiedName~VerifyChecksTests"`
Expected: PASS.

(If any other project's `VerifyChecksTests.PublicApi` also fails because it re-exports the core type, accept it the same way: replace its `.verified.txt` with the test's `.received.txt`.)

- [ ] **Step 6: Commit**

```bash
git add src/Namotion.Interceptor/SubjectChangeContext.cs \
        src/Namotion.Interceptor.Tests/SubjectChangeContextLocalOriginTests.cs \
        src/Namotion.Interceptor.Tests/VerifyChecksTests.PublicApi.verified.txt
git commit -m "feat: add SubjectChangeContext.WithLocalOrigin() local-origin scope (#345)"
```

---

## Task 2: Migrate the derived handler to `WithLocalOrigin()`

**Files:**
- Modify: `src/Namotion.Interceptor.Tracking/Change/DerivedPropertyChangeHandler.cs:357`

This is a pure idiom migration with identical behavior (both reset source to null and preserve timestamps), so existing derived tests are the regression guard.

- [ ] **Step 1: Make the change**

In `src/Namotion.Interceptor.Tracking/Change/DerivedPropertyChangeHandler.cs`, change line 357 from:

```csharp
        using (SubjectChangeContext.WithSource(null))
```

to:

```csharp
        using (SubjectChangeContext.WithLocalOrigin())
```

- [ ] **Step 2: Run the tracking tests to verify still green**

Run: `dotnet test src/Namotion.Interceptor.Tracking.Tests`
Expected: PASS (no behavior change; derived recalculations still publish `Source = null`).

- [ ] **Step 3: Commit**

```bash
git add src/Namotion.Interceptor.Tracking/Change/DerivedPropertyChangeHandler.cs
git commit -m "refactor: route derived recalculation through WithLocalOrigin (#345)"
```

---

## Task 3: Generator emits `WithLocalOrigin()` around implemented hooks and the INPC raise

**Files:**
- Modify: `src/Namotion.Interceptor.Generator/Models/PropertyMetadata.cs`
- Modify: `src/Namotion.Interceptor.Generator/SubjectMetadataExtractor.cs` (`CollectProperties`, lines 107-165)
- Modify: `src/Namotion.Interceptor.Generator/SubjectCodeGenerator.cs` (`EmitNotifyPropertyChangedImplementation` lines 116-133; `EmitProperty` setter block lines 308-318)
- Regression: `src/Namotion.Interceptor.Generator.Tests/PropertyHooksTests.cs` (existing, must stay green)
- Refresh: `src/Namotion.Interceptor.Generator.Tests/**/*.verified.txt`

- [ ] **Step 1: Add the two hook flags to `PropertyMetadata`**

In `src/Namotion.Interceptor.Generator/Models/PropertyMetadata.cs`, change the record to add two flags at the end (after `InterfaceTypeName`):

```csharp
namespace Namotion.Interceptor.Generator.Models;

internal sealed record PropertyMetadata(
    string Name,
    string FullTypeName,
    string AccessModifier,
    bool IsPartial,
    bool IsVirtual,
    bool IsOverride,
    bool IsDerived,
    bool IsRequired,
    bool HasGetter,
    bool HasSetter,
    bool HasInit,
    bool IsFromInterface,
    string? GetterAccessModifier,
    string? SetterAccessModifier,
    string? InterfaceTypeName = null,
    bool HasChangingHook = false,
    bool HasChangedHook = false);
```

- [ ] **Step 2: Detect implemented hooks in the extractor**

In `src/Namotion.Interceptor.Generator/SubjectMetadataExtractor.cs`, inside `CollectProperties`, add a first pass that collects implemented method names, then set the flags per property.

Add this block immediately after `var properties = new List<PropertyMetadata>();` (line 112):

```csharp
        // First pass: collect names of On{X}Changing/On{X}Changed partial method bodies that are
        // actually implemented (have a block or expression body), across all partial declarations.
        // Name-only matching is deliberately over-approximate: a false positive costs one redundant
        // scope around a compiler-erased call; a false negative would silently restore source
        // inheritance for that hook.
        var implementedHookMethods = new HashSet<string>();
        foreach (var syntaxReference in typeSymbol.DeclaringSyntaxReferences)
        {
            if (syntaxReference.GetSyntax(cancellationToken) is not ClassDeclarationSyntax hookClassDecl)
            {
                continue;
            }

            foreach (var method in hookClassDecl.Members.OfType<MethodDeclarationSyntax>())
            {
                if (method.Body is not null || method.ExpressionBody is not null)
                {
                    implementedHookMethods.Add(method.Identifier.ValueText);
                }
            }
        }
```

Then, in the property-building loop, immediately before the `properties.Add(new PropertyMetadata(` call (line 146), add:

```csharp
                var hasChangingHook = implementedHookMethods.Contains($"On{propertyName}Changing");
                var hasChangedHook = implementedHookMethods.Contains($"On{propertyName}Changed");
```

And change the `properties.Add(new PropertyMetadata(...))` call so its final arguments pass the flags by name (leaving `InterfaceTypeName` at its default). Replace:

```csharp
                properties.Add(new PropertyMetadata(
                    propertyName,
                    fullyQualifiedName,
                    accessModifier,
                    isPartial,
                    isVirtual,
                    isOverride,
                    isDerived,
                    isRequired,
                    hasGetter,
                    hasSetter,
                    hasInit,
                    IsFromInterface: false,
                    getterAccessModifier,
                    setterAccessModifier));
```

with:

```csharp
                properties.Add(new PropertyMetadata(
                    propertyName,
                    fullyQualifiedName,
                    accessModifier,
                    isPartial,
                    isVirtual,
                    isOverride,
                    isDerived,
                    isRequired,
                    hasGetter,
                    hasSetter,
                    hasInit,
                    IsFromInterface: false,
                    getterAccessModifier,
                    setterAccessModifier,
                    HasChangingHook: hasChangingHook,
                    HasChangedHook: hasChangedHook));
```

(Interface default properties never have hooks, so leave `ExtractInterfaceDefaultProperties` unchanged; the flags default to `false` there.)

- [ ] **Step 3: Wrap the INPC raise inside the generated `RaisePropertyChanged`**

In `src/Namotion.Interceptor.Generator/SubjectCodeGenerator.cs`, `EmitNotifyPropertyChangedImplementation` (lines 116-133), replace the `builder.Append("""..."""` block with:

```csharp
        builder.Append("""
                    public event PropertyChangedEventHandler? PropertyChanged;

                    [MethodImpl(MethodImplOptions.AggressiveInlining)]
                    protected void RaisePropertyChanged(string propertyName)
                    {
                        var handler = PropertyChanged;
                        if (handler is null)
                        {
                            return;
                        }

                        using (SubjectChangeContext.WithLocalOrigin())
                        {
                            handler.Invoke(this, PropertyChangedEventArgsCache.Get(propertyName));
                        }
                    }

                    void IRaisePropertyChanged.RaisePropertyChanged(string propertyName) => RaisePropertyChanged(propertyName);


            """);
```

- [ ] **Step 4: Wrap implemented hook calls (and the manual-INPC-base raise) in the setter**

In `src/Namotion.Interceptor.Generator/SubjectCodeGenerator.cs`, `EmitProperty`, replace the setter body emission (lines 308-318, from `builder.AppendLine($"            {setterModifiers}{accessorText}");` through the closing `builder.AppendLine("            }");` of the setter) with:

```csharp
            builder.AppendLine($"            {setterModifiers}{accessorText}");
            builder.AppendLine("            {");
            builder.AppendLine("                var newValue = value;");
            builder.AppendLine("                var cancel = false;");

            // OnXChanging: wrap in a local-origin scope only when the hook is actually implemented,
            // so unimplemented hooks keep the bare (compiler-erased) call and pay nothing.
            if (property.HasChangingHook)
            {
                builder.AppendLine("                using (SubjectChangeContext.WithLocalOrigin())");
                builder.AppendLine("                {");
                builder.AppendLine($"                    On{property.Name}Changing(ref newValue, ref cancel);");
                builder.AppendLine("                }");
            }
            else
            {
                builder.AppendLine($"                On{property.Name}Changing(ref newValue, ref cancel);");
            }

            builder.AppendLine($"                if (!cancel && SetPropertyValue(nameof({property.Name}), newValue, _{property.Name}, static (o, v) => (({metadata.ClassName})o)._{property.Name} = v))");
            builder.AppendLine("                {");

            // OnXChanged: same conditional wrapping.
            if (property.HasChangedHook)
            {
                builder.AppendLine("                    using (SubjectChangeContext.WithLocalOrigin())");
                builder.AppendLine("                    {");
                builder.AppendLine($"                        On{property.Name}Changed(_{property.Name});");
                builder.AppendLine("                    }");
            }
            else
            {
                builder.AppendLine($"                    On{property.Name}Changed(_{property.Name});");
            }

            // INPC raise. The own and base-generated cases are wrapped inside RaisePropertyChanged
            // itself, so the call site stays bare. Only the manual-IRaisePropertyChanged-base case
            // (the user's method cannot be modified) is wrapped here at the interface-cast call site.
            var raisePropertyChangedIsManualBase = !metadata.BaseClassHasInterceptorSubject && metadata.BaseClassHasInpc;
            if (raisePropertyChangedIsManualBase)
            {
                builder.AppendLine("                    using (SubjectChangeContext.WithLocalOrigin())");
                builder.AppendLine("                    {");
                builder.AppendLine($"                        {raisePropertyChangedCall};");
                builder.AppendLine("                    }");
            }
            else
            {
                builder.AppendLine($"                    {raisePropertyChangedCall};");
            }

            builder.AppendLine("                }");
            builder.AppendLine("            }");
```

(The `raisePropertyChangedCall` local is already computed above, lines 302-306. Leave it unchanged.)

- [ ] **Step 5: Build the generator and run the hook behavior tests**

Run: `dotnet test src/Namotion.Interceptor.Generator.Tests --filter "FullyQualifiedName~PropertyHooksTests"`
Expected: PASS — all existing hook behavior tests still pass (wrapping changes the source scope, not the hook semantics).

- [ ] **Step 6: Refresh the generator snapshots**

The `RaisePropertyChanged` shape change alters the generated code for every standalone subject snapshot. Run the full generator test project:

Run: `dotnet test src/Namotion.Interceptor.Generator.Tests`
Expected: some `SourceGeneratorTests` / `VirtualPartialTests` / `InterfaceDefaultPropertyTests` snapshot tests FAIL with a diff.

Review each diff and confirm the only changes are (a) `RaisePropertyChanged` becoming the null-check + `WithLocalOrigin()` block, and (b) for any model that implements hooks, the hook call wrapped in `using (SubjectChangeContext.WithLocalOrigin())`. Then accept all produced snapshots:

```bash
find src/Namotion.Interceptor.Generator.Tests -name '*.received.txt' -not -path '*/obj/*' -not -path '*/bin/*' \
  -exec sh -c 'mv "$1" "${1%.received.txt}.verified.txt"' _ {} \;
```

Re-run: `dotnet test src/Namotion.Interceptor.Generator.Tests`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Namotion.Interceptor.Generator/ src/Namotion.Interceptor.Generator.Tests/
git commit -m "feat: generate WithLocalOrigin scopes around hooks and INPC raise (#345)"
```

---

## Task 4: Pin the pay-nothing guarantee with a hook-scope snapshot test

**Files:**
- Create: `src/Namotion.Interceptor.Generator.Tests/Models/PersonWithSelectiveHooks.cs`
- Create: `src/Namotion.Interceptor.Generator.Tests/HookScopeGenerationTests.cs`
- Create: `src/Namotion.Interceptor.Generator.Tests/HookScopeGenerationTests.WhenOnlyOneHookImplemented_ThenScopeEmittedOnlyForThatHook.verified.txt` (via snapshot accept)

This pins that a property whose hook is implemented gets the scope, while a sibling property without an implemented hook does not (the pay-nothing guarantee), using a behavioral check on the generated runtime type rather than a brittle full-file snapshot.

- [ ] **Step 1: Create the model**

Create `src/Namotion.Interceptor.Generator.Tests/Models/PersonWithSelectiveHooks.cs`:

```csharp
using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Generator.Tests.Models;

[InterceptorSubject]
public partial class PersonWithSelectiveHooks
{
    // Hooked: OnHookedChanged is implemented, so its setter must wrap the call in a local-origin scope.
    public partial string? Hooked { get; set; }

    // NotHooked: no hook bodies implemented, so its setter must keep the bare (erased) calls.
    public partial string? NotHooked { get; set; }

    public object? HookedSourceInsideChanged { get; private set; }

    partial void OnHookedChanged(string? newValue)
    {
        // Capture the ambient source seen inside the implemented hook body.
        HookedSourceInsideChanged = SubjectChangeContext.Current.Source;
    }
}
```

- [ ] **Step 2: Write the failing test**

Create `src/Namotion.Interceptor.Generator.Tests/HookScopeGenerationTests.cs`:

```csharp
using Namotion.Interceptor.Generator.Tests.Models;
using Xunit;

namespace Namotion.Interceptor.Generator.Tests;

public class HookScopeGenerationTests
{
    [Fact]
    public void WhenHookImplemented_ThenHookBodyRunsUnderLocalOriginScope()
    {
        // Arrange
        var person = new PersonWithSelectiveHooks();
        var source = new object();

        // Act: write under an ambient source scope, like an inbound/commit apply.
        using (SubjectChangeContext.WithSource(source))
        {
            person.Hooked = "value";
        }

        // Assert: the implemented hook saw a null source (the generated scope reset it),
        // even though the surrounding write ran under a real source scope.
        Assert.Null(person.HookedSourceInsideChanged);
    }

    [Fact]
    public void WhenHookNotImplemented_ThenSetterStillWritesValue()
    {
        // Arrange
        var person = new PersonWithSelectiveHooks();

        // Act: a property whose hooks are not implemented pays nothing and behaves normally.
        person.NotHooked = "value";

        // Assert
        Assert.Equal("value", person.NotHooked);
    }
}
```

- [ ] **Step 3: Run the test to verify it passes**

Run: `dotnet test src/Namotion.Interceptor.Generator.Tests --filter "FullyQualifiedName~HookScopeGenerationTests"`
Expected: PASS. (This depends on Task 3's generator change; if `WhenHookImplemented...` fails with a non-null source, the setter wrapping in Task 3 Step 4 was not applied correctly.)

- [ ] **Step 4: Commit**

```bash
git add src/Namotion.Interceptor.Generator.Tests/Models/PersonWithSelectiveHooks.cs \
        src/Namotion.Interceptor.Generator.Tests/HookScopeGenerationTests.cs
git commit -m "test: pin local-origin hook scope and pay-nothing for unhooked properties (#345)"
```

---

## Task 5: Recreate the cascade model and pin cascade + derived local-origin behavior

**Files:**
- Create: `src/Namotion.Interceptor.Connectors.Tests/Models/CascadingDevice.cs`
- Create: `src/Namotion.Interceptor.Connectors.Tests/Transactions/SubjectCascadeLocalOriginTests.cs`

`CascadingDevice` was removed by the transaction test-suite cleanup (#341, `2511e6c8`); recreate it. The cascade behavior tests this file adds replace the deleted `SubjectTransactionEchoSuppressionTests` cascade pins (which asserted the old inherited-source behavior).

- [ ] **Step 1: Create the cascade model**

Create `src/Namotion.Interceptor.Connectors.Tests/Models/CascadingDevice.cs`:

```csharp
using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Connectors.Tests.Models;

[InterceptorSubject]
public partial class CascadingDevice
{
    public partial int Primary { get; set; }

    public partial int Secondary { get; set; }

    // Cascade: writing Primary computes Secondary locally. Secondary is the local model's own
    // computation, so its change must publish as local origin (Source = null).
    partial void OnPrimaryChanged(int newValue)
    {
        Secondary = newValue * 2;
    }
}
```

- [ ] **Step 2: Write the failing behavior tests**

Create `src/Namotion.Interceptor.Connectors.Tests/Transactions/SubjectCascadeLocalOriginTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Connectors.Transactions;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Transactions;
using Xunit;

namespace Namotion.Interceptor.Connectors.Tests.Transactions;

/// <summary>
/// Pins that framework-invoked consequence writes publish as local origin (Source = null) and flow
/// to bound sources: hook cascades from OnXChanged, and derived recalculations. Replaces the cascade
/// pins removed with SubjectTransactionEchoSuppressionTests by the #341 cleanup, flipped from the old
/// inherited-source behavior to the new local-origin behavior.
/// </summary>
public class SubjectCascadeLocalOriginTests : TransactionTestBase
{
    // Drains until the sentinel arrives (excluded from the result); throws TimeoutException after 10s.
    private static List<SubjectPropertyChange> DrainUntil(
        PropertyChangeQueueSubscription subscription, Func<SubjectPropertyChange, bool> isSentinel)
    {
        var changes = new List<SubjectPropertyChange>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (subscription.TryDequeue(out var change, timeout.Token))
        {
            if (isSentinel(change))
            {
                return changes;
            }
            changes.Add(change);
        }
        throw new TimeoutException("Sentinel notification was not received within 10 seconds.");
    }

    [Fact]
    public async Task WhenCommitAppliedChangeTriggersCascade_ThenCascadePublishesLocalOrigin()
    {
        // Arrange
        var context = CreateContext();
        var device = new CascadingDevice(context);

        var writtenBatches = new List<SubjectPropertyChange[]>();
        var sourceMock = CreateSucceedingSource();
        sourceMock
            .Setup(s => s.WriteChangesAsync(
                It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(),
                It.IsAny<CancellationToken>()))
            .Callback((ReadOnlyMemory<SubjectPropertyChange> batch, CancellationToken _) =>
                writtenBatches.Add(batch.ToArray()))
            .Returns(new ValueTask<WriteResult>(WriteResult.Success));

        new PropertyReference(device, nameof(CascadingDevice.Primary)).SetSource(sourceMock.Object);
        new PropertyReference(device, nameof(CascadingDevice.Secondary)).SetSource(sourceMock.Object);

        using var subscription = context.CreatePropertyChangeQueueSubscription();

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            device.Primary = 5;
            await transaction.CommitAsync(CancellationToken.None);
        }

        var secondaryAfterCommit = device.Secondary;

        var sentinel = new Person(context);
        sentinel.LastName = "Sentinel";
        var changes = DrainUntil(subscription, c =>
            ReferenceEquals(c.Property.Subject, sentinel) && c.Property.Name == nameof(Person.LastName));

        // Assert: the cascade produced Secondary = 10.
        Assert.Equal(10, secondaryAfterCommit);

        var primaryChanges = changes.Where(c => c.Property.Name == nameof(CascadingDevice.Primary)).ToList();
        var secondaryChanges = changes.Where(c => c.Property.Name == nameof(CascadingDevice.Secondary)).ToList();

        // Primary carries the confirming source (the triggering write keeps its ambient scope).
        Assert.Single(primaryChanges);
        Assert.Equal(5, primaryChanges[0].GetNewValue<int>());
        Assert.Same(sourceMock.Object, primaryChanges[0].Source);

        // Secondary (the cascade) now publishes local origin (null) instead of inheriting the source.
        Assert.Single(secondaryChanges);
        Assert.Equal(10, secondaryChanges[0].GetNewValue<int>());
        Assert.Null(secondaryChanges[0].Source);

        // Stage 1 (the transactional source write) still contained only Primary; the cascade reaches
        // the source through the background queue, not the transaction.
        Assert.Single(writtenBatches);
        Assert.Single(writtenBatches[0]);
        Assert.Equal(nameof(CascadingDevice.Primary), writtenBatches[0][0].Property.Name);
    }

    [Fact]
    public async Task WhenInboundSourceValueTriggersCascade_ThenCascadeIsDeliveredToBoundSource()
    {
        // Arrange
        var context = CreateContext();
        var device = new CascadingDevice(context);
        var sourceMock = CreateSucceedingSource();

        new PropertyReference(device, nameof(CascadingDevice.Primary)).SetSource(sourceMock.Object);
        new PropertyReference(device, nameof(CascadingDevice.Secondary)).SetSource(sourceMock.Object);

        // A ChangeQueueProcessor whose source identity IS the bound source: it echo-drops changes
        // already marked with that source and delivers everything else.
        var delivered = new List<SubjectPropertyChange>();
        var processor = new ChangeQueueProcessor(
            source: sourceMock.Object,
            context: context,
            propertyFilter: _ => true,
            writeHandler: (batch, _) =>
            {
                lock (delivered)
                {
                    delivered.AddRange(batch.ToArray());
                }
                return ValueTask.CompletedTask;
            },
            bufferTime: TimeSpan.FromMilliseconds(8),
            logger: NullLogger.Instance);

        using var processorCts = new CancellationTokenSource();
        var processTask = processor.ProcessAsync(processorCts.Token);

        try
        {
            // Act: a value arrives from the source, triggering the OnPrimaryChanged cascade.
            new PropertyReference(device, nameof(CascadingDevice.Primary))
                .SetValueFromSource(sourceMock.Object, DateTimeOffset.UtcNow, null, 7);

            // Wait until the processor has delivered the Secondary cascade.
            await AsyncTestHelpers.WaitUntilAsync(
                () =>
                {
                    lock (delivered)
                    {
                        return delivered.Any(c => c.Property.Name == nameof(CascadingDevice.Secondary));
                    }
                },
                message: "Processor did not deliver the Secondary cascade.");
        }
        finally
        {
            await processorCts.CancelAsync();
            processor.Dispose();
            try { await processTask; } catch (OperationCanceledException) { }
        }

        // Assert
        Assert.Equal(14, device.Secondary);
        lock (delivered)
        {
            // Secondary (local origin) is delivered to the bound source.
            var secondary = delivered.Single(c => c.Property.Name == nameof(CascadingDevice.Secondary));
            Assert.Equal(14, secondary.GetNewValue<int>());

            // Primary (marked with the inbound source) is echo-dropped, not pushed back.
            Assert.DoesNotContain(delivered, c => c.Property.Name == nameof(CascadingDevice.Primary));
        }
    }

    [Fact]
    public async Task WhenCommitAppliedChangeTriggersDerivedRecalculation_ThenDerivedNotificationStaysLocalOrigin()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);

        var writtenBatches = new List<SubjectPropertyChange[]>();
        var sourceMock = CreateSucceedingSource();
        sourceMock
            .Setup(s => s.WriteChangesAsync(
                It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(),
                It.IsAny<CancellationToken>()))
            .Callback((ReadOnlyMemory<SubjectPropertyChange> batch, CancellationToken _) =>
                writtenBatches.Add(batch.ToArray()))
            .Returns(new ValueTask<WriteResult>(WriteResult.Success));

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(sourceMock.Object);

        using var subscription = context.CreatePropertyChangeQueueSubscription();

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "John";
            await transaction.CommitAsync(CancellationToken.None);
        }

        var sentinel = new Person(context);
        sentinel.LastName = "Sentinel";
        var changes = DrainUntil(subscription, c =>
            ReferenceEquals(c.Property.Subject, sentinel) && c.Property.Name == nameof(Person.LastName));

        // Assert: FullName recalculation stays local origin (unchanged behavior, re-pinned).
        var fullNameChanges = changes.Where(c =>
            ReferenceEquals(c.Property.Subject, person) && c.Property.Name == nameof(Person.FullName)).ToList();
        Assert.Single(fullNameChanges);
        Assert.Null(fullNameChanges[0].Source);

        var firstNameChanges = changes.Where(c =>
            ReferenceEquals(c.Property.Subject, person) && c.Property.Name == nameof(Person.FirstName)).ToList();
        Assert.Single(firstNameChanges);
        Assert.Same(sourceMock.Object, firstNameChanges[0].Source);
    }
}
```

- [ ] **Step 3: Run the tests to verify they pass**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~SubjectCascadeLocalOriginTests"`
Expected: PASS. (These depend on Task 3's generator change. If `WhenCommitAppliedChangeTriggersCascade...` fails because Secondary's source is `sourceMock.Object` instead of null, the generator hook wrapping is not in effect — rebuild the generator and the test project.)

- [ ] **Step 4: Commit**

```bash
git add src/Namotion.Interceptor.Connectors.Tests/Models/CascadingDevice.cs \
        src/Namotion.Interceptor.Connectors.Tests/Transactions/SubjectCascadeLocalOriginTests.cs
git commit -m "test: pin hook cascade and derived local-origin delivery (#345)"
```

---

## Task 6: Pin INPC write-back local origin

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors.Tests/Transactions/SubjectCascadeLocalOriginTests.cs` (add one test)

- [ ] **Step 1: Add the failing test**

Append this method inside the `SubjectCascadeLocalOriginTests` class in `src/Namotion.Interceptor.Connectors.Tests/Transactions/SubjectCascadeLocalOriginTests.cs` (before the closing brace of the class):

```csharp
    [Fact]
    public void WhenInpcHandlerWritesBackDuringSourceScopedApply_ThenWriteBackPublishesLocalOrigin()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);
        var sourceMock = CreateSucceedingSource();

        // An INPC handler that reacts to FirstName by writing LastName (a model write-back).
        person.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Person.FirstName))
            {
                person.LastName = "Derived";
            }
        };

        using var subscription = context.CreatePropertyChangeQueueSubscription();

        // Act: apply FirstName under a source scope, like an inbound/commit apply. The generated
        // RaisePropertyChanged enters a local-origin scope before invoking subscribers, so the
        // handler's LastName write-back is local origin.
        using (SubjectChangeContext.WithSource(sourceMock.Object))
        {
            person.FirstName = "John";
        }

        var sentinel = new Person(context);
        sentinel.LastName = "Sentinel";
        var changes = DrainUntil(subscription, c =>
            ReferenceEquals(c.Property.Subject, sentinel) && c.Property.Name == nameof(Person.LastName));

        var personChanges = changes.Where(c => ReferenceEquals(c.Property.Subject, person)).ToList();
        var firstNameChanges = personChanges.Where(c => c.Property.Name == nameof(Person.FirstName)).ToList();
        var lastNameChanges = personChanges.Where(c => c.Property.Name == nameof(Person.LastName)).ToList();

        // Assert: the triggering FirstName write keeps the ambient source.
        Assert.Single(firstNameChanges);
        Assert.Same(sourceMock.Object, firstNameChanges[0].Source);

        // The INPC handler's LastName write-back publishes local origin.
        Assert.Single(lastNameChanges);
        Assert.Null(lastNameChanges[0].Source);
    }
```

- [ ] **Step 2: Run the test to verify it passes**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~WhenInpcHandlerWritesBackDuringSourceScopedApply"`
Expected: PASS. (Depends on Task 3's `RaisePropertyChanged` wrapping. If LastName's source is `sourceMock.Object`, the INPC wrapping is not in effect.)

- [ ] **Step 3: Commit**

```bash
git add src/Namotion.Interceptor.Connectors.Tests/Transactions/SubjectCascadeLocalOriginTests.cs
git commit -m "test: pin INPC write-back local origin during source-scoped apply (#345)"
```

---

## Task 7: Update documentation

**Files:**
- Modify: `docs/connectors.md`
- Modify: `docs/tracking-transactions.md`
- Modify: `docs/generator.md`

- [ ] **Step 1: connectors.md — add a consequence-write semantics note**

In `docs/connectors.md`, immediately after the `### Write Consistency Guarantees` intro paragraph (after line 71, before the table at line 73), insert:

```markdown
**Locally computed values flow to sources.** A change notification's source marks exactly the values a source sent (inbound) or confirmed (transaction commit stage 1). Everything the local model computes is local origin (`Source = null`) and flows to bound sources like any local write. This covers derived recalculations, generated property hook cascades (`OnXChanging`/`OnXChanged`), and `INotifyPropertyChanged` handler write-backs. As a result, a hook cascade or derived value bound to a source is delivered to that source even when the trigger came from the same source, instead of being suppressed as an echo. INPC handlers should react (UI refresh, logging, forwarding) rather than mutate the model; if they do mutate, those writes are local origin.

```

- [ ] **Step 2: tracking-transactions.md — replace the stage-2 cascade rule**

In `docs/tracking-transactions.md`, after the "Revert operations call setters with old values, which also trigger `OnChanging/OnChanged` methods." sentence (line 254), insert:

```markdown

Hook cascade writes made inside `OnChanging`/`OnChanged` during a commit apply publish as local origin (`Source = null`), not under the confirming source's scope. They are therefore delivered to their bound sources by the background change queue rather than suppressed as echoes. Writing a cascade value explicitly into the transaction is still the way to get confirmed, atomic delivery; it is now optional for delivery to happen at all.
```

- [ ] **Step 3: generator.md — note the local-origin scope on hooks**

In `docs/generator.md`, in the `### Property Hooks` section, after the `OnNameChanged` code block (after line 110, before the next subsection), insert:

```markdown

Implemented hook bodies run inside a local-origin scope (`SubjectChangeContext.WithLocalOrigin()`): any property write a hook makes (a cascade) publishes with `Source = null`, so it flows to bound sources like any local write. The scope is emitted only for hooks that are actually implemented, so properties without hook bodies pay nothing.
```

- [ ] **Step 4: Commit**

```bash
git add docs/connectors.md docs/tracking-transactions.md docs/generator.md
git commit -m "docs: document local-origin consequence-write semantics (#345)"
```

---

## Task 8: Full verification and wrap-up

**Files:** none (verification only)

- [ ] **Step 1: Build the whole solution**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: build succeeds with no warnings (warnings are errors in this repo).

- [ ] **Step 2: Run the unit test suite (excluding integration)**

Run: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`
Expected: PASS — all tests green, including the new local-origin pins, the refreshed generator snapshots, and the public API snapshot.

- [ ] **Step 3: Confirm no stray received snapshots remain**

Run: `find src -name '*.received.txt' -not -path '*/obj/*' -not -path '*/bin/*'`
Expected: no output (every snapshot was accepted).

- [ ] **Step 4: Final review against the spec**

Open `docs/superpowers/specs/2026-06-11-hook-cascade-null-source-design.md` and confirm each item in its Tests section maps to a task here:
- Commit-cascade delivery → Task 5 (`WhenCommitAppliedChangeTriggersCascade...`)
- Inbound-cascade delivery (pump level) → Task 5 (`WhenInboundSourceValueTriggersCascade...`)
- Derived-stays-local → Task 5 (`WhenCommitAppliedChangeTriggersDerivedRecalculation...`)
- INPC write-back → Task 6
- Generator pay-nothing → Task 4

If everything is green and mapped, the implementation is complete. (Per the spec's Relationship-to-other-issues note, schedule this PR to land in the same release bundle as, or after, #346.)
```

---

## Notes for the implementer

- **Do not commit unrelated working-tree changes** (`.claude/settings.local.json`, `docs/plans/`, `src/HomeBlaze/HomeBlaze/Data/Servers/Test.json`). Stage only the files each task names.
- **Generator changes require a rebuild before dependent test projects see them.** `dotnet test` on a downstream project rebuilds the generator, but if a behavior test unexpectedly shows the old (inherited-source) result, do a clean rebuild of `Namotion.Interceptor.Generator` first.
- **`WithSource(null)` must not reappear** outside the derived handler's migrated line. After Task 3, `grep -rn "WithSource(null)" src --include="*.cs"` (excluding `obj`/`bin`/`*.g.cs`) should return nothing.
