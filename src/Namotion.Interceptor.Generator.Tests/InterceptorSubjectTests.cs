using Namotion.Interceptor.Generator.Tests.Models;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Attributes;
using System.Reflection;

namespace Namotion.Interceptor.Generator.Tests;

[InterceptorSubject]
public partial class DetachedGeneratedAccessSubject
{
    public partial DetachedGeneratedAccessSubject? Child { get; set; }

    public partial int Count { get; set; }
}

public class InterceptorSubjectTests
{
    private static readonly FieldInfo ExecutorField = typeof(DetachedGeneratedAccessSubject)
        .GetField("_executor", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("The generated executor field is missing.");

    private static readonly FieldInfo RevisionField = typeof(InterceptorExecutor)
        .GetField("Revision", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("The executor revision field is missing.");

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

    [Fact]
    public void WhenDetachedStructuralSetterRunsFirst_ThenItInitializesTheExecutorAndConsumesATerminalRevision()
    {
        // Arrange
        var subject = new DetachedGeneratedAccessSubject();
        var child = new DetachedGeneratedAccessSubject();
        Assert.Null(ExecutorField.GetValue(subject));

        // Act
        subject.Child = child;

        // Assert
        var executor = Assert.IsType<InterceptorExecutor>(ExecutorField.GetValue(subject));
        Assert.Same(child, subject.Child);
        Assert.Equal(1L, Assert.IsType<long>(RevisionField.GetValue(executor)));
        Assert.True(new PropertyReference(subject, nameof(DetachedGeneratedAccessSubject.Child))
            .TryGetWriteState(true, out var revision, out _));
        Assert.Equal(1, revision);
    }

    [Fact]
    public void WhenDetachedStructuralGetterRunsFirst_ThenItInitializesTheExecutor()
    {
        // Arrange
        var subject = new DetachedGeneratedAccessSubject();
        Assert.Null(ExecutorField.GetValue(subject));

        // Act
        var child = subject.Child;

        // Assert
        Assert.Null(child);
        Assert.IsType<InterceptorExecutor>(ExecutorField.GetValue(subject));
    }

    [Fact]
    public void WhenDetachedScalarSetterRunsFirst_ThenItPreservesTheDirectFastPath()
    {
        // Arrange
        var subject = new DetachedGeneratedAccessSubject();
        Assert.Null(ExecutorField.GetValue(subject));

        // Act
        subject.Count = 42;

        // Assert
        Assert.Equal(42, subject.Count);
        Assert.Null(ExecutorField.GetValue(subject));
    }
}
