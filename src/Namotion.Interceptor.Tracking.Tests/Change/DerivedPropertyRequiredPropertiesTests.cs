using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Change;

public class DerivedPropertyRequiredPropertiesTests
{
    [Fact]
    public void WhenCompileTimeDerivedPropertyIsEvaluated_ThenRequiredPropertiesDoesNotContainItself()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithDerivedPropertyChangeDetection();

        var person = new Person(context) { FirstName = "A", LastName = "B" };

        var fullName = new PropertyReference(person, nameof(Person.FullName));
        var fullNameWithPrefix = new PropertyReference(person, nameof(Person.FullNameWithPrefix));

        // Act
        _ = person.FullName;
        _ = person.FullNameWithPrefix;

        // Assert
        Assert.DoesNotContain(fullName, fullName.GetRequiredProperties().ToArray());
        Assert.DoesNotContain(fullNameWithPrefix, fullNameWithPrefix.GetRequiredProperties().ToArray());
    }

    [Fact]
    public void WhenDynamicDerivedPropertyIsEvaluated_ThenRequiredPropertiesDoesNotContainItself()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithDerivedPropertyChangeDetection();

        var person = new Person(context) { FirstName = "A", LastName = "B" };
        var registered = person.TryGetRegisteredSubject()!;

        var dynamicDerived = registered.AddDerivedProperty<string>(
            "DynamicFull",
            s => ((Person)s).FirstName + " " + ((Person)s).LastName);

        // Act
        var deps = dynamicDerived.Reference.GetRequiredProperties().ToArray();

        // Assert
        Assert.DoesNotContain(dynamicDerived.Reference, deps);
        Assert.Contains(new PropertyReference(person, nameof(Person.FirstName)), deps);
        Assert.Contains(new PropertyReference(person, nameof(Person.LastName)), deps);
        Assert.Equal(2, deps.Length);
    }

    [Fact]
    public void WhenNestedDynamicDerivedPropertiesAreEvaluated_ThenNeitherContainsItself()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithDerivedPropertyChangeDetection();

        var person = new Person(context) { FirstName = "A", LastName = "B" };
        var registered = person.TryGetRegisteredSubject()!;

        var inner = registered.AddDerivedProperty<string>(
            "DynamicFull",
            s => ((Person)s).FirstName + " " + ((Person)s).LastName);

        var outer = registered.AddDerivedProperty<string>(
            "DynamicFullPrefixed",
            s => "Mr. " + s.Properties["DynamicFull"].GetValue?.Invoke(s));

        // Act
        var innerDeps = inner.Reference.GetRequiredProperties().ToArray();
        var outerDeps = outer.Reference.GetRequiredProperties().ToArray();

        // Assert — inner must not contain itself; outer must not contain itself
        Assert.DoesNotContain(inner.Reference, innerDeps);
        Assert.DoesNotContain(outer.Reference, outerDeps);

        // Outer should have recorded inner as a dependency (only the nested self-ref must be excluded)
        Assert.Contains(inner.Reference, outerDeps);
    }

    [Fact]
    public void WhenDynamicDerivedReadsSameDependencyTwice_ThenDedupedAndNoSelfRef()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithDerivedPropertyChangeDetection();

        var person = new Person(context) { FirstName = "A", LastName = "B" };
        var registered = person.TryGetRegisteredSubject()!;

        var dynamicDerived = registered.AddDerivedProperty<string>(
            "DoubledFirstName",
            s => ((Person)s).FirstName + ((Person)s).FirstName);

        // Act
        var deps = dynamicDerived.Reference.GetRequiredProperties().ToArray();

        // Assert
        Assert.DoesNotContain(dynamicDerived.Reference, deps);
        Assert.Single(deps);
        Assert.Equal(nameof(Person.FirstName), deps[0].Metadata.Name);
    }

    [Fact]
    public void WhenDynamicDerivedDependencyChanges_ThenRecalculationStillHasNoSelfRef()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithDerivedPropertyChangeDetection();

        var person = new Person(context) { FirstName = "A", LastName = "B" };
        var registered = person.TryGetRegisteredSubject()!;

        var dynamicDerived = registered.AddDerivedProperty<string>(
            "DynamicFull",
            s => ((Person)s).FirstName + " " + ((Person)s).LastName);

        // Act — mutate a dep to trigger recalculation and re-record
        person.FirstName = "Changed";
        var deps = dynamicDerived.Reference.GetRequiredProperties().ToArray();

        // Assert
        Assert.DoesNotContain(dynamicDerived.Reference, deps);
        Assert.Equal(2, deps.Length);
    }

    [Fact]
    public void WhenDynamicDerivedWithSetterShortCircuitsAtAttach_ThenSetterTriggeredRecalcDiscoversNewDependency()
    {
        // Pins the DerivedPropertyData.IsDerived guard in WriteProperty. Before that guard was
        // introduced, the check used HasRequiredProperties — which was incorrectly true for dynamic
        // derived properties only because self-reference pollution kept the deps list non-empty.
        // Filtering self-refs made the short-circuit-at-attach case record zero deps, which would
        // skip the setter-triggered recalc with the old guard and leave the getter permanently
        // stale. Regressing the guard to HasRequiredProperties must fail this test.

        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithDerivedPropertyChangeDetection();

        var person = new Person(context) { FirstName = "A" };
        var registered = person.TryGetRegisteredSubject()!;
        var firstNameProperty = registered.TryGetProperty(nameof(Person.FirstName))!;
        var firstNameReference = new PropertyReference(person, nameof(Person.FirstName));

        var shortCircuitFlag = true;

        // Getter short-circuits on shortCircuitFlag=true so FirstName is not read at attach →
        // recorded deps are empty → IsDerived is the only correct guard for the setter recalc.
        var derived = registered.AddDerivedProperty<bool>(
            "ComputedFlag",
            _ => shortCircuitFlag || !string.IsNullOrEmpty(firstNameProperty.GetValue() as string),
            (_, v) => shortCircuitFlag = v);

        var derivedReference = derived.Reference;

        // Sanity: attach left RequiredProperties empty (no FirstName dep recorded yet).
        Assert.Empty(derivedReference.GetRequiredProperties().ToArray());

        // Act — invoking the setter flips shortCircuitFlag=false, forcing a recalc that now reads
        // FirstName. The IsDerived guard must allow this recalc despite empty prior deps.
        derived.SetValue(false);
        var deps = derivedReference.GetRequiredProperties().ToArray();

        // Assert — FirstName is now recorded as a dep; no self-ref present.
        Assert.DoesNotContain(derivedReference, deps);
        Assert.Contains(firstNameReference, deps);
    }
}
