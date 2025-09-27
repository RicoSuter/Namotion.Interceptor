using Moq;
using Namotion.Interceptor.Generator.Tests.Models;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Generator.Tests;

public class InterceptorSubjectTests
{
    [Fact]
    public void WhenSettingData_ThenDataCanBeRead()
    {
        // Arrange
        IInterceptorSubject person = new PersonWithFileScopedNamespace();
        
        // Act
        person.SetData("MyData", 55);
        var success = person.TryGetData("MyData", out var data);
        
        // Assert
        Assert.True(success);
        Assert.Equal(55, data);
    }

    public class TestMethodInterceptor : IMethodInterceptor
    {
        public List<MethodInvocationContext> Contexts { get; } = [];
        
        public object? InvokeMethod(MethodInvocationContext context, InvokeMethodInterceptionDelegate next)
        {
            Contexts.Add(context);
            return next(ref context);
        }
    }
    
    [Fact]
    public void WhenCallingMethod_ThenResultIsCorrect()
    {
        // Arrange
        var interceptor = new TestMethodInterceptor();
        var context = InterceptorSubjectContext
            .Create()
            .WithService(() => interceptor);
        
        var calculator = new Calculator(context);
        
        // Act
        var result = calculator.Sum(1, 2);
        var result2 = calculator.Sum(1, 2);
        
        // Assert
        Assert.Equal(3, result);
        Assert.Equal(2, interceptor.Contexts.Count);
    }
}