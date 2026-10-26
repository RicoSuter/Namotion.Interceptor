using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Tests;

public class ExcludeFromAttributeTests
{
    [Theory]
    [InlineData(typeof(UserInterfaceExcludedType), ExclusionTarget.UserInterface, true)]
    [InlineData(typeof(UserInterfaceExcludedType), ExclusionTarget.Agents, false)]
    [InlineData(typeof(AllExcludedType), (ExclusionTarget)4, true)]
    [InlineData(typeof(DerivedFromExcludedType), ExclusionTarget.UserInterface, true)]
    [InlineData(typeof(ExcludeFromAttributeTests), ExclusionTarget.All, false)]
    public void WhenATypeIsChecked_ThenItIsExcludedOnlyFromTheTargetsOfItsAttribute(Type type, ExclusionTarget target, bool expected)
    {
        // Act
        var isExcluded = ExcludeFromAttribute.IsExcluded(type, target);

        // Assert
        Assert.Equal(expected, isExcluded);
    }
}

[ExcludeFrom(ExclusionTarget.UserInterface)]
public class UserInterfaceExcludedType;

[ExcludeFrom(ExclusionTarget.All)]
public class AllExcludedType;

public class DerivedFromExcludedType : UserInterfaceExcludedType;
