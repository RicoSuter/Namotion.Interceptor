using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Generator.Tests;

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
