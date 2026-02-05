using Namotion.Interceptor.Generator.Tests.Models;

namespace Namotion.Interceptor.Generator.Tests;

public class NestedClassTests
{
    [Fact]
    public void SingleNestedClass_CanBeCreatedWithContext()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();

        // Act
        var subject = new OuterClass.SingleNestedSubject(context);
        subject.Name = "Test";

        // Assert
        Assert.Equal("Test", subject.Name);
    }

    [Fact]
    public void SingleNestedClass_ImplementsIInterceptorSubject()
    {
        // Arrange
        var subject = new OuterClass.SingleNestedSubject();

        // Act & Assert
        Assert.IsAssignableFrom<IInterceptorSubject>(subject);
    }

    [Fact]
    public void DeepNestedClass_CanBeCreatedWithContext()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();

        // Act
        var subject = new OuterClass.MiddleClass.DeepNestedSubject(context);
        subject.Value = 42;

        // Assert
        Assert.Equal(42, subject.Value);
    }

    [Fact]
    public void DeepNestedClass_ImplementsIInterceptorSubject()
    {
        // Arrange
        var subject = new OuterClass.MiddleClass.DeepNestedSubject();

        // Act & Assert
        Assert.IsAssignableFrom<IInterceptorSubject>(subject);
    }

    [Fact]
    public void NestedInsideSubject_CanBeCreatedWithContext()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();

        // Act
        var subject = new OuterSubject.NestedInsideSubject(context);
        subject.Description = "Nested";

        // Assert
        Assert.Equal("Nested", subject.Description);
    }

    [Fact]
    public void OuterSubject_CanBeCreatedWithContext()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();

        // Act
        var subject = new OuterSubject(context);
        subject.OuterName = "Outer";

        // Assert
        Assert.Equal("Outer", subject.OuterName);
    }

    [Fact]
    public void NestedClass_PropertiesAreTracked()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var subject = new OuterClass.SingleNestedSubject(context);

        // Act
        var properties = ((IInterceptorSubject)subject).Properties;

        // Assert
        Assert.Contains("Name", properties.Keys);
    }
}
