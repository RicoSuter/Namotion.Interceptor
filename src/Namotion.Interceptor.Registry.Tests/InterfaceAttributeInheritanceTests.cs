using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Registry.Tests;

#region Test Types

[AttributeUsage(AttributeTargets.Property)]
public class InitializerAttribute : Attribute, ISubjectPropertyInitializer
{
    public static int InitializeCount { get; set; }
    public static string? LastPropertyName { get; set; }

    public string Value { get; }
    public InitializerAttribute(string value) => Value = value;

    public void InitializeProperty(RegisteredSubjectProperty property)
    {
        InitializeCount++;
        LastPropertyName = property.Name;
    }

    public static void Reset()
    {
        InitializeCount = 0;
        LastPropertyName = null;
    }
}

public interface IWithInitializer
{
    [Initializer("from-interface")]
    double Value { get; }
}

[InterceptorSubject]
public partial class SubjectWithInterfaceInitializer : IWithInitializer
{
    public partial double Value { get; set; }
}

public interface IWithInitializerOnClass
{
    double Value { get; }
}

[InterceptorSubject]
public partial class SubjectWithClassInitializer : IWithInitializerOnClass
{
    [Initializer("from-class")]
    public partial double Value { get; set; }
}

#endregion

public class InterfaceAttributeInheritanceTests
{
    [Fact]
    public void InterfaceAttribute_WithInitializer_IsInvoked()
    {
        // Arrange
        InitializerAttribute.Reset();
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        // Act
        var subject = new SubjectWithInterfaceInitializer(context);

        // Assert
        Assert.Equal(1, InitializerAttribute.InitializeCount);
        Assert.Equal("Value", InitializerAttribute.LastPropertyName);
    }

    [Fact]
    public void ClassAttribute_WithInitializer_IsInvoked()
    {
        // Arrange
        InitializerAttribute.Reset();
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        // Act
        var subject = new SubjectWithClassInitializer(context);

        // Assert
        Assert.Equal(1, InitializerAttribute.InitializeCount);
        Assert.Equal("Value", InitializerAttribute.LastPropertyName);
    }
}
