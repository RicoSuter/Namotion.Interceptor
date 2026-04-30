using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Generator.Tests;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class FlaggedSubjectMethodAttribute : SubjectMethodAttribute
{
    public string Tag { get; }

    public FlaggedSubjectMethodAttribute(string tag) => Tag = tag;
}

[AttributeUsage(AttributeTargets.Parameter)]
public sealed class InterceptedParameterMarkerAttribute : Attribute
{
    public string Label { get; }

    public InterceptedParameterMarkerAttribute(string label) => Label = label;
}

[InterceptorSubject]
public partial class InterceptedSubjectMethodHost
{
    public int Counter { get; private set; }

    [FlaggedSubjectMethod("counted")]
    private int IncrementWithoutInterceptor([InterceptedParameterMarker("delta")] int delta)
    {
        Counter += delta;
        return Counter;
    }

    private int UnregisteredWithoutInterceptor(int delta) => delta;
}

public class SubjectMethodOnInterceptedMethodTests
{
    private sealed class RecordingMethodInterceptor : IMethodInterceptor
    {
        public List<MethodInvocationContext> Contexts { get; } = [];

        public object? InvokeMethod(MethodInvocationContext context, InvokeMethodInterceptionDelegate next)
        {
            Contexts.Add(context);
            return next(ref context);
        }
    }

    [Fact]
    public void WhenSubjectMethodIsOnWithoutInterceptorMethod_ThenAppearsInMethodsDictionary()
    {
        // Arrange
        var host = new InterceptedSubjectMethodHost();

        // Act
        var methods = ((IInterceptorSubject)host).Methods;

        // Assert
        Assert.True(methods.ContainsKey("Increment"));
    }

    [Fact]
    public void WhenWithoutInterceptorMethodHasNoSubjectMethodAttribute_ThenNotInMethodsDictionary()
    {
        // Arrange
        var host = new InterceptedSubjectMethodHost();

        // Act
        var methods = ((IInterceptorSubject)host).Methods;

        // Assert
        Assert.False(methods.ContainsKey("Unregistered"));
    }

    [Fact]
    public void WhenSubjectMethodIsOnWithoutInterceptorMethod_ThenIsInterceptedIsTrue()
    {
        // Arrange
        var host = new InterceptedSubjectMethodHost();

        // Act
        var metadata = ((IInterceptorSubject)host).Methods["Increment"];

        // Assert
        Assert.True(metadata.IsIntercepted);
    }

    [Fact]
    public void WhenSubjectMethodIsOnWithoutInterceptorMethod_ThenInvokeReturnsBodyResult()
    {
        // Arrange
        var host = new InterceptedSubjectMethodHost();
        var metadata = ((IInterceptorSubject)host).Methods["Increment"];

        // Act
        var result = metadata.Invoke(host, [5]);

        // Assert
        Assert.Equal(5, result);
        Assert.Equal(5, host.Counter);
    }

    [Fact]
    public void WhenInvokingThroughMetadataWithInterceptor_ThenInterceptorChainRuns()
    {
        // Arrange
        var interceptor = new RecordingMethodInterceptor();
        var subjectContext = InterceptorSubjectContext.Create()
            .WithService(() => interceptor);
        var host = new InterceptedSubjectMethodHost(subjectContext);
        var metadata = ((IInterceptorSubject)host).Methods["Increment"];

        // Act
        var result = metadata.Invoke(host, [3]);

        // Assert
        Assert.Equal(3, result);
        Assert.Single(interceptor.Contexts);
        Assert.Equal("Increment", interceptor.Contexts[0].MethodName);
    }

    [Fact]
    public void WhenSubjectMethodIsOnWithoutInterceptorMethod_ThenAttributesAreInMetadata()
    {
        // Arrange
        var host = new InterceptedSubjectMethodHost();
        var metadata = ((IInterceptorSubject)host).Methods["Increment"];

        // Act
        var marker = metadata.Attributes.OfType<FlaggedSubjectMethodAttribute>().FirstOrDefault();

        // Assert
        Assert.NotNull(marker);
        Assert.Equal("counted", marker.Tag);
    }

    [Fact]
    public void WhenSubjectMethodIsOnWithoutInterceptorMethod_ThenParameterAttributesAreInMetadata()
    {
        // Arrange
        var host = new InterceptedSubjectMethodHost();
        var metadata = ((IInterceptorSubject)host).Methods["Increment"];

        // Act
        var parameterMarker = metadata.Parameters[0].Attributes
            .OfType<InterceptedParameterMarkerAttribute>().FirstOrDefault();

        // Assert
        Assert.NotNull(parameterMarker);
        Assert.Equal("delta", parameterMarker.Label);
    }

    [Fact]
    public void WhenSubjectMethodIsOnWithoutInterceptorMethod_ThenAttributeIsPropagatedToWrapper()
    {
        // Arrange
        var wrapperMethod = typeof(InterceptedSubjectMethodHost).GetMethod("Increment");

        // Act
        var marker = wrapperMethod?.GetCustomAttributes(inherit: false)
            .OfType<FlaggedSubjectMethodAttribute>()
            .FirstOrDefault();

        // Assert
        Assert.NotNull(wrapperMethod);
        Assert.NotNull(marker);
        Assert.Equal("counted", marker.Tag);
    }
}
