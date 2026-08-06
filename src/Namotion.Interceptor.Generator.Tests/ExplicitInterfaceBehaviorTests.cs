using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Generator.Tests;

#region Case A: explicit implementation in a sub-interface

public enum CaseAGender { Male, Female }

public interface ICaseAHuman
{
    CaseAGender Gender { get; }
}

public interface ICaseAMale : ICaseAHuman
{
    CaseAGender ICaseAHuman.Gender => CaseAGender.Male;
}

[InterceptorSubject]
public partial class CaseAJohn : ICaseAMale
{
}

#endregion

#region Case I: a base class implementation beats an interface default

public interface ICaseIHuman
{
    string Origin => "interface-default";
}

public class CaseIBase : ICaseIHuman
{
    string ICaseIHuman.Origin => "base-class-explicit";
}

[InterceptorSubject]
public partial class CaseIDerived : CaseIBase
{
}

#endregion

#region Case AA: two explicit implementations of one generic interface, at different instantiations

// Deduplication (Task 4) keeps this from emitting duplicate dictionary keys once the name
// resolution below makes both entries resolve to "Kind". NI0008 reports the collision from
// Task 11; the suppression is placed now so that task does not break this file's build.
#pragma warning disable NI0008

public interface ICaseAAFoo<T>
{
    string Kind { get; }
}

[InterceptorSubject]
public partial class CaseAASubject : ICaseAAFoo<int>, ICaseAAFoo<string>
{
    string ICaseAAFoo<int>.Kind => "int";
    string ICaseAAFoo<string>.Kind => "string";
}

#pragma warning restore NI0008

#endregion

#region Case Z: a class that declares a property and explicitly implements the same member

public interface ICaseZKind
{
    string Kind { get; }
}

[InterceptorSubject]
public partial class CaseZSubject : ICaseZKind
{
    public partial string Kind { get; set; }

    string ICaseZKind.Kind => "explicit";
}

#endregion

#region Case AD: base implements, derived re-declares. Intentional, so NI0005 is suppressed.

#pragma warning disable NI0005

public interface ICaseADHuman
{
    string Origin => "interface-default";
}

public class CaseADBase : ICaseADHuman
{
}

[InterceptorSubject]
public partial class CaseADDerived : CaseADBase
{
    public partial string Origin { get; set; }
}

#pragma warning restore NI0005

#endregion

#region Inheritance regression: an override partial property must not duplicate the base key

[InterceptorSubject]
public partial class OverrideBase
{
    public virtual partial string Name { get; set; }
}

[InterceptorSubject]
public partial class OverrideDerived : OverrideBase
{
    public override partial string Name { get; set; }
}

#endregion

public class ExplicitInterfaceBehaviorTests
{
    [Fact]
    public void WhenSubInterfaceExplicitlyImplementsMember_ThenSubjectExposesItByMemberName()
    {
        // Arrange
        var john = new CaseAJohn();

        // Act
        var properties = ((IInterceptorSubject)john).Properties;

        // Assert
        Assert.True(properties.ContainsKey("Gender"));
        Assert.Equal(CaseAGender.Male, properties["Gender"].GetValue?.Invoke(john));
    }

    [Fact]
    public void WhenBaseClassImplementsAndInterfaceHasDefault_ThenBaseClassImplementationWins()
    {
        // Arrange
        var derived = new CaseIDerived();

        // Act
        var value = ((IInterceptorSubject)derived).Properties["Origin"].GetValue?.Invoke(derived);

        // Assert
        Assert.Equal("base-class-explicit", value);
    }

    [Fact]
    public void WhenTwoExplicitImplementationsCollideOnName_ThenOneEntryIsExposed()
    {
        // Arrange (case AA)
        var subject = new CaseAASubject();

        // Act
        var properties = ((IInterceptorSubject)subject).Properties;

        // Assert: first declaration wins, and reading DefaultProperties does not throw
        Assert.Single(properties, p => p.Key == "Kind");
        Assert.Equal("int", properties["Kind"].GetValue?.Invoke(subject));
    }

    [Fact]
    public void WhenClassDeclaresAndExplicitlyImplementsSameProperty_ThenSinglePropertyIsExposed()
    {
        // Arrange
        var subject = new CaseZSubject { Kind = "tracked" };

        // Act
        var properties = ((IInterceptorSubject)subject).Properties;

        // Assert
        Assert.Single(properties, p => p.Key == "Kind");
        Assert.Equal("tracked", properties["Kind"].GetValue?.Invoke(subject));
    }

    [Fact]
    public void WhenDerivedRedeclaresBaseImplementedProperty_ThenSubjectAndInterfaceDiffer()
    {
        // Arrange (case AD): the divergence NI0005 warns about, pinned as behaviour
        var derived = new CaseADDerived { Origin = "derived" };

        // Act
        var throughInterface = ((ICaseADHuman)derived).Origin;

        // Assert
        Assert.Equal("derived", derived.Origin);
        Assert.Equal("interface-default", throughInterface);
    }

    [Fact]
    public void WhenDerivedOverridesPartialProperty_ThenSingleKeyIsExposed()
    {
        // Arrange
        var subject = new OverrideDerived { Name = "value" };

        // Act
        var properties = ((IInterceptorSubject)subject).Properties;

        // Assert
        Assert.Single(properties, p => p.Key == "Name");
        Assert.Equal("value", properties["Name"].GetValue?.Invoke(subject));
    }
}
