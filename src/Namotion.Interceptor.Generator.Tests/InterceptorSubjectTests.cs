using Moq;
using Namotion.Interceptor.Generator.Tests.Models;

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
        public List<MethodInvocationInterception> Contexts { get; } = new();
        
        public void AttachTo(IInterceptorSubject subject)
        {
        }

        public void DetachFrom(IInterceptorSubject subject)
        {
        }

        public object? InvokeMethod(MethodInvocationInterception context, Func<MethodInvocationInterception, object?> next)
        {
            Contexts.Add(context);
            return next(context);
        }
    }
    
    [Fact]
    public void WhenCallingMethod_ThenResultIsCorrect()
    {
        // Arrange
        var interceptor = new TestMethodInterceptor();
        var context = InterceptorSubjectContext
            .Create()
            .WithInterceptor(() => interceptor);
        
        var calculator = new Calculator(context);
        
        // Act
        var result = calculator.Sum(1, 2);
        var result2 = calculator.Sum(1, 2);
        
        // Assert
        Assert.Equal(3, result);
        Assert.Equal(2, interceptor.Contexts.Count);
    }
}