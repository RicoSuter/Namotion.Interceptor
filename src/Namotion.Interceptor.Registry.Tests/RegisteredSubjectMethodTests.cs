using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Tests.Models;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Registry.Tests;

public class RegisteredSubjectMethodTests
{
    [Fact]
    public void WhenSubjectHasMethods_ThenMethodsAreDiscovered()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        var calculator = new Calculator(context);

        // Act
        var registered = calculator.TryGetRegisteredSubject()!;

        // Assert
        Assert.Equal(2, registered.Methods.Length);
        Assert.NotNull(registered.TryGetMethod("Add"));
        Assert.NotNull(registered.TryGetMethod("Reset"));
    }

    [Fact]
    public void WhenMethodInvoked_ThenReturnsCorrectResult()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        var calculator = new Calculator(context);
        var method = calculator.TryGetRegisteredSubject()!.TryGetMethod("Add")!;

        // Act
        var result = method.Invoke([3, 4]);

        // Assert
        Assert.Equal(7, result);
    }

    [Fact]
    public void WhenMethodHasNoReturnValue_ThenInvokeReturnsNull()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        var calculator = new Calculator(context);
        calculator.Value = 42;
        var method = calculator.TryGetRegisteredSubject()!.TryGetMethod("Reset")!;

        // Act
        var result = method.Invoke([]);

        // Assert
        Assert.Null(result);
        Assert.Equal(0, calculator.Value);
    }

    [Fact]
    public void WhenSubjectHasNoMethods_ThenMethodsIsEmpty()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        var person = new Person(context);

        // Act
        var registered = person.TryGetRegisteredSubject()!;

        // Assert
        Assert.Empty(registered.Methods);
    }

    [Fact]
    public void WhenMembersAccessed_ThenContainsBothPropertiesAndMethods()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        var calculator = new Calculator(context);

        // Act
        var registered = calculator.TryGetRegisteredSubject()!;

        // Assert
        Assert.True(registered.Members.Count > 2); // at least Value property + 2 methods
        Assert.NotNull(registered.TryGetMember("Value"));
        Assert.NotNull(registered.TryGetMember("Add"));
        Assert.NotNull(registered.TryGetMember("Reset"));
    }

    [Fact]
    public void WhenDynamicMethodAdded_ThenAppearsInMethods()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        var calculator = new Calculator(context);
        var registered = calculator.TryGetRegisteredSubject()!;

        // Act
        registered.AddMethod("Multiply", typeof(int),
            [new SubjectMethodParameterMetadata("a", typeof(int), []),
             new SubjectMethodParameterMetadata("b", typeof(int), [])],
            (s, p) => (int)p[0]! * (int)p[1]!);

        // Assert
        Assert.Equal(3, registered.Methods.Length);
        var method = registered.TryGetMethod("Multiply")!;
        Assert.Equal(6, method.Invoke([2, 3]));
        Assert.True(method.IsDynamic);
    }

    [Fact]
    public void WhenMethodDiscovered_ThenParameterMetadataIsCorrect()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        var calculator = new Calculator(context);

        // Act
        var method = calculator.TryGetRegisteredSubject()!.TryGetMethod("Add")!;

        // Assert
        Assert.Equal(typeof(int), method.ReturnType);
        Assert.Equal(2, method.Parameters.Count);
        Assert.Equal("a", method.Parameters[0].Name);
        Assert.Equal(typeof(int), method.Parameters[0].Type);
        Assert.Equal("b", method.Parameters[1].Name);
        Assert.Equal(typeof(int), method.Parameters[1].Type);
        Assert.False(method.IsIntercepted);
        Assert.False(method.IsDynamic);
    }

    [Fact]
    public void WhenMethodDiscovered_ThenPropertiesDoNotContainMethods()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        var calculator = new Calculator(context);

        // Act
        var registered = calculator.TryGetRegisteredSubject()!;

        // Assert
        Assert.Single(registered.Properties); // Only "Value"
        Assert.Equal("Value", registered.Properties[0].Name);
    }

    [Fact]
    public void WhenTryGetMethodCalledForProperty_ThenReturnsNull()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        var calculator = new Calculator(context);

        // Act
        var result = calculator.TryGetRegisteredSubject()!.TryGetMethod("Value");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void WhenTryGetPropertyCalledForMethod_ThenReturnsNull()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        var calculator = new Calculator(context);

        // Act
        var result = calculator.TryGetRegisteredSubject()!.TryGetProperty("Add");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void WhenMethodHasAttributes_ThenReflectionAttributesAreAvailable()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        var calculator = new Calculator(context);

        // Act
        var method = calculator.TryGetRegisteredSubject()!.TryGetMethod("Add")!;

        // Assert
        Assert.Contains(method.ReflectionAttributes, a => a is Namotion.Interceptor.Attributes.SubjectMethodAttribute);
    }

    [Fact]
    public void WhenAddingMethodConcurrently_ThenAllMethodsAreRegistered()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        var calculator = new Calculator(context);
        var registered = calculator.TryGetRegisteredSubject()!;
        var methodCount = 100;

        // Act
        Parallel.For(0, methodCount, i =>
        {
            registered.AddMethod($"DynMethod{i}", typeof(int),
                [new SubjectMethodParameterMetadata("x", typeof(int), [])],
                (s, p) => (int)p[0]! + i);
        });

        // Assert
        Assert.Equal(2 + methodCount, registered.Methods.Length);
        for (var i = 0; i < methodCount; i++)
        {
            Assert.NotNull(registered.TryGetMethod($"DynMethod{i}"));
        }
    }
}
