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
        var usedBy = dynamicDerived.Reference.GetUsedByProperties().Items.ToArray();

        // Assert
        Assert.DoesNotContain(dynamicDerived.Reference, deps);
        Assert.Contains(new PropertyReference(person, nameof(Person.FirstName)), deps);
        Assert.Contains(new PropertyReference(person, nameof(Person.LastName)), deps);
        Assert.Equal(2, deps.Length);

        // Symmetric: the derived property must not appear in its own used-by list either
        // (a self-ref in RequiredProperties would transitively register itself in UsedBy).
        Assert.DoesNotContain(dynamicDerived.Reference, usedBy);
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

        var firstNameReference = new PropertyReference(person, nameof(Person.FirstName));

        // Act
        var deps = dynamicDerived.Reference.GetRequiredProperties().ToArray();

        // Assert
        Assert.DoesNotContain(dynamicDerived.Reference, deps);
        Assert.Single(deps);
        Assert.Equal(firstNameReference, deps[0]);
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
        // Pins the IsDerived guard in WriteProperty: a short-circuiting getter records zero deps
        // at attach, and the setter-triggered recalc must still run to surface new dependencies.
        // Regressing the guard to HasRequiredProperties fails this test.

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

        // shortCircuitFlag=true means FirstName is not read at attach → recorded deps are empty.
        var derived = registered.AddDerivedProperty<bool>(
            "ComputedFlag",
            _ => shortCircuitFlag || !string.IsNullOrEmpty(firstNameProperty.GetValue() as string),
            (_, v) => shortCircuitFlag = v);

        var derivedReference = derived.Reference;

        // Sanity: attach recorded zero deps.
        Assert.Empty(derivedReference.GetRequiredProperties().ToArray());

        // Act — setter flips the flag; the recalc now reads FirstName.
        derived.SetValue(false);
        var deps = derivedReference.GetRequiredProperties().ToArray();

        // Assert
        Assert.DoesNotContain(derivedReference, deps);
        Assert.Contains(firstNameReference, deps);
    }
}
